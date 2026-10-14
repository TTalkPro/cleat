using System.Collections;
using System.Collections.Generic;
using System.Threading;

namespace Cleat.Scheme;

/// <summary>布尔单例、真值判断与小整数装箱缓存。设计：designs/02-runtime.md §2。</summary>
public static class RT
{
    // 与生成代码共用同一对布尔对象（cleat-082：eq? 比较 #t / #f 靠对象标识）
    public static readonly object True = Cleat.Core.Runtime.Truth.True;
    public static readonly object False = Cleat.Core.Runtime.Truth.False;

    /// <summary>真值：#f 与 #nil（.NET null）为假，其余（包括 '()）为真（designs/13-exceptions.md §7）。</summary>
    public static bool IsTrue(object o) => o != null && (o is not bool b || b);

    public static object Bool(bool b) => b ? True : False;

    // 缓存 [-1024, 16384)（iron-071；原来到 1024）。超出缓存的 fixnum 每次都分配，ack 这类深递归里每次 gen0 回收都要扫整个栈，
    // 分配占了大半时间。上限是折中：65536 时 ack 再快 20%，但 bv2string（0 ~ 65535 的随机值）慢 10%——随机取散在 2 MB 里的缓存
    // 对象，cache miss 比新分配还贵。Box 保持原样：加 AggressiveInlining 后 ray、divrec 慢 8 ~ 13%（缓存范围不变也一样）
    private const int MinCache = -1024, MaxCache = 16384;
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

/// <summary>
/// 空表 '()：非 null 的单例（与 Clojure 的 PersistentList.EMPTY 同理）。#nil 是 .NET 的 null，与 '()、#f 两两不同。
/// 正规列表以它结尾；以 #nil 结尾的序对链不是正规列表。
/// </summary>
public sealed class EmptyList : IReadOnlyList<object>
{
    public static readonly EmptyList Instance = new EmptyList();
    private EmptyList() { }
    public override string ToString() => "()";

    /// <summary>与 Clojure 的 EmptyList 相同：first / next 为 null，more 为自己。</summary>
    public object First => null;
    public Cons Next => null;
    public object More => this;
    public int Count => 0;
    public object this[int index] => throw new System.ArgumentOutOfRangeException(nameof(index));
    public IEnumerator<object> GetEnumerator() { yield break; }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
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
