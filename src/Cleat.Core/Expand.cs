using System;
using System.Collections.Generic;
using clojure.lang;

namespace Cleat.Core.Expand;

// 分析器与 clojure.core 用的几个类型（cleat-093 起 C# 展开器与 Lcore 已删，命名空间名保留）。

/// <summary>特殊形式的符号（Clojure 的 Compiler.specials）：clojure.core/special-symbol? 查它。</summary>
public static class SpecialForms
{
    public static readonly IPersistentSet Specials = PersistentHashSet.create(
      Symbol.intern("def"), Symbol.intern("loop*"), Symbol.intern("recur"), Symbol.intern("if"), Symbol.intern("case*"), Symbol.intern("let*"),
      Symbol.intern("letfn*"), Symbol.intern("do"), Symbol.intern("fn*"), Symbol.intern("quote"), Symbol.intern("var"), Symbol.intern("import*"),
      Symbol.intern("."), Symbol.intern("set!"), Symbol.intern("deftype*"), Symbol.intern("reify*"), Symbol.intern("try"), Symbol.intern("throw"),
      Symbol.intern("monitor-enter"), Symbol.intern("monitor-exit"), Symbol.intern("catch"), Symbol.intern("finally"), Symbol.intern("new"), Symbol.intern("&"));

    public static bool IsSpecial(object sym) => sym is Symbol && Specials.contains(sym);
}

/// <summary>分析器看到的词法绑定；也作为宏的 &amp;env 的值。</summary>
public sealed class LocalBinding
{
    public readonly Symbol Name;
    /// <summary>唯一化后的名字（name__N），Lcore 中 (local …) 用它。</summary>
    public readonly Symbol Unique;
    public readonly Symbol Tag;
    public readonly bool IsArg, IsField, IsThis;
    /// <summary>deftype 的可变字段才能 set!。</summary>
    public readonly bool IsMutable;
    /// <summary>所属的 fn 深度（分析器不用，只给宏用）。</summary>
    public readonly int FnDepth;

    public LocalBinding(Symbol name, Symbol unique, Symbol tag, bool isArg, bool isField, bool isThis, bool isMutable, int fnDepth)
    {
        Name = name; Unique = unique; Tag = tag; IsArg = isArg; IsField = isField; IsThis = isThis; IsMutable = isMutable; FnDepth = fnDepth;
    }

    public override string ToString() => Unique.ToString();
}

/// <summary>分析错误（语法、未解析的符号、宏抛出的异常），带源位置；是 <see cref="clojure.lang.Compiler.CompilerException"/>（cleat-189）。</summary>
public sealed class ExpanderException : clojure.lang.Compiler.CompilerException
{
    public static readonly Keyword PhaseSyntax = PHASE_MACRO_SYNTAX_CHECK, PhaseMacroexpand = PHASE_MACROEXPANSION,
      PhaseCompile = PHASE_COMPILE_SYNTAX_CHECK;

    public ExpanderException(string sourcePath, int line, int column, Keyword phase, string message, Exception cause = null)
        : this(sourcePath, line, column, phase, message, cause, null)
    {
    }

    public ExpanderException(string sourcePath, int line, int column, Keyword phase, string message, Exception cause, string symbol)
        : base(sourcePath, line, column, phase, message, cause, symbol)
    {
    }
}
