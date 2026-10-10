using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using clojure.lang;

namespace Cleat.Core.Runtime;

/// <summary>
/// 运行时宿主互操作（designs/02 §8、05 §5），不用 DLR：按实参的运行时类型做重载决议，结果按
/// （类型、成员名、静态/实例、实例运行时类型、实参类型序列、显式参数类型、泛型实参）缓存为
/// System.Linq.Expressions 编译出的委托；缓存命中后不再反射（无 MethodInfo.Invoke）。
/// 公开 API 的签名与 Cleat.Emit 的 EmitInterop.CoreApi 一一对应。
/// <para>规则（从 ClojureCLR Reflector 的 IsCongruent / Subsumes / BoxArg 移植，去掉 DLR 绑定器）：</para>
/// <list type="bullet">
/// <item>匹配等级：0 精确类型；1 可赋值（含装箱到 object / 接口、null → 引用类型）；2 同类数值拓宽（int → long、float → double、char → int）、IFn → 委托；
/// 3 跨类拓宽（long → double / decimal）；4 数值收窄（long → int、double → float …）、枚举名；</item>
/// <item>逐参数支配：各参数等级都不差且至少一处更好者胜；等级相同时非 params 展开优先；再用 Subsumes（形参更具体）决胜；仍有多个则报 “More than one matching method found”；</item>
/// <item>params 数组：实参多出的部分打包；ref/out 与指针参数不支持（NotSupportedException）；</item>
/// <item>无参调用：方法 → 属性 → 字段；<c>set_X</c> 一参：属性 setter 方法 → 字段；GetField/SetField：字段 → 属性 → 无参方法；</item>
/// <item>返回值与回调参数按 Clojure 表示转换（ConvertFrom：int → long、float → double、decimal → BigDecimal、ulong 超出 long → BigInt）。</item>
/// </list>
/// </summary>
public static class Reflector
{
    #region Cache keys

    /// <summary>nil 实参在缓存键里的占位类型。</summary>
    private sealed class NullArg
    {
    }

    private static readonly Type s_nullArg = typeof(NullArg);
    private static readonly object s_true = true;
    private static readonly object s_false = false;

    private sealed class CallKey : IEquatable<CallKey>
    {
        public readonly Type Type;
        public readonly string Name;
        public readonly bool IsStatic;
        public readonly Type InstanceType;
        public readonly Type[] ArgTypes;
        public readonly Type[] ParamTypes;
        public readonly Type[] GenericArgs;
        private readonly int _hash;

        public CallKey(Type type, string name, bool isStatic, Type instanceType, Type[] argTypes, Type[] paramTypes, Type[] genericArgs)
        {
            Type = type;
            Name = name;
            IsStatic = isStatic;
            InstanceType = instanceType;
            ArgTypes = argTypes;
            ParamTypes = paramTypes;
            GenericArgs = genericArgs;
            var h = new HashCode();
            h.Add(type);
            h.Add(name);
            h.Add(isStatic);
            h.Add(instanceType);
            AddAll(ref h, argTypes);
            AddAll(ref h, paramTypes);
            AddAll(ref h, genericArgs);
            _hash = h.ToHashCode();
        }

        private static void AddAll(ref HashCode h, Type[] ts)
        {
            if (ts == null)
            {
                h.Add(-1);
                return;
            }
            h.Add(ts.Length);
            foreach (Type t in ts) h.Add(t);
        }

        private static bool Same(Type[] a, Type[] b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (!ReferenceEquals(a[i], b[i])) return false;
            }
            return true;
        }

        public bool Equals(CallKey o) =>
            o != null && _hash == o._hash && Type == o.Type && IsStatic == o.IsStatic && InstanceType == o.InstanceType
            && string.Equals(Name, o.Name, StringComparison.Ordinal)
            && Same(ArgTypes, o.ArgTypes) && Same(ParamTypes, o.ParamTypes) && Same(GenericArgs, o.GenericArgs);

        public override bool Equals(object obj) => Equals(obj as CallKey);

        public override int GetHashCode() => _hash;
    }

    private static readonly ConcurrentDictionary<CallKey, Func<object, object[], object>> s_calls = new ConcurrentDictionary<CallKey, Func<object, object[], object>>();
    private static readonly ConcurrentDictionary<CallKey, Func<object, object[], object>> s_ctors = new ConcurrentDictionary<CallKey, Func<object, object[], object>>();
    private static readonly ConcurrentDictionary<CallKey, Func<object, object[], object>> s_getters = new ConcurrentDictionary<CallKey, Func<object, object[], object>>();
    private static readonly ConcurrentDictionary<CallKey, Func<object, object[], object>> s_setters = new ConcurrentDictionary<CallKey, Func<object, object[], object>>();
    private static readonly ConcurrentDictionary<Type, Func<IFn, Delegate>> s_delegateFactories = new ConcurrentDictionary<Type, Func<IFn, Delegate>>();

    private static readonly MethodInfo s_convertMethod = typeof(Reflector).GetMethod(nameof(Convert), new[] { typeof(object) });
    private static readonly MethodInfo s_convertFromMethod = typeof(Reflector).GetMethod(nameof(ConvertFrom), new[] { typeof(object), typeof(Type) });
    private static readonly MethodInfo s_applyToMethod = typeof(IFn).GetMethod(nameof(IFn.applyTo), new[] { typeof(ISeq) });
    private static readonly MethodInfo s_arraySeqCreate = typeof(ArraySeq).GetMethod(nameof(ArraySeq.create), new[] { typeof(object[]) });

    private static Type[] ArgTypes(object[] args)
    {
        if (args.Length == 0) return Type.EmptyTypes;
        var ts = new Type[args.Length];
        for (int i = 0; i < args.Length; i++) ts[i] = args[i] == null ? s_nullArg : args[i].GetType();
        return ts;
    }

    #endregion

    #region Public API (signatures fixed by EmitInterop.CoreApi)

    /// <summary>
    /// 方法调用：<paramref name="instance"/> 非 null 为实例调用（<paramref name="type"/> 可为声明类型或实例的运行时类型），否则静态调用。
    /// <paramref name="paramTypes"/> 非 null 时按形参类型精确选重载；<paramref name="genericArgs"/> 非 null 时为泛型方法的类型实参。
    /// 无参名字也可命中属性与字段；<c>get_Item</c>/<c>set_Item</c> 经 DefaultMemberAttribute 映射到索引器；数组下标直接处理。
    /// </summary>
    public static object Call(Type type, string name, object instance, object[] args, Type[] paramTypes, Type[] genericArgs)
    {
        if (name == null) throw new ArgumentNullException(nameof(name));
        args ??= Array.Empty<object>();
        bool isStatic = instance == null;
        if (type == null)
        {
            if (isStatic) throw new ArgumentNullException(nameof(type), "static call needs a type");
            type = instance.GetType();
        }

        if (instance is Array arr && (name == "get_Item" || name == "set_Item" || name == "Get" || name == "Set"))
            return ArrayElement(arr, name[0] == 'g' || name[0] == 'G', args);

        var key = new CallKey(type, name, isStatic, instance?.GetType(), ArgTypes(args), paramTypes, genericArgs);
        if (!s_calls.TryGetValue(key, out Func<object, object[], object> fn))
            fn = s_calls.GetOrAdd(key, CompileCall);
        return fn(instance, args);
    }

    /// <summary>字段或属性读取（<c>(.-x o)</c>）；找不到时退到无参方法。instance 为 null 时取静态成员。</summary>
    public static object GetField(Type type, string name, object instance)
    {
        if (name == null) throw new ArgumentNullException(nameof(name));
        bool isStatic = instance == null;
        if (type == null)
        {
            if (isStatic) throw new ArgumentNullException(nameof(type), "static member access needs a type");
            type = instance.GetType();
        }
        var key = new CallKey(type, name, isStatic, instance?.GetType(), Type.EmptyTypes, null, null);
        if (!s_getters.TryGetValue(key, out Func<object, object[], object> fn))
            fn = s_getters.GetOrAdd(key, CompileGetter);
        return fn(instance, Array.Empty<object>());
    }

    /// <summary>字段或属性写入（<c>(set! (.x o) v)</c>）；返回 nil。</summary>
    public static object SetField(Type type, string name, object instance, object value)
    {
        if (name == null) throw new ArgumentNullException(nameof(name));
        bool isStatic = instance == null;
        if (type == null)
        {
            if (isStatic) throw new ArgumentNullException(nameof(type), "static member access needs a type");
            type = instance.GetType();
        }
        var args = new[] { value };
        var key = new CallKey(type, name, isStatic, instance?.GetType(), ArgTypes(args), null, null);
        if (!s_setters.TryGetValue(key, out Func<object, object[], object> fn))
            fn = s_setters.GetOrAdd(key, CompileSetter);
        return fn(instance, args);
    }

    /// <summary>构造：按实参类型选构造函数；无构造函数的值类型零参取默认值；委托类型接受一个 IFn。</summary>
    public static object New(Type type, object[] args)
    {
        if (type == null) throw new ArgumentNullException(nameof(type));
        args ??= Array.Empty<object>();
        if (typeof(Delegate).IsAssignableFrom(type) && args.Length == 1 && args[0] is IFn f)
            return MakeDelegate(f, type);
        var key = new CallKey(type, ".ctor", true, null, ArgTypes(args), null, null);
        if (!s_ctors.TryGetValue(key, out Func<object, object[], object> fn))
            fn = s_ctors.GetOrAdd(key, CompileCtor);
        return fn(null, args);
    }

    /// <summary>一维数组。</summary>
    public static object NewArray(Type elementType, object length)
    {
        if (elementType == null) throw new ArgumentNullException(nameof(elementType));
        return Array.CreateInstance(elementType, RT.intCast(NotNil(length, typeof(int))));
    }

    /// <summary>
    /// Clojure 的强制转换：数值目标类型按 RT 的 cast（<c>(int 3.7)</c> 截断、long → int 溢出抛 ArgumentException），
    /// 结果装箱为目标类型；枚举接受名字或整数；引用类型做类型检查，IFn 可转委托；失败抛 InvalidCastException。
    /// </summary>
    public static object Cast(Type t, object v)
    {
        if (t == null) throw new ArgumentNullException(nameof(t));
        if (t == typeof(object)) return v;
        if (t.IsValueType)
        {
            Type inner = Nullable.GetUnderlyingType(t);
            if (inner != null) return v == null ? null : Cast(inner, v);
            if (t.IsEnum) return ToEnum(t, v);
            switch (Type.GetTypeCode(t))
            {
                case TypeCode.Int64: return RT.longCast(Num(v, t));
                case TypeCode.Int32: return RT.intCast(Num(v, t));
                case TypeCode.Double: return RT.doubleCast(Num(v, t));
                case TypeCode.Single: return RT.floatCast(Num(v, t));
                case TypeCode.Int16: return RT.shortCast(Num(v, t));
                case TypeCode.Byte: return RT.byteCast(Num(v, t));
                case TypeCode.SByte: return RT.sbyteCast(Num(v, t));
                case TypeCode.UInt16: return RT.ushortCast(Num(v, t));
                case TypeCode.UInt32: return RT.uintCast(Num(v, t));
                case TypeCode.UInt64: return RT.ulongCast(Num(v, t));
                case TypeCode.Decimal: return RT.decimalCast(Num(v, t));
                case TypeCode.Char: return v is char ? v : RT.charCast(Num(v, t));
                case TypeCode.Boolean: return RT.booleanCast(v) ? s_true : s_false;
            }
            if (v != null && t.IsInstanceOfType(v)) return v;
            throw CastError(v, t);
        }
        if (v == null) return null;
        if (t.IsInstanceOfType(v)) return v;
        if (v is IFn fn && typeof(Delegate).IsAssignableFrom(t)) return MakeDelegate(fn, t);
        throw CastError(v, t);
    }

    /// <summary><c>instance?</c>：装箱的 bool。</summary>
    public static object Is(Type t, object v)
    {
        if (t == null) throw new ArgumentNullException(nameof(t));
        return v != null && t.IsInstanceOfType(v) ? s_true : s_false;
    }

    /// <summary>
    /// Clojure 值 → CLR 形参类型：数值按 RT 的 cast（拓宽与收窄、溢出报错与 RT 一致），IFn → 任意委托类型，
    /// nil → 引用类型/可空类型的 null，枚举接受 keyword/symbol/string 名字或整数，char 接受整数；其余做类型检查。
    /// 每个 T 的转换函数只构造一次。
    /// </summary>
    public static T Convert<T>(object v) => Converter<T>.Fn(v);

    /// <summary>
    /// CLR 返回值 → Clojure 表示：int/short/byte/sbyte/ushort/uint → long，ulong 超出 long → BigInt，float → double，
    /// decimal → BigDecimal；bool/char/string/long/double 与其它引用类型（含枚举）不变。<paramref name="declared"/> 为声明类型，
    /// 是已知的非 object 引用类型时直接返回。
    /// </summary>
    public static object ConvertFrom(object v, Type declared)
    {
        if (v == null) return null;
        if (declared != null && !declared.IsValueType && declared != typeof(object) && declared != typeof(ValueType)) return v;
        switch (v)
        {
            case int i: return (long)i;
            case short s: return (long)s;
            case byte b: return (long)b;
            case sbyte sb: return (long)sb;
            case ushort us: return (long)us;
            case uint ui: return (long)ui;
            case ulong ul: return ul <= long.MaxValue ? (object)(long)ul : BigInt.fromBigInteger(BigInteger.Create(ul));
            case float f: return (double)f;
            case decimal d: return BigDecimal.Create(d);
            default: return v;
        }
    }

    /// <summary>IFn → 委托（Func/Action/任意委托类型）：每个委托类型只编译一次包装；回调参数按 ConvertFrom 转为 Clojure 表示，返回值经 Convert。</summary>
    public static Delegate MakeDelegate(IFn fn, Type delegateType)
    {
        if (delegateType == null) throw new ArgumentNullException(nameof(delegateType));
        if (!typeof(Delegate).IsAssignableFrom(delegateType)) throw new ArgumentException(delegateType + " is not a delegate type", nameof(delegateType));
        if (fn == null) return null;
        if (!s_delegateFactories.TryGetValue(delegateType, out Func<IFn, Delegate> factory))
            factory = s_delegateFactories.GetOrAdd(delegateType, BuildDelegateFactory);
        return factory(fn);
    }

    #endregion

    #region Arrays

    private static object ArrayElement(Array arr, bool get, object[] args)
    {
        int rank = arr.Rank;
        int nIdx = get ? args.Length : args.Length - 1;
        if (nIdx != rank)
            throw new ArgumentException($"Array of rank {rank} needs {rank} index(es), got {nIdx}");
        if (get)
        {
            object v = rank == 1 ? arr.GetValue(RT.intCast(NotNil(args[0], typeof(int)))) : arr.GetValue(Indices(args, rank));
            return ConvertFrom(v, arr.GetType().GetElementType());
        }
        object val = ToType(arr.GetType().GetElementType(), args[nIdx]);
        if (rank == 1) arr.SetValue(val, RT.intCast(NotNil(args[0], typeof(int))));
        else arr.SetValue(val, Indices(args, rank));
        return null;
    }

    private static int[] Indices(object[] args, int n)
    {
        var ix = new int[n];
        for (int i = 0; i < n; i++) ix[i] = RT.intCast(NotNil(args[i], typeof(int)));
        return ix;
    }

    #endregion

    #region Member lookup

    private static BindingFlags Flags(bool isStatic) =>
        BindingFlags.Public | (isStatic ? BindingFlags.Static | BindingFlags.FlattenHierarchy : BindingFlags.Instance);

    /// <summary>接口的实例成员查找要连同其父接口。</summary>
    private static IEnumerable<Type> LookupTypes(Type t, bool isStatic) =>
        t.IsInterface && !isStatic ? new[] { t }.Concat(t.GetInterfaces()) : new[] { t };

    private static string MapIndexerName(Type t, string name)
    {
        if (name != "get_Item" && name != "set_Item") return name;
        var dm = t.GetCustomAttribute<DefaultMemberAttribute>(true);
        return dm == null || dm.MemberName == "Item" ? name : name.Substring(0, 4) + dm.MemberName;
    }

    private static List<MethodInfo> FindMethods(Type t, string name, bool isStatic, Type[] genericArgs)
    {
        var found = new List<MethodInfo>();
        foreach (Type lt in LookupTypes(t, isStatic))
        {
            foreach (MethodInfo m in lt.GetMethods(Flags(isStatic)))
            {
                if (!string.Equals(m.Name, name, StringComparison.Ordinal)) continue;
                if (m.IsGenericMethodDefinition)
                {
                    if (genericArgs == null || m.GetGenericArguments().Length != genericArgs.Length) continue;
                    try
                    {
                        found.Add(m.MakeGenericMethod(genericArgs));
                    }
                    catch (ArgumentException)
                    {
                        // 约束不满足
                    }
                }
                else if (genericArgs == null && !m.ContainsGenericParameters)
                {
                    found.Add(m);
                }
            }
        }
        return found;
    }

    private static PropertyInfo FindProperty(Type t, string name, bool isStatic)
    {
        foreach (Type lt in LookupTypes(t, isStatic))
        {
            PropertyInfo best = null;
            foreach (PropertyInfo p in lt.GetProperties(Flags(isStatic)))
            {
                if (p.Name != name || p.GetIndexParameters().Length != 0) continue;
                if (best == null || p.DeclaringType == lt) best = p;
            }
            if (best != null) return best;
        }
        return null;
    }

    private static FieldInfo FindField(Type t, string name, bool isStatic) => t.GetField(name, Flags(isStatic));

    #endregion

    #region Overload resolution

    private readonly struct Candidate
    {
        public readonly MethodBase Method;
        public readonly int[] Levels;
        public readonly bool Expanded;

        public Candidate(MethodBase method, int[] levels, bool expanded)
        {
            Method = method;
            Levels = levels;
            Expanded = expanded;
        }
    }

    private static bool IsNullable(Type t) => t.IsValueType && Nullable.GetUnderlyingType(t) != null;

    private static bool IsNumericCode(TypeCode c) => c >= TypeCode.SByte && c <= TypeCode.Decimal;

    private static bool IsNumericType(Type t) => !t.IsEnum && IsNumericCode(Type.GetTypeCode(t));

    /// <summary>Clojure 数值实参：CLR 数值、char 与 BigInt/BigInteger/Ratio/BigDecimal。</summary>
    private static bool IsNumericArg(Type a) =>
        IsNumericType(a) || a == typeof(char) || a == typeof(BigInt) || a == typeof(BigInteger) || a == typeof(Ratio) || a == typeof(BigDecimal);

    /// <summary>CLR 的隐式数值拓宽（含 char → 整数）。</summary>
    private static bool IsWidening(Type from, Type to)
    {
        TypeCode f = Type.GetTypeCode(from), t = Type.GetTypeCode(to);
        switch (f)
        {
            case TypeCode.SByte: return t is TypeCode.Int16 or TypeCode.Int32 or TypeCode.Int64 or TypeCode.Single or TypeCode.Double or TypeCode.Decimal;
            case TypeCode.Byte: return t is TypeCode.Int16 or TypeCode.UInt16 or TypeCode.Int32 or TypeCode.UInt32 or TypeCode.Int64 or TypeCode.UInt64 or TypeCode.Single or TypeCode.Double or TypeCode.Decimal;
            case TypeCode.Int16: return t is TypeCode.Int32 or TypeCode.Int64 or TypeCode.Single or TypeCode.Double or TypeCode.Decimal;
            case TypeCode.UInt16: return t is TypeCode.Int32 or TypeCode.UInt32 or TypeCode.Int64 or TypeCode.UInt64 or TypeCode.Single or TypeCode.Double or TypeCode.Decimal;
            case TypeCode.Int32: return t is TypeCode.Int64 or TypeCode.Single or TypeCode.Double or TypeCode.Decimal;
            case TypeCode.UInt32: return t is TypeCode.Int64 or TypeCode.UInt64 or TypeCode.Single or TypeCode.Double or TypeCode.Decimal;
            case TypeCode.Int64: return t is TypeCode.Single or TypeCode.Double or TypeCode.Decimal;
            case TypeCode.UInt64: return t is TypeCode.Single or TypeCode.Double or TypeCode.Decimal;
            case TypeCode.Char: return t is TypeCode.UInt16 or TypeCode.Int32 or TypeCode.UInt32 or TypeCode.Int64 or TypeCode.UInt64 or TypeCode.Single or TypeCode.Double or TypeCode.Decimal;
            case TypeCode.Single: return t == TypeCode.Double;
            default: return false;
        }
    }

    private static bool IsFloating(Type t) => Type.GetTypeCode(t) is TypeCode.Single or TypeCode.Double or TypeCode.Decimal;

    /// <summary>形参/实参匹配等级：0 精确，1 可赋值，2 同类拓宽/IFn → 委托，3 跨类拓宽（整数 → 浮点），4 收窄/枚举名；-1 不匹配。</summary>
    private static int MatchLevel(Type p, Type a)
    {
        if (a == s_nullArg) return !p.IsValueType || IsNullable(p) ? 1 : -1;
        if (p == a) return 0;
        if (p.IsAssignableFrom(a)) return 1;
        Type inner = Nullable.GetUnderlyingType(p);
        if (inner != null)
        {
            int l = MatchLevel(inner, a);
            return l < 0 ? -1 : Math.Max(l, 1);
        }
        if (IsNumericType(p) || p == typeof(char))
        {
            if (!IsNumericArg(a)) return -1;
            if (!IsNumericType(a) && a != typeof(char)) return 4;   // BigInt / Ratio / BigDecimal：经 RT 的 cast
            if (!IsWidening(a, p)) return 4;
            return IsFloating(p) == IsFloating(a) ? 2 : 3;
        }
        if (typeof(Delegate).IsAssignableFrom(p) && typeof(IFn).IsAssignableFrom(a)) return 2;
        if (p.IsEnum && (a == typeof(Keyword) || a == typeof(Symbol) || a == typeof(string) || IsNumericType(a))) return 4;
        if (p.IsArray && a.IsArray && p.GetArrayRank() == a.GetArrayRank() && !p.GetElementType().IsValueType && !a.GetElementType().IsValueType
            && p.GetElementType().IsAssignableFrom(a.GetElementType()))
            return 1;
        return -1;
    }

    private static bool Unsupported(ParameterInfo[] ps) =>
        ps.Any(p => p.ParameterType.IsByRef || p.ParameterType.IsPointer || p.ParameterType.IsByRefLike);

    private static bool IsParams(ParameterInfo[] ps) =>
        ps.Length > 0 && ps[ps.Length - 1].ParameterType.IsArray && ps[ps.Length - 1].IsDefined(typeof(ParamArrayAttribute), false);

    private static Candidate? Match(MethodBase m, Type[] argTypes)
    {
        ParameterInfo[] ps = m.GetParameters();
        if (Unsupported(ps)) return null;
        int n = argTypes.Length;
        if (ps.Length == n)
        {
            var levels = new int[n];
            bool ok = true;
            for (int i = 0; i < n && ok; i++)
            {
                levels[i] = MatchLevel(ps[i].ParameterType, argTypes[i]);
                ok = levels[i] >= 0;
            }
            if (ok) return new Candidate(m, levels, false);
        }
        if (IsParams(ps) && n >= ps.Length - 1)
        {
            var levels = new int[n];
            bool ok = true;
            Type elem = ps[ps.Length - 1].ParameterType.GetElementType();
            for (int i = 0; i < n && ok; i++)
            {
                levels[i] = MatchLevel(i < ps.Length - 1 ? ps[i].ParameterType : elem, argTypes[i]);
                ok = levels[i] >= 0;
            }
            if (ok) return new Candidate(m, levels, true);
        }
        return null;
    }

    /// <summary>a 严格好于 b：各参数等级都不差且至少一处更好；等级全同时非展开好于展开。</summary>
    private static bool Better(in Candidate a, in Candidate b)
    {
        bool strict = false;
        for (int i = 0; i < a.Levels.Length; i++)
        {
            if (a.Levels[i] > b.Levels[i]) return false;
            if (a.Levels[i] < b.Levels[i]) strict = true;
        }
        return strict || (!a.Expanded && b.Expanded);
    }

    /// <summary>ClojureCLR Reflector.Subsumes：c1 的形参类型比 c2 更具体。</summary>
    private static bool Subsumes(ParameterInfo[] c1, ParameterInfo[] c2)
    {
        if (c1.Length != c2.Length) return false;
        bool better = false;
        for (int i = 0; i < c1.Length; i++)
        {
            Type t1 = c1[i].ParameterType, t2 = c2[i].ParameterType;
            if (t1 != t2)
            {
                if ((!t1.IsPrimitive && t2.IsPrimitive) || t2.IsAssignableFrom(t1)) better = true;
                else return false;
            }
        }
        return better;
    }

    private static string ArgTypeNames(Type[] argTypes) =>
        string.Join(", ", argTypes.Select(t => t == s_nullArg ? "nil" : Util.NameForType(t)));

    private static string Describe(MethodBase m) =>
        (m.DeclaringType != null ? Util.NameForType(m.DeclaringType) + "." : "") + m.Name
        + "(" + string.Join(", ", m.GetParameters().Select(p => Util.NameForType(p.ParameterType))) + ")";

    /// <summary>从同名候选中选重载；没有可用者返回 null，多个不可分辨者抛错。</summary>
    private static Candidate? Select(IReadOnlyList<MethodBase> methods, Type[] argTypes, Type[] paramTypes, Type type, string what)
    {
        if (methods.Count == 0) return null;
        if (paramTypes != null)
        {
            foreach (MethodBase m in methods)
            {
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length == paramTypes.Length && !Unsupported(ps) && ps.Select(p => p.ParameterType).SequenceEqual(paramTypes))
                {
                    var levels = new int[ps.Length];
                    for (int i = 0; i < ps.Length; i++) levels[i] = MatchLevel(ps[i].ParameterType, argTypes[i]);
                    if (levels.Any(l => l < 0))
                        throw new InvalidCastException($"Arguments ({ArgTypeNames(argTypes)}) do not fit the explicit parameter types of {Describe(m)}");
                    return new Candidate(m, levels, false);
                }
            }
            throw new InvalidOperationException($"No {what} {methods[0].Name} on {Util.NameForType(type)} has parameter types ({string.Join(", ", paramTypes.Select(Util.NameForType))})");
        }

        var applicable = new List<Candidate>();
        foreach (MethodBase m in methods)
        {
            Candidate? c = Match(m, argTypes);
            if (c != null) applicable.Add(c.Value);
        }
        if (applicable.Count == 0) return null;
        if (applicable.Count == 1) return applicable[0];

        var best = applicable.Where(a => !applicable.Any(b => !ReferenceEquals(b.Method, a.Method) && Better(b, a))).ToList();
        if (best.Count == 1) return best[0];
        if (best.Count == 0) best = applicable;

        var specific = best.Where(a => best.All(b => ReferenceEquals(a.Method, b.Method) || Subsumes(a.Method.GetParameters(), b.Method.GetParameters()))).ToList();
        if (specific.Count == 1) return specific[0];

        // 同签名的成员（派生类型 new 隐藏、接口继承）：取声明类型最靠近叶子的
        Type[] sig0 = best[0].Method.GetParameters().Select(p => p.ParameterType).ToArray();
        if (best.All(b => b.Method.GetParameters().Select(p => p.ParameterType).SequenceEqual(sig0)))
        {
            foreach (Candidate a in best)
            {
                if (best.All(b => b.Method.DeclaringType.IsAssignableFrom(a.Method.DeclaringType))) return a;
            }
            return best[0];
        }

        // 只差在引用类型的形参（实参同时实现两个接口：ChunkedCons 既是 IList 又是 ISeq，PersistentHashSet.create）：
        // 与 JVM Clojure 的 Reflector 相同，取第一个（cleat-053）；数值转换上的歧义照旧报错
        if (best.All(b => b.Levels.SequenceEqual(best[0].Levels)) && best[0].Levels.All(l => l <= 1)
            && best.All(b => b.Method.GetParameters().All(p => !p.ParameterType.IsValueType)))
            return best[0];

        // 互不支配的候选里有不需要数值转换的（各参数精确或可赋值）：Util.equiv(long, Object) 与 equiv(Object, double) 对实参
        // (Int64, Double)；(Char, Int64) 时 equiv(long, long) 要把 char 拓宽，JVM 根本不认它。JVM 的 Reflector 取第一个相容的
        // 重载（后来者只在 subsumes 时替换），不报歧义；这里取第一个无需转换的（cleat-032）
        var direct = best.Where(b => b.Levels.All(l => l <= 1)).ToList();
        if (direct.Count > 0)
            return direct[0];

        throw new InvalidOperationException(
            $"More than one matching {what} found: {methods[0].Name} for type {Util.NameForType(type)} with argument types ({ArgTypeNames(argTypes)}): "
            + string.Join("; ", best.Select(c => Describe(c.Method))));
    }

    #endregion

    #region Expression building

    private static Expression Arg(ParameterExpression args, int i) => Expression.ArrayIndex(args, Expression.Constant(i));

    /// <summary>object 表达式 → 形参类型 p（实参运行时类型 a 已知时能精确匹配的直接转换，其余经 Convert&lt;P&gt;）。</summary>
    private static Expression ConvertArg(Expression e, Type p, Type a)
    {
        if (p == typeof(object)) return e;
        if (a == s_nullArg) return Expression.Default(p);
        if (p == a) return Expression.Convert(e, p);
        if (!p.IsValueType && p.IsAssignableFrom(a)) return Expression.Convert(e, p);
        return Expression.Call(s_convertMethod.MakeGenericMethod(p), e);
    }

    /// <summary>类型为 t 的表达式 → Clojure 表示（object 类型）。</summary>
    private static Expression ToClojure(Expression e, Type t)
    {
        if (t == typeof(void)) return Expression.Block(e, Expression.Constant(null, typeof(object)));
        if (t == typeof(object)) return Expression.Call(s_convertFromMethod, e, Expression.Constant(t, typeof(Type)));
        if (t == typeof(ValueType) || IsNullable(t)) return Expression.Call(s_convertFromMethod, Expression.Convert(e, typeof(object)), Expression.Constant(typeof(object), typeof(Type)));
        if (!t.IsValueType) return Expression.Convert(e, typeof(object));
        if (t == typeof(long) || t == typeof(double) || t == typeof(bool) || t == typeof(char) || t.IsEnum) return Expression.Convert(e, typeof(object));
        TypeCode c = Type.GetTypeCode(t);
        if (IsNumericCode(c)) return Expression.Call(s_convertFromMethod, Expression.Convert(e, typeof(object)), Expression.Constant(t, typeof(Type)));
        return Expression.Convert(e, typeof(object));
    }

    private static Expression Target(ParameterExpression inst, Type type, Type instanceType, MemberInfo member)
    {
        // 转成查找成员时用的类型（声明类型或实例运行时类型）；接口上的成员按接口调用（显式实现也能到）
        Type declaring = member.DeclaringType;
        Type cast = declaring != null && declaring.IsInterface ? declaring : type;
        _ = instanceType;
        return Expression.Convert(inst, cast);
    }

    private static Expression[] BuildArgs(ParameterExpression args, in Candidate c, Type[] argTypes)
    {
        ParameterInfo[] ps = c.Method.GetParameters();
        var exprs = new Expression[ps.Length];
        int fixedCount = c.Expanded ? ps.Length - 1 : ps.Length;
        for (int i = 0; i < fixedCount; i++) exprs[i] = ConvertArg(Arg(args, i), ps[i].ParameterType, argTypes[i]);
        if (c.Expanded)
        {
            Type elem = ps[ps.Length - 1].ParameterType.GetElementType();
            var rest = new List<Expression>();
            for (int i = fixedCount; i < argTypes.Length; i++) rest.Add(ConvertArg(Arg(args, i), elem, argTypes[i]));
            exprs[ps.Length - 1] = Expression.NewArrayInit(elem, rest);
        }
        return exprs;
    }

    private static Func<object, object[], object> Compile(Expression body, ParameterExpression inst, ParameterExpression args) =>
        Expression.Lambda<Func<object, object[], object>>(body, inst, args).Compile();

    private static Func<object, object[], object> CompileCall(CallKey key)
    {
        var inst = Expression.Parameter(typeof(object), "inst");
        var args = Expression.Parameter(typeof(object[]), "args");
        string name = MapIndexerName(key.Type, key.Name);
        int argc = key.ArgTypes.Length;

        List<MethodInfo> methods = FindMethods(key.Type, name, key.IsStatic, key.GenericArgs);
        Type lookupType = key.Type;
        if (methods.Count == 0 && !key.IsStatic && key.InstanceType != key.Type)
        {
            methods = FindMethods(key.InstanceType, name, false, key.GenericArgs);
            lookupType = key.InstanceType;
        }
        Candidate? chosen = Select(methods, key.ArgTypes, key.ParamTypes, lookupType, key.IsStatic ? "static method" : "instance method");
        if (chosen != null)
        {
            var m = (MethodInfo)chosen.Value.Method;
            Expression target = key.IsStatic ? null : Target(inst, lookupType, key.InstanceType, m);
            Expression call = Expression.Call(target, m, BuildArgs(args, chosen.Value, key.ArgTypes));
            return Compile(ToClojure(call, m.ReturnType), inst, args);
        }

        // 属性 / 字段：无参取值，set_X 一参赋值
        if (argc == 0 && key.ParamTypes == null && key.GenericArgs == null)
        {
            Expression get = MemberGet(inst, key, name, false);
            if (get != null) return Compile(get, inst, args);
        }
        if (argc == 1 && key.ParamTypes == null && key.GenericArgs == null && name.StartsWith("set_", StringComparison.Ordinal))
        {
            Expression set = MemberSet(inst, args, key, name.Substring(4));
            if (set != null) return Compile(set, inst, args);
        }

        if (methods.Count == 0)
        {
            if (key.GenericArgs != null && lookupType.GetMember(name, Flags(key.IsStatic)).Length > 0)
                throw new InvalidOperationException($"No {(key.IsStatic ? "static" : "instance")} method {name} on {Util.NameForType(lookupType)} takes {key.GenericArgs.Length} generic argument(s)");
            throw new MissingMethodException($"No {(key.IsStatic ? "static" : "instance")} member {name} found for type {Util.NameForType(lookupType)}");
        }
        if (methods.All(m => Unsupported(m.GetParameters())))
            throw new NotSupportedException($"Method {name} on {Util.NameForType(lookupType)} has ref/out or pointer parameters, which the runtime Reflector does not support: "
                + string.Join("; ", methods.Select(Describe)));
        throw new InvalidOperationException(
            $"No matching {(key.IsStatic ? "static" : "instance")} method {name} found for type {Util.NameForType(lookupType)} with argument types ({ArgTypeNames(key.ArgTypes)}); candidates: "
            + string.Join("; ", methods.Select(Describe)));
    }

    /// <summary>字段 → 属性（methodFallback 时再 → 无参方法）；找不到返回 null。</summary>
    private static Expression MemberGet(ParameterExpression inst, CallKey key, string name, bool methodFallback)
    {
        foreach (Type t in LookupOrder(key))
        {
            FieldInfo f = FindField(t, name, key.IsStatic);
            if (f != null)
            {
                if (f.IsLiteral) return ToClojure(Expression.Constant(f.GetValue(null), f.FieldType), f.FieldType);   // GetValue：枚举常量是枚举值（GetRawConstantValue 给底层整数，cleat-053）
                Expression fe = key.IsStatic ? Expression.Field(null, f) : Expression.Field(Target(inst, t, key.InstanceType, f), f);
                return ToClojure(fe, f.FieldType);
            }
            PropertyInfo p = FindProperty(t, name, key.IsStatic);
            if (p != null && p.CanRead && p.GetMethod.IsPublic)
            {
                Expression pe = key.IsStatic ? Expression.Property(null, p) : Expression.Property(Target(inst, t, key.InstanceType, p), p);
                return ToClojure(pe, p.PropertyType);
            }
            if (methodFallback)
            {
                var ms = FindMethods(t, name, key.IsStatic, null).Where(m => m.GetParameters().Length == 0).ToList();
                if (ms.Count > 0)
                {
                    MethodInfo m = ms.Count == 1 ? ms[0] : ms.OrderByDescending(x => Depth(x.DeclaringType)).First();
                    Expression call = Expression.Call(key.IsStatic ? null : Target(inst, t, key.InstanceType, m), m);
                    return ToClojure(call, m.ReturnType);
                }
            }
        }
        return null;
    }

    private static Expression MemberSet(ParameterExpression inst, ParameterExpression args, CallKey key, string name)
    {
        Type argType = key.ArgTypes[0];
        foreach (Type t in LookupOrder(key))
        {
            FieldInfo f = FindField(t, name, key.IsStatic);
            if (f != null)
            {
                if (f.IsLiteral || f.IsInitOnly)
                    throw new InvalidOperationException($"Attempt to set readonly field {f.Name} in class {Util.NameForType(f.DeclaringType)}");
                if (!key.IsStatic && t.IsValueType)
                    throw new NotSupportedException($"Cannot set field {f.Name} on a boxed value type {Util.NameForType(t)}");
                Expression fe = key.IsStatic ? Expression.Field(null, f) : Expression.Field(Target(inst, t, key.InstanceType, f), f);
                return Expression.Block(Expression.Assign(fe, ConvertArg(Arg(args, 0), f.FieldType, argType)), Expression.Constant(null, typeof(object)));
            }
            PropertyInfo p = FindProperty(t, name, key.IsStatic);
            if (p != null)
            {
                if (!p.CanWrite || !p.SetMethod.IsPublic)
                    throw new InvalidOperationException($"Property {p.Name} of {Util.NameForType(p.DeclaringType)} has no public setter");
                if (!key.IsStatic && t.IsValueType)
                    throw new NotSupportedException($"Cannot set property {p.Name} on a boxed value type {Util.NameForType(t)}");
                Expression call = Expression.Call(key.IsStatic ? null : Target(inst, t, key.InstanceType, p), p.SetMethod, ConvertArg(Arg(args, 0), p.PropertyType, argType));
                return Expression.Block(call, Expression.Constant(null, typeof(object)));
            }
        }
        return null;
    }

    private static IEnumerable<Type> LookupOrder(CallKey key)
    {
        yield return key.Type;
        if (!key.IsStatic && key.InstanceType != key.Type) yield return key.InstanceType;
    }

    private static int Depth(Type t)
    {
        int d = 0;
        for (; t != null; t = t.BaseType) d++;
        return d;
    }

    private static Func<object, object[], object> CompileGetter(CallKey key)
    {
        var inst = Expression.Parameter(typeof(object), "inst");
        var args = Expression.Parameter(typeof(object[]), "args");
        Expression get = MemberGet(inst, key, key.Name, true);
        if (get == null)
            throw new MissingMemberException($"No matching {(key.IsStatic ? "static" : "instance")} field or property {key.Name} found for type {Util.NameForType(key.Type)}");
        return Compile(get, inst, args);
    }

    private static Func<object, object[], object> CompileSetter(CallKey key)
    {
        var inst = Expression.Parameter(typeof(object), "inst");
        var args = Expression.Parameter(typeof(object[]), "args");
        Expression set = MemberSet(inst, args, key, key.Name);
        if (set == null)
            throw new MissingMemberException($"No matching {(key.IsStatic ? "static" : "instance")} field or property {key.Name} found for type {Util.NameForType(key.Type)}");
        return Compile(set, inst, args);
    }

    private static Func<object, object[], object> CompileCtor(CallKey key)
    {
        var inst = Expression.Parameter(typeof(object), "inst");
        var args = Expression.Parameter(typeof(object[]), "args");
        Type t = key.Type;
        if (t.IsAbstract || t.ContainsGenericParameters)
            throw new InvalidOperationException($"Cannot construct {Util.NameForType(t)}: abstract or open generic type");
        ConstructorInfo[] ctors = t.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        Candidate? chosen = Select(ctors, key.ArgTypes, null, t, "constructor");
        if (chosen == null)
        {
            if (t.IsValueType && key.ArgTypes.Length == 0) return Compile(Expression.Convert(Expression.New(t), typeof(object)), inst, args);
            if (ctors.Length == 0) throw new MissingMethodException($"No public constructor for type {Util.NameForType(t)}");
            if (ctors.All(c => Unsupported(c.GetParameters())))
                throw new NotSupportedException($"Constructors of {Util.NameForType(t)} have ref/out or pointer parameters, which the runtime Reflector does not support");
            throw new InvalidOperationException(
                $"No matching constructor found for type {Util.NameForType(t)} with argument types ({ArgTypeNames(key.ArgTypes)}); candidates: "
                + string.Join("; ", ctors.Select(Describe)));
        }
        Expression nw = Expression.New((ConstructorInfo)chosen.Value.Method, BuildArgs(args, chosen.Value, key.ArgTypes));
        return Compile(Expression.Convert(nw, typeof(object)), inst, args);
    }

    private static Func<IFn, Delegate> BuildDelegateFactory(Type delegateType)
    {
        MethodInfo invoke = delegateType.GetMethod("Invoke");
        ParameterInfo[] ps = invoke.GetParameters();
        if (Unsupported(ps) || invoke.ReturnType.IsByRef)
            throw new NotSupportedException($"Delegate type {Util.NameForType(delegateType)} has ref/out or pointer parameters, which IFn wrapping does not support");
        var fn = Expression.Parameter(typeof(IFn), "fn");
        ParameterExpression[] parms = ps.Select((p, i) => Expression.Parameter(p.ParameterType, p.Name ?? ("p" + i))).ToArray();
        Expression[] cljArgs = parms.Select(p => ToClojure(p, p.Type)).ToArray();
        Expression call;
        if (ps.Length <= 20)
        {
            MethodInfo inv = typeof(IFn).GetMethod("invoke", Enumerable.Repeat(typeof(object), ps.Length).ToArray());
            call = Expression.Call(fn, inv, cljArgs);
        }
        else
        {
            call = Expression.Call(fn, s_applyToMethod, Expression.Call(s_arraySeqCreate, Expression.NewArrayInit(typeof(object), cljArgs)));
        }
        Expression body = invoke.ReturnType == typeof(void)
            ? Expression.Block(typeof(void), call)
            : ConvertArg(call, invoke.ReturnType, typeof(object));
        LambdaExpression inner = Expression.Lambda(delegateType, body, parms);
        return Expression.Lambda<Func<IFn, Delegate>>(Expression.Convert(inner, typeof(Delegate)), fn).Compile();
    }

    #endregion

    #region Value conversion

    private static InvalidCastException CastError(object v, Type t) =>
        new InvalidCastException(v == null
            ? $"Cannot convert nil to {Util.NameForType(t)}"
            : $"Cannot convert {Util.NameForType(v.GetType())} ({RT.printString(v)}) to {Util.NameForType(t)}");

    private static object NotNil(object v, Type t) => v ?? throw CastError(null, t);

    /// <summary>数值转换前的检查：nil 与非数值（含 char 以外的字符串等）报 InvalidCastException，让错误清楚。</summary>
    private static object Num(object v, Type t)
    {
        if (v == null) throw CastError(null, t);
        Type a = v.GetType();
        if (IsNumericArg(a)) return v;
        throw CastError(v, t);
    }

    private static object ToEnum(Type t, object v)
    {
        switch (v)
        {
            case null: throw CastError(null, t);
            case Keyword k: return Enum.Parse(t, k.Name, true);
            case Symbol s: return Enum.Parse(t, s.Name, true);
            case string str: return Enum.Parse(t, str, true);
        }
        if (t.IsInstanceOfType(v)) return v;
        if (v is Enum) throw CastError(v, t);
        if (IsNumericArg(v.GetType())) return Enum.ToObject(t, RT.longCast(v));
        throw CastError(v, t);
    }

    private static object ToDelegate(Type t, object v)
    {
        if (v == null) return null;
        if (t.IsInstanceOfType(v)) return v;
        if (v is IFn fn) return MakeDelegate(fn, t);
        throw CastError(v, t);
    }

    private static object CheckCast(Type t, object v)
    {
        if (v == null)
        {
            if (t.IsValueType) throw CastError(null, t);
            return null;
        }
        if (t.IsInstanceOfType(v)) return v;
        throw CastError(v, t);
    }

    /// <summary>object → t（装箱结果）；Convert&lt;T&gt; 的非泛型形式，数组元素赋值等处使用。</summary>
    private static object ToType(Type t, object v)
    {
        if (t == typeof(object)) return v;
        if (t.IsValueType && !IsNullable(t) && !t.IsEnum && (IsNumericType(t) || t == typeof(char))) return Cast(t, v);
        if (t.IsEnum) return ToEnum(t, v);
        if (typeof(Delegate).IsAssignableFrom(t)) return ToDelegate(t, v);
        if (IsNullable(t)) return v == null ? null : ToType(Nullable.GetUnderlyingType(t), v);
        return CheckCast(t, v);
    }

    private static class Converter<T>
    {
        public static readonly Func<object, T> Fn = (Func<object, T>)BuildConverter(typeof(T));
    }

    private static Delegate BuildConverter(Type t)
    {
        if (t == typeof(object)) return (Func<object, object>)(v => v);
        if (!t.IsEnum)
        {
            switch (Type.GetTypeCode(t))
            {
                case TypeCode.Int64: return (Func<object, long>)(v => v is long x ? x : RT.longCast(Num(v, typeof(long))));
                case TypeCode.Int32: return (Func<object, int>)(v => v is int x ? x : RT.intCast(Num(v, typeof(int))));
                case TypeCode.Double: return (Func<object, double>)(v => v is double x ? x : RT.doubleCast(Num(v, typeof(double))));
                case TypeCode.Single: return (Func<object, float>)(v => v is float x ? x : RT.floatCast(Num(v, typeof(float))));
                case TypeCode.Int16: return (Func<object, short>)(v => v is short x ? x : RT.shortCast(Num(v, typeof(short))));
                case TypeCode.Byte: return (Func<object, byte>)(v => v is byte x ? x : RT.byteCast(Num(v, typeof(byte))));
                case TypeCode.SByte: return (Func<object, sbyte>)(v => v is sbyte x ? x : RT.sbyteCast(Num(v, typeof(sbyte))));
                case TypeCode.UInt16: return (Func<object, ushort>)(v => v is ushort x ? x : RT.ushortCast(Num(v, typeof(ushort))));
                case TypeCode.UInt32: return (Func<object, uint>)(v => v is uint x ? x : RT.uintCast(Num(v, typeof(uint))));
                case TypeCode.UInt64: return (Func<object, ulong>)(v => v is ulong x ? x : RT.ulongCast(Num(v, typeof(ulong))));
                case TypeCode.Decimal: return (Func<object, decimal>)(v => v is decimal x ? x : RT.decimalCast(Num(v, typeof(decimal))));
                case TypeCode.Char: return (Func<object, char>)(v => v is char x ? x : RT.charCast(Num(v, typeof(char))));
                case TypeCode.Boolean: return (Func<object, bool>)(v => v is bool x ? x : throw CastError(v, typeof(bool)));
                case TypeCode.String: return (Func<object, string>)(v => v == null ? null : v is string x ? x : throw CastError(v, typeof(string)));
            }
        }
        // 其余经 object 形式的辅助函数再转换为 T（枚举/委托/可空/引用/其它值类型）
        var p = Expression.Parameter(typeof(object), "v");
        var tc = Expression.Constant(t, typeof(Type));
        MethodInfo helper;
        if (t.IsEnum) helper = typeof(Reflector).GetMethod(nameof(ToEnum), BindingFlags.NonPublic | BindingFlags.Static);
        else if (typeof(Delegate).IsAssignableFrom(t)) helper = typeof(Reflector).GetMethod(nameof(ToDelegate), BindingFlags.NonPublic | BindingFlags.Static);
        else if (IsNullable(t)) helper = typeof(Reflector).GetMethod(nameof(ToType), BindingFlags.NonPublic | BindingFlags.Static);
        else helper = typeof(Reflector).GetMethod(nameof(CheckCast), BindingFlags.NonPublic | BindingFlags.Static);
        Expression body = Expression.Convert(Expression.Call(helper, tc, p), t);
        return Expression.Lambda(typeof(Func<,>).MakeGenericType(typeof(object), t), body, p).Compile();
    }

    #endregion
}
