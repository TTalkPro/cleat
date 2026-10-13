using System;
using System.Collections.Generic;
using clojure.lang;

namespace Cleat.Core.Expand;

/// <summary>
/// Lcore（designs/03-language-core.md §3）：展开器的输出，纯数据（列表、向量、符号、关键字、字符串、Var 对象、Type 对象）。
/// 节点都是以符号开头的列表；这里集中定义节点名与构造函数，编译器（Clojure）与 stage0（C#）按同样的形状读取。
///
///   (const d)                                   字面量 / quote；d 为任意数据
///   (local x)                                   词法变量，x 为唯一化后的符号（name__N），元数据 :tag
///   (var-ref ns/name {:dynamic b :redef b})     读 Var
///   (the-var ns/name)                           #'ns/name
///   (if t a b)   (do e …)
///   (let [x e …] body)   (loop [x e …] body)   (recur e …)
///   (fn name [once?] ((x …) rest body) …)       每个子句：固定参数列表、剩余参数（nil 表示无）、体
///   (letfn [x (fn …) …] body)
///   (def ns/name {:dynamic b :init b} meta init)   meta / init 为表达式或 nil
///   (set! target e)                             target: (local x) | (var-ref …) | (host-field …)
///   (throw e)
///   (try body ((catch "T" x handler) …) finally)   finally 为表达式或 nil
///   (monitor-enter e)  (monitor-exit e)
///   (case e shift mask low high default ((h test then) …) switch-type test-type skip-check)
///   (host-call "T"|nil "member" static? target (args …))      target 为 nil 时是静态调用
///   (host-field "T"|nil "name" static? target)
///   (host-set-field "T"|nil "name" static? target value)
///   (host-new "T" (args …))
///   (host-type "T")                             类型字面量
///   (instance-of "T" e)
///   (deftype tagname "T" (fields …) ("I" …) (methods …) opts)   field: (field name "T" mutable? volatile?)
///                                               method: (method "I"|nil "name" ((x "T") …) "R" body)
///   (import "T")
///   (call f (args …))
///   (vector e …)  (map k v …)  (set e …)
///   (keyword-invoke :k e)
///   (tag "T" e)                                 带类型提示的表达式
/// </summary>
public static class Lcore
{
    public static readonly Symbol Const = Symbol.intern("const"), Local = Symbol.intern("local"), VarRef = Symbol.intern("var-ref"),
      TheVar = Symbol.intern("the-var"), If = Symbol.intern("if"), Do = Symbol.intern("do"), Let = Symbol.intern("let"), Loop = Symbol.intern("loop"),
      Recur = Symbol.intern("recur"), Fn = Symbol.intern("fn"), Letfn = Symbol.intern("letfn"), Def = Symbol.intern("def"), SetBang = Symbol.intern("set!"),
      Throw = Symbol.intern("throw"), Try = Symbol.intern("try"), Catch = Symbol.intern("catch"), MonitorEnter = Symbol.intern("monitor-enter"),
      MonitorExit = Symbol.intern("monitor-exit"), Case = Symbol.intern("case"), HostCall = Symbol.intern("host-call"), HostField = Symbol.intern("host-field"),
      HostSetField = Symbol.intern("host-set-field"), HostNew = Symbol.intern("host-new"), HostType = Symbol.intern("host-type"),
      InstanceOf = Symbol.intern("instance-of"), Deftype = Symbol.intern("deftype"), Field = Symbol.intern("field"), Method = Symbol.intern("method"),
      Import = Symbol.intern("import"), Call = Symbol.intern("call"), Vector = Symbol.intern("vector"), Map = Symbol.intern("map"), Set = Symbol.intern("set"),
      KeywordInvoke = Symbol.intern("keyword-invoke"), Tag = Symbol.intern("tag");

    public static readonly Keyword DynamicKey = Keyword.intern("dynamic"), RedefKey = Keyword.intern("redef"), InitKey = Keyword.intern("init"),
      OnceKey = Keyword.intern("once"), MacroKey = Keyword.intern("macro");

    public static ISeq List(params object[] items) => PersistentList.create(items).seq() ?? PersistentList.EMPTY;

    /// <summary>把源位置（line / column / file）附到节点上。</summary>
    public static object WithPos(object node, IPersistentMap pos) => pos == null || !(node is IObj o) ? node : o.withMeta(pos);
}

/// <summary>展开器看到的词法绑定；也作为宏的 &amp;env 的值。</summary>
public sealed class LocalBinding
{
    public readonly Symbol Name;
    /// <summary>唯一化后的名字（name__N），Lcore 中 (local …) 用它。</summary>
    public readonly Symbol Unique;
    public readonly Symbol Tag;
    public readonly bool IsArg, IsField, IsThis;
    /// <summary>deftype 的可变字段才能 set!。</summary>
    public readonly bool IsMutable;
    /// <summary>所属的 fn 深度，用于判断 recur / 捕获（编译器自己再算一遍自由变量；这里只给宏用）。</summary>
    public readonly int FnDepth;

    public LocalBinding(Symbol name, Symbol unique, Symbol tag, bool isArg, bool isField, bool isThis, bool isMutable, int fnDepth)
    {
        Name = name; Unique = unique; Tag = tag; IsArg = isArg; IsField = isField; IsThis = isThis; IsMutable = isMutable; FnDepth = fnDepth;
    }

    public override string ToString() => Unique.ToString();
}

/// <summary>展开错误（语法、未解析的符号、宏抛出的异常），带源位置；与 Clojure 的 CompilerException 对应。</summary>
public sealed class ExpanderException : Exception, IExceptionInfo
{
    public string SourcePath { get; }
    public int Line { get; }
    public int Column { get; }
    public Keyword Phase { get; }

    // the :clojure.error/phase values are plain keywords, as Compiler.PHASE_* on the JVM (clojure.main/ex-triage
    // dispatches on :compile-syntax-check …); cleat-069 found them namespaced, which made ex-str throw "No matching clause"
    public static readonly Keyword PhaseSyntax = Keyword.intern(null, "macro-syntax-check"),
      PhaseMacroexpand = Keyword.intern(null, "macroexpansion"), PhaseCompile = Keyword.intern(null, "compile-syntax-check");

    /// <summary>原因文本（不含位置前缀）：Clojure 的 ex-str 把它放在第二行（cleat-055）。</summary>
    public string Reason { get; }

    /// <summary>
    /// 出错形式的运算符（Clojure 的 CompilerException.symbol）：特殊形式名（"compiling recur at"）或宏的符号（"macroexpanding m at"）；
    /// 普通调用与符号为 null（"compiling at"）。
    /// </summary>
    public string Symbol { get; }

    public ExpanderException(string sourcePath, int line, int column, Keyword phase, string message, Exception cause = null)
        : this(sourcePath, line, column, phase, message, cause, null)
    {
    }

    public ExpanderException(string sourcePath, int line, int column, Keyword phase, string message, Exception cause, string symbol)
        : base(Format(sourcePath, line, column, message), cause)
    {
        SourcePath = sourcePath; Line = line; Column = column; Phase = phase; Reason = message; Symbol = symbol;
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
