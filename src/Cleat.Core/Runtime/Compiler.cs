using System;
using System.IO;
using Cleat.Core.Expand;
using Cleat.Core.Runtime;

namespace clojure.lang;

/// <summary>
/// <c>clojure.lang.Compiler</c> 门面（cleat-189）：cleat 的编译器是 C#（Cleat.Compiler，按需加载），没有 JVM / ClojureCLR 的
/// Compiler 类；这里只提供库与测试常用的公开成员，转到 cleat 的对应物：
/// <list type="bullet">
/// <item><see cref="CompilerException"/>：cleat 的分析错误（<see cref="ExpanderException"/>）就是它的子类，<c>(catch Compiler+CompilerException e …)</c> 能接住。</item>
/// <item><see cref="munge"/> / <see cref="demunge"/> / <see cref="CHAR_MAP"/>：<see cref="Munge"/>。</item>
/// <item><see cref="eval"/> / <see cref="load(TextReader)"/> / <see cref="loadFile"/> / <see cref="macroexpand1"/>：运行时钩子（<see cref="RuntimeHooks"/>、<see cref="Loader"/>）。</item>
/// <item>动态 Var：<see cref="SOURCE"/> = <c>*source-path*</c>、<see cref="SOURCE_PATH"/> = <c>*file*</c>（ClojureCLR 名 SourceVar / SourcePathVar 同值）。</item>
/// </list>
/// 分析器内部（<c>analyze</c>、<c>$LocalBinding</c>、<c>$ObjExpr</c>、<c>LOADER</c>、<c>METHOD</c>、<c>LINE</c> …）不提供，见 designs/03 §4.2。
/// </summary>
public static class Compiler
{
    /// <summary>
    /// 编译期错误（语法、未解析的符号 / 类名、宏展开抛出的异常）。消息是 <c>Syntax error (文件:行:列) 原因</c>，
    /// <c>ex-data</c> 带 <c>:clojure.error/source</c>、<c>line</c>、<c>column</c>、<c>phase</c>（与 <c>symbol</c>）。
    /// 宏抛出的异常在 <see cref="Exception.InnerException"/> 里。执行期的异常不包装（与 JVM 的 load 不同，见 designs/03 §4.2）。
    /// </summary>
    public class CompilerException : Exception, IExceptionInfo
    {
        // the :clojure.error/phase values are plain keywords, as Compiler.PHASE_* on the JVM (clojure.main/ex-triage
        // dispatches on :compile-syntax-check …); cleat-069 found them namespaced, which made ex-str throw "No matching clause"
        public static readonly Keyword PHASE_READ = Keyword.intern(null, "read-source"),
          PHASE_MACRO_SYNTAX_CHECK = Keyword.intern(null, "macro-syntax-check"),
          PHASE_MACROEXPANSION = Keyword.intern(null, "macroexpansion"),
          PHASE_COMPILE_SYNTAX_CHECK = Keyword.intern(null, "compile-syntax-check"),
          PHASE_COMPILATION = Keyword.intern(null, "compilation"),
          PHASE_EXECUTION = Keyword.intern(null, "execution");

        public string SourcePath { get; }
        public int Line { get; }
        public int Column { get; }
        public Keyword Phase { get; }

        /// <summary>原因文本（不含位置前缀）：Clojure 的 ex-str 把它放在第二行（cleat-055）。</summary>
        public string Reason { get; }

        /// <summary>
        /// 出错形式的运算符（Clojure 的 CompilerException.symbol）：特殊形式名（"compiling recur at"）或宏的符号（"macroexpanding m at"）；
        /// 普通调用与符号为 null（"compiling at"）。
        /// </summary>
        public string Symbol { get; }

        /// <summary>JVM 的公开字段 <c>source</c> / <c>line</c>。</summary>
        public string source => SourcePath;
        public int line => Line;

        public CompilerException(string sourcePath, int line, int column, Keyword phase, string message, Exception cause, string symbol)
            : base(Format(sourcePath, line, column, message), cause)
        {
            SourcePath = sourcePath; Line = line; Column = column; Phase = phase; Reason = message; Symbol = symbol;
        }

        /// <summary>JVM 的 <c>(source, line, column, cause)</c>：编译期语法错误，原因取 cause 的消息。</summary>
        public CompilerException(string sourcePath, int line, int column, Exception cause)
            : this(sourcePath, line, column, PHASE_COMPILE_SYNTAX_CHECK, cause?.Message, cause, null)
        {
        }

        private static string Format(string path, int line, int column, string message) =>
          $"Syntax error ({path ?? "NO_SOURCE_PATH"}:{line}:{column}) {message}";

        public IPersistentMap getData()
        {
            var m = RT.map(
              Keyword.intern("clojure.error", "source"), SourcePath, Keyword.intern("clojure.error", "line"), (long)Line,
              Keyword.intern("clojure.error", "column"), (long)Column, Keyword.intern("clojure.error", "phase"), Phase);
            return Symbol == null ? m : m.assoc(Keyword.intern("clojure.error", "symbol"), clojure.lang.Symbol.intern(Symbol));
        }
    }

    // —— 名字改写 ——

    public static IPersistentMap CHAR_MAP => Munge.CHAR_MAP;

    public static string munge(string name) => Munge.munge(name);

    public static string demunge(string mungedName) => Munge.demunge(mungedName);

    // —— 特殊形式 ——

    /// <summary>特殊形式表（JVM 是 符号 → 解析器 的映射；这里值为 nil，只用来查键）。</summary>
    public static readonly IPersistentMap specials = BuildSpecials();

    private static IPersistentMap BuildSpecials()
    {
        IPersistentMap m = PersistentHashMap.EMPTY;
        for (ISeq s = RT.seq(SpecialForms.Specials); s != null; s = s.next()) m = m.assoc(s.first(), null);
        return m;
    }

    // —— 求值 / 加载 ——

    public static object eval(object form) =>
        RuntimeHooks.Require(RuntimeHooks.Eval, "eval")(form);

    public static object eval(object form, bool freshLoader) => eval(form);

    public static object macroexpand1(object form) =>
        RuntimeHooks.Require(RuntimeHooks.Macroexpand1, "macroexpand-1")(form, null);

    public static object macroexpand(object form)
    {
        while (true)
        {
            object exf = macroexpand1(form);
            if (exf == form) return form;
            form = exf;
        }
    }

    public static object load(TextReader rdr) => Loader.LoadSource(rdr, null, null);

    public static object load(TextReader rdr, string relativePath) => Loader.LoadSource(rdr, relativePath, relativePath);

    public static object load(TextReader rdr, string sourcePath, string sourceName) => Loader.LoadSource(rdr, sourcePath, sourceName);

    /// <summary>ClojureCLR 的四参版本（多一个 relativePath，cleat 不用）。</summary>
    public static object load(TextReader rdr, string sourcePath, string sourceName, string relativePath) =>
        Loader.LoadSource(rdr, sourcePath, sourceName);

    public static object loadFile(string file) => Loader.LoadFile(file);

    // —— 动态 Var ——

    /// <summary><c>*source-path*</c>（文件名）。</summary>
    public static readonly Var SOURCE = RT.SourceVar;

    /// <summary><c>*file*</c>（完整路径）。</summary>
    public static readonly Var SOURCE_PATH = RT.SourcePathVar;

    public static readonly Var SourceVar = RT.SourceVar;
    public static readonly Var SourcePathVar = RT.SourcePathVar;
    public static readonly Var COMPILE_FILES = RT.CompileFilesVar;
    public static readonly Var COMPILE_PATH = RT.CompilePathVar;
    public static readonly Var COMPILER_OPTIONS = RT.CompilerOptionsVar;
}
