// Copyright (c) 2026 the IronScheme contributors
// This source code is subject to terms and conditions of the BSD License.
// See docs/license.txt.
using System;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Cleat.Scheme;

/// <summary>
/// 大整数（System.Numerics.BigInteger）的辅助操作（iron-059：取代 Oyster.IntX 特有的成员）。
/// </summary>
public static class BigIntegers
{
    /// <summary>是否在 fixnum（int）范围内。GetBitLength 不含符号位，-2^31 为 31。</summary>
    public static bool IsInt32(this BigInteger i) => i.GetBitLength() < 32;

    /// <summary>转为最接近的 double（就近舍入，平局取偶）；超出范围为 ±∞。</summary>
    public static double ToFloat64(this BigInteger i) =>
      i.GetBitLength() <= 53 ? (double)i : RationalToDouble(i, BigInteger.One);

    /// <summary>
    /// 按 2、8、10、16 进制写出（小写字母，负数带 "-"）。十进制见 <see cref="ToDecimalString"/>；2 的幂进制直接从补码字节取位，线性时间。
    /// </summary>
    public static string ToString(BigInteger i, int radix)
    {
        if (radix == 10) return ToDecimalString(i);
        int bitsPerDigit = BitsPerDigit(radix);
        if (i.IsZero) return "0";
        bool negative = i.Sign < 0;
        byte[] bytes = BigInteger.Abs(i).ToByteArray(isUnsigned: true, isBigEndian: false);
        long bitCount = (long)BigInteger.Abs(i).GetBitLength();
        int digits = (int)((bitCount + bitsPerDigit - 1) / bitsPerDigit);
        var chars = new char[digits + (negative ? 1 : 0)];
        int pos = chars.Length;
        for (long bit = 0; bit < bitCount; bit += bitsPerDigit)
        {
            int d = 0;
            for (int k = 0; k < bitsPerDigit; k++)
            {
                long b = bit + k;
                if (b < bitCount && (bytes[b >> 3] & (1 << (int)(b & 7))) != 0) d |= 1 << k;
            }
            chars[--pos] = (char)(d < 10 ? '0' + d : 'a' + d - 10);
        }
        if (negative) chars[--pos] = '-';
        return new string(chars);
    }

    #region 十进制输出

    // BCL 的 BigInteger.ToString 按位数平方增长（77338 位约 130ms，143137 位约 440ms），而除法是次平方的。
    // 位数多时分治：按 10^(Leaf·2^k) 拆成高低两半递归，低半左补 0；叶子交给 BCL。
    // 77338 位约 14ms，143137 位约 37ms。约 1000 位以下 BCL 更快。
    private const int DecimalLeafDigits = 256;
    private const long DecimalDirectBits = 3300;              // 约 1000 位
    private const long DecimalPowerCacheBits = 1L << 22;      // 缓存的幂最多约 126 万位（512KB），更大的每次现算
    private static BigInteger[] s_decimalPowers = { BigInteger.Pow(10, DecimalLeafDigits) };   // [k] = 10^(Leaf·2^k)

    /// <summary>十进制写出（不变区域）。</summary>
    public static string ToDecimalString(BigInteger i)
    {
        long bits = i.GetBitLength();
        if (bits <= DecimalDirectBits) return i.ToString(CultureInfo.InvariantCulture);
        bool negative = i.Sign < 0;
        var v = BigInteger.Abs(i);
        var powers = DecimalPowers(v, bits);
        var sb = new StringBuilder((int)(bits * 0.30103) + 2);
        if (negative) sb.Append('-');
        AppendDecimal(sb, v, powers.Length - 1, 0, powers);
        return sb.ToString();
    }

    /// <summary>10^(Leaf·2^k)，直到再平方就超过 v；末项满足 v &lt; 末项²。</summary>
    private static BigInteger[] DecimalPowers(BigInteger v, long bits)
    {
        var cached = s_decimalPowers;
        var list = new System.Collections.Generic.List<BigInteger>(cached);
        int n = 1;
        while (true)
        {
            var p = list[n - 1];
            if (bits <= 2 * p.GetBitLength() - 2) break;   // v < 2^(2·len-2) ≤ p²
            BigInteger sq;
            if (n < list.Count) sq = list[n];
            else
            {
                sq = p * p;
                list.Add(sq);
            }
            if (v < sq) break;
            n++;
        }
        // 只缓存不超过上限的幂；并发时后写者覆盖，值相同
        int keep = 0;
        while (keep < list.Count && list[keep].GetBitLength() <= DecimalPowerCacheBits) keep++;
        if (keep > cached.Length) s_decimalPowers = list.GetRange(0, keep).ToArray();
        return list.GetRange(0, n).ToArray();
    }

    /// <summary>写出 v（v &lt; powers[level]²）；width &gt; 0 时左补 0 到 width 位。</summary>
    private static void AppendDecimal(StringBuilder sb, BigInteger v, int level, int width, BigInteger[] powers)
    {
        if (level < 0)
        {
            string s = v.ToString(CultureInfo.InvariantCulture);
            if (width > s.Length) sb.Append('0', width - s.Length);
            sb.Append(s);
            return;
        }
        int lowWidth = DecimalLeafDigits << level;
        var q = BigInteger.DivRem(v, powers[level], out var r);
        if (width == 0 && q.IsZero)
        {
            AppendDecimal(sb, r, level - 1, 0, powers);
            return;
        }
        AppendDecimal(sb, q, level - 1, width == 0 ? 0 : width - lowWidth, powers);
        AppendDecimal(sb, r, level - 1, lowWidth, powers);
    }

    #endregion

    /// <summary>
    /// 无符号整数的数字串（2、8、10、16 进制，十六进制不分大小写）；有非法数字时为 false。
    /// 十进制用 BCL 的解析；2 的幂进制直接拼补码字节，线性时间。
    /// </summary>
    public static bool TryParse(ReadOnlySpan<char> s, int radix, out BigInteger value)
    {
        value = BigInteger.Zero;
        if (s.IsEmpty) return false;
        foreach (char c in s)
            if (DigitValue(c) >= radix) return false;
        if (s.Length <= 9 && radix <= 10 || s.Length <= 7)
        {
            // 短串：int 累加即可
            int acc = 0;
            foreach (char c in s) acc = acc * radix + DigitValue(c);
            value = acc;
            return true;
        }
        if (radix == 10)
        {
            value = BigInteger.Parse(s, NumberStyles.None, CultureInfo.InvariantCulture);
            return true;
        }
        if (radix is not (2 or 8 or 16))
        {
            // 其他进制（reader 只用 2、8、10、16）：逐位累加
            BigInteger acc = BigInteger.Zero;
            foreach (char c in s) acc = acc * radix + DigitValue(c);
            value = acc;
            return true;
        }
        int bitsPerDigit = BitsPerDigit(radix);
        var bytes = new byte[((long)s.Length * bitsPerDigit + 7) / 8 + 1];   // 末尾多一个 0 字节，结果非负
        long bit = 0;
        for (int j = s.Length - 1; j >= 0; j--, bit += bitsPerDigit)
        {
            int d = DigitValue(s[j]);
            for (int k = 0; k < bitsPerDigit; k++)
                if ((d & (1 << k)) != 0) bytes[(bit + k) >> 3] |= (byte)(1 << (int)((bit + k) & 7));
        }
        value = new BigInteger(bytes);
        return true;
    }

    private static int DigitValue(char c) =>
      c >= '0' && c <= '9' ? c - '0' : (c | 0x20) >= 'a' && (c | 0x20) <= 'f' ? (c | 0x20) - 'a' + 10 : 99;

    private static int BitsPerDigit(int radix) => radix switch
    {
        2 => 1,
        8 => 3,
        16 => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(radix), radix, "radix must be 2, 8, 10 or 16"),
    };

    /// <summary>
    /// n/d 转为最接近的 double（就近舍入，平局取偶，含次正规数）；d 须为正。
    /// 不能分别转换分子分母再相除：两者超出 double 范围时得 ∞/∞ = NaN，且有两次舍入。
    /// </summary>
    public static double RationalToDouble(BigInteger n, BigInteger d)
    {
        if (n.GetBitLength() <= 53 && d.GetBitLength() <= 53) return (double)n / (double)d;   // 两者精确，IEEE 除法只舍入一次
        int sign = n.Sign;
        if (sign == 0) return 0.0;
        n = BigInteger.Abs(n);

        // 2^(e-1) < n/d < 2^(e+1)
        long e = n.GetBitLength() - d.GetBitLength();
        if (e > 1025) return sign * double.PositiveInfinity;
        if (e < -1077) return sign * 0.0;

        // 商取 55 或 56 位，余数记为粘滞位：值 = (q + 粘滞) * 2^-s
        long s = 55 - e;
        BigInteger q = s >= 0 ? BigInteger.DivRem(n << (int)s, d, out var rem) : BigInteger.DivRem(n, d << (int)-s, out rem);
        bool sticky = !rem.IsZero;
        long qbits = q.GetBitLength();
        long exp = qbits - 1 - s;   // 2^exp ≤ 值 < 2^(exp+1)
        if (exp > 1023) return sign * double.PositiveInfinity;

        // 保留的有效位：正规数 53 位；次正规数随指数减少
        long keep = exp >= -1022 ? 53 : 53 - (-1022 - exp);
        if (keep < 0) return sign * 0.0;
        int drop = (int)(qbits - keep);
        BigInteger half = BigInteger.One << (drop - 1);
        BigInteger low = q & ((BigInteger.One << drop) - 1);
        q >>= drop;
        if (low > half || (low == half && (sticky || !q.IsEven))) q += 1;

        // q ≤ 2^53，转换精确；结果可表示（或溢出为 ∞），ScaleB 不再舍入
        return sign * Math.ScaleB((double)q, (int)(drop - s));
    }
}
