// Copyright (c) 2026 the IronScheme contributors
// This source code is subject to terms and conditions of the BSD License.
// See docs/license.txt.
using System.Collections.Generic;
using System.Text;

namespace Cleat.Scheme;

/// <summary>
/// 字符串的基本操作（iron-055，designs/12-strings.md §4）：按码点，适用于 System.String 与 SchemeString
/// （过渡期兼容 StringBuilder）。由 boot/base.sls 等经 clr-static-call 调用，参数检查在 Scheme 侧的 define/contract 中。
/// </summary>
public partial class Builtins
{
    /// <summary>Scheme 字符对象 → 码点。</summary>
    public static int CharCode(object ch) => ch switch
    {
        char c => c,
        SchemeChar sc => sc.Value,
        _ => (int)AssertionViolation("char->integer", "not a char", ch)
    };

    public static bool IsSchemeString(object o) => SchemeString.IsString(o);

    public static int StringLength(object s) => SchemeString.LengthOf(s);

    public static object StringRef(object s, int k)
    {
        if ((uint)k >= (uint)SchemeString.LengthOf(s)) return AssertionViolation("string-ref", "index out of range", k);
        return CharObject(SchemeString.CodePointAt(s, k));
    }

    public static object StringSet(object s, int k, object ch)
    {
        int cp = CharCode(ch);
        switch (s)
        {
            case SchemeString ss when ss.IsMutable:
                if ((uint)k >= (uint)ss.Length) return AssertionViolation("string-set!", "index out of range", k);
                ss.Set(k, cp);
                return Cleat.Scheme.Unspecified.Instance;
            case StringBuilder sb when cp < 0x10000:   // 旧种子过渡期
                sb[k] = (char)cp;
                return Cleat.Scheme.Unspecified.Instance;
            default:
                return AssertionViolation("string-set!", "not a mutable string", s);
        }
    }

    public static object StringFill(object s, object ch)
    {
        if (s is SchemeString ss && ss.IsMutable) { ss.Fill(CharCode(ch)); return Cleat.Scheme.Unspecified.Instance; }
        return AssertionViolation("string-fill!", "not a mutable string", s);
    }

    /// <summary>长度为 k、字符为 fill 的可变串。</summary>
    public static object MakeString(int k, object fill) => SchemeString.Make(k, CharCode(fill));

    /// <summary>由字符组成的可变串（string、list->string）。</summary>
    public static object CharsToString(object chars)
    {
        var cps = new List<int>();
        for (object x = chars; x is Cons c; x = c.cdr) cps.Add(CharCode(c.car));
        return SchemeString.FromCodePoints(cps, true);
    }

    /// <summary>可变副本。</summary>
    public static object StringCopy(object s) =>
      s is SchemeString ss && !ss.IsWide ? SchemeString.MutableFrom(ss.ToString())
      : SchemeString.FromCodePoints(SchemeString.Slice(s, 0, SchemeString.LengthOf(s)), true);

    /// <summary>s[start, end)：System.String 与不可变宽串得到不可变串，可变串得到可变串（与原实现一致）。</summary>
    public static object Substring(object s, int start, int end)
    {
        if (s is string str) return str.Substring(start, end - start);
        var cps = SchemeString.Slice(s, start, end);
        bool mutable = s is StringBuilder || (s is SchemeString ss && ss.IsMutable);
        return mutable ? SchemeString.FromCodePoints(cps, true) : Immutable(cps);
    }

    /// <summary>拼接（不可变结果，与原实现一致）。</summary>
    public static object StringAppend(object args)
    {
        // 两个宿主字符串（最常见）直接 Concat，不建中间表（iron-067）
        if (args is Cons c1 && c1.car is string s1 && c1.cdr is Cons c2 && c2.car is string s2 && c2.cdr is EmptyList)
            return string.Concat(s1, s2);
        int n = 0;
        bool allPlain = true;
        for (object x = args; x is Cons c; x = c.cdr)
        {
            n++;
            if (!(c.car is string)) allPlain = false;
        }
        if (allPlain)
        {
            var arr = new string[n];
            int i = 0;
            for (object x = args; x is Cons c; x = c.cdr) arr[i++] = (string)c.car;
            return string.Concat(arr);
        }
        var parts = new List<object>(n);
        for (object x = args; x is Cons c; x = c.cdr) parts.Add(c.car);
        var cps = new List<int>();
        foreach (var p in parts) cps.AddRange(SchemeString.Slice(p, 0, SchemeString.LengthOf(p)));
        return Immutable(cps);
    }

    /// <summary>码点 → 不可变串：全部在 BMP 内时为 System.String，否则为不可变宽串。</summary>
    private static object Immutable(IReadOnlyList<int> cps)
    {
        bool wide = false;
        foreach (int cp in cps) if (cp >= 0x10000) { wide = true; break; }
        if (wide) return SchemeString.FromCodePoints(cps, false);
        var chars = new char[cps.Count];
        for (int i = 0; i < chars.Length; i++) chars[i] = (char)cps[i];
        return new string(chars);
    }

    /// <summary>BMP 之外字符的大小写映射（iron-055；原来 char-upcase 等对它们原样返回）。</summary>
    public static object CharUpcaseWide(object ch) =>
      CharObject(System.Text.Rune.ToUpperInvariant(new System.Text.Rune(CharCode(ch))).Value);

    public static object CharDowncaseWide(object ch) =>
      CharObject(System.Text.Rune.ToLowerInvariant(new System.Text.Rune(CharCode(ch))).Value);

    /// <summary>按码点比较，-1 / 0 / 1。</summary>
    public static int StringCompare(object a, object b) => SchemeString.Compare(a, b);

    public static int StringCompareCi(object a, object b) => SchemeString.Compare(a, b, ignoreCase: true);

    /// <summary>内容相等。</summary>
    public static bool StringEquals(object a, object b) => SchemeString.ContentEquals(a, b);
}

/// <summary>
/// 文本端口按码点读取（iron-055）：TextReader 给出 UTF-16 码元，这里把代理对组合成一个字符。
/// lookahead-char 遇到高代理时须读出两个码元，读出的字符记为该 reader 的待取字符，之后的各种读取先取它。
/// </summary>
public static class TextPorts
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<System.IO.TextReader, object> s_pending = new();
    private static int s_pendingCount;   // 有待取字符的 reader 个数：为 0 时不查表

    private static bool TakePending(System.IO.TextReader r, out int cp)
    {
        cp = 0;
        if (s_pendingCount == 0 || !s_pending.TryGetValue(r, out var box)) return false;
        s_pending.Remove(r);
        System.Threading.Interlocked.Decrement(ref s_pendingCount);
        cp = (int)box;
        return true;
    }

    /// <summary>读一个码点；文件尾为 -1。孤立的代理换成 U+FFFD。</summary>
    private static int ReadCodePoint(System.IO.TextReader r)
    {
        if (TakePending(r, out int p)) return p;
        int c = r.Read();
        if (c < 0 || !char.IsSurrogate((char)c)) return c;
        if (char.IsHighSurrogate((char)c))
        {
            int d = r.Peek();
            if (d >= 0 && char.IsLowSurrogate((char)d)) { r.Read(); return char.ConvertToUtf32((char)c, (char)d); }
        }
        return 0xFFFD;
    }

    public static object ReadChar(System.IO.TextReader r)
    {
        int cp = ReadCodePoint(r);
        return cp < 0 ? Builtins.EofObject() : Builtins.CharObject(cp);
    }

    public static object PeekChar(System.IO.TextReader r)
    {
        if (s_pendingCount != 0 && s_pending.TryGetValue(r, out var box)) return Builtins.CharObject((int)box);
        int c = r.Peek();
        if (c < 0) return Builtins.EofObject();
        if (!char.IsSurrogate((char)c)) return (char)c;
        int cp = ReadCodePoint(r);   // 读出整个字符，记为待取
        s_pending.AddOrUpdate(r, cp);
        System.Threading.Interlocked.Increment(ref s_pendingCount);
        return Builtins.CharObject(cp);
    }

    /// <summary>读至多 n 个字符；一个也没有时为文件尾。</summary>
    public static object ReadString(System.IO.TextReader r, int n)
    {
        var cps = new List<int>(n);
        while (cps.Count < n)
        {
            int cp = ReadCodePoint(r);
            if (cp < 0) break;
            cps.Add(cp);
        }
        if (cps.Count == 0 && n > 0) return Builtins.EofObject();
        return Codepoints(cps);
    }

    /// <summary>get-line：读到换行符为止，只认换行符（R6RS 8.2.9，iron-074；TextReader.ReadLine 把 CR 也当行尾）。
    /// 行尾转换由转码端口在字符层做；字符串端口不转换，"a\r\nb" 的第一行是 "a\r"。</summary>
    public static object ReadLine(System.IO.TextReader r)
    {
        string head = TakePending(r, out int p) ? char.ConvertFromUtf32(p) : null;
        if (head == "\n") return "";
        // 转码端口自己按块找行尾；其余（字符串端口等）逐字符读到换行符
        string line = r is TranscodedReader tr ? tr.ReadLine() : IO.ReadLineLf(r);
        if (line == null) return head == null ? Builtins.EofObject() : SchemeString.Normalize(head);
        return SchemeString.Normalize(head + line);
    }

    public static object ReadAll(System.IO.TextReader r)
    {
        string head = TakePending(r, out int p) ? char.ConvertFromUtf32(p) : "";
        string rest = r.ReadToEnd();
        if (head.Length == 0 && rest.Length == 0) return Builtins.EofObject();
        return SchemeString.Normalize(head + rest);
    }

    private static object Codepoints(List<int> cps)
    {
        foreach (int cp in cps) if (cp >= 0x10000) return SchemeString.FromCodePoints(cps, false);
        var a = new char[cps.Count];
        for (int i = 0; i < a.Length; i++) a[i] = (char)cps[i];
        return new string(a);
    }
}
