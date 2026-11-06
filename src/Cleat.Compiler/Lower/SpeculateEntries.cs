using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection.Emit;
using Cleat.Compiler.Ir;
using Cleat.Core.Runtime;
using clojure.lang;
using Symbol = clojure.lang.Symbol;
using Ref = Cleat.Compiler.Ir.Ref;
using Label = Cleat.Compiler.Ir.Label;

namespace Cleat.Compiler.Lower;

/// <summary>
/// SpeculateEntries（cleat-180，designs/30 §4；codegen.clj speculate-entries :1580 与 cg-typed-self! / cg-typed-entry! / clj-typed-entry-method，cleat-104）：
/// 顶层 (def f (fn [形参…] 体))：单固定子句、≤ 4 形参、没有闭包字段、没有原始签名提示，体是纯的（只有 Numbers / Util.equiv 的运算、
/// if / let / begin / 循环与经 Var 的自调用，且至少有一次自调用），不超过 spec-entry-max-nodes 个节点。形参（在 Numbers 运算里用到的）与返回值推测
/// long / double：类型化的一份体（绑定换新名，自调用换成 (TypedSelf …)）在这些固定类型下推断出的返回类型就是猜的类型、
/// 自调用的实参也是，就生成 invokeStaticTyped(self, T…) → R；通用入口 invokeStatic 包成 (TypedEntry …)：实参类型相符时
/// 转调它，否则（或类型化体里自调用发现 Var 被重定义、新函数的结果不是 R，抛 TypedBailout）走通用体。long 溢出照常抛异常，不重算。
/// </summary>
internal static partial class CodeGen
{
    internal static readonly bool OptTyped = !OptFlags.Off("typed");

    private const int SpecEntryMaxNodes = 200;

    private static bool SpecSelfRef(Expr e, object v) => e is CljVarRef vr && ReferenceEquals(vr.Var, v);

    /// <summary>类型化体允许的调用（codegen.clj spec-pure-call?）。</summary>
    private static bool SpecPureCall(CljCall cc)
    {
        if (NumbersArithOp(cc.Op, cc.Datum, cc.Args) != null) return true;
        if (cc.Op as string != "CallStatic" || !(cc.Datum is long d1 && d1 == 3) || cc.Args.Length < 3) return false;
        var t = QuotedDatum(cc.Args[0]);
        var nm = QuotedDatum(cc.Args[1]);
        // cleat-134：只读宿主调用可以进入运行时引用特化。aset / 其他反射调用可能在 TypedBailout 后被通用体重算，副作用会重复，因此保守不接受。
        if (IsRtType(t) && nm is "aget" or "agetOnArray" or "alength" or "count" or "intCast") return true;
        return IsNumbersType(t) || (t is System.Type ty && ty.FullName == "clojure.lang.Util" && nm is "equiv" or "equals");
    }

    /// <summary>类型化入口的体：纯运算与自调用（codegen.clj spec-pure?）。</summary>
    private static bool SpecPure(Expr e, object v)
    {
        int n = 0;
        bool Ok(Expr x)
        {
            if (++n > SpecEntryMaxNodes) return false;
            return x switch
            {
                Ref or Quote => true,
                If i => Ok(i.Test) && Ok(i.Then) && Ok(i.Else),
                Begin b => b.Body.All(Ok),
                Let lt => lt.Inits.All(Ok) && Ok(lt.Body),
                Label lb => Ok(lb.Body0) && Ok(lb.Body1),
                Jump j => j.Args.All(Ok),
                CljInvoke ci => SpecSelfRef(ci.Fn, v) && ci.Args.All(Ok),
                CljCall cc => SpecPureCall(cc) && cc.Args.All(Ok),
                _ => false,
            };
        }
        return Ok(e);
    }

    /// <summary>codegen.clj spec-typed-copy：改名（同 spec-rename），经 Var 的自调用换成 (TypedSelf 'info f 实参 …)。</summary>
    private sealed class SpecTypedCopy : Rewriter
    {
        private readonly IReadOnlyDictionary<Symbol, Symbol> _m;
        private readonly object _v, _info;

        public SpecTypedCopy(IReadOnlyDictionary<Symbol, Symbol> m, object v, object info)
        {
            _m = m;
            _v = v;
            _info = info;
        }

        private Symbol N(Symbol x) => _m.TryGetValue(x, out var y) ? y : x;
        public override Node VisitRef(Ref n) => _m.TryGetValue(n.Name, out var y) ? new Ref(y) { Pos = n.Pos } : n;
        public override Node VisitLet(Let n) => n.Update(n.Vars.Select(N).ToImmutableArray(), RewriteList(n.Inits), Rewrite(n.Body));
        public override Node VisitLabel(Label n) => n.Update(N(n.Name), n.Params.Select(N).ToImmutableArray(), Rewrite(n.Body0), Rewrite(n.Body1));
        public override Node VisitJump(Jump n) => n.Update(N(n.Target), RewriteList(n.Args));

        public override Node VisitCljInvoke(CljInvoke n)
        {
            var args = RewriteList(n.Args);
            return SpecSelfRef(n.Fn, _v)
                ? new CljCall("TypedSelf", false, args.Insert(0, n.Fn).Insert(0, Q(_info))) { Pos = n.Pos }
                : n.Update(Rewrite(n.Fn), args);
        }
    }

    /// <summary>类型化体里的 TypedSelf 的实参表（codegen.clj spec-typed-selves）。</summary>
    private static List<ImmutableArray<Expr>> SpecTypedSelves(Expr e)
    {
        var acc = new List<ImmutableArray<Expr>>();
        void Walk(Expr x)
        {
            switch (x)
            {
                case If i: Walk(i.Test); Walk(i.Then); Walk(i.Else); break;
                case Begin b: foreach (var y in b.Body) Walk(y); break;
                case Let lt: foreach (var y in lt.Inits) Walk(y); Walk(lt.Body); break;
                case Label lb: Walk(lb.Body0); Walk(lb.Body1); break;
                case Jump j: foreach (var y in j.Args) Walk(y); break;
                case CljCall cc:
                    if (cc.Op as string == "TypedSelf") acc.Add(cc.Args.RemoveRange(0, 2));
                    foreach (var y in cc.Args) Walk(y);
                    break;
            }
        }
        Walk(e);
        return acc;
    }

    /// <summary>返回类型：expr-type 加上 label（体与循环体的类型之并，jump 是 Top）；只用于类型化入口的判定（codegen.clj spec-body-type）。</summary>
    private static TypeKind SpecBodyType(Expr e, Dictionary<Symbol, TypeKind> env) => e switch
    {
        Label lb => TypeJoin(SpecBodyType(lb.Body0, env), SpecBodyType(lb.Body1, env)),
        If i => TypeJoin(SpecBodyType(i.Then, env), SpecBodyType(i.Else, env)),
        Begin b => SpecBodyType(b.Body[^1], env),
        Let lt => SpecBodyType(lt.Body, env),
        _ => ExprType(e, env),
    };

    private static char SigChar(TypeKind t) => t == TypeKind.L64 ? 'L' : t == TypeKind.Fl ? 'D' : 'O';

    /// <summary>
    /// 猜测 guess（形参 → L64 / Fl / None）与返回 r：成立时类型化入口，否则 null（codegen.clj spec-entry-try 的 AOT 档：
    /// 没有运行时特化的 guard / ref-types，存储形式就是 guess）。
    /// </summary>
    private static SpecEntry SpecEntryTry(ImmutableArray<Symbol> xs, Expr body, object v, TypeKind[] guess, TypeKind r)
    {
        var tabs = Tabs;
        string sig = new string(guess.Select(SigChar).ToArray()) + SigChar(r);
        var m = FreshMap(xs.Concat(SpecBinders(body)));
        var ps = xs.Select(p => m[p]).ToImmutableArray();
        var info = RT.vector(sig);
        var tb = new SpecTypedCopy(m, v, info).Rewrite(body);
        var scratch = new Dictionary<Symbol, TypeKind>();
        for (int k = 0; k < ps.Length; k++)
            if (guess[k] != TypeKind.None) tabs.FixedTypes[ps[k]] = guess[k];
        InferVarTypesInto(tb, scratch, tabs.SoundTypes);
        var env = new Dictionary<Symbol, TypeKind>(scratch);
        for (int k = 0; k < ps.Length; k++)
            if (guess[k] != TypeKind.None) env[ps[k]] = guess[k];
        var rt = SpecBodyType(tb, env);
        var selves = SpecTypedSelves(tb);
        bool selfOk = selves.All(args => args.Select((a, k) => k >= guess.Length || guess[k] == TypeKind.None || guess[k] == ExprType(a, env)).All(b => b));
        // 数值特化的好处来自递归自调用（AOT 档没有引用特化，tags 总是空）
        return r == rt && selves.Count > 0 && selfOk ? new SpecEntry(sig, ps, tb, guess) : null;
    }

    /// <summary>clj-def 的 fn 能否有类型化入口（codegen.clj spec-entry）。</summary>
    private static SpecEntry SpecEntryFor(object v, Lambda l)
    {
        if (l.Name is not FnName nm || l.Free.Length != 0 || l.Clauses.Length != 1 || nm.Variadic is not (null or false) || nm.Async) return null;
        if (nm.Prims is System.Collections.IEnumerable pr && pr.Cast<object>().Any()) return null;
        var cl = l.Clauses[0];
        if (cl.Rest != null || cl.Params.Length < 1 || cl.Params.Length > 4 || !SpecPure(cl.Body, v)) return null;
        var body = cl.Body;
        // fn 的 arity 循环把形参复制进标签变量：(label recur (p' …) (jump recur p …) 体)——看标签变量的用途
        (ImmutableArray<Expr> Args, ImmutableArray<Symbol> Ls, Expr B1)? arity =
            body is Label lb && lb.Body0 is Jump j && j.Target.Equals(lb.Name) && j.Args.Length == lb.Params.Length ? (j.Args, lb.Params, lb.Body1) : null;
        var num = cl.Params.Select(p =>
            SpecNumericUse(body, p)
            || (arity is { } ar && ar.Args.Select((a, k) => IsRefOf(a, p) && SpecNumericUse(ar.B1, ar.Ls[k])).Any(b => b))).ToArray();
        if (!num.Any(b => b)) return null;
        foreach (var (t, r) in new[] { (TypeKind.L64, TypeKind.L64), (TypeKind.L64, TypeKind.Fl), (TypeKind.Fl, TypeKind.Fl) })
            if (SpecEntryTry(cl.Params, body, v, num.Select(b => b ? t : TypeKind.None).ToArray(), r) is { } se) return se;
        return null;
    }

    /// <summary>codegen.clj speculate-entries：(clj-def Var … (lambda …) …) 有类型化入口时登记进 SpecEntries，通用子句的体包成 (TypedEntry '[签名 形参…] 体)。</summary>
    private sealed class SpeculateEntriesPass : Rewriter
    {
        public override Node VisitCljDef(CljDef n)
        {
            var e0 = Rewrite(n.Expr0);
            var e1 = Rewrite(n.Expr1);
            if (e0 is Lambda l && OptTyped && n.Datum is Var && l.Name is FnName nm
                && !Tabs.SpecEntries.ContainsKey((string)nm.Class) && SpecEntryFor(n.Datum, l) is { } se)
            {
                s_specChanged = true;
                Tabs.SpecEntries[(string)nm.Class] = se;
                var cl = l.Clauses[0];
                var info = RT.vector(se.Sig, RT.vector(cl.Params.Cast<object>().ToArray()));
                var wrapped = cl.Update(cl.Params, cl.Rest, CljCallOf("TypedEntry", false, Q(info), cl.Body));
                e0 = l.Update(l.Name, l.Free, ImmutableArray.Create(wrapped));
            }
            return n.Update(n.Datum, n.Flag0, n.Flag1, e0, e1);
        }
    }

    internal static Expr SpeculateEntries(Expr form) => new SpeculateEntriesPass().Rewrite(form);

    // —— 发射 ——

    /// <summary>
    /// 类型化体里的自调用（codegen.clj cg-typed-self!）：Var 的值还是本函数时直接 call invokeStaticTyped，否则经 IFn.invoke 调用、
    /// 结果按签名取回（不是推测的类型时 TypedBailout）。原始值留在栈上。
    /// </summary>
    private static void CgTypedSelf(CljCall cc, MethodCtx mc)
    {
        string sig = TypedSelfSig(cc);
        var f = cc.Args[1];
        var args = cc.Args.RemoveRange(0, 2);
        var cs = SigParams(sig);
        char rc = SigRet(sig);
        int root = mc.NewLocal(Rt.Obj);
        Cg(f, mc, Ctx.Value);
        mc.Emit(OpCodes.Stloc, root);
        var temps = new List<(int T, bool Raw)>();
        for (int k = 0; k < args.Length && k < cs.Length; k++)
        {
            var r = SigCharRaw(cs[k]);
            if (r != Raw.None && RawKind(r) == ExprType(args[k], Tabs.SoundTypes))
            {
                CgRaw(args[k], mc, r);
                int t = mc.NewLocal(RawType(r));
                mc.Emit(OpCodes.Stloc, t);
                temps.Add((t, true));
            }
            else
            {
                Cg(args[k], mc, Ctx.Value);
                int t = mc.NewLocal(Rt.Obj);
                mc.Emit(OpCodes.Stloc, t);
                temps.Add((t, false));
            }
        }
        var slow = new Lbl();
        var end = new Lbl();
        mc.Emit(OpCodes.Ldloc, root);
        mc.Emit(OpCodes.Isinst, mc.Class);
        mc.Emit(OpCodes.Brfalse, slow);
        mc.Emit(OpCodes.Ldloc, root);
        for (int k = 0; k < temps.Count; k++)
        {
            mc.Emit(OpCodes.Ldloc, temps[k].T);
            if (!temps[k].Raw && SigCharRaw(cs[k]) != Raw.None) mc.Emit(OpCodes.Unbox_Any, SigCharType(cs[k]));
        }
        mc.Emit(OpCodes.Call, TypedMethodRef(mc.Class, sig));
        mc.Emit(OpCodes.Br, end);
        mc.Mark(slow);
        mc.Emit(OpCodes.Ldloc, root);
        mc.Emit(OpCodes.Castclass, Rt.IFnType);
        for (int k = 0; k < temps.Count; k++)
        {
            mc.Emit(OpCodes.Ldloc, temps[k].T);
            if (temps[k].Raw) BoxFromSig(mc, cs[k]);
        }
        mc.Emit(OpCodes.Callvirt, Rt.Invoke(args.Length));
        if (SigCharRaw(rc) != Raw.None) mc.Emit(OpCodes.Call, rc == 'L' ? Rt.TypedBailoutLong : Rt.TypedBailoutDouble);
        mc.Mark(end);
    }

    private static MethodRef TypedMethodRef(ClassDecl cls, string sig) =>
        new MethodRef(cls, "invokeStaticTyped", new object[] { Rt.Obj }.Concat(SigParamTypes(sig)).ToArray(), SigCharType(SigRet(sig)));

    /// <summary>
    /// 通用入口（codegen.clj cg-typed-entry!）：实参的类型与签名相符时在 try 里调 invokeStaticTyped（TypedBailout 时落回），否则生成通用体。
    /// e：('[签名 形参…] 体)。
    /// </summary>
    private static void CgTypedEntry(ImmutableArray<Expr> e, MethodCtx mc, Ctx ctx)
    {
        var info = QuotedDatum(e[0]);
        string sig = (string)RT.nth(info, 0);
        var ps = ((System.Collections.IEnumerable)RT.nth(info, 1)).Cast<Symbol>().ToList();
        var body = e[1];
        var cs = SigParams(sig);
        char rc = SigRet(sig);
        var args = ps.Select(p => mc.Vars[p].Index).ToList();
        var gen = new Lbl();
        var end = new Lbl();
        int tmp = mc.NewLocal(Rt.Obj);
        for (int k = 0; k < args.Count && k < cs.Length; k++)
            if (SigCharRaw(cs[k]) != Raw.None)
            {
                mc.Emit(OpCodes.Ldarg, args[k]);
                mc.Emit(OpCodes.Isinst, cs[k] == 'L' ? Rt.Int64 : Rt.Double);
                mc.Emit(OpCodes.Brfalse, gen);
            }
        var tryBody = mc.WithBuffer(() =>
        {
            mc.Emit(OpCodes.Ldarg_0);
            for (int k = 0; k < args.Count && k < cs.Length; k++)
            {
                mc.Emit(OpCodes.Ldarg, args[k]);
                if (SigCharRaw(cs[k]) != Raw.None) mc.Emit(OpCodes.Unbox_Any, SigCharType(cs[k]));
            }
            mc.Emit(OpCodes.Call, TypedMethodRef(mc.Class, sig));
            if (rc == 'L') BoxLong(mc);
            else BoxDouble(mc);
            mc.Emit(OpCodes.Stloc, tmp);
        });
        var t = new TryIns { Body = tryBody };
        t.Catches.Add((Rt.TypedBailoutType, new List<Ins> { new Il(OpCodes.Pop) }));
        mc.Emit(t);
        mc.Emit(OpCodes.Ldloc, tmp);
        mc.Emit(OpCodes.Brfalse, gen);
        mc.Emit(OpCodes.Ldloc, tmp);
        FinishValue(mc, ctx);
        if (ctx != Ctx.Tail) mc.Emit(OpCodes.Br, end);
        mc.Mark(gen);
        Cg(body, mc, ctx);
        if (ctx != Ctx.Tail) mc.Mark(end);
    }

    /// <summary>invokeStaticTyped(self, T…) → R：类型化子句的体（codegen.clj clj-typed-entry-method）。</summary>
    private static MethodDecl TypedEntryMethod(UnitState u, ClassDecl cls, SpecEntry se)
    {
        var mc = new MethodCtx(u, cls, true);
        BindClauseParams(mc, se.Params, 1, se.Sig);
        mc.Big = BiggerThan(se.Body, BigBodyNodes);
        BindNumParams(mc, se.Params);
        InitClearing(mc, se.Body);
        Cg(se.Body, mc, Ctx.Tail);
        return mc.Finish(new MethodDecl("invokeStaticTyped")
        {
            Static = true,
            Optimize = true,
            Params = new object[] { Rt.Obj }.Concat(SigParamTypes(se.Sig)).ToArray(),
            Ret = SigCharType(SigRet(se.Sig)),
        });
    }
}
