using System;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Cleat.Core.Runtime;

/// <summary>
/// 数值的文本形式按 Clojure（JVM）的约定（cleat-032，designs/03 §4）：<c>(str 1.0)</c> 是 "1.0"、<c>(str 1e100)</c> 是 "1.0E100"、
/// <c>(str 2.3E-4)</c> 是 "2.3E-4"、<c>(str ##Inf)</c> 是 "Infinity"、<c>(str true)</c> 是 "true"——即 Double.toString /
/// Float.toString / Boolean.toString。数字取最短往返的十进制数字（与 JDK 19+ 相同）；科学记数法至少两位有效数字时取最接近的两位
/// （Double.toString 的规定，次正规数 4.9E-324 因此不是 5E-324）。.NET 的默认文本（"1"、"1E+100"、"∞"、"True"）不再外露。
/// </summary>
public static class JvmText
{
    public static string DoubleToString(double d) => FloatingToString(d, false);

    public static string FloatToString(float f) => FloatingToString(f, true);

    private static string FloatingToString(double d, bool single)
    {
        if (double.IsNaN(d)) return "NaN";
        if (double.IsPositiveInfinity(d)) return "Infinity";
        if (double.IsNegativeInfinity(d)) return "-Infinity";
        if (d == 0.0) return double.IsNegative(d) ? "-0.0" : "0.0";
        bool neg = d < 0;
        double a = Math.Abs(d);
        string r = single ? ((float)a).ToString("R", CultureInfo.InvariantCulture) : a.ToString("R", CultureInfo.InvariantCulture);
        var (digits, exp) = Digits(r);
        var sb = new StringBuilder();
        if (neg) sb.Append('-');
        if (a >= 1e-3 && a < 1e7)
        {
            // 普通记数法：整数部分 . 小数部分（至少一位）
            if (exp >= 0)
            {
                string padded = digits.Length < exp + 1 ? digits + new string('0', exp + 1 - digits.Length) : digits;
                sb.Append(padded, 0, exp + 1).Append('.');
                string frac = padded.Substring(exp + 1);
                sb.Append(frac.Length == 0 ? "0" : frac);
            }
            else
                sb.Append("0.").Append('0', -exp - 1).Append(digits);
        }
        else
        {
            if (digits.Length == 1)
            {
                // 至少两位有效数字：取最接近真实值的两位（正确舍入）
                string two = single ? ((float)a).ToString("E1", CultureInfo.InvariantCulture) : a.ToString("E1", CultureInfo.InvariantCulture);
                var (d2, e2) = Digits(two);
                bool roundTrips = single ? float.Parse(two, CultureInfo.InvariantCulture) == (float)a : double.Parse(two, CultureInfo.InvariantCulture) == a;
                if (roundTrips)
                {
                    digits = d2.Length == 1 ? d2 + "0" : d2;
                    exp = e2;
                }
            }
            sb.Append(digits[0]).Append('.');
            sb.Append(digits.Length > 1 ? digits.Substring(1) : "0");
            sb.Append('E').Append(exp.ToString(CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    /// <summary>非负有限数的十进制文本 → 有效数字（去掉首尾的 0）与指数：值 = d[0].d[1..] × 10^exp。</summary>
    private static (string Digits, int Exp) Digits(string text)
    {
        int ePos = text.IndexOfAny(new[] { 'E', 'e' });
        int e10 = 0;
        string mant = text;
        if (ePos >= 0)
        {
            e10 = int.Parse(text.Substring(ePos + 1), CultureInfo.InvariantCulture);
            mant = text.Substring(0, ePos);
        }
        int dot = mant.IndexOf('.');
        string intPart = dot < 0 ? mant : mant.Substring(0, dot);
        string frac = dot < 0 ? "" : mant.Substring(dot + 1);
        string all = intPart + frac;
        int pointPos = intPart.Length + e10;
        int lz = 0;
        while (lz < all.Length && all[lz] == '0') lz++;
        if (lz == all.Length) return ("0", 0);
        all = all.Substring(lz).TrimEnd('0');
        pointPos -= lz;
        return (all, pointPos - 1);
    }

    /// <summary>str 的一个参数（RT.CultureToString）：浮点数与布尔按 JVM 的文本，其余数值用不变区域性。</summary>
    public static string ToStr(object x) => x switch
    {
        double d => DoubleToString(d),
        float f => FloatToString(f),
        bool b => b ? "true" : "false",
        _ => null,
    };

    // —— clojure.string 的 java.lang.String 语义（cleat-032）——

    /// <summary>Pattern.split(input, limit)：不含捕获组（.NET 的 Regex.Split 会把捕获组插进结果）；开头的零宽匹配不产生空串；
    /// 没有匹配时结果就是输入本身；limit 为 0 时去掉末尾的空串，正数时最多 limit 段。</summary>
    public static object[] Split(System.Text.RegularExpressions.Regex re, string input, long limit)
    {
        var list = new System.Collections.Generic.List<object>();
        int index = 0;
        bool matchLimited = limit > 0;
        for (var m = re.Match(input); m.Success; m = m.NextMatch())
        {
            if (!matchLimited || list.Count < limit - 1)
            {
                if (index == 0 && m.Index == 0 && m.Length == 0) continue;
                list.Add(input.Substring(index, m.Index - index));
                index = m.Index + m.Length;
            }
            else if (list.Count == limit - 1)
            {
                list.Add(input.Substring(index));
                index = m.Index + m.Length;
            }
        }
        if (index == 0) return new object[] { input };
        if (!matchLimited || list.Count < limit) list.Add(input.Substring(index));
        int n = list.Count;
        if (limit == 0)
            while (n > 0 && ((string)list[n - 1]).Length == 0) n--;
        return list.GetRange(0, n).ToArray();
    }

    /// <summary>String.indexOf(value, from)：from 小于 0 按 0，超过长度按长度；找不到为 null。</summary>
    public static object IndexOf(string s, object value, long from)
    {
        int f = (int)Math.Clamp(from, 0, s.Length);
        int r = value is char c ? s.IndexOf(c, f) : s.IndexOf((string)value, f, StringComparison.Ordinal);
        return r < 0 ? null : (object)(long)r;
    }

    /// <summary>String.lastIndexOf(value, from)：匹配的起点不超过 from（.NET 的 LastIndexOf 要求匹配的终点不超过 startIndex）；
    /// from 小于 0 为 null，超过末尾按末尾。</summary>
    public static object LastIndexOf(string s, object value, long from)
    {
        if (value is char c)
        {
            long f = Math.Min(from, s.Length - 1L);
            if (f < 0) return null;
            int rc = s.LastIndexOf(c, (int)f);
            return rc < 0 ? null : (object)(long)rc;
        }
        string v = (string)value;
        long start = Math.Min(from, (long)s.Length - v.Length);
        if (start < 0) return null;
        if (v.Length == 0) return start;
        int r = s.LastIndexOf(v, (int)(start + v.Length - 1), StringComparison.Ordinal);
        return r < 0 ? null : (object)(long)r;
    }

    /// <summary>String.replace(CharSequence, CharSequence)：空的 match 在每个位置（含两端）插入替换串。</summary>
    public static string Replace(string s, string match, string replacement)
    {
        if (match.Length > 0) return s.Replace(match, replacement, StringComparison.Ordinal);
        var sb = new StringBuilder(replacement);
        foreach (char ch in s) sb.Append(ch).Append(replacement);
        return sb.ToString();
    }

    /// <summary>Matcher.quoteReplacement：给 \ 与 $ 加反斜杠。</summary>
    public static string QuoteReplacement(string s)
    {
        if (s.IndexOf('\\') < 0 && s.IndexOf('$') < 0) return s;
        var sb = new StringBuilder();
        foreach (char c in s)
        {
            if (c == '\\' || c == '$') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>java.util.regex 的替换串语法（\x 为字面 x，$n / ${name} 为组引用）→ .NET 的（$$ 为字面 $，${n}）。</summary>
    public static string NetReplacement(System.Text.RegularExpressions.Regex re, string repl)
    {
        int groupCount = 0;
        foreach (int g in re.GetGroupNumbers()) groupCount = Math.Max(groupCount, g);
        var sb = new StringBuilder();
        for (int i = 0; i < repl.Length; i++)
        {
            char c = repl[i];
            if (c == '\\')
            {
                if (++i >= repl.Length) throw new ArgumentException("character to be escaped is missing");
                c = repl[i];
                sb.Append(c == '$' ? "$$" : c.ToString());
            }
            else if (c == '$')
            {
                if (++i >= repl.Length) throw new ArgumentException("Illegal group reference: group index is missing");
                if (repl[i] == '{')
                {
                    int end = repl.IndexOf('}', i);
                    if (end < 0) throw new ArgumentException("named capturing group is missing trailing '}'");
                    string name = repl.Substring(i + 1, end - i - 1);
                    if (re.GroupNumberFromName(name) < 0) throw new ArgumentException("No group with name {" + name + "}");
                    sb.Append("${").Append(name).Append('}');
                    i = end;
                }
                else
                {
                    int d = repl[i] - '0';
                    if (d < 0 || d > 9) throw new ArgumentException("Illegal group reference");
                    int refNum = d;
                    while (i + 1 < repl.Length)
                    {
                        int nd = repl[i + 1] - '0';
                        if (nd < 0 || nd > 9) break;
                        int newRef = refNum * 10 + nd;
                        if (groupCount < newRef) break;
                        refNum = newRef;
                        i++;
                    }
                    if (refNum > groupCount) throw new IndexOutOfRangeException("No group " + refNum);
                    sb.Append("${").Append(refNum).Append('}');
                }
            }
            else
                sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Matcher.replaceAll / replaceFirst（count 为 -1 或 1）按 Java 的替换串语法。</summary>
    public static string RegexReplace(System.Text.RegularExpressions.Regex re, string s, string javaRepl, int count) =>
        re.Replace(s, NetReplacement(re, javaRepl), count);

    private static readonly System.Text.RegularExpressions.Regex s_javaDouble = new System.Text.RegularExpressions.Regex(
        @"^[+-]?(NaN|Infinity|((\d+\.?\d*|\.\d+)([eE][+-]?\d+)?)[fFdD]?|(0[xX]([0-9a-fA-F]+\.?|[0-9a-fA-F]*\.[0-9a-fA-F]+)[pP][+-]?\d+)[fFdD]?)$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Double.valueOf 的文法（parse-double）：去掉首尾空白（≤ U+0020）；NaN / Infinity 区分大小写；可带 f F d D 后缀；
    /// 十六进制有效数字 0x1.8p1；不合文法为 null。</summary>
    public static object ParseDouble(string s)
    {
        s = s.Trim(Enumerable.Range(0, 33).Select(i => (char)i).ToArray());
        var m = s_javaDouble.Match(s);
        if (!m.Success) return null;
        bool neg = s[0] == '-';
        string body = s[0] == '+' || s[0] == '-' ? s.Substring(1) : s;
        // 文法保证：末尾的 f F d D 一定是后缀（十六进制形式以 p 指数的数字结尾）
        if ("fFdD".IndexOf(body[^1]) >= 0)
            body = body.Substring(0, body.Length - 1);
        double v;
        if (body == "NaN") v = double.NaN;
        else if (body == "Infinity") v = double.PositiveInfinity;
        else if (body.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            int p = body.IndexOfAny(new[] { 'p', 'P' });
            string mant = body.Substring(2, p - 2);
            int exp = int.Parse(body.Substring(p + 1), CultureInfo.InvariantCulture);
            int dot = mant.IndexOf('.');
            string digits = dot < 0 ? mant : mant.Remove(dot, 1);
            int fracDigits = dot < 0 ? 0 : mant.Length - dot - 1;
            var big = digits.Length == 0 ? System.Numerics.BigInteger.Zero
                : System.Numerics.BigInteger.Parse("0" + digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            v = (double)big * Math.Pow(2, exp - 4 * fracDigits);
        }
        else v = double.Parse(body, NumberStyles.Float, CultureInfo.InvariantCulture);
        return neg ? -v : v;
    }
}

