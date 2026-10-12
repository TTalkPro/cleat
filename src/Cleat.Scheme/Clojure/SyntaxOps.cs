using System;
using clojure.lang;

namespace Cleat.Scheme.Clojure;

/// <summary>
/// 卫生宏（cleat-057，clojure.syntax）的运行时入口。模式匹配、模板实例化与语法对象的操作都是 Scheme 过程
/// （(clojure syntax) 装入时放进下面的槽——它们要用 psyntax 的 syntax-dispatch、datum->syntax、free-identifier=?）；
/// 编译后的 Clojure 代码经 <see cref="ClojureOps.SyntaxMatch"/> 等（<c>$clj-call</c>）、clojure.syntax 的函数经这里的静态方法调用，
/// 实参与返回值都不经 Scheme 互操作的转换（designs/13 §7 的值边界）。语法对象对 Clojure 是不透明的值。
/// </summary>
public static class SyntaxOps
{
    /// <summary>(match 命名空间名 字面量名列表 模式 语法对象) → 匹配到的值的 object[]，或 #f。</summary>
    public static object Match;
    /// <summary>(template 命名空间名 模板 变量 值向量) → 语法结构。</summary>
    public static object Template;
    public static object ToDatum, ToSyntax, IsIdentifier, FreeIdEq, BoundIdEq, Temporaries;

    internal static Procedure Need(object p, string name) =>
        p as Procedure ?? throw new InvalidOperationException("clojure.syntax: the (clojure syntax) library is not loaded (" + name + ")");

    /// <summary>语法对象 → Clojure 数据（去掉 wrap；不是语法对象的值原样）。</summary>
    public static object SyntaxToDatum(object x) => Need(ToDatum, "syntax->datum").Invoke1(x);

    /// <summary>Clojure 数据 → 语法对象，取 ctx（标识符，或命名空间：其顶层上下文）的上下文。</summary>
    public static object DatumToSyntax(object ctx, object x) => Need(ToSyntax, "datum->syntax").Invoke2(ctx, x);

    public static object Identifier(object x) => Need(IsIdentifier, "identifier?").Invoke1(x);

    public static object FreeIdentifierEquals(object a, object b) => Need(FreeIdEq, "free-identifier=?").Invoke2(a, b);

    public static object BoundIdentifierEquals(object a, object b) => Need(BoundIdEq, "bound-identifier=?").Invoke2(a, b);

    /// <summary>与 coll 等长的新标识符列表（Clojure 列表）。</summary>
    public static object GenerateTemporaries(object coll) => Need(Temporaries, "generate-temporaries").Invoke1(coll);
}
