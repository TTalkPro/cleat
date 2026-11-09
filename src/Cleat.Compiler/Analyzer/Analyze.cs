using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Cleat.Compiler.Ir;
using Cleat.Core.Compiler;
using clojure.lang;
using static Cleat.Compiler.Analyzer.Clj;
using Ref = Cleat.Compiler.Ir.Ref;
using Symbol = clojure.lang.Symbol;

namespace Cleat.Compiler.Analyzer;

/// <summary>
/// C# 分析器（cleat-172，designs/30 §2 D5）：Clojure 形式 → L0，逐函数移植 <c>src/clojure/cleat/compiler/analyzer.clj</c>（各成员注明对应的函数）。
/// 宏 = Var 上的 :macro，以 (&amp;form &amp;env 实参…) 调用；决议直接用 clojure.lang.Namespace；产出的 L0 与 Clojure 分析器的 unparse 逐字相同
/// （G1 差分，tools/cs-diff.clj），局部名 g$名字$N$c 的序号是本分析器自己的全局计数。
/// 调用点优化（cleat-178：:inline、instance?、EscapePropagate、.inl 内联、PrimInvoke、直接链接、KnownCall、loopinl、crossnstype、accinline）
/// 与原版一样在分析时决定，开关记号不变。
/// </summary>
public static partial class FormAnalyzer
{
    /// <summary>analyze-top：顶层形式 → L0（在 *ns* 里；上下文为非尾、没有局部）。</summary>
    public static Expr AnalyzeTop(object form) => Analyze(form, Ctx.Root.At(SourceOf(form)));

    // —— 节点的构造 ——

    private static readonly object s_false = false;
    private static readonly object s_fixed0 = 0L, s_fixed1 = 1L, s_fixed3 = 3L, s_fixed4 = 4L;

    private static Quote Q(object d) => new Quote(d);

    private static Expr R(Symbol s) => new Ref(s);

    private static Clause MkClause(IEnumerable<Symbol> ps, Expr body) => new Clause(ImmutableArray.CreateRange(ps), null, body);

    private static Lambda MkLambda(object nm, params Clause[] clauses) => new Lambda(nm, default, ImmutableArray.Create(clauses));

    /// <summary>mk-let1：psyntax 的 let，((lambda (x …) 体) 初值 …)。</summary>
    private static Expr MkLet1(IEnumerable<Symbol> names, IEnumerable<Expr> inits, Expr body) =>
        new Call(MkLambda(s_false, MkClause(names, body)), ImmutableArray.CreateRange(inits));

    /// <summary>mk-seq：build-sequence，一项为它本身，多项为 begin。</summary>
    private static Expr MkSeq(IReadOnlyList<Expr> es) => es.Count == 1 ? es[0] : new Begin(ImmutableArray.CreateRange(es));

    private static CljCall CljCallOf(string method, object fixd, params Expr[] args) => new CljCall(method, fixd, ImmutableArray.Create(args));

    private static CljCall CljCallOf(string method, object fixd, IEnumerable<Expr> args) => new CljCall(method, fixd, ImmutableArray.CreateRange(args));

    private static Expr InvokeNode(Expr f, IEnumerable<Expr> args) => new CljInvoke(f, ImmutableArray.CreateRange(args));

    /// <summary>named-let：((letrec ((lp (lambda 标签名 (y …) 体))) lp) 初值 …)。</summary>
    private static Expr NamedLet(Symbol lp, string lpBase, IEnumerable<Symbol> ys, IEnumerable<Expr> inits, Expr body) =>
        new Call(new Letrec(ImmutableArray.Create(lp), ImmutableArray.Create<Expr>(MkLambda(lpBase, MkClause(ys, body))), R(lp)),
                 ImmutableArray.CreateRange(inits));

    /// <summary>var-reference（namespaces.sls）：:const 为常量，否则 clj-var-ref（动态 Var 为 Var.get）。</summary>
    private static Expr VarReference(Var v)
    {
        if (Truthy(Get(v.meta(), ConstKw))) return Q(v.get());
        return new CljVarRef(v, v.isDynamic());
    }

    private static Expr AnalyzeNonTail(object form, Ctx ctx) => Analyze(form, ctx.Clear().NonTail());

    // —— 主分派 ——

    /// <summary>analyze：Clojure 形式 → L0 的 Expr；给节点记源位置（形式自己的 :line，没有时沿用外层；cleat-158）。</summary>
    private static Expr Analyze(object form, Ctx ctx)
    {
        Expr n;
        if (form is Symbol s) n = AnalyzeSymbol(s, ctx);
        else if (IsList(form) && RT.seq(form) != null) n = AnalyzeSeq(form, ctx);
        else n = AnalyzeDatum(form, ctx);
        // 词法变量在 Clojure 编译器里是符号，不是节点，不记位置
        if (n is not Ref && !n.Pos.IsKnown && Position(form, ctx) is { } p) n.Pos = ToSourcePos(p);
        return n;
    }

    private static SourcePos ToSourcePos(Pos p) => new SourcePos((int)(p.Line ?? 0), (int)(p.Column ?? 0), p.File);

    // —— 字面量与集合（syntax.sls datum-expression / const-value）——

    private static readonly object s_noConst = new object();

    /// <summary>const-value：数据作为常量的值（引号去掉、集合逐项）；含符号或非常量列表时为 s_noConst。</summary>
    private static object ConstValue(object d)
    {
        switch (d)
        {
            case Symbol: return s_noConst;
            case null: return null;
            case IRecord or IType: return d;
        }
        if (IsList(d))
        {
            if (RT.seq(d) == null) return d;
            if (SymIs(RT.first(d), "quote") && RT.count(d) == 2) return Boundary(RT.second(d));
            return s_noConst;
        }
        if (d is IPersistentMap pm)
        {
            if (pm.count() == 0) return d;
            var kvs = new List<object>();
            for (var s = pm.seq(); s != null; s = s.next())
            {
                var e = (IMapEntry)s.first();
                kvs.Add(ConstValue(Boundary(e.key())));
                kvs.Add(ConstValue(Boundary(e.val())));
            }
            if (kvs.Exists(x => ReferenceEquals(x, s_noConst))) return s_noConst;
            var a = kvs.ToArray();
            object m = a.Length <= 16 ? PersistentArrayMap.createAsIfByAssoc(a) : PersistentHashMap.create(a);
            return WithMeta(m, Meta(d));
        }
        if (d is IPersistentVector pv)
        {
            if (pv.count() == 0) return d;
            var vs = new List<object>();
            for (int i = 0; i < pv.count(); i++) vs.Add(ConstValue(pv.nth(i)));
            if (vs.Exists(x => ReferenceEquals(x, s_noConst))) return s_noConst;
            return WithMeta(Vec(vs), Meta(d));
        }
        if (d is IPersistentSet ps)
        {
            if (ps.count() == 0) return d;
            var vs = new List<object>();
            for (var s = ps.seq(); s != null; s = s.next()) vs.Add(ConstValue(Boundary(s.first())));
            if (vs.Exists(x => ReferenceEquals(x, s_noConst))) return s_noConst;
            return WithMeta(PersistentHashSet.create(vs.ToArray()), Meta(d));
        }
        return d;
    }

    /// <summary>coll-kind</summary>
    private static string CollKind(object d) => d switch
    {
        IPersistentVector => "VectorOf",
        IPersistentMap => "MapOf",
        IPersistentSet => "SetOf",
        _ => null,
    };

    /// <summary>coll-items：映射为 k v k v …</summary>
    private static List<object> CollItems(object d)
    {
        if (d is not IPersistentMap m) return Items(d);
        var r = new List<object>();
        for (var s = m.seq(); s != null; s = s.next())
        {
            var e = (IMapEntry)s.first();
            r.Add(e.key());
            r.Add(e.val());
        }
        return r;
    }

    private static object StripMeta(object v) => v is IObj o ? o.withMeta(null) : v;

    /// <summary>analyze-datum：常量；集合按元素分析（cleat-065：元数据不是常量时在字面量的词法上下文里求值再 WithMetaOf）。</summary>
    private static Expr AnalyzeDatum(object d, Ctx ctx)
    {
        if (d == null) return Q(null);
        if (d is Namespace ns) return CljCallOf("FindNamespaceOf", s_false, Q(ns.Name));
        if (CollKind(d) == null && !(IsList(d) && RT.seq(d) == null)) return Q(Boundary(d));
        var v = ConstValue(d);
        var m = LiteralMeta(d);
        var pos = Position(d, ctx);
        var sub = ctx.NonTail().At(pos);
        Expr node;
        if (ReferenceEquals(v, s_noConst))
        {
            var args = new List<Expr>();
            foreach (var x in CollItems(d)) args.Add(Analyze(x, sub));
            node = CljCallOf(CollKind(d), s_fixed0, args);
        }
        else node = Q(StripMeta(v));
        if (m == null) return node;
        var mv = ConstValue(m);
        if (!ReferenceEquals(v, s_noConst) && !ReferenceEquals(mv, s_noConst)) return Q(WithMeta(StripMeta(v), mv));
        if (ReferenceEquals(mv, s_noConst)) return CljCallOf("WithMetaOf", s_false, node, Analyze(m, sub));
        return CljCallOf("WithMetaOf", s_false, node, Q(mv));
    }

    // —— 体（syntax.sls body-stx）——

    /// <summary>analyze-body：空为 (quote nil)，一项为它在 ctx 里的分析，多项为 (begin 非尾… 最后一项)，从左到右立即分析。</summary>
    private static Expr AnalyzeBody(IReadOnlyList<object> forms, Ctx ctx)
    {
        if (forms.Count == 0) return Q(null);
        if (forms.Count == 1) return Analyze(forms[0], ctx);
        var es = new List<Expr>(forms.Count);
        var nt = ctx.NonTail();
        for (int i = 0; i < forms.Count - 1; i++) es.Add(Analyze(forms[i], nt));
        es.Add(Analyze(forms[^1], ctx));
        return MkSeq(es);
    }

    // —— 符号（namespaces.sls ns-lookup，syntax.sls unresolved-symbol）——

    /// <summary>type-reference：桩类型经 RealType 取真正的类型。</summary>
    private static Expr TypeReference(Type t) => HostSyntax.IsStub(t) ? CljCallOf("RealType", s_false, Q(t)) : Q(t);

    /// <summary>analyze-symbol</summary>
    private static Expr AnalyzeSymbol(Symbol sym, Ctx ctx)
    {
        if (sym.Namespace == null && ctx.FindLocal(sym) is { } l)
        {
            // cleat-125：escape 续延局部作为普通值读出时包回旧的异常续延
            return l.Kind == LocalKind.Escape ? CljCallOf("EscapeFallbackOf", s_false, R(l.Name)) : R(l.Name);
        }
        var (o, msg, priv) = ResolveIn(CurrentNs, sym);
        if (o is Var v)
        {
            if (v.IsMacro) throw Err(sym, ctx, "Can't take the value of a macro: " + v);
            return VarReference(v);
        }
        if (o is Type t) return TypeReference(t);
        if (o != null) throw Err(sym, ctx, "Unable to resolve symbol: " + sym + " in this context");
        if (priv) throw Err(sym, ctx, msg);
        if (sym.Namespace != null && HostSyntax.MaybeArrayType(sym) is { } at) return TypeReference(at);
        if (sym.Namespace != null && StaticMemberOf(ctx, sym) is { } sm)
            return CljCallOf("CallStatic", s_fixed3, Q(sm.Type), Q(MungeStr(sm.Member)), Q(null));
        throw Err(sym, ctx, msg);
    }

    // —— 调用（syntax.sls application）——

    private static List<Expr> AnalyzeAll(IEnumerable<object> forms, Ctx ctx)
    {
        var r = new List<Expr>();
        foreach (var f in forms) r.Add(Analyze(f, ctx));
        return r;
    }

    /// <summary>analyze-seq</summary>
    private static Expr AnalyzeSeq(object form, Ctx ctx)
    {
        var pos = Position(form, ctx);
        ctx = ctx.At(pos);
        var op = RT.first(form);
        // 特殊形式自己决定实参的实现时机（宏展开里的惰性部分在第一次遍历时才 gensym，消耗 id 的先后影响 fn 类名）
        if (SpecialHead(op) is { } sp) return AnalyzeSpecial(sp, form, ctx);
        var args = Items(RT.next(form));
        if (op is Symbol sop)
        {
            if (sop.Namespace == null && ctx.FindLocal(sop) is { } l)
            {
                // cleat-125：escape 续延的单参调用返回 marker，立刻离开当前方法
                if (l.Kind == LocalKind.Escape && args.Count == 1)
                    return CljCallOf("EscapePropagate", s_false,
                        CljCallOf("EscapeInvoke", s_false, R(l.Name), Analyze(args[0], ctx.NonTail())));
                return InvokeNode(R(l.Name), AnalyzeAll(args, ctx.NonTail()));
            }
            var (o, msg, priv) = ResolveIn(CurrentNs, sop);
            if (o is Var mv && mv.IsMacro) return Analyze(CallMacro(mv, form, pos, ctx), ctx.At(pos));
            if (o is Var v) return VarCall(form, v, ctx);
            if (o is Type t) return InvokeNode(TypeReference(t), AnalyzeAll(args, ctx.NonTail()));
            if (o != null) throw Err(form, ctx, "Unable to resolve symbol: " + sop + " in this context");
            if (priv) throw Err(sop, ctx, msg);
            if (sop.Namespace != null && HostSyntax.MaybeArrayType(sop) is { } at)
                return InvokeNode(TypeReference(at), AnalyzeAll(args, ctx.NonTail()));
            if ((sop.Namespace != null && StaticMemberOf(ctx, sop) != null) || IsHostSugar(sop))
                return Analyze(RewriteToDot(ctx, form, form) ?? form, ctx);
            throw Err(sop, ctx, msg);
        }
        var nt = ctx.NonTail();
        if (op == null) throw Err(form, ctx, "Can't call nil, form: " + RT.printString(form));
        if (MacroVarHead(form, ctx) is { } hv) return Analyze(CallMacro(hv, form, pos, ctx), ctx);
        if (op is Keyword kw && args.Count is >= 1 and <= 2)
        {
            var asx = AnalyzeAll(args, nt);
            return new CljKeywordInvoke(kw, asx[0], ImmutableArray.CreateRange(asx.GetRange(1, asx.Count - 1)));
        }
        return InvokeNode(Analyze(op, nt), AnalyzeAll(args, nt));
    }

    /// <summary>analyze-special：特殊形式分派。</summary>
    private static Expr AnalyzeSpecial(string nm, object form, Ctx ctx) => nm switch
    {
        "def" => SpDef(form, ctx),
        "if" => SpIf(form, ctx),
        "do" => AnalyzeBody(Items(RT.next(form)), ctx.Clear()),
        "let*" => SpLet(false, form, ctx),
        "loop*" => SpLet(true, form, ctx),
        "recur" => SpRecur(form, ctx),
        "fn*" => ExpandFn(form, false, ctx),
        "quote" => SpQuote(form, ctx),
        "var" => SpVar(form, ctx),
        "throw" => SpThrow(form, ctx),
        "try" => SpTry(form, ctx),
        "monitor-enter" => SpMonitor("monitor-enter", "MonitorEnterOf", form, ctx),
        "monitor-exit" => SpMonitor("monitor-exit", "MonitorExitOf", form, ctx),
        "." => SpDot(form, ctx),
        "new" => SpNew(form, ctx),
        "set!" => SpSet(form, ctx),
        "letfn*" => SpLetfn(form, ctx),
        "case*" => SpCase(form, ctx),
        "deftype*" => SpDeftype(form, ctx),
        "reify*" => SpReify(form, ctx),
        "proxy-class*" => SpProxyClass(form, ctx),
        "gen-class*" => SpGenClass(form, ctx),
        "await*" => SpAwait(form, ctx),
        "escape*" => SpEscape(form, ctx),
        "import*" => SpImport(form, ctx),
        "catch" => throw Err(form, ctx, "catch must be inside try"),
        "finally" => throw Err(form, ctx, "finally must be inside try"),
        "&" => throw Err(form, ctx, "Unable to resolve symbol: & in this context"),
        _ => throw new InvalidOperationException("special form " + nm),
    };
}
