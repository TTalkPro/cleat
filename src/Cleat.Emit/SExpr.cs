using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Cleat.Emit;

// Lcil 的极简 S 表达式读取器（designs/05-cil-backend.md，格式见 LCIL.md §1），不依赖 Clojure reader。从作者自己的 IronScheme.Emit（TTalkPro/IronScheme 分支，同一作者，无上游代码）的 SExpr.cs 移植。
// 每个节点带源位置，供校验报错。

public abstract class SNode
{
    public int Line, Col;
    public string Where(string file) => $"{file}:{Line}:{Col}";
}

public sealed class SList : SNode
{
    public readonly List<SNode> Items = new List<SNode>();
    public int Count => Items.Count;
    public SNode this[int i] => Items[i];
    /// <summary>首项为符号时返回其名字，否则 null。</summary>
    public string Head => Items.Count > 0 && Items[0] is SSym s ? s.Name : null;
    public override string ToString() => "(" + string.Join(" ", Items) + ")";
}

public sealed class SSym : SNode
{
    public readonly string Name;
    public SSym(string name) { Name = name; }
    public override string ToString() => Name;
}

public sealed class SStr : SNode
{
    public readonly string Value;
    public SStr(string value) { Value = value; }
    public override string ToString() => "\"" + Value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}

public sealed class SInt : SNode
{
    public readonly long Value;
    public SInt(long value) { Value = value; }
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

public sealed class SReal : SNode
{
    public readonly double Value;
    public SReal(double value) { Value = value; }
    public override string ToString() => Value.ToString("R", CultureInfo.InvariantCulture);
}

public sealed class SBool : SNode
{
    public readonly bool Value;
    public SBool(bool value) { Value = value; }
    public override string ToString() => Value ? "#t" : "#f";
}

public sealed class LcilException : Exception
{
    public LcilException(string message) : base(message) { }
}

public sealed class SExprReader
{
    private readonly string _text, _file;
    private int _pos, _line = 1, _col = 1;

    public SExprReader(string text, string file) { _text = text; _file = file ?? "lcil"; }

    private LcilException Error(string msg, int l, int c) => new LcilException($"{_file}:{l}:{c}: {msg}");

    /// <summary>读全部数据。</summary>
    public List<SNode> ReadAll()
    {
        var r = new List<SNode>();
        while (true)
        {
            SkipAtmosphere();
            if (_pos >= _text.Length) return r;
            r.Add(ReadItem());
        }
    }

    private int Peek() => _pos < _text.Length ? _text[_pos] : -1;

    private char Next()
    {
        char c = _text[_pos++];
        if (c == '\n') { _line++; _col = 1; } else _col++;
        return c;
    }

    private void SkipAtmosphere()
    {
        while (_pos < _text.Length)
        {
            char c = _text[_pos];
            if (char.IsWhiteSpace(c)) Next();
            else if (c == ';') { while (_pos < _text.Length && _text[_pos] != '\n') Next(); }
            else if (c == '#' && _pos + 1 < _text.Length && _text[_pos + 1] == '|')
            {
                int l = _line, cc = _col;
                Next(); Next();
                while (true)
                {
                    if (_pos >= _text.Length) throw Error("unterminated block comment", l, cc);
                    if (_text[_pos] == '|' && _pos + 1 < _text.Length && _text[_pos + 1] == '#') { Next(); Next(); break; }
                    Next();
                }
            }
            else return;
        }
    }

    private SNode ReadItem()
    {
        SkipAtmosphere();
        int l = _line, c = _col;
        if (_pos >= _text.Length) throw Error("unexpected end of input", l, c);
        char ch = _text[_pos];
        SNode n;
        if (ch == '(' || ch == '[')
        {
            char close = ch == '(' ? ')' : ']';
            Next();
            var list = new SList();
            while (true)
            {
                SkipAtmosphere();
                if (_pos >= _text.Length) throw Error("unterminated list", l, c);
                if (_text[_pos] == ')' || _text[_pos] == ']')
                {
                    if (_text[_pos] != close) throw Error("mismatched bracket", _line, _col);
                    Next();
                    break;
                }
                list.Items.Add(ReadItem());
            }
            n = list;
        }
        else if (ch == ')' || ch == ']') throw Error("unexpected '" + ch + "'", l, c);
        else if (ch == '"') n = ReadString(l, c);
        else n = ReadAtom(l, c);
        n.Line = l; n.Col = c;
        return n;
    }

    private SNode ReadString(int l, int c)
    {
        Next();
        var sb = new StringBuilder();
        while (true)
        {
            if (_pos >= _text.Length) throw Error("unterminated string", l, c);
            char ch = Next();
            if (ch == '"') return new SStr(sb.ToString());
            if (ch != '\\') { sb.Append(ch); continue; }
            if (_pos >= _text.Length) throw Error("unterminated string", l, c);
            char e = Next();
            switch (e)
            {
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'x':
                    {
                        int semi = _text.IndexOf(';', _pos);
                        if (semi < 0 || !int.TryParse(_text.AsSpan(_pos, semi - _pos), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int cp)
                            || cp > 0x10FFFF || (cp >= 0xD800 && cp <= 0xDFFF))
                            throw Error("bad \\x escape in string", _line, _col);
                        sb.Append(char.ConvertFromUtf32(cp));
                        while (_pos <= semi) Next();
                        break;
                    }
                default: throw Error("unknown string escape \\" + e, _line, _col);
            }
        }
    }

    private SNode ReadAtom(int l, int c)
    {
        int start = _pos;
        while (_pos < _text.Length)
        {
            char ch = _text[_pos];
            if (char.IsWhiteSpace(ch) || ch == '(' || ch == ')' || ch == '[' || ch == ']' || ch == '"' || ch == ';') break;
            Next();
        }
        string s = _text.Substring(start, _pos - start);
        if (s == "#t") return new SBool(true);
        if (s == "#f") return new SBool(false);
        if (s.StartsWith("#x", StringComparison.Ordinal) || s.StartsWith("#X", StringComparison.Ordinal))
        {
            if (long.TryParse(s.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out long h)) return new SInt(h);
            throw Error("bad hexadecimal number: " + s, l, c);
        }
        if (s.Length > 0 && (char.IsDigit(s[0]) || ((s[0] == '-' || s[0] == '+' || s[0] == '.') && s.Length > 1 && (char.IsDigit(s[1]) || s[1] == '.'))))
        {
            if (long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long i)) return new SInt(i);
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return new SReal(d);
            throw Error("bad number: " + s, l, c);
        }
        if (s.StartsWith("#", StringComparison.Ordinal)) throw Error("unsupported syntax: " + s, l, c);
        return new SSym(s);
    }
}
