using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using BigInteger = System.Numerics.BigInteger;

namespace Cleat.Scheme;

/// <summary>
/// CLR 互操作的运行时实现（设计 02 §9）。编译期能决议的成员由发射器直接生成调用（iron-019）；
/// 决议不了的、以及 stage0 解释执行的 clr-* 形式，都走这里：按实际参数类型选重载并缓存。
/// 转换规则以旧系统 ClrGenerators.cs（ConvertToHelper / ConvertFromHelper）与 Helpers.cs 为准。
/// </summary>
public static class Interop
{
    #region 调用

    /// <summary>(clr-call T member instance arg …)：instance 为 null 表示静态调用。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, string> s_defaultMembers =
      new System.Collections.Concurrent.ConcurrentDictionary<Type, string>();

    public static object Call(Type type, string member, object instance, object[] args) =>
      Call(type, member, instance, args, null, null);

    /// <param name="paramTypes">显式指定的参数类型（"Name(T1,T2)" 或 (Name T1 T2) 形式）；null 表示按实参推断</param>
    /// <param name="genericArgs">泛型方法的类型参数；null 表示非泛型</param>
    public static object Call(Type type, string member, object instance, object[] args, Type[] paramTypes, Type[] genericArgs)
    {
        bool isStatic = instance == null;
        if (!isStatic && !type.IsInstanceOfType(instance)) instance = ConvertTo(instance, type, "clr-call");

        // 索引器：按 DefaultMember 映射；数组直接下标访问
        if (member == "get_Item" || member == "set_Item")
        {
            if (type.IsArray && !isStatic)
            {
                var arr = (Array)instance;
                int idx = (int)ConvertTo(args[0], typeof(int), "clr-call");
                if (member == "get_Item") return ConvertFrom(arr.GetValue(idx), type.GetElementType());
                arr.SetValue(ConvertTo(args[1], type.GetElementType(), "clr-call"), idx);
                return Unspecified.Instance;
            }
            var dm = s_defaultMembers.GetOrAdd(type, t => t.GetCustomAttribute<DefaultMemberAttribute>()?.MemberName);
            if (dm != null) member = member.Substring(0, 4) + dm;
        }

        var method = ResolveMethod(type, member, isStatic, args, paramTypes, genericArgs);
        if (method == null)
            return Errors.AssertionViolation("clr-call", $"member could not be resolved on type: {type.Name}", Symbol.Intern(member), Cons.FromArray(args));

        var converted = ConvertArguments(method.GetParameters(), args, "clr-call");
        object result = InvokeUnwrapped(() => method.Invoke(isStatic ? null : instance, converted));
        return ConvertFrom(result, method.ReturnType);
    }

    public static object GetField(Type type, string name, object instance)
    {
        var f = type.GetField(name, BindingFlags.Public | (instance == null ? BindingFlags.Static : BindingFlags.Instance) | BindingFlags.FlattenHierarchy);
        if (f == null) return Errors.AssertionViolation("clr-field-get", $"field not found on type: {type.Name}", Symbol.Intern(name));
        return ConvertFrom(f.GetValue(instance), f.FieldType);
    }

    public static object SetField(Type type, string name, object instance, object value)
    {
        var f = type.GetField(name, BindingFlags.Public | (instance == null ? BindingFlags.Static : BindingFlags.Instance) | BindingFlags.FlattenHierarchy);
        if (f == null) return Errors.AssertionViolation("clr-field-set!", $"field not found on type: {type.Name}", Symbol.Intern(name));
        f.SetValue(instance, ConvertTo(value, f.FieldType, "clr-field-set!"));
        return Unspecified.Instance;
    }

    /// <summary>(clr-new T arg …)：值类型无参数时得到默认值。</summary>
    public static object New(Type type, object[] args)
    {
        if (args.Length == 0 && type.IsValueType) return ConvertFrom(Activator.CreateInstance(type), type);
        // 构造函数的选择同样按（类型、实参类型）缓存；原先每次都重新打分（iron-033）
        var key = new CacheKey(type, ".ctor", false, args, null, null);
        if (!s_resolved.TryGetValue(key, out var ctor))
        {
            ctor = SelectOverload(type.GetConstructors(), args, null);
            if (ctor != null) s_resolved[key] = ctor;
        }
        if (ctor == null) return Errors.AssertionViolation("clr-new", $"constructor could not be resolved on type: {type.Name}", Cons.FromArray(args));
        var converted = ConvertArguments(ctor.GetParameters(), args, "clr-new");
        return ConvertFrom(InvokeUnwrapped(() => ((ConstructorInfo)ctor).Invoke(converted)), type);
    }

    public static object NewArray(Type elementType, object size) =>
      Array.CreateInstance(elementType, (int)ConvertTo(size, typeof(int), "clr-new-array"));

    /// <summary>(clr-cast T e)：转换为 T 后再转回 Scheme 表示；null 得到 T 的默认值（与旧系统一致）。</summary>
    public static object Cast(Type type, object value)
    {
        if (value == null && type.IsValueType) return CastResult(Activator.CreateInstance(type), type);
        // 整数类型之间（含 char）按 IL 的 conv 指令转换：不检查溢出，截断取低位（与旧编译器一致）
        if ((IsNumeric(type) || type == typeof(char)) && value is int or long or char or byte or sbyte or short or ushort or uint or ulong)
        {
            long v = value is char c ? c : value is ulong ul ? unchecked((long)ul) : System.Convert.ToInt64(value, CultureInfo.InvariantCulture);
            object r = Type.GetTypeCode(type) switch
            {
                TypeCode.Byte => unchecked((byte)v),
                TypeCode.SByte => unchecked((sbyte)v),
                TypeCode.Int16 => unchecked((short)v),
                TypeCode.UInt16 => unchecked((ushort)v),
                TypeCode.Char => unchecked((char)v),
                TypeCode.Int32 => unchecked((int)v),
                TypeCode.UInt32 => unchecked((uint)v),
                TypeCode.Int64 => v,
                TypeCode.UInt64 => unchecked((ulong)v),
                TypeCode.Single => (float)v,
                TypeCode.Double => (double)v,
                _ => null,
            };
            if (r != null) return CastResult(r, type);
        }
        return CastResult(ConvertTo(value, type, "clr-cast"), type);
    }

    public static object Is(Type type, object value) => RT.Bool(type.IsInstanceOfType(value));

    private static object InvokeUnwrapped(Func<object> f)
    {
        try { return f(); }
        catch (TargetInvocationException e) when (e.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }
    }

    #endregion

    #region 重载决议

    private static readonly ConcurrentDictionary<CacheKey, MethodBase> s_resolved = new ConcurrentDictionary<CacheKey, MethodBase>();

    /// <summary>
    /// 决议缓存的键：类型、成员名、静态与否、实参的运行时类型（null 实参记为 null）、显式参数类型与泛型参数。
    /// 原先拼成字符串（含 AssemblyQualifiedName），每次调用都要分配与格式化；现按引用比较类型（iron-033）。
    /// </summary>
    private sealed class CacheKey : IEquatable<CacheKey>
    {
        private readonly Type _type;
        private readonly string _member;
        private readonly bool _isStatic;
        private readonly Type[] _types;   // 实参类型，其后接显式参数类型与泛型参数（以 typeof(void) 分隔）
        private readonly int _hash;

        public CacheKey(Type type, string member, bool isStatic, object[] args, Type[] paramTypes, Type[] genericArgs)
        {
            _type = type;
            _member = member;
            _isStatic = isStatic;
            int n = args.Length + 2 + (paramTypes?.Length ?? 0) + (genericArgs?.Length ?? 0);
            _types = new Type[n];
            int k = 0;
            foreach (var a in args) _types[k++] = a?.GetType();
            _types[k++] = typeof(void);
            if (paramTypes != null) foreach (var p in paramTypes) _types[k++] = p;
            _types[k++] = genericArgs == null ? null : typeof(void);
            if (genericArgs != null) foreach (var g in genericArgs) _types[k++] = g;
            var h = new HashCode();
            h.Add(type); h.Add(member); h.Add(isStatic);
            foreach (var t in _types) h.Add(t);
            _hash = h.ToHashCode();
        }

        public bool Equals(CacheKey o)
        {
            if (o == null || _hash != o._hash || _type != o._type || _isStatic != o._isStatic || _member != o._member || _types.Length != o._types.Length) return false;
            for (int i = 0; i < _types.Length; i++) if (_types[i] != o._types[i]) return false;
            return true;
        }

        public override bool Equals(object o) => Equals(o as CacheKey);
        public override int GetHashCode() => _hash;
    }

    private static MethodInfo ResolveMethod(Type type, string member, bool isStatic, object[] args, Type[] paramTypes, Type[] genericArgs)
    {
        var key = new CacheKey(type, member, isStatic, args, paramTypes, genericArgs);
        if (s_resolved.TryGetValue(key, out var cached)) return (MethodInfo)cached;

        var flags = BindingFlags.Public | (isStatic ? BindingFlags.Static : BindingFlags.Instance) | BindingFlags.FlattenHierarchy;
        var candidates = new List<MethodBase>();
        foreach (var mi in type.GetMember(member, MemberTypes.Method, flags).Cast<MethodInfo>())
        {
            if (genericArgs != null)
            {
                if (mi.ContainsGenericParameters && mi.GetGenericArguments().Length == genericArgs.Length)
                    candidates.Add(mi.MakeGenericMethod(genericArgs));
            }
            else if (!mi.ContainsGenericParameters) candidates.Add(mi);
        }
        // 接口类型上找不到时，再查 object 的方法（ToString 等）
        if (candidates.Count == 0 && type.IsInterface && !isStatic)
            candidates.AddRange(typeof(object).GetMember(member, MemberTypes.Method, flags).Cast<MethodInfo>());

        var m = (MethodInfo)SelectOverload(candidates, args, paramTypes);
        if (m != null) s_resolved[key] = m;
        return m;
    }

    /// <summary>按参数逐个打分：类型完全相同 3、可赋值 2、可转换 1、不可转换则淘汰；取总分最高者，平分时取先出现者。</summary>
    private static MethodBase SelectOverload(IEnumerable<MethodBase> candidates, object[] args, Type[] paramTypes)
    {
        MethodBase best = null;
        int bestScore = -1;
        foreach (var m in candidates)
        {
            var ps = m.GetParameters();
            bool hasParams = ps.Length > 0 && ps[^1].IsDefined(typeof(ParamArrayAttribute), false);
            if (paramTypes != null)
            {
                if (ps.Length != paramTypes.Length) continue;
                bool match = true;
                for (int i = 0; i < ps.Length && match; i++) match = paramTypes[i] == null || ps[i].ParameterType == paramTypes[i];
                if (match) return m;
                continue;
            }
            int score = Score(ps, args, hasParams);
            if (score > bestScore) { best = m; bestScore = score; }
        }
        return best;
    }

    private static int Score(ParameterInfo[] ps, object[] args, bool hasParams)
    {
        if (ps.Any(p => p.ParameterType.IsByRef)) return -1;   // 不支持 ref/out
        int fixedCount = hasParams ? ps.Length - 1 : ps.Length;
        if (hasParams ? args.Length < fixedCount : args.Length != ps.Length) return -1;
        int total = 0;
        for (int i = 0; i < args.Length; i++)
        {
            Type pt = i < fixedCount ? ps[i].ParameterType : null;
            if (pt == null)
            {
                // params 部分：若恰好传入一个数组（或可转换为该数组类型的向量），按数组整体传递
                if (args.Length == ps.Length && ps[^1].ParameterType.IsInstanceOfType(args[i])) { total += 2; continue; }
                if (args.Length == ps.Length && args[i] is Array && CanConvert(args[i], ps[^1].ParameterType)) { total += 1; continue; }
                pt = ps[^1].ParameterType.GetElementType();
            }
            int s = ScoreOne(args[i], pt);
            if (s < 0) return -1;
            total += s;
        }
        return total;
    }

    private static int ScoreOne(object a, Type pt)
    {
        if (a == null) return !pt.IsValueType || Nullable.GetUnderlyingType(pt) != null ? 2 : -1;
        if (a.GetType() == pt) return 3;
        if (pt.IsInstanceOfType(a)) return 2;
        return CanConvert(a, pt) ? 1 : -1;
    }

    private static object[] ConvertArguments(ParameterInfo[] ps, object[] args, string who)
    {
        bool hasParams = ps.Length > 0 && ps[^1].IsDefined(typeof(ParamArrayAttribute), false);
        var result = new object[ps.Length];
        int fixedCount = hasParams ? ps.Length - 1 : ps.Length;
        for (int i = 0; i < fixedCount; i++) result[i] = ConvertTo(args[i], ps[i].ParameterType, who);
        if (hasParams)
        {
            var arrType = ps[^1].ParameterType;
            if (args.Length == ps.Length && arrType.IsInstanceOfType(args[^1])) result[^1] = args[^1];
            else if (args.Length == ps.Length && args[^1] is Array && CanConvert(args[^1], arrType)) result[^1] = ConvertTo(args[^1], arrType, who);
            else
            {
                var et = arrType.GetElementType();
                var arr = Array.CreateInstance(et, args.Length - fixedCount);
                for (int i = fixedCount; i < args.Length; i++) arr.SetValue(ConvertTo(args[i], et, who), i - fixedCount);
                result[^1] = arr;
            }
        }
        return result;
    }

    #endregion

    #region 转换

    private static bool IsNumeric(Type t) =>
      Type.GetTypeCode(t) is TypeCode.Byte or TypeCode.SByte or TypeCode.Int16 or TypeCode.UInt16 or TypeCode.Int32 or TypeCode.UInt32
        or TypeCode.Int64 or TypeCode.UInt64 or TypeCode.Single or TypeCode.Double or TypeCode.Decimal;

    private static bool IsSchemeNumber(object a) => a is int or double or BigInteger or Fraction || (a != null && IsNumeric(a.GetType()));

    private static bool CanConvert(object a, Type t)
    {
        if (typeof(Delegate).IsAssignableFrom(t)) return a is Procedure;
        if (t.IsEnum) return a is Symbol || a is Cons || IsSchemeNumber(a);
        if (t == typeof(string)) return a is SchemeString || a is StringBuilder;
        if (t == typeof(bool)) return true;                         // 旧系统：非布尔按真值转换
        if (t == typeof(char)) return (a is SchemeChar sc && sc.Value < 0x10000) || a is int;
        if (IsNumeric(t)) return IsSchemeNumber(a) || a is char;
        if (t.IsArray) return a is object[] || a is Cons || (a is Array arr && arr.GetType().GetElementType() != null);
        return false;
    }

    /// <summary>Scheme 值 → CLR 类型 t。无法转换时报 assertion-violation。</summary>
    public static object ConvertTo(object a, Type t, string who = "clr")
    {
        if (t == typeof(object) || t == typeof(void)) return a;
        if (a == null)
        {
            if (!t.IsValueType || Nullable.GetUnderlyingType(t) != null) return null;
            return Errors.AssertionViolation(who, "value type cannot be null", t.Name);
        }
        if (t.IsInstanceOfType(a)) return a;
        var u = Nullable.GetUnderlyingType(t);
        if (u != null) return ConvertTo(a, u, who);

        try
        {
            if (typeof(Delegate).IsAssignableFrom(t) && a is Procedure p) return ToDelegate(t, p);
            if (t.IsEnum) return a is Symbol || a is Cons ? SymbolToEnum(t, a) : Enum.ToObject(t, ToInteger(a));
            if (t == typeof(string) && (a is SchemeString || a is StringBuilder)) return a.ToString();
            if (t == typeof(bool)) return RT.IsTrue(a);
            if (t == typeof(char) && a is SchemeChar sc && sc.Value < 0x10000) return (char)sc.Value;
            // 字符与整数之间按数值转换（旧编译器对 (clr-cast Int32 (clr-cast Char c)) 生成 IL 数值转换）
            if (t == typeof(char) && a is int code) return checked((char)code);
            if (IsNumeric(t) && a is char ch) return ConvertNumber((int)ch, t);
            if (IsNumeric(t) && IsSchemeNumber(a)) return ConvertNumber(a, t);
            if (t.IsArray)
            {
                var et = t.GetElementType();
                IList<object> items = a switch
                {
                    object[] v => v,
                    Cons c => c.ToList(),
                    Array arr => arr.Cast<object>().ToList(),
                    _ => null,
                };
                if (items != null)
                {
                    var result = Array.CreateInstance(et, items.Count);
                    for (int i = 0; i < items.Count; i++) result.SetValue(ConvertTo(items[i], et, who), i);
                    return result;
                }
            }
        }
        catch (Exception e) when (e is InvalidCastException or OverflowException or FormatException or ArgumentException)
        {
            return Errors.AssertionViolation(who, $"cannot convert to {t.Name}: {e.Message}", a);
        }
        return Errors.AssertionViolation(who, "expected type: " + t.Name, a);
    }

    public static T Convert<T>(object a) => (T)ConvertTo(a, typeof(T));

    private static long ToInteger(object a) => a switch
    {
        int i => i,
        BigInteger b => (long)b,
        _ => System.Convert.ToInt64(a, CultureInfo.InvariantCulture),
    };

    private static object ConvertNumber(object a, Type t)
    {
        if (a is BigInteger b)
        {
            switch (Type.GetTypeCode(t))
            {
                case TypeCode.Int64: return (long)b;
                case TypeCode.UInt64: return (ulong)b;
                case TypeCode.Double: return b.ToFloat64();
                case TypeCode.Single: return (float)b.ToFloat64();
                case TypeCode.Decimal: return (decimal)b;
                default: return System.Convert.ChangeType((long)b, t, CultureInfo.InvariantCulture);
            }
        }
        if (a is Fraction) return System.Convert.ChangeType(Builtins.ToInexact(a), t, CultureInfo.InvariantCulture);
        return System.Convert.ChangeType(a, t, CultureInfo.InvariantCulture);
    }

    /// <summary>CLR 返回值 → Scheme 值：void → 未指定值，枚举 → 符号，bool → #t/#f 单例（旧系统每次装箱新对象）。</summary>
    /// <summary>
    /// CLR 值 → Scheme 表示：布尔规范化、枚举 → 符号；Scheme 不直接使用的 CLR 数值类型转为 Scheme 数
    /// （各整型 → fixnum 或 bignum，float → flonum）。声明类型为 object 的返回值与字段值也经过这里（按运行时类型，iron-039：
    /// 如 SqliteCommand.ExecuteScalar 返回装箱的 Int64）。
    /// </summary>
    public static object ConvertFrom(object v, Type declared)
    {
        if (declared == typeof(void)) return Unspecified.Instance;
        switch (v)
        {
            case null: return null;
            case int i: return RT.Box(i);
            case string s: return SchemeString.Normalize(s);   // 含代理码元的转为宽串（iron-055）
            case double: return v;
            case bool b: return RT.Bool(b);
            case long l: return Builtins.ToIntegerIfPossible(l);
            case short s: return RT.Box(s);
            case byte b8: return RT.Box(b8);
            case sbyte sb: return RT.Box(sb);
            case ushort us: return RT.Box(us);
            case uint ui: return Builtins.ToIntegerIfPossible(ui);
            case ulong ul: return Builtins.ToIntegerIfPossible(ul);
            case float f: return (double)f;
            case Enum e: return EnumToSymbol(e);
            default: return v;
        }
    }

    /// <summary>clr-cast 的结果：只做布尔规范化与枚举 → 符号，保留 long 等 CLR 数值类型（运行时按实参类型选重载时要用到）。</summary>
    private static object CastResult(object v, Type declared)
    {
        if (declared == typeof(void)) return Unspecified.Instance;
        switch (v)
        {
            case bool b: return RT.Bool(b);
            case int i: return RT.Box(i);
            case Enum e: return EnumToSymbol(e);
            case string s: return SchemeString.Normalize(s);
            default: return v;
        }
    }

    public static object EnumToSymbol(object value) => Symbol.Intern(value.ToString());

    /// <summary>符号 → 枚举值；符号列表 → 按位或（[Flags] 枚举）。</summary>
    public static object SymbolToEnum(Type type, object symbol)
    {
        switch (symbol)
        {
            case Symbol s:
                try { return Enum.Parse(type, s.Name, true); }
                catch (ArgumentException ex) { return Errors.AssertionViolation("symbol-to-enum", ex.Message, symbol, type); }
            case Cons c:
                long v = 0;
                foreach (var n in c) v |= System.Convert.ToInt64(SymbolToEnum(type, n), CultureInfo.InvariantCulture);
                return Enum.ToObject(type, v);
            default:
                return Errors.AssertionViolation("symbol-to-enum", "not a valid type", symbol, type);
        }
    }

    #endregion

    #region 过程 → 委托

    private static readonly ConcurrentDictionary<Type, Func<Procedure, Delegate>> s_delegateFactories = new ConcurrentDictionary<Type, Func<Procedure, Delegate>>();

    private static readonly MethodInfo s_invokeN = typeof(Procedure).GetMethod(nameof(Procedure.InvokeN));
    private static readonly MethodInfo s_convertFromM = typeof(Interop).GetMethod(nameof(ConvertFrom));
    private static readonly MethodInfo s_convertToM = typeof(Interop).GetMethod(nameof(ConvertTo));

    /// <summary>用 System.Linq.Expressions 生成"参数转为 Scheme 值 → InvokeN → 结果转为返回类型"的适配委托，按委托类型缓存工厂。</summary>
    public static Delegate ToDelegate(Type delegateType, Procedure proc) =>
      s_delegateFactories.GetOrAdd(delegateType, BuildFactory)(proc);

    private static Func<Procedure, Delegate> BuildFactory(Type dt)
    {
        var invoke = dt.GetMethod("Invoke");
        var ps = invoke.GetParameters();
        if (ps.Any(p => p.ParameterType.IsByRef)) throw new NotSupportedException("delegates with ref/out parameters are not supported: " + dt);

        var procParam = Expression.Parameter(typeof(Procedure), "proc");
        var lambdaParams = ps.Select(p => Expression.Parameter(p.ParameterType, p.Name)).ToArray();
        var argArray = Expression.NewArrayInit(typeof(object),
          lambdaParams.Select(p => (Expression)Expression.Call(s_convertFromM, Expression.Convert(p, typeof(object)), Expression.Constant(p.Type))));
        Expression call = Expression.Call(procParam, s_invokeN, argArray);
        Expression body = invoke.ReturnType == typeof(void)
          ? Expression.Block(typeof(void), call)
          : Expression.Convert(Expression.Call(s_convertToM, call, Expression.Constant(invoke.ReturnType), Expression.Constant("delegate")), invoke.ReturnType);
        var inner = Expression.Lambda(dt, body, lambdaParams);
        return Expression.Lambda<Func<Procedure, Delegate>>(inner, procParam).Compile();
    }

    #endregion
}
