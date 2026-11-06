using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection.Emit;
using Cleat.Compiler.Ir;
using Symbol = clojure.lang.Symbol;
using Ref = Cleat.Compiler.Ir.Ref;

namespace Cleat.Compiler.Lower;

/// <summary>Numbers / Num 算术的发射结果（codegen.clj num-emit! / numbers-raw-emit! 的返回值 :bool / :l64 / :num）。</summary>
internal enum ArithResult { None, Bool, L64, Num }

/// <summary>
/// ChooseRepresentation 的发射一侧（cleat-179）：codegen.clj 的 cg-raw 全档（numbers-raw-emit!、Num 路径 cg-raw-num / num-emit! / num-arith、
/// cg-raw-join!）、类型化局部变量（bind-local! / bind-value! / binding-code / bind-num-params!）、label 的原始形参与 jump 的 keep 集。
/// 表示由 <see cref="ReprTables"/>（InferTypes 的结果）决定，这里只按表发射。
/// </summary>
internal static partial class CodeGen
{
    // —— 变量的原始存储 ——

    /// <summary>x 是 Num 形参的别名且形参已绑定为 NumArg 时，形参的位置；否则 null（codegen.clj num-alias-loc）。</summary>
    internal static Loc NumAliasLoc(MethodCtx mc, Symbol x) =>
        Tabs.NumAliases.TryGetValue(x, out var p) && mc.Vars.TryGetValue(p, out var loc) && loc.Kind == LocKind.NumArg ? loc : null;

    /// <summary>变量存储的原始类型（codegen.clj var-raw-type）。</summary>
    internal static Raw VarRawType(MethodCtx mc, Symbol x)
    {
        if (!mc.Vars.TryGetValue(x, out var loc)) return Raw.None;
        return loc.Kind switch
        {
            LocKind.TLocal or LocKind.TArg or LocKind.DField => loc.Raw,   // 原始字段（cleat-112）
            LocKind.NumArg => Raw.Num,
            _ => Raw.None,
        };
    }

    /// <summary>不是大方法时 var-types 给 x 的原始存储，否则 None。</summary>
    private static Raw StoredRaw(MethodCtx mc, Symbol x) =>
        !mc.Big && Tabs.VarTypes.TryGetValue(x, out var t) ? KindRaw(t) : Raw.None;

    /// <summary>绑定局部变量 x（codegen.clj bind-local!）：Num 形参的别名共用形参的位置；推断定了类型的取原始局部变量；其余 object。</summary>
    internal static void BindLocal(MethodCtx mc, Symbol x)
    {
        if (NumAliasLoc(mc, x) is { } alias)
        {
            mc.Vars[x] = alias;
            return;
        }
        var t = StoredRaw(mc, x);
        if (t != Raw.None) mc.Vars[x] = new Loc(LocKind.TLocal, mc.NewLocal(RawType(t)), t);
        else mc.Vars[x] = new Loc(LocKind.Local, mc.NewLocal(Rt.Obj));
    }

    private static void BindValue(MethodCtx mc, Symbol x)
    {
        BindLocal(mc, x);
        StoreVar(mc, x);
    }

    /// <summary>x 的初值的代码（codegen.clj binding-code）：原始存储的变量原始求值。</summary>
    private static List<Ins> BindingCode(MethodCtx mc, Symbol x, Expr e) => mc.WithBuffer(() =>
    {
        var t = StoredRaw(mc, x);
        if (t != Raw.None) CgRaw(e, mc, t);
        else Cg(e, mc, Ctx.Value);
    });

    /// <summary>let 的绑定（cg* 的 let 与 cg-raw-join! 共用）：右边依次求值（看不到同一 let 的变量），再依次存入新变量；Num 形参的别名不求值。</summary>
    private static void BindLetVars(Let lt, MethodCtx mc)
    {
        var codes = new List<List<Ins>>();
        for (int k = 0; k < lt.Vars.Length; k++)
            codes.Add(NumAliasLoc(mc, lt.Vars[k]) != null ? new List<Ins>() : BindingCode(mc, lt.Vars[k], lt.Inits[k]));
        for (int k = 0; k < lt.Vars.Length; k++)
        {
            if (NumAliasLoc(mc, lt.Vars[k]) != null) BindLocal(mc, lt.Vars[k]);
            else
            {
                mc.EmitAll(codes[k]);
                BindValue(mc, lt.Vars[k]);
            }
        }
    }

    /// <summary>
    /// 推断为 Num 的 object 形参（codegen.clj bind-num-params!，cleat-102）：方法入口 FromObject 一次存进 Num 局部变量，原始用途读它，
    /// 装箱用途仍读实参。在 Big 决定之后调用。
    /// </summary>
    internal static void BindNumParams(MethodCtx mc, ImmutableArray<Symbol> ps)
    {
        if (mc.Big) return;
        foreach (var p in ps)
        {
            if (!mc.Vars.TryGetValue(p, out var loc) || loc.Kind != LocKind.Arg) continue;
            if (Tabs.VarTypes.GetValueOrDefault(p) != TypeKind.Num) continue;
            int t = mc.NewLocal(Rt.NumType);
            mc.Emit(OpCodes.Ldarg, loc.Index);
            mc.Emit(OpCodes.Call, Rt.NumFromObject);
            mc.Emit(OpCodes.Stloc, t);
            mc.Vars[p] = new Loc(LocKind.NumArg, loc.Index, numLocal: t);
        }
    }

    /// <summary>
    /// 分层方法里尾位置循环的 jump（codegen.clj cg* jump 的 keep 分支，cleat-117）：实参读完后，循环体不再引用（不在 keep 里、存储位置也不在
    /// keep 的位置里）的 object 形参置空；Num 形参连同它的 Num 副本（Tag 字段对非数也持有对象本身）一并清掉。
    /// </summary>
    private static void ClearUnkeptArgs(MethodCtx mc, ImmutableArray<Symbol> ps, ImmutableArray<Expr> args, HashSet<Symbol> keep)
    {
        // 按存储位置判断（cleat-123 发现）：let 把变量绑定到形参时可能直接复用形参的槽（别名），名字不在循环体里不代表槽不再被用
        var keepLocs = new HashSet<Loc>(keep.Where(v => mc.Vars.ContainsKey(v)).Select(v => mc.Vars[v]));
        Symbol Under(Symbol p, Expr a)
        {
            if (NumAliasLoc(mc, p) != null) return null;   // Num 形参的别名（循环变量就用形参的存储，cleat-102）不清
            return a switch
            {
                Ref r => r.Name,
                CljCall { Op: "Tagged" or "Speculated" } cc => Under(p, cc.Args[1]),
                _ => null,
            };
        }
        var seen = new HashSet<Symbol>();
        int n = System.Math.Min(ps.Length, args.Length);
        for (int k = 0; k < n; k++)
        {
            var a = Under(ps[k], args[k]);
            if (a is null || !seen.Add(a)) continue;
            if (keep.Contains(a) || !mc.Vars.TryGetValue(a, out var loc) || keepLocs.Contains(loc)) continue;
            switch (loc.Kind)
            {
                case LocKind.Arg:
                    mc.Emit(OpCodes.Ldnull);
                    mc.Emit(OpCodes.Starg, loc.Index);
                    break;
                case LocKind.NumArg:
                    mc.Emit(OpCodes.Ldnull);
                    mc.Emit(OpCodes.Starg, loc.Index);
                    mc.Emit(OpCodes.Ldnull);
                    mc.Emit(OpCodes.Call, Rt.NumFromObject);
                    mc.Emit(OpCodes.Stloc, loc.NumLocal);
                    break;
            }
        }
    }

    // —— 原始值（iron-034c / cleat-042 / 102 / 108）——

    private static readonly Dictionary<string, string> NumMethods = new Dictionary<string, string>
    {
        ["add"] = "Add", ["minus"] = "Subtract", ["multiply"] = "Multiply", ["divide"] = "Divide", ["inc"] = "Inc", ["dec"] = "Dec",
        ["lt"] = "LessThan", ["lte"] = "LessOrEqual", ["gt"] = "GreaterThan", ["gte"] = "GreaterOrEqual", ["equiv"] = "Equiv",
    };

    /// <summary>有 Num 操作数的 Numbers 算术 / 比较：Num 方法名、实参、是否比较（codegen.clj num-arith，cleat-102）；否则 null。</summary>
    private static (string Method, ImmutableArray<Expr> Args, bool Bool)? NumArith(Expr e)
    {
        if (e is not CljCall cc || !OptNum) return null;
        var op = NumbersArithOp(cc.Op, cc.Datum, cc.Args);
        if (op == null || !(NumbersNumOps.Contains(op) || NumbersBoolOps.Contains(op))) return null;
        var args = cc.Args.RemoveRange(0, 3);
        if (op == "minus" && args.Length == 1) return null;
        if (!args.Any(a => ExprType(a) == TypeKind.Num)) return null;
        return (NumMethods[op], args, NumbersBoolOps.Contains(op));
    }

    /// <summary>(TypedSelf …) 的返回类型（L64 / Fl），别的节点 None（codegen.clj typed-self-ret）。</summary>
    internal static Raw TypedSelfRet(Expr e) => e is CljCall { Op: "TypedSelf" } cc ? SigCharRaw(SigRet(TypedSelfSig(cc))) : Raw.None;

    /// <summary>Numbers 的 long 原始运算：实参原始求值后 call Numbers.op(long…)（codegen.clj numbers-raw-emit-args!）。</summary>
    private static void NumbersRawEmitArgs(ImmutableArray<Expr> args, string op, MethodCtx mc)
    {
        foreach (var x in args) CgRaw(x, mc, Raw.L64);
        mc.Emit(OpCodes.Call, Rt.NumbersLong(op, args.Length));
    }

    /// <summary>实参都是 :l64 的 Numbers 运算：原始发射，返回 L64 / Bool；不是时 None（codegen.clj numbers-raw-emit!）。</summary>
    private static ArithResult NumbersRawEmit(Expr e, MethodCtx mc)
    {
        if (NumbersArithArgs(e, Tabs.VarTypes) is not { } oa) return ArithResult.None;
        NumbersRawEmitArgs(oa.Args, oa.Op, mc);
        return NumbersBoolOps.Contains(oa.Op) ? ArithResult.Bool : ArithResult.L64;
    }

    /// <summary>有 Num 操作数的 Numbers 算术 / 比较：按 Num 算，返回 Num / Bool；不是时 None（codegen.clj num-emit!）。</summary>
    private static ArithResult NumEmit(Expr e, MethodCtx mc)
    {
        if (NumArith(e) is not { } na) return ArithResult.None;
        foreach (var a in na.Args) CgRawNum(a, mc);
        mc.Emit(OpCodes.Call, Rt.NumOp(na.Method, na.Args.Length));
        return na.Bool ? ArithResult.Bool : ArithResult.Num;
    }

    /// <summary>
    /// 把 e 的值以 Num 压栈（codegen.clj cg-raw-num）：常量直接构造；Num 变量 ldloc；l64 / fl 变量转换；有 Num 操作数的算术递归按 Num 算；
    /// 其余求值后 FromObject。
    /// </summary>
    private static void CgRawNum(Expr e, MethodCtx mc)
    {
        void FromObject()
        {
            Cg(e, mc, Ctx.Value);
            mc.Emit(OpCodes.Call, Rt.NumFromObject);
        }
        // 推断为 double / long 的子表达式（宿主调用、原始算术）：原始路径算出再包进 Num，不装箱
        void FromTyped()
        {
            switch (ExprType(e))
            {
                case TypeKind.Fl:
                    CgRaw(e, mc, Raw.Fl);
                    mc.Emit(OpCodes.Call, Rt.NumFromDouble);
                    break;
                case TypeKind.L64:
                    CgRaw(e, mc, Raw.L64);
                    mc.Emit(OpCodes.Call, Rt.NumFromInt64);
                    break;
                default: FromObject(); break;
            }
        }
        switch (e)
        {
            case Quote { Datum: long l }:
                mc.Emit(OpCodes.Ldc_I8, l);
                mc.Emit(OpCodes.Call, Rt.NumFromInt64);
                return;
            case Quote { Datum: double d } when !double.IsNaN(d) && !double.IsInfinity(d):
                mc.Emit(OpCodes.Ldc_R8, d);
                mc.Emit(OpCodes.Call, Rt.NumFromDouble);
                return;
            case Quote: FromObject(); return;
            case Ref r:
                {
                    mc.Vars.TryGetValue(r.Name, out var loc);
                    switch (loc?.Kind)
                    {
                        case LocKind.NumArg:
                            RefVar(mc, r.Name, false);
                            mc.Emit(OpCodes.Ldloc, loc.NumLocal);
                            return;
                        case LocKind.TLocal or LocKind.TArg:
                            switch (loc.Raw)
                            {
                                case Raw.Num:
                                    RefVar(mc, r.Name, false);
                                    mc.Emit(OpCodes.Ldloc, loc.Index);
                                    return;
                                case Raw.L64:
                                    CgRaw(e, mc, Raw.L64);
                                    mc.Emit(OpCodes.Call, Rt.NumFromInt64);
                                    return;
                                case Raw.Fl:
                                    CgRaw(e, mc, Raw.Fl);
                                    mc.Emit(OpCodes.Call, Rt.NumFromDouble);
                                    return;
                            }
                            FromObject();
                            return;
                        default: FromObject(); return;
                    }
                }
            case CljCall:
                if (NumArith(e) is { } na)
                {
                    if (na.Bool) FromObject();
                    else
                    {
                        foreach (var a in na.Args) CgRawNum(a, mc);
                        mc.Emit(OpCodes.Call, Rt.NumOp(na.Method, na.Args.Length));
                    }
                }
                else FromTyped();
                return;
            case If or Let or Begin: FromTyped(); return;
            default: FromObject(); return;
        }
    }

    /// <summary>(PrimInvoke …) 的返回正好是 t 时它的实参表，否则 default（codegen.clj raw-prim-invoke）。</summary>
    private static bool RawPrimInvoke(Expr e, Raw t) =>
        t is Raw.L64 or Raw.Fl && e is CljCall { Op: "PrimInvoke" } pi && SigCharRaw(SigRet((string)QuotedDatum(pi.Args[0]))) == t;

    /// <summary>静态决议、原始返回正好是 t 的宿主调用的决议结果，否则 null（codegen.clj raw-host-call）。</summary>
    private static HostSig RawHostCall(Expr e, Raw t)
    {
        if (t is not (Raw.L64 or Raw.Fl) || e is not CljCall cc) return null;
        var sl = HostCallSignature(cc.Op, cc.Datum, cc.Args);
        return sl != null && !ReferenceEquals(sl, Pending) && HostRetRaw(sl.Ret) == t ? sl : null;
    }

    /// <summary>把 e 的值以 t（Fx / Fl / L64 / Num）的原始形式压栈（codegen.clj cg-raw）。</summary>
    internal static void CgRaw(Expr e, MethodCtx mc, Raw t)
    {
        var oa = t == Raw.L64 ? NumbersArithArgs(e, Tabs.VarTypes) : null;
        var na = t is Raw.Fl or Raw.L64 ? NumArith(e) : null;
        if (t == Raw.Num) CgRawNum(e, mc);
        else if (t is Raw.L64 or Raw.Fl && t == TypedSelfRet(e)) CgTypedSelf((CljCall)e, mc);
        // 含 Num 操作数的算术要 double / long 原始值（推断把 (+ double-var num) 定成 double）：整棵按 Num 算再取值，不装箱
        else if (na is { Bool: false })
        {
            CgRawNum(e, mc);
            mc.Emit(OpCodes.Call, t == Raw.Fl ? Rt.NumToDouble : Rt.NumToInt64);
        }
        else if (oa is { } o && NumbersLongOps.Contains(o.Op)) NumbersRawEmitArgs(o.Args, o.Op, mc);
        else if (RawPrimInvoke(e, t)) CgPrimInvokeCore(((CljCall)e).Args, mc);
        else if (RawHostCall(e, t) is { } sl) CgStaticHostCall(sl, mc, Ctx.Value, false, raw: true);
        else if (OptJoinRaw && CgRawJoin(e, mc, t)) { }
        else CgRawDefault(e, mc, t);
    }

    /// <summary>cg-raw 的缺省档（codegen.clj cg-raw-default）：原始常量、原始存储的变量直接读；其余求值后 unbox.any。</summary>
    private static void CgRawDefault(Expr e, MethodCtx mc, Raw t)
    {
        void Fallback()
        {
            Cg(e, mc, Ctx.Value);
            mc.Emit(OpCodes.Unbox_Any, RawType(t));
        }
        switch (e)
        {
            case Quote { Datum: int n } when t == Raw.Fx: mc.Emit(OpCodes.Ldc_I4, n); return;
            case Quote { Datum: double d } when t == Raw.Fl && !double.IsNaN(d) && !double.IsInfinity(d): mc.Emit(OpCodes.Ldc_R8, d); return;
            case Quote { Datum: long l } when t == Raw.L64: mc.Emit(OpCodes.Ldc_I8, l); return;
            case Ref r when mc.Vars.TryGetValue(r.Name, out var loc) && loc.Kind is LocKind.TLocal or LocKind.TArg && loc.Raw == t:
                RefVar(mc, r.Name, false);
                mc.Emit(loc.Kind == LocKind.TArg ? OpCodes.Ldarg : OpCodes.Ldloc, loc.Index);
                return;
            case Ref r2 when mc.Vars.TryGetValue(r2.Name, out var loc2) && loc2.Kind == LocKind.DField && loc2.Raw == t:   // 原始字段直接读（cleat-112）
                mc.Emit(OpCodes.Ldarg_0);
                mc.Emit(OpCodes.Ldfld, loc2.Field);
                return;
            default: Fallback(); return;
        }
    }

    /// <summary>
    /// if / begin / let 的原始发射（codegen.clj cg-raw-join!，cleat-108 / 123）：各支 / 最后一项 / 体原始求值，汇合处不装箱。
    /// 是这几种节点（let 只在体的类型正好是 t 时）时生成并返回 true。
    /// </summary>
    private static bool CgRawJoin(Expr e, MethodCtx mc, Raw t)
    {
        switch (e)
        {
            case If i:
                {
                    var elseL = new Lbl();
                    var endL = new Lbl();
                    Cg(i.Test, mc, Ctx.Test);
                    mc.Emit(OpCodes.Brfalse, elseL);
                    var c2 = BranchRefCounts(mc, i.Else);   // 局部变量清除按分支，同 cg* 的 if
                    AdjustUses(mc, c2, -1);
                    CgRaw(i.Then, mc, t);
                    AdjustUses(mc, c2, +1);
                    mc.Emit(OpCodes.Br, endL);
                    mc.Mark(elseL);
                    CgRaw(i.Else, mc, t);
                    mc.Mark(endL);
                    return true;
                }
            case Begin b:
                for (int k = 0; k < b.Body.Length - 1; k++) Cg(b.Body[k], mc, Ctx.Effect);
                CgRaw(b.Body[^1], mc, t);
                return true;
            // let（cleat-123）：同 cg* 绑定变量，体原始求值——内联的调用是 (let ([r Var 读] …) (if 守卫 快 慢))，两支都是原始值时不再装箱再拆箱
            case Let lt when RawKind(t) == ExprType(lt.Body):
                BindLetVars(lt, mc);
                CgRaw(lt.Body, mc, t);
                return true;
            default: return false;
        }
    }
}
