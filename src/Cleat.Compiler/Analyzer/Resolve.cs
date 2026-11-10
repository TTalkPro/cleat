using System.Linq;
using System;
using System.Collections.Generic;
using Cleat.Core.Compiler;
using Cleat.Core.Expand;
using Cleat.Core.Runtime;
using clojure.lang;
using static Cleat.Compiler.Analyzer.Clj;
using Symbol = clojure.lang.Symbol;

namespace Cleat.Compiler.Analyzer;

// 源位置、错误、决议、类型名与宿主语法糖（analyzer.clj 的"小工具""错误""决议""宿主语法糖"几节，原版 namespaces.sls / syntax.sls）。
public static partial class FormAnalyzer
{
    /// <summary>opt-crossns-type?（cleat-144）：生成类的跨 ns 决议与缺失依赖的编译期加载共用这个开关。</summary>
    private static readonly bool s_optCrossnsType = !OptFlags.Off("crossnstype");

    private static Namespace CurrentNs => (Namespace)RT.CurrentNSVar.deref();

    private static string MungeStr(string s) => HostSyntax.Munge(s);

    private static Symbol TagOf(object x) => HostSyntax.TagOf(x);

    /// <summary>tag-type：提示 → 类型；不认识的点分名先 require 它所在的 ns 再试（cleat-144）。</summary>
    private static Type TagType(object t)
    {
        if (!Truthy(t)) return null;
        var r = HostSyntax.TagType(t);
        if (r != null || !s_optCrossnsType) return r;
        string n = t switch
        {
            Symbol s => s.Name,
            string str => str,
            Type ty => ty.FullName,
            _ => null,
        };
        if (n != null && n.Contains('.'))
        {
            RequireTypeNs(n);
            return HostSyntax.TagType(t);
        }
        return null;
    }

    private static string TypeName(Type t) => HostSyntax.TypeName(t);

    /// <summary>class-for-name：RT.classForName，失败时查编译期登记表（同进程刚分析过、还没求值的 deftype，cleat-144）。</summary>
    private static Type ClassForName(string n)
    {
        Type t = null;
        try { t = RT.classForName(n); }
        catch (Exception) { }
        if (t != null || !s_optCrossnsType) return t;
        try { return CompileTimeTypes.Get(n); }
        catch (Exception) { return null; }
    }

    /// <summary>require-type-ns!：类型名的 ns 部分当作 Clojure ns 加载（失败吞掉：宿主包名不是 Clojure ns）。</summary>
    private static void RequireTypeNs(string n)
    {
        if (!s_optCrossnsType || n == null) return;
        int dot = n.LastIndexOf('.');
        if (dot <= 0) return;
        string typeNs = n.Substring(0, dot);
        var requireVar = RT.var("clojure.core", "require");
        // 当前 ns 里刚分析 / 即将求值的生成类不该回头 require 自己
        if (Equals(Symbol.intern(typeNs), CurrentNs.Name)) return;
        try { requireVar.invoke(V(Symbol.intern(typeNs))); }
        catch (Exception) { }
    }

    /// <summary>import-class-for-name：找不到时 require 类型 ns 后重试，并立刻在当前 ns 里 import。</summary>
    private static Type ImportClassForName(string n)
    {
        var t0 = ClassForName(n);
        if (t0 != null) return t0;
        RequireTypeNs(n);
        var t = ClassForName(n);
        if (t != null) ClojureOps.ImportOf(t);
        return t;
    }

    private static readonly Keyword[] s_sourceKeys = { LineKw, ColumnKw, FileKw, SourceSpanKw, EndLineKw, EndColumnKw };

    /// <summary>user-meta：fn* / reify 的用户元数据，去掉 reader 的源位置（JVM 的 FnExpr 同样去掉 :line :column :file）；空时 nil。
    /// 集合字面量不用它：它们的元数据原样保留（<see cref="LiteralMeta"/>）。</summary>
    private static IPersistentMap UserMeta(object x)
    {
        if (Meta(x) is not IPersistentMap m) return null;
        foreach (var k in s_sourceKeys) m = m.without(k);
        return m.count() > 0 ? m : null;
    }

    /// <summary>集合字面量的元数据（cleat-167）：原样，空时 nil——reader 不给向量 / map / 集合记位置，TransferSource 也不给它们带位置，所以这里没有要剥的键。</summary>
    private static IPersistentMap LiteralMeta(object x) => Meta(x) is IPersistentMap m && m.count() > 0 ? m : null;

    /// <summary>real-source：源文件名，去掉占位符 NO_SOURCE_PATH / NO_SOURCE_FILE（cleat-158）。</summary>
    private static string RealSource(object f)
    {
        if (f == null) return null;
        string s = Str(f);
        return s.Length > 0 && s != "NO_SOURCE_PATH" && s != "NO_SOURCE_FILE" ? s : null;
    }

    private static string SourcePath() => Str(RT.SourcePathVar.deref());

    private static long? ParseLong(object x) => long.TryParse(Str(x), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>source-of：形式自己的源位置；没有 :line 时 nil。文件缺省取分析时的 *source-path*（cleat-158）。</summary>
    private static Pos SourceOf(object x)
    {
        if (x is not IMeta im || im.meta() is not { } m) return null;
        var line = m.valAt(LineKw);
        if (!Truthy(line)) return null;
        var col = m.valAt(ColumnKw);
        return new Pos(ParseLong(line), ParseLong(Truthy(col) ? col : 1L), RealSource(m.valAt(FileKw)) ?? RealSource(SourcePath()));
    }

    // —— 错误（namespaces.sls expander-exception / clojure-error-hook）——

    private static readonly HashSet<string> s_specialNames = new HashSet<string>
    {
        "def", "if", "do", "let*", "loop*", "recur", "fn*", "quote", "var", "throw", "try", "monitor-enter", "monitor-exit", ".", "new",
        "set!", "letfn*", "case*", "deftype*", "reify*", "proxy-class*", "gen-class*", "await*", "import*", "escape*", "catch", "finally", "&",
    };

    /// <summary>special-head：形式头部的特殊形式名（未限定；import* 也可以是 clojure.core/import*），否则 null。</summary>
    private static string SpecialHead(object x)
    {
        if (x is not Symbol s) return null;
        if (s.Namespace == null && s_specialNames.Contains(s.Name)) return s.Name;
        if (s.Namespace == "clojure.core" && s.Name == "import*") return "import*";
        return null;
    }

    private static Pos Position(object form, Ctx ctx) => SourceOf(form) ?? ctx.Pos;

    private enum Phase { MacroSyntaxCheck, Macroexpand, CompileSyntaxCheck }

    /// <summary>expander-exception</summary>
    private static ExpanderException ExpanderEx(Pos pos, Phase phase, string msg, Exception cause, string sym = null) =>
        new ExpanderException(pos?.File ?? SourcePath(), (int)(pos?.Line ?? 0), (int)(pos?.Column ?? 0),
            phase switch
            {
                Phase.MacroSyntaxCheck => ExpanderException.PhaseSyntax,
                Phase.Macroexpand => ExpanderException.PhaseMacroexpand,
                _ => ExpanderException.PhaseCompile,
            },
            msg, cause, sym);

    /// <summary>
    /// err：语法错误（compile-syntax-check）；位置取形式自己的或上下文的；出错形式是特殊形式时带它的名字。调用处写 <c>throw Err(…)</c>。
    /// </summary>
    private static ExpanderException Err(object form, Ctx ctx, string msg) =>
        ExpanderEx(Position(form, ctx), Phase.CompileSyntaxCheck, msg, null, IsList(form) ? SpecialHead(RT.first(form)) : null);

    // —— 决议（namespaces.sls resolve-in）——

    /// <summary>从零编镜像期间为真（CsDriver.CompileBoot 在没有加载镜像时设）：clojure.core 的前向引用先 intern 成未绑定的 Var。</summary>
    internal static bool DeclareCoreForwardRefs;

    /// <summary>namespace-for：别名或命名空间名。</summary>
    private static Namespace NamespaceFor(Namespace ns, string nsname)
    {
        var s = Symbol.intern(nsname);
        return ns.LookupAlias(s) ?? Namespace.find(s);
    }

    /// <summary>resolve-in → (对象, null, false) 或 (null, 错误文本, private?)。</summary>
    private static (object O, string Msg, bool Private) ResolveIn(Namespace ns, Symbol sym)
    {
        string nsname = sym.Namespace, nm = sym.Name;
        if (nsname != null)
        {
            var target = NamespaceFor(ns, nsname);
            if (target == null) return (null, "No such namespace: " + nsname, false);
            var v = target.FindInternedVar(Symbol.intern(nm));
            // 从零编镜像（没有起步镜像，cleat-181）时 clojure.core 的前向引用：core.clj 在 concat 定义之前就有语法引用
            // （`not` 的 :inline，展开成对 clojure.core/concat 的调用，在延后才调用的 fn 里）——以前起步镜像里已有这些 Var。
            // 先 intern 成未绑定的 Var（同 declare），之后的 def 绑定它；只在这个模式下、只对 clojure.core
            if (v == null && DeclareCoreForwardRefs && target.Name.Name == "clojure.core") v = Var.intern(target, Symbol.intern(nm));
            if (v == null) return (null, "No such var: " + sym, false);
            if (!ReferenceEquals(v.Namespace, ns) && !v.isPublic) return (null, "var: " + sym + " is not public", true);
            return (v, null, false);
        }
        int dot = nm.IndexOf('.');
        if (dot > 0 || nm.EndsWith("]", StringComparison.Ordinal))
        {
            var t = ClassForName(nm);
            return t != null ? (t, null, false) : (null, "Unable to resolve classname: " + nm, false);
        }
        if (nm == "ns") return (RT.NSVar, null, false);
        if (nm == "in-ns") return (RT.InNSVar, null, false);
        var o = ns.GetMapping(Symbol.intern(nm));
        return o != null ? (o, null, false) : (null, "Unable to resolve symbol: " + sym + " in this context", false);
    }

    // —— 宿主语法糖（syntax.sls rewrite-to-dot / static-member-of）——

    /// <summary>maybe-type：形式是否指一个类型（同名局部遮蔽简单名）。</summary>
    private static Type MaybeType(Ctx ctx, object form) =>
        HostSyntax.MaybeType(form, form is Symbol s && s.Namespace == null && ctx.FindLocal(s) != null);

    /// <summary>static-member-of：Type/member → (类型, 成员名) 或 null。</summary>
    private static (Type Type, string Member)? StaticMemberOf(Ctx ctx, Symbol sym)
    {
        string nsname = sym.Namespace;
        if (nsname == null || NamespaceFor(CurrentNs, nsname) != null || HostSyntax.IsPosDigit(sym.Name)) return null;
        var t = MaybeType(ctx, Symbol.intern(nsname));
        return t != null ? (t, sym.Name) : null;
    }

    private static readonly Keyword[] s_transferKeys = { LineKw, ColumnKw, SourceSpanKw, TagKw };

    /// <summary>transfer-source：把形式 old 的 :line :column :source-span :tag 带到 x 上（自己有的不动）。</summary>
    public static object TransferSource(object x, object old)
    {
        if (x is not IObj o) return x;
        var om = Meta(old);
        IPersistentMap nm = o.meta() ?? (IPersistentMap)PersistentArrayMap.EMPTY;
        foreach (var k in s_transferKeys)
        {
            // 源位置只带给列表（错误位置只看列表；JVM 的 reader 也只给列表记位置）。向量 / map / 集合字面量的元数据按用户写的原样保留
            // （cleat-167：原来连它们也带上 :line，于是 AnalyzeDatum 得把位置键都剥掉，用户自己写的 ^{:line 9} 也被一并剥掉）；:tag 照旧带给任何 IObj（JVM 的 preserveTag）
            if (!ReferenceEquals(k, TagKw) && x is not ISeq) continue;
            var v = Boundary(Get(om, k));
            if (Truthy(v) && !Truthy(nm.valAt(k))) nm = nm.assoc(k, v);
        }
        return o.withMeta(nm);
    }

    private static readonly Symbol DotSym = Symbol.intern(null, "."), NewSym = Symbol.intern(null, "new"),
        IdentitySym = Symbol.intern("clojure.core", "identity"), SystemTypeSym = Symbol.intern(null, "System.Type"),
        FnStarSym = Symbol.intern(null, "fn*"), QuoteSym = Symbol.intern(null, "quote"), LambdaSym = Symbol.intern(null, "lambda");

    private static IPersistentList ListStar(IEnumerable<object> heads, IEnumerable<object> rest)
    {
        var l = new List<object>(heads);
        l.AddRange(rest);
        return PersistentList.create(l);
    }

    private static IEnumerable<object> Drop(List<object> xs, int n)
    {
        for (int i = n; i < xs.Count; i++) yield return xs[i];
    }

    /// <summary>rewrite-to-dot：(.m x …) / (T. …) / (T/m …) → (. …) 或 (new …)；不是宿主糖返回 null，. 开头的原样。</summary>
    private static object RewriteToDot(Ctx ctx, object form, object where)
    {
        var items = Items(form);
        object op = items.Count > 0 ? items[0] : null;
        if (SymIs(op, ".")) return form;
        if (op is not Symbol sop) return null;
        string nm = sop.Name;
        if (sop.Namespace == null && nm.Length > 1 && nm[0] == '.')
        {
            if (items.Count < 2) throw Err(where, ctx, "Malformed member expression, expecting (.member target ...)");
            object target = items[1];
            if (MaybeType(ctx, target) != null)
                target = WithMeta(RT.list(IdentitySym, target), RT.map(TagKw, SystemTypeSym));
            return TransferSource(ListStar(new[] { DotSym, target, Symbol.intern(nm.Substring(1)) }, Drop(items, 2)), form);
        }
        int li = nm.LastIndexOf('.');
        if (sop.Namespace == null && li > 0 && li == nm.Length - 1)
            return TransferSource(ListStar(new object[] { NewSym, Symbol.intern(nm.Substring(0, nm.Length - 1)) }, Drop(items, 1)), form);
        if (sop.Namespace != null && StaticMemberOf(ctx, sop) is { } sm)
        {
            var tsym = Symbol.intern(sop.Namespace);
            items = new[] { op }.Concat(QmCallArgs(sop, Drop(items, 1).ToList())).ToList();   // param-tags（cleat-192）
            object r;
            if (nm == "new") r = ListStar(new object[] { NewSym, tsym }, Drop(items, 1));
            else if (nm.Length > 1 && nm[0] == '.')
            {
                if (items.Count < 2)
                {
                    // 措辞同 ClojureCLR；原因是 ArgumentException（JVM 的 IllegalArgumentException，test_clojure CLJ-2914 按原因的类型认，cleat-193）
                    string msg = "Malformed method expression, expecting (" + sop + " target ...)";
                    throw ExpanderEx(Position(where, ctx), Phase.CompileSyntaxCheck, msg, new ArgumentException(msg), null);
                }
                object tgt = items[1];
                if (tgt is IObj) tgt = WithMeta(tgt, RT.assoc(Meta(tgt), TagKw, Symbol.intern(sm.Type.FullName)));
                r = ListStar(new object[] { DotSym, tgt, Symbol.intern(nm.Substring(1)) }, Drop(items, 2));
            }
            else r = ListStar(new object[] { DotSym, tsym, Symbol.intern(nm) }, Drop(items, 1));
            return TransferSource(r, form);
        }
        return null;
    }

    /// <summary>host-sugar?：.m 或 T. 形式的头部符号。</summary>
    private static bool IsHostSugar(Symbol sym)
    {
        string nm = sym.Name;
        if (sym.Namespace != null) return false;
        if (nm.Length > 1 && nm[0] == '.') return true;
        int i = nm.LastIndexOf('.');
        return i >= 0 && i == nm.Length - 1;
    }

    // —— 宏调用（namespaces.sls call-macro）——

    /// <summary>locals-env：&amp;env，当前上下文的局部 sym → LocalBinding（内层遮蔽外层）；没有局部时 nil。</summary>
    private static IPersistentMap LocalsEnv(Ctx ctx)
    {
        var ls = new List<Local>(LocalList.Enumerate(LocalList.WithoutMarkers(ctx.Locals)));
        if (ls.Count == 0) return null;
        ls.Reverse();
        IPersistentMap m = PersistentArrayMap.EMPTY;
        foreach (var l in ls)
        {
            var k = l.Kind;
            m = m.assoc(l.Sym, new LocalBinding(l.Sym, l.Sym, l.Tag, k == LocalKind.Arg, k == LocalKind.Field, k == LocalKind.This, l.Mutable, 0));
        }
        return m;
    }

    private static string RemoveFnSuffix(string nm)
    {
        int i = nm.IndexOf("__", StringComparison.Ordinal);
        return i < 0 ? nm : nm.Substring(0, i);
    }

    /// <summary>macro-arity-fix：宏自己的 ArityException 把 &amp;form &amp;env 两个隐含实参减掉（Clojure 1.12 同）。</summary>
    private static Exception MacroArityFix(Var v, Exception ex)
    {
        if (ex is ArityException ae)
        {
            string nm = ae.Name;
            if (RemoveFnSuffix(nm) == MungeStr(Str(v.Namespace.Name)) + "$" + MungeStr(v.Symbol.Name))
                return new ArityException(ae.Actual - 2, nm);
        }
        return ex;
    }

    /// <summary>
    /// 宏的 spec 检查（cleat-166，Clojure 的 Compiler.checkSpecs）：加载了 clojure.spec.alpha（packages/spec.alpha）之后，宏展开前
    /// 用 <c>clojure.spec.alpha/macroexpand-check</c> 核对宏的 :args spec（s/fdef 过的宏；core 的 defn / let / ns … 在加载
    /// clojure.core.specs.alpha 后生效），不合时报 macro-syntax-check 阶段的错误。spec 是可选包：没加载时这里只是一次缓存的查找，
    /// 也不像 ClojureCLR 那样为此去加载 spec。CLOJURE_SPEC_SKIP_MACROS=true（RT.instrumentMacros 为假）时整个关掉。
    /// </summary>
    private static Var s_macroCheck;

    private static Var MacroCheckVar()
    {
        if (s_macroCheck != null) return s_macroCheck;
        var ns = Namespace.find(SpecNs);
        if (ns?.FindInternedVar(MacroCheckSym) is Var v && v.isBound) s_macroCheck = v;
        return s_macroCheck;
    }

    private static readonly Symbol SpecNs = Symbol.intern("clojure.spec.alpha"), MacroCheckSym = Symbol.intern("macroexpand-check");

    private static void CheckMacroSpec(Var v, object form, Pos pos)
    {
        if (!RT.instrumentMacros || MacroCheckVar() is not Var check) return;
        try { check.invoke(v, RT.next(form)); }
        catch (Exception ex)
        {
            throw ExpanderEx(pos, Phase.MacroSyntaxCheck, ex.Message, ex, Str(v.Namespace.Name) + "/" + v.Symbol.Name);
        }
    }

    /// <summary>call-macro：宏 Var v 展开形式 form；异常按 Clojure 1.12 的 macroexpand1 分阶段。</summary>
    /// <para><paramref name="rawArity"/>：直接调 macroexpand-1 / macroexpand（Compiler.macroexpand1）时为真，元数错误按 JVM 原样抛出
    /// <see cref="ArityException"/>（宏自己的元数去掉 &amp;form / &amp;env 两个隐含参数，cleat-189）；分析途中仍包成编译错误。</para>
    private static object CallMacro(Var v, object form, Pos pos, Ctx ctx, bool rawArity = false)
    {
        CheckMacroSpec(v, form, pos);
        try
        {
            return v.applyTo(RT.cons(form, RT.cons(LocalsEnv(ctx), RT.next(form))));
        }
        catch (ExpanderException)
        {
            throw;
        }
        catch (Exception ex0)
        {
            var ex = MacroArityFix(v, ex0);
            bool arity = !ReferenceEquals(ex, ex0);
            if (rawArity && ex is ArityException)
            {
                if (arity) throw ex;
                throw;
            }
            object op = RT.first(form);
            string sym = !arity && op is Symbol ? Str(op) : null;
            Phase phase = arity ? Phase.CompileSyntaxCheck
                : ex is ArgumentException or InvalidOperationException or IExceptionInfo || ex.GetType() == typeof(Exception) ? Phase.MacroSyntaxCheck
                : Phase.Macroexpand;
            throw ExpanderEx(pos, phase, ex.Message, ex, sym);
        }
    }

    /// <summary>macro-var-head：以宏 Var 对象开头的形式返回该 Var（别的命名空间的私有宏报错），否则 null。</summary>
    private static Var MacroVarHead(object form, Ctx ctx)
    {
        if (form is not ISeq) return null;
        if (RT.first(form) is not Var op || !op.IsMacro) return null;
        if (!ReferenceEquals(op.Namespace, CurrentNs) && !op.isPublic) throw Err(form, ctx, "Var: " + op + " is not public");
        return op;
    }

    /// <summary>
    /// macroexpand-1（namespaces.sls macroexpand-1-in-ns）：宏调用（含以宏 Var 对象开头的形式）与宿主语法糖展开一层；
    /// 特殊形式与其余形式原样返回。
    /// </summary>
    public static object Macroexpand1(object form) => Macroexpand1(form, false);

    /// <summary><paramref name="rawArity"/> 见 <see cref="CallMacro"/>：macroexpand-1 钩子（用户直接调用）为真，编译驱动为假。</summary>
    public static object Macroexpand1(object form, bool rawArity)
    {
        var ctx = Ctx.Root;
        if (MacroVarHead(form, ctx) is { } mv) return CallMacro(mv, form, SourceOf(form), ctx, rawArity);
        if (IsList(form) && RT.seq(form) != null && RT.first(form) is Symbol op && SpecialHead(op) == null)
        {
            var (o, _, _) = ResolveIn(CurrentNs, op);
            if (o is Var v && v.IsMacro) return CallMacro(v, form, SourceOf(form), ctx, rawArity);
            if (o != null) return form;
            if ((op.Namespace != null && StaticMemberOf(ctx, op) != null) || IsHostSugar(op)) return RewriteToDot(ctx, form, form) ?? form;
            return form;
        }
        return form;
    }

    /// <summary>driver.clj macroexpand-full：macroexpand-1 到不动点（按身份比较）。</summary>
    public static object MacroexpandFull(object form)
    {
        var f = form;
        while (true)
        {
            var g = Macroexpand1(f);
            if (ReferenceEquals(g, f)) return f;
            f = g;
        }
    }
}
