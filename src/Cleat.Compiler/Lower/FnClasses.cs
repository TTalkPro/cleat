using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection.Emit;
using Cleat.Compiler.Ir;
using clojure.lang;
using Symbol = clojure.lang.Symbol;

namespace Cleat.Compiler.Lower;

/// <summary>生成的函数类：类、无参构造器、自由变量字段（c0、c1 …）。</summary>
internal sealed class FnClass
{
    public FnClass(ClassDecl cls, MethodDecl ctor, List<FieldDecl> fields)
    {
        Class = cls;
        Ctor = ctor;
        Fields = fields;
    }

    public ClassDecl Class { get; }
    public MethodDecl Ctor { get; }
    public List<FieldDecl> Fields { get; }
}

internal static partial class CodeGen
{
    /// <summary>fn* 的类；self 是 letrec 里绑定这个 lambda 的变量（到这里的 lambda 只有 Clojure 的 fn*：try 的处理器内联、deftype 的方法另行生成、循环降成 label）。</summary>
    private static FnClass LambdaClass(Lambda le, MethodCtx mc, Symbol self) =>
        le.Name is FnName nm
            ? CljFnClass(mc, nm, le.Free, le.Clauses, self)
            : throw new InvalidOperationException($"codegen: a non-fn* lambda reached codegen: {IrPrinter.ToText(le)}");

    private static void FillFields(MethodCtx mc, FnClass c, IReadOnlyList<Symbol> fvs, Action load)
    {
        for (int j = 0; j < fvs.Count; j++)
        {
            if (load != null)
            {
                load();
                mc.Emit(OpCodes.Castclass, c.Class);
            }
            else mc.Emit(OpCodes.Dup);
            RefVar(mc, fvs[j], true);
            mc.Emit(OpCodes.Stfld, c.Fields[j]);
        }
    }

    /// <summary>创建闭包：newobj 后逐个写自由变量字段。</summary>
    private static void MakeClosure(MethodCtx mc, FnClass c, ImmutableArray<Symbol> fvs)
    {
        mc.Emit(OpCodes.Newobj, c.Ctor);
        FillFields(mc, c, fvs, null);
    }

    private static string UniqueTypeName(UnitState u, string nm)
    {
        for (int k = 1; ; k++)
        {
            string n = k == 1 ? nm : nm + "__" + k;
            if (u.TypeNames.Add(n)) return n;
        }
    }

    /// <summary>fn 名字记录里固定子句的原始签名（变长子句没有）。</summary>
    private static string ClauseSig(FnName nm, Clause cl)
    {
        if (cl.Rest != null || nm.Prims is not System.Collections.IEnumerable prims) return null;
        foreach (var p in prims)
        {
            var pair = ((System.Collections.IEnumerable)p).Cast<object>().ToList();
            if (Convert.ToInt64(pair[0]) == cl.Params.Length) return (string)pair[1];
        }
        return null;
    }

    private static void BindClauseParams(MethodCtx mc, ImmutableArray<Symbol> ps, int j0, string sig)
    {
        var cs = sig != null ? SigParams(sig) : Array.Empty<char>();
        for (int k = 0; k < ps.Length; k++)
        {
            var raw = k < cs.Length ? SigCharRaw(cs[k]) : Raw.None;
            mc.Vars[ps[k]] = raw != Raw.None ? new Loc(LocKind.TArg, j0 + k, raw) : new Loc(LocKind.Arg, j0 + k);
        }
        if (sig != null) mc.RetType = SigCharRaw(SigRet(sig));
    }

    private static object[] Objs(int n) => Enumerable.Repeat((object)Rt.Obj, n).ToArray();

    private static object[] SigParamTypes(string sig) => SigParams(sig).Select(c => (object)SigCharType(c)).ToArray();

    private static MethodDecl ClauseMethod(UnitState u, ClassDecl cls, Clause cl, List<(Symbol Var, FieldDecl F)> fields, Symbol self, string mname, string sig, bool once)
    {
        var mc = new MethodCtx(u, cls, false) { OnceFn = once };
        BindClauseParams(mc, cl.Params, 1, sig);
        foreach (var (v, f) in fields) mc.Vars[v] = new Loc(LocKind.Field, field: f);
        if (self != null) mc.Vars[self] = new Loc(LocKind.Self);
        mc.Big = BiggerThan(cl.Body, BigBodyNodes);
        BindNumParams(mc, cl.Params);
        InitClearing(mc, cl.Body);
        Cg(cl.Body, mc, Ctx.Tail);
        var m = sig != null
            ? new MethodDecl(mname) { Optimize = true, Params = SigParamTypes(sig), Ret = SigCharType(SigRet(sig)), ImplementsType = PrimIface(sig), ImplementsName = "invokePrim" }
            : new MethodDecl(mname) { Override = true, Optimize = true, Params = Objs(cl.Params.Length), Ret = Rt.Obj };
        return mc.Finish(m);
    }

    private static MethodDecl StaticClauseMethod(UnitState u, ClassDecl cls, Clause cl, Symbol self, string sig, bool once)
    {
        var mc = new MethodCtx(u, cls, true) { OnceFn = once };
        if (self != null) mc.Vars[self] = new Loc(LocKind.Arg, 0);
        BindClauseParams(mc, cl.Params, 1, sig);
        mc.Big = BiggerThan(cl.Body, BigBodyNodes);
        BindNumParams(mc, cl.Params);
        InitClearing(mc, cl.Body);
        Cg(cl.Body, mc, Ctx.Tail);
        var m = sig != null
            ? new MethodDecl("invokeStaticPrim") { Static = true, Optimize = true, Params = new object[] { Rt.Obj }.Concat(SigParamTypes(sig)).ToArray(), Ret = SigCharType(SigRet(sig)) }
            : new MethodDecl("invokeStatic") { Static = true, Optimize = true, Params = Objs(cl.Params.Length + 1), Ret = Rt.Obj };
        return mc.Finish(m);
    }

    private enum BridgeKind { Static, Prim, Invoke }

    private static MethodDecl PrimBridge(UnitState u, ClassDecl cls, string sig, BridgeKind kind, MethodDecl invokeStaticPrim, MethodDecl invokePrim)
    {
        var mc = new MethodCtx(u, cls, kind == BridgeKind.Static);
        var cs = SigParams(sig);
        char rc = SigRet(sig);
        int n = cs.Length;
        var ptypes = SigParamTypes(sig);
        var rtype = SigCharType(rc);
        mc.Emit(OpCodes.Ldarg_0);
        switch (kind)
        {
            case BridgeKind.Static:
                for (int j = 1; j <= n; j++)
                {
                    ArgTake(mc, j);
                    UnboxToSig(mc, cs[j - 1]);
                }
                mc.Emit(OpCodes.Call, invokeStaticPrim);
                BoxFromSig(mc, rc);
                mc.Emit(OpCodes.Ret);
                return mc.Finish(new MethodDecl("invokeStatic") { Static = true, Optimize = true, Params = Objs(n + 1), Ret = Rt.Obj });
            case BridgeKind.Prim:
                for (int j = 1; j <= n; j++) mc.Emit(OpCodes.Ldarg, j);
                mc.Emit(OpCodes.Call, invokeStaticPrim);
                mc.Emit(OpCodes.Ret);
                return mc.Finish(new MethodDecl("invokePrim") { Optimize = true, Params = ptypes, Ret = rtype, ImplementsType = PrimIface(sig), ImplementsName = "invokePrim" });
            default:
                for (int j = 1; j <= n; j++)
                {
                    ArgTake(mc, j);
                    UnboxToSig(mc, cs[j - 1]);
                }
                mc.Emit(OpCodes.Call, invokePrim);
                BoxFromSig(mc, rc);
                mc.Emit(OpCodes.Ret);
                return mc.Finish(new MethodDecl("invoke") { Override = true, Optimize = true, Params = Objs(n), Ret = Rt.Obj });
        }
    }

    private static MethodDecl ForwardMethod(UnitState u, ClassDecl cls, Clause cl, MethodDecl invokeStatic)
    {
        var mc = new MethodCtx(u, cls, true);
        int n = cl.Params.Length;
        if (SeqPointCode(mc, cl.Body) is { } line) mc.Emit(line);
        mc.Emit(OpCodes.Ldarg_0);
        for (int j = 1; j <= n; j++)
        {
            if (TieredMethod)
            {
                mc.Emit(OpCodes.Ldarga, j);
                mc.Emit(OpCodes.Call, Rt.LocalsTake);
            }
            else mc.Emit(OpCodes.Ldarg, j);
        }
        mc.Emit(OpCodes.Call, invokeStatic);
        mc.Emit(OpCodes.Ret);
        return mc.Finish(new MethodDecl("invoke") { Override = true, Optimize = true, Params = Objs(n), Ret = Rt.Obj });
    }

    /// <summary>
    /// fn* → AFunction / RestFn 的子类（codegen.clj clj-fn-class!）。分层编译的类（<see cref="ReprTables.TierClasses"/>）在生成期间设
    /// <see cref="TieredMethod"/>，方法（转发方法与原始签名的桥除外）带 Tiered。
    /// </summary>
    private static FnClass CljFnClass(MethodCtx outer, FnName nm, ImmutableArray<Symbol> free, ImmutableArray<Clause> cls, Symbol self)
    {
        bool saved = s_tieredMethod;
        s_tieredMethod = Tabs.TierClasses.Contains((string)nm.Class);
        try { return CljFnClassBody(outer, nm, free, cls, self); }
        finally { s_tieredMethod = saved; }
    }

    private static FnClass CljFnClassBody(MethodCtx outer, FnName nm, ImmutableArray<Symbol> free, ImmutableArray<Clause> cls, Symbol self)
    {
        var u = outer.Unit;
        bool once = RT.booleanCast(nm.Get(Keyword.intern(null, "once")));
        var fieldVars = self != null ? free.Where(v => !v.Equals(self)).ToList() : free.ToList();
        var cname = UniqueTypeName(u, (string)nm.Class);
        long? variadic = nm.Variadic is null or false ? null : Convert.ToInt64(nm.Variadic);
        int n = cls.Length;
        var c = new ClassDecl(cname) { Sealed = true, Base = variadic != null ? Rt.RestFnType : Rt.AFunctionType };
        var fields = fieldVars.Select((v, j) => c.AddField("c" + j, Rt.Obj)).ToList();
        var fieldMap = fieldVars.Zip(fields, (v, f) => (v, f)).ToList();
        var fixedClauses = variadic != null ? cls.Take(n - 1).ToList() : cls.ToList();
        bool async = nm.Async;

        var staticByClause = new Dictionary<Clause, MethodDecl>(ReferenceEqualityComparer.Instance);
        // 转发 / 桥方法不分层：tier-0 把转发实参的 JIT 临时变量报告为整个方法内活着，被调用的实现运行期间抓着实参（惰性序列的头，cleat-117）
        var fwd = new HashSet<MethodDecl>(ReferenceEqualityComparer.Instance);
        MethodDecl AddFixed(MethodDecl m)
        {
            fwd.Add(m);
            return c.AddMethod(m);
        }
        if (fields.Count == 0 && !async)
        {
            foreach (var cl in fixedClauses)
            {
                string sig = ClauseSig(nm, cl);
                var sm = c.AddMethod(StaticClauseMethod(u, c, cl, self, sig, once));
                if (sig != null)
                {
                    // 转发方法调 invokeStatic(object…)：有原始签名时是桥（拆箱后调 invokeStaticPrim）
                    staticByClause[cl] = AddFixed(PrimBridge(u, c, sig, BridgeKind.Static, sm, null));
                    AddFixed(PrimBridge(u, c, sig, BridgeKind.Prim, sm, null));
                }
                else staticByClause[cl] = sm;
            }
            // 类型化入口（cleat-104）
            if (Tabs.SpecEntries.TryGetValue((string)nm.Class, out var se)) c.AddMethod(TypedEntryMethod(u, c, se));
        }
        for (int j = 0; j < n; j++)
        {
            var cl = cls[j];
            bool lastVariadic = variadic != null && j == n - 1;
            string sig = lastVariadic ? null : ClauseSig(nm, cl);
            if (async)
            {
                // ^:async fn 的体 thunk：运行时 async 方法 invokeAsync 返回 Task<object>，invoke() 转调它
                var ia = c.AddMethod(AsyncClauseMethod(u, c, cl, fieldMap, self));
                c.AddMethod(new MethodDecl("invoke") { Override = true, Ret = Rt.Obj, Body = { new Il(OpCodes.Ldarg_0), new Il(OpCodes.Call, ia), new Il(OpCodes.Ret) } });
            }
            else if (fields.Count == 0 && !lastVariadic) AddFixed(ForwardMethod(u, c, cl, staticByClause[cl]));
            else if (!lastVariadic && sig != null)
            {
                var ip = c.AddMethod(ClauseMethod(u, c, cl, fieldMap, self, "invokePrim", sig, once));
                AddFixed(PrimBridge(u, c, sig, BridgeKind.Invoke, null, ip));
            }
            else c.AddMethod(ClauseMethod(u, c, cl, fieldMap, self, lastVariadic ? "doInvoke" : "invoke", null, once));
        }
        if (variadic != null)
            c.AddMethod(new MethodDecl("getRequiredArity") { Override = true, Ret = Rt.Int32, Body = { new Il(OpCodes.Ldc_I4, (int)variadic.Value), new Il(OpCodes.Ret) } });
        {
            var yes = new Lbl();
            var has = new MethodDecl("HasArity") { Override = true, Params = new object[] { Rt.Int32 }, Ret = Rt.Bool };
            foreach (var cl in fixedClauses)
            {
                has.Body.Add(new Il(OpCodes.Ldarg_1));
                has.Body.Add(new Il(OpCodes.Ldc_I4, cl.Params.Length));
                has.Body.Add(new Il(OpCodes.Beq, yes));
            }
            if (variadic != null)
            {
                has.Body.Add(new Il(OpCodes.Ldarg_1));
                has.Body.Add(new Il(OpCodes.Ldc_I4, (int)variadic.Value));
                has.Body.Add(new Il(OpCodes.Bge, yes));
            }
            has.Body.Add(new Il(OpCodes.Ldc_I4_0));
            has.Body.Add(new Il(OpCodes.Ret));
            has.Body.Add(new LabelIns(yes));
            has.Body.Add(new Il(OpCodes.Ldc_I4_1));
            has.Body.Add(new Il(OpCodes.Ret));
            c.AddMethod(has);
        }
        var ctor = c.AddMethod(new MethodDecl(".ctor", MethodKind.Ctor)
        {
            Body = { new Il(OpCodes.Ldarg_0), new Il(OpCodes.Call, variadic != null ? Rt.RestFnCtor : Rt.AFunctionCtor), new Il(OpCodes.Ret) },
        });
        foreach (var sig in cls.Select(cl => ClauseSig(nm, cl)).Where(s => s != null).Distinct())
            c.Interfaces.Add(PrimIface(sig));
        if (TieredMethod)
            foreach (var m in c.Methods)
                if (!fwd.Contains(m)) m.Tiered = true;
        u.Decl.Classes.Add(c);
        return new FnClass(c, ctor, fields);
    }

    // —— 编译单元（codegen.clj generate-unit）——

    private static MethodDecl TopLevelMethod(UnitState u, Expr form, int k)
    {
        var mc = new MethodCtx(u, u.Decl.Module, true);
        Cg(form, mc, Ctx.Tail);
        return mc.Finish(new MethodDecl("Init" + k) { Static = true, Ret = Rt.Obj });
    }

    /// <summary>
    /// L3 顶层表达式 → L5 编译单元（codegen.clj generate-unit）。loopFlags：与 forms 对齐的"含循环"标志（只对它们做类型推断；null 表示全部）。
    /// live：内存模式（eval）——常量对象直接交给发射器。L4 的 pass 顺序同 generate-unit：MarkTierClasses → InferTypes → CollapseEntryLabels（改了就重推）
    /// → SpeculateEntries → SpeculateLoops（改了就清表重推，类型化入口的体按签名固定形参后推断）。
    /// </summary>
    internal static UnitDecl GenerateUnit(IReadOnlyList<Expr> forms, string assemblyName, string moduleName, bool live, IReadOnlyList<bool> loopFlags = null)
    {
        forms = RunReprPasses(forms, live, loopFlags);
        var decl = new UnitDecl(assemblyName, moduleName, live);
        var u = new UnitState(decl);
        var inits = forms.Select((f, k) => TopLevelMethod(u, f, k)).ToList();
        var entry = new MethodDecl("Initialize") { Static = true, Ret = Rt.Obj };
        if (inits.Count == 0)
        {
            entry.Body.Add(new Il(OpCodes.Ldnull));
            entry.Body.Add(new Il(OpCodes.Ret));
        }
        else
        {
            for (int k = 0; k < inits.Count; k++)
            {
                entry.Body.Add(new Il(OpCodes.Call, inits[k]));
                entry.Body.Add(new Il(k == inits.Count - 1 ? OpCodes.Ret : OpCodes.Pop));
            }
        }
        foreach (var m in inits) decl.Module.AddMethod(m);
        decl.Entry = decl.Module.AddMethod(entry);
        FinishVarsClass(u);
        return decl;
    }

    /// <summary>InferTypes 一遍（generate-unit 的 infer!）：collect-assigned! 全部形式，推断带循环的形式。</summary>
    private static void InferForms(IReadOnlyList<Expr> forms, IReadOnlyList<bool> loopFlags)
    {
        foreach (var f in forms) CollectAssigned(f);
        if (!OptInfer) return;
        bool byFlags = loopFlags != null && loopFlags.Count == forms.Count;
        for (int k = 0; k < forms.Count; k++)
            if (!byFlags || loopFlags[k]) InferVarTypesInto(forms[k], Tabs.VarTypes);
    }

    /// <summary>generate-unit 里 L4 的 pass（表示与推测）：结果是要生成的形式，表示表留在 <see cref="Tabs"/>。</summary>
    private static IReadOnlyList<Expr> RunReprPasses(IReadOnlyList<Expr> forms, bool live, IReadOnlyList<bool> loopFlags)
    {
        var tabs = Tabs;
        tabs.ClearAll();
        if (live || OptImageTier)
            foreach (var f in forms) MarkTierClasses(f);
        InferForms(forms, loopFlags);
        // 不回跳、入口实参都是变量的标签（内联子句外面的 arity 标签）折叠成代换：原始值能穿过内联的体（cleat-123）
        if (OptCollapse)
        {
            var fs1 = forms.Select(CollapseEntryLabels).ToList();
            if (fs1.Where((f, k) => !ReferenceEquals(f, forms[k])).Any())
            {
                tabs.ClearInference();
                InferForms(fs1, loopFlags);
                forms = fs1;
            }
        }
        // 循环推测（cleat-103）按第一遍推断的结果挑候选；改写了循环就清表重推（快份的形参由推断定成原始类型）
        if (OptInfer && (OptLoopSpec || OptTyped))
        {
            bool saved = s_specChanged;
            s_specChanged = false;
            try
            {
                IReadOnlyList<Expr> fs2 = forms;
                if (OptTyped) fs2 = fs2.Select(SpeculateEntries).ToList();
                if (OptLoopSpec)
                {
                    bool byFlags = loopFlags != null && loopFlags.Count == fs2.Count;
                    fs2 = fs2.Select((f, k) => !byFlags || loopFlags[k] ? SpeculateLoops(f) : f).ToList();
                }
                if (s_specChanged)
                {
                    tabs.ClearInference();
                    InferForms(fs2, loopFlags);
                    // 类型化入口的体（不在形式里）：形参按签名固定类型后推断
                    foreach (var se in tabs.SpecEntries.Values.ToList())
                    {
                        for (int k = 0; k < se.Params.Length; k++)
                        {
                            var t = se.Guess[k];
                            if (t == TypeKind.None) continue;
                            tabs.FixedTypes[se.Params[k]] = t;
                            tabs.VarTypes[se.Params[k]] = t;
                            tabs.SoundTypes[se.Params[k]] = t;
                        }
                        InferVarTypesInto(se.Body, tabs.VarTypes);
                    }
                    forms = fs2;
                }
            }
            finally { s_specChanged = saved; }
        }
        return forms;
    }
}
