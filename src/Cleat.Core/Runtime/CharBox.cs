using System.Runtime.CompilerServices;

namespace Cleat.Core.Runtime;

/// <summary>
/// ASCII 字符装箱缓存：0 ~ 127 的 char 装箱时取预先分配的对象。JVM 的 Character.valueOf 也缓存这个范围；
/// browse 反复执行 (nth string 0) 时，CLR 的隐式装箱每次分配 16 字节，而匹配用的 ? / * / a / b 只有少数几个值。
/// 与 LongBox 不同，这只改变引用同一性（Clojure 不保证 char 的 identical?），值语义不变。
/// </summary>
public static class CharBox
{
    private const int Hi = 127;
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
