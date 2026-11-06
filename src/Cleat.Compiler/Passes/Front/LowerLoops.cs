using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Cleat.Compiler.Ir;
using Symbol = clojure.lang.Symbol;

namespace Cleat.Compiler.Passes.Front;

/// <summary>
/// lower-loops：L0 → L0（loops.clj，IronScheme iron-034b）。
/// 单绑定 letrec 的 lambda、全部引用都是参数个数相符的尾调用 ⇒ <c>(label f (x …) body fbody)</c>，调用 ⇒ <c>(jump f e …)</c>；
/// let 绑定（含立即调用的 lambda）、只在尾位置调用的单子句 lambda 也变成 label；<c>((letrec ((f le)) f) e …)</c> 先改写成
/// <c>(letrec ((f le)) (f e …))</c>。
/// </summary>
public sealed class LowerLoops : Rewriter, IPass
{
    private readonly FrontState _state;

    public LowerLoops(FrontState state) { _state = state; }

    public string Name => "LowerLoops";
    public Langs Input => Langs.L0;
    public Langs Output => Langs.L0;
    public Node Run(Node ir) => Rewrite((Expr)ir);

    // —— 工具（loops.clj）——

    internal static ImmutableArray<Expr> Kids(Expr e) => e switch
    {
        Ref or Quote or CljVarRef or Uninitialized => ImmutableArray<Expr>.Empty,
        If i => ImmutableArray.Create(i.Test, i.Then, i.Else),
        Begin b => b.Body,
        Set s => ImmutableArray.Create(s.Value),
        Lambda l => l.Clauses.Select(c => c.Body).ToImmutableArray(),
        Letrec lr => lr.Inits.Add(lr.Body),
        Let lt => lt.Inits.Add(lt.Body),
        Call c => c.Args.Insert(0, c.Fn),
        Throw t => ImmutableArray.Create(t.Value),
        TryFinally tf => ImmutableArray.Create(tf.Body, tf.Finally),
        TryCatch tc => tc.Handlers.Insert(0, tc.Body),
        Label lb => ImmutableArray.Create(lb.Body0, lb.Body1),
        Jump j => j.Args,
        CheckInitialized ci => ImmutableArray.Create(ci.Value),
        CljVarSet vs => ImmutableArray.Create(vs.Value),
        CljDef d => ImmutableArray.Create(d.Expr0, d.Expr1),
        CljInvoke ci => ci.Args.Insert(0, ci.Fn),
        CljKeywordInvoke k => k.Args.Insert(0, k.Target),
        CljCall cc => cc.Args,
        CljStaticCall sc => sc.Args,
        CljCase cs => cs.Rest.InsertRange(0, new[] { cs.Expr0, cs.Expr1 }),
        CljDeftype dt => dt.Methods.CastArray<Expr>(),
        Box bx => ImmutableArray.Create(bx.Value),
        Unbox => ImmutableArray<Expr>.Empty,
        SetBox sb => ImmutableArray.Create(sb.Value),
        _ => throw new System.InvalidOperationException($"LowerLoops: unexpected {e.Kind}"),
    };

    private static bool IsRefTo(Expr e, Symbol v) => e is Ref r && r.Name.Equals(v);

    /// <summary>单子句、无剩余参数的 lambda：形参表与体；否则 false。</summary>
    internal static bool SimpleLambda(Expr e, out ImmutableArray<Symbol> ps, out Expr body)
    {
        if (e is Lambda { Clauses.Length: 1 } l && l.Clauses[0].Rest is null)
        {
            ps = l.Clauses[0].Params;
            body = l.Clauses[0].Body;
            return true;
        }
        ps = default;
        body = null;
        return false;
    }

    /// <summary>v 在 e 中的引用是否都是参数个数为 n 的尾调用：null 表示合格，否则是原因（value / arity / non-tail / closure / assigned）。</summary>
    internal static string CheckRefs(Symbol v, int n, Expr e, bool tail, bool inLambda)
    {
        string R(Expr x, bool t) => CheckRefs(v, n, x, t, inLambda);
        string Rs(IEnumerable<Expr> xs)
        {
            foreach (var x in xs)
                if (R(x, false) is { } why) return why;
            return null;
        }
        switch (e)
        {
            case Ref r: return r.Name.Equals(v) ? (inLambda ? "closure" : "value") : null;
            case Set s: return s.Name.Equals(v) ? "assigned" : R(s.Value, false);
            case Call c:
                if (IsRefTo(c.Fn, v))
                {
                    if (inLambda) return "closure";
                    if (c.Args.Length != n) return "arity";
                    if (!tail) return "non-tail";
                    return Rs(c.Args);
                }
                // 立即调用的 lambda：体在尾位置
                if (SimpleLambda(c.Fn, out var ps, out var body) && ps.Length == c.Args.Length)
                    return Rs(c.Args) ?? R(body, tail);
                return R(c.Fn, false) ?? Rs(c.Args);
            case If i: return R(i.Test, false) ?? R(i.Then, tail) ?? R(i.Else, tail);
            case Begin b: return Rs(b.Body.Take(b.Body.Length - 1)) ?? R(b.Body[^1], tail);
            case CljCase cs:
                {
                    var why = R(cs.Expr0, false) ?? R(cs.Expr1, tail);
                    if (why != null) return why;
                    foreach (var x in cs.Rest)
                        if (R(x, tail) is { } w) return w;
                    return null;
                }
            case Let lt: return Rs(lt.Inits) ?? R(lt.Body, tail);
            case Letrec lr: return Rs(lr.Inits) ?? R(lr.Body, tail);
            case Lambda l:
                foreach (var cl in l.Clauses)
                    if (CheckRefs(v, n, cl.Body, false, true) is { } w) return w;
                return null;
            default: return Rs(Kids(e));
        }
    }

    private static T At<T>(T n, Node src) where T : Node
    {
        if (!n.Pos.IsKnown) n.Pos = src.Pos;
        return n;
    }

    /// <summary>把 node 的尾位置上对 v 的调用改成 jump（输入是未处理的 L0）。</summary>
    internal static Expr Jumpify(Symbol v, Expr node)
    {
        switch (node)
        {
            case Call c:
                if (IsRefTo(c.Fn, v)) return At(new Jump(v, c.Args), c);
                if (SimpleLambda(c.Fn, out var ps, out var body) && ps.Length == c.Args.Length)
                    return At(new Call(At(new Lambda(false, default, ImmutableArray.Create(At(new Clause(ps, null, Jumpify(v, body)), c))), c), c.Args), c);
                return node;
            case If i: return At(new If(i.Test, Jumpify(v, i.Then), Jumpify(v, i.Else)), i);
            case Begin b: return At(new Begin(b.Body.SetItem(b.Body.Length - 1, Jumpify(v, b.Body[^1]))), b);
            case CljCase cs: return At(new CljCase(cs.Datum, cs.Expr0, Jumpify(v, cs.Expr1), cs.Rest.Select(x => Jumpify(v, x)).ToImmutableArray()), cs);
            case Let lt: return At(new Let(lt.Vars, lt.Inits, Jumpify(v, lt.Body)), lt);
            case Letrec lr: return At(new Letrec(lr.Vars, lr.Inits, Jumpify(v, lr.Body)), lr);
            default: return node;
        }
    }

    // —— 处理器 ——

    /// <summary>let 绑定的单子句 lambda，体里只在尾位置按相符的参数个数调用：变成 label。</summary>
    private Expr LowerLet(ImmutableArray<Symbol> xs, ImmutableArray<Expr> es, Expr body, Node src)
    {
        var keepX = new List<Symbol>();
        var keepE = new List<Expr>();
        var labels = new List<(Symbol F, ImmutableArray<Symbol> Ps, Expr Body)>();   // 绑定的逆序（loops.clj 用 cons）
        for (int i = 0; i < xs.Length; i++)
        {
            if (SimpleLambda(es[i], out var ps, out var fbody) && CheckRefs(xs[i], ps.Length, body, true, false) is null)
                labels.Insert(0, (xs[i], ps, fbody));
            else
            {
                keepX.Add(xs[i]);
                keepE.Add(es[i]);
            }
        }
        if (labels.Count == 0)
            return At(new Let(xs, RewriteList(es), Rewrite(body)), src);
        var jbody = body;
        foreach (var l in labels) jbody = Jumpify(l.F, jbody);
        var inner = Rewrite(jbody);
        foreach (var l in labels)
        {
            _state.LoopsMade++;
            inner = At(new Label(l.F, l.Ps, inner, Rewrite(l.Body)), src);
        }
        return keepX.Count == 0 ? inner : At(new Let(keepX.ToImmutableArray(), RewriteList(keepE.ToImmutableArray()), inner), src);
    }

    private static bool NamedLetrec(Expr e, out Symbol f, out Expr le)
    {
        if (e is Letrec { Vars.Length: 1 } lr && IsRefTo(lr.Body, lr.Vars[0]))
        {
            f = lr.Vars[0];
            le = lr.Inits[0];
            return true;
        }
        f = null;
        le = null;
        return false;
    }

    public override Node VisitCall(Call n)
    {
        if (NamedLetrec(n.Fn, out var f, out var le))
            return Rewrite<Expr>(At(new Letrec(ImmutableArray.Create(f), ImmutableArray.Create(le), At(new Call(new Ref(f), n.Args), n)), n));
        if (SimpleLambda(n.Fn, out var ps, out var body) && ps.Length == n.Args.Length)
            return LowerLet(ps, n.Args, body, n);           // 立即调用的 lambda 就是 let
        return n.Update(Rewrite(n.Fn), RewriteList(n.Args));
    }

    public override Node VisitLet(Let n) => LowerLet(n.Vars, n.Inits, n.Body, n);

    public override Node VisitLetrec(Letrec n)
    {
        if (n.Vars.Length == 1 && SimpleLambda(n.Inits[0], out var ps, out var fbody))
        {
            var f = n.Vars[0];
            if (CheckRefs(f, ps.Length, n.Body, true, false) is null && CheckRefs(f, ps.Length, fbody, true, false) is null)
            {
                _state.LoopsMade++;
                var b0 = Rewrite(Jumpify(f, n.Body));
                var b1 = Rewrite(Jumpify(f, fbody));
                return At(new Label(f, ps, b0, b1), n);
            }
        }
        return n.Update(n.Vars, RewriteList(n.Inits), Rewrite(n.Body));
    }
}
