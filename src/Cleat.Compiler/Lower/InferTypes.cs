using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Cleat.Compiler.Ir;
using Cleat.Core.Runtime;
using clojure.lang;
using Symbol = clojure.lang.Symbol;
using Ref = Cleat.Compiler.Ir.Ref;
using Box = Cleat.Compiler.Ir.Box;
using Label = Cleat.Compiler.Ir.Label;

namespace Cleat.Compiler.Lower;

/// <summary>类型化入口（cleat-104）：签名（每个形参 L / D / O 加返回字符）、类型化体的形参、类型化体、各形参的推测类型。</summary>
internal sealed class SpecEntry
{
    public SpecEntry(string sig, ImmutableArray<Symbol> ps, Expr body, TypeKind[] guess)
    {
        Sig = sig;
        Params = ps;
        Body = body;
        Guess = guess;
    }

    public string Sig { get; }
    public ImmutableArray<Symbol> Params { get; }
    public Expr Body { get; }
    public TypeKind[] Guess { get; }
}

/// <summary>
/// L4 表示层的按变量的表（designs/30 §3.3：L4 = L3 + 这些表；codegen.clj :438-443 与 spec-entries / tier-classes），每个编译单元重算。
/// 变量名在进程内唯一（分析器的 g$名字$N$c 与推测 pass 的 名字%N），所以按符号做键。
/// </summary>
internal sealed class ReprTables
{
    /// <summary>变量 → 存储的原始类型 :fx / :fl / :l64 / :num（var-types）。</summary>
    public readonly Dictionary<Symbol, TypeKind> VarTypes = new Dictionary<Symbol, TypeKind>();

    /// <summary>推断收敛后的类型（sound-types；宿主调用按它选重载）。</summary>
    public readonly Dictionary<Symbol, TypeKind> SoundTypes = new Dictionary<Symbol, TypeKind>();

    /// <summary>原始签名子句与类型化入口的 L / D 形参、^long / ^double 字段（fixed-types）。</summary>
    public readonly Dictionary<Symbol, TypeKind> FixedTypes = new Dictionary<Symbol, TypeKind>();

    /// <summary>变量的已知类型（var-facts：不改变存储）。</summary>
    public readonly Dictionary<Symbol, TypeKind> VarFacts = new Dictionary<Symbol, TypeKind>();

    /// <summary>只是 Num 形参副本的变量 → 形参（num-aliases，cleat-102）。</summary>
    public readonly Dictionary<Symbol, Symbol> NumAliases = new Dictionary<Symbol, Symbol>();

    /// <summary>被 set! / set-box! 的局部变量（assigned-vars）。</summary>
    public readonly HashSet<Symbol> AssignedVars = new HashSet<Symbol>();

    /// <summary>fn 的类名（名字记录的 :class）→ 类型化入口（spec-entries，cleat-104）。</summary>
    public readonly Dictionary<string, SpecEntry> SpecEntries = new Dictionary<string, SpecEntry>(StringComparer.Ordinal);

    /// <summary>分层编译的函数类（tier-classes，cleat-114 / 117）。</summary>
    public readonly HashSet<string> TierClasses = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>重推断之前清掉的表（generate-unit 的 hclear! 列表，不含 spec-entries / tier-classes）。</summary>
    public void ClearInference()
    {
        VarTypes.Clear();
        SoundTypes.Clear();
        FixedTypes.Clear();
        VarFacts.Clear();
        AssignedVars.Clear();
        NumAliases.Clear();
    }

    public void ClearAll()
    {
        ClearInference();
        SpecEntries.Clear();
        TierClasses.Clear();
    }
}

/// <summary>
/// InferTypes（cleat-179，designs/30 §3.3 / §4 的"表示"组）：codegen.clj 的类型格、expr-type（带变量类型环境）、infer-walk、
/// infer-var-types-into!、collect-assigned!、count-nodes 与 l3-some?。分析 pass：只填 <see cref="ReprTables"/>，不改 IR。
/// </summary>
internal static partial class CodeGen
{
    // —— 测量开关（designs/README 的 CLEAT_NO_OPT 记号表；codegen.clj :72-92）——
    internal static readonly bool OptInfer = !OptFlags.Off("infer");
    internal static readonly bool OptNum = !OptFlags.Off("num");
    internal static readonly bool OptJoinRaw = !OptFlags.Off("joinraw");
    internal static readonly bool OptLoopWeight = !OptFlags.Off("loopweight");

    [ThreadStatic] private static ReprTables s_tabs;

    /// <summary>当前线程的表示表（codegen.clj 的全局 Hashtable；编译单元开始时清空）。</summary>
    internal static ReprTables Tabs => s_tabs ??= new ReprTables();

    internal static TypeKind RawKind(Raw r) => r switch
    {
        Raw.L64 => TypeKind.L64,
        Raw.Fl => TypeKind.Fl,
        Raw.Fx => TypeKind.Fx,
        Raw.Num => TypeKind.Num,
        _ => TypeKind.None,
    };

    /// <summary>可作存储的类型 → 原始表示（:fx :fl :l64 :num），其余 None。</summary>
    internal static Raw KindRaw(TypeKind t) => t switch
    {
        TypeKind.L64 => Raw.L64,
        TypeKind.Fl => Raw.Fl,
        TypeKind.Fx => Raw.Fx,
        TypeKind.Num => Raw.Num,
        _ => Raw.None,
    };

    private static TypeKind KindOfKeyword(Keyword k) => k.Name switch
    {
        "l64" => TypeKind.L64,
        "fl" => TypeKind.Fl,
        "num" => TypeKind.Num,
        "fx" => TypeKind.Fx,
        _ => TypeKind.None,
    };

    internal static readonly Keyword L64Key = Keyword.intern(null, "l64");
    internal static readonly Keyword FlKey = Keyword.intern(null, "fl");

    internal static Keyword KindKeyword(TypeKind t) => t == TypeKind.L64 ? L64Key : FlKey;

    /// <summary>(TypedSelf '[签名 …] f 实参 …) 的签名。</summary>
    internal static string TypedSelfSig(CljCall cc) => (string)RT.first(QuotedDatum(cc.Args[0]));

    // —— 表达式的类型 ——

    /// <summary>
    /// 表达式的类型（:fx :fl :int … 与 :l64）或 None（codegen.clj expr-type）。env：变量 → 类型 | Top，缺省 var-types；
    /// 不在 env 里的变量查 var-facts。
    /// </summary>
    internal static TypeKind ExprType(Expr e, Dictionary<Symbol, TypeKind> env = null)
    {
        env ??= Tabs.VarTypes;
        switch (e)
        {
            case Quote q: return q.Datum is long ? TypeKind.L64 : DatumType(q.Datum);
            case CljCall cc:
                {
                    var op = cc.Op as string;
                    switch (op)
                    {
                        case "Tagged": return ExprType(cc.Args[1], env);   // 静态类型标注（cleat-101 的延续）：透明
                        case "Speculated": return QuotedDatum(cc.Args[0]) is Keyword k ? KindOfKeyword(k) : TypeKind.None;   // 推测循环的快份入口（cleat-103）
                        case "TypedSelf": return RawKind(SigCharRaw(SigRet(TypedSelfSig(cc))));   // 类型化体里的自调用（cleat-104）
                        case "TypedEntry": return ExprType(cc.Args[1], env);
                        case "PrimInvoke": return RawKind(SigCharRaw(SigRet((string)QuotedDatum(cc.Args[0]))));
                    }
                    var nop = NumbersArithOp(cc.Op, cc.Datum, cc.Args);
                    if (nop != null && NumbersNumOps.Contains(nop))
                    {
                        var ts = cc.Args.Skip(3).Select(a => ExprType(a, env)).ToList();
                        if (ts.Contains(TypeKind.Num)) return TypeKind.Num;   // 有 Num 操作数的算术按 Num 算（cleat-102）
                        if (NumbersLongOps.Contains(nop) && ts.All(t => t == TypeKind.L64)) return TypeKind.L64;
                        if (NumbersLongOps.Contains(nop) && ts.All(t => t is TypeKind.L64 or TypeKind.Top)) return TypeKind.Top;
                    }
                    return HostCallType(cc, env);
                }
            case Ref r:
                if (env.TryGetValue(r.Name, out var et)) return et;
                return Tabs.VarFacts.TryGetValue(r.Name, out var ft) ? ft : TypeKind.None;
            case If i: return TypeJoin(ExprType(i.Then, env), ExprType(i.Else, env));
            case Begin b: return ExprType(b.Body[^1], env);
            case Let lt: return ExprType(lt.Body, env);
            // 不回跳的标签（内联子句外面的 arity 标签）的值就是体的值（cleat-123）；真正的循环不推（出口变量在推断早期可能被暂定成别的类型）
            case Label lb: return JumpsTo(lb.Body1, lb.Name) ? TypeKind.None : ExprType(lb.Body1, env);
            case Jump: return TypeKind.Top;
            case Throw: return TypeKind.Top;
            default: return TypeKind.None;
        }
    }

    /// <summary>(CallStatic 'Numbers op nil a b)，op 有 long 原始重载、实参都是 :l64 时：op 与实参（codegen.clj numbers-arith-args）。</summary>
    private static (string Op, ImmutableArray<Expr> Args)? NumbersArithArgs(Expr e, Dictionary<Symbol, TypeKind> env)
    {
        if (e is not CljCall cc) return null;
        var op = NumbersArithOp(cc.Op, cc.Datum, cc.Args);
        // 只有 long 的原始重载存在的运算（divide 两个 long 可能是 Ratio，没有 long → long 的重载；它只走 Num 路径）
        if (op == null || !(NumbersLongOps.Contains(op) || NumbersBoolOps.Contains(op))) return null;
        var args = cc.Args.RemoveRange(0, 3);
        return args.All(a => ExprType(a, env) == TypeKind.L64) ? (op, args) : null;
    }

    // —— 遍历工具 ——

    /// <summary>
    /// e（含嵌套 lambda 的子句体；own 为真时不进嵌套的 lambda）里有满足 pred 的节点（codegen.clj l3-some?）。
    /// 与 codegen.clj 相同：不进 clj-deftype 的方法、clj-var-set! 的值。
    /// </summary>
    internal static bool L3Some(Func<Expr, bool> pred, Expr e, bool own = false)
    {
        var top = e;
        bool Walk(Expr x)
        {
            if (pred(x)) return true;
            switch (x)
            {
                case If i: return Walk(i.Test) || Walk(i.Then) || Walk(i.Else);
                case Begin b: return b.Body.Any(Walk);
                case Let lt: return lt.Inits.Any(Walk) || Walk(lt.Body);
                case Letrec lr: return lr.Inits.Any(Walk) || Walk(lr.Body);
                case Label lb: return Walk(lb.Body0) || Walk(lb.Body1);
                case Jump j: return j.Args.Any(Walk);
                case Lambda l: return !(own && !ReferenceEquals(x, top)) && l.Clauses.Any(c => Walk(c.Body));
                case Call c: return Walk(c.Fn) || c.Args.Any(Walk);
                case Throw t: return Walk(t.Value);
                case TryFinally tf: return Walk(tf.Body) || Walk(tf.Finally);
                case TryCatch tc: return Walk(tc.Body) || tc.Handlers.Any(Walk);
                case CljInvoke ci: return Walk(ci.Fn) || ci.Args.Any(Walk);
                case CljKeywordInvoke k: return Walk(k.Target) || k.Args.Any(Walk);
                case CljCall cc: return cc.Args.Any(Walk);
                case CljStaticCall sc: return sc.Args.Any(Walk);
                case CljCase cs: return Walk(cs.Expr0) || Walk(cs.Expr1) || cs.Rest.Any(Walk);
                case CljDef d: return Walk(d.Expr0) || Walk(d.Expr1);
                case Box bx: return Walk(bx.Value);
                case Set s: return Walk(s.Value);
                case SetBox sb: return Walk(sb.Value);
                case CheckInitialized ci: return Walk(ci.Value);
                default: return false;
            }
        }
        return Walk(e);
    }

    /// <summary>body（不进嵌套 lambda）里有跳到 label 的 jump。</summary>
    internal static bool JumpsTo(Expr body, Symbol label) => L3Some(j => j is Jump jj && jj.Target.Equals(label), body, own: true);

    /// <summary>节点是真正的循环：label 的循环体里有跳回自己的 jump（codegen.clj real-loop?；每个子句都有的 arity 标签，没有 recur 时不算）。</summary>
    internal static bool RealLoop(Expr n) => n is Label lb && L3Some(j => j is Jump jj && jj.Target.Equals(lb.Name), lb.Body1);

    /// <summary>e 的节点数超过 n（codegen.clj count-nodes：不进 lambda 与 letrec 的绑定，clj-call 等叶子形式整个算一个）。</summary>
    internal static bool BiggerThan(Expr body, int n)
    {
        int cnt = 0;
        bool More() => ++cnt <= n;
        void Walk(Expr e)
        {
            switch (e)
            {
                case Lambda: return;
                case If i: if (More()) { Walk(i.Test); Walk(i.Then); Walk(i.Else); } return;
                case Begin b: if (More()) foreach (var x in b.Body) Walk(x); return;
                case Set s: if (More()) Walk(s.Value); return;
                case Letrec lr: if (More()) Walk(lr.Body); return;
                case Let lt: if (More()) { foreach (var x in lt.Inits) Walk(x); Walk(lt.Body); } return;
                case Call c: if (More()) { Walk(c.Fn); foreach (var x in c.Args) Walk(x); } return;
                case Throw t: if (More()) Walk(t.Value); return;
                case TryFinally tf: if (More()) { Walk(tf.Body); Walk(tf.Finally); } return;
                case TryCatch tc: if (More()) { Walk(tc.Body); foreach (var x in tc.Handlers) Walk(x); } return;
                case Label lb: if (More()) { Walk(lb.Body0); Walk(lb.Body1); } return;
                case Jump j: if (More()) foreach (var x in j.Args) Walk(x); return;
                case CheckInitialized ci: if (More()) Walk(ci.Value); return;
                default: More(); return;
            }
        }
        Walk(body);
        return cnt > n;
    }

    internal const int BigBodyNodes = 2000;

    /// <summary>被 set! / set-box! 的变量记入 AssignedVars（codegen.clj collect-assigned!）。</summary>
    internal static void CollectAssigned(Expr e)
    {
        switch (e)
        {
            case Set s: Tabs.AssignedVars.Add(s.Name); break;
            case SetBox sb: Tabs.AssignedVars.Add(sb.Name); break;
        }
        foreach (var k in Passes.Front.LowerLoops.Kids(e)) CollectAssigned(k);
    }

    private static bool InlinedBinding(Symbol x) => x.ToString().EndsWith("$inl", StringComparison.Ordinal);

    /// <summary>原始签名子句的 L / D 形参登记为固定类型（codegen.clj fix-clause-params!，cleat-042）。</summary>
    private static void FixClauseParams(object nm, Clause cl)
    {
        if (nm is not FnName fn || ClauseSig(fn, cl) is not { } sig) return;
        var cs = SigParams(sig);
        for (int k = 0; k < cl.Params.Length && k < cs.Length; k++)
        {
            var r = SigCharRaw(cs[k]);
            if (r != Raw.None) Tabs.FixedTypes[cl.Params[k]] = RawKind(r);
        }
    }

    /// <summary>字段的原始类型（"System.Int64" / "System.Double"）→ :l64 / :fl（codegen.clj field-raw，cleat-112）。</summary>
    private static TypeKind FieldRawKind(object ft) => ft as string == "System.Int64" ? TypeKind.L64 : ft as string == "System.Double" ? TypeKind.Fl : TypeKind.None;

    // —— infer-walk ——

    /// <summary>
    /// codegen.clj infer-walk（iron-034c，原版 infer-walk 的 Clojure 子集）：收集 let / label 变量的定义、被排除的变量、原始 / 装箱用途（按循环深度加权，
    /// cleat-139 的 loopweight）、形参。原始用途穿过 if / begin 的尾位置（cleat-108 的 joinraw）。
    /// </summary>
    private sealed class InferWalk
    {
        public Action<Symbol, Expr> AddDef;
        public Action<Symbol> Exclude;
        public Dictionary<Symbol, ImmutableArray<Symbol>> Labels;
        public Func<Symbol, bool> Candidate;
        public Action<Symbol, bool, int> Use;
        public Action<Symbol> Param;

        /// <summary>用途计数的权重：真正循环体里乘 8（上限 512）（*infer-use-weight*）。</summary>
        private int _weight = 1;

        private bool MaybeTyped(Expr e) => e switch
        {
            Quote q => q.Datum is long || DatumType(q.Datum) != TypeKind.None,
            Ref r => Candidate(r.Name),
            CljCall cc => cc.Op as string is "PrimInvoke" or "TypedSelf" || HostCallLayout(cc.Op, cc.Datum, cc.Args) != null,
            If i => MaybeTyped(i.Then) || MaybeTyped(i.Else),
            Begin b => MaybeTyped(b.Body[^1]),
            Let lt => MaybeTyped(lt.Body),
            Jump => true,
            Throw => true,
            _ => false,
        };

        private void Body(Clause cl)
        {
            foreach (var p in cl.Params) Param(p);
            Walk(cl.Body);
        }

        private void Raw(Expr e)
        {
            switch (e)
            {
                case Ref r: Use(r.Name, true, _weight); break;
                // 分支汇合（cleat-108）：if 两支、begin 最后一项的值就是整个表达式的值，原始发射（cg-raw-join!）直接在各支里原始求值
                case If i when OptJoinRaw:
                    Walk(i.Test);
                    Raw(i.Then);
                    Raw(i.Else);
                    break;
                case Begin b when OptJoinRaw:
                    for (int k = 0; k < b.Body.Length - 1; k++) Walk(b.Body[k]);
                    Raw(b.Body[^1]);
                    break;
                default: Walk(e); break;
            }
        }

        public void Walk(Expr e)
        {
            switch (e)
            {
                case Ref r: Use(r.Name, false, _weight); break;
                case Quote: break;
                case If i: Walk(i.Test); Walk(i.Then); Walk(i.Else); break;
                case Begin b: foreach (var x in b.Body) Walk(x); break;
                case Set s: Exclude(s.Name); Walk(s.Value); break;
                case Lambda l:
                    foreach (var x in l.Free) Exclude(x);
                    foreach (var cl in l.Clauses) FixClauseParams(l.Name, cl);
                    foreach (var cl in l.Clauses) Body(cl);
                    break;
                case Letrec lr: foreach (var x in lr.Inits) Walk(x); Walk(lr.Body); break;
                case Let lt:
                    for (int k = 0; k < lt.Vars.Length; k++)
                        if (MaybeTyped(lt.Inits[k])) AddDef(lt.Vars[k], lt.Inits[k]);
                    foreach (var x in lt.Inits) Raw(x);
                    Walk(lt.Body);
                    break;
                case Call c: Walk(c.Fn); foreach (var x in c.Args) Walk(x); break;
                case Throw t: Walk(t.Value); break;
                case TryFinally tf: Walk(tf.Body); Walk(tf.Finally); break;
                case TryCatch tc: Walk(tc.Body); foreach (var x in tc.Handlers) Walk(x); break;
                case Label lb:
                    Labels[lb.Name] = lb.Params;
                    foreach (var p in lb.Params) AddDef(p, null);
                    Walk(lb.Body0);
                    if (OptLoopWeight && RealLoop(lb))
                    {
                        int saved = _weight;
                        _weight = Math.Min(512, 8 * _weight);
                        Walk(lb.Body1);
                        _weight = saved;
                    }
                    else Walk(lb.Body1);
                    break;
                case Jump j:
                    if (Labels.TryGetValue(j.Target, out var ps))
                        for (int k = 0; k < ps.Length && k < j.Args.Length; k++) AddDef(ps[k], j.Args[k]);
                    foreach (var x in j.Args) Raw(x);
                    break;
                case Box bx: Walk(bx.Value); break;
                case SetBox sb: Walk(sb.Value); break;
                case CljVarSet vs: Walk(vs.Value); break;
                case CljDef d: Walk(d.Expr0); Walk(d.Expr1); break;
                case CljInvoke ci: Walk(ci.Fn); foreach (var x in ci.Args) Walk(x); break;
                case CljKeywordInvoke kw: Walk(kw.Target); foreach (var x in kw.Args) Walk(x); break;
                case CljCall cc: CljCallUses(cc); break;
                case CljStaticCall sc: foreach (var x in sc.Args) Walk(x); break;
                case CheckInitialized ci: Walk(ci.Value); break;
                case CljCase cs: Walk(cs.Expr0); Walk(cs.Expr1); foreach (var x in cs.Rest) Walk(x); break;
                case CljDeftype dt:
                    {
                        // ^long / ^double 字段（cleat-112）：固定类型，方法体里的算术按原始类型算；其余字段不参与推断（只有 deftype*）
                        var fts = DeftypeInfo.Kind(dt.Datum) is false
                            ? DeftypeInfo.Sl(DeftypeInfo.Get(dt.Datum, 2)).Select(f => { var fl = DeftypeInfo.Sl(f); return fl.Count > 3 ? fl[3] : false; }).ToList()
                            : new List<object>();
                        for (int k = 0; k < dt.Fields.Length; k++)
                        {
                            var r = FieldRawKind(k < fts.Count ? fts[k] : false);
                            if (r != TypeKind.None) Tabs.FixedTypes[dt.Fields[k]] = r;
                            else Exclude(dt.Fields[k]);
                        }
                        foreach (var le in dt.Methods) Walk(le);
                        break;
                    }
            }
        }

        private void CljCallUses(CljCall cc)
        {
            var e = cc.Args;
            string op = cc.Op as string;
            if (NumbersArithOp(cc.Op, cc.Datum, e) != null)
            {
                for (int k = 3; k < e.Length; k++) Raw(e[k]);
                return;
            }
            if (op is "PrimInvoke" or "TypedSelf")
            {
                Walk(e[1]);
                var cs = SigParams(op == "PrimInvoke" ? (string)QuotedDatum(e[0]) : TypedSelfSig(cc));
                for (int k = 2; k < e.Length && k - 2 < cs.Length; k++)
                {
                    if (SigCharRaw(cs[k - 2]) != Lower.Raw.None) Raw(e[k]);
                    else Walk(e[k]);
                }
                return;
            }
            if (HostCallLayout(cc.Op, cc.Datum, e) is { } lay)
            {
                if (lay.Inst != null) Walk(lay.Inst);
                foreach (var a in lay.Args) Raw(a);
                return;
            }
            foreach (var x in e) Walk(x);
        }
    }

    /// <summary>
    /// codegen.clj infer-var-types-into!（原版同名）：let 变量与 label 形参的类型取各定义类型的交（单调不动点，从 Top 出发），
    /// 原始用途不少于装箱用途时取原始存储（写进 output），否则只记事实；没定成原始类型的数值变量按 Num（cleat-102）。
    /// seeds：推测（cleat-103 / 104）试算用——体外变量的已知类型，放进环境、不参与不动点。
    /// </summary>
    internal static void InferVarTypesInto(Expr form, Dictionary<Symbol, TypeKind> output, Dictionary<Symbol, TypeKind> seeds = null)
    {
        var tabs = Tabs;
        var defs = new Dictionary<Symbol, List<Expr>>();
        var defsOrder = new List<Symbol>();
        var excluded = new HashSet<Symbol>();
        var labels = new Dictionary<Symbol, ImmutableArray<Symbol>>();
        var rawUses = new Dictionary<Symbol, int>();
        var boxedUses = new Dictionary<Symbol, int>();
        var ps = new HashSet<Symbol>();
        new InferWalk
        {
            AddDef = (x, e) =>
            {
                if (!defs.TryGetValue(x, out var l))
                {
                    defsOrder.Add(x);
                    defs[x] = l = new List<Expr>();
                }
                if (e != null) l.Insert(0, e);
            },
            Exclude = v => excluded.Add(v),
            Labels = labels,
            Candidate = x => defs.ContainsKey(x),
            Use = (x, raw, w) =>
            {
                if (!defs.ContainsKey(x) && !ps.Contains(x)) return;
                var t = raw ? rawUses : boxedUses;
                t[x] = t.GetValueOrDefault(x) + w;
            },
            Param = p => ps.Add(p),
        }.Walk(form);

        var env = new Dictionary<Symbol, TypeKind>();
        var users = new Dictionary<Symbol, List<Symbol>>();
        var cands = defsOrder.Where(x => !excluded.Contains(x)).ToList();   // 不动点与顺序无关（单调，从 top 出发）
        IEnumerable<Symbol> Deps(Expr e) => e switch
        {
            Ref r => new[] { r.Name },
            CljCall { Op: "Tagged" } tg => Deps(tg.Args[1]),
            CljCall cc => HostCallLayout(cc.Op, cc.Datum, cc.Args) is { } lay ? lay.Args.SelectMany(Deps).ToList() : Array.Empty<Symbol>(),
            If i => Deps(i.Then).Concat(Deps(i.Else)).ToList(),
            Begin b => Deps(b.Body[^1]),
            Let lt => Deps(lt.Body),
            _ => Array.Empty<Symbol>(),
        };
        TypeKind MeetDefs(Symbol x)
        {
            var t = TypeKind.Top;
            if (defs.TryGetValue(x, out var es))
                foreach (var e in es)
                {
                    if (t == TypeKind.None) break;
                    t = TypeJoin(t, ExprType(e, env));
                }
            return t == TypeKind.Fl && InlinedBinding(x) ? TypeKind.None : t;
        }
        void Propagate(IEnumerable<Symbol> start)
        {
            var work = new LinkedList<Symbol>(start);
            while (work.Count > 0)
            {
                var x = work.First.Value;
                work.RemoveFirst();
                var old = env.GetValueOrDefault(x);
                if (old == TypeKind.None) continue;
                var nw = MeetDefs(x);
                if (old != nw)
                {
                    env[x] = nw;
                    if (users.TryGetValue(x, out var us))
                        for (int k = us.Count - 1; k >= 0; k--) work.AddFirst(us[k]);
                }
            }
        }
        void Decide(Symbol x)
        {
            var t = env.GetValueOrDefault(x);
            bool prim = t is TypeKind.Fx or TypeKind.Fl or TypeKind.L64;
            if (prim) tabs.SoundTypes[x] = t;
            if (prim && boxedUses.GetValueOrDefault(x) <= rawUses.GetValueOrDefault(x)) output[x] = t;
            else if (t != TypeKind.None && t is not (TypeKind.Top or TypeKind.L64)) tabs.VarFacts[x] = t;
        }
        foreach (var (k, v) in tabs.FixedTypes.ToList())
        {
            env[k] = v;
            tabs.VarTypes[k] = v;
            tabs.SoundTypes[k] = v;
        }
        if (seeds != null)
            foreach (var (k, v) in seeds.ToList()) env[k] = v;
        foreach (var x in cands) env[x] = TypeKind.Top;
        foreach (var x in cands)
            if (defs.TryGetValue(x, out var es))
                foreach (var e in es)
                    foreach (var v in Deps(e))
                    {
                        if (!users.TryGetValue(v, out var us)) users[v] = us = new List<Symbol>();
                        us.Insert(0, x);
                    }
        Propagate(cands);
        foreach (var x in cands) Decide(x);

        // Num（cleat-102）：没定成 :fx / :fl / :l64 的——
        //   形参：有原始用途（Numbers 算术 / 比较的操作数）就放进 Num（入口 FromObject 一次；装箱用途读原来的实参，不另花钱）；
        //   let 变量与 label 形参：全部定义都是数值类型的表达式（含 Num 变量、形参）且原始用途不少于装箱用途——只增不减的不动点。
        // 这是表示，不是类型事实：不进 var-facts。
        if (!OptNum) return;
        static bool Numeric(TypeKind t) => t is TypeKind.Fx or TypeKind.Fl or TypeKind.Int or TypeKind.Real or TypeKind.Num or TypeKind.L64;
        bool Fresh(Symbol x) => !output.ContainsKey(x) && !tabs.FixedTypes.ContainsKey(x) && !excluded.Contains(x);
        foreach (var x in ps)
            if (Fresh(x) && rawUses.GetValueOrDefault(x) > 0)
            {
                env[x] = TypeKind.Num;
                output[x] = TypeKind.Num;
            }
        // 形参的副本（fn 的 arity 循环把形参复制进标签变量；(let [x p] …)）：全部定义都是对同一个 Num 形参（或其别名）的引用时
        // 做别名——否则副本的装箱用途每次 Num.Box 新建对象，还丢掉 NaN 的同一性相等
        Symbol Root(Symbol q) => tabs.NumAliases.TryGetValue(q, out var r) ? Root(r) : q;
        for (bool changed = true; changed;)
        {
            changed = false;
            foreach (var x in cands)
            {
                var ds = defs.GetValueOrDefault(x) ?? new List<Expr>();
                var qs = ds.Select(d => (d as Ref)?.Name).Distinct().ToList();
                if (Fresh(x) && ds.Count > 0 && qs.Count == 1 && qs[0] is { } q
                    && output.GetValueOrDefault(q) == TypeKind.Num && (ps.Contains(Root(q)) || tabs.NumAliases.ContainsKey(q)))
                {
                    tabs.NumAliases[x] = Root(q);
                    env[x] = TypeKind.Num;
                    output[x] = TypeKind.Num;
                    changed = true;
                }
            }
        }
        for (bool changed = true; changed;)
        {
            changed = false;
            foreach (var x in cands)
            {
                if (!Fresh(x) || !defs.TryGetValue(x, out var ds) || ds.Count == 0) continue;
                if (ds.All(d => Numeric(ExprType(d, env))) && boxedUses.GetValueOrDefault(x) <= rawUses.GetValueOrDefault(x))
                {
                    env[x] = TypeKind.Num;
                    output[x] = TypeKind.Num;
                    changed = true;
                }
            }
        }
    }
}
