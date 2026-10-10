using System.Threading;

namespace Cleat.Scheme;

/// <summary>布尔单例、真值判断与小整数装箱缓存。设计：designs/02-runtime.md §2。</summary>
public static class RT
{
    public static readonly object True = true;
    public static readonly object False = false;

    /// <summary>Scheme 真值：只有 #f 为假。</summary>
    public static bool IsTrue(object o) => !(o is bool b) || b;

    public static object Bool(bool b) => b ? True : False;

    private const int MinCache = -1024, MaxCache = 1024;
    private static readonly object[] s_intCache = MakeIntCache();

    private static object[] MakeIntCache()
    {
        var c = new object[MaxCache - MinCache];
        for (int i = 0; i < c.Length; i++) c[i] = i + MinCache;
        return c;
    }

    /// <summary>fixnum 装箱；小整数返回缓存的同一对象。</summary>
    public static object Box(int value) =>
      value >= MinCache && value < MaxCache ? s_intCache[value - MinCache] : value;
}

/// <summary>未指定值（如 set! 的返回值）。</summary>
public sealed class Unspecified
{
    public static readonly Unspecified Instance = new Unspecified();
    private Unspecified() { }
    public override string ToString() => "#<unspecified>";
}

/// <summary>letrec 变量在初始化前的哨兵值。</summary>
public sealed class Uninitialized
{
    public static readonly Uninitialized Instance = new Uninitialized();
    private Uninitialized() { }
    public override string ToString() => "#<uninitialized>";
}

/// <summary>符号值槽未绑定时的哨兵值。不会作为 Scheme 值出现。</summary>
public sealed class Unbound
{
    public static readonly Unbound Instance = new Unbound();
    private Unbound() { }
    public override string ToString() => "#<unbound>";
}

/// <summary>多值。单个值不包装，直接返回该值。</summary>
public sealed class MultipleValues
{
    private readonly object[] _values;

    public MultipleValues(params object[] values) { _values = values; }

    public int Length => _values.Length;

    public object this[int index]
    {
        get => _values[index];
        set => _values[index] = value;
    }

    public object[] ToArray() => _values;

    /// <summary>取出恰好 expects 个值，个数不符时报错。</summary>
    public object[] ToArray(int expects)
    {
        if (expects != _values.Length)
            Errors.AssertionViolation("call-with-values", $"expected {expects} values, got {_values.Length}", _values);
        return _values;
    }
}

/// <summary>Scheme 字符：Unicode 码点。与旧运行时同形（Scheme 源码通过 clr-field-get Value 访问）。</summary>
public readonly struct SchemeChar
{
    public readonly int Value;
    public SchemeChar(int value) { Value = value; }
    public override string ToString() => char.ConvertFromUtf32(Value);
}
