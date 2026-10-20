using System.Runtime.CompilerServices;

namespace Cleat.Core.Runtime;

/// <summary>
/// ASCII 字符装箱缓存：0 ~ 255 的 char 装箱时取预先分配的对象。JVM 的 Character.valueOf 缓存 0 ~ 127，
/// 这里扩到 0 ~ 255（Latin-1）：常量加载点（cleat-142）与 RT.nth / StringSeq 共用一张表，多出的 128 项只花 2 KB，
/// 覆盖基准里出现的非 ASCII 定界字符；Clojure 不保证 char 的 identical?，值语义不变。
/// browse 反复执行 (nth string 0) 时，CLR 的隐式装箱每次分配 16 字节，而匹配用的 ? / * / a / b 只有少数几个值。
/// 与 LongBox 不同，这只改变引用同一性（Clojure 不保证 char 的 identical?），值语义不变。
/// </summary>
public static class CharBox
{
    private const int Hi = 255;
    private static readonly object[] s_cache = Build();

    private static object[] Build()
    {
        var a = new object[Hi + 1];
        for (int i = 0; i <= Hi; i++) a[i] = (char)i;
        return a;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static object Get(char x) => x <= Hi ? s_cache[x] : x;
}
