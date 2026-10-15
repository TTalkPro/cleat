using System.Runtime.CompilerServices;

namespace Cleat.Core.Runtime;

/// <summary>
/// 小整数装箱缓存（cleat-109）：-128 ~ 1023 的 long 装箱时取预先分配的对象，不再每次分配 24 字节。JVM 的 Long.valueOf 缓存 -128 ~ 127
/// （nqueens(12) 一次 JVM 分配 163 MB、cleat 1289 MB，差在这里）；范围放宽到 1023 覆盖常见的下标与计数。Clojure 不保证数的 identical?，
/// 与 JVM 不同的只是 128 ~ 1023 也相同。生成的代码在 box System.Int64 的地方调用 <see cref="Get"/>；运行时的 Numbers.num(long) 与 Num.Box 同。
/// </summary>
public static class LongBox
{
    private const long Lo = -128;
    private const long Hi = 1023;
    private static readonly object[] s_cache = Build();

    private static object[] Build()
    {
        var a = new object[Hi - Lo + 1];
        for (long v = Lo; v <= Hi; v++) a[v - Lo] = v;
        return a;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static object Get(long x) => (ulong)(x - Lo) <= (ulong)(Hi - Lo) ? s_cache[x - Lo] : x;
}
