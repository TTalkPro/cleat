// Copyright (c) 2026 the IronScheme contributors
// This source code is subject to terms and conditions of the BSD License.
// See docs/license.txt.
using System;
using System.Collections.Generic;
using System.Text;

namespace Cleat.Scheme;

/// <summary>
/// Scheme 字符串（iron-055，designs/12-strings.md）：字符（Unicode 标量值）的序列，下标按码点。
/// 不可变且不含代理码元的串仍用 System.String；本类用于可变串（取代 StringBuilder），以及含 BMP 之外字符的不可变串。
/// 存储仿 PEP 393：全部字符在 BMP 内时为 char[]（窄），否则为 int[]（宽）；可变串写入 BMP 之外的字符时就地变宽。
/// </summary>
public sealed class SchemeString
{
    private char[] _narrow;   // 窄存储；宽时为 null
    private int[] _wide;      // 宽存储；窄时为 null
    private int _length;
    public readonly bool IsMutable;

    private SchemeString(char[] narrow, int[] wide, int length, bool mutable)
    {
        _narrow = narrow; _wide = wide; _length = length; IsMutable = mutable;
    }

    public int Length => _length;
    public bool IsWide => _wide != null;

    #region 构造

    /// <summary>长度为 k、每个字符为 fill 的可变串。</summary>
    public static SchemeString Make(int k, int fill)
    {
        if (fill < 0x10000)
        {
            var a = new char[k];
            if (fill != 0) Array.Fill(a, (char)fill);
            return new SchemeString(a, null, k, true);
        }
        var w = new int[k];
        Array.Fill(w, fill);
        return new SchemeString(null, w, k, true);
    }

    /// <summary>由码点构造。</summary>
    public static SchemeString FromCodePoints(IReadOnlyList<int> cps, bool mutable)
    {
        int n = cps.Count;
        bool isWide = false;
        for (int i = 0; i < n; i++) if (cps[i] >= 0x10000) { isWide = true; break; }
        if (isWide)
        {
            var w = new int[n];
            for (int i = 0; i < n; i++) w[i] = cps[i];
            return new SchemeString(null, w, n, mutable);
        }
        var a = new char[n];
        for (int i = 0; i < n; i++) a[i] = (char)cps[i];
        return new SchemeString(a, null, n, mutable);
    }

    /// <summary>UTF-16 文本 → 可变串（成对的代理组合为一个字符，孤立的代理换成 U+FFFD）。</summary>
    public static SchemeString MutableFrom(string s) => FromUtf16(s, true);

    private static SchemeString FromUtf16(string s, bool mutable)
    {
        if (!HasSurrogates(s)) return new SchemeString(s.ToCharArray(), null, s.Length, mutable);
        return FromCodePoints(CodePoints(s), mutable);
    }

    #endregion

    #region 入口规范化

    /// <summary>s 是否含代理码元（只扫描，不分配）。</summary>
    public static bool HasSurrogates(string s)
    {
        foreach (char c in s) if (char.IsSurrogate(c)) return true;
        return false;
    }

    /// <summary>
    /// 从 .NET 进入 Scheme 的字符串：不含代理码元时原样返回（Scheme 可见的 System.String 不含代理码元）；
    /// 否则转为不可变的宽串。
    /// </summary>
    public static object Normalize(string s) => s == null || !HasSurrogates(s) ? s : FromUtf16(s, false);

    /// <summary>UTF-16 → 码点（成对的代理组合，孤立的代理换成 U+FFFD）。</summary>
    public static List<int> CodePoints(string s)
    {
        var r = new List<int>(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                r.Add(char.ConvertToUtf32(c, s[i + 1]));
                i++;
            }
            else r.Add(char.IsSurrogate(c) ? 0xFFFD : c);
        }
        return r;
    }

    #endregion

    #region 访问

    public int CodePointAt(int i)
    {
        if ((uint)i >= (uint)_length) throw new ArgumentOutOfRangeException(nameof(i));
        return _wide != null ? _wide[i] : _narrow[i];
    }

    public void Set(int i, int cp)
    {
        if ((uint)i >= (uint)_length) throw new ArgumentOutOfRangeException(nameof(i));
        if (_wide != null) { _wide[i] = cp; return; }
        if (cp < 0x10000) { _narrow[i] = (char)cp; return; }
        // 就地变宽
        var w = new int[_length];
        for (int j = 0; j < _length; j++) w[j] = _narrow[j];
        w[i] = cp;
        _wide = w; _narrow = null;
    }

    public void Fill(int cp)
    {
        for (int i = 0; i < _length; i++) Set(i, cp);
    }

    #endregion

    /// <summary>UTF-16 形式（传给 .NET、打印、散列时使用）。</summary>
    public override string ToString()
    {
        if (_wide == null) return new string(_narrow, 0, _length);
        var sb = new StringBuilder(_length + 8);
        for (int i = 0; i < _length; i++)
        {
            int cp = _wide[i];
            if (cp < 0x10000) sb.Append((char)cp); else sb.Append(char.ConvertFromUtf32(cp));
        }
        return sb.ToString();
    }

    #region 对任意 Scheme 字符串表示的操作（System.String、SchemeString；过渡期兼容 StringBuilder）

    public static bool IsString(object o) => o is string || o is SchemeString || o is StringBuilder;

    public static int LengthOf(object s) => s switch
    {
        string str => str.Length,
        SchemeString ss => ss._length,
        StringBuilder sb => sb.Length,
        _ => throw new InvalidCastException()
    };

    public static int CodePointAt(object s, int i) => s switch
    {
        string str => str[i],
        SchemeString ss => ss.CodePointAt(i),
        StringBuilder sb => sb[i],
        _ => throw new InvalidCastException()
    };

    /// <summary>s[start, end) 的码点。</summary>
    public static int[] Slice(object s, int start, int end)
    {
        var r = new int[end - start];
        switch (s)
        {
            case string str: for (int i = start; i < end; i++) r[i - start] = str[i]; break;
            case SchemeString ss when ss._wide != null: Array.Copy(ss._wide, start, r, 0, end - start); break;
            case SchemeString ss: for (int i = start; i < end; i++) r[i - start] = ss._narrow[i]; break;
            case StringBuilder sb: for (int i = start; i < end; i++) r[i - start] = sb[i]; break;
            default: throw new InvalidCastException();
        }
        return r;
    }

    /// <summary>按码点比较（-1 / 0 / 1）；ignoreCase 时逐字符按 Unicode 简单折叠比较。</summary>
    public static int Compare(object a, object b, bool ignoreCase = false)
    {
        if (!ignoreCase && a is string sa && b is string sb)
        {
            // 两者都不含代理码元，按码元比较即按码点比较
            int r = string.CompareOrdinal(sa, sb);
            return r < 0 ? -1 : r > 0 ? 1 : 0;
        }
        int na = LengthOf(a), nb = LengthOf(b), n = Math.Min(na, nb);
        for (int i = 0; i < n; i++)
        {
            int x = CodePointAt(a, i), y = CodePointAt(b, i);
            if (ignoreCase) { x = Fold(x); y = Fold(y); }
            if (x != y) return x < y ? -1 : 1;
        }
        return na < nb ? -1 : na > nb ? 1 : 0;
    }

    private static int Fold(int cp)
    {
        string s = char.ConvertFromUtf32(cp).ToUpperInvariant().ToLowerInvariant();
        return char.ConvertToUtf32(s, 0);
    }

    /// <summary>内容相等（equal?）。</summary>
    public static bool ContentEquals(object a, object b)
    {
        if (a is string sa && b is string sb) return string.Equals(sa, sb, StringComparison.Ordinal);
        int n = LengthOf(a);
        if (n != LengthOf(b)) return false;
        for (int i = 0; i < n; i++) if (CodePointAt(a, i) != CodePointAt(b, i)) return false;
        return true;
    }

    #endregion
}
