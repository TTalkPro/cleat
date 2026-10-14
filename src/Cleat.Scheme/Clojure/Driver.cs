using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.ExceptionServices;
using Cleat.Core.Expand;
using Cleat.Core.Compiler;
using Cleat.Core.Runtime;
using clojure.lang;
using CoreHooks = Cleat.Core.Runtime.RuntimeHooks;
using CoreBoot = Cleat.Core.Runtime.BootInterface;

namespace Cleat.Scheme.Clojure;

/// <summary>
/// 编译驱动 <c>(clojure driver)</c> 的 C# 一侧（cleat-052，designs/04 §9）：Scheme 侧决定流程（顶层 do 拆分、展开、简单形式 / 编译、
/// 收集 AOT 单元、登记钩子），这里给出不经 Scheme 互操作值转换的过程（读形式、简单形式的解释、带位置执行）、钩子的委托包装、
/// Clojure 风格的错误报告与接口文件。
/// <para>与 <see cref="ExpandOps"/> 相同，Scheme 侧拿到的都是 <see cref="Procedure"/> 对象（实参与返回值不转换）。</para>
/// </summary>
public static class Driver
{
    // —— 过程外壳 ——

    private sealed class P1 : Procedure
    {
        private readonly string _name;
        private readonly Func<object, object> _f;
        public P1(string name, Func<object, object> f) { _name = name; _f = f; }
        public override string Name => _name;
        public override object Invoke1(object a) => _f(a);
    }

    private sealed class P2 : Procedure
    {
        private readonly string _name;
        private readonly Func<object, object, object> _f;
        public P2(string name, Func<object, object, object> f) { _name = name; _f = f; }
        public override string Name => _name;
        public override object Invoke2(object a, object b) => _f(a, b);
    }

    private static readonly object s_false = false;

    // ———————————————— 一、简单形式的解释（designs/04 §9：Chez 的 compile-interpret-simple）————————————————
    //
    // 镜像里的 eval 经编译器：每个顶层形式生成一个内存程序集（约 2 ~ 3 ms，展开只占 0.075 ms）。不创建函数 / 类型的形式
    // （常量、Var 引用与 def、直接调用、互操作、let 与 if）在这里直接解释。先整体检查（Simple），通过才求值，不会半途退回编译。

    /// <summary>TryEvalSimple 对不是简单形式的 Lcore 返回它（Scheme 侧用 eq? 比较）。</summary>
    public static readonly object NotSimple = new NotSimpleMarker();

    private sealed class NotSimpleMarker
    {
        public override string ToString() => "#<not-simple>";
    }

    private static readonly Symbol s_quote = Symbol.Intern("quote"), s_if = Symbol.Intern("if"), s_begin = Symbol.Intern("begin"),
      s_caseLambda = Symbol.Intern("case-lambda"), s_lambda = Symbol.Intern("lambda"), s_null = Symbol.Intern("null?"), s_nil = Symbol.Intern("nil?"),
      s_varRef = Symbol.Intern("$clj-var-ref"), s_varSet = Symbol.Intern("$clj-var-set!"), s_def = Symbol.Intern("$clj-def"),
      s_invoke = Symbol.Intern("$clj-invoke"), s_kwInvoke = Symbol.Intern("$clj-keyword-invoke"), s_call = Symbol.Intern("$clj-call");

    /// <summary>简单形式的求值次数与退回编译的次数（度量用；与 Clojure 驱动共用 <see cref="DriverOps"/> 的计数，cleat-090）。</summary>
    public static long SimpleCount { get => DriverOps.SimpleCount; set => DriverOps.SimpleCount = value; }
    public static long CompiledCount { get => DriverOps.CompiledCount; set => DriverOps.CompiledCount = value; }

    /// <summary>(try-eval-simple lcore) → 值，或 <see cref="NotSimple"/>。</summary>
    public static readonly Procedure TryEvalSimple = new P1("clojure-try-eval-simple", TryEval);

    /// <summary>环境变量 CLEAT_NO_SIMPLE_EVAL 设置时关闭快速路径（度量与对照测试用；与 Clojure 驱动共用）。</summary>
    public static bool SimpleEvalEnabled { get => DriverOps.SimpleEvalEnabled; set => DriverOps.SimpleEvalEnabled = value; }

    public static object TryEval(object core)
    {
        if (!SimpleEvalEnabled || !Simple(core, null))
        {
            CompiledCount++;
            return NotSimple;
        }
        SimpleCount++;
        return Eval(core, null);
    }

    private sealed class Env
    {
        public readonly Symbol Name;
        public readonly object Value;
        public readonly Env Next;
        public Env(Symbol name, object value, Env next) { Name = name; Value = value; Next = next; }
    }

    private sealed class Scope
    {
        public readonly Symbol Name;
        public readonly Scope Next;
        public Scope(Symbol name, Scope next) { Name = name; Next = next; }
        public static bool Has(Scope s, Symbol x)
        {
            for (; s != null; s = s.Next) if (ReferenceEquals(s.Name, x)) return true;
            return false;
        }
    }

    private static List<object> Items(object list)
    {
        var r = new List<object>();
        object p = list;
        while (PairOf(p, out var car, out var cdr)) { r.Add(car); p = cdr; }
        return p is null or EmptyList ? r : null;   // 非真列表：null（Scheme 表尾 '()，Clojure 列表的 next 为 null）
    }

    /// <summary>Lcore 的序对：模板节点是 clojure.lang 的列表，展开器生成的子结构是 Scheme 的 Cons（字段 car / cdr）</summary>
    private static bool PairOf(object x, out object car, out object cdr)
    {
        switch (x)
        {
            case clojure.lang.ISeq s when s != null: car = s.first(); cdr = s.next(); return true;
            case Cleat.Scheme.Cons c: car = c.car; cdr = c.cdr; return true;
            default: car = cdr = null; return false;
        }
    }

    /// <summary>Lcore 里的符号：两种表示都有，按名字比较（节点头与特殊形式名）</summary>
    private static bool HeadIs(object x, string name) =>
      x != null && x.ToString() == name && (x is clojure.lang.Symbol || x.GetType().Name == "Symbol");

    private static object Quoted(object x) => PairOf(x, out var car, out var cdr) && HeadIs(car, "quote") && PairOf(cdr, out var v, out _) ? v : NotSimple;

    private static bool AllSimple(List<object> xs, int from, Scope sc)
    {
        for (int i = from; i < xs.Count; i++) if (!Simple(xs[i], sc)) return false;
        return true;
    }

    /// <summary>立即调用的单子句 lambda（let）：返回 形参表，否则 null。</summary>
    private static List<Symbol> LetFormals(object head, out object body)
    {
        body = null;
        var h = Items(head);
        if (h == null || h.Count < 2) return null;
        object formals;
        if (ReferenceEquals(h[0], s_caseLambda))
        {
            if (h.Count != 2) return null;
            var clause = Items(h[1]);
            if (clause == null || clause.Count != 2) return null;
            formals = clause[0];
            body = clause[1];
        }
        else if (ReferenceEquals(h[0], s_lambda))
        {
            if (h.Count != 3) return null;
            formals = h[1];
            body = h[2];
        }
        else return null;
        var fs = Items(formals);
        if (fs == null || fs.Any(f => f is not Symbol)) return null;
        return fs.Cast<Symbol>().ToList();
    }

    private static bool Simple(object x, Scope sc)
    {
        if (x is Symbol s) return Scope.Has(sc, s);
        if (x is not Cons) return x is not Procedure;   // 自求值常量
        var xs = Items(x);
        if (xs == null || xs.Count == 0) return false;
        object head = xs[0];
        if (ReferenceEquals(head, s_quote)) return xs.Count == 2;
        if (ReferenceEquals(head, s_if)) return (xs.Count == 3 || xs.Count == 4) && AllSimple(xs, 1, sc);
        if (ReferenceEquals(head, s_begin)) return AllSimple(xs, 1, sc);
        if (ReferenceEquals(head, s_null) || ReferenceEquals(head, s_nil)) return xs.Count == 2 && Simple(xs[1], sc);
        if (ReferenceEquals(head, s_varRef)) return xs.Count == 3 && Quoted(xs[1]) is Var;
        if (ReferenceEquals(head, s_varSet)) return xs.Count == 3 && Quoted(xs[1]) is Var && Simple(xs[2], sc);
        if (ReferenceEquals(head, s_def)) return xs.Count == 6 && Quoted(xs[1]) is Var && Simple(xs[4], sc) && Simple(xs[5], sc);
        if (ReferenceEquals(head, s_invoke)) return xs.Count >= 2 && AllSimple(xs, 1, sc);
        if (ReferenceEquals(head, s_kwInvoke)) return (xs.Count == 3 || xs.Count == 4) && Quoted(xs[1]) is Keyword && AllSimple(xs, 2, sc);
        if (ReferenceEquals(head, s_call))
            return xs.Count >= 3 && OpsMethod(Quoted(xs[1]), Quoted(xs[2]), xs.Count - 3) != null && AllSimple(xs, 3, sc);
        if (head is Cons)
        {
            var formals = LetFormals(head, out object body);
            if (formals == null || formals.Count != xs.Count - 1 || !AllSimple(xs, 1, sc)) return false;
            foreach (var f in formals) sc = new Scope(f, sc);
            return Simple(body, sc);
        }
        return false;
    }

    private static bool IsTrue(object x) => RT.IsTrue(x);   // #f 与 #nil 为假（cleat-058）

    private static object Eval(object x, Env env)
    {
        if (x is Symbol s)
        {
            for (var e = env; e != null; e = e.Next) if (ReferenceEquals(e.Name, s)) return e.Value;
            throw new InvalidOperationException("simple eval: unbound local " + s.Name);
        }
        if (x is not Cons c) return x;
        object head = c.car;
        if (ReferenceEquals(head, s_quote)) return ((Cons)c.cdr).car;
        var xs = Items(x);
        if (ReferenceEquals(head, s_if))
            return IsTrue(Eval(xs[1], env)) ? Eval(xs[2], env) : xs.Count == 4 ? Eval(xs[3], env) : Unspecified.Instance;
        if (ReferenceEquals(head, s_begin))
        {
            object last = Unspecified.Instance;
            for (int i = 1; i < xs.Count; i++) last = Eval(xs[i], env);
            return last;
        }
        if (ReferenceEquals(head, s_null)) return Eval(xs[1], env) is EmptyList;
        if (ReferenceEquals(head, s_nil)) return Eval(xs[1], env) == null;
        if (ReferenceEquals(head, s_varRef))
        {
            var v = (Var)Quoted(xs[1]);
            return IsTrue(Quoted(xs[2])) ? v.get() : v.getRawRoot();
        }
        if (ReferenceEquals(head, s_varSet)) return ((Var)Quoted(xs[1])).set(Eval(xs[2], env));
        if (ReferenceEquals(head, s_def))
        {
            // DefExpr（与 codegen 的 clj-def 相同的顺序）：setDynamic、bindRoot（有初值时）、setMeta；值为 Var
            var v = (Var)Quoted(xs[1]);
            if (IsTrue(Quoted(xs[3]))) v.setDynamic();
            if (IsTrue(Quoted(xs[2]))) v.bindRoot(Eval(xs[4], env));
            v.setMeta((IPersistentMap)Eval(xs[5], env));
            return v;
        }
        if (ReferenceEquals(head, s_invoke))
        {
            var f = (IFn)Eval(xs[1], env);
            var args = new object[xs.Count - 2];
            for (int i = 0; i < args.Length; i++) args[i] = Eval(xs[i + 2], env);
            return args.Length <= 20 ? ClojureOps.InvokeIFn(f, args) : f.applyTo(ArraySeq.create(args));
        }
        if (ReferenceEquals(head, s_kwInvoke))
        {
            var k = (Keyword)Quoted(xs[1]);
            object target = Eval(xs[2], env);
            if (xs.Count == 3) return target is ILookup l ? l.valAt(k) : clojure.lang.RT.get(target, k);
            object nf = Eval(xs[3], env);
            return target is ILookup l2 ? l2.valAt(k, nf) : clojure.lang.RT.get(target, k, nf);
        }
        if (ReferenceEquals(head, s_call))
        {
            object fixedCount = Quoted(xs[2]);
            var m = OpsMethod(Quoted(xs[1]), fixedCount, xs.Count - 3);
            var vals = new object[xs.Count - 3];
            for (int i = 0; i < vals.Length; i++) vals[i] = Eval(xs[i + 3], env);
            object[] args = vals;
            if (fixedCount is not bool)
            {
                int n = Convert.ToInt32(fixedCount);
                args = new object[n + 1];
                Array.Copy(vals, args, n);
                var rest = new object[vals.Length - n];
                Array.Copy(vals, n, rest, 0, rest.Length);
                args[n] = rest;
            }
            try { return m.Invoke(null, args); }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(e.InnerException).Throw();
                throw;
            }
        }
        // let：((case-lambda ((x …) body)) e …)
        var formals = LetFormals(head, out object body);
        var inner = env;
        var values = new object[formals.Count];
        for (int i = 0; i < values.Length; i++) values[i] = Eval(xs[i + 1], env);
        for (int i = 0; i < values.Length; i++) inner = new Env(formals[i], values[i], inner);
        return Eval(body, inner);
    }

    private static readonly ConcurrentDictionary<(string, int), MethodInfo> s_opsMethods = new();

    /// <summary>$clj-call 的目标：ClojureOps 的公有静态方法（固定个数时最后一个参数是 object[]）；不存在时 null。</summary>
    private static MethodInfo OpsMethod(object name, object fixedCount, int argc)
    {
        if (name == NotSimple || name == null || fixedCount == NotSimple) return null;
        int fixedN = fixedCount is bool ? -1 : Convert.ToInt32(fixedCount);
        if (fixedN > argc) return null;
        int paramCount = fixedN < 0 ? argc : fixedN + 1;
        return s_opsMethods.GetOrAdd((name.ToString(), paramCount), key =>
            typeof(ClojureOps).GetMethods(BindingFlags.Public | BindingFlags.Static)
              .FirstOrDefault(m => m.Name == key.Item1 && m.GetParameters().Length == key.Item2
                                   && (fixedN < 0 || m.GetParameters()[key.Item2 - 1].ParameterType == typeof(object[]))));
    }

    // ———————————————— 二、读形式与带位置的执行 ————————————————

    /// <summary>(make-reader TextReader) → LineNumberingTextReader（已是则原样）。</summary>
    public static readonly Procedure MakeReader = new P1("clojure-make-reader", r =>
        r is LineNumberingTextReader ? r : new LineNumberingTextReader((TextReader)r));

    /// <summary>(read-form reader eof) → 下一个形式，或 eof（Diagnostics.ReadForm）。</summary>
    public static readonly Procedure ReadForm = new P2("clojure-read-form", (r, eof) => Diagnostics.ReadForm((LineNumberingTextReader)r, eof));

    /// <summary>
    /// (run-form thunk form) → thunk 的值。异常不包装、原样传出（Clojure 代码的 catch 不受影响）；第一次经过（最内层的形式）时
    /// 在 <see cref="Exception.Data"/> 记下 文件:行（Diagnostics.Stamp），report-error 据此报告位置。
    /// </summary>
    public static readonly Procedure RunForm = new P2("clojure-run-form", (thunk, form) =>
    {
        try { return ((Procedure)thunk).Invoke0(); }
        catch (Exception e) when (Diagnostics.Stamp(e, form)) { throw; }
    });

    /// <summary>(top-level-do-body form) → (do a b …) 的 Scheme 列表 (a b …)，否则 #f。</summary>
    public static readonly Procedure TopLevelDoBody = new P1("clojure-top-level-do-body", form =>
    {
        if (form is not ISeq s || !s_do.Equals(s.first())) return s_false;
        object r = EmptyList.Instance;
        var items = new List<object>();
        for (ISeq p = s.next(); p != null; p = p.next()) items.Add(p.first());
        for (int i = items.Count - 1; i >= 0; i--) r = new Cons(items[i], r);
        return r;
    });

    private static readonly clojure.lang.Symbol s_do = clojure.lang.Symbol.intern("do");

    /// <summary>
    /// (macroexpand-top? form)：Clojure 的 Compiler.eval 先对序列形式 macroexpand（展开出 do 则拆开）。头部是 def… 的形式也展开
    /// （cleat-053：defprotocol / defrecord 展开为顶层 do，接口要在后面的子形式展开之前生成；Compiler.eval 里 def… 的例外只关于
    /// 是否包进 fn，不关于 macroexpand）。
    /// </summary>
    public static readonly Procedure MacroexpandTop = new P1("clojure-macroexpand-top?", form => form is ISeq);

    // ———————————————— 三、钩子 ————————————————

    /// <summary>(install-hook! 名字 过程)：把 Scheme 过程包成 <see cref="CoreHooks"/> 的委托（实参与返回值不转换）。</summary>
    public static void InstallHook(string name, object proc)
    {
        var p = (Procedure)proc;
        switch (name)
        {
            case "eval": CoreHooks.Eval = x => p.Invoke1(x); break;
            case "load-source": CoreHooks.LoadSource = (r, path, src) => p.Invoke3(r, path, src); break;
            case "compile": CoreHooks.Compile = (r, path, src) => p.Invoke3(r, path, src); break;
            case "macroexpand-1": CoreHooks.Macroexpand1 = (x, env) => p.Invoke1(x); break;
            case "compile-boot": CompileBootHook = (forms, dll) => p.Invoke2(forms, dll); break;
            case "compile-package": CompilePackageHook = (pkg, forms, roots, dll) => p.Invoke4(pkg, forms, roots, dll); break;
            default: throw new ArgumentException("unknown hook for the Clojure driver: " + name, nameof(name));
        }
    }

    /// <summary>(clojure-compile-boot 形式列表 dll路径)：编译 Clojure 镜像（cleat-053）；由 (clojure driver) 登记。</summary>
    public static Func<object, object, object> CompileBootHook;

    /// <summary>
    /// 编译 Clojure 镜像：依次求值 forms（Clojure 形式），把加载到的全部顶层形式编成一个程序集 dllPath（旁边 .lcil、.ifc）。
    /// </summary>
    public static void CompileBoot(IEnumerable<object> forms, string dllPath)
    {
        // CLEAT_COMPILER=clojure（cleat-090）：Clojure 写的驱动登记了编译入口时交给它
        if (DriverOps.CompileBootHook is { } clj)
        {
            clj.invoke(LazilyPersistentVector.createOwning(forms.ToArray()), Path.GetFullPath(dllPath));
            return;
        }
        var hook = CompileBootHook ?? throw new InvalidOperationException("the Clojure driver has not registered compile-boot");
        object list = EmptyList.Instance;
        foreach (var f in forms.Reverse()) list = new Cons(f, list);
        hook(list, Path.GetFullPath(dllPath));
    }

    /// <summary>(clojure-compile-package 包名 形式列表 根目录列表或#t dll路径)：编包程序集（cleat-067）；由 (clojure driver) 登记。</summary>
    public static Func<object, object, object, object, object> CompilePackageHook;

    /// <summary>
    /// 编包程序集（designs/17 §2.2）：在 user 命名空间里依次求值 forms（通常是 <c>(require 'ns)</c>），roots（源码根目录）下被加载的文件
    /// 编成一个程序集 dllPath（每个文件一个入口；旁边 .ifc）。roots 为 null 时被加载的全部源码文件都编进去。
    /// </summary>
    public static void CompilePackage(string package, System.Collections.IEnumerable forms, System.Collections.IEnumerable roots, string dllPath)
    {
        if (DriverOps.CompilePackageHook is { } clj)
        {
            object rts = roots == null ? (object)true : LazilyPersistentVector.createOwning(roots.Cast<object>().Select(r => (object)Path.GetFullPath(r.ToString())).ToArray());
            clj.invoke(package, LazilyPersistentVector.createOwning(forms.Cast<object>().ToArray()), rts, Path.GetFullPath(dllPath));
            return;
        }
        var hook = CompilePackageHook ?? throw new InvalidOperationException("the Clojure driver has not registered compile-package");
        object list = EmptyList.Instance;
        foreach (var f in forms.Cast<object>().Reverse()) list = new Cons(f, list);
        object rs = true;
        if (roots != null)
        {
            rs = EmptyList.Instance;
            foreach (var r in roots.Cast<object>().Reverse()) rs = new Cons(Path.GetFullPath(r.ToString()), rs);
        }
        hook(package, list, rs, Path.GetFullPath(dllPath));
    }

    /// <summary>
    /// 登记 Clojure 风格的错误报告（<see cref="Diagnostics.ReportError"/>），并让诊断能取出 Scheme 条件对象的文本
    /// （raise 抛出的 SchemeException）。
    /// </summary>
    public static void InstallReportError()
    {
        Diagnostics.MessageHook = SchemeMessage;
        CoreHooks.ReportError = Diagnostics.ReportError;
    }

    private static string SchemeMessage(Exception e)
    {
        if (e is SchemeException se)
        {
            var parts = new List<string>();
            for (object p = se.Condition is Condition ? Conditions.SimpleConditions(se.Condition) : null; p is Cons c; p = c.cdr)
                if (c.car is Record r && r.Fields.Length > 0)
                    parts.Add(string.Join(" ", r.Fields.Select(f => f is Cons l ? string.Join(" ", l) : f?.ToString() ?? "()")));
            if (parts.Count > 0) return string.Join(": ", parts);
        }
        return null;
    }

    // ———————————————— 四、AOT：输出路径、镜像核对、接口文件 ————————————————

    private static readonly Symbol s_type = Symbol.Intern("type");
    private static readonly Symbol s_cljFn = Symbol.Intern("$clj-fn");

    /// <summary>
    /// 写接口文件（designs/05 §6，cleat-022 的 EDN 格式）：dll 旁的 <c>名字.ifc</c>，镜像 id 为 dll 的 MVID（发射器按内容哈希）。
    /// 条目：本单元顶层 <c>(def v (fn* …))</c> 定义的已知函数——Var 不是 ^:dynamic / ^:redef、只定义一次、fn 类名在单元内唯一
    /// （codegen 对重名加 __N，唯一时类名即 <c>ns$name</c>）、无自由变量（顶层 fn*）；记 Var → 类、各 arity（变长为 -(必需个数+1)）。
    /// 这类 fn 类同时生成了静态 <c>invokeStatic</c>（cleat-033，designs/04 §6）：编译器对参数个数恰为某固定 arity 的调用
    /// 直接 <c>call 类::invokeStatic</c>（跨单元直接链接），:static 据此为真。返回条目数。
    /// </summary>
    public static int WriteInterfaceFile(string dllPath, object cores, object lcil)
    {
        var file = BuildInterfaceFile(Path.GetFileNameWithoutExtension(dllPath), cores, lcil, Mvid(dllPath));
        string ifc = InterfaceFile.PathFor(dllPath);
        if (File.Exists(ifc)) File.Delete(ifc);
        file.Save(ifc);
        return file.Entries.Count;
    }

    /// <summary>
    /// 包程序集的接口文件（cleat-067）：写到临时目录（镜像 id 为空——它嵌在自己的程序集里，不会配错），返回 .ifc 的路径；
    /// 编译驱动把它与旁边的 .inl（有时）作为嵌入资源 cleat.ifc / cleat.inl 发射进程序集，之后 <see cref="DeletePackageInterface"/>。
    /// </summary>
    public static string WritePackageInterface(string dllPath, object cores, object lcil)
    {
        string dir = Path.Combine(Path.GetTempPath(), "cleat-ifc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string ifc = Path.Combine(dir, InterfaceFile.ResourceName);
        BuildInterfaceFile(Path.GetFileNameWithoutExtension(dllPath), cores, lcil, Guid.Empty).Save(ifc);
        return ifc;
    }

    /// <summary>.ifc 旁的 .inl 存在时返回其路径，否则 #f。</summary>
    public static object InlineFileOf(string ifc)
    {
        string inl = InterfaceFile.InlinePathFor(ifc);
        return File.Exists(inl) ? inl : s_false;
    }

    public static void DeletePackageInterface(string ifc)
    {
        try { Directory.Delete(Path.GetDirectoryName(ifc)!, true); }
        catch (IOException) { }
    }

    private static InterfaceFile BuildInterfaceFile(string asm, object cores, object lcil, Guid imageId)
    {
        var types = new HashSet<string>(StringComparer.Ordinal);
        CollectTypes(lcil, types);
        var fnNames = new Dictionary<string, int>(StringComparer.Ordinal);
        var defs = new Dictionary<Var, int>();
        var candidates = new List<(Var v, string cls, int[] arities, object init)>();
        foreach (var core in Items(cores) ?? new List<object>())
            Walk(core, fnNames, defs, candidates);
        var file = new InterfaceFile { AssemblyName = asm, ImageId = imageId };
        foreach (var (v, cls, arities, init) in candidates)
        {
            if (defs[v] != 1 || fnNames[cls] != 1 || !types.Contains(cls)) continue;
            var m = v.meta();
            if (v.isDynamic() || clojure.lang.RT.booleanCast(m?.valAt(Keyword.intern("redef")))) continue;
            file.Entries.Add(new InterfaceFile.Entry
            {
                Var = clojure.lang.Symbol.intern(v.Namespace.Name.Name, v.Symbol.Name),
                ClassName = cls + ", " + asm,
                Arities = arities,
                HasStatic = true,
                Inline = v.IsMacro ? null : InlineClauses(init),
            });
        }
        return file;
    }

    private static Guid Mvid(string dllPath)
    {
        using var pe = new PEReader(File.OpenRead(dllPath));
        var md = pe.GetMetadataReader();
        return md.GetGuid(md.GetModuleDefinition().Mvid);
    }

    private static void CollectTypes(object x, HashSet<string> types)
    {
        for (object p = x; PairOf(p, out var c, out var pd); p = pd)
        {
            if (PairOf(c, out var item, out var pn) && PairOf(pn, out var tn, out _)
                && HeadIs(item, "type") && tn != null && !PairOf(tn, out _, out _))
                types.Add(tn.ToString());
            if (PairOf(c, out var sub, out _)) CollectTypes(sub, types);
        }
    }

    /// <summary>顶层形式里的 $clj-def / $clj-fn：数 fn 类名与 Var 的定义次数，记下 (def v (fn* …)) 的候选。</summary>
    private static void Walk(object x, Dictionary<string, int> fnNames, Dictionary<Var, int> defs, List<(Var, string, int[], object)> candidates)
    {
        if (!PairOf(x, out var head0, out var tail0)) return;
        if (HeadIs(head0, "quote")) return;
        var xs = Items(x);
        if (xs != null && xs.Count >= 1)
        {
            if (HeadIs(xs[0], "$clj-fn") && xs.Count == 5 && Quoted(xs[1]) is object nm && nm != NotSimple)
            {
                string name = nm.ToString();
                fnNames[name] = fnNames.TryGetValue(name, out int k) ? k + 1 : 1;
            }
            if (HeadIs(xs[0], "$clj-def") && xs.Count == 6 && Quoted(xs[1]) is Var v)
            {
                defs[v] = defs.TryGetValue(v, out int k) ? k + 1 : 1;
                if (FnNode(xs[4]) is { } fn && Quoted(fn[1]) is object cls && cls != NotSimple)
                {
                    var arities = FnArities(fn[2], fn[4]);
                    if (arities != null) candidates.Add((v, cls.ToString(), arities, xs[4]));
                }
            }
        }
        for (object p = x; PairOf(p, out var pc, out var pd); p = pd) Walk(pc, fnNames, defs, candidates);
    }

    // ———————————————— 可内联的小函数（cleat-042 第 4 项）————————————————
    // 顶层 (def v (fn* …)) 的固定子句足够小、只含 let / loop / if / 调用这类结构时，接口文件记下子句的 Lcore（fasl），
    // 其它单元的展开器在调用点内联它（带守卫：Var 的根还是这个函数类时走内联体，否则普通调用）。
    // Lcore 的结构只看 Scheme 的序对与符号（与 parse-core 的视角相同）；clojure.lang 的列表与符号是常量

    private const int InlineMaxPairs = 160;

    private static readonly HashSet<string> s_noInline = new(StringComparer.Ordinal)
    {
        "set!", "library-letrec*", "typed-case-lambda", "annotated-typed-case-lambda", "async-lambda",
        "$clj-fn", "$clj-deftype", "$clj-def", "$clj-var-set!", "$try/catch", "$try/finally", "define-clr-class-internal",
        "clr-using-internal", "clr-reference-internal",
    };

    private static Dictionary<int, byte[]> InlineClauses(object init)
    {
        // 只认不带自引用的 fn*（($clj-fn …) 或外包 WithMetaOf）；命名 fn* 的 letrec 里子句引用 self，内联后无处绑定
        var xs = Items(init);
        if (xs == null) return null;
        if (xs.Count == 5 && HeadIs(xs[0], "$clj-call") && Quoted(xs[1]) is string m && m == "WithMetaOf") xs = Items(xs[3]);
        if (xs == null || xs.Count != 5 || !HeadIs(xs[0], "$clj-fn")) return null;
        var lambda = Items(xs[4]);
        if (lambda == null || lambda.Count < 2 || !HeadIs(lambda[0], "case-lambda")) return null;
        Dictionary<int, byte[]> r = null;
        // 变长子句（总在最后）的形参也是真列表（必需参数 + 剩余 seq），不能当固定子句内联
        int last = Quoted(xs[2]) is bool or null ? lambda.Count : lambda.Count - 1;
        for (int i = 1; i < last; i++)
        {
            var clause = lambda[i];
            if (!(clause is Cleat.Scheme.Cons c) || Items(c.car) is not { } formals || !formals.All(f => f is Cleat.Scheme.Symbol)) continue;
            int pairs = 0;
            if (!InlineOk(clause, ref pairs)) continue;
            try { (r ??= new Dictionary<int, byte[]>())[formals.Count] = Cleat.Scheme.Fasl.Write(clause); }
            catch (Exception) { }   // 常量里有 fasl 写不了的对象：不内联
        }
        return r;
    }

    private static bool InlineOk(object x, ref int pairs)
    {
        while (x is Cleat.Scheme.Cons c)
        {
            if (++pairs > InlineMaxPairs) return false;
            if (c.car is Cleat.Scheme.Symbol h)
            {
                string n = h.ToString();
                if (n == "quote") return true;
                if (s_noInline.Contains(n)) return false;
            }
            if (c.car is Cleat.Scheme.Cons && !InlineOk(c.car, ref pairs)) return false;
            x = c.cdr;
        }
        return true;
    }

    private static readonly ConcurrentDictionary<(InterfaceFile.Entry, int), object[]> s_inlineCache = new();

    /// <summary>
    /// (inline-target var argc)：Var 是接口文件里的已知函数、有参数个数为 argc 的可内联子句时返回 #(函数类 子句)，否则 #f。
    /// 子句是 fasl 读回的新副本（每次调用点各自改名，见展开器的 inline-call）。
    /// </summary>
    public static object InlineTarget(object var, object argc)
    {
        if (var is not Var v) return s_false;
        int n;
        try { n = Convert.ToInt32(argc); }
        catch (Exception) { return s_false; }
        var e = InterfaceFile.FindKnown(clojure.lang.Symbol.intern(v.Namespace.Name.Name, v.Symbol.Name));
        var bytes = e == null ? null : InterfaceFile.FindInline(e, n);
        if (bytes == null) return s_false;
        var cached = s_inlineCache.GetOrAdd((e, n), _ =>
        {
            int comma = e.ClassName.IndexOf(',');
            string tn = comma < 0 ? e.ClassName : e.ClassName.Substring(0, comma).Trim();
            string an = comma < 0 ? null : e.ClassName.Substring(comma + 1).Trim();
            var t = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => an == null || a.GetName().Name == an).Select(a => a.GetType(tn, false)).FirstOrDefault(x => x != null);
            return t == null ? null : new object[] { t, bytes };
        });
        if (cached == null) return s_false;
        // 读不了的内联体（旧 fasl 版本、过期产物的 .inl）不内联，照常调用
        // Clojure 编译器写的 .inl 是 L0 子句（[:cleat/l0 …]，cleat-090），Scheme 的展开器只认 Lcore 子句（Scheme 的表）
        try { return Cleat.Scheme.Fasl.Read((byte[])cached[1]) is Cons clause ? new object[] { cached[0], clause } : s_false; }
        catch (System.IO.InvalidDataException) { return s_false; }
    }

    private static readonly Symbol s_letrec = Symbol.Intern("letrec");

    /// <summary>
    /// def 的初值是 fn*：<c>($clj-fn …)</c>；带元数据（defn 的 :arglists）时外面套一层
    /// <c>($clj-call '("WithMetaOf" #f) fn '元数据)</c>；命名 fn* 是 <c>(letrec ((f ($clj-fn …))) f)</c>
    /// （自引用在类里是 this，没有自由变量）。
    /// </summary>
    private static List<object> FnNode(object init)
    {
        var xs = Items(init);
        if (xs == null) return null;
        if (xs.Count == 5 && HeadIs(xs[0], "$clj-fn")) return xs;
        if (xs.Count == 5 && HeadIs(xs[0], "$clj-call") && Quoted(xs[1]) is string m && m == "WithMetaOf")
            return FnNode(xs[3]);
        if (xs.Count == 3 && HeadIs(xs[0], "letrec") && Items(xs[1]) is { Count: 1 } bs && Items(bs[0]) is { Count: 2 } b
            && b[0]?.ToString() is string name && xs[2]?.ToString() == name)
            return FnNode(b[1]);
        return null;
    }

    /// <summary>($clj-fn '类名 '变长必需个数|#f '原始签名 (case-lambda (形参 体) …)) 的 arity：固定子句为形参个数，变长子句为 -(必需个数 + 1)。</summary>
    private static int[] FnArities(object variadic, object lambda)
    {
        var l = Items(lambda);
        if (l == null || l.Count < 2 || !HeadIs(l[0], "case-lambda")) return null;
        object req = Quoted(variadic);
        var r = new List<int>();
        for (int i = 1; i < l.Count; i++)
        {
            var clause = Items(l[i]);
            if (clause == null) return null;
            var formals = Items(clause[0]);
            if (formals == null) return null;
            r.Add(formals.Count);
        }
        if (req is not bool && req != NotSimple)
        {
            // 变长子句（与必需个数 + 1 个形参同形）：记为 -(必需 + 1)
            int n = Convert.ToInt32(req);
            int idx = r.LastIndexOf(n + 1);
            if (idx >= 0) r[idx] = -(n + 1);
        }
        return r.ToArray();
    }
}
