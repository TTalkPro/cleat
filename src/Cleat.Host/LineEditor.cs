using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Cleat.Host;

/// <summary>补全与提示的数据来源（REPL 提供：交互环境的 Var、命名空间与关键字的候选，Var 的参数提示）。</summary>
internal interface IReplServices
{
    /// <summary>以 prefix 开头的候选名字（Var、命名空间、关键字）。</summary>
    IReadOnlyList<string> Complete(string prefix);

    /// <summary>名字的参数提示（如 "(inc2 x)"）；没有时为 null。</summary>
    string Hint(string name);
}

/// <summary>
/// REPL 的行编辑器（cleat-034，做法沿用 IronScheme iron-038 / designs/07 §4），只用 System.Console，Windows / Linux / macOS 通用：
/// 行内编辑、多行输入（括号未闭合时换行继续）、历史、Tab 补全、行尾的参数提示、右括号匹配高亮。
/// 只编辑当前行；之前输入的续行已提交。标准输入被重定向时不使用（Repl 退回逐行读取）。
/// 词法按 Clojure：逗号是空白，括号 ( ) [ ] { }，字符字面量 \c，没有块注释。
/// </summary>
internal sealed class LineEditor
{
    private readonly IReplServices _services;
    private readonly string _historyFile;
    private readonly List<string> _history = new List<string>();
    private readonly Dictionary<string, string> _hintCache = new Dictionary<string, string>();
    private const int MaxHistory = 1000;

    // 当前输入的状态
    private readonly List<string> _committed = new List<string>();   // 已提交的行（多行输入的前几行）
    private readonly StringBuilder _line = new StringBuilder();      // 正在编辑的行
    private int _pos;            // 光标在 line 中的位置
    private string _prompt;

    public LineEditor(IReplServices services, string historyFile)
    {
        _services = services;
        _historyFile = historyFile;
        try
        {
            if (historyFile != null && File.Exists(historyFile))
                _history.AddRange(File.ReadAllLines(historyFile).Where(l => l.Length > 0).TakeLast(MaxHistory));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>读入一个完整的输入（括号平衡，可跨多行）；输入结束（空行上 Ctrl+D / Ctrl+Z）时返回 null，Ctrl+C 取消时返回空串。</summary>
    public string Read(string firstPrompt, string continuationPrompt)
    {
        _committed.Clear();
        _line.Clear();
        _pos = 0;
        _prompt = firstPrompt;
        int historyIndex = _history.Count;
        string pending = null;      // 浏览历史前正在输入的内容
        bool lastWasTab = false;
        bool treatCtrlC = Console.TreatControlCAsInput;
        Console.TreatControlCAsInput = true;
        try
        {
            StartLine();
            while (true)
            {
                var key = Console.ReadKey(true);
                bool tab = false;
                bool ctrl = (key.Modifiers & ConsoleModifiers.Control) != 0;
                switch (key.Key)
                {
                    case ConsoleKey.Enter:
                        {
                            string text = Text();
                            if (IsComplete(text))
                            {
                                _pos = _line.Length;
                                Render(false);
                                Console.WriteLine();
                                AddHistory(text);
                                return text;
                            }
                            // 括号未闭合：提交当前行，换行继续
                            _pos = _line.Length;
                            Render(false);
                            Console.WriteLine();
                            _committed.Add(_line.ToString());
                            _line.Clear();
                            _pos = 0;
                            _prompt = continuationPrompt;
                            StartLine();
                            break;
                        }
                    case ConsoleKey.Tab:
                        tab = true;
                        Complete(lastWasTab);
                        break;
                    case ConsoleKey.Backspace:
                        if (ctrl) DeleteWordBackward();
                        else if (_pos > 0) { _line.Remove(_pos - 1, 1); _pos--; Render(); }
                        break;
                    case ConsoleKey.Delete:
                        if (_pos < _line.Length) { _line.Remove(_pos, 1); Render(); }
                        break;
                    case ConsoleKey.LeftArrow:
                        _pos = ctrl ? WordStart(_pos) : Math.Max(0, _pos - 1);
                        Render();
                        break;
                    case ConsoleKey.RightArrow:
                        _pos = ctrl ? WordEnd(_pos) : Math.Min(_line.Length, _pos + 1);
                        Render();
                        break;
                    case ConsoleKey.Home: _pos = 0; Render(); break;
                    case ConsoleKey.End: _pos = _line.Length; Render(); break;
                    case ConsoleKey.UpArrow:
                    case ConsoleKey.DownArrow:
                        {
                            if (_committed.Count > 0 || _history.Count == 0) break;
                            if (historyIndex == _history.Count) pending = _line.ToString();
                            historyIndex = key.Key == ConsoleKey.UpArrow ? Math.Max(0, historyIndex - 1) : Math.Min(_history.Count, historyIndex + 1);
                            SetLine(historyIndex == _history.Count ? pending ?? "" : _history[historyIndex]);
                            break;
                        }
                    case ConsoleKey.Escape:
                        SetLine("");
                        break;
                    default:
                        if (ctrl && HandleControl(key.Key, out bool finish, out var result))
                        {
                            if (finish) return result;
                            break;
                        }
                        if (key.KeyChar >= ' ' && !ctrl)
                        {
                            _line.Insert(_pos, key.KeyChar);
                            _pos++;
                            Render();
                            if (key.KeyChar == ')' || key.KeyChar == ']' || key.KeyChar == '}') FlashMatch();
                        }
                        break;
                }
                lastWasTab = tab;
            }
        }
        finally
        {
            Console.TreatControlCAsInput = treatCtrlC;
        }
    }

    /// <summary>Ctrl 组合键；返回真表示已处理。finish 为真时 Read 返回 result（Ctrl+C 取消返回空串；空输入上 Ctrl+D / Ctrl+Z 返回 null 表示输入结束）。</summary>
    private bool HandleControl(ConsoleKey k, out bool finish, out string result)
    {
        finish = false;
        result = null;
        switch (k)
        {
            case ConsoleKey.A: _pos = 0; Render(); return true;
            case ConsoleKey.E: _pos = _line.Length; Render(); return true;
            case ConsoleKey.B: _pos = Math.Max(0, _pos - 1); Render(); return true;
            case ConsoleKey.F: _pos = Math.Min(_line.Length, _pos + 1); Render(); return true;
            case ConsoleKey.K: _line.Remove(_pos, _line.Length - _pos); Render(); return true;
            case ConsoleKey.U: _line.Remove(0, _pos); _pos = 0; Render(); return true;
            case ConsoleKey.W: DeleteWordBackward(); return true;
            case ConsoleKey.L:
                Console.Clear();
                foreach (var l in _committed) Console.WriteLine(l);
                StartLine();
                return true;
            case ConsoleKey.C:
                _pos = _line.Length;
                Render(false);
                Console.WriteLine("^C");
                finish = true;
                result = "";
                return true;
            case ConsoleKey.D:
            case ConsoleKey.Z:   // Windows 的输入结束
                if (_line.Length == 0 && _committed.Count == 0)
                {
                    Console.WriteLine();
                    finish = true;   // result 为 null：输入结束
                    return true;
                }
                if (k == ConsoleKey.D && _pos < _line.Length) { _line.Remove(_pos, 1); Render(); }
                return true;
        }
        return false;
    }

    private string Text() => _committed.Count == 0 ? _line.ToString() : string.Join("\n", _committed) + "\n" + _line;

    private void SetLine(string s)
    {
        _line.Clear();
        _line.Append(s);
        _pos = _line.Length;
        Render();
    }

    private void AddHistory(string text)
    {
        string entry = string.Join(" ", text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
        if (entry.Length == 0 || (_history.Count > 0 && _history[^1] == entry)) return;
        _history.Add(entry);
        if (_history.Count > MaxHistory) _history.RemoveAt(0);
        try
        {
            if (_historyFile != null) File.AppendAllLines(_historyFile, new[] { entry });
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ———————————————— 绘制 ————————————————

    private static int Width => Math.Max(20, Console.BufferWidth > 0 ? Console.BufferWidth : 80);

    private static int Height
    {
        get
        {
            try { return Console.BufferHeight > 0 ? Console.BufferHeight : int.MaxValue; }
            catch (IOException) { return int.MaxValue; }
        }
    }

    // 光标位置：Unix 终端上不查询（查询要与终端往返，有的伪终端不回应，等待回应时会吞掉按键），
    // 由编辑器记录光标相对行起点的行列，用 ANSI 相对移动定位、"清除到屏幕末尾"清残留；终端滚动不影响相对移动。
    // Windows 控制台的光标查询是本地 API 调用，用绝对定位。
    private static readonly bool s_ansi = !OperatingSystem.IsWindows();
    private int _row, _col;       // ANSI：光标相对行起点的行列
    private int _top;            // Windows：行起点所在的缓冲区行
    private int _lastLength;     // Windows：上次绘制的字符数（用空格清除残留）

    /// <summary>开始绘制新的一行：调用时光标在一行的开头。</summary>
    private void StartLine()
    {
        _row = _col = 0;
        _lastLength = 0;
        if (!s_ansi) _top = Math.Max(0, CursorTop());
        Render();
    }

    /// <summary>重绘当前行（提示符 + 文本 + 暗色的提示），清除残留，把光标放回 pos。</summary>
    private void Render(bool withHint = true)
    {
        int width = Width;
        string hint = withHint && _pos == _line.Length ? CurrentHint() : null;
        MoveTo(0);
        Console.Write(_prompt);
        Console.Write(_line.ToString());
        int length = _prompt.Length + _line.Length;
        if (hint != null)
        {
            var color = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write("  " + hint);
            Console.ForegroundColor = color;
            length += 2 + hint.Length;
        }
        if (s_ansi)
        {
            Console.Write("\x1b[J");   // 清除到屏幕末尾
                                       // 写满整行时终端停在行末（延迟换行），按此记录位置
            _row = length == 0 ? 0 : (length - 1) / width;
            _col = length == 0 ? 0 : (length % width == 0 ? width - 1 : length % width);
        }
        else
        {
            if (_lastLength > length) Console.Write(new string(' ', _lastLength - length));
            int written = Math.Max(length, _lastLength);
            int rows = written == 0 ? 0 : (written - 1) / width;
            if (_top + rows > Height - 1) _top = Math.Max(0, Height - 1 - rows);
            _lastLength = length;
        }
        MoveTo(_prompt.Length + _pos);
    }

    /// <summary>把光标移到当前行（含提示符）的第 offset 个字符处。</summary>
    private void MoveTo(int offset)
    {
        int width = Width, r = offset / width, c = offset % width;
        if (s_ansi)
        {
            var sb = new StringBuilder("\r");
            if (r < _row) sb.Append("\x1b[").Append(_row - r).Append('A');
            else if (r > _row) sb.Append("\x1b[").Append(r - _row).Append('B');
            if (c > 0) sb.Append("\x1b[").Append(c).Append('C');
            Console.Write(sb.ToString());
            _row = r;
            _col = c;
        }
        else
        {
            try { Console.SetCursorPosition(c, Math.Min(Math.Max(0, _top + r), Height - 1)); }
            catch (ArgumentOutOfRangeException) { }
            catch (IOException) { }
        }
    }

    private static int CursorTop()
    {
        try { return Console.CursorTop; }
        catch (IOException) { return 0; }
    }

    /// <summary>右括号匹配：短暂把光标移到与之匹配的左括号（只在当前行内）。</summary>
    private void FlashMatch()
    {
        int match = MatchingOpen(_line.ToString(), _pos - 1);
        if (match < 0) return;
        MoveTo(_prompt.Length + match);
        for (int i = 0; i < 15 && !Console.KeyAvailable; i++) System.Threading.Thread.Sleep(20);
        MoveTo(_prompt.Length + _pos);
    }

    // ———————————————— 补全与提示 ————————————————

    private void Complete(bool listCandidates)
    {
        string before = Text().Substring(0, Text().Length - (_line.Length - _pos));
        int start = CompletionStart(before);
        // start 是在整个输入中的位置；补全只作用于当前行
        int lineStart = before.Length - _pos;
        if (start < lineStart) return;
        string prefix = before.Substring(start);
        var candidates = _services.Complete(prefix);
        if (candidates.Count == 0) return;
        string common = CommonPrefix(candidates);
        if (candidates.Count == 1)
        {
            // 别名补成 "cstr/" 时不加空格：接着按 Tab 补成员
            Replace(start - lineStart, candidates[0].EndsWith('/') ? candidates[0] : candidates[0] + " ");
            return;
        }
        if (common.Length > prefix.Length)
        {
            Replace(start - lineStart, common);
            return;
        }
        if (!listCandidates) return;
        // 第二次 Tab：在下方列出候选，再重绘输入
        _pos = _line.Length;
        Render(false);
        Console.WriteLine();
        int width = Width, col = Math.Min(width - 1, candidates.Max(s => s.Length) + 2), perRow = Math.Max(1, width / col);
        const int MaxShown = 200;
        var shown = candidates.Take(MaxShown).ToList();
        for (int i = 0; i < shown.Count; i++)
        {
            Console.Write(shown[i].PadRight(col));
            if ((i + 1) % perRow == 0 || i == shown.Count - 1) Console.WriteLine();
        }
        if (candidates.Count > MaxShown) Console.WriteLine($"… {candidates.Count - MaxShown} more");
        foreach (var l in _committed) Console.WriteLine(l);
        StartLine();
    }

    private void Replace(int from, string text)
    {
        _line.Remove(from, _pos - from);
        _line.Insert(from, text);
        _pos = from + text.Length;
        Render();
    }

    private string CurrentHint()
    {
        string name = EnclosingHead(Text());
        if (name == null) return null;
        if (!_hintCache.TryGetValue(name, out var hint))
        {
            try { hint = _services.Hint(name); } catch (Exception) { hint = null; }
            _hintCache[name] = hint;
        }
        return hint;
    }

    private static string CommonPrefix(IReadOnlyList<string> xs)
    {
        string p = xs[0];
        foreach (var s in xs)
        {
            int i = 0;
            while (i < p.Length && i < s.Length && p[i] == s[i]) i++;
            p = p.Substring(0, i);
        }
        return p;
    }

    // ———————————————— 简单的词法扫描（跳过字符串、字符、注释、正则字面量） ————————————————

    private static bool IsDelimiter(char c) => char.IsWhiteSpace(c) || c is '(' or ')' or '[' or ']' or '{' or '}' or '"' or ';' or '\\' or '@' or '^' or '`' or '~' || c == '\'';

    private int WordStart(int i)
    {
        while (i > 0 && IsDelimiter(_line[i - 1])) i--;
        while (i > 0 && !IsDelimiter(_line[i - 1])) i--;
        return i;
    }

    private int WordEnd(int i)
    {
        while (i < _line.Length && IsDelimiter(_line[i])) i++;
        while (i < _line.Length && !IsDelimiter(_line[i])) i++;
        return i;
    }

    private void DeleteWordBackward()
    {
        int s = WordStart(_pos);
        _line.Remove(s, _pos - s);
        _pos = s;
        Render();
    }

    /// <summary>扫描文本，返回仍未闭合的左括号位置（栈底在前）；inString 等表示扫描结束时的状态。逗号是空白，字符字面量 \x 跳一个字符。</summary>
    private static List<int> OpenParens(string s, out bool inString)
    {
        var stack = new List<int>();
        inString = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (inString)
            {
                if (c == '\\') i++;
                else if (c == '"') inString = false;
                continue;
            }
            switch (c)
            {
                case '"': inString = true; break;
                case ';': while (i < s.Length && s[i] != '\n') i++; break;
                case '\\': i++; break;                        // 字符字面量 \( \a \newline 的开头
                case '#':
                    if (i + 1 < s.Length && s[i + 1] == '\'') i++;   // var #'x：名字里的引号不算定界
                    break;
                case '(': case '[': case '{': stack.Add(i); break;
                case ')': case ']': case '}': if (stack.Count > 0) stack.RemoveAt(stack.Count - 1); break;
            }
        }
        return stack;
    }

    /// <summary>输入是否完整：括号平衡、不在字符串中，且有非空白内容。</summary>
    internal static bool IsComplete(string s)
    {
        if (s.Trim().Length == 0) return true;
        var open = OpenParens(s, out bool inString);
        return open.Count == 0 && !inString;
    }

    /// <summary>与 close 处的右括号匹配的左括号位置；没有时为 -1。</summary>
    private static int MatchingOpen(string s, int close)
    {
        var open = OpenParens(s.Substring(0, close), out bool inString);
        return inString || open.Count == 0 ? -1 : open[^1];
    }

    /// <summary>补全的起点：光标前的词（符号 / 关键字 / 限定的 ns/name 整体，'/' 属于词的一部分）。</summary>
    internal static int CompletionStart(string before)
    {
        OpenParens(before, out bool inString);
        if (inString) return before.Length;
        int start = before.Length;
        while (start > 0 && !IsDelimiter(before[start - 1])) start--;
        return start;
    }

    /// <summary>光标所在（最内层未闭合）列表的首个符号；光标仍在首符号上时为 null。</summary>
    internal static string EnclosingHead(string before)
    {
        var open = OpenParens(before, out bool inString);
        if (inString || open.Count == 0) return null;
        int p = open[^1] + 1, e = p;
        while (e < before.Length && !IsDelimiter(before[e])) e++;
        if (e == p || e >= before.Length) return null;   // 还在输入首符号
        return before.Substring(p, e - p);
    }
}
