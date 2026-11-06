using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Cleat.Compiler.Ir;
using Cleat.Core.Runtime;
using clojure.lang;
using Symbol = clojure.lang.Symbol;
using Ref = Cleat.Compiler.Ir.Ref;
using Label = Cleat.Compiler.Ir.Label;

namespace Cleat.Compiler.Lower;

/// <summary>
/// 推测的 L4 → L4 pass（cleat-180，designs/30 §4 的"推测"组）：CollapseEntryLabels（<c>collapse</c>）、SpeculateLoops——每个循环标签依次
/// 试标量替换（<c>scalar</c>，cleat-123）、否则 long / double 推测（<c>loopspec</c>，cleat-103），再试数组推测（<c>arrayspec</c>，cleat-127）。
/// 逐函数照 codegen.clj（collapse-entry-labels :1154、spec-label :989、array-spec-label :1087、scalar-label :1322、speculate-loops :1416）。
/// pass 读写 <see cref="ReprTables"/>（第一遍推断的结果；试算推断与原版一样写进同一组表），改了 IR 时由 GenerateUnit 清表重推。
/// </summary>
internal static partial class CodeGen
{
    internal static readonly bool OptLoopSpec = !OptFlags.Off("loopspec");
    internal static readonly bool OptArraySpec = !OptFlags.Off("arrayspec");
    internal static readonly bool OptScalar = !OptFlags.Off("scalar");
    internal static readonly bool OptCollapse = !OptFlags.Off("collapse");

    /// <summary>推测 pass 改写了形式（codegen.clj *spec-changed*）：GenerateUnit 据此清表重推。</summary>
    [ThreadStatic] private static bool s_specChanged;

    private static int s_fresh;

    /// <summary>新的局部变量名 名字%N（loops.clj fresh；序号在进程内唯一，不会与分析器的 g$名字$N$c 冲突）。</summary>
    internal static Symbol Fresh(string nm) => Symbol.intern(nm + "%" + Interlocked.Increment(ref s_fresh));

    private static Expr Q(object d) => new Quote(d);
    private static Expr R(Symbol x) => new Ref(x);
    private static Expr CljCallOf(string op, object d1, params Expr[] args) => new CljCall(op, d1, ImmutableArray.Create(args));
    private static Expr IsInstanceOf(Type t, Expr x) => CljCallOf("IsInstanceOf", false, Q(t), x);
    private static Expr Tagged(object t, Expr x) => CljCallOf("Tagged", false, Q(t), x);

    // —— 改名 ——

    /// <summary>codegen.clj spec-rename：变量引用、let / label 的绑定名与 jump 的目标按 m 改名（其余子节点照常递归，lambda 的自由变量表不改）。</summary>
    private sealed class SpecRenamer : Rewriter
    {
        private readonly IReadOnlyDictionary<Symbol, Symbol> _m;
        public SpecRenamer(IReadOnlyDictionary<Symbol, Symbol> m) { _m = m; }
        private Symbol N(Symbol x) => _m.TryGetValue(x, out var y) ? y : x;
        public override Node VisitRef(Ref n) => _m.TryGetValue(n.Name, out var y) ? new Ref(y) { Pos = n.Pos } : n;
        public override Node VisitLet(Let n) => n.Update(n.Vars.Select(N).ToImmutableArray(), RewriteList(n.Inits), Rewrite(n.Body));
        public override Node VisitLabel(Label n) => n.Update(N(n.Name), n.Params.Select(N).ToImmutableArray(), Rewrite(n.Body0), Rewrite(n.Body1));
        public override Node VisitJump(Jump n) => n.Update(N(n.Target), RewriteList(n.Args));
    }

    internal static Expr SpecRename(Expr e, IReadOnlyDictionary<Symbol, Symbol> m) => new SpecRenamer(m).Rewrite(e);

    /// <summary>名字 → 新名字（按顺序各取一个 fresh；重复的名字以最后一个为准，同 (into {} …)）。</summary>
    private static Dictionary<Symbol, Symbol> FreshMap(IEnumerable<Symbol> xs)
    {
        var m = new Dictionary<Symbol, Symbol>();
        foreach (var x in xs) m[x] = Fresh(x.ToString());
        return m;
    }

    // —— 形状检查与收集 ——

    private const int SpecMaxNodes = 300;
    private const int ScalarMaxNodes = 600;
    private const int SplitMaxNodes = 150;

    /// <summary>
    /// 可以复制的体（codegen.clj spec-simple? / scalar-nodes-ok?）：没有 lambda / letrec / try / 赋值 / box / deftype / def，节点不超过 max；
    /// allowLabel 时允许（不回跳的）label。
    /// </summary>
    private static bool SimpleBody(Expr e, int max, bool allowLabel)
    {
        int n = 0;
        bool Ok(Expr x)
        {
            if (++n > max) return false;
            return x switch
            {
                Ref or Quote or CljVarRef => true,
                If i => Ok(i.Test) && Ok(i.Then) && Ok(i.Else),
                Begin b => b.Body.All(Ok),
                Let lt => lt.Inits.All(Ok) && Ok(lt.Body),
                Label lb when allowLabel => Ok(lb.Body0) && Ok(lb.Body1),
                Call c => Ok(c.Fn) && c.Args.All(Ok),
                Throw t => Ok(t.Value),
                Jump j => j.Args.All(Ok),
                CheckInitialized ci => Ok(ci.Value),
                CljInvoke ci => Ok(ci.Fn) && ci.Args.All(Ok),
                CljKeywordInvoke k => Ok(k.Target) && k.Args.All(Ok),
                CljCall cc => cc.Args.All(Ok),
                CljStaticCall sc => sc.Args.All(Ok),
                CljCase cs => Ok(cs.Expr0) && Ok(cs.Expr1) && cs.Rest.All(Ok),
                _ => false,
            };
        }
        return Ok(e);
    }

    private static bool SpecSimple(Expr e) => SimpleBody(e, SpecMaxNodes, false);

    /// <summary>复制体里的绑定名：let 变量、label 名与形参（codegen.clj spec-binders）。</summary>
    private static List<Symbol> SpecBinders(Expr e)
    {
        var acc = new List<Symbol>();
        void Walk(Expr x)
        {
            switch (x)
            {
                case If i: Walk(i.Test); Walk(i.Then); Walk(i.Else); break;
                case Begin b: foreach (var y in b.Body) Walk(y); break;
                case Let lt:
                    acc.AddRange(lt.Vars);
                    foreach (var y in lt.Inits) Walk(y);
                    Walk(lt.Body);
                    break;
                case Label lb:
                    acc.Add(lb.Name);
                    acc.AddRange(lb.Params);
                    Walk(lb.Body0);
                    Walk(lb.Body1);
                    break;
                case Call c: Walk(c.Fn); foreach (var y in c.Args) Walk(y); break;
                case Throw t: Walk(t.Value); break;
                case Jump j: foreach (var y in j.Args) Walk(y); break;
                case CheckInitialized ci: Walk(ci.Value); break;
                case CljInvoke ci: Walk(ci.Fn); foreach (var y in ci.Args) Walk(y); break;
                case CljKeywordInvoke k: Walk(k.Target); foreach (var y in k.Args) Walk(y); break;
                case CljCall cc: foreach (var y in cc.Args) Walk(y); break;
                case CljStaticCall sc: foreach (var y in sc.Args) Walk(y); break;
                case CljCase cs: Walk(cs.Expr0); Walk(cs.Expr1); foreach (var y in cs.Rest) Walk(y); break;
            }
        }
        Walk(e);
        return acc;
    }

    /// <summary>body0 是 let / begin 套着的一个 (jump lbl 实参 …)：用 f(实参) 替换那个 jump；别的形状或 f 返回 null 时 null（codegen.clj spec-final-jump）。</summary>
    private static Expr SpecFinalJump(Expr e, Symbol lbl, Func<ImmutableArray<Expr>, Expr> f)
    {
        switch (e)
        {
            case Let lt:
                return SpecFinalJump(lt.Body, lbl, f) is { } b ? new Let(lt.Vars, lt.Inits, b) : null;
            case Begin bg:
                return SpecFinalJump(bg.Body[^1], lbl, f) is { } b2 ? new Begin(bg.Body.SetItem(bg.Body.Length - 1, b2)) : null;
            case Jump j when j.Target.Equals(lbl): return f(j.Args);
            default: return null;
        }
    }

    private static bool SpecTrivial(Expr e) => e is Ref or Quote;

    /// <summary>v 在 e 里是 Numbers 算术 / 比较的操作数（codegen.clj spec-numeric-use?）。</summary>
    private static bool SpecNumericUse(Expr e, Symbol v)
    {
        bool Walk(Expr x) => x switch
        {
            If i => Walk(i.Test) || Walk(i.Then) || Walk(i.Else),
            Begin b => b.Body.Any(Walk),
            Let lt => lt.Inits.Any(Walk) || Walk(lt.Body),
            Call c => Walk(c.Fn) || c.Args.Any(Walk),
            Throw t => Walk(t.Value),
            Jump j => j.Args.Any(Walk),
            CheckInitialized ci => Walk(ci.Value),
            CljInvoke ci => Walk(ci.Fn) || ci.Args.Any(Walk),
            CljKeywordInvoke k => Walk(k.Target) || k.Args.Any(Walk),
            CljCall cc => (NumbersArithOp(cc.Op, cc.Datum, cc.Args) != null && cc.Args.Skip(3).Any(a => a is Ref r && r.Name.Equals(v))) || cc.Args.Any(Walk),
            CljStaticCall sc => sc.Args.Any(Walk),
            CljCase cs => Walk(cs.Expr0) || Walk(cs.Expr1) || cs.Rest.Any(Walk),
            _ => false,
        };
        return Walk(e);
    }

    // —— 循环推测（cleat-103，skeg iron-071 / 080B）——
    // 最内层 label 循环里初值类型未定的形参：在"它们是 long（double）"的前提下推断能把它们定成原始类型时，生成快慢两份循环——
    // 入口按实参 isinst，成立进快份（形参的初值是 (Speculated 'l64 t)，其余由推断完成：原始局部、回边不检查），
    // 否则进原来的循环（object / Num）。Clojure 的 long 溢出抛异常，两份一致，不需要哨兵与重算。

    /// <summary>快份：(label x' (p' …) (jump x' 实参' …) body1')，推测的位置包 Speculated（codegen.clj spec-copy）。</summary>
    private static (Expr Label, List<Symbol> Params) SpecCopy(Symbol x, ImmutableArray<Symbol> xs, Expr body1, IReadOnlyList<Expr> args, IReadOnlyDictionary<int, TypeKind> guess)
    {
        var m = FreshMap(new[] { x }.Concat(xs).Concat(SpecBinders(body1)));
        var x1 = m[x];
        var args1 = args.Select((a, k) => guess.TryGetValue(k, out var t) ? CljCallOf("Speculated", false, Q(KindKeyword(t)), a) : a).ToImmutableArray();
        var ps = xs.Select(p => m[p]).ToList();
        return (new Label(x1, ps.ToImmutableArray(), new Jump(x1, args1), SpecRename(body1, m)), ps);
    }

    /// <summary>推测：位置 → L64 / Fl。先都猜 long，试算推断定不成的改猜 double，再不成的放弃；空表示不推测（codegen.clj spec-guess）。</summary>
    private static Dictionary<int, TypeKind> SpecGuess(Symbol x, ImmutableArray<Symbol> xs, Expr body1, IReadOnlyList<Expr> args, List<int> cands)
    {
        var guess = cands.ToDictionary(k => k, _ => TypeKind.L64);
        for (int n = 0; ; n++)
        {
            if (guess.Count == 0 || n > 2) return new Dictionary<int, TypeKind>();
            var (copy, ps1) = SpecCopy(x, xs, body1, args, guess);
            var scratch = new Dictionary<Symbol, TypeKind>();
            InferVarTypesInto(copy, scratch, Tabs.SoundTypes);
            var bad = guess.Where(kv => kv.Value != scratch.GetValueOrDefault(ps1[kv.Key])).ToList();
            if (bad.Count == 0) return guess;
            foreach (var (k, t) in bad)
            {
                if (t == TypeKind.L64) guess[k] = TypeKind.Fl;
                else guess.Remove(k);
            }
        }
    }

    /// <summary>(label x (x* …) body0 body1) 的推测形式，不推测时 null（codegen.clj spec-label）。</summary>
    private static Expr SpecLabel(Symbol x, ImmutableArray<Symbol> xs, Expr body0, Expr body1)
    {
        if (!SpecSimple(body1)) return null;
        return SpecFinalJump(body0, x, args =>
        {
            var cands = Enumerable.Range(0, args.Length).Where(k =>
            {
                var a = args[k];
                // 第一遍已定成原始类型的不推测；Num 变量照样推测（cleat-113：Num 的比较与混合运算仍走 Numbers.ops，快份全程原始 double）
                return a is not Quote && ExprType(a, Tabs.SoundTypes) is not (TypeKind.L64 or TypeKind.Fl or TypeKind.Fx)
                       && Tabs.VarTypes.GetValueOrDefault(xs[k]) is not (TypeKind.L64 or TypeKind.Fl)
                       && SpecNumericUse(body1, xs[k]);
            }).ToList();
            var guess = cands.Count > 0 ? SpecGuess(x, xs, body1, args, cands) : new Dictionary<int, TypeKind>();
            if (guess.Count == 0) return null;
            s_specChanged = true;
            var temps = args.Select(a => SpecTrivial(a) ? (object)a : Fresh("spec")).ToList();
            Expr TempExpr(int k) => temps[k] is Symbol s ? R(s) : (Expr)temps[k];
            var bindVars = new List<Symbol>();
            var bindInits = new List<Expr>();
            for (int k = 0; k < args.Length; k++)
                if (temps[k] is Symbol s)
                {
                    bindVars.Add(s);
                    bindInits.Add(args[k]);
                }
            var (fast, _) = SpecCopy(x, xs, body1, Enumerable.Range(0, args.Length).Select(TempExpr).ToList(), guess);
            Expr Check(int k) => IsInstanceOf(guess[k] == TypeKind.L64 ? typeof(long) : typeof(double), TempExpr(k));
            var ks = guess.Keys.OrderBy(k => k).ToList();
            var test = Check(ks[0]);
            foreach (var k in ks.Skip(1)) test = new If(test, Check(k), Q(false));
            var sel = new If(test, fast, new Jump(x, Enumerable.Range(0, args.Length).Select(TempExpr).ToImmutableArray()));
            return bindVars.Count == 0 ? sel : new Let(bindVars.ToImmutableArray(), bindInits.ToImmutableArray(), sel);
        });
    }

    // —— 数组推测（cleat-127，designs/23 §1）——
    // 循环体里被 aget / aset / alength 当数组用、在循环外绑定且不被赋值的变量 a，类型静态未知时：按用法取元素类型的证据，
    // 生成 (if (isinst T[] a) (let ([a' a]) 循环的副本（a 的用途换成 (Tagged T[] a')）) 原来的循环)。快份里的数组访问静态决议到
    // 类型化的 RT.aget / aset（ldelem / stelem）；数组在进入循环时就确定，循环里 a 不变，不需要 bailout。最多两种元素类型。

    private static bool IsRtType(object t) => t is Symbol { Name: "clojure.lang.RT", Namespace: null } || t as string == "clojure.lang.RT" || t is Type ty && ty.FullName == "clojure.lang.RT";

    /// <summary>(CallStatic 3 'clojure.lang.RT '名字 'nil 实参…)：名字与实参，否则 null（codegen.clj rt-call-name）。</summary>
    private static (string Name, ImmutableArray<Expr> Args)? RtCallName(Expr e)
    {
        if (e is not CljCall { Op: "CallStatic" } cc || !(cc.Datum is long d1 && d1 == 3) || cc.Args.Length < 4) return null;
        if (!IsRtType(QuotedDatum(cc.Args[0])) || QuotedDatum(cc.Args[1]) is not string nm || !EmptyGen(QuotedDatum(cc.Args[2]))) return null;
        return (nm, cc.Args.RemoveRange(0, 3));
    }

    private sealed class ArrayUse
    {
        public bool Numeric, Generic;
        public readonly List<TypeKind> AsetTypes = new List<TypeKind>();
    }

    /// <summary>body 里以裸变量为数组实参的 RT.aget / agetOnArray / aset / alength：变量 → 用法（codegen.clj array-uses）。</summary>
    private static Dictionary<Symbol, ArrayUse> ArrayUses(Expr body)
    {
        var acc = new Dictionary<Symbol, ArrayUse>();
        ArrayUse Note(Symbol v) => acc.TryGetValue(v, out var u) ? u : acc[v] = new ArrayUse();
        void Walk(Expr e, bool numeric)
        {
            if (RtCallName(e) is { } rc && rc.Name is "agetOnArray" or "aget" or "aset" or "alength" && rc.Args.Length > 0 && rc.Args[0] is Ref av)
            {
                switch (rc.Name)
                {
                    case "agetOnArray" or "aget":
                        if (numeric) Note(av.Name).Numeric = true;
                        else Note(av.Name).Generic = true;
                        break;
                    case "aset":
                        Note(av.Name).AsetTypes.Add(rc.Args.Length > 2 ? ExprType(rc.Args[2], Tabs.SoundTypes) : TypeKind.None);
                        break;
                }
            }
            switch (e)
            {
                case If i: Walk(i.Test, false); Walk(i.Then, false); Walk(i.Else, false); break;
                case Begin b: foreach (var x in b.Body) Walk(x, false); break;
                case Let lt: foreach (var x in lt.Inits) Walk(x, false); Walk(lt.Body, false); break;
                case Label lb: Walk(lb.Body0, false); Walk(lb.Body1, false); break;
                case Jump j: foreach (var x in j.Args) Walk(x, false); break;
                case Call c: Walk(c.Fn, false); foreach (var x in c.Args) Walk(x, false); break;
                case CljInvoke ci: Walk(ci.Fn, false); foreach (var x in ci.Args) Walk(x, false); break;
                case CljKeywordInvoke k: Walk(k.Target, false); foreach (var x in k.Args) Walk(x, false); break;
                case CljCall cc:
                    {
                        bool num = NumbersArithOp(cc.Op, cc.Datum, cc.Args) != null;
                        foreach (var x in cc.Args) Walk(x, num);
                        break;
                    }
                case CljStaticCall sc: foreach (var x in sc.Args) Walk(x, false); break;
                case Throw t: Walk(t.Value, false); break;
            }
        }
        Walk(body, false);
        return acc;
    }

    /// <summary>一个数组变量的元素类型推测（最多两个）：aset 的值类型 > aget 结果进入算术（long[]、double[]）> 通用用途（object[]）（codegen.clj array-guesses）。</summary>
    private static List<Type> ArrayGuesses(ArrayUse u)
    {
        IEnumerable<Type> ks;
        if (u.AsetTypes.Count > 0)
            ks = u.AsetTypes.Select(t => t == TypeKind.L64 ? typeof(long[]) : t == TypeKind.Fl ? typeof(double[]) : typeof(object[])).Distinct();
        else if (u.Numeric) ks = new[] { typeof(long[]), typeof(double[]) };
        else if (u.Generic) ks = new[] { typeof(object[]) };
        else return null;
        var l = ks.ToList();
        return l.Count is >= 1 and <= 2 ? l : null;
    }

    /// <summary>codegen.clj spec-tag-var：对 v 的引用换成 (Tagged t v')。</summary>
    private sealed class TagVar : Rewriter
    {
        private readonly Symbol _v, _v1;
        private readonly object _t;
        public TagVar(Symbol v, Symbol v1, object t)
        {
            _v = v;
            _v1 = v1;
            _t = t;
        }
        public override Node VisitRef(Ref n) => n.Name.Equals(_v) ? Tagged(_t, R(_v1)) : n;
    }

    /// <summary>(label x (x* …) body0 body1) 的数组推测形式；不推测时 null。fallback 是原来的（可能已做数值推测的）循环（codegen.clj array-spec-label）。</summary>
    private static Expr ArraySpecLabel(Symbol x, ImmutableArray<Symbol> xs, Expr body0, Expr body1, Expr fallback)
    {
        if (!OptArraySpec || !SpecSimple(body1) || !JumpsTo(body1, x)) return null;
        var uses = ArrayUses(body1);
        var bodyBinders = new HashSet<Symbol>(SpecBinders(body1));
        (Symbol V, List<Type> Ts)? cand = null;
        foreach (var (v, u) in uses.OrderBy(kv => kv.Key.ToString(), StringComparer.CurrentCulture))   // 同 Clojure 的 compare（String.CompareTo）
        {
            // 守卫要放在循环入口，所以只有循环体外绑定的数组才可推测。
            // 内联结果这类 let 临时在循环体内绑定；若不排除，副本会引用未进入绑定的原名。
            if (xs.Contains(v) || bodyBinders.Contains(v) || Tabs.AssignedVars.Contains(v)
                || Tabs.VarTypes.GetValueOrDefault(v) is TypeKind.L64 or TypeKind.Fl or TypeKind.Fx) continue;
            if (ArrayGuesses(u) is { } ts)
            {
                cand = (v, ts);
                break;   // 第一版只推测一个变量（多个变量的组合会让副本成倍增长）
            }
        }
        if (cand is not { } c) return null;
        s_specChanged = true;
        var elseE = fallback;
        foreach (var t in Enumerable.Reverse(c.Ts))
        {
            var v1 = Fresh(c.V.ToString());
            var m = FreshMap(new[] { x }.Concat(xs).Concat(SpecBinders(body0)).Concat(SpecBinders(body1)));
            var b0 = new TagVar(c.V, v1, t).Rewrite(SpecRename(body0, m));
            var b1 = new TagVar(c.V, v1, t).Rewrite(SpecRename(body1, m));
            elseE = new If(IsInstanceOf(t, R(c.V)),
                           new Let(ImmutableArray.Create(v1), ImmutableArray.Create(R(c.V)),
                                   new Label(m[x], xs.Select(p => m[p]).ToImmutableArray(), b0, b1)),
                           elseE);
        }
        return elseE;
    }

    // —— 折叠入口标签（cleat-123）——

    /// <summary>
    /// codegen.clj collapse-entry-labels：(label L (p …) (jump L a …) body1)，body1 不跳回 L、实参都是不被赋值的变量：换成 body1[p := a]
    /// （内联的子句的 arity 标签）。体里有 lambda / deftype 时不折叠：闭包转换后 lambda 带显式的捕获变量表，代换不改它。
    /// </summary>
    private sealed class CollapseEntryLabelsPass : Rewriter
    {
        public override Node VisitLabel(Label n)
        {
            var body0 = Rewrite(n.Body0);
            var body1 = Rewrite(n.Body1);
            if (body0 is Jump j && j.Target.Equals(n.Name) && j.Args.Length == n.Params.Length
                && j.Args.All(a => a is Ref r && !Tabs.AssignedVars.Contains(r.Name))
                && !n.Params.Any(Tabs.AssignedVars.Contains)
                && !JumpsTo(body1, n.Name)
                && !L3Some(e => e is Lambda or CljDeftype, body1))
            {
                var m = new Dictionary<Symbol, Symbol>();
                for (int k = 0; k < n.Params.Length; k++) m[n.Params[k]] = ((Ref)j.Args[k]).Name;
                return SpecRename(body1, m);
            }
            return n.Update(n.Name, n.Params, body0, body1);
        }
    }

    internal static Expr CollapseEntryLabels(Expr e) => new CollapseEntryLabelsPass().Rewrite(e);

    // —— 循环变量的标量替换（cleat-123，designs/22 §4 D10）——
    // 最内层循环的循环变量 z：recur 时（某些分支）构造 deftype T 的新对象 (NewOf T a b …)、循环体里读它的字段。T 的字段都是 long / double、
    // 构造器的实参与字段一一对应时，z 拆成每个字段一个原始循环变量与对象槽 zobj：
    //   进入时 (isinst T z0) 不成立 → 原来的循环；成立 → 字段从 z0 读出，zobj = z0（原来的对象）。
    //   字段读：(if zobj (zobj 的字段) 标量)——zobj 不空时以对象为准（被交出去以后可能被改）。
    //   别的用途（交给未知调用、守卫失败的分支……）：zobj 为空才用当前字段构造一次，用 zobj——一轮内是同一个对象，同一性与原程序相同，
    //   只是分配推迟到真正需要时（deftype 的构造器没有副作用；字段按构造器同样的转换语义存）。
    //   recur：新值在尾位置是 (NewOf T a b) 时字段取 a / b（按构造器的转换）、zobj 置空；是 z 本身时不变；是别的（未知对象）时
    //   带着本轮全部实参跳回原来的循环（在 recur 处切换是精确的：本轮的副作用都已发生）。

    /// <summary>deftype T 的字段（名字与 L64 / Fl，声明顺序），全是 long / double 且个数为 n 时；否则 null（codegen.clj scalar-type-fields）。</summary>
    private static List<(string Name, TypeKind Kind)> ScalarTypeFields(object t, int n)
    {
        if (t is not Type ty || ty.IsValueType || !ty.IsSealed) return null;
        var ks = ty.GetFields().Where(f => !f.IsStatic)
                   .Select(f => (f.Name, f.FieldType == typeof(long) ? TypeKind.L64 : f.FieldType == typeof(double) ? TypeKind.Fl : TypeKind.None)).ToList();
        return n == ks.Count && n > 0 && ks.All(k => k.Item2 != TypeKind.None) ? ks : null;
    }

    private static (object Type, ImmutableArray<Expr> Args)? NewOfParts(Expr e) =>
        e is CljCall { Op: "NewOf" } cc && cc.Datum is long d1 && d1 == 1 && cc.Args.Length > 0 ? (QuotedDatum(cc.Args[0]), cc.Args.RemoveAt(0)) : null;

    private static Expr Untag(Expr e) => e is CljCall { Op: "Tagged" } cc && cc.Args.Length == 2 ? Untag(cc.Args[1]) : e;

    /// <summary>(GetInstanceField 'T 'f z)（实例可带 Tagged）：T、f、实例，否则 null（codegen.clj field-read-of）。</summary>
    private static (object Type, object Field, Expr Inst)? FieldReadOf(Expr e) =>
        e is CljCall { Op: "GetInstanceField" } cc && cc.Datum is false && cc.Args.Length == 3 ? (QuotedDatum(cc.Args[0]), QuotedDatum(cc.Args[1]), Untag(cc.Args[2])) : null;

    private static bool IsRefOf(Expr e, Symbol x) => e is Ref r && r.Name.Equals(x);

    /// <summary>e 的尾位置表达式（穿过 let / begin / if / label 的体）（codegen.clj tails）。</summary>
    private static IEnumerable<Expr> Tails(Expr e) => e switch
    {
        Let lt => Tails(lt.Body),
        Begin b => Tails(b.Body[^1]),
        If i => Tails(i.Then).Concat(Tails(i.Else)),
        Label lb => Tails(lb.Body1),
        _ => new[] { e },
    };

    /// <summary>body 里跳到 x 的 jump 的实参表（不进 lambda）（codegen.clj loop-jumps）。</summary>
    private static List<ImmutableArray<Expr>> LoopJumps(Expr body, Symbol x)
    {
        var acc = new List<ImmutableArray<Expr>>();
        L3Some(j =>
        {
            if (j is Jump jj && jj.Target.Equals(x)) acc.Add(jj.Args);
            return false;
        }, body, own: true);
        return acc;
    }

    /// <summary>构造器存原始字段时的转换（PrimArgLong / PrimArgDouble），实参已是同类型的原始值时省掉（codegen.clj prim-arg-conv）。</summary>
    private static Expr PrimArgConv(Expr e, TypeKind k) =>
        k == ExprType(e, Tabs.SoundTypes) ? e
        : CljCallOf("CallStatic", 3L, Q(typeof(ClojureStaticCalls)), Q(k == TypeKind.L64 ? "PrimArgLong" : "PrimArgDouble"), Q(null), e);

    /// <summary>
    /// codegen.clj rw-sub：对 e 的各个子表达式应用 rw 后重建（scalar-nodes-ok? 允许的形式）；绑定名与 jump 目标经 rn 改名。
    /// 变量引用由 rw 自己处理（rw 在调 RwSub 之前分派）。
    /// </summary>
    private static Expr RwSub(Func<Expr, Expr> rw, Func<Symbol, Symbol> rn, Expr e)
    {
        ImmutableArray<Expr> All(ImmutableArray<Expr> xs) => xs.Select(rw).ToImmutableArray();
        switch (e)
        {
            case Ref: return rw(e);
            case If i: return new If(rw(i.Test), rw(i.Then), rw(i.Else));
            case Begin b: return new Begin(All(b.Body));
            case Let lt:
                {
                    var vs = lt.Vars.Select(rn).ToImmutableArray();
                    return new Let(vs, All(lt.Inits), rw(lt.Body));
                }
            case Label lb:
                {
                    var nm = rn(lb.Name);
                    var ps = lb.Params.Select(rn).ToImmutableArray();
                    return new Label(nm, ps, rw(lb.Body0), rw(lb.Body1));
                }
            case Jump j:
                {
                    var t = rn(j.Target);
                    return new Jump(t, All(j.Args));
                }
            case Call c: return new Call(rw(c.Fn), All(c.Args));
            case Throw t: return new Throw(rw(t.Value));
            case CheckInitialized ci: return new CheckInitialized(rw(ci.Value));
            case CljInvoke ci: return new CljInvoke(rw(ci.Fn), All(ci.Args));
            case CljKeywordInvoke k: return new CljKeywordInvoke(k.Keyword, rw(k.Target), All(k.Args));
            case CljCall cc: return new CljCall(cc.Op, cc.Datum, All(cc.Args));
            case CljStaticCall sc: return new CljStaticCall(sc.Datum0, sc.Datum1, All(sc.Args));
            case CljCase cs: return new CljCase(cs.Datum, rw(cs.Expr0), rw(cs.Expr1), All(cs.Rest));
            default: return e;   // quote、clj-var-ref 与其余
        }
    }

    private static int NodeCount(Expr e)
    {
        int n = 0;
        L3Some(_ =>
        {
            n++;
            return false;
        }, e);
        return n;
    }

    /// <summary>(let ([t E]) body) → E 的每个尾位置 X 换成 (let ([t' X]) body')（body' 是换了新名的副本）（codegen.clj push-let-into-tails）。</summary>
    private static Expr PushLetIntoTails(Symbol t, Expr e, Expr body)
    {
        Expr CopyBody(Expr x)
        {
            var m = FreshMap(new[] { t }.Concat(SpecBinders(body)));
            return new Let(ImmutableArray.Create(m[t]), ImmutableArray.Create(x), SpecRename(body, m));
        }
        Expr Push(Expr x) => x switch
        {
            Let lt => new Let(lt.Vars, lt.Inits, Push(lt.Body)),
            Begin b => new Begin(b.Body.SetItem(b.Body.Length - 1, Push(b.Body[^1]))),
            If i => new If(i.Test, Push(i.Then), Push(i.Else)),
            _ => CopyBody(x),
        };
        return Push(e);
    }

    /// <summary>
    /// (let ([t (NewOf T a …)]) body) 的拆分形式（codegen.clj scalarize-let-new，cleat-123 第二步）：字段局部变量 + 对象槽
    /// （同循环变量：字段读按 (if tobj (tobj 的字段) 标量)，别的用途在 tobj 为空时才构造一次）；不拆时 null。
    /// </summary>
    private static Expr ScalarizeLetNew(Symbol t, Expr e, Expr body)
    {
        if (NewOfParts(e) is not { } np) return null;
        var ty = np.Type;
        var args = np.Args;
        var fs = Tabs.AssignedVars.Contains(t) ? null : ScalarTypeFields(ty, args.Length);
        if (fs == null) return null;
        if (!L3Some(x => FieldReadOf(x) is { } fr && ReferenceEquals(fr.Type, ty) && IsRefOf(fr.Inst, t), body)) return null;
        var tf = fs.Select(f => Fresh(t + "." + f.Name)).ToList();
        var tobj = Fresh(t + ".obj");
        bool raw = args.Select((a, k) => fs[k].Kind == ExprType(a, Tabs.SoundTypes)).All(b => b);
        var ats = raw ? null : args.Select(_ => Fresh("sr")).ToList();
        Expr Materialize() => new If(R(tobj), R(tobj),
            new Begin(ImmutableArray.Create<Expr>(new Set(tobj, new CljCall("NewOf", 1L, ImmutableArray.Create(Q(ty)).AddRange(tf.Select(R)))), R(tobj))));
        var materialize = Materialize();
        Expr Rw(Expr x)
        {
            if (FieldReadOf(x) is { } fr)
            {
                if (ReferenceEquals(fr.Type, ty) && IsRefOf(fr.Inst, t))
                {
                    int j = fs.FindIndex(f => Equals(f.Name, fr.Field));
                    return new If(R(tobj), CljCallOf("GetInstanceField", false, Q(ty), Q(fr.Field), Tagged(ty, R(tobj))), R(tf[j]));
                }
                return RwSub(Rw, s => s, x);
            }
            return x is Ref r ? (r.Name.Equals(t) ? materialize : x) : RwSub(Rw, s => s, x);
        }
        var inits = raw ? args : ats.Select((a, k) => PrimArgConv(R(a), fs[k].Kind)).ToImmutableArray();
        var inner = new Let(tf.ToImmutableArray(), inits,
                            new Let(ImmutableArray.Create(tobj), ImmutableArray.Create(Q(null)), Rw(body)));
        return raw ? inner : new Let(ats.ToImmutableArray(), args, inner);
    }

    /// <summary>e 里（不进 lambda）可拆的 let 绑定的构造；多绑定的 let 先拆成嵌套的单绑定（名字唯一，求值顺序不变）（codegen.clj scalarize-lets）。</summary>
    private static Expr ScalarizeLets(Expr e)
    {
        Expr Walk(Expr x)
        {
            switch (x)
            {
                case Let { Vars.Length: 0 } lt0: return new Let(lt0.Vars, lt0.Inits, Walk(lt0.Body));
                case Let lt when lt.Vars.Length > 1:
                    {
                        Expr b = lt.Body;
                        for (int k = lt.Vars.Length - 1; k >= 0; k--) b = new Let(ImmutableArray.Create(lt.Vars[k]), ImmutableArray.Create(lt.Inits[k]), b);
                        return Walk(b);
                    }
                case Let lt:
                    {
                        var t = lt.Vars[0];
                        var e0 = Walk(lt.Inits[0]);
                        var body = lt.Body;
                        if (ScalarizeLetNew(t, e0, body) is { } s) return s;
                        var ts = Tails(e0).ToList();
                        var news = ts.Select(NewOfParts).Where(p => p != null).Select(p => p.Value).ToList();
                        if (news.Count > 0 && news.Count < ts.Count && !Tabs.AssignedVars.Contains(t)
                            && news.Any(n => ScalarTypeFields(n.Type, n.Args.Length) != null)
                            && NodeCount(body) <= SplitMaxNodes
                            && L3Some(y => FieldReadOf(y) is { } fr && IsRefOf(fr.Inst, t), body))
                            return Walk(PushLetIntoTails(t, e0, body));
                        return new Let(ImmutableArray.Create(t), ImmutableArray.Create(e0), Walk(body));
                    }
                case Lambda: return x;
                case Ref: return x;
                default: return RwSub(Walk, s => s, x);
            }
        }
        return Walk(e);
    }

    /// <summary>(label x (x* …) body0 body1) 的标量替换形式；不做时 null（codegen.clj scalar-label）。</summary>
    private static Expr ScalarLabel(Symbol x, ImmutableArray<Symbol> xs, Expr body0, Expr body1)
    {
        if (!OptScalar) return null;
        body1 = CollapseEntryLabels(body1);
        if (!SimpleBody(body1, ScalarMaxNodes, true) || !JumpsTo(body1, x)) return null;
        var jumps = LoopJumps(body1, x);
        int k = -1;
        Symbol z = null;
        object t = null;
        List<(string Name, TypeKind Kind)> fs = null;
        for (int i = 0; i < xs.Length && k < 0; i++)
        {
            var zi = xs[i];
            var news = jumps.SelectMany(js => Tails(js[i])).Select(NewOfParts).Where(p => p != null).Select(p => p.Value).ToList();
            var ti = news.Count > 0 ? news[0].Type : null;
            var fsi = ti != null ? ScalarTypeFields(ti, news[0].Args.Length) : null;
            if (fsi != null && !Tabs.AssignedVars.Contains(zi)
                && news.All(n => ReferenceEquals(n.Type, ti) && n.Args.Length == fsi.Count)
                && L3Some(e => FieldReadOf(e) is { } fr && ReferenceEquals(fr.Type, ti) && IsRefOf(fr.Inst, zi), body1))
            {
                k = i;
                z = zi;
                t = ti;
                fs = fsi;
            }
        }
        if (k < 0) return null;
        s_specChanged = true;
        var fx = Fresh("fast");
        var zf = fs.Select(f => Fresh(z + "." + f.Name)).ToList();
        var zobj = Fresh(z + ".obj");
        var others = xs.Where((_, i) => i != k).ToList();
        var m = FreshMap(others.Concat(SpecBinders(body1)));
        Symbol Mn(Symbol s) => m.TryGetValue(s, out var y) ? y : s;
        var fps = others.Select(Mn).Concat(zf).Append(zobj).ToImmutableArray();   // 快份循环的形参：其余循环变量、各字段、对象槽
        Expr ReadField(object nm, Symbol v) => CljCallOf("GetInstanceField", false, Q(t), Q(nm), Tagged(t, R(v)));
        var materialize = new If(R(zobj), R(zobj),
            new Begin(ImmutableArray.Create<Expr>(new Set(zobj, new CljCall("NewOf", 1L, ImmutableArray.Create(Q(t)).AddRange(zf.Select(R)))), R(zobj))));
        Expr FastJump(IEnumerable<Expr> otherVals, IEnumerable<Expr> fieldVals, Expr objVal) =>
            new Jump(fx, otherVals.Concat(fieldVals).Append(objVal).ToImmutableArray());
        Expr Rw(Expr e)
        {
            if (FieldReadOf(e) is { } fr)
            {
                if (ReferenceEquals(fr.Type, t) && IsRefOf(fr.Inst, z))
                {
                    int j = fs.FindIndex(f => Equals(f.Name, fr.Field));
                    return new If(R(zobj), ReadField(fr.Field, zobj), R(zf[j]));
                }
                return RwSub(Rw, Mn, e);
            }
            switch (e)
            {
                case Ref r: return r.Name.Equals(z) ? materialize : (m.TryGetValue(r.Name, out var y) ? R(y) : e);
                case Jump jp when jp.Target.Equals(x):
                    {
                        // recur：z 之前的实参先求值进临时变量，z 的实参按尾位置分派，之后的实参在每个尾里求值（求值顺序不变）
                        var pre = jp.Args.Take(k).ToList();
                        var post = jp.Args.Skip(k + 1).ToList();
                        var tmps = pre.Select(_ => Fresh("sr")).ToList();
                        IEnumerable<Expr> OthersVals(IEnumerable<Expr> post1) => tmps.Select(R).Concat(post1);
                        Expr Slow(Expr zv)
                        {
                            var zt = Fresh("sr");
                            return new Let(ImmutableArray.Create(zt), ImmutableArray.Create(zv),
                                           new Jump(x, tmps.Select(R).Append(R(zt)).Concat(post.Select(Rw)).ToImmutableArray()));
                        }
                        Expr Push(Expr ze)
                        {
                            if (NewOfParts(ze) is { } np && ReferenceEquals(np.Type, t))
                            {
                                // 实参已是字段的原始类型：构造器的转换是恒等，不经临时变量
                                if (np.Args.Select((a, i) => fs[i].Kind == ExprType(a, Tabs.SoundTypes)).All(b => b))
                                    return FastJump(OthersVals(post.Select(Rw).ToList()), np.Args.Select(Rw).ToList(), Q(null));
                                var ats = np.Args.Select(_ => Fresh("sr")).ToList();
                                return new Let(ats.ToImmutableArray(), np.Args.Select(Rw).ToImmutableArray(),
                                               FastJump(OthersVals(post.Select(Rw).ToList()), ats.Select((a, i) => PrimArgConv(R(a), fs[i].Kind)).ToList(), Q(null)));
                            }
                            if (IsRefOf(Untag(ze), z)) return FastJump(OthersVals(post.Select(Rw).ToList()), zf.Select(R).ToList(), R(zobj));
                            return ze switch
                            {
                                Let lt => new Let(lt.Vars.Select(Mn).ToImmutableArray(), lt.Inits.Select(Rw).ToImmutableArray(), Push(lt.Body)),
                                Begin b => new Begin(b.Body.Take(b.Body.Length - 1).Select(Rw).Append(Push(b.Body[^1])).ToImmutableArray()),
                                If i => new If(Rw(i.Test), Push(i.Then), Push(i.Else)),
                                _ => Slow(Rw(ze)),
                            };
                        }
                        var pushed = Push(jp.Args[k]);
                        return pre.Count == 0 ? pushed : new Let(tmps.ToImmutableArray(), pre.Select(Rw).ToImmutableArray(), pushed);
                    }
                default: return RwSub(Rw, Mn, e);
            }
        }
        var fastBody = ScalarizeLets(Rw(body1));
        return SpecFinalJump(body0, x, args =>
        {
            var tmps = args.Select(_ => Fresh("sr")).ToList();
            var zt = tmps[k];
            var entryArgs = tmps.Where((_, i) => i != k).Select(R)
                                .Concat(fs.Select(f => ReadField(f.Name, zt)))
                                .Append(R(zt)).ToImmutableArray();
            return new Let(tmps.ToImmutableArray(), args,
                           new If(IsInstanceOf((Type)t, R(zt)),
                                  new Label(fx, fps, new Jump(fx, entryArgs), fastBody),
                                  new Jump(x, tmps.Select(R).ToImmutableArray())));
        });
    }

    /// <summary>codegen.clj speculate-loops：每个循环标签（先处理内层）——标量替换，否则数值推测；之后试数组推测（回退是前一步的结果）。</summary>
    private sealed class SpeculateLoopsPass : Rewriter
    {
        public override Node VisitLabel(Label n)
        {
            var body0 = Rewrite(n.Body0);
            var body1 = Rewrite(n.Body1);
            if (ScalarLabel(n.Name, n.Params, body0, body1) is { } sb0)
                return new Label(n.Name, n.Params, sb0, body1) { Pos = n.Pos };   // 标量替换（cleat-123）：快份在 body0 里，原来的循环兼作退回
            var b0 = SpecLabel(n.Name, n.Params, body0, body1);
            var plain = n.Update(n.Name, n.Params, b0 ?? body0, body1);
            return ArraySpecLabel(n.Name, n.Params, body0, body1, plain) ?? plain;
        }
    }

    internal static Expr SpeculateLoops(Expr form) => new SpeculateLoopsPass().Rewrite(form);
}
