using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection.Emit;
using Cleat.Compiler.Emit;
using Cleat.Compiler.Ir;
using Cleat.Compiler.Lower;
using Cleat.Core.Compiler;
using Cleat.Core.Runtime;
using clojure.lang;

namespace Cleat.Compiler.Driver;

/// <summary>正在编的单元（driver.clj 的 DriverOps.State）：收集顶层形式的 L0（求值之后，后序）。</summary>
internal sealed class DriverState
{
    public List<Expr> Unit;
    public bool Boot;
    public PackageState Package;
}

internal sealed class PackageState
{
    /// <summary>根目录（为 null 表示 true：加载到的文件都编进来）。</summary>
    public List<string> Roots;
    public readonly List<(string Key, List<Expr> Forms)> Segments = new List<(string, List<Expr>)>();
}

public static partial class CsDriver
{
    [ThreadStatic] private static DriverState t_state;
    private static int s_formOrdinal;

    internal static DriverState State => t_state;

    private static T WithState<T>(DriverState s, Func<T> body)
    {
        var old = t_state;
        var oldClj = DriverOps.State.Value;
        t_state = s;
        // 分析器读 DriverOps.State 的 :package（inline-target 的包构建准备，cleat-145；两个分析器相同）
        DriverOps.State.Value = s.Package != null ? RT.map(Keyword.intern(null, "package"), true) : (object)PersistentArrayMap.EMPTY;
        try { return body(); }
        finally
        {
            t_state = old;
            DriverOps.State.Value = oldClj;
        }
    }

    /// <summary>构建镜像时每个顶层形式开始前把 RT 的 id 计数器置为 编号 × 100000（gensym 与 fn 类名不随进程历史变化）。</summary>
    private static void ResetIds() => RT.setNextID(++s_formOrdinal * 100000);

    // —— load ——

    private static bool PackageFile(PackageState pkg, string path)
    {
        if (pkg.Roots == null) return true;
        var full = AotPaths.PackageFullPath(path);
        return pkg.Roots.Any(r => AotPaths.UnderDirectory(full, r));
    }

    /// <summary>编进镜像或包的文件：*file*、*source-path* 与源文件绑成加载名（产物里不带构建机的绝对路径，cleat-091 / 158）。</summary>
    private static object LoadBuilt(TextReader reader, string path, string name)
    {
        Var.pushThreadBindings(RT.mapUniqueKeys(RT.SourceVar, name));
        string old = DriverOps.SourceFile.Value;
        DriverOps.SourceFile.Value = name;
        Var.pushThreadBindings(RT.mapUniqueKeys(RT.SourcePathVar, name));
        try { return LoadReader(reader, path, name); }
        finally
        {
            Var.popThreadBindings();
            DriverOps.SourceFile.Value = old;
            Var.popThreadBindings();
        }
    }

    /// <summary>load-source 钩子。</summary>
    public static object LoadHook(TextReader reader, string path, string name)
    {
        var st = t_state;
        var pkg = st?.Package;
        if (pkg != null && st.Boot != true && PackageFile(pkg, path))
        {
            var unit = new List<Expr>();
            var v = WithState(new DriverState { Unit = unit, Package = pkg }, () => LoadBuilt(reader, path, name));
            string k = AotPaths.LoadKey(path, name);
            if (!pkg.Segments.Any(s => s.Key == k)) pkg.Segments.Add((k, unit));
            return v;
        }
        bool boot = st?.Boot == true;
        return WithState(new DriverState { Unit = boot ? st.Unit : null, Boot = boot, Package = pkg },
                         () => boot ? LoadBuilt(reader, path, name) : LoadReader(reader, path, name));
    }

    /// <summary>compile 钩子（*compile-files* 为真时加载文件）：这个文件与它加载到的、还没加载过的文件编成一个包程序集。</summary>
    public static object CompileHook(TextReader reader, string path, string name)
    {
        if (!RT.booleanCast(RT.CompileFilesVar.deref())) return LoadHook(reader, path, name);
        if (t_state?.Package != null) return LoadHook(reader, path, name);
        var pkg = new PackageState();
        var v = WithState(new DriverState { Package = pkg }, () => LoadHook(reader, path, name));
        string k = AotPaths.LoadKey(path, name);
        CompilePackageUnit(pkg.Segments, AotPaths.LoadKeyNamespace(k), AotPaths.CompileOutputPath(path, name), AotPaths.ClojureImageId());
        return v;
    }

    // —— 分析器的开关（analyzer.clj 的 inline-calls? / reflection-warnings? / restrict-loop-inlines?）——

    /// <summary>C# 分析器的开关（按 Clojure 原名）。</summary>
    private static (Func<bool> Get, Action<bool> Set) CsAnalyzerFlag(string name) => name switch
    {
        "inline-calls?" => (() => Analyzer.FormAnalyzer.InlineCalls, v => Analyzer.FormAnalyzer.InlineCalls = v),
        "reflection-warnings?" => (() => Analyzer.FormAnalyzer.ReflectionWarnings, v => Analyzer.FormAnalyzer.ReflectionWarnings = v),
        "restrict-loop-inlines?" => (() => Analyzer.FormAnalyzer.RestrictLoopInlines, v => Analyzer.FormAnalyzer.RestrictLoopInlines = v),
        _ => throw new ArgumentException("analyzer flag " + name),
    };

    /// <summary>在 body 期间把分析器的开关置为给定值，之后还原。</summary>
    private static T WithAnalyzerFlags<T>(IEnumerable<(string Name, bool Value)> flags, Func<T> body)
    {
        var cs = flags.Select(f => (Flag: CsAnalyzerFlag(f.Name), Old: CsAnalyzerFlag(f.Name).Get(), f.Value)).ToList();
        foreach (var f in cs) f.Flag.Set(f.Value);
        try { return body(); }
        finally { foreach (var f in cs) f.Flag.Set(f.Old); }
    }

    // —— 镜像 ——

    /// <summary>依次求值形式（boot-order 的 load / require），全部加载到的文件的顶层形式按求值顺序（后序）收集成一个单元，编译为镜像 dll。</summary>
    public static object CompileBoot(object forms, string dll)
    {
        var unit = new List<Expr>();
        s_formOrdinal = 0;
        // 没有起步镜像（--no-boot，cleat-181）：clojure.core 的前向引用先 declare
        bool fromScratch = Cleat.Core.Runtime.Startup.ClojureBootPath == null;
        Analyzer.FormAnalyzer.DeclareCoreForwardRefs = fromScratch;
        try
        {
            // 构建镜像时不做新的内联决定（种子与两级镜像的 .inl 必须一致），也不报反射警告（cleat-099）
            WithAnalyzerFlags(new[] { ("inline-calls?", false), ("reflection-warnings?", false) }, () =>
                WithState(new DriverState { Unit = unit, Boot = true }, () =>
                {
                    for (var s = RT.seq(forms); s != null; s = s.next())
                    {
                        ResetIds();
                        EvalToplevel(s.first());
                    }
                    return (object)null;
                }));
        }
        finally { Analyzer.FormAnalyzer.DeclareCoreForwardRefs = false; }
        string asm = Path.GetFileNameWithoutExtension(dll);
        var decl = CompileUnit(unit, asm, "#Module", false);
        if (File.Exists(dll)) File.Delete(dll);
        IlEmitter.Save(decl, dll, true);
        var file = BuildInterfaceFile(asm, unit, decl, DriverOps.Mvid(dll));
        string ifc = InterfaceFile.PathFor(dll);
        if (File.Exists(ifc)) File.Delete(ifc);
        file.Save(ifc);
        return dll;
    }

    // —— 包程序集（designs/17 §2）——

    /// <summary>在 user 命名空间里依次求值形式（通常是 (require 'ns) …），根目录下被加载到的文件编成一个包程序集。</summary>
    public static object CompilePackage(string package, object forms, object roots, string dll, object imageId)
    {
        var pkg = new PackageState { Roots = roots is true ? null : ((System.Collections.IEnumerable)RT.seq(roots) ?? Array.Empty<object>()).Cast<object>().Select(r => r.ToString()).ToList() };
        // 包构建不做 eval 专属的循环体内联限制（cleat-120）
        WithAnalyzerFlags(new[] { ("restrict-loop-inlines?", false) }, () =>
            WithState(new DriverState { Package = pkg }, () =>
            {
                for (var s = RT.seq(forms); s != null; s = s.next()) EvalToplevel(s.first());
                return (object)null;
            }));
        CompilePackageUnit(pkg.Segments, package, dll, imageId is string ? imageId : AotPaths.ClojureImageId());
        return dll;
    }

    private static void CompilePackageUnit(List<(string Key, List<Expr> Forms)> segments, string package, string dll, object imageId)
    {
        var check = new List<Expr>();
        if (imageId is string id)
            check.Add(new CljCall("CheckBootImage", false, ImmutableArray.Create<Expr>(new Quote(id))));
        var l0s = check.Concat(segments.SelectMany(s => s.Forms)).ToList();
        string asm = Path.GetFileNameWithoutExtension(dll);
        var decl = CompileUnit(l0s, asm, "#Module", false);
        // 入口 Initialize 只做镜像核对与预链接；每个文件一个入口 Load<i> 依次调它的 Init<k>
        int nfixed = check.Count;
        var initOf = decl.Module.Methods.Where(m => m.Name.StartsWith("Init", StringComparison.Ordinal) && m.Name != "Initialize")
                                        .ToDictionary(m => int.Parse(m.Name.Substring(4)));
        decl.Entry.Body.Clear();
        for (int k = 0; k < nfixed; k++)
        {
            decl.Entry.Body.Add(new Il(OpCodes.Call, initOf[k]));
            decl.Entry.Body.Add(new Il(OpCodes.Pop));
        }
        decl.Entry.Body.Add(new Il(OpCodes.Ldnull));
        decl.Entry.Body.Add(new Il(OpCodes.Ret));
        decl.LazyStart = true;
        decl.Attributes.Add(("Cleat.Core.CleatPackageAttribute", new[] { package }));
        if (imageId is string bootId) decl.Attributes.Add(("Cleat.Core.CleatBootImageAttribute", new[] { bootId }));   // cleat-186
        int start = nfixed;
        for (int i = 0; i < segments.Count; i++)
        {
            int n = segments[i].Forms.Count;
            var load = new MethodDecl("Load" + i) { Static = true, Ret = Rt.Obj };
            if (n == 0)
            {
                load.Body.Add(new Il(OpCodes.Ldnull));
                load.Body.Add(new Il(OpCodes.Ret));
            }
            else
                for (int k = start; k < start + n; k++)
                {
                    load.Body.Add(new Il(OpCodes.Call, initOf[k]));
                    load.Body.Add(new Il(k == start + n - 1 ? OpCodes.Ret : OpCodes.Pop));
                }
            decl.Module.AddMethod(load);
            decl.Attributes.Add(("Cleat.Core.CleatNamespaceAttribute", new[] { segments[i].Key, decl.Module.Name, load.Name }));
            start += n;
        }
        string dir = Path.Combine(Path.GetTempPath(), "cleat-ifc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string ifc = Path.Combine(dir, InterfaceFile.ResourceName);
            BuildInterfaceFile(asm, l0s, decl, Guid.Empty).Save(ifc);
            decl.Resources.Add(("cleat.ifc", ifc));
            string inl = InterfaceFile.InlinePathFor(ifc);
            if (File.Exists(inl)) decl.Resources.Add(("cleat.inl", inl));
            if (File.Exists(dll)) File.Delete(dll);
            IlEmitter.Save(decl, dll, true);
        }
        finally
        {
            try { Directory.Delete(dir, true); }
            catch (IOException) { }
        }
    }

    // —— 接口文件（driver.clj build-interface-file）——

    /// <summary>def 的初值是 fn*：(lambda FnName …)；带元数据时外面套 (clj-call "WithMetaOf" …)；命名 fn* 是 (letrec ([f (lambda …)]) f)。</summary>
    private static Lambda FnNode(Expr e) => e switch
    {
        Lambda { Name: FnName } l => l,
        CljCall { Op: "WithMetaOf" } c when c.Args.Length > 0 => FnNode(c.Args[0]),
        Letrec { Vars.Length: 1 } lr when lr.Body is Ir.Ref r && r.Name.Equals(lr.Vars[0]) => FnNode(lr.Inits[0]),
        _ => null,
    };

    private static string FnClass(Lambda le) => (string)((FnName)le.Name).Class;

    /// <summary>各子句的形参个数；变长子句记为 -(必需 + 1)。</summary>
    private static int[] FnArities(Lambda le)
    {
        var r = le.Clauses.Select(c => c.Params.Length).ToArray();
        if (((FnName)le.Name).Variadic is long req)
        {
            int n = (int)req + 1;
            int idx = Array.LastIndexOf(r, n);
            if (idx >= 0) r[idx] = -n;
        }
        return r;
    }

    private static void WalkL0(Expr x, Dictionary<string, int> fnNames, Dictionary<Var, int> defs, List<(Var V, string Cls, int[] Arities, Expr E0)> cands)
    {
        var w = new L0Walker(fnNames, defs, cands);
        w.Walk(x);
    }

    private sealed class L0Walker : Walker
    {
        private readonly Dictionary<string, int> _fnNames;
        private readonly Dictionary<Var, int> _defs;
        private readonly List<(Var, string, int[], Expr)> _cands;

        public L0Walker(Dictionary<string, int> fnNames, Dictionary<Var, int> defs, List<(Var, string, int[], Expr)> cands)
        {
            _fnNames = fnNames;
            _defs = defs;
            _cands = cands;
        }

        public override void VisitLambda(Lambda n)
        {
            if (n.Name is FnName nm) _fnNames[(string)nm.Class] = (_fnNames.TryGetValue((string)nm.Class, out var c) ? c : 0) + 1;
            base.VisitLambda(n);
        }

        public override void VisitCljDef(CljDef n)
        {
            if (n.Datum is Var v)
            {
                _defs[v] = (_defs.TryGetValue(v, out var c) ? c : 0) + 1;
                if (FnNode(n.Expr0) is { } le) _cands.Add((v, FnClass(le), FnArities(le), n.Expr0));
            }
            base.VisitCljDef(n);
        }
    }

    private static InterfaceFile BuildInterfaceFile(string asm, IReadOnlyList<Expr> l0s, UnitDecl decl, Guid imageId)
    {
        var types = new HashSet<string>(decl.AllClasses.Select(c => c.Name), StringComparer.Ordinal);
        var fnNames = new Dictionary<string, int>(StringComparer.Ordinal);
        var defs = new Dictionary<Var, int>(ReferenceEqualityComparer.Instance);
        var cands = new List<(Var V, string Cls, int[] Arities, Expr E0)>();
        var accessors = new Accessors.Table();
        foreach (var l0 in l0s)
        {
            WalkL0(l0, fnNames, defs, cands);
            Accessors.Collect(l0, accessors);   // driver.clj walk-l0 的 clj-deftype 分支（访问器内联，默认关）
        }
        var file = new InterfaceFile { AssemblyName = asm, ImageId = imageId };
        var redef = Keyword.intern(null, "redef");
        foreach (var (v, cls, arities, e0) in cands)
        {
            if (defs[v] != 1 || !fnNames.TryGetValue(cls, out var fc) || fc != 1 || !types.Contains(cls) || v.isDynamic() || RT.booleanCast(RT.get(v.meta(), redef)))
                continue;
            var e = new InterfaceFile.Entry
            {
                Var = Symbol.intern(v.Namespace.Name.Name, v.Symbol.Name),
                ClassName = cls + ", " + asm,
                Arities = arities,
                HasStatic = true,
            };
            if (!v.IsMacro)
                foreach (var (n, bytes) in InlineClauses(e0, accessors)) DriverOps.AddInline(e, n, bytes);
            file.Entries.Add(e);
        }
        return file;
    }

    /// <summary>eval 的顶层 (def v (fn* …))（cleat-116）：登记已知函数与可内联的小子句（同接口文件）；宏与动态 Var 不登记。</summary>
    private static void RegisterEvalInlines(Expr l0, UnitDecl unit)
    {
        if (unit == null || l0 is not CljDef { Datum: Var v } d || v.isDynamic() || v.IsMacro || FnNode(d.Expr0) is not { } le) return;
        string cls = FnClass(le);
        if (!unit.Classes.Any(c => c.Name == cls)) return;
        var e = new InterfaceFile.Entry
        {
            Var = Symbol.intern(v.Namespace.Name.Name, v.Symbol.Name),
            ClassName = cls + ", " + unit.Name,
            Arities = FnArities(le),
            HasStatic = true,
        };
        foreach (var (n, bytes) in InlineClauses(d.Expr0, Accessors.EvalAccessors)) DriverOps.AddInline(e, n, bytes);
        InterfaceFile.RegisterEntry(e);
    }

    // —— 可内联的小子句（driver.clj inline-clauses / inline-ok?；cleat-145 的访问器特化见 Accessors，默认关）——

    private const int InlineMaxPairs = 160, InlineMaxPairsArith = 400;
    private static readonly HashSet<string> NoInlineHeads = new HashSet<string> { "set!", "clj-def", "clj-var-set!", "clj-deftype", "try-catch", "try-finally" };
    private static readonly HashSet<string> ArithOnlyCalls = new HashSet<string> { "CallStatic", "GetInstanceField", "GetInstanceFieldDirect", "NewOf", "Tagged" };
    private static readonly HashSet<string> NotArithHeads = new HashSet<string> { "clj-invoke", "clj-keyword-invoke", "clj-static-call", "clj-var-ref", "clj-var-set!", "set!" };

    private static bool HeadIs(object x, out string head)
    {
        head = x is Symbol { Namespace: null } s ? s.Name : null;
        return head != null;
    }

    private static bool ArithOnly(object x)
    {
        if (x is not ISeq and not IPersistentList || RT.seq(x) is not ISeq s) return true;
        if (HeadIs(s.first(), out var h))
        {
            if (h == "quote") return true;
            if (NotArithHeads.Contains(h)) return false;
            if (h == "clj-call")
            {
                if (!ArithOnlyCalls.Contains(RT.second(s) as string)) return false;
                for (var r = s.next()?.next()?.next(); r != null; r = r.next())
                    if (!ArithOnly(r.first())) return false;
                return true;
            }
        }
        for (var r = s; r != null; r = r.next())
            if (!ArithOnly(r.first())) return false;
        return true;
    }

    /// <summary>序对个数不超过上限，不含赋值、定义、类型、try 与嵌套的 fn*；只有字段读、算术与构造的子句上限放宽（cleat-123）。</summary>
    private static bool InlineOk(object sexpr)
    {
        int max = ArithOnly(sexpr) ? InlineMaxPairsArith : InlineMaxPairs;
        int pairs = 0;
        bool Ok(object x)
        {
            if (x is not ISeq and not IPersistentList) return true;
            bool first = true;
            for (var xs = RT.seq(x); xs != null; xs = xs.next(), first = false)
            {
                var h = xs.first();
                if (++pairs > max) return false;
                if (first && HeadIs(h, out var hn))
                {
                    if (hn == "quote") return true;
                    if (NoInlineHeads.Contains(hn)) return false;
                    if (hn == "lambda" && xs.next()?.first() is IPersistentMap m && m.containsKey(FnName.ClassKey)) return false;
                }
                if (h is ISeq or IPersistentList && !Ok(h)) return false;
            }
            return true;
        }
        return Ok(sexpr);
    }

    private static object CallStatic(string method, object arg) =>
        PersistentList.create(new object[]
        {
            Symbol.intern("clj-call"), "CallStatic", 3L, RT.list(Symbol.intern("quote"), typeof(ClojureStaticCalls)),
            RT.list(Symbol.intern("quote"), method), RT.list(Symbol.intern("quote"), null), arg,
        });

    /// <summary>有原始签名的子句：L / D 形参经 PrimArgLong / PrimArgDouble 重新绑定、结果经 PrimRetLong / PrimRetDouble（同 invoke → invokePrim 的桥）。</summary>
    private static object PrimSigWrap(object cl, string sig)
    {
        if (sig == null) return cl;
        var items = ((System.Collections.IEnumerable)cl).Cast<object>().ToList();
        var xs = ((System.Collections.IEnumerable)items[1]).Cast<object>().Select(o => (Symbol)o).ToList();
        object rst = items[2], body = items[3];
        char rc = sig[^1];
        var prim = new List<(Symbol X, Symbol P, string F)>();
        for (int k = 0; k < xs.Count && k < sig.Length - 1; k++)
        {
            string f = sig[k] == 'L' ? "PrimArgLong" : sig[k] == 'D' ? "PrimArgDouble" : null;
            if (f != null) prim.Add((xs[k], Symbol.intern(xs[k].Name + "$pa"), f));
        }
        var m = prim.ToDictionary(p => p.X, p => p.P);
        object Subst(object y) => y switch
        {
            Symbol s when m.TryGetValue(s, out var p) => p,
            ISeq or IPersistentList => WithMetaOf(y, RT.seq(y) is ISeq sq ? PersistentList.create(Enumerate(sq).Select(Subst).ToList()) : PersistentList.EMPTY),
            IPersistentVector v => PersistentVector.create(((System.Collections.IEnumerable)v).Cast<object>().Select(Subst).ToList()),
            _ => y,
        };
        object body1 = m.Count == 0 ? body : Subst(body);
        object body2 = rc == 'L' ? CallStatic("PrimRetLong", body1) : rc == 'D' ? CallStatic("PrimRetDouble", body1) : body1;
        object newBody = prim.Count == 0
            ? body2
            : RT.listStar(Symbol.intern("call"),
                          RT.list(Symbol.intern("lambda"), false, RT.list(Symbol.intern("clause"), PersistentList.create(prim.Select(p => (object)p.P).ToList()), false, body2)),
                          RT.seq(PersistentList.create(prim.Select(p => CallStatic(p.F, p.X)).ToList())));
        return RT.list(Symbol.intern("clause"), items[1], rst, newBody);
    }

    private static IEnumerable<object> Enumerate(ISeq s)
    {
        for (; s != null; s = s.next()) yield return s.first();
    }

    private static object WithMetaOf(object from, object to) =>
        from is IMeta fm && fm.meta() is { } meta && to is IObj o ? o.withMeta(meta) : to;

    /// <summary>def 的初值（不带自引用的 fn*，或外包 WithMetaOf）的可内联固定子句：(参数个数, fasl 字节)。</summary>
    private static IEnumerable<(int N, byte[] Bytes)> InlineClauses(Expr e0, Accessors.Table accessors)
    {
        Lambda le = e0 switch
        {
            Lambda { Name: FnName } l => l,
            CljCall { Op: "WithMetaOf" } c when c.Args.Length > 0 && c.Args[0] is Lambda { Name: FnName } l2 => l2,
            _ => null,
        };
        if (le == null) yield break;
        var nm = (FnName)le.Name;
        int lastFixed = nm.Variadic is long ? le.Clauses.Length - 1 : le.Clauses.Length;
        var tag = Keyword.intern("cleat", "l0");
        for (int k = 0; k < lastFixed; k++)
        {
            var cl = le.Clauses[k];
            string sig = null;
            if (nm.Prims is System.Collections.IEnumerable prims)
                foreach (var p in prims)
                {
                    var pair = ((System.Collections.IEnumerable)p).Cast<object>().ToList();
                    if (Convert.ToInt64(pair[0]) == cl.Params.Length) { sig = (string)pair[1]; break; }
                }
            var raw = PrimSigWrap(IrPrinter.ToData(cl), sig);
            // 访问器特化多包了一层守卫，可能超出内联上限：这时退回未特化的子句，不能因此丢掉原有的内联
            var sp = Analyzer.FormAnalyzer.OptAccinline && accessors.Count > 0 ? Accessors.SpecializeClause(raw, accessors) : null;
            var cand = sp ?? raw;
            var sexpr = InlineOk(cand) ? cand : raw;
            if (!InlineOk(sexpr)) continue;
            byte[] bytes;
            try { bytes = Fasl.Write(RT.vector(tag, sexpr)); }
            catch (Exception) { continue; }   // 常量里有 fasl 写不了的对象：不内联
            yield return (cl.Params.Length, bytes);
        }
    }

    // —— 给宿主的编译入口（DriverOps.InstallCompileHooks）——

    internal sealed class CompileBootFn : AFn
    {
        public override object invoke(object forms, object dll) => CompileBoot(forms, (string)dll);
    }

    internal sealed class CompilePackageFn : AFn
    {
        public override object invoke(object name, object forms, object roots, object dll) => CompilePackage((string)name, forms, roots, (string)dll, null);
        public override object invoke(object name, object forms, object roots, object dll, object imageId) => CompilePackage((string)name, forms, roots, (string)dll, imageId);
    }
}
