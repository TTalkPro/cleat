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
using Cleat.Core.Runtime;
using clojure.lang;
using CoreHooks = Cleat.Core.Runtime.RuntimeHooks;
using CoreBoot = Cleat.Core.Runtime.BootInterface;

namespace Cleat.Scheme.Clojure;

/// <summary>
/// 编译驱动 <c>(clojure driver)</c> 的 C# 一侧（cleat-052，designs/04 §9）：Scheme 侧决定流程（顶层 do 拆分、展开、简单形式 / 编译、
/// 收集 AOT 单元、登记钩子），这里给出不经 Scheme 互操作值转换的过程（读形式、简单形式的解释、带位置执行）、钩子的委托包装、
/// Clojure 风格的错误报告与接口文件。
/// <para>与 <see cref="ClojureOps"/> 相同，Scheme 侧拿到的都是 <see cref="Procedure"/> 对象（实参与返回值不转换）。</para>
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
      s_caseLambda = Symbol.Intern("case-lambda"), s_lambda = Symbol.Intern("lambda"), s_null = Symbol.Intern("null?"),
      s_varRef = Symbol.Intern("$clj-var-ref"), s_varSet = Symbol.Intern("$clj-var-set!"), s_def = Symbol.Intern("$clj-def"),
      s_invoke = Symbol.Intern("$clj-invoke"), s_kwInvoke = Symbol.Intern("$clj-keyword-invoke"), s_call = Symbol.Intern("$clj-call");

    /// <summary>简单形式的求值次数与退回编译的次数（度量用）。</summary>
    public static long SimpleCount, CompiledCount;

    /// <summary>(try-eval-simple lcore) → 值，或 <see cref="NotSimple"/>。</summary>
    public static readonly Procedure TryEvalSimple = new P1("clojure-try-eval-simple", TryEval);

    /// <summary>环境变量 CLEAT_NO_SIMPLE_EVAL 设置时关闭快速路径（度量与对照测试用）。</summary>
    public static bool SimpleEvalEnabled = Environment.GetEnvironmentVariable("CLEAT_NO_SIMPLE_EVAL") == null;

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
        for (; p is Cons c; p = c.cdr) r.Add(c.car);
        return p == null ? r : null;   // 非真列表：null
    }

    private static object Quoted(object x) => x is Cons c && ReferenceEquals(c.car, s_quote) && c.cdr is Cons d ? d.car : NotSimple;

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
        if (ReferenceEquals(head, s_null)) return xs.Count == 2 && Simple(xs[1], sc);
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

    private static bool IsTrue(object x) => !(x is bool b) || b;

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
        if (ReferenceEquals(head, s_null)) return Eval(xs[1], env) == null;
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

    private static readonly IPersistentMap s_readOpts = clojure.lang.RT.map(Keyword.intern("read-cond"), Keyword.intern("allow"));

    /// <summary>(make-reader TextReader) → LineNumberingTextReader（已是则原样）。</summary>
    public static readonly Procedure MakeReader = new P1("clojure-make-reader", r =>
        r is LineNumberingTextReader ? r : new LineNumberingTextReader((TextReader)r));

    [ThreadStatic] private static int t_lastLine, t_lastColumn;

    /// <summary>(read-form reader eof) → 下一个形式，或 eof。记下形式开始的行列（形式本身没有 :line 元数据时用）。</summary>
    public static readonly Procedure ReadForm = new P2("clojure-read-form", (r, eof) =>
    {
        var rdr = (LineNumberingTextReader)r;
        SkipWhitespace(rdr);
        t_lastLine = rdr.LineNumber;
        t_lastColumn = rdr.ColumnNumber;
        return LispReader.read(rdr, false, eof, false, s_readOpts);
    });

    private static void SkipWhitespace(LineNumberingTextReader r)
    {
        while (true)
        {
            int ch = r.Read();
            if (ch == -1) return;
            if (!char.IsWhiteSpace((char)ch) && ch != ',')
            {
                r.Unread(ch);
                return;
            }
        }
    }

    public const string LocationKey = "cleat.error/location";

    /// <summary>
    /// (run-form thunk form) → thunk 的值。异常不包装、原样传出（Clojure 代码的 catch 不受影响）；第一次经过（最内层的形式）时
    /// 在 <see cref="Exception.Data"/> 记下 文件:行:列，report-error 据此报告位置。
    /// </summary>
    public static readonly Procedure RunForm = new P2("clojure-run-form", (thunk, form) =>
    {
        try { return ((Procedure)thunk).Invoke0(); }
        catch (Exception e) when (Stamp(e, form)) { throw; }
    });

    private static bool Stamp(Exception e, object form)
    {
        try
        {
            if (e is ExpanderException || e.Data.Contains(LocationKey)) return false;
            var m = (form as IMeta)?.meta();
            object line = m?.valAt(Keyword.intern("line")), column = m?.valAt(Keyword.intern("column"));
            e.Data[LocationKey] = $"{SourceLabel()}:{line ?? t_lastLine}:{column ?? t_lastColumn}";
        }
        catch (Exception)
        {
            // 位置只是诊断信息
        }
        return false;
    }

    /// <summary>当前源的名字：*file*（全路径，与语法错误相同）；REPL / -e 为 REPL。</summary>
    private static string SourceLabel()
    {
        string file = clojure.lang.RT.SourcePathVar.deref() as string;
        return string.IsNullOrEmpty(file) || file == "NO_SOURCE_PATH" ? "REPL" : file;
    }

    /// <summary>(top-level-do-body form) → (do a b …) 的 Scheme 列表 (a b …)，否则 #f。</summary>
    public static readonly Procedure TopLevelDoBody = new P1("clojure-top-level-do-body", form =>
    {
        if (form is not ISeq s || !s_do.Equals(s.first())) return s_false;
        object r = null;
        var items = new List<object>();
        for (ISeq p = s.next(); p != null; p = p.next()) items.Add(p.first());
        for (int i = items.Count - 1; i >= 0; i--) r = new Cons(items[i], r);
        return r;
    });

    private static readonly clojure.lang.Symbol s_do = clojure.lang.Symbol.intern("do");

    /// <summary>(macroexpand-top? form)：Clojure 的 Compiler.eval 先对集合形式 macroexpand，头部是 def… 符号的除外。</summary>
    public static readonly Procedure MacroexpandTop = new P1("clojure-macroexpand-top?", form =>
        form is ISeq s && !(s.first() is clojure.lang.Symbol h && h.Name.StartsWith("def", StringComparison.Ordinal)));

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
            default: throw new ArgumentException("unknown hook for the Clojure driver: " + name, nameof(name));
        }
    }

    /// <summary>登记 Clojure 风格的错误报告（写到 *err*）。</summary>
    public static void InstallReportError() => CoreHooks.ReportError = ReportError;

    public static void ReportError(Exception e)
    {
        var w = clojure.lang.RT.errPrintWriter();
        w.WriteLine(FormatError(e));
        if (Environment.GetEnvironmentVariable("CLEAT_TRACE_ERRORS") != null) w.WriteLine(e);
        w.Flush();
    }

    /// <summary>
    /// Clojure（clojure.main/ex-str）风格的错误文本：
    ///   展开错误：ExpanderException 的消息 <c>Syntax error (文件:行:列) 原因</c>；
    ///   读错误：<c>Syntax error reading source at (文件:行:列).</c> + 原因；
    ///   其余：<c>Execution error (类型) at (文件:行:列).</c> + 原因（类型为 Exception 时省略，与 Clojure 相同）。
    /// </summary>
    public static string FormatError(Exception e)
    {
        while (e is TargetInvocationException && e.InnerException != null) e = e.InnerException;
        switch (e)
        {
            case ExpanderException:
                return e.Message;
            case LispReader.ReaderException re:
                return $"Syntax error reading source at ({SourceLabel()}:{re.Line}:{re.Column}).{Environment.NewLine}{(re.InnerException ?? re).Message}";
        }
        string loc = e.Data.Contains(LocationKey) ? (string)e.Data[LocationKey] : SourceLabel() + ":1";
        string type = e.GetType() == typeof(Exception) ? "" : " (" + e.GetType().Name + ")";
        return $"Execution error{type} at ({loc}).{Environment.NewLine}{Message(e)}";
    }

    private static string Message(Exception e)
    {
        if (e is SchemeException se)
        {
            var parts = new List<string>();
            for (object p = se.Condition is Condition ? Conditions.SimpleConditions(se.Condition) : null; p is Cons c; p = c.cdr)
                if (c.car is Record r && r.Fields.Length > 0)
                    parts.Add(string.Join(" ", r.Fields.Select(f => f is Cons l ? string.Join(" ", l) : f?.ToString() ?? "()")));
            if (parts.Count > 0) return string.Join(": ", parts);
        }
        return e.Message;
    }

    // ———————————————— 四、AOT：输出路径、镜像核对、接口文件 ————————————————

    /// <summary>
    /// (compile-output-path 源路径 源名字) → .clj.dll 的路径：ClojureCLR 的命名（a/b.clj → a.b.clj.dll）。目录：*compile-path* 是字符串时用它，
    /// 否则用找到源码的那个加载路径目录（源路径去掉源名字），这样加载器下一次在同一目录里找到 dll。
    /// </summary>
    public static string CompileOutputPath(object sourcePath, object sourceName)
    {
        string path = sourcePath?.ToString() ?? "", name = (sourceName?.ToString() ?? Path.GetFileName(path)).Replace('\\', '/');
        string file = name.Replace('/', '.') + ".dll";
        if (clojure.lang.RT.CompilePathVar.deref() is string dir && dir.Length > 0) return Path.GetFullPath(Path.Combine(dir, file));
        string full = Path.GetFullPath(path).Replace('\\', '/');
        string root = full.EndsWith("/" + name, StringComparison.Ordinal) ? full.Substring(0, full.Length - name.Length) : Path.GetDirectoryName(full);
        return Path.GetFullPath(Path.Combine(root, file));
    }

    /// <summary>编译时正在运行的 Clojure boot 镜像（cleat.boot.dll）的 id；没有时 #f。产物入口据此生成 BootInterface.Check。</summary>
    public static object ClojureImageId() => CoreBoot.CurrentImageId?.ToString() ?? s_false;

    private static readonly Symbol s_type = Symbol.Intern("type");
    private static readonly Symbol s_cljFn = Symbol.Intern("$clj-fn");

    /// <summary>
    /// 写接口文件（designs/05 §6，cleat-022 的 EDN 格式）：dll 旁的 <c>名字.ifc</c>，镜像 id 为 dll 的 MVID（发射器按内容哈希）。
    /// 条目：本单元顶层 <c>(def v (fn* …))</c> 定义的已知函数——Var 不是 ^:dynamic / ^:redef、只定义一次、fn 类名在单元内唯一
    /// （codegen 对重名加 __N，唯一时类名即 <c>ns$name</c>）、无自由变量（顶层 fn*）；记 Var → 类、各 arity（变长为 -(必需个数+1)）。
    /// 直接链接（invokeStatic）尚未生成，:static 为假。返回条目数。
    /// </summary>
    public static int WriteInterfaceFile(string dllPath, object cores, object lcil)
    {
        string asm = Path.GetFileNameWithoutExtension(dllPath);
        var types = new HashSet<string>(StringComparer.Ordinal);
        CollectTypes(lcil, types);
        var fnNames = new Dictionary<string, int>(StringComparer.Ordinal);
        var defs = new Dictionary<Var, int>();
        var candidates = new List<(Var v, string cls, int[] arities)>();
        foreach (var core in Items(cores) ?? new List<object>())
            Walk(core, fnNames, defs, candidates);
        var file = new InterfaceFile { AssemblyName = asm, ImageId = Mvid(dllPath) };
        foreach (var (v, cls, arities) in candidates)
        {
            if (defs[v] != 1 || fnNames[cls] != 1 || !types.Contains(cls)) continue;
            var m = v.meta();
            if (v.isDynamic() || clojure.lang.RT.booleanCast(m?.valAt(Keyword.intern("redef")))) continue;
            file.Entries.Add(new InterfaceFile.Entry
            {
                Var = clojure.lang.Symbol.intern(v.Namespace.Name.Name, v.Symbol.Name),
                ClassName = cls + ", " + asm,
                Arities = arities,
                HasStatic = false,
            });
        }
        string ifc = InterfaceFile.PathFor(dllPath);
        if (File.Exists(ifc)) File.Delete(ifc);
        file.Save(ifc);
        return file.Entries.Count;
    }

    private static Guid Mvid(string dllPath)
    {
        using var pe = new PEReader(File.OpenRead(dllPath));
        var md = pe.GetMetadataReader();
        return md.GetGuid(md.GetModuleDefinition().Mvid);
    }

    private static void CollectTypes(object x, HashSet<string> types)
    {
        for (object p = x; p is Cons c; p = c.cdr)
        {
            if (c.car is Cons item && ReferenceEquals(item.car, s_type) && item.cdr is Cons n && n.car != null && n.car is not Cons)
                types.Add(n.car.ToString());
            if (c.car is Cons sub) CollectTypes(sub, types);
        }
    }

    /// <summary>顶层形式里的 $clj-def / $clj-fn：数 fn 类名与 Var 的定义次数，记下 (def v (fn* …)) 的候选。</summary>
    private static void Walk(object x, Dictionary<string, int> fnNames, Dictionary<Var, int> defs, List<(Var, string, int[])> candidates)
    {
        if (x is not Cons c) return;
        if (ReferenceEquals(c.car, s_quote)) return;
        var xs = Items(x);
        if (xs != null && xs.Count >= 1)
        {
            if (ReferenceEquals(xs[0], s_cljFn) && xs.Count == 4 && Quoted(xs[1]) is object nm && nm != NotSimple)
            {
                string name = nm.ToString();
                fnNames[name] = fnNames.TryGetValue(name, out int k) ? k + 1 : 1;
            }
            if (ReferenceEquals(xs[0], s_def) && xs.Count == 6 && Quoted(xs[1]) is Var v)
            {
                defs[v] = defs.TryGetValue(v, out int k) ? k + 1 : 1;
                if (FnNode(xs[4]) is { } fn && Quoted(fn[1]) is object cls && cls != NotSimple)
                {
                    var arities = FnArities(fn[2], fn[3]);
                    if (arities != null) candidates.Add((v, cls.ToString(), arities));
                }
            }
        }
        for (object p = x; p is Cons pc; p = pc.cdr) Walk(pc.car, fnNames, defs, candidates);
    }

    private static readonly Symbol s_letrec = Symbol.Intern("letrec");

    /// <summary>def 的初值是 fn*：<c>($clj-fn …)</c>，或命名 fn* 的 <c>(letrec ((f ($clj-fn …))) f)</c>（自引用在类里是 this，没有自由变量）。</summary>
    private static List<object> FnNode(object init)
    {
        var xs = Items(init);
        if (xs == null) return null;
        if (xs.Count == 4 && ReferenceEquals(xs[0], s_cljFn)) return xs;
        if (xs.Count == 3 && ReferenceEquals(xs[0], s_letrec) && Items(xs[1]) is { Count: 1 } bs && Items(bs[0]) is { Count: 2 } b
            && b[0] is Symbol name && ReferenceEquals(xs[2], name))
            return FnNode(b[1]);
        return null;
    }

    /// <summary>($clj-fn '类名 '变长必需个数|#f (case-lambda (形参 体) …)) 的 arity：固定子句为形参个数，变长子句为 -(必需个数 + 1)。</summary>
    private static int[] FnArities(object variadic, object lambda)
    {
        var l = Items(lambda);
        if (l == null || l.Count < 2 || !ReferenceEquals(l[0], s_caseLambda)) return null;
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
