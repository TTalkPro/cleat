using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using clojure.lang;

namespace Cleat.Core.Runtime;

/// <summary>
/// JVM 异常类名的隐式导入（cleat-032，designs/03 §4）。可移植的 Clojure 代码写 <c>(catch IllegalArgumentException e …)</c>、
/// <c>(throw (IllegalStateException. "…"))</c>：JVM 上这些名字来自 java.lang 的隐式导入。cleat 把它们映射到 Cleat.Core 在
/// 同样情形下真正抛出的 CLR 异常类型，于是捕获与构造都按 CLR 的类型进行。短名进每个命名空间的默认导入（RT.DefaultImports），
/// 全名（java.lang.X、java.util.X …）经 RT.classForName 解析。只覆盖异常类型：其余 Java API（String 的方法、Long/MAX_VALUE …）
/// 不模拟，语料里这些行归入 cleat-divergences.edn 的 :host/jvm-api。
/// </summary>
public static class JvmNames
{
    private static readonly (string Full, Type Type)[] s_aliases =
    {
        ("java.lang.Throwable", typeof(Exception)),
        ("java.lang.Exception", typeof(Exception)),
        ("java.lang.RuntimeException", typeof(Exception)),
        ("java.lang.IllegalArgumentException", typeof(ArgumentException)),
        ("java.lang.IllegalStateException", typeof(InvalidOperationException)),
        ("java.lang.UnsupportedOperationException", typeof(InvalidOperationException)),
        ("java.lang.ClassCastException", typeof(InvalidCastException)),
        ("java.lang.NullPointerException", typeof(NullReferenceException)),
        ("java.lang.ArithmeticException", typeof(ArithmeticException)),
        ("java.lang.NumberFormatException", typeof(FormatException)),
        ("java.lang.IndexOutOfBoundsException", typeof(ArgumentOutOfRangeException)),
        ("java.lang.StringIndexOutOfBoundsException", typeof(ArgumentOutOfRangeException)),
        ("java.lang.ArrayIndexOutOfBoundsException", typeof(IndexOutOfRangeException)),
        ("java.lang.InterruptedException", typeof(System.Threading.ThreadInterruptedException)),
        ("java.util.concurrent.TimeoutException", typeof(TimeoutException)),
        ("java.util.regex.PatternSyntaxException", typeof(RegexParseException)),
        ("java.io.IOException", typeof(IOException)),
        ("java.io.FileNotFoundException", typeof(FileNotFoundException)),
        ("java.io.EOFException", typeof(EndOfStreamException)),
        ("java.util.IllegalFormatException", typeof(IllegalFormatException)),
        ("java.util.UnknownFormatFlagsException", typeof(UnknownFormatFlagsException)),
        ("java.util.IllegalFormatFlagsException", typeof(IllegalFormatFlagsException)),
        ("java.util.IllegalFormatConversionException", typeof(IllegalFormatConversionException)),
        ("java.util.FormatFlagsConversionMismatchException", typeof(FormatFlagsConversionMismatchException)),
        ("java.util.DuplicateFormatFlagsException", typeof(DuplicateFormatFlagsException)),
        ("java.util.UnknownFormatConversionException", typeof(UnknownFormatConversionException)),
        ("java.util.IllegalFormatWidthException", typeof(IllegalFormatWidthException)),
        ("java.util.MissingFormatWidthException", typeof(MissingFormatWidthException)),
        ("java.util.IllegalFormatPrecisionException", typeof(IllegalFormatPrecisionException)),
        ("java.util.MissingFormatArgumentException", typeof(MissingFormatArgumentException)),
    };

    private static readonly Dictionary<string, Type> s_full = BuildFull();

    private static Dictionary<string, Type> BuildFull()
    {
        var d = new Dictionary<string, Type>(StringComparer.Ordinal);
        foreach (var (full, t) in s_aliases) d[full] = t;
        return d;
    }

    /// <summary>全名（java.lang.IllegalArgumentException …）→ CLR 类型；不是别名返回 null。</summary>
    public static Type FindFull(string name) => name != null && s_full.TryGetValue(name, out var t) ? t : null;

    /// <summary>短名的隐式导入（java.lang 与 java.util / java.io 的异常类）：不覆盖 System 命名空间里已有的同名类型。</summary>
    public static void AddImports(IDictionary<Symbol, Type> d)
    {
        foreach (var (full, t) in s_aliases)
        {
            var sym = Symbol.intern(full.Substring(full.LastIndexOf('.') + 1));
            d.TryAdd(sym, t);
        }
    }
}
