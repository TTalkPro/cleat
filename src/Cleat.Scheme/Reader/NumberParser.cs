using System;
using System.Globalization;
using System.Text.RegularExpressions;
using BigInteger = System.Numerics.BigInteger;

namespace Cleat.Scheme;

/// <summary>
/// 数字解析（R6RS 4.2.8），取代旧系统由 gppg 生成的 NumberParser。语义逐条对照旧语法（IronScheme/Compiler/NumberParser.y）：
/// - 前缀 #x #b #o #d 与 #e #i 各至多一个、顺序任意；
/// - 十进制下无小数点、无指数的是精确整数，否则是 flonum；#e 时小数按十进制字符串精确换算（#e1.1 = 11/10）；
/// - 指数标记 e s f d l 等价；尾数宽度 |n 忽略；溢出得到 +inf.0；
/// - 复数：直角（a+bi、+i、-i、a+inf.0i …）与极坐标（a@b）；
/// - 分母为 0 的分数、无法解析的文本：返回 null。
/// 精确 / 非精确转换直接用 C# 实现，不经 Scheme 钩子（reader 在 boot 镜像加载前也要能用）。
/// </summary>
public partial class Builtins
{
    /// <summary>解析失败返回 null。</summary>
    public static object ParseNumber(string s, int radix = 10)
    {
        if (string.IsNullOrEmpty(s)) return null;
        bool? exact = null;
        bool radixSet = false;
        int i = 0;
        while (i + 1 < s.Length && s[i] == '#')
        {
            switch (char.ToLowerInvariant(s[i + 1]))
            {
                case 'x': if (radixSet) return null; radix = 16; radixSet = true; break;
                case 'b': if (radixSet) return null; radix = 2; radixSet = true; break;
                case 'o': if (radixSet) return null; radix = 8; radixSet = true; break;
                case 'd': if (radixSet) return null; radix = 10; radixSet = true; break;
                case 'e': if (exact != null) return null; exact = true; break;
                case 'i': if (exact != null) return null; exact = false; break;
                default: return null;
            }
            i += 2;
        }
        string body = s.Substring(i);
        if (body.Length == 0) return null;

        object v;
        try
        {
            v = ParseComplex(body, radix, exact == true);
            if (v == null) return null;
            if (exact == true) v = ToExact(v);
            else if (exact == false) v = ToInexact(v);
        }
        catch (DivideByZeroException) { return null; }
        catch (FormatException) { return null; }
        catch (OverflowException) { return null; }
        return v;
    }

    private static object ParseComplex(string body, int radix, bool wantExact)
    {
        char last = char.ToLowerInvariant(body[^1]);
        // 实数不会以 i 结尾（+inf.0 以 0 结尾，十六进制中 i 不是数字），以 i 结尾即为虚数形式
        if (last == 'i')
        {
            string withoutI = body[..^1];
            if (withoutI == "+") return MakeRectangular(0, 1);
            if (withoutI == "-") return MakeRectangular(0, -1);
            int k = FindImagSplit(withoutI, radix);
            string realText = k > 0 ? withoutI[..k] : null;
            string imagText = k > 0 ? withoutI[k..] : withoutI;
            if (imagText.Length == 0 || (imagText[0] != '+' && imagText[0] != '-')) return null;   // 虚部必须带符号
            object imag = imagText.Length == 1 ? (imagText[0] == '+' ? 1 : -1) : ParseReal(imagText, radix, wantExact);
            if (imag == null) return null;
            object real = realText == null ? (object)0 : ParseReal(realText, radix, wantExact);
            if (real == null) return null;
            return MakeRectangular(real, imag);
        }
        int at = body.IndexOf('@');
        if (at > 0)
        {
            object mag = ParseReal(body[..at], radix, wantExact), ang = ParseReal(body[(at + 1)..], radix, wantExact);
            return mag == null || ang == null ? null : MakePolar(mag, ang);
        }
        return ParseReal(body, radix, wantExact);
    }

    /// <summary>直角形式中实部与虚部的分界：最后一个不在开头、且不紧跟指数标记的 + 或 -。</summary>
    private static int FindImagSplit(string s, int radix)
    {
        for (int k = s.Length - 1; k > 0; k--)
        {
            if (s[k] != '+' && s[k] != '-') continue;
            char prev = char.ToLowerInvariant(s[k - 1]);
            bool afterExponent = radix == 10 && "esfdl".IndexOf(prev) >= 0 && k >= 2 && (char.IsDigit(s[k - 2]) || s[k - 2] == '.');
            if (!afterExponent) return k;
        }
        return -1;
    }

    private static object ParseReal(string s, int radix, bool wantExact)
    {
        if (s.Length == 0) return null;
        int sign = 0;
        if (s[0] == '+') sign = 1; else if (s[0] == '-') sign = -1;
        string rest = sign == 0 ? s : s[1..];
        string lower = rest.ToLowerInvariant();
        if (lower == "nan.0" || lower == "inf.0")
        {
            if (sign == 0) return null;                  // 必须带符号
            double d = lower == "nan.0" ? double.NaN : double.PositiveInfinity;
            return sign < 0 ? Multiply(-1, d) : d;
        }
        object u = ParseUReal(rest, radix, wantExact);
        if (u == null) return null;
        return sign < 0 ? Multiply(-1, u) : u;
    }

    private static readonly Regex s_decimal10 = new Regex(
      @"^(?:(?<int>\d+)(?:(?<dot>\.)(?<frac>\d*))?|(?<dot>\.)(?<frac>\d+))(?:[esfdlESFDL](?<exp>[+-]?\d+))?(?:\|\d+)?$",
      RegexOptions.Compiled | RegexOptions.ExplicitCapture);

    private const long MaxExponent = 100000;

    private static object ParseUReal(string s, int radix, bool wantExact)
    {
        if (s.Length == 0) return null;
        int slash = s.IndexOf('/');
        if (slash >= 0)
        {
            var num = ParseUInteger(s[..slash], radix);
            var den = ParseUInteger(s[(slash + 1)..], radix);
            if (num == null || den == null) return null;
            var f = new Fraction(ConvertToBigInteger(num), ConvertToBigInteger(den));   // 分母为 0 时抛 DivideByZeroException
            return f.Denominator == 1 ? ToIntegerIfPossible(f.Numerator) : (object)f;
        }
        if (radix == 10)
        {
            var m = s_decimal10.Match(s);
            if (!m.Success) return null;
            bool hasDot = m.Groups["dot"].Success, hasExp = m.Groups["exp"].Success;
            if (!hasDot && !hasExp) return ParseUInteger(m.Groups["int"].Value, 10);
            string intPart = m.Groups["int"].Success ? m.Groups["int"].Value : "";
            string fracPart = m.Groups["frac"].Success ? m.Groups["frac"].Value : "";
            // 指数可能超出 int 范围（如 tests/r6rs/contrib.sls 中的 0e9999999999999999999999）：
            // 截断到 ±MaxExponent，非精确时交给 double.Parse 得到 0.0 或 ±inf.0
            long exp = 0;
            if (hasExp && !long.TryParse(m.Groups["exp"].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exp))
                exp = m.Groups["exp"].Value.StartsWith("-") ? -MaxExponent : MaxExponent;
            exp = Math.Clamp(exp, -MaxExponent, MaxExponent);
            if (wantExact)
                return Math.Abs(exp) >= MaxExponent ? null : ExactDecimal(intPart, fracPart, (int)exp);
            string text = (intPart.Length == 0 ? "0" : intPart) + "." + (fracPart.Length == 0 ? "0" : fracPart) + "e" + exp.ToString(CultureInfo.InvariantCulture);
            return double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);   // .NET Core 3.0+ 正确舍入，溢出得 ∞
        }
        return ParseUInteger(s, radix);
    }

    private static object ParseUInteger(string s, int radix) =>
      BigIntegers.TryParse(s, radix, out var v) ? ToIntegerIfPossible(v) : null;

    /// <summary>十进制字符串的精确值：整数部分与小数部分拼成整数，乘以 10^(exp - 小数位数)。</summary>
    private static object ExactDecimal(string intPart, string fracPart, int exp)
    {
        BigInteger digits = BigInteger.Parse((intPart + fracPart).TrimStart('0') is { Length: > 0 } t ? t : "0");
        int scale = exp - fracPart.Length;
        if (scale >= 0) return ToIntegerIfPossible(digits * BigInteger.Pow(10, scale));
        var f = new Fraction(digits, BigInteger.Pow(10, -scale));
        return f.Denominator == 1 ? ToIntegerIfPossible(f.Numerator) : (object)f;
    }

    #region 精确 / 非精确转换（reader 用，不经 Scheme 钩子）

    /// <summary>非精确数 → 精确数（二进制浮点的精确值）；NaN / 无穷抛 FormatException。</summary>
    public static object ToExact(object v)
    {
        switch (v)
        {
            case double d: return DoubleToExact(d);
            case Complex64 c:
                return MakeRectangular(DoubleToExact(c.Real), DoubleToExact(c.Imag));
            default: return v;   // 已经精确
        }
    }

    private static object DoubleToExact(double d)
    {
        if (double.IsNaN(d) || double.IsInfinity(d)) throw new FormatException("no exact representation");
        long bits = BitConverter.DoubleToInt64Bits(d);
        bool negative = bits < 0;
        int e = (int)((bits >> 52) & 0x7FF);
        long mantissa = bits & 0xFFFFFFFFFFFFFL;
        if (e == 0) e = 1; else mantissa |= 1L << 52;
        e -= 1075;
        BigInteger num = mantissa;
        if (negative) num = -num;
        if (e >= 0) return ToIntegerIfPossible(num * BigInteger.Pow(2, e));
        var f = new Fraction(num, BigInteger.Pow(2, -e));
        return f.Denominator == 1 ? ToIntegerIfPossible(f.Numerator) : (object)f;
    }

    /// <summary>精确数 → 非精确数。</summary>
    public static object ToInexact(object v) => v switch
    {
        int i => (double)i,
        BigInteger or Fraction => ConvertToReal(v),
        ComplexFraction cf => cf.ToComplex64(),
        _ => v,
    };

    #endregion

    #region 复数构造（移植自旧 IronScheme/Compiler/Helper.cs）

    private static Fraction GetFraction(object o) => o switch
    {
        int i => i,
        BigInteger b => b,
        Fraction f => f,
        _ => null,
    };

    /// <summary>虚部为精确 0 时返回实部；为浮点 0 时仍构造复数（去掉 -0）。</summary>
    public static object MakeRectangular(object re, object im)
    {
        if (IsExact(re) && IsExact(im))
        {
            var f = GetFraction(im);
            return f == 0 ? re : ComplexFraction.Make(GetFraction(re), f);
        }
        double i2 = ConvertToReal(im);
        if (i2 == 0.0 && !(im is double)) return re;   // 保留 -0.0 虚部（R6RS：(angle -1.0-0.0i) 为 -π）
        return Complex64.Make(ConvertToReal(re), i2);
    }

    public static object MakePolar(object mag, object ang)
    {
        double a = ConvertToReal(ang);
        if (a == 0.0)
        {
            a = 0;
            if (!(ang is double)) return mag;
        }
        object r = MakeRectangular(Math.Cos(a), Math.Sin(a));
        return r is double d ? ConvertToReal(mag) * d : (object)(ConvertToReal(mag) * (Complex64)r);
    }

    #endregion
}
