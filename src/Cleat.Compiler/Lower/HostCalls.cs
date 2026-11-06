using System;
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

/// <summary>表达式的类型（codegen.clj 的格元素 :fx :fl :int :real :num :bool :char 与 :l64，加上 :top）。</summary>
internal enum TypeKind { None, Fx, Fl, Int, Real, Num, Bool, Char, L64, Top }

internal static partial class CodeGen
{
    internal static readonly bool OptHost = !OptFlags.Off("host");

    private static readonly HashSet<string> NarrowValueTypes = new HashSet<string> { "System.Byte", "System.SByte", "System.Int16", "System.UInt16", "System.Single" };

    // —— 类型格（cleat-174/175 只有常量、宿主调用结果与原始签名；变量类型的推导在 cleat-179）——

    private static TypeKind Parent(TypeKind t) => t switch
    {
        TypeKind.Fx => TypeKind.Int,
        TypeKind.Int => TypeKind.Real,
        TypeKind.Fl => TypeKind.Real,
        TypeKind.Real => TypeKind.Num,
        _ => TypeKind.None,
    };

    private static IEnumerable<TypeKind> Ancestors(TypeKind t)
    {
        for (var x = t; x != TypeKind.None; x = Parent(x)) yield return x;
    }

    internal static TypeKind TypeJoin(TypeKind a, TypeKind b)
    {
        if (a == TypeKind.Top) return b;
        if (b == TypeKind.Top) return a;
        if (a == TypeKind.None || b == TypeKind.None) return TypeKind.None;
        var bs = new HashSet<TypeKind>(Ancestors(b));
        return Ancestors(a).FirstOrDefault(bs.Contains);
    }

    private static TypeKind DatumType(object d) => d switch
    {
        int => TypeKind.Fx,
        double => TypeKind.Fl,
        long or System.Numerics.BigInteger or BigInt => TypeKind.Int,
        bool => TypeKind.Bool,
        char => TypeKind.Char,
        _ when Util.IsNumeric(d) => TypeKind.Real,
        _ => TypeKind.None,
    };

    private static readonly HashSet<string> NumbersLongOps = new HashSet<string> { "add", "minus", "multiply", "inc", "dec" };
    private static readonly HashSet<string> NumbersBoolOps = new HashSet<string> { "lt", "lte", "gt", "gte", "equiv" };
    private static readonly HashSet<string> NumbersNumOps = new HashSet<string> { "add", "minus", "multiply", "divide", "inc", "dec" };

    private static bool IsNumbersType(object tp) => tp is Type t && t.FullName == "clojure.lang.Numbers" || tp as string == "clojure.lang.Numbers";
    private static bool EmptyGen(object g) => g is null or false;

    /// <summary>(CallStatic 3 'Numbers 'op nil a b) 的 op（只认 long 原始重载存在的运算与 divide），否则 null。</summary>
    internal static string NumbersArithOp(object d0, object d1, ImmutableArray<Expr> e)
    {
        if (d0 as string != "CallStatic" || !(d1 is long n && n == 3) || e.Length < 4 || e.Length > 5 || !IsNumbersType(QuotedDatum(e[0]))) return null;
        if (QuotedDatum(e[1]) is not string op || !EmptyGen(QuotedDatum(e[2]))) return null;
        int argc = e.Length - 3;
        bool ok = op is "inc" or "dec" ? argc == 1
                : op == "minus" || (op == "divide" ? argc == 2 : (NumbersLongOps.Contains(op) || NumbersBoolOps.Contains(op)) && argc == 2);
        return ok ? op : null;
    }

    // —— 宿主调用的静态决议（cleat-042 第 3 项）——

    /// <summary>静态决议的宿主调用：签名（ClojureStaticCalls.Resolve 的结果）、static?、实例表达式、实参。</summary>
    internal sealed class HostSig
    {
        public object[] Sig;
        public bool Static;
        public Expr Instance;
        public ImmutableArray<Expr> Args;
        public string Decl => (string)Sig[0];
        public string Name => (string)Sig[1];
        public string[] Params => ((object[])Sig[2]).Select(p => (string)p).ToArray();
        public string Ret => Sig[3]?.ToString();
        public bool Virt => Sig[4] is true;
        public string Kind => Sig[5]?.ToString();
        public bool ClojureDefined => Sig.Length > 6 && Sig[6] is true;
    }

    internal static readonly HostSig Pending = new HostSig();

    internal static (bool Static, Expr Inst, ImmutableArray<Expr> Args)? HostCallLayout(object d0, object d1, ImmutableArray<Expr> e)
    {
        string op = d0 as string;
        bool d1False = d1 is false;
        long d1n = d1 is long n ? n : -1;
        if (op == "CallStatic" && d1n == 3 && e.Length >= 3) return (true, null, e.RemoveRange(0, 3));
        if (op == "NewOf" && d1n == 1 && e.Length >= 1) return (true, null, e.RemoveAt(0));
        if (op == "CallInstance" && d1n == 4 && e.Length >= 4) return (false, e[2], e.RemoveRange(0, 4));
        if (op == "GetStaticField" && d1False && e.Length == 2) return (true, null, ImmutableArray<Expr>.Empty);
        if ((op == "GetInstanceField" || op == "GetInstanceFieldDirect") && d1False && e.Length == 3) return (false, e[2], ImmutableArray<Expr>.Empty);
        return null;
    }

    /// <summary>
    /// 静态决议（codegen.clj host-call-signature）：签名 / Pending（实参类型还不定）/ null。env 是实参类型用的变量类型环境，
    /// 缺省（null 或 var-types）用 sound-types（推断收敛后的类型）。
    /// </summary>
    internal static HostSig HostCallSignature(object d0, object d1, ImmutableArray<Expr> e, Dictionary<Symbol, TypeKind> env = null)
    {
        if (!OptHost) return null;
        if (env is null || ReferenceEquals(env, Tabs.VarTypes)) env = Tabs.SoundTypes;
        var layout = HostCallLayout(d0, d1, e);
        if (layout is not { } lay) return null;
        string op = (string)d0;
        var t = QuotedDatum(e[0]) as Type;
        bool ctor = op == "NewOf";
        var nm = ctor ? "" : QuotedDatum(e[1]) as string;
        object gen = op is "CallStatic" or "CallInstance" ? QuotedDatum(e[lay.Static ? 2 : 3]) : null;
        if (t == null || nm == null || !EmptyGen(gen)) return null;
        var ats = new object[lay.Args.Length];
        for (int k = 0; k < ats.Length; k++)
        {
            var a = StaticArgType(lay.Args[k], env);
            if (ReferenceEquals(a, Pending)) return Pending;
            ats[k] = a ?? (object)false;
        }
        object[] sig = ctor ? ClojureStaticCalls.ResolveCtor(t, ats)
                     : op is "CallStatic" or "CallInstance" ? ClojureStaticCalls.Resolve(t, nm, ats, lay.Static)
                     : ClojureStaticCalls.ResolveField(t, nm, lay.Static);
        return sig.Length > 0 ? new HostSig { Sig = sig, Static = lay.Static, Instance = lay.Inst, Args = lay.Args } : null;
    }

    private static readonly Dictionary<string, Raw> HostRetRaws = new Dictionary<string, Raw>
    {
        ["System.Int64"] = Raw.L64, ["System.Int32"] = Raw.L64, ["System.Int16"] = Raw.L64, ["System.SByte"] = Raw.L64, ["System.Byte"] = Raw.L64,
        ["System.UInt16"] = Raw.L64, ["System.Double"] = Raw.Fl, ["System.Single"] = Raw.Fl,
    };

    internal static Raw HostRetRaw(string ret) => ret != null && HostRetRaws.TryGetValue(ret, out var r) ? r : Raw.None;

    private static TypeKind HostCallType(CljCall cc, Dictionary<Symbol, TypeKind> env)
    {
        var sl = HostCallSignature(cc.Op, cc.Datum, cc.Args, env);
        if (ReferenceEquals(sl, Pending)) return TypeKind.Top;
        return sl == null ? TypeKind.None : RawKind(HostRetRaw(sl.Ret));
    }

    /// <summary>实参的静态类型名（给 ClojureStaticCalls.Resolve）：类型名 / Pending / null（未知）（codegen.clj static-arg-type）。</summary>
    private static object StaticArgType(Expr e, Dictionary<Symbol, TypeKind> env)
    {
        object ByType() => ExprType(e, env) switch
        {
            TypeKind.L64 => "System.Int64",
            TypeKind.Fl => "System.Double",
            TypeKind.Top => Pending,
            _ => null,
        };
        if (e is CljCall { Op: "Tagged" } tg && tg.Args.Length == 2)
            return StaticArgType(tg.Args[1], env) ?? (QuotedDatum(tg.Args[0]) is Type tt ? tt.AssemblyQualifiedName : null);
        switch (e)
        {
            case Quote q:
                return q.Datum switch { long => "System.Int64", int => "System.Int32", double => "System.Double", string => "System.String", _ => null };
            case CljCall { Op: "CallStatic" } cs:
                {
                    var sl = HostCallSignature(cs.Op, cs.Datum, cs.Args, env);
                    if (ReferenceEquals(sl, Pending)) return Pending;
                    if (sl != null && (sl.Ret is "System.Int32" or "System.Boolean" or "System.Char" or "System.String" || NarrowValueTypes.Contains(sl.Ret))) return sl.Ret;
                    return ByType();
                }
            default: return ByType();
        }
    }

    private static bool TryStaticHostCall(CljCall cc, MethodCtx mc, Ctx ctx)
    {
        var sl = HostCallSignature(cc.Op, cc.Datum, cc.Args);
        if (sl == null || ReferenceEquals(sl, Pending)) return false;
        CgStaticHostCall(sl, mc, ctx, false, false);
        return true;
    }

    private static readonly Dictionary<string, string> ValueArgHelpers = new Dictionary<string, string>
    {
        ["System.Int32"] = "ArgInt32", ["System.Int64"] = "ArgInt64", ["System.Double"] = "ArgDouble", ["System.Boolean"] = "ArgBoolean", ["System.Char"] = "ArgChar",
        ["System.Byte"] = "ArgByte", ["System.SByte"] = "ArgSByte", ["System.Int16"] = "ArgInt16", ["System.UInt16"] = "ArgUInt16", ["System.Single"] = "ArgSingle",
    };

    private static System.Reflection.MethodInfo StaticCallsMethod(string name, Type p) => Rt.S(Rt.StaticCallsType, name, p);

    private enum RawUse { None, L64, Fl, Native }

    /// <summary>x 是静态决议、返回类型为 pt（pt 为 null 时不限）的方法调用时返回它的决议结果，否则 null（cleat-111）。</summary>
    private static HostSig NativeHostCall(Expr x, string pt)
    {
        if (x is not CljCall cc) return null;
        var sl = HostCallSignature(cc.Op, cc.Datum, cc.Args);
        if (sl == null || ReferenceEquals(sl, Pending) || sl.Kind != "method") return null;
        return pt == null || pt == sl.Ret ? sl : null;
    }

    /// <summary>
    /// 静态决议的宿主调用（codegen.clj cg-static-host-call!）。raw：结果按原始值留在栈上（Int32 与较窄的整数加宽成 long、Single 加宽成 double）；
    /// native：原生返回值留在栈上。guarded：接收者已被具体类型守卫过（cleat-145）。
    /// </summary>
    internal static void CgStaticHostCall(HostSig sl, MethodCtx mc, Ctx ctx, bool guarded, bool raw, bool native = false)
    {
        string decl = sl.Decl, nm = sl.Name, ret = sl.Ret;
        var pts = sl.Params;
        var opnds = new List<Expr>();
        var types = new List<string>();
        if (!sl.Static) { opnds.Add(sl.Instance); types.Add(null); }
        opnds.AddRange(sl.Args);
        types.AddRange(pts);
        RawUse RawOf(Expr x, string pt)
        {
            if (pt == null) return RawUse.None;
            var t = ExprType(x);
            if (t == TypeKind.L64 && pt is "System.Int64" or "System.Int32") return RawUse.L64;
            if (t == TypeKind.Fl && pt == "System.Double") return RawUse.Fl;
            if (NarrowValueTypes.Contains(pt) && NativeHostCall(x, pt) != null) return RawUse.Native;
            return RawUse.None;
        }
        var raws = opnds.Select((x, k) => RawOf(x, types[k])).ToList();
        var codes = opnds.Select((x, k) => mc.WithBuffer(() =>
        {
            switch (raws[k])
            {
                case RawUse.Native: CgStaticHostCall(NativeHostCall(x, null), mc, Ctx.Value, false, false, native: true); break;
                case RawUse.L64: CgRaw(x, mc, Raw.L64); break;
                case RawUse.Fl: CgRaw(x, mc, Raw.Fl); break;
                default: Cg(x, mc, Ctx.Value); break;
            }
        })).ToList();
        void Convert(string pt, RawUse r)
        {
            if (pt == null)
            {
                if (!guarded)
                {
                    mc.Emit(OpCodes.Ldstr, nm);
                    mc.Emit(OpCodes.Call, StaticCallsMethod2("Instance"));
                }
                mc.Emit(OpCodes.Castclass, decl);
                return;
            }
            if (r is RawUse.L64 or RawUse.Fl)
            {
                if (pt == "System.Int32") mc.Emit(OpCodes.Call, Rt.S(typeof(RT), "intCast", Rt.Int64));
                return;
            }
            if (r == RawUse.Native) return;
            if (pt == "System.Object") return;
            if (ValueArgHelpers.TryGetValue(pt, out var helper)) mc.Emit(OpCodes.Call, StaticCallsMethod(helper, Rt.Obj));
            else mc.Emit(OpCodes.Castclass, pt);
        }
        if (codes.Count > 0 && codes.Skip(1).Any(ContainsTry))
        {
            var temps = codes.Select((c, k) =>
            {
                object lt = raws[k] == RawUse.Native ? types[k] : raws[k] == RawUse.L64 ? Rt.Int64 : raws[k] == RawUse.Fl ? Rt.Double : Rt.Obj;
                int t = mc.NewLocal(lt);
                mc.EmitAll(c);
                mc.Emit(OpCodes.Stloc, t);
                return t;
            }).ToList();
            for (int k = 0; k < temps.Count; k++)
            {
                mc.Emit(OpCodes.Ldloc, temps[k]);
                Convert(types[k], raws[k]);
            }
        }
        else
            for (int k = 0; k < codes.Count; k++)
            {
                mc.EmitAll(codes[k]);
                Convert(types[k], raws[k]);
            }
        string kind = sl.Kind;
        switch (kind)
        {
            case "method":
                mc.Emit(sl.Virt ? OpCodes.Callvirt : OpCodes.Call, new MethodRef(decl, nm, pts.Cast<object>().ToArray(), ret));
                break;
            case "ctor":
                mc.Emit(OpCodes.Newobj, new MethodRef(decl, ".ctor", pts.Cast<object>().ToArray(), Rt.Void, ctor: true));
                if (sl.Virt) mc.Emit(OpCodes.Box, decl);
                break;
            default:
                if (kind == "volatile-field") mc.Emit(OpCodes.Volatile);
                mc.Emit(sl.Static ? OpCodes.Ldsfld : OpCodes.Ldfld, new FieldRef(decl, nm));
                break;
        }
        if (raw)
        {
            switch (ret)
            {
                case "System.Int32" or "System.Int16" or "System.SByte": mc.Emit(OpCodes.Conv_I8); break;
                case "System.Byte" or "System.UInt16": mc.Emit(OpCodes.Conv_U8); break;
                case "System.Single": mc.Emit(OpCodes.Conv_R8); break;
            }
            return;
        }
        if (native) return;
        if (ctx == Ctx.Effect)
        {
            if (ret != "System.Void") mc.Emit(OpCodes.Pop);
            return;
        }
        if (kind == "ctor") { FinishValue(mc, ctx); return; }
        if (ret == "System.Boolean") { FinishBool(mc, ctx); return; }
        switch (ret)
        {
            case "System.Void": mc.Emit(OpCodes.Ldnull); break;
            case "System.Object":
                if (!(guarded || sl.ClojureDefined)) mc.Emit(OpCodes.Call, StaticCallsMethod("RetObject", Rt.Obj));
                break;
            case "System.Int32": mc.Emit(OpCodes.Call, StaticCallsMethod("RetInt32", Rt.Int32)); break;
            case "System.Int64": BoxLong(mc); break;
            case "System.Double": BoxDouble(mc); break;
            case "System.Char": mc.Emit(OpCodes.Box, ret); break;
            case "System.Byte" or "System.UInt16": mc.Emit(OpCodes.Conv_U8); BoxLong(mc); break;
            case "System.SByte" or "System.Int16": mc.Emit(OpCodes.Conv_I8); BoxLong(mc); break;
            case "System.Single": mc.Emit(OpCodes.Conv_R8); BoxDouble(mc); break;
        }
        FinishValue(mc, ctx);
    }

    private static System.Reflection.MethodInfo StaticCallsMethod2(string name) => Rt.S(Rt.StaticCallsType, name, Rt.Obj, Rt.Obj);
}
