using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Cleat.Compiler.Ir;
using Cleat.Core.Compiler;
using Cleat.Core.Runtime;
using clojure.lang;
using static Cleat.Compiler.Analyzer.Clj;
using Symbol = clojure.lang.Symbol;

namespace Cleat.Compiler.Analyzer;

// Var 调用点（analyzer.clj var-call 及其优化，cleat-178）：:inline 展开、instance?、^:escape 传播、.inl 内联（cleat-042 / 090 / 116 / 120 / 123）、
// 原始签名 PrimInvoke（cleat-042）、直接链接 clj-static-call（cleat-033）、KnownCall（cleat-110）、访问器内联的包构建准备（cleat-145，默认关）。
public static partial class FormAnalyzer
{
    /// <summary>
    /// opt-accinline?（cleat-145）：小访问器跨编译单元内联，**默认关**；CLEAT_OPT_ACCINLINE=1 打开，CLEAT_NO_OPT=accinline 仍能单独关掉。
    /// </summary>
    internal static readonly bool OptAccinline =
        Environment.GetEnvironmentVariable("CLEAT_OPT_ACCINLINE") == "1" && !OptFlags.Off("accinline");

    /// <summary>opt-known-calls?（cleat-110）</summary>
    private static readonly bool s_optKnownCalls = !OptFlags.Off("known");

    /// <summary>opt-loopinl?（cleat-120）：循环体里不内联"展开后还调用用户函数 / 宿主方法"的小函数。</summary>
    private static readonly bool s_optLoopinl = !OptFlags.Off("loopinl");

    private static Expr AnalyzeCallArgAt(object x, Ctx nt, Pos pos) => Analyze(x, nt.At(pos));

    /// <summary>escape-local：直接的 escape 续延局部（cleat-125）。</summary>
    private static Local EscapeLocal(Ctx ctx, object x)
    {
        if (x is Symbol s && s.Namespace == null && ctx.FindLocal(s) is { Kind: LocalKind.Escape } l) return l;
        return null;
    }

    /// <summary>analyze-call-arg：escape 续延作为普通实参时包回异常回退。</summary>
    private static Expr AnalyzeCallArg(Ctx ctx, object x)
    {
        if (EscapeLocal(ctx, x) is { } l) return CljCallOf("EscapeFallbackOf", s_false, R(l.Name));
        return Analyze(x, ctx.NonTail().At(Position(x, ctx)));
    }

    /// <summary>escape-param-positions：fn* 形参上的 ^:escape（cleat-125）→ 参数位置的向量（只看单子句的 fn*）。</summary>
    private static IPersistentVector EscapeParamPositions(object init)
    {
        if (!(IsList(init) && RT.seq(init) != null && SymIs(RT.first(init), "fn*"))) return null;
        var restItems = RT.next(init);
        bool single = RT.first(restItems) is IPersistentVector;
        var clauses = single ? new List<object> { restItems } : Items(restItems);
        if (clauses.Count != 1) return null;
        var parms = RT.first(clauses[0]);
        if (parms is not IPersistentVector pv) return null;
        var r = new List<object>();
        for (int i = 0; i < pv.count(); i++)
            if (Truthy(Get(Meta(pv.nth(i)), EscapeKw))) r.Add((long)i);
        return Vec(r);
    }

    /// <summary>prim-kind：^long / ^double 的签名字母。</summary>
    private static string PrimKind(object t)
    {
        if (t is not Symbol s || s.Namespace != null) return null;
        return s.Name switch { "long" => "L", "double" => "D", _ => null };
    }

    /// <summary>
    /// prim-sig（cleat-042）：形参与返回的 ^long / ^double 各一个字母，其余 O；至少一个原始类型、不超过 4 个形参、
    /// clojure.lang.primifs 里有这个接口时返回签名，否则 null。
    /// </summary>
    private static string PrimSig(object vec, IReadOnlyList<object> parms, object rettag)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var p in parms) sb.Append(PrimKind(TagOf(p)) ?? "O");
        sb.Append(PrimKind(TagOf(vec)) ?? PrimKind(rettag) ?? "O");
        string sig = sb.ToString();
        if (parms.Count <= 4 && sig.Any(c => c != 'O') && ClassForName("clojure.lang.primifs." + sig) != null) return sig;
        return null;
    }

    /// <summary>var-rettag：Var 元数据的返回类型提示；defn 求值过的 :tag（函数 long / double 本身）认回符号（cleat-096）。</summary>
    private static object VarRettag(object vm)
    {
        var t = Get(vm, TagKw);
        if (t is Symbol) return t;
        if (t != null && ReferenceEquals(t, RT.var("clojure.core", "long").deref())) return Symbol.intern(null, "long");
        if (t != null && ReferenceEquals(t, RT.var("clojure.core", "double").deref())) return Symbol.intern(null, "double");
        return null;
    }

    /// <summary>var-prim-sig：:arglists 里实参个数为 n、没有 &amp; 的那个参数向量的原始签名。</summary>
    private static string VarPrimSig(object vm, int n)
    {
        var al = Get(vm, ArglistsKw);
        if (RT.seq(al) is not ISeq s) return null;
        for (; s != null; s = s.next())
        {
            if (s.first() is IPersistentVector v && v.count() == n && !Items(v).Any(x => SymIs(x, "&")))
            {
                var sig = PrimSig(v, Items(v), VarRettag(vm));
                if (sig != null) return sig;
            }
        }
        return null;
    }

    /// <summary>
    /// known-call-class（cleat-110）：非动态的 Var、当前值是 AFunction 子类且有 n 个实参的公开静态 invokeStatic、类型能按名字引用时返回该类。
    /// </summary>
    private static Type KnownCallClass(Var v, int n)
    {
        if (!(s_optKnownCalls && !v.isDynamic() && v.hasRoot())) return null;
        if (v.getRawRoot() is not AFunction r) return null;
        var t = r.GetType();
        var m = t.GetMethod("invokeStatic", Enumerable.Repeat(typeof(object), n + 1).ToArray());
        return m != null && m.IsStatic && t.IsPublic && !t.IsNested && !t.IsGenericType ? t : null;
    }

    /// <summary>direct-link-target：AotPaths.DirectLinkTarget 的类名（"类, 程序集"）或 null。</summary>
    private static string DirectLinkTarget(Var v, int n) => AotPaths.DirectLinkTarget(v, n) as string;

    // —— .inl 内联（cleat-042 第 4 项，原版 syntax.sls inline-target / inline-call；.inl 是 L0 子句，cleat-090）——

    private static readonly Dictionary<(InterfaceFile.Entry, int), (Type Type, object Clause)?> s_inlineCache = new();

    /// <summary>find-type-named：在已加载程序集里按 "类型名[, 程序集名]" 找类型。</summary>
    private static Type FindTypeNamed(string cls)
    {
        int comma = cls.IndexOf(',');
        string tn = comma < 0 ? cls : cls.Substring(0, comma).Trim();
        string an = comma < 0 ? null : cls.Substring(comma + 1).Trim();
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (an == null || an == a.GetName().Name)
            {
                var t = a.GetType(tn, false);
                if (t != null) return t;
            }
        }
        return null;
    }

    /// <summary>inline-target：Var 有参数个数为 n 的可内联子句时返回 (函数类, 子句的 S 表达式)。</summary>
    private static (Type Type, object Clause)? InlineTarget(Var v, int n)
    {
        // 包构建期（驱动状态里绑着 :package）依赖包的入口不执行，按命名空间载入依赖程序集的接口文件（只在包构建期做，见 analyzer.clj）
        if (OptAccinline && DriverOps.State.Value is IPersistentMap st && st.containsKey(PackageKw))
            PackageIndex.PrepareNamespaceInterface(Str(v.Namespace.Name));
        var e = InterfaceFile.FindKnown(Symbol.intern(Str(v.Namespace.Name), Str(v.Symbol)));
        var bytes = e != null ? InterfaceFile.FindInline(e, n) : null;
        if (bytes == null) return null;
        lock (s_inlineCache)
        {
            if (s_inlineCache.TryGetValue((e, n), out var hit)) return hit;
        }
        (Type, object)? r = null;
        try
        {
            var data = Fasl.Read(bytes);
            // 别的格式的 .inl 不认
            if (data is IPersistentVector dv && dv.count() > 0 && Equals(CleatL0Kw, dv.nth(0)))
            {
                var t = FindTypeNamed(e.ClassName);
                if (t != null) r = (t, RT.second(dv));
            }
        }
        catch (Exception) { r = null; }
        lock (s_inlineCache) s_inlineCache[(e, n)] = r;
        return r;
    }

    /// <summary>inl-name：g$名字$N$c → g$名字$M$后缀。</summary>
    private static Symbol InlName(string old, string suffix)
    {
        var parts = old.Split('$').ToList();
        while (parts.Count > 0 && parts[^1].Length == 0) parts.RemoveAt(parts.Count - 1);
        string nm = parts.Count > 1 ? parts[1] : "x";
        return Symbol.intern(null, "g$" + nm + "$" + NextCounter() + "$" + suffix);
    }

    /// <summary>
    /// alpha-rename-clause：子句 S 表达式里的局部变量（g$…）逐一改名，子句的形参带 $inl 后缀；quote 里的不动；列表的源位置 metadata 保留（cleat-158）。
    /// </summary>
    private static object AlphaRenameClause(object cl)
    {
        var env = new Dictionary<Symbol, Symbol>();
        var parms = new HashSet<object>(Items(RT.second(cl)));
        if (RT.nth(cl, 2) is Symbol rs) parms.Add(rs);
        bool Temp(object x) => x is Symbol s && s.Namespace == null && s.Name.StartsWith("g$", StringComparison.Ordinal);
        Symbol Rn(Symbol x)
        {
            if (env.TryGetValue(x, out var y)) return y;
            var n = InlName(x.Name, parms.Contains(x) ? "inl" : "c");
            env[x] = n;
            return n;
        }
        object Walk(object x)
        {
            if (Temp(x)) return Rn((Symbol)x);
            if (x is ISeq && Equals(QuoteSym, RT.first(x))) return x;
            if (x is ISeq)
            {
                var items = Items(x).Select(Walk).ToList();
                return WithMeta(PersistentList.create(items), Meta(x));
            }
            return x;
        }
        return Walk(cl);
    }

    /// <summary>
    /// inline-call：((lambda (r t …) (if (IsInstanceOf 函数类 r) ((lambda 子句) t …) (r t …))) Var读 实参 …)。
    /// 守卫保证 with-redefs / 重定义照常生效；有原始签名的函数守卫不成立时走 PrimInvoke（cleat-123）。
    /// </summary>
    private static Expr InlineCall((Type Type, object Clause) target, Var v, List<Expr> args, string primSig)
    {
        var lam = IrReader.ReadExpr(RT.list(LambdaSym, false, AlphaRenameClause(target.Clause)), Langs.L0);
        var r = FreshName("r");
        var ts = args.Select(_ => InlName("g$t$0$c", "inl")).ToList();
        Expr slow = primSig != null
            ? CljCallOf("PrimInvoke", s_false, new Expr[] { Q(primSig), R(r) }.Concat(ts.Select(R)))
            : InvokeNode(R(r), ts.Select(R));
        var body = new If(CljCallOf("IsInstanceOf", s_false, Q(target.Type), R(r)),
                          new Call(lam, ImmutableArray.CreateRange(ts.Select(R))),
                          slow);
        return new Call(MkLambda(s_false, MkClause(new[] { r }.Concat(ts), body)),
                        ImmutableArray.CreateRange(new[] { VarReference(v) }.Concat(args)));
    }

    private static readonly HashSet<Symbol> s_callHeads = new HashSet<Symbol>
    {
        Symbol.intern(null, "clj-invoke"), Symbol.intern(null, "clj-static-call"), Symbol.intern(null, "clj-keyword-invoke"),
    };

    private static readonly Symbol CljCallSym = Symbol.intern(null, "clj-call");

    /// <summary>
    /// clause-has-calls?：子句 S 表达式里有对别的函数 / 宿主方法的真实调用（clj-invoke / clj-static-call / clj-keyword-invoke / KnownCall / PrimInvoke）；
    /// quote 里的不看。
    /// </summary>
    private static bool ClauseHasCalls(object x)
    {
        if (x is not ISeq) return false;
        var h = RT.first(x);
        if (Equals(QuoteSym, h)) return false;
        if (h is Symbol hs && s_callHeads.Contains(hs)) return true;
        if (Equals(CljCallSym, h) && RT.second(x) is string op && (op == "KnownCall" || op == "PrimInvoke")) return true;
        for (var s = RT.seq(x); s != null; s = s.next())
            if (ClauseHasCalls(s.first())) return true;
        return false;
    }

    private static readonly Symbol ClojureCoreSym = Symbol.intern(null, "clojure.core");

    /// <summary>var-call（syntax.sls var-call，namespaces.sls default-var-call）：Var 调用点的全部决定。</summary>
    private static Expr VarCall(object form, Var v, Ctx ctx)
    {
        var args = Items(RT.next(form));
        int n = args.Count;
        var vm = v.meta();
        var inline = Get(vm, InlineKw);
        var arities = Get(vm, InlineAritiesKw);
        var nt = ctx.NonTail();
        var pos = Position(form, ctx);
        if (Truthy(inline) && inline is IFn ifn && (!Truthy(arities) || Truthy(((IFn)arities).invoke((long)n))))
        {
            if (!ReferenceEquals(v.Namespace, CurrentNs) && !v.isPublic) throw Err(form, ctx, "var: " + v + " is not public");
            object expansion = null;
            bool expanded = true;
            if (DeclareCoreForwardRefs)
            {
                // 从零编镜像（cleat-181）：inliner 自己可能用到还没定义的 Var（core.clj 早期的 :inline 里有语法引用，要 concat）——
                // 这时不内联、按普通调用编；第二遍（在这一遍编出的镜像上）就和有起步镜像时完全一样
                try { expansion = ifn.applyTo(RT.next(form)); }
                catch (Exception) { expanded = false; }
            }
            else expansion = ifn.applyTo(RT.next(form));
            if (expanded) return Analyze(TransferSource(expansion, form), ctx.At(pos));
        }
        if (n == 2 && Str(v) == "#'clojure.core/instance?" && MaybeType(ctx, args[0]) != null)
            return CljCallOf("IsInstanceOf", s_false, Q(MaybeType(ctx, args[0])), Analyze(args[1], nt.At(pos)));
        // cleat-125：目标 Var 声明了 ^:escape 形参，而这一位置确实传直接续延局部时才走 marker 返回协议
        if (Get(vm, EscapeParamsKw) is IPersistentVector ep && n == ep.count()
            && Enumerable.Range(0, n).Any(i => Truthy(ep.nth(i)) && EscapeLocal(ctx, i < args.Count ? args[i] : null) != null))
        {
            var epSet = PersistentHashSet.create(Items(ep).ToArray());
            var analyzed = new List<Expr>();
            for (int i = 0; i < args.Count; i++)
            {
                if (epSet.contains((long)i) && EscapeLocal(ctx, args[i]) != null) analyzed.Add(Analyze(args[i], nt));   // 直接传 owner
                else analyzed.Add(AnalyzeCallArg(ctx, args[i]));
            }
            return CljCallOf("EscapePropagate", s_false, InvokeNode(VarReference(v), analyzed));
        }
        // 只在 fn 体里内联；实参是 NaN 字面量时不内联（cleat-116）；cleat-120：循环体里、非 core、子句还有真实调用时不内联
        if (InlineCalls && ctx.FnName != null && !args.Any(a => a is double d && double.IsNaN(d)) && InlineTarget(v, n) is { } it
            && !(s_optLoopinl && RestrictLoopInlines && ctx.InLoop && !Equals(v.Namespace.Name, ClojureCoreSym)
                 && ClauseHasCalls(it.Clause)))
        {
            var target = InlineTarget(v, n).Value;
            var analyzedArgs = AnalyzeAll(args, nt.At(pos));
            return InlineCall(target, v, analyzedArgs, n is >= 1 and <= 4 ? VarPrimSig(vm, n) : null);
        }
        if (n is >= 1 and <= 4 && VarPrimSig(vm, n) is { } sig)
            return CljCallOf("PrimInvoke", s_false, new Expr[] { Q(sig), VarReference(v) }.Concat(AnalyzeAll(args, nt.At(pos))));
        // -g / CLEAT_NO_INLINE：保留物理栈帧，直接链接与 KnownCall 也可能被 JIT 内联掉
        if (InlineCalls && DirectLinkTarget(v, n) is { } cls)
            return new CljStaticCall(cls, (long)n, ImmutableArray.CreateRange(new[] { VarReference(v) }.Concat(AnalyzeAll(args, nt.At(pos)))));
        if (InlineCalls && KnownCallClass(v, n) is { } kc)
            return CljCallOf("KnownCall", s_false, new Expr[] { Q(kc), VarReference(v) }.Concat(AnalyzeAll(args, nt.At(pos))));
        return InvokeNode(VarReference(v), AnalyzeAll(args, nt.At(pos)));
    }
}
