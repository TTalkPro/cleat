using System;
using System.Reflection;
using Cleat.Core.Runtime;
using clojure.lang;
using RtBox = Cleat.Core.Runtime.Box;

namespace Cleat.Compiler.Lower;

/// <summary>
/// 生成的代码调用的运行时成员（designs/30 §2 D4：编译期就是 MethodInfo，不再按字符串在发射时决议）。
/// 拼写错误在类型初始化时就抛出，而不是在某次发射时。
/// </summary>
internal static class Rt
{
    private const BindingFlags PublicStatic = BindingFlags.Public | BindingFlags.Static;
    private const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;

    public static readonly Type Obj = typeof(object), ObjArray = typeof(object[]), Int32 = typeof(int), Int64 = typeof(long),
        Double = typeof(double), Bool = typeof(bool), Char = typeof(char), Void = typeof(void), Str = typeof(string);

    internal static MethodInfo S(Type t, string name, params Type[] ps) =>
        t.GetMethod(name, PublicStatic, null, ps, null) ?? throw new MissingMethodException(t.FullName, name);

    internal static MethodInfo I(Type t, string name, params Type[] ps) =>
        t.GetMethod(name, PublicInstance, null, ps, null) ?? throw new MissingMethodException(t.FullName, name);

    internal static FieldInfo F(Type t, string name) =>
        t.GetField(name, PublicStatic | BindingFlags.Instance) ?? throw new MissingFieldException(t.FullName, name);

    // —— Truth / 装箱缓存 ——
    public static readonly MethodInfo IsTrue = S(typeof(Truth), "IsTrue", Obj);
    public static readonly MethodInfo BoolBox = S(typeof(Truth), "Bool", Bool);
    public static readonly FieldInfo TrueField = F(typeof(Truth), "True"), FalseField = F(typeof(Truth), "False");
    public static readonly MethodInfo LongBoxGet = S(typeof(LongBox), "Get", Int64);
    public static readonly MethodInfo LongBoxDouble = S(typeof(LongBox), "Double", Double);
    public static readonly MethodInfo CharBoxGet = S(typeof(CharBox), "Get", Char);
    public static readonly MethodInfo LocalsTake = S(typeof(Locals), "Take", Obj.MakeByRefType());

    // —— Num：不装箱的未定类型数（cleat-102）——
    public static readonly Type NumType = typeof(Num);
    public static readonly MethodInfo NumBox = S(typeof(Num), "Box", NumType);
    public static readonly MethodInfo NumFromObject = S(typeof(Num), "FromObject", Obj);
    public static readonly MethodInfo NumFromInt64 = S(typeof(Num), "FromInt64", Int64);
    public static readonly MethodInfo NumFromDouble = S(typeof(Num), "FromDouble", Double);
    public static readonly MethodInfo NumToInt64 = S(typeof(Num), "ToInt64", NumType);
    public static readonly MethodInfo NumToDouble = S(typeof(Num), "ToDouble", NumType);

    /// <summary>Num 的算术 / 比较（codegen.clj num-methods 的方法名），n 个 Num 实参。</summary>
    public static MethodInfo NumOp(string name, int n) => n == 1 ? S(typeof(Num), name, NumType) : S(typeof(Num), name, NumType, NumType);

    /// <summary>clojure.lang.Numbers 的 long 原始重载（add / minus / … / lt …），n 个 long 实参。</summary>
    public static MethodInfo NumbersLong(string op, int n) => n == 1 ? S(typeof(Numbers), op, Int64) : S(typeof(Numbers), op, Int64, Int64);

    // —— 类型化入口（cleat-104）——
    public static readonly Type TypedBailoutType = typeof(TypedBailout);
    public static readonly MethodInfo TypedBailoutLong = S(typeof(TypedBailout), "Long", Obj);
    public static readonly MethodInfo TypedBailoutDouble = S(typeof(TypedBailout), "Double", Obj);

    // —— Box（被捕获又被赋值的局部变量）——
    public static readonly ConstructorInfo BoxCtor = typeof(RtBox).GetConstructor(new[] { Obj });
    public static readonly FieldInfo BoxValue = F(typeof(RtBox), "Value");
    public static readonly Type BoxType = typeof(RtBox);

    // —— Var ——
    public static readonly Type VarType = typeof(Var);
    public static readonly MethodInfo VarGet = I(typeof(Var), "get");
    public static readonly MethodInfo VarGetRawRoot = I(typeof(Var), "getRawRoot");
    public static readonly MethodInfo VarSet = I(typeof(Var), "set", Obj);
    public static readonly MethodInfo VarSetDynamic = I(typeof(Var), "setDynamic");
    public static readonly MethodInfo VarBindRoot = I(typeof(Var), "bindRoot", Obj);
    public static readonly MethodInfo VarSetMeta = I(typeof(Var), "setMeta", typeof(IPersistentMap));

    // —— 函数 ——
    public static readonly Type IFnType = typeof(IFn), AFunctionType = typeof(AFunction), RestFnType = typeof(RestFn);
    public static readonly ConstructorInfo AFunctionCtor = typeof(AFunction).GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
    public static readonly ConstructorInfo RestFnCtor = typeof(RestFn).GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
    private static readonly MethodInfo[] s_invoke = new MethodInfo[22];

    /// <summary>IFn.invoke：n ≤ 20 个 object；n = 21 表示 20 个 object 加 object[]。</summary>
    public static MethodInfo Invoke(int n)
    {
        if (s_invoke[n] is { } m) return m;
        var ps = new Type[n];
        for (int k = 0; k < n; k++) ps[k] = k == 20 ? ObjArray : Obj;
        return s_invoke[n] = I(typeof(IFn), "invoke", ps);
    }

    public static readonly Type ILookupType = typeof(ILookup);
    public static readonly Type PersistentMapType = typeof(IPersistentMap), PersistentVectorType = typeof(IPersistentVector);
    public static readonly Type IObjType = typeof(IObj), IMetaType = typeof(IMeta);
    public static readonly MethodInfo UtilIsNonCharNumeric = S(typeof(Util), "IsNonCharNumeric", Obj);
    public static readonly MethodInfo UtilConvertToInt = S(typeof(Util), "ConvertToInt", Obj);
    public static readonly MethodInfo UtilHash = S(typeof(Util), "hash", Obj);
    public static readonly MethodInfo UtilEquiv = S(typeof(Util), "equiv", Obj, Obj);

    /// <summary>RT.get(o, k) / RT.get(o, k, notFound)。</summary>
    public static MethodInfo RtGet(int n) => n == 2 ? S(typeof(RT), "get", Obj, Obj) : S(typeof(RT), "get", Obj, Obj, Obj);

    /// <summary>ILookup.valAt(k) / valAt(k, notFound)。</summary>
    public static MethodInfo ValAt(int n) => n == 1 ? I(typeof(ILookup), "valAt", Obj) : I(typeof(ILookup), "valAt", Obj, Obj);

    // —— ClojureOps / ClojureStaticCalls ——
    public static readonly Type OpsType = typeof(ClojureOps), StaticCallsType = typeof(ClojureStaticCalls);
    public static readonly FieldInfo Uninitialized = F(typeof(ClojureOps), "Uninitialized");
    public static readonly MethodInfo CheckInitialized = S(typeof(ClojureOps), "CheckInitialized", Obj, Obj);
    public static readonly MethodInfo IsEscapeMarker = S(typeof(ClojureOps), "IsEscapeMarker", Obj);
    public static readonly MethodInfo DefineType = S(typeof(ClojureOps), "DefineType", Obj);
    public static readonly MethodInfo BoxArg = S(typeof(ClojureOps), "BoxArg", Obj);
    public static readonly MethodInfo ConvertReturn = S(typeof(ClojureOps), "ConvertReturn", Obj, typeof(Type));
    public static readonly MethodInfo PrimArgLong = S(typeof(ClojureStaticCalls), "PrimArgLong", Obj);
    public static readonly MethodInfo PrimArgDouble = S(typeof(ClojureStaticCalls), "PrimArgDouble", Obj);
    public static readonly MethodInfo PrimRetLong = S(typeof(ClojureStaticCalls), "PrimRetLong", Obj);
    public static readonly MethodInfo PrimRetDouble = S(typeof(ClojureStaticCalls), "PrimRetDouble", Obj);
    public static readonly MethodInfo GetTypeFromHandle = S(typeof(Type), "GetTypeFromHandle", typeof(RuntimeTypeHandle));
    public static readonly MethodInfo EnsureStarted = S(typeof(RuntimeBootstrap), "EnsureStarted");

    /// <summary>
    /// clj-call 的缺省目标：ClojureOps 的公有静态方法 name（fixed 为 false 时参数个数 = argc，全部 object；
    /// 为整数 n 时前 n 个 object、最后一个 object[]）。没有时 null。
    /// </summary>
    public static MethodInfo OpsMethod(string name, object fixedArg, int argc)
    {
        int fixedN = fixedArg is false ? -1 : Convert.ToInt32(fixedArg);
        int pc = fixedN < 0 ? argc : fixedN + 1;
        foreach (var m in typeof(ClojureOps).GetMethods(PublicStatic))
        {
            if (m.Name != name) continue;
            var ps = m.GetParameters();
            if (ps.Length != pc) continue;
            if (fixedN >= 0 && ps[pc - 1].ParameterType != ObjArray) continue;
            return m;
        }
        return null;
    }
}
