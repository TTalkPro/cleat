using System;
using System.IO;
using System.Diagnostics;
using System.Reflection;
using Cleat.Core.Expand;
using clojure.lang;
using CoreHooks = Cleat.Core.Runtime.RuntimeHooks;

namespace Cleat.Core.Compiler;

/// <summary>
/// Clojure 风格的诊断（cleat-055 的诊断边界）：读 / 执行错误的源位置记录、错误文本（clojure.main/ex-str）、过滤过的栈。
/// cleat-082 从元层的 Driver 搬来，编译驱动用。
/// </summary>
public static class Diagnostics
{
    [ThreadStatic] private static int t_lastLine, t_lastColumn;
    [ThreadStatic] private static string t_lastSource;

    /// <summary>CLEAT_TRACE_LOAD：加载时把每个顶层形式的 文件:行 写到标准错误（找出没有位置的错误出在哪个形式，cleat-053）。</summary>
    private static readonly bool s_traceLoad = Environment.GetEnvironmentVariable("CLEAT_TRACE_LOAD") != null;

    private static readonly IPersistentMap s_readOpts = clojure.lang.RT.map(Keyword.intern("read-cond"), Keyword.intern("allow"));

    /// <summary>读下一个顶层形式（或 eof）。记下形式开始的行列（形式本身没有 :line 元数据时用）。</summary>
    public static object ReadForm(LineNumberingTextReader rdr, object eof)
    {
        SkipWhitespace(rdr);
        t_lastLine = rdr.LineNumber;
        t_lastColumn = rdr.ColumnNumber;
        t_lastSource = SourceLabel();
        if (s_traceLoad) Console.Error.WriteLine($"cleat: load {SourceLabel()}:{t_lastLine}");
        try { return LispReader.read(rdr, false, eof, false, s_readOpts); }
        catch (Exception e) when (StampSource(e)) { throw; }
    }

    private static void SkipWhitespace(LineNumberingTextReader r)
    {
        while (true)
        {
            int ch = r.Read();
            if (ch == -1) return;
            if (!char.IsWhiteSpace((char)ch) && ch != ',')
            {
                r.Unread(ch);
                return;
            }
        }
    }

    /// <summary>
    /// 读错误报告时 <c>*file*</c> 的绑定已经弹出（错误在 load 之外报告）：读的时候把源的名字记进 <see cref="Exception.Data"/>
    /// （cleat-055；原来文件里的读错误报成 REPL）。
    /// </summary>
    private static bool StampSource(Exception e)
    {
        try
        {
            if (!e.Data.Contains(SourceKey)) e.Data[SourceKey] = SourceLabel();
        }
        catch (Exception)
        {
            // 只是诊断信息
        }
        return false;
    }

    public const string SourceKey = "cleat.error/source";

    public const string LocationKey = "cleat.error/location";

    /// <summary>
    /// 执行顶层形式时的异常过滤器：异常不包装、原样传出（Clojure 代码的 catch 不受影响）；第一次经过（最内层的形式）时
    /// 在 <see cref="Exception.Data"/> 记下 文件:行，<see cref="ReportError"/> 据此报告位置。总是返回 false。
    /// </summary>
    public static bool Stamp(Exception e, object form)
    {
        try
        {
            if (e is ExpanderException || e.Data.Contains(LocationKey)) return false;
            // cleat-158：运行期栈上第一个带行号的用户 Clojure 源帧（序列点 + PDB）优先，指向最内层抛出点；
            // 拿不到（宿主内部抛出、无 PDB）退回顶层形式自己的 :pos
            e.Data[LocationKey] = FirstUserLocation(e) ?? FormLocation(form);
        }
        catch (Exception)
        {
            // 位置只是诊断信息
        }
        return false;
    }

    private static string FormLocation(object form)
    {
        var m = (form as IMeta)?.meta();
        object line = m?.valAt(Keyword.intern("line"));
        // 执行错误的位置只有 文件:行（Clojure 的 ex-str 对 :execution 不带列）
        return $"{SourceLabel()}:{line ?? t_lastLine}";
    }

    /// <summary>
    /// 异常栈里第一个属于用户源的帧：有行号、文件名是 .clj / .cljc（Clojure 源），且帧不属于库命名空间
    /// （clojure.* / cleat.*，与 JVM 的 clojure.main 相同：位置只报用户代码，库的 core.clj:6417 只出现在栈里）。
    /// C# 运行时帧（.cs）与没有 PDB 的帧（行号 0）跳过。
    /// </summary>
    internal static string FirstUserLocation(Exception e)
    {
        try
        {
            // 先看 root cause：外层包装可能停在调用点（如 -g 前的 g），JVM 的位置在最内层抛出点
            if (e.InnerException != null && FirstUserLocation(e.InnerException) is string inner) return inner;
            string first = null;
            foreach (var f in new StackTrace(e, true).GetFrames())
            {
                int line = f.GetFileLineNumber();
                string file = f.GetFileName();
                if (line <= 0 || string.IsNullOrEmpty(file)) continue;
                if (!file.EndsWith(".clj", StringComparison.Ordinal) && !file.EndsWith(".cljc", StringComparison.Ordinal)) continue;
                if (IsLibraryFrame(f.GetMethod()?.DeclaringType)) continue;
                // JVM 的栈报告源文件名（t.clj / core.clj），不是构建机上的绝对路径；序列点里也保留加载名
                first ??= $"{Path.GetFileName(file)}:{line}";
            }
            if (first != null) return first;
            // 宿主 / REPL 报告的可能是包装异常；JVM 的位置在最内层抛出点，pst 也取 root cause
            for (var cause = e.InnerException; cause != null; cause = cause.InnerException)
            {
                if (FirstUserLocation(cause) is string location) return location;
            }
        }
        catch (Exception)
        {
            // 位置只是诊断信息
        }
        return null;
    }

    /// <summary>当前源的名字：*file*（全路径，与语法错误相同）；REPL / -e 为 REPL。</summary>
    private static string SourceLabel() => DisplayPath(clojure.lang.RT.SourcePathVar.deref() as string);

    /// <summary>
    /// 错误文本里的源路径（clojure.main/ex-triage 的规则）：没有源文件为 REPL；当前目录之下的文件相对于当前目录
    /// （<c>cleat input.clj</c> 与 <c>cleat /全路径/input.clj</c> 都报 <c>input.clj</c>），其余原样。
    /// </summary>
    public static string DisplayPath(string file)
    {
        if (string.IsNullOrEmpty(file) || file == "NO_SOURCE_PATH" || file == "NO_SOURCE_FILE") return "REPL";
        try
        {
            if (Path.IsPathRooted(file))
            {
                string cwd = Path.GetFullPath(Directory.GetCurrentDirectory()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string full = Path.GetFullPath(file);
                if (full.StartsWith(cwd, StringComparison.Ordinal)) return full.Substring(cwd.Length).Replace('\\', '/');
            }
        }
        catch (Exception)
        {
            // 路径只是诊断信息
        }
        return file;
    }

    /// <summary>--report stdio（cleat-034，clojure.main 的语义）：错误报告写到 *out* 而不是 *err*。宿主按命令行设置。</summary>
    public static bool ReportToStdOut;

    /// <summary>登记 Clojure 风格的错误报告（写到 *err*，或 --report stdio 时的 *out*）。</summary>
    public static void InstallReportError() => CoreHooks.ReportError = ReportError;

    /// <summary>
    /// 报告错误（写到 *err*）：Clojure 的 REPL / clojure.main 只打印 ex-str 的两行，不打印栈（cleat-055 诊断边界）。
    /// 环境变量 CLEAT_TRACE_ERRORS：<c>clojure</c> 时加上过滤过的栈（<see cref="ClojureStackTrace"/>：没有编译器、驱动与
    /// 反射调用的帧，与 Clojure 过滤 clojure.lang 帧同理）；其他非空值（如 1）时加上完整的 CLR 异常与栈。
    /// </summary>
    public static void ReportError(Exception e)
    {
        TextWriter w = ReportToStdOut
            ? (TextWriter)(clojure.lang.RT.OutVar.deref() as TextWriter ?? Console.Out)
            : clojure.lang.RT.errPrintWriter();
        w.Write(FormatError(e));
        w.WriteLine();
        string trace = Environment.GetEnvironmentVariable("CLEAT_TRACE_ERRORS");
        if (!string.IsNullOrEmpty(trace))
            w.WriteLine(trace == "clojure" ? ClojureStackTrace(e) : e.ToString());
        w.Flush();
    }

    private static string NL => Environment.NewLine;

    /// <summary>
    /// Clojure 1.12（clojure.main/ex-str，与 JVM 的输出逐条对照，cleat-055）的错误文本，两行：
    /// <list type="bullet">
    /// <item>读错误：<c>Syntax error reading source at (文件:行:列).</c></item>
    /// <item>展开错误（ExpanderException，按 phase）：<c>Syntax error[ (类型)] compiling [符号 ]at (文件:行:列).</c>、
    ///   <c>Syntax error macroexpanding [符号 ]at (…).</c>、<c>Unexpected error[ (类型)] macroexpanding [符号 ]at (…).</c></item>
    /// <item>其余：<c>Execution error[ (类型)] at (文件:行).</c></item>
    /// </list>
    /// 第二行是原因。类型为 System.Exception 时省略（Clojure 省略 Exception / RuntimeException）；没有源文件时为 REPL。
    /// </summary>
    public static string FormatError(Exception e)
    {
        while (e is TargetInvocationException && e.InnerException != null) e = e.InnerException;
        switch (e)
        {
            case ExpanderException x:
                return FormatSyntaxError(x);
            case LispReader.ReaderException re:
                string src = re.Data.Contains(SourceKey) ? (string)re.Data[SourceKey] : SourceLabel();
                return $"Syntax error reading source at ({src}:{re.Line}:{re.Column}).{NL}{(re.InnerException ?? re).Message}";
        }
        string loc = e.Data.Contains(LocationKey) ? (string)e.Data[LocationKey] : SourceLabel() + ":1";
        return $"Execution error{CauseType(e)} at ({loc}).{NL}{Message(e)}";
    }

    private static string CauseType(Exception e) =>
      e == null || e.GetType() == typeof(Exception) ? "" : " (" + e.GetType().Name + ")";

    private static string FormatSyntaxError(ExpanderException x)
    {
        // 宏展开出的形式没有位置（0:0）：报最内层顶层形式的位置（Clojure 的编译器同样报外层形式的 LINE / COLUMN）
        string loc = x.Line == 0 && t_lastLine > 0 && DisplayPath(x.SourcePath) == t_lastSource && t_lastSource != "REPL"
            ? $"{DisplayPath(x.SourcePath)}:{t_lastLine}:{t_lastColumn}"
            : $"{DisplayPath(x.SourcePath)}:{x.Line}:{x.Column}";
        string sym = x.Symbol != null ? x.Symbol + " " : "";
        string reason = x.Reason ?? x.Message;
        if (x.Phase == ExpanderException.PhaseSyntax) return $"Syntax error macroexpanding {sym}at ({loc}).{NL}{reason}";
        if (x.Phase == ExpanderException.PhaseMacroexpand)
            return $"Unexpected error{CauseType(x.InnerException)} macroexpanding {sym}at ({loc}).{NL}{reason}";
        string type = x.InnerException != null ? CauseType(x.InnerException) : CompileCauseType(reason);
        return $"Syntax error{type} compiling {sym}at ({loc}).{NL}{reason}";
    }

    /// <summary>
    /// 展开器自己报的编译错误没有原因异常；JVM 的 Clojure 对其中一部分用 IllegalArgumentException / UnsupportedOperationException /
    /// IllegalStateException（ex-str 会打印类型），其余是 RuntimeException（不打印）。按原因文本对应到 CLR 的同类异常名。
    /// </summary>
    private static string CompileCauseType(string reason)
    {
        foreach (var (prefix, type) in s_compileCauseTypes)
            if (reason.StartsWith(prefix, StringComparison.Ordinal)) return " (" + type + ")";
        return "";
    }

    private static readonly (string, string)[] s_compileCauseTypes =
    {
        ("Bad binding form", "ArgumentException"),
        ("Unable to resolve classname", "ArgumentException"),
        ("fn params must be Symbols", "ArgumentException"),
        ("Malformed member expression", "ArgumentException"),
        ("Malformed assignment", "ArgumentException"),
        ("Invalid assignment target", "ArgumentException"),
        ("Cannot assign to non-mutable", "ArgumentException"),
        ("Mismatched argument count to recur", "ArgumentException"),
        ("Can't call nil", "ArgumentException"),
        ("Can only recur from tail position", "NotSupportedException"),
        ("Cannot recur across try", "NotSupportedException"),
        ("var: ", "InvalidOperationException"),
        ("Var: ", "InvalidOperationException"),
    };

    /// <summary>
    /// 过滤过的栈（CLEAT_TRACE_ERRORS=clojure）：去掉编译器与驱动（Cleat.Compiler.*、DriverOps）、单元入口（#Module$）与反射调用的帧，
    /// 留下用户代码的帧。
    /// </summary>
    public static string ClojureStackTrace(Exception e)
    {
        var sb = new System.Text.StringBuilder();
        for (var x = e; x != null; x = x.InnerException)
        {
            if (x != e) sb.Append("Caused by: ");
            sb.Append(x.GetType().FullName).Append(": ").Append(x.Message).Append(NL);
            foreach (var f in new System.Diagnostics.StackTrace(x, true).GetFrames())
            {
                var m = f.GetMethod();
                var t = m?.DeclaringType;
                if (!IsClojureFrame(t)) continue;
                sb.Append("   at ").Append(t.FullName).Append('.').Append(m.Name);
                if (f.GetFileName() is string file && f.GetFileLineNumber() > 0) sb.Append(" (").Append(Path.GetFileName(file)).Append(':').Append(f.GetFileLineNumber()).Append(')');
                sb.Append(NL);
            }
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// 库命名空间的帧（clojure.* 与 cleat.*）：JVM 报执行错误的位置时跳过它们，只报用户代码的帧。
    /// 镜像的方法带上自己的源文件后（cleat-158），不跳就会把 clojure/core.clj 的帧漏进 "at (文件:行)"。
    /// </summary>
    private static bool IsLibraryFrame(Type t)
    {
        string n = t == null ? null : t.FullName ?? t.Name;
        if (string.IsNullOrEmpty(n)) return false;
        int dollar = n.IndexOf('$');
        if (dollar >= 0) n = n.Substring(0, dollar);       // clojure.core$mapv$fn__941 → clojure.core
        return n == "clojure" || n.StartsWith("clojure.", StringComparison.Ordinal)
          || n == "cleat" || n.StartsWith("cleat.", StringComparison.Ordinal)
          || n.StartsWith("Cleat.", StringComparison.Ordinal);
    }

    public static bool IsClojureFrame(Type t)
    {
        if (t == null) return false;
        string n = t.FullName ?? t.Name;
        if (n.StartsWith("#Module", StringComparison.Ordinal)) return false;   // 单元与单元入口（顶层形式的执行）
        // 编译器与驱动（Cleat.Compiler.*，designs/30）不算用户的帧
        if (n.StartsWith("Cleat.Compiler.", StringComparison.Ordinal)
            || n.StartsWith("Cleat.Core.Compiler.DriverOps", StringComparison.Ordinal))
            return false;
        if (n.StartsWith("System.Reflection.", StringComparison.Ordinal)
            || n.StartsWith("System.RuntimeMethodHandle", StringComparison.Ordinal) || n.StartsWith("System.Runtime.ExceptionServices.", StringComparison.Ordinal))
            return false;
        return true;
    }

    /// <summary>异常的原因文本；<see cref="MessageHook"/>（可选）返回 null 时用 Message。</summary>
    public static Func<Exception, string> MessageHook;

    private static string Message(Exception e) => MessageHook?.Invoke(e) ?? e.Message;
}
