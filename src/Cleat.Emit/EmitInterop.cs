using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace Cleat.Emit;

// 互操作指令的决议（designs/05-cil-backend.md §5；从 IronScheme 移植）。
// 两个运行时（LCIL.md §2 的 runtime 子句，designs/15 §1）各有一套规则，按方法选择：
//   clojure：类型名经 Cleat.Core.Runtime.TypeResolver 解析，运行时决议与值转换由 Cleat.Core.Runtime.Reflector 完成（void → nil）；
//   scheme ：类型名经 Cleat.Scheme.TypeResolver（含别名、尖括号泛型）解析，运行时决议与值转换由 Cleat.Scheme.Interop 完成
//            （void → 未指定值，字符串规范化为宽串）。
// 成员唯一时静态生成调用，参数经 Convert<T>、返回值经 ConvertFrom 转换；
// 不唯一、类型在运行时才知道、索引器、params 数组、值类型实例等情况，生成对运行时 Call / GetField / … 的调用，由运行时决议。
public sealed partial class LcilEmitter
{
    private sealed class CoreApi
    {
        public readonly bool IsScheme;
        public MethodInfo Resolve, Call, GetField, SetField, New, NewArray, Cast, Is, ConvertGeneric, ConvertFrom;
        // 只有 Scheme 运行时有：返回值的字符串规范化、未指定值、泛型类型名说明用的符号与表
        public MethodInfo NormalizeString;
        public FieldInfo Unspecified;
        public Func<string, object> Intern;
        public Func<object[], object> MakeList;

        private CoreApi(bool scheme) { IsScheme = scheme; }

        private static MethodInfo Need(Type t, string name, Type[] ps) => t.GetMethod(name, ps) ?? throw new LcilException($"{t.FullName}.{name} not found in {t.Assembly.GetName().Name}");

        private void Common(Type interop, Type resolver, Type resolveSpec)
        {
            Resolve = Need(resolver, "Resolve", new[] { resolveSpec, typeof(IEnumerable<string>) });
            Call = Need(interop, "Call", new[] { typeof(Type), typeof(string), typeof(object), typeof(object[]), typeof(Type[]), typeof(Type[]) });
            GetField = Need(interop, "GetField", new[] { typeof(Type), typeof(string), typeof(object) });
            SetField = Need(interop, "SetField", new[] { typeof(Type), typeof(string), typeof(object), typeof(object) });
            New = Need(interop, "New", new[] { typeof(Type), typeof(object[]) });
            NewArray = Need(interop, "NewArray", new[] { typeof(Type), typeof(object) });
            Cast = Need(interop, "Cast", new[] { typeof(Type), typeof(object) });
            Is = Need(interop, "Is", new[] { typeof(Type), typeof(object) });
            ConvertGeneric = Need(interop, "Convert", new[] { Type.MakeGenericMethodParameter(0) });
            ConvertFrom = Need(interop, "ConvertFrom", new[] { typeof(object), typeof(Type) });
        }

        public static CoreApi Clojure(Assembly core)
        {
            var api = new CoreApi(false);
            api.Common(core.GetType("Cleat.Core.Runtime.Reflector", true), core.GetType("Cleat.Core.Runtime.TypeResolver", true), typeof(string));
            return api;
        }

        public static CoreApi Scheme(Assembly scheme)
        {
            var api = new CoreApi(true);
            api.Common(scheme.GetType("Cleat.Scheme.Interop", true), scheme.GetType("Cleat.Scheme.TypeResolver", true), typeof(object));
            api.NormalizeString = Need(scheme.GetType("Cleat.Scheme.SchemeString", true), "Normalize", new[] { typeof(string) });
            api.Unspecified = scheme.GetType("Cleat.Scheme.Unspecified", true).GetField("Instance");
            var intern = Need(scheme.GetType("Cleat.Scheme.Symbol", true), "Intern", new[] { typeof(string) });
            api.Intern = s => intern.Invoke(null, new object[] { s });
            var fromArray = Need(scheme.GetType("Cleat.Scheme.Cons", true), "FromArray", new[] { typeof(object[]) });
            api.MakeList = xs => fromArray.Invoke(null, new object[] { xs });
            return api;
        }
    }

    private CoreApi _clojureApi, _schemeApi;

    /// <summary>方法所属运行时的互操作 API（按需加载对应的运行时程序集）。</summary>
    private CoreApi Core(BodyContext cx) =>
      cx.Runtime == LcilRuntime.Scheme
        ? _schemeApi ??= CoreApi.Scheme(LoadReference(SchemeAssemblyName))
        : _clojureApi ??= CoreApi.Clojure(LoadReference(CoreAssemblyName));

    private static readonly MethodInfo s_getTypeFromHandle = typeof(Type).GetMethod("GetTypeFromHandle");
    private static readonly MethodInfo s_objectGetType = typeof(object).GetMethod("GetType");

    /// <summary>
    /// 互操作指令的类型操作数：本 Lcil 中定义的类型，否则经所属运行时的 TypeResolver 按方法的命名空间列表解析
    /// （clojure：ClojureCLR 的类型名语法，含泛型与数组；scheme：IronScheme 的别名表，尖括号泛型转成符号说明）。
    /// </summary>
    private Type ClrType(string name, BodyContext cx, Instr ins)
    {
        if (_types.TryGetValue(name, out var tb)) return tb;
        var api = Core(cx);
        var t = (Type)api.Resolve.Invoke(null, new object[] { api.IsScheme ? GenericSpec(name, api) : name, cx.Namespaces });
        return t ?? throw Err(ins.Pos, "type not found: " + name);
    }

    /// <summary>
    /// Scheme 运行时：泛型类型名 "Name&lt;A,B&lt;C&gt;&gt;" → TypeResolver 的说明 (Name A (B C))（符号组成的 Cons）；其余原样返回字符串。
    /// </summary>
    private static object GenericSpec(string name, CoreApi api)
    {
        if (name.IndexOf('<') < 0) return name;
        int pos = 0;
        object Parse()
        {
            int start = pos;
            while (pos < name.Length && name[pos] != '<' && name[pos] != ',' && name[pos] != '>') pos++;
            var head = name.Substring(start, pos - start).Trim();
            if (pos >= name.Length || name[pos] != '<') return head;
            pos++;
            var items = new List<object> { api.Intern(head) };
            while (true)
            {
                var a = Parse();
                items.Add(a is string s ? api.Intern(s) : a);
                if (pos >= name.Length) throw new LcilException("bad generic type name: " + name);
                char c = name[pos++];
                if (c == '>') break;
                if (c != ',') throw new LcilException("bad generic type name: " + name);
            }
            return api.MakeList(items.ToArray());
        }
        return Parse();
    }

    private LocalBuilder Temp(BodyContext cx, int i)
    {
        while (cx.Temps.Count <= i) cx.Temps.Add(cx.IL.DeclareLocal(typeof(object)));
        return cx.Temps[i];
    }

    private static void EmitType(ILGenerator il, Type t)
    {
        il.Emit(OpCodes.Ldtoken, t);
        il.Emit(OpCodes.Call, s_getTypeFromHandle);
    }

    /// <summary>栈顶 object → t（t 为 object 时不转换）。</summary>
    private static void ConvertTo(ILGenerator il, CoreApi api, Type t)
    {
        if (t != typeof(object)) il.Emit(OpCodes.Call, api.ConvertGeneric.MakeGenericMethod(t));
    }

    /// <summary>
    /// 栈顶类型为 t 的值 → 运行时的表示。clojure：void → nil，long / double / bool / char 只装箱，其余值类型与 object 按运行时规则转换（int → long 等）。
    /// scheme：void → 未指定值，字符串规范化（含代理码元的转为宽串，iron-055），值类型装箱后按运行时规则转换。
    /// </summary>
    private static void ConvertFrom(ILGenerator il, CoreApi api, Type t)
    {
        if (t == typeof(void))
        {
            if (api.IsScheme) il.Emit(OpCodes.Ldsfld, api.Unspecified); else il.Emit(OpCodes.Ldnull);
            return;
        }
        if (t == typeof(object) || t == typeof(ValueType))
        {
            // 声明为 object 的值按运行时类型转换（装箱的 Int64 等）
            EmitType(il, typeof(object));
            il.Emit(OpCodes.Call, api.ConvertFrom);
            return;
        }
        if (api.IsScheme && t == typeof(string)) { il.Emit(OpCodes.Call, api.NormalizeString); return; }
        if (!t.IsValueType) return;   // 其他引用类型 ConvertFrom 不改变值
        if (!api.IsScheme && (t == typeof(long) || t == typeof(double) || t == typeof(bool) || t == typeof(char))) { il.Emit(OpCodes.Box, t); return; }   // 本就是 Clojure 的表示
        il.Emit(OpCodes.Box, t);
        EmitType(il, t);
        il.Emit(OpCodes.Call, api.ConvertFrom);
    }

    /// <summary>把临时变量 from..from+n-1 装进 object[]。</summary>
    private void EmitArgArray(BodyContext cx, int from, int n)
    {
        var il = cx.IL;
        EmitInt(il, n);
        il.Emit(OpCodes.Newarr, typeof(object));
        for (int i = 0; i < n; i++)
        {
            il.Emit(OpCodes.Dup);
            EmitInt(il, i);
            il.Emit(OpCodes.Ldloc, Temp(cx, from + i));
            il.Emit(OpCodes.Stelem_Ref);
        }
    }

    private void EmitTypeArray(ILGenerator il, Type[] ts)
    {
        EmitInt(il, ts.Length);
        il.Emit(OpCodes.Newarr, typeof(Type));
        for (int i = 0; i < ts.Length; i++)
        {
            il.Emit(OpCodes.Dup);
            EmitInt(il, i);
            EmitType(il, ts[i]);
            il.Emit(OpCodes.Stelem_Ref);
        }
    }

    private static bool NoByRef(ParameterInfo[] ps) => !ps.Any(p => p.ParameterType.IsByRef || p.ParameterType.IsPointer);

    private static bool Simple(ParameterInfo[] ps) =>
      !ps.Any(p => p.ParameterType.IsByRef || p.ParameterType.IsPointer) &&
      !(ps.Length > 0 && ps[^1].IsDefined(typeof(ParamArrayAttribute), false));

    /// <summary>可静态决议的方法：显式参数类型时按类型精确匹配；否则该名字只有一个重载且参数个数相符。</summary>
    private static MethodInfo StaticMethod(Type t, string member, bool isStatic, int argc, Type[] ptypes)
    {
        if (member == "get_Item" || member == "set_Item")
        {
            // 数组下标由运行时处理；其余按 DefaultMember 映射到实际的索引器名（iron-033：hashtable-ref 等热点）
            if (t.IsArray) return null;
            var dm = t.GetCustomAttribute<DefaultMemberAttribute>();
            if (dm == null) return null;
            member = member.Substring(0, 4) + dm.MemberName;
        }
        if (!isStatic && t.IsValueType) return null;
        var flags = BindingFlags.Public | (isStatic ? BindingFlags.Static : BindingFlags.Instance) | BindingFlags.FlattenHierarchy;
        var all = t.GetMember(member, MemberTypes.Method, flags).Cast<MethodInfo>().Where(m => !m.ContainsGenericParameters).ToList();
        // 显式参数类型时 params 数组参数按数组整体传入（如 (Concat String[])），与运行时一致；原先也交给运行时，string-append 因此是热点（iron-033）
        if (ptypes != null)
            return all.FirstOrDefault(m => m.GetParameters().Select(p => p.ParameterType).SequenceEqual(ptypes) && NoByRef(m.GetParameters()));
        // 按参数个数过滤（04 §5 规则 2）：能接受 argc 个实参的重载（含 params 数组重载）恰好一个、且是普通签名时静态调用。
        // 原先要求同名只有一个重载，String.Substring(int)/(int,int) 这类按个数即可区分的调用也落到运行时（iron-033）
        var fit = all.Where(m =>
        {
            var p = m.GetParameters();
            return p.Length == argc || (p.Length > 0 && p.Length - 1 <= argc && p[^1].IsDefined(typeof(ParamArrayAttribute), false));
        }).ToList();
        if (fit.Count != 1) return null;
        var ps = fit[0].GetParameters();
        return ps.Length == argc && Simple(ps) ? fit[0] : null;
    }

    private void Clr(BodyContext cx, Instr ins)
    {
        var il = cx.IL;
        var api = Core(cx);
        switch (ins.ClrOp)
        {
            case "call": ClrCall(cx, ins); return;
            case "field-get": ClrFieldGet(cx, ins); return;
            case "field-set": ClrFieldSet(cx, ins); return;
            case "new": ClrNew(cx, ins); return;
            case "new-array":
                {
                    var t = ClrType(ins.ClrType, cx, ins);
                    var size = Temp(cx, 0);
                    il.Emit(OpCodes.Stloc, size);
                    EmitType(il, t);
                    il.Emit(OpCodes.Ldloc, size);
                    il.Emit(OpCodes.Call, api.NewArray);
                    return;
                }
            case "cast":
            case "is":
                {
                    var t = ClrType(ins.ClrType, cx, ins);
                    var v = Temp(cx, 0);
                    il.Emit(OpCodes.Stloc, v);
                    EmitType(il, t);
                    il.Emit(OpCodes.Ldloc, v);
                    il.Emit(OpCodes.Call, ins.ClrOp == "cast" ? api.Cast : api.Is);
                    return;
                }
            case "type": EmitType(il, ClrType(ins.ClrType, cx, ins)); return;
        }
        throw Err(ins.Pos, "unknown interop instruction clr-" + ins.ClrOp);
    }

    // 栈：[实例] 参数 1..n → 临时变量：0 = 实例，1..n = 参数
    private void SpillArgs(BodyContext cx, int n, bool hasInstance)
    {
        for (int i = n; i >= 1; i--) cx.IL.Emit(OpCodes.Stloc, Temp(cx, i));
        if (hasInstance) cx.IL.Emit(OpCodes.Stloc, Temp(cx, 0));
    }

    private void ClrCall(BodyContext cx, Instr ins)
    {
        var il = cx.IL;
        var api = Core(cx);
        int n = ins.ClrArgc;
        SpillArgs(cx, n, !ins.ClrStatic);
        Type t = ins.ClrType == null ? null : ClrType(ins.ClrType, cx, ins);
        Type[] ptypes = ins.ClrParams?.Select(p => ClrType(p, cx, ins)).ToArray();
        if (ptypes != null && ptypes.Length != n) throw Err(ins.Pos, "explicit parameter types do not match argument count");
        Type[] gtypes = ins.ClrGenerics?.Select(g => ClrType(g, cx, ins)).ToArray();
        var target = t == null || gtypes != null ? null : StaticMethod(t, ins.ClrMember, ins.ClrStatic, n, ptypes);   // 泛型方法交给运行时
        if (target == null && t != null && gtypes == null && ptypes == null && ins.ClrHints != null && ins.ClrHints.Count == n)
            target = StaticMethod(t, ins.ClrMember, ins.ClrStatic, n, ins.ClrHints.Select(h => ClrType(h, cx, ins)).ToArray());
        if (target != null)
        {
            if (!ins.ClrStatic) { il.Emit(OpCodes.Ldloc, Temp(cx, 0)); ConvertTo(il, api, t); }
            var ps = target.GetParameters();
            for (int i = 0; i < n; i++) { il.Emit(OpCodes.Ldloc, Temp(cx, i + 1)); ConvertTo(il, api, ps[i].ParameterType); }
            il.Emit(ins.ClrStatic || target.DeclaringType.IsValueType ? OpCodes.Call : OpCodes.Callvirt, target);
            ConvertFrom(il, api, target.ReturnType);
            return;
        }
        // 运行时决议：Call(type, member, instance, args, paramTypes, genericArgs)
        if (t != null) EmitType(il, t);
        else { il.Emit(OpCodes.Ldloc, Temp(cx, 0)); il.Emit(OpCodes.Callvirt, s_objectGetType); }
        il.Emit(OpCodes.Ldstr, ins.ClrMember);
        if (ins.ClrStatic) il.Emit(OpCodes.Ldnull); else il.Emit(OpCodes.Ldloc, Temp(cx, 0));
        EmitArgArray(cx, 1, n);
        if (ptypes != null) EmitTypeArray(il, ptypes); else il.Emit(OpCodes.Ldnull);
        if (gtypes != null) EmitTypeArray(il, gtypes); else il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Call, api.Call);
    }

    private FieldInfo StaticField(Type t, string name, bool isStatic) =>
      t == null || (!isStatic && t.IsValueType) ? null
        : t.GetField(name, BindingFlags.Public | (isStatic ? BindingFlags.Static : BindingFlags.Instance) | BindingFlags.FlattenHierarchy);

    private void ClrFieldGet(BodyContext cx, Instr ins)
    {
        var il = cx.IL;
        var api = Core(cx);
        SpillArgs(cx, 0, !ins.ClrStatic);
        Type t = ins.ClrType == null ? null : ClrType(ins.ClrType, cx, ins);
        var f = StaticField(t, ins.ClrMember, ins.ClrStatic);
        if (f != null && f.IsLiteral && EmitLiteral(il, api, f.GetRawConstantValue(), f.FieldType)) return;
        if (f != null && !f.IsLiteral)
        {
            if (ins.ClrStatic) il.Emit(OpCodes.Ldsfld, f);
            else { il.Emit(OpCodes.Ldloc, Temp(cx, 0)); ConvertTo(il, api, t); il.Emit(OpCodes.Ldfld, f); }
            ConvertFrom(il, api, f.FieldType);
            return;
        }
        if (t != null) EmitType(il, t);
        else { il.Emit(OpCodes.Ldloc, Temp(cx, 0)); il.Emit(OpCodes.Callvirt, s_objectGetType); }
        il.Emit(OpCodes.Ldstr, ins.ClrMember);
        if (ins.ClrStatic) il.Emit(OpCodes.Ldnull); else il.Emit(OpCodes.Ldloc, Temp(cx, 0));
        il.Emit(OpCodes.Call, api.GetField);
    }

    /// <summary>常量字段（const）直接内联值；不认识的类型返回 false，交给运行时。</summary>
    private static bool EmitLiteral(ILGenerator il, CoreApi api, object v, Type ft)
    {
        switch (v)
        {
            case string s: il.Emit(OpCodes.Ldstr, s); break;
            case int i when !ft.IsEnum: EmitInt(il, i); break;
            case long l: il.Emit(OpCodes.Ldc_I8, l); break;
            case double d: il.Emit(OpCodes.Ldc_R8, d); break;
            case float fl: il.Emit(OpCodes.Ldc_R4, fl); break;
            case bool b: EmitInt(il, b ? 1 : 0); break;
            case char c: EmitInt(il, c); break;
            case short s16: EmitInt(il, s16); break;
            case ushort u16: EmitInt(il, u16); break;
            case byte u8: EmitInt(il, u8); break;
            case sbyte s8: EmitInt(il, s8); break;
            default: return false;
        }
        ConvertFrom(il, api, ft);
        return true;
    }

    private void ClrFieldSet(BodyContext cx, Instr ins)
    {
        var il = cx.IL;
        var api = Core(cx);
        SpillArgs(cx, 1, !ins.ClrStatic);
        Type t = ins.ClrType == null ? null : ClrType(ins.ClrType, cx, ins);
        var f = StaticField(t, ins.ClrMember, ins.ClrStatic);
        if (f != null && !f.IsLiteral && !f.IsInitOnly)
        {
            if (!ins.ClrStatic) { il.Emit(OpCodes.Ldloc, Temp(cx, 0)); ConvertTo(il, api, t); }
            il.Emit(OpCodes.Ldloc, Temp(cx, 1));
            ConvertTo(il, api, f.FieldType);
            il.Emit(ins.ClrStatic ? OpCodes.Stsfld : OpCodes.Stfld, f);
            ConvertFrom(il, api, typeof(void));   // 结果：clojure 为 nil，scheme 为未指定值
            return;
        }
        if (t != null) EmitType(il, t);
        else { il.Emit(OpCodes.Ldloc, Temp(cx, 0)); il.Emit(OpCodes.Callvirt, s_objectGetType); }
        il.Emit(OpCodes.Ldstr, ins.ClrMember);
        if (ins.ClrStatic) il.Emit(OpCodes.Ldnull); else il.Emit(OpCodes.Ldloc, Temp(cx, 0));
        il.Emit(OpCodes.Ldloc, Temp(cx, 1));
        il.Emit(OpCodes.Call, api.SetField);
    }

    private void ClrNew(BodyContext cx, Instr ins)
    {
        var il = cx.IL;
        var api = Core(cx);
        int n = ins.ClrArgc;
        SpillArgs(cx, n, false);
        var t = ClrType(ins.ClrType, cx, ins);
        var ctors = t.GetConstructors();
        ConstructorInfo ctor = null;
        if (!t.IsValueType && ctors.Length == 1 && ctors[0].GetParameters().Length == n && Simple(ctors[0].GetParameters()))
            ctor = ctors[0];
        else if (!t.IsValueType && ins.ClrHints != null && ins.ClrHints.Count == n)
        {
            var hs = ins.ClrHints.Select(h => ClrType(h, cx, ins)).ToArray();
            // 提示类型与参数类型完全相同：params 数组参数收到的就是数组，整体传入（与运行时一致）
            ctor = ctors.FirstOrDefault(c => c.GetParameters().Select(p => p.ParameterType).SequenceEqual(hs) && NoByRef(c.GetParameters()));
        }
        if (ctor != null)
        {
            var ps = ctor.GetParameters();
            for (int i = 0; i < n; i++) { il.Emit(OpCodes.Ldloc, Temp(cx, i + 1)); ConvertTo(il, api, ps[i].ParameterType); }
            il.Emit(OpCodes.Newobj, ctor);
            return;
        }
        EmitType(il, t);
        EmitArgArray(cx, 1, n);
        il.Emit(OpCodes.Call, api.New);
    }
}
