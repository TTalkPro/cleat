using System;

namespace Cleat.Core.Runtime;

/// <summary>
/// cleat-138 的单一签名 key（designs/27 §2）：每个进 key 的形参按精确 CLR 类型取 4 位类型码，第 i 个形参占
/// 第 4i ～ 4i+3 位，最多 16 个形参。码 0 表示其他类型 / null / 不进 key 的形参，特化体里按 object 处理、不守卫。
/// 判定用 GetType() 相等（JIT 折叠成方法表比较），不用 is：CLR 数组协变让 long[] 通过 is ulong[]。
/// </summary>
public static class RuntimeSigKey
{
    public const int Other = 0;
    public const int Int64 = 1;
    public const int Double = 2;
    public const int Int32 = 3;
    public const int Int64Array = 4;
    public const int Int32Array = 5;
    public const int DoubleArray = 6;
    public const int String = 7;
    public const int ByteArray = 8;
    public const int CharArray = 9;
    public const int SingleArray = 10;
    public const int Int16Array = 11;
    public const int BooleanArray = 12;
    public const int ObjectArray = 13;

    /// <summary>key 能容纳的形参个数。</summary>
    public const int MaxParams = 16;

    private static readonly Type[] s_types =
    {
        null, typeof(long), typeof(double), typeof(int), typeof(long[]), typeof(int[]), typeof(double[]),
        typeof(string), typeof(byte[]), typeof(char[]), typeof(float[]), typeof(short[]), typeof(bool[]), typeof(object[]),
    };

    /// <summary>实参的类型码；按常见程度排列比较顺序。</summary>
    public static int Code(object x)
    {
        if (x is null) return Other;
        var t = x.GetType();
        if (t == typeof(long)) return Int64;
        if (t == typeof(double)) return Double;
        if (t == typeof(int)) return Int32;
        if (t == typeof(string)) return String;
        if (!t.IsArray) return Other;
        if (t == typeof(long[])) return Int64Array;
        if (t == typeof(int[])) return Int32Array;
        if (t == typeof(double[])) return DoubleArray;
        if (t == typeof(object[])) return ObjectArray;
        if (t == typeof(byte[])) return ByteArray;
        if (t == typeof(char[])) return CharArray;
        if (t == typeof(float[])) return SingleArray;
        if (t == typeof(short[])) return Int16Array;
        if (t == typeof(bool[])) return BooleanArray;
        return Other;
    }

    /// <summary>类型码对应的 CLR 类型；0 / 未知码为 null。</summary>
    public static Type TypeOf(int code) => code > 0 && code < s_types.Length ? s_types[code] : null;

    /// <summary>
    /// 编译期已知 CLR 类型的类型码（cleat-140）：静态类型必须是精确类型才返回非 0；
    /// CLR 数组协变让 <c>typeof(long).IsAssignableFrom</c> 一类判断不可靠，所以只做同一类型比较。
    /// </summary>
    public static int CodeOf(Type t)
    {
        if (t is null) return Other;
        for (var i = 1; i < s_types.Length; i++) if (t == s_types[i]) return i;
        return Other;
    }

    /// <summary>key 第 i 个形参的类型码。</summary>
    public static int Slot(long key, int i) => (int)((ulong)key >> (4 * i)) & 0xF;

    /// <summary>mask 的第 i 位为 1 的形参进 key。</summary>
    public static long Of(int mask, object a1)
        => (mask & 1) != 0 ? Code(a1) : 0;

    public static long Of(int mask, object a1, object a2)
        => Of(mask, a1) | ((mask & 2) != 0 ? (long)Code(a2) << 4 : 0);

    public static long Of(int mask, object a1, object a2, object a3)
        => Of(mask, a1, a2) | ((mask & 4) != 0 ? (long)Code(a3) << 8 : 0);

    public static long Of(int mask, object a1, object a2, object a3, object a4)
        => Of(mask, a1, a2, a3) | ((mask & 8) != 0 ? (long)Code(a4) << 12 : 0);

    public static long Of(int mask, object a1, object a2, object a3, object a4, object a5)
        => Of(mask, a1, a2, a3, a4) | ((mask & 16) != 0 ? (long)Code(a5) << 16 : 0);

    public static long Of(int mask, object a1, object a2, object a3, object a4, object a5, object a6)
        => Of(mask, a1, a2, a3, a4, a5) | ((mask & 32) != 0 ? (long)Code(a6) << 20 : 0);

    public static long Of(int mask, object a1, object a2, object a3, object a4, object a5, object a6, object a7)
        => Of(mask, a1, a2, a3, a4, a5, a6) | ((mask & 64) != 0 ? (long)Code(a7) << 24 : 0);

    public static long Of(int mask, object a1, object a2, object a3, object a4, object a5, object a6, object a7, object a8)
        => Of(mask, a1, a2, a3, a4, a5, a6, a7) | ((mask & 128) != 0 ? (long)Code(a8) << 28 : 0);

    public static long Of(int mask, object[] args)
    {
        long key = 0;
        var n = Math.Min(args.Length, MaxParams);
        for (var i = 0; i < n; i++)
        {
            if ((mask & (1 << i)) != 0) key |= (long)Code(args[i]) << (4 * i);
        }
        return key;
    }

    /// <summary>条目 key 能否服务实参 key：条目每个非 0 槽位都与实参相同（0 槽位是 object 通配）。</summary>
    public static bool Compatible(long entry, long actual)
    {
        for (var i = 0; i < MaxParams; i++)
        {
            var e = Slot(entry, i);
            if (e != 0 && e != Slot(actual, i)) return false;
        }
        return true;
    }

    /// <summary>非 0 槽位数：兼容条目里越多越具体。</summary>
    public static int Specificity(long key)
    {
        var n = 0;
        for (var i = 0; i < MaxParams; i++)
        {
            if (Slot(key, i) != 0) n++;
        }
        return n;
    }
}
