using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Cleat.Compiler.Ir;
using Symbol = clojure.lang.Symbol;

namespace Cleat.Compiler.Passes.Front;

/// <summary>前中端 pass 共享的状态：分析表（AnalyzeRefs 填）与本形式生成的 label 个数（LowerLoops 计，179 据此决定是否做类型推导）。</summary>
public sealed class FrontState
{
    public FrontAnalysis Analysis;
    public int LoopsMade;

    /// <summary>front.clj 的 const-in-run（CLEAT_NO_OPT=letrec 关）。</summary>
    public bool ConstInRun = !Cleat.Core.Runtime.OptFlags.Off("letrec");
}

internal static class PosUtil
{
    public static T At<T>(this T n, Node src) where T : Node
    {
        if (src != null && !n.Pos.IsKnown) n.Pos = src.Pos;
        return n;
    }
}

/// <summary>front/analyze：L0 上的分析（被赋值 / 被捕获 / letrec-refs / deftype 字段），结果放进 <see cref="FrontState.Analysis"/>；IR 不变。</summary>
public sealed class AnalyzeRefs : IPass
{
    private readonly FrontState _state;
    public AnalyzeRefs(FrontState state) { _state = state; }
    public string Name => "AnalyzeRefs";
    public Langs Input => Langs.L0;
    public Langs Output => Langs.L0;

    public Node Run(Node ir)
    {
        _state.Analysis = FrontAnalysis.Of((Expr)ir);
        return ir;
    }
}

/// <summary>
/// simplify-letrec（含 recognize-let）：L0 → L1（front.clj）。letrec 只绑定 lambda；绑定 i 的右边引用了在 i 处或之后绑定的变量 v 时，
/// 若从 i 到 v 的绑定都能并入同一组（lambda 且未被赋值，或 const-in-run 的常量），引用合法，否则 v 是前向变量：
/// 先绑定为 uninitialized，到它的位置再 set!。<c>((lambda (x …) body) e …)</c> 改成 let。
/// </summary>
public sealed class SimplifyLetrec : Rewriter, IPass
{
    private readonly FrontState _state;
    private readonly HashSet<Symbol> _fwd = new HashSet<Symbol>();

    public SimplifyLetrec(FrontState state) { _state = state; }
    public string Name => "SimplifyLetrec";
    public Langs Input => Langs.L0;
    public Langs Output => Langs.L1;
    public Node Run(Node ir) => Rewrite((Expr)ir);

    private FrontAnalysis An => _state.Analysis;
    private bool Assigned(Symbol x) => An.Assigned.Contains(x);
    private bool Groupable(Symbol x, Expr e) => e is Lambda && !Assigned(x);
    private bool ConstBinding(Symbol x, Expr e) => _state.ConstInRun && !_fwd.Contains(x) && e is Quote;
    private bool RunMember(Symbol x, Expr e) => Groupable(x, e) || ConstBinding(x, e);

    /// <summary>从 x 到 v（含两端）的绑定都可并入同一组。</summary>
    private bool SameRun(Symbol x, Symbol v, ImmutableArray<Symbol> xs, ImmutableArray<Expr> es)
    {
        bool started = false;
        for (int i = 0; i < xs.Length; i++)
        {
            if (!started && !xs[i].Equals(x)) continue;
            if (!RunMember(xs[i], es[i])) return false;
            if (xs[i].Equals(v)) return true;
            started = true;
        }
        return false;
    }

    private SymList ForwardVars(ImmutableArray<Symbol> xs, ImmutableArray<Expr> es)
    {
        var acc = SymList.Empty;
        for (int i = 0; i < xs.Length; i++)
        {
            var r = An.LetrecRefs.TryGetValue(es[i], out var refs) ? refs : SymList.Empty;
            foreach (var v in r)
            {
                bool later = false;
                for (int k = i; k < xs.Length && !later; k++) later = xs[k].Equals(v);
                if (later && !SameRun(xs[i], v, xs, es)) acc = acc.Add(v);
            }
        }
        return acc;
    }

    public override Node VisitCall(Call n)
    {
        var args = RewriteList(n.Args);
        if (n.Fn is Lambda { Clauses.Length: 1 } l && l.Clauses[0].Rest is null && l.Clauses[0].Params.Length == args.Length)
            return new Let(l.Clauses[0].Params, args, Rewrite(l.Clauses[0].Body)).At(n);
        return n.Update(Rewrite(n.Fn), args);
    }

    public override Node VisitLetrec(Letrec n)
    {
        var body = Rewrite(n.Body);
        // 前向变量会被标为已赋值而不能并组：反复计算到不再变化
        var forward = new List<Symbol>();
        while (true)
        {
            var fresh = ForwardVars(n.Vars, n.Inits).Where(v => !forward.Contains(v)).ToList();
            if (fresh.Count == 0) break;
            foreach (var v in fresh)
            {
                An.Assigned.Add(v);
                _fwd.Add(v);
            }
            forward.AddRange(fresh);
        }
        foreach (var v in forward)
        {
            An.Assigned.Add(v);
            An.Forward.Add(v);
        }
        var xs = n.Vars;
        var es = n.Inits;

        Expr Lp(int i)
        {
            if (i >= xs.Length) return body;
            if (RunMember(xs[i], es[i]))
            {
                // 一组：常量提到前面用 let 绑定，lambda 进 letrec
                var gx = new List<Symbol>();
                var ge = new List<Expr>();
                var cx = new List<Symbol>();
                var ce = new List<Expr>();
                int j = i;
                while (true)
                {
                    if (j < xs.Length && Groupable(xs[j], es[j]))
                    {
                        gx.Add(xs[j]);
                        ge.Add(Rewrite(es[j]));
                        j++;
                    }
                    else if (j < xs.Length && ConstBinding(xs[j], es[j]))
                    {
                        cx.Add(xs[j]);
                        ce.Add(Rewrite(es[j]));
                        j++;
                    }
                    else
                    {
                        Expr rst = gx.Count == 0 ? Lp(j) : new Letrec(gx.ToImmutableArray(), ge.ToImmutableArray(), Lp(j)).At(n);
                        return cx.Count == 0 ? rst : new Let(cx.ToImmutableArray(), ce.ToImmutableArray(), rst).At(n);
                    }
                }
            }
            if (forward.Contains(xs[i]))
                return new Begin(ImmutableArray.Create<Expr>(new Set(xs[i], Rewrite(es[i])).At(n), Lp(i + 1))).At(n);
            return new Let(ImmutableArray.Create(xs[i]), ImmutableArray.Create(Rewrite(es[i])), Lp(i + 1)).At(n);
        }

        var inner = Lp(0);
        if (forward.Count == 0) return inner;
        return new Let(forward.ToImmutableArray(), forward.Select(_ => (Expr)new Uninitialized().At(n)).ToImmutableArray(), inner).At(n);
    }
}

/// <summary>check-forward：L1 → L1。前向变量在赋值之前可能被读到：引用包上 check-initialized。</summary>
public sealed class CheckForward : Rewriter, IPass
{
    private readonly FrontState _state;
    public CheckForward(FrontState state) { _state = state; }
    public string Name => "CheckForward";
    public Langs Input => Langs.L1;
    public Langs Output => Langs.L1;
    public Node Run(Node ir) => _state.Analysis.Forward.Count == 0 ? ir : Rewrite((Expr)ir);

    public override Node VisitRef(Ref n) => _state.Analysis.Forward.Contains(n.Name) ? new CheckInitialized(n).At(n) : n;
}

/// <summary>cp0-lite：L1 → L1。常量测试的 if 折叠；begin 里删掉常量语句。</summary>
public sealed class Cp0Lite : Rewriter, IPass
{
    public Cp0Lite(string name = "Cp0Lite") { Name = name; }
    public string Name { get; }
    public Langs Input => Langs.L1;
    public Langs Output => Langs.L1;
    public Node Run(Node ir) => Rewrite((Expr)ir);

    public override Node VisitIf(If n)
    {
        var e0 = Rewrite(n.Test);
        var e1 = Rewrite(n.Then);
        var e2 = Rewrite(n.Else);
        if (e0 is Quote q) return q.Datum is not null && q.Datum is not false ? e1 : e2;
        return n.Update(e0, e1, e2);
    }

    public override Node VisitBegin(Begin n)
    {
        var body = RewriteList(n.Body);
        var last = body[^1];
        var keep = body.Take(body.Length - 1).Where(e => e is not Quote).ToList();
        if (keep.Count == 0) return last;
        if (keep.Count == body.Length - 1) return n.Update(body);
        keep.Add(last);
        return new Begin(keep.ToImmutableArray()).At(n);
    }
}

/// <summary>
/// simplify-lets：L1 → L1（inline.sls 的 count-refs! / simplify-lets）。let 的常量（Int32 / Double / 布尔 / 字符）与未被赋值的变量传播到引用处；
/// 没被引用、初值无副作用（常量 / 变量 / lambda）的 let 绑定与没被引用的 letrec 绑定删掉。
/// </summary>
public sealed class SimplifyLets : Rewriter, IPass
{
    private readonly FrontState _state;
    private readonly Dictionary<Symbol, int> _counts = new Dictionary<Symbol, int>();
    private readonly Dictionary<Symbol, Expr> _subst = new Dictionary<Symbol, Expr>();

    public SimplifyLets(FrontState state) { _state = state; }
    public string Name => "SimplifyLets";
    public Langs Input => Langs.L1;
    public Langs Output => Langs.L1;

    public Node Run(Node ir)
    {
        new RefCounter(_counts).Walk(ir);
        return Rewrite((Expr)ir);
    }

    private sealed class RefCounter : Walker
    {
        private readonly Dictionary<Symbol, int> _counts;
        public RefCounter(Dictionary<Symbol, int> counts) { _counts = counts; }
        public override void VisitRef(Ref n) => _counts[n.Name] = (_counts.TryGetValue(n.Name, out var c) ? c : 0) + 1;
    }

    private bool Assigned(Symbol x) => _state.Analysis.Assigned.Contains(x);
    private int Refs(Symbol x) => _counts.TryGetValue(x, out var c) ? c : 0;

    /// <summary>值得传播的常量（原版 simple-datum?：Int32、Double、布尔、字符；long 不传播——会改变 identical? 与装箱次数）。</summary>
    private static bool SimpleDatum(object d) => d is int or double or bool or char;

    private bool Propagatable(Symbol x, Expr e) =>
        !Assigned(x) && e switch
        {
            Quote q => SimpleDatum(q.Datum),
            Ref r => !Assigned(r.Name),
            _ => false,
        };

    private static bool DroppableInit(Expr e) => e is Quote or Ref or Lambda;

    public override Node VisitRef(Ref n) => _subst.TryGetValue(n.Name, out var e) ? e : n;

    public override Node VisitLet(Let n)
    {
        var inits = RewriteList(n.Inits);
        var kx = new List<Symbol>();
        var ke = new List<Expr>();
        for (int i = 0; i < n.Vars.Length; i++)
        {
            var x = n.Vars[i];
            var e = inits[i];
            if (Propagatable(x, e)) _subst[x] = e;
            else if (Refs(x) == 0 && !Assigned(x) && DroppableInit(e)) { }
            else
            {
                kx.Add(x);
                ke.Add(e);
            }
        }
        var body = Rewrite(n.Body);
        if (kx.Count == 0) return body;
        return kx.Count == n.Vars.Length ? n.Update(n.Vars, inits, body) : new Let(kx.ToImmutableArray(), ke.ToImmutableArray(), body).At(n);
    }

    public override Node VisitLetrec(Letrec n)
    {
        var kx = new List<Symbol>();
        var kl = new List<Expr>();
        for (int i = 0; i < n.Vars.Length; i++)
        {
            if (Refs(n.Vars[i]) == 0) continue;
            kx.Add(n.Vars[i]);
            kl.Add(n.Inits[i]);
        }
        var body = Rewrite(n.Body);
        if (kx.Count == 0) return body;
        var lambdas = RewriteList(kl.ToImmutableArray());
        return kx.Count == n.Vars.Length ? n.Update(n.Vars, lambdas, body) : new Letrec(kx.ToImmutableArray(), lambdas, body).At(n);
    }
}

/// <summary>
/// convert-assignments：L1 → L2。被赋值又被捕获的词法变量（deftype 字段除外）装箱：引用 → unbox、set! → set-box!、
/// let 初值与 jump 实参包 box、子句进入时把被装箱的形参换成 Box。
/// </summary>
public sealed class ConvertAssignments : Rewriter, IPass
{
    private readonly FrontState _state;
    private readonly Dictionary<Symbol, ImmutableArray<Symbol>> _labelParams = new Dictionary<Symbol, ImmutableArray<Symbol>>();

    public ConvertAssignments(FrontState state) { _state = state; }
    public string Name => "ConvertAssignments";
    public Langs Input => Langs.L1;
    public Langs Output => Langs.L2;
    public Node Run(Node ir) => Rewrite((Expr)ir);

    private bool Boxed(Symbol x)
    {
        var an = _state.Analysis;
        return an.Assigned.Contains(x) && an.Captured.Contains(x) && !an.Fields.Contains(x);
    }

    private Expr BoxIf(Symbol x, Expr e) => Boxed(x) ? new Box(e).At(e) : e;

    public override Node VisitRef(Ref n) => Boxed(n.Name) ? new Unbox(n.Name).At(n) : n;

    public override Node VisitSet(Set n)
    {
        var e = Rewrite(n.Value);
        return Boxed(n.Name) ? new SetBox(n.Name, e).At(n) : n.Update(n.Name, e);
    }

    public override Node VisitLet(Let n)
    {
        var inits = RewriteList(n.Inits);
        var body = Rewrite(n.Body);
        var b = ImmutableArray.CreateBuilder<Expr>(inits.Length);
        for (int i = 0; i < inits.Length; i++) b.Add(BoxIf(n.Vars[i], inits[i]));
        return n.Update(n.Vars, b.MoveToImmutable(), body);
    }

    public override Node VisitLabel(Label n)
    {
        _labelParams[n.Name] = n.Params;
        var b0 = Rewrite(n.Body0);
        var b1 = Rewrite(n.Body1);
        return n.Update(n.Name, n.Params, b0, b1);
    }

    public override Node VisitJump(Jump n)
    {
        var args = RewriteList(n.Args);
        var ps = _labelParams.TryGetValue(n.Target, out var p) ? p : ImmutableArray<Symbol>.Empty;
        int k = System.Math.Min(ps.Length, args.Length);   // map 两个序列：短的那个为准
        var b = ImmutableArray.CreateBuilder<Expr>(k);
        for (int i = 0; i < k; i++) b.Add(BoxIf(ps[i], args[i]));
        return n.Update(n.Target, b.MoveToImmutable());
    }

    public override Node VisitClause(Clause n)
    {
        var body = Rewrite(n.Body);
        var all = n.Rest is null ? n.Params : n.Params.Insert(0, n.Rest);
        var bx = all.Where(Boxed).ToList();
        if (bx.Count == 0) return n.Update(n.Params, n.Rest, body);
        var stmts = bx.Select(v => (Expr)new Set(v, new Box(new Ref(v)).At(n)).At(n)).ToList();
        stmts.Add(body);
        return new Clause(n.Params, n.Rest, new Begin(stmts.ToImmutableArray()).At(n)).At(n);
    }
}

/// <summary>uncover-free：L2 → L3。lambda 带上自由变量表（顺序同 closures.clj：set-add 追加在末尾）。</summary>
public sealed class UncoverFree : Rewriter, IPass
{
    private readonly Dictionary<Lambda, ImmutableArray<Symbol>> _fvt = new Dictionary<Lambda, ImmutableArray<Symbol>>(ReferenceEqualityComparer.Instance);

    public string Name => "UncoverFree";
    public Langs Input => Langs.L2;
    public Langs Output => Langs.L3;

    public Node Run(Node ir)
    {
        Free((Expr)ir);
        return Rewrite((Expr)ir);
    }

    public override Node VisitLambda(Lambda n)
    {
        var clauses = RewriteList(n.Clauses);
        var free = _fvt.TryGetValue(n, out var fv) ? fv : SymVec.Empty;
        return n.Update(n.Name, free, clauses);
    }

    private ImmutableArray<Symbol> All(IEnumerable<Expr> es) => SymVec.UnionAll(es.Select(Free).ToList());

    private ImmutableArray<Symbol> Free(Expr e)
    {
        switch (e)
        {
            case Ref r: return ImmutableArray.Create(r.Name);
            case Quote: return SymVec.Empty;
            case If i: return All(new[] { i.Test, i.Then, i.Else });
            case Begin b: return All(b.Body);
            case Set s: return SymVec.Add(Free(s.Value), s.Name);
            case Lambda l:
                {
                    var fv = SymVec.UnionAll(l.Clauses.Select(c => SymVec.Minus(Free(c.Body), c.Rest is null ? c.Params : c.Params.Insert(0, c.Rest))).ToList());
                    _fvt[l] = fv;
                    return fv;
                }
            case Label lb: return SymVec.Minus(SymVec.Union(Free(lb.Body0), Free(lb.Body1)), lb.Params.Insert(0, lb.Name));
            case Jump j: return SymVec.Add(All(j.Args), j.Target);
            case Uninitialized: return SymVec.Empty;
            case CheckInitialized ci: return Free(ci.Value);
            case Letrec lr:
                {
                    var parts = new List<ImmutableArray<Symbol>> { Free(lr.Body) };
                    parts.AddRange(lr.Inits.Select(Free));
                    return SymVec.Minus(SymVec.UnionAll(parts), lr.Vars);
                }
            case Let lt: return SymVec.Union(All(lt.Inits), SymVec.Minus(Free(lt.Body), lt.Vars));
            case Call c: return All(c.Args.Insert(0, c.Fn));
            case Throw t: return Free(t.Value);
            case TryFinally tf: return SymVec.Union(Free(tf.Body), Free(tf.Finally));
            case TryCatch tc: return All(tc.Handlers.Insert(0, tc.Body));
            case CljVarRef: return SymVec.Empty;
            case CljVarSet vs: return Free(vs.Value);
            case CljDef d: return SymVec.Union(Free(d.Expr0), Free(d.Expr1));
            case CljInvoke ci: return All(ci.Args.Insert(0, ci.Fn));
            case CljKeywordInvoke k: return All(k.Args.Insert(0, k.Target));
            case CljCall cc: return All(cc.Args);
            case CljStaticCall sc: return All(sc.Args);
            case CljCase cs: return All(cs.Rest.InsertRange(0, new[] { cs.Expr0, cs.Expr1 }));
            case CljDeftype dt: return SymVec.Minus(All(dt.Methods.CastArray<Expr>()), dt.Fields);
            case Box bx: return Free(bx.Value);
            case Unbox ub: return ImmutableArray.Create(ub.Name);
            case SetBox sb: return SymVec.Add(Free(sb.Value), sb.Name);
            default: throw new System.InvalidOperationException($"UncoverFree: unexpected {e?.Kind.ToString() ?? "null"} in L2");
        }
    }
}

/// <summary>前中端（pipeline.clj front-end）：p0（分析器的输出）→ L3。</summary>
public static class FrontEnd
{
    /// <summary>按 pipeline.clj 的顺序组装：LowerLoops → AnalyzeRefs → SimplifyLetrec → CheckForward → Cp0Lite → SimplifyLets → Cp0Lite → ConvertAssignments → UncoverFree。</summary>
    public static Pipeline Create(FrontState state) => new Pipeline(
        new LowerLoops(state),
        new AnalyzeRefs(state),
        new SimplifyLetrec(state),
        new CheckForward(state),
        new Cp0Lite("Cp0Lite"),
        new SimplifyLets(state),
        new Cp0Lite("Cp0LiteAfterLets"),
        new ConvertAssignments(state),
        new UncoverFree());

    /// <summary>p0 → L3；state 收集分析表与 label 个数。</summary>
    public static Expr Run(Expr p0, FrontState state, PipelineOptions options = null) => (Expr)Create(state).Run(p0, options);

    /// <summary>
    /// 逐个 pass 运行并回调每个 pass 之后的 IR（差分工具用：阶段名同 Clojure 编译器的 dump——l0 sl cf c1 in l2 l3）。
    /// </summary>
    public static Expr RunWithStages(Expr p0, FrontState state, System.Action<string, Expr> stage)
    {
        var names = new Dictionary<string, string>
        {
            ["LowerLoops"] = "l0", ["SimplifyLetrec"] = "sl", ["CheckForward"] = "cf", ["Cp0Lite"] = "c1",
            ["Cp0LiteAfterLets"] = "in", ["ConvertAssignments"] = "l2", ["UncoverFree"] = "l3",
        };
        Node ir = p0;
        foreach (var p in Create(state).Passes)
        {
            ir = p.Run(ir);
            if (names.TryGetValue(p.Name, out var s)) stage(s, (Expr)ir);
        }
        return (Expr)ir;
    }
}
