using System.Collections.Generic;
using System.Linq;
using Cleat.Compiler.Ir;
using Symbol = clojure.lang.Symbol;

namespace Cleat.Compiler.Passes.Front;

/// <summary>
/// 前端的分析表（front.clj 的 Analysis）。SimplifyLetrec 会往 <see cref="Assigned"/> 与 <see cref="Forward"/> 里加前向变量，
/// 之后的 SimplifyLets、ConvertAssignments 看到的是加过之后的表（与 Clojure 编译器相同的共享可变表）。
/// </summary>
public sealed class FrontAnalysis
{
    /// <summary>被 set! 的词法变量。</summary>
    public readonly HashSet<Symbol> Assigned = new HashSet<Symbol>();

    /// <summary>出现在某个 lambda 自由变量中的词法变量。</summary>
    public readonly HashSet<Symbol> Captured = new HashSet<Symbol>();

    /// <summary>letrec 右边（L0 节点，按身份）→ 它引用的同组变量（顺序同 front.clj）。</summary>
    internal readonly Dictionary<Expr, SymList> LetrecRefs = new Dictionary<Expr, SymList>(ReferenceEqualityComparer.Instance);

    /// <summary>simplify-letrec 引入的前向变量。</summary>
    public readonly HashSet<Symbol> Forward = new HashSet<Symbol>();

    /// <summary>deftype* 的字段变量（被 set! 也不装箱）。</summary>
    public readonly HashSet<Symbol> Fields = new HashSet<Symbol>();

    /// <summary>分析 L0（front.clj analyze）。</summary>
    public static FrontAnalysis Of(Expr ir)
    {
        var an = new FrontAnalysis();
        an.Free(ir);
        return an;
    }

    private SymList Free(Expr e)
    {
        switch (e)
        {
            case Ref r: return SymList.Of(r.Name);
            case Quote: return SymList.Empty;
            case If i: return SymList.UnionAll(new[] { Free(i.Test), Free(i.Then), Free(i.Else) });
            case Begin b:
                {
                    // (cons e e*)：最后一个先
                    var parts = new List<SymList>(b.Body.Length) { Free(b.Body[^1]) };
                    for (int k = 0; k < b.Body.Length - 1; k++) parts.Add(Free(b.Body[k]));
                    return SymList.UnionAll(parts);
                }
            case Set s:
                Assigned.Add(s.Name);
                return Free(s.Value).Add(s.Name);
            case Lambda l:
                {
                    var fv = SymList.UnionAll(l.Clauses.Select(ClauseFree).ToList());
                    foreach (var x in fv) Captured.Add(x);
                    return fv;
                }
            case Letrec lr:
                {
                    var fvs = lr.Inits.Select(Free).ToList();
                    var vars = new HashSet<Symbol>(lr.Vars);
                    for (int k = 0; k < fvs.Count; k++)
                        LetrecRefs[lr.Inits[k]] = Filter(fvs[k], vars);
                    var parts = new List<SymList> { Free(lr.Body) };
                    parts.AddRange(fvs);
                    return SymList.UnionAll(parts).Minus(lr.Vars);
                }
            case Let lt:
                {
                    var inits = SymList.UnionAll(lt.Inits.Select(Free).ToList());
                    return SymList.Union(inits, Free(lt.Body).Minus(lt.Vars));
                }
            case Label lb: return SymList.Union(Free(lb.Body0), Free(lb.Body1)).Minus(lb.Params.Add(lb.Name));
            case Jump j: return SymList.UnionAll(j.Args.Select(Free).ToList()).Add(j.Target);
            case Uninitialized: return SymList.Empty;
            case CheckInitialized ci: return Free(ci.Value);
            case Call c: return SymList.UnionAll(new[] { Free(c.Fn) }.Concat(c.Args.Select(Free)).ToList());
            case Throw t: return Free(t.Value);
            case TryFinally tf: return SymList.Union(Free(tf.Body), Free(tf.Finally));
            case TryCatch tc: return SymList.UnionAll(new[] { Free(tc.Body) }.Concat(tc.Handlers.Select(Free)).ToList());
            case CljVarRef: return SymList.Empty;
            case CljVarSet vs: return Free(vs.Value);
            case CljDef d: return SymList.Union(Free(d.Expr0), Free(d.Expr1));
            case CljInvoke ci: return SymList.UnionAll(new[] { Free(ci.Fn) }.Concat(ci.Args.Select(Free)).ToList());
            case CljKeywordInvoke k: return SymList.UnionAll(new[] { Free(k.Target) }.Concat(k.Args.Select(Free)).ToList());
            case CljCall cc: return SymList.UnionAll(cc.Args.Select(Free).ToList());
            case CljStaticCall sc: return SymList.UnionAll(sc.Args.Select(Free).ToList());
            case CljCase cs: return SymList.UnionAll(new[] { Free(cs.Expr0), Free(cs.Expr1) }.Concat(cs.Rest.Select(Free)).ToList());
            case CljDeftype dt:
                foreach (var x in dt.Fields) Fields.Add(x);
                return SymList.UnionAll(dt.Methods.Select(m => Free(m)).ToList()).Minus(dt.Fields);
            default:
                throw new System.InvalidOperationException($"FrontAnalysis: unexpected {e?.Kind.ToString() ?? "null"} in L0");
        }
    }

    private SymList ClauseFree(Clause c) =>
        Free(c.Body).Minus(c.Rest is null ? (IEnumerable<Symbol>)c.Params : c.Params.Insert(0, c.Rest));

    private static SymList Filter(SymList fv, HashSet<Symbol> keep) =>
        fv.Minus(fv.Where(x => !keep.Contains(x)).ToList());
}
