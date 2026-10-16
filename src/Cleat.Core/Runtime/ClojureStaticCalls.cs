using System;
using System.Linq;
using System.Reflection;
using Cleat.Core.Compiler;

namespace Cleat.Core.Runtime;

/// <summary>
/// 宿主方法调用的编译期静态决议（cleat-042 第 3 项）。<c>(. T (m a …))</c> 原先一律经 <see cref="ClojureOps.CallStatic"/> 走运行时的
/// <see cref="Reflector"/>：每次调用分配实参数组与缓存键、查表、经委托调用，<c>:inline</c> 的 <c>nth</c> / <c>rem</c> / <c>long</c> 等热点
/// 每次约 100 ns 以上。这里按 JVM Clojure 的规则在编译期选定重载，codegen 直接发 <c>call</c>：
/// <list type="bullet">
/// <item>该名字、该参数个数的（非泛型、无 ref/params 的）公共方法只有一个时选它（clojure.lang 的类型不看实参类型，同 JVM），实参按 Reflector 的规则转换（<see cref="Reflector.Convert{T}"/>）；</item>
/// <item>有多个时，实参类型静态未知（object），只选形参全为 object 的那一个（<c>Numbers.add(object, object)</c>）；</item>
/// <item>否则交回运行时决议。</item>
/// </list>
/// 决议任何能按名字引用的公共类型（含 eval 生成的 deftype / 协议接口）；重载有歧义时（如未知实参的 StringBuilder.Append）交回运行时。
/// 返回值的转换与 Reflector 的 ToClojure 相同。
/// </summary>
public static class ClojureStaticCalls
{
    private static readonly Assembly s_core = typeof(Reflector).Assembly;

    /// <summary>codegen 能直接写出转换的值类型形参</summary>
    private static bool ValueParamOk(Type p) =>
        p == typeof(int) || p == typeof(long) || p == typeof(double) || p == typeof(bool) || p == typeof(char)
        // cleat-111：较窄的数值类型（(byte x) 的 RT.byteCast、byte[] 的 aset 等）；值类型形参只匹配静态类型完全相同的实参，转换同 Reflector
        || p == typeof(byte) || p == typeof(sbyte) || p == typeof(short) || p == typeof(ushort) || p == typeof(float);

    /// <summary>能按名字（HostSyntax.TypeName）在 Lcil 中引用的类型：非泛型、非嵌套的公共类型，不是 deftype 的桩</summary>
    private static bool Nameable(Type t) =>
        t.IsArray
            ? t.IsSZArray && ArrayElementNameable(t.GetElementType())   // 一维数组（cleat-101 的延续：RT.aset / aget 的 long[] … 重载）
            : t.IsPublic && !t.IsNested && !t.IsGenericType && !t.IsByRef && !t.IsPointer && !HostSyntax.IsStub(t)
              && !(t is System.Reflection.Emit.TypeBuilder);

    /// <summary>数组的元素类型：原始类型、object、string，或 BCL / Core 里能按名字引用的类型（Lcil 写作 "元素全名[]"）</summary>
    private static bool ArrayElementNameable(Type e) =>
        e.IsPrimitive || e == typeof(object) || e == typeof(string)
        || (!e.IsArray && Nameable(e) && (e.Assembly == s_core || e.Assembly == typeof(object).Assembly));

    /// <summary>
    /// 形参类型：object、五种原始类型，能按名字引用的引用类型（发 castclass，JVM 的 checkcast；cleat-101 起不再限于 String 与 Core 的类型）；
    /// 不碰委托（Reflector 把 IFn 转成委托）、数组（Reflector 做元素类型的协变）、枚举与 Nullable（值类型，Reflector 按名字 / 数字转换）
    /// </summary>
    private static bool ParamOk(Type p, bool core) =>
        p == typeof(object) || ValueParamOk(p)
        || (!p.IsValueType && Nameable(p) && !typeof(Delegate).IsAssignableFrom(p));   // cleat-101：任何能按名字引用的引用类型都 castclass

    private static bool ReturnOk(Type r) =>
        r == typeof(void) || r == typeof(object) || ValueParamOk(r) || (!r.IsValueType && Nameable(r));

    /// <summary>
    /// 编译期决议 t 的 name 方法（isStatic 为真时静态方法）。argTypes：每个实参的静态类型名（数字 / 字符串字面量、
    /// 推断为 long 的变量），未知为 #f。可决议时返回 [声明类型名, 成员名, 形参类型名数组, 返回类型名, 是否虚调用, "method" | "field" | "volatile-field"]，否则返回空数组。
    /// 规则同 JVM Clojure 的 Compiler.paramArgTypeMatch / subsumes：静态类型未知的实参只匹配非值类型形参；
    /// 有多个匹配时选与已知实参类型精确相符最多的那个，并列则交回运行时。
    /// </summary>
    // 推断的不动点会对同一调用反复决议（cleat-042）：按（类型, 名字, 实参类型, 静态?）缓存。桩类型不缓存（同名重定义时换新桩）
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(Type, string, string, bool), object[]> s_resolved = new();

    public static object[] Resolve(object t, object name, object argTypes, object isStatic)
    {
        // aget 的 :inline 是 RT.agetOnArray(Array, int)（不分元素类型，结果装箱）。数组类型已知时改用类型化的 RT.aget(T[], int)：
        // long / double / object / bool / char 的 Clojure 表示就是自己；int / short / byte / sbyte / ushort / float（cleat-118）的类型化结果
        // 按静态调用的返回值规则加宽成 long / double——与 agetOnArray 的结果经 RetObject 规范化后相同（(aget (int-array 1) 0) 一直是 Int64），
        // 而原始用途不再经 Array.GetValue 与装箱。uint / ulong / decimal 不改（返回值规则与运行时的不同）
        if (t is Type rt && rt == typeof(clojure.lang.RT) && name is "agetOnArray" && argTypes is object[] { Length: 2 } ats0
            && ats0[0] is string an && Type.GetType(an) is Type at && at.IsSZArray && s_agetElements.Contains(at.GetElementType()))
            return Resolve(t, "aget", argTypes, isStatic);
        if (t is not Type ty || name is not string nm || HostSyntax.IsStub(ty)) return ResolveUncached(t, name, argTypes, isStatic);
        var key = (ty, nm, string.Join(",", ((object[])argTypes).Select(a => a as string ?? "?")), isStatic is bool sb && sb);
        return s_resolved.GetOrAdd(key, _ => ResolveUncached(t, name, argTypes, isStatic));
    }

    /// <summary>
    /// 选中的方法在静态类型未知的实参位置上是数组形参：不静态决议，交给运行时（cleat-106）。数组的元素类型决定该选哪个重载
    /// （RT.aset 的 long[] / object[] …、MemoryStream(int) / MemoryStream(byte[])），未知时静态选出的那个可能是错的。
    /// 数组形参照常参与匹配（保持歧义），只是不让未知实参落在它上面。
    /// </summary>
    private static bool UnknownAtArray(Type[] pts, Type[] ats) =>
        Enumerable.Range(0, pts.Length).Any(i => ats[i] == null && pts[i].IsArray);

    private static readonly Type[] s_agetElements =
        { typeof(long), typeof(double), typeof(object), typeof(bool), typeof(char),
          typeof(int), typeof(short), typeof(byte), typeof(sbyte), typeof(ushort), typeof(float) };

    /// <summary>求值生成的程序集：动态程序集（RunAndCollect），或从字节装载、没有文件位置的（cleat-114 的分层编译）</summary>
    private static bool IsEvalAssembly(System.Reflection.Assembly a) => a.IsDynamic || string.IsNullOrEmpty(a.Location);

    private static object[] ResolveUncached(object t, object name, object argTypes, object isStatic)
    {
        if (t is not Type type || name is not string member) return Array.Empty<object>();
        // deftype 方法体里的 this（桩类型）：在 deftype 声明的接口里找，callvirt 接口方法（真正的类型实现它们）
        Type[] stubIfaces = HostSyntax.IsStub(type) ? HostSyntax.StubInterfaces(type) : null;
        if (stubIfaces == null && !Nameable(type)) return Array.Empty<object>();
        Type[] ats = ((object[])argTypes).Select(a => a is string s ? Type.GetType(s) : null).ToArray();
        int n = ats.Length;
        bool st = isStatic is bool b && b;
        if (!st && type.IsValueType || stubIfaces != null && st) return Array.Empty<object>();
        var flags = BindingFlags.Public | (st ? BindingFlags.Static : BindingFlags.Instance) | (st ? BindingFlags.DeclaredOnly : BindingFlags.FlattenHierarchy);
        var fit = type.GetMember(member, MemberTypes.Method, flags).Cast<MethodInfo>()
            .Where(m => !m.ContainsGenericParameters && !m.IsGenericMethodDefinition)
            .Where(m =>
            {
                var ps = m.GetParameters();
                return ps.Length == n && !ps.Any(p => p.ParameterType.IsByRef || p.ParameterType.IsPointer || p.IsDefined(typeof(ParamArrayAttribute), false));
            })
            .ToList();
        if (stubIfaces != null)
            fit = stubIfaces.Concat(stubIfaces.SelectMany(i => i.GetInterfaces())).Distinct().SelectMany(i => i.GetMethods())
                .Where(m => m.Name == member && m.GetParameters().Length == n && !m.ContainsGenericParameters
                            && !m.GetParameters().Any(p => p.ParameterType.IsByRef || p.ParameterType.IsPointer))
                .ToList();
        // 接口继承的方法不在 FlattenHierarchy 里
        else if (!st && fit.Count == 0 && type.IsInterface)
            fit = type.GetInterfaces().SelectMany(i => i.GetMethods())
                .Where(m => m.Name == member && m.GetParameters().Length == n && !m.ContainsGenericParameters).ToList();
        // Cleat.Core（clojure.lang）按 JVM 规则；其余类型的重载按运行时实参类型选，只有唯一相容的重载时才静态决议
        bool core = stubIfaces == null ? type.Assembly == s_core : fit.Count > 0 && fit.All(m => m.DeclaringType.Assembly == s_core);
        var matching = fit.Where(m => m.GetParameters().Select((p, i) => Match(p.ParameterType, ats[i], core)).All(x => x)).ToList();
        MethodInfo chosen = null;
        // JVM：该参数个数的方法只有一个时直接用它（实参按形参转换，RT.nth(object, int) 的 int 实参），不看类型匹配
        if (core && fit.Count == 1) chosen = fit[0];
        else if (matching.Count == 1) chosen = matching[0];
        else if (core && matching.Count > 1)
        {
            // 按已知实参的精确相符数挑——只有静态类型未知的位置上各候选的形参都一样时才可靠（运行时的实参类型不会改变选择）；
            // 否则 (aset arr i v) 在 arr 类型未知时会挑中 long[] 的重载（cleat-101 的延续：数组形参可决议之后暴露）
            int Score(MethodInfo m) => m.GetParameters().Select((p, i) => ats[i] != null && p.ParameterType == ats[i] ? 1 : 0).Sum();
            int best = matching.Max(Score);
            var top = matching.Where(m => Score(m) == best).ToList();
            bool unknownAgree = Enumerable.Range(0, n).Where(i => ats[i] == null)
                .All(i => matching.Select(m => m.GetParameters()[i].ParameterType).Distinct().Count() == 1);
            // 并列时取在已知实参位置上形参更具体的那个（值类型形参优于 object，JVM 的 subsumes）：add(double, double) 优于 add(double, object)
            if (top.Count > 1)
            {
                bool Subsumes(MethodInfo a, MethodInfo b) => Enumerable.Range(0, n).All(i =>
                {
                    var pa = a.GetParameters()[i].ParameterType; var pb = b.GetParameters()[i].ParameterType;
                    return pa == pb || ats[i] != null && pb == typeof(object) && pa.IsValueType;
                });
                var most = top.Where(m => top.All(o => o == m || Subsumes(m, o))).ToList();
                if (most.Count == 1) top = most;
            }
            if (top.Count == 1 && unknownAgree) chosen = top[0];
        }
        // 其余类型（cleat-043，按 CLR / C# 的做法）：有多个相容的重载时，取形参与全部已知实参类型逐一相同的那一个——
        // 只在静态类型未知的位置上各重载的形参都一样时（运行时的实参类型不会改变选择），例如 gen-class 的
        // foo(Object, Int32) 与 foo(Object, Object) 对 (.foo x o (int 1))
        else if (!core && matching.Count > 1 && ats.Any(a => a != null))
        {
            var exact = matching.Where(m => m.GetParameters().Select((p, i) => ats[i] == null || p.ParameterType == ats[i]).All(x => x)).ToList();
            bool unknownAgree = Enumerable.Range(0, n).Where(i => ats[i] == null)
                .All(i => matching.Select(m => m.GetParameters()[i].ParameterType).Distinct().Count() == 1);
            if (exact.Count == 1 && unknownAgree) chosen = exact[0];
        }
        // 没有相符的方法、无实参时：公共字段，再公共属性的 getter（Reflector 的顺序：方法优先，再字段 / 属性）。
        // 属性（cleat-101）：getter 当作普通方法决议，codegen 发 call / callvirt get_X——此前属性读一律走运行时的 Reflector（约 80 ns 一次）
        if (chosen == null && n == 0 && matching.Count == 0 && stubIfaces == null)
        {
            var fflags = BindingFlags.Public | (st ? BindingFlags.Static : BindingFlags.Instance) | BindingFlags.FlattenHierarchy;
            var field = type.GetField(member, fflags);
            if (field != null) return FieldSig(field);
            var prop = type.GetProperty(member, fflags);
            if (prop == null && type.IsInterface) prop = type.GetInterfaces().Select(i => i.GetProperty(member, fflags)).FirstOrDefault(p => p != null);
            var getter = prop?.GetGetMethod();
            if (getter == null || getter.GetParameters().Length != 0 || getter.IsStatic != st) return Array.Empty<object>();
            chosen = getter;
        }
        if (chosen == null || !Nameable(chosen.DeclaringType)) return Array.Empty<object>();
        var pts = chosen.GetParameters().Select(p => p.ParameterType).ToArray();
        if (UnknownAtArray(pts, ats)) return Array.Empty<object>();
        if (!pts.All(p => ParamOk(p, core)) || !ReturnOk(chosen.ReturnType)) return Array.Empty<object>();
        return new object[]
        {
            HostSyntax.TypeName(chosen.DeclaringType), chosen.Name,
            pts.Select(p => (object)HostSyntax.TypeName(p)).ToArray(),
            HostSyntax.TypeName(chosen.ReturnType),
            !chosen.IsStatic && !chosen.DeclaringType.IsValueType,
            "method",
            // cleat-113：声明类型是 Clojure 代码定义的（求值生成的动态程序集里的 deftype / definterface，或桩）：返回值本来就是 Clojure 的表示，
            // 生成的代码不必再经 RetObject（对引用类型的 `is not ValueType` 要沿继承链走一遍）
            chosen.ReturnType == typeof(object) && (IsEvalAssembly(chosen.DeclaringType.Assembly) || HostSyntax.IsStub(chosen.DeclaringType)),
        };
    }

    /// <summary>
    /// 构造器的编译期决议（cleat-101）：<c>(new T a …)</c> 此前一律经 <see cref="ClojureOps.NewOf"/> → <see cref="Reflector.New"/>（每次分配 CallKey 与
    /// 类型数组，约 80 ns）。规则同 <see cref="Resolve"/> 的方法：该实参个数的公共构造器（无 ref / params）只有一个时选它（实参按 Reflector 的规则转换），
    /// 多个时按实参静态类型选唯一相容的。可决议时返回 [类型名, "", 形参类型名数组, 类型名, 是否值类型, "ctor"]，否则空数组。
    /// 不决议：委托类型（Reflector 用 IFn 建委托）、抽象类型、桩类型、不能按名字引用的类型。
    /// </summary>
    public static object[] ResolveCtor(object t, object argTypes)
    {
        if (t is Type stub && HostSyntax.IsStub(stub)) return StubCtor(stub, ((object[])argTypes).Length);
        if (t is not Type type || !Nameable(type) || type.IsAbstract || type.IsInterface || typeof(Delegate).IsAssignableFrom(type)) return Array.Empty<object>();
        var key = (type, ".ctor", string.Join(",", ((object[])argTypes).Select(a => a as string ?? "?")), false);
        return s_resolved.GetOrAdd(key, _ => ResolveCtorUncached(type, (object[])argTypes));
    }

    /// <summary>
    /// 正在编译的 deftype / defrecord 的桩（同一个顶层形式里的 new，典型是 defrecord 展开出的工厂 ->R / map->R）：桩的字段就是全部字段，
    /// 生成的类有全字段的构造器；记录（末四个字段 __meta __extmap __hash __hasheq）另有基本字段与基本字段 + meta + extmap 两个。
    /// 类型名写成不带程序集的全名，发射器按名字找到本单元正在定义的类型。
    /// </summary>
    private static object[] StubCtor(Type stub, int n)
    {
        var fields = stub.GetFields(BindingFlags.Public | BindingFlags.Instance).Select(f => f.Name).ToArray();
        int all = fields.Length;
        bool record = all >= 4 && fields[^4] == "__meta" && fields[^3] == "__extmap" && fields[^2] == "__hash" && fields[^1] == "__hasheq";
        if (n != all && !(record && (n == all - 4 || n == all - 2))) return Array.Empty<object>();
        return new object[] { stub.FullName, "", Enumerable.Repeat((object)"System.Object", n).ToArray(), stub.FullName, false, "ctor" };
    }

    private static object[] ResolveCtorUncached(Type type, object[] argTypes)
    {
        Type[] ats = argTypes.Select(a => a is string s ? Type.GetType(s) : null).ToArray();
        int n = ats.Length;
        bool core = type.Assembly == s_core;
        var fit = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Where(c =>
            {
                var ps = c.GetParameters();
                return ps.Length == n && !ps.Any(p => p.ParameterType.IsByRef || p.ParameterType.IsPointer || p.IsDefined(typeof(ParamArrayAttribute), false));
            })
            .ToList();
        var matching = fit.Where(c => c.GetParameters().Select((p, i) => Match(p.ParameterType, ats[i], core)).All(x => x)).ToList();
        ConstructorInfo chosen = null;
        if (fit.Count == 1 && (core || matching.Count == 1)) chosen = fit[0];
        else if (matching.Count == 1) chosen = matching[0];
        else if (matching.Count > 1 && ats.Any(a => a != null))
        {
            var exact = matching.Where(c => c.GetParameters().Select((p, i) => ats[i] == null || p.ParameterType == ats[i]).All(x => x)).ToList();
            bool unknownAgree = Enumerable.Range(0, n).Where(i => ats[i] == null)
                .All(i => matching.Select(c => c.GetParameters()[i].ParameterType).Distinct().Count() == 1);
            if (exact.Count == 1 && unknownAgree) chosen = exact[0];
        }
        if (chosen == null) return Array.Empty<object>();
        var pts = chosen.GetParameters().Select(p => p.ParameterType).ToArray();
        if (UnknownAtArray(pts, ats)) return Array.Empty<object>();
        if (!pts.All(p => ParamOk(p, core))) return Array.Empty<object>();
        return new object[] { HostSyntax.TypeName(type), "", pts.Select(p => (object)HostSyntax.TypeName(p)).ToArray(), HostSyntax.TypeName(type), type.IsValueType, "ctor" };
    }

    /// <summary>
    /// (. x -f) / (. T -f)：t 的公共字段 name（Reflector.GetField 先找字段）。可决议时返回与 Resolve 相同格式的
    /// [声明类型名, 字段名, [], 字段类型名, false, "field" | "volatile-field"]；常量、属性、桩类型交回运行时
    /// </summary>
    public static object[] ResolveField(object t, object name, object isStatic)
    {
        if (t is not Type type || name is not string member || !Nameable(type)) return Array.Empty<object>();
        bool st = isStatic is bool b && b;
        if (!st && type.IsValueType) return Array.Empty<object>();
        var f = type.GetField(member, BindingFlags.Public | (st ? BindingFlags.Static : BindingFlags.Instance) | BindingFlags.FlattenHierarchy);
        return FieldSig(f);
    }

    private static object[] FieldSig(FieldInfo f) =>
      f != null && !f.IsLiteral && Nameable(f.DeclaringType) && ReturnOk(f.FieldType) && f.FieldType != typeof(void)
        ? new object[]
          {
              HostSyntax.TypeName(f.DeclaringType), f.Name, Array.Empty<object>(), HostSyntax.TypeName(f.FieldType), false,
              f.GetRequiredCustomModifiers().Contains(typeof(System.Runtime.CompilerServices.IsVolatile)) ? "volatile-field" : "field",
          }
        : Array.Empty<object>();

    /// <summary>(instance? T x) 的 T 能直接 isinst 时它在 Lcil 里的名字，否则 ""（桩、泛型、嵌套等交给运行时的 Reflector.Is）</summary>
    public static string IsinstName(object t) => t is Type ty && Nameable(ty) ? HostSyntax.TypeName(ty) : "";

    /// <summary>形参类型 p 能否接受静态类型为 a（null 为未知：core 时只匹配引用类型形参，否则什么都可能）的实参</summary>
    private static bool Match(Type p, Type a, bool core)
    {
        if (a == null) return !core || !p.IsValueType;
        if (p == a || p == typeof(object)) return true;
        if (p == typeof(int) && a == typeof(long)) return true;
        // cleat-118：Core 的方法接受精确的加宽——较窄的整数（含 Int32）→ Int64、Single → Double（类型化数组访问的结果进 Numbers 的算术，
        // 与这些结果在 Clojure 里的值相同：它们本来就加宽成 long / double）
        if (core && p == typeof(long) && (a == typeof(int) || a == typeof(short) || a == typeof(sbyte) || a == typeof(byte) || a == typeof(ushort))) return true;
        if (core && p == typeof(double) && a == typeof(float)) return true;
        return !p.IsValueType && !a.IsValueType && p.IsAssignableFrom(a);
    }

    // —— 实参转换（Reflector.ConvertArg 的规则：同类型直接拆箱，否则 Convert<T>）；引用类型形参由 codegen 发 castclass（同 JVM 的 checkcast）——
    public static int ArgInt32(object v) => v is int i ? i : v is long l ? clojure.lang.RT.intCast(l) : Reflector.Convert<int>(v);
    public static long ArgInt64(object v) => v is long l ? l : Reflector.Convert<long>(v);
    public static double ArgDouble(object v) => v is double d ? d : Reflector.Convert<double>(v);
    public static bool ArgBoolean(object v) => v is bool b ? b : Reflector.Convert<bool>(v);
    public static char ArgChar(object v) => v is char c ? c : Reflector.Convert<char>(v);
    public static byte ArgByte(object v) => v is byte b ? b : Reflector.Convert<byte>(v);
    public static sbyte ArgSByte(object v) => v is sbyte b ? b : Reflector.Convert<sbyte>(v);
    public static short ArgInt16(object v) => v is short b ? b : Reflector.Convert<short>(v);
    public static ushort ArgUInt16(object v) => v is ushort b ? b : Reflector.Convert<ushort>(v);
    public static float ArgSingle(object v) => v is float b ? b : Reflector.Convert<float>(v);

    // —— 原始签名的 fn（cleat-042）：JVM 的转换语义 ——
    // 实参（invoke(Object) → invokePrim）：RT.longCast / doubleCast（越界抛 IllegalArgument），只收数字——字符串、字符、关键字是
    // ClassCastException（ClojureCLR 的 longCast 会解析字符串、取字符码），nil 是 NullPointerException。
    // 返回值：Number.longValue() / doubleValue()——大整数与比值取低 64 位、浮点饱和、NaN 为 0，不报错；非数字同样 ClassCastException。

    private static Exception NotNumber(object x) =>
      new InvalidCastException($"{clojure.lang.Util.NameForType(x.GetType())} cannot be cast to Number");

    public static long PrimArgLong(object x) =>
      x is long l ? l : x == null ? throw new NullReferenceException() : clojure.lang.Util.IsNumeric(x) ? clojure.lang.RT.longCast(x) : throw NotNumber(x);

    public static double PrimArgDouble(object x) =>
      x is double d ? d : x == null ? throw new NullReferenceException() : clojure.lang.Util.IsNumeric(x) ? clojure.lang.RT.doubleCast(x) : throw NotNumber(x);

    public static long PrimRetLong(object x)
    {
        switch (x)
        {
            case long l: return l;
            case null: throw new NullReferenceException();
            case double d: return SaturatingLong(d);
            case float f: return SaturatingLong(f);
            case int i: return i;
            case short sh: return sh;
            case byte by: return by;
            case sbyte sb: return sb;
            case ushort us: return us;
            case uint ui: return ui;
            case ulong ul: return unchecked((long)ul);
            case char: throw NotNumber(x);
            case clojure.lang.BigInt b: return Low64(Sys(b.toBigInteger()));
            case System.Numerics.BigInteger bi: return Low64(bi);
            case clojure.lang.Ratio r: return Low64(System.Numerics.BigInteger.Divide(Sys(r.numerator), Sys(r.denominator)));
            case clojure.lang.BigDecimal bd: return Low64(Sys(bd.ToBigInteger()));
            case decimal m: return Low64(new System.Numerics.BigInteger(decimal.Truncate(m)));
            default: throw NotNumber(x);
        }
    }

    public static double PrimRetDouble(object x) =>
      x switch
      {
          double d => d,
          null => throw new NullReferenceException(),
          char => throw NotNumber(x),
          clojure.lang.BigInt b => b.doubleValue(),
          IConvertible c when clojure.lang.Util.IsNumeric(x) => c.ToDouble(System.Globalization.CultureInfo.InvariantCulture),
          _ when clojure.lang.Util.IsNumeric(x) => clojure.lang.RT.doubleCast(x),
          _ => throw NotNumber(x),
      };

    private static long SaturatingLong(double d) =>
      double.IsNaN(d) ? 0 : d >= 9.223372036854775807E18 ? long.MaxValue : d <= -9.223372036854775808E18 ? long.MinValue : (long)d;

    // 非 long 返回值的少见路径：经十进制文本换成 System.Numerics.BigInteger
    private static System.Numerics.BigInteger Sys(clojure.lang.BigInteger b) =>
      System.Numerics.BigInteger.Parse(b.ToString(), System.Globalization.CultureInfo.InvariantCulture);

    private static long Low64(System.Numerics.BigInteger b) => unchecked((long)(ulong)(b & ulong.MaxValue));

    // —— 返回值（Reflector.ToClojure）——
    // 绝大多数返回值已是 Clojure 的表示（long、double、引用类型）：先排除这些，只有 int 等少见的值类型才走 ConvertFrom 的类型分派（cleat-042：
    // fib 的剖析里 ConvertFrom 占 35%）
    // cleat-113：每次返回 object 的宿主调用都经过这里——内联进生成的代码，转换放到不内联的方法里
    // （long / double / bool 是密封类型，判断就是一次方法表比较；GetType().IsValueType 在这里是两次真正的调用，更慢）
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static object RetObject(object v) =>
        v is null || v is long || v is double || v is bool || v is not ValueType ? v : RetValue(v);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static object RetValue(object v) => Reflector.ConvertFrom(v, typeof(object));

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static object RetInt32(int v) => LongBox.Get(v);

    /// <summary>实例调用的实例为 nil 时与 ClojureOps.CallInstance 同样报错</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static object Instance(object inst, object name) => ClojureOps.NotNil(inst, name);
}
