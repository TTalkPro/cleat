using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Cleat.Scheme;

/// <summary>
/// 编译器生成的代码调用的辅助方法（designs/03-compiler.md §4 lower-calls，iron-025）。
/// 都很短，JIT 可以内联。
/// </summary>
public static class Calls
{
    /// <summary>被调用的值 → Procedure；不是过程时报 assertion-violation。</summary>
    public static Procedure Proc(object f) =>
      f as Procedure ?? (Procedure)Errors.AssertionViolation("apply", "not a procedure", f) ?? throw new InvalidOperationException("not a procedure");

    /// <summary>读全局位置（符号值槽）；未绑定时报 undefined-error。</summary>
    public static object Global(object symbol)
    {
        var s = (Symbol)symbol;
        object v = s.Value;
        return v is Unbound ? Errors.UndefinedError(s) : v;
    }

    /// <summary>写全局位置。</summary>
    public static void SetGlobal(object symbol, object value) => ((Symbol)symbol).Value = value;

    /// <summary>(apply f a … lst) 的实参 [a … lst] → f 的实参数组；尾位置的 apply 用它展开后 tail. callvirt InvokeN。</summary>
    public static object[] ApplyArgs(object[] args)
    {
        if (args.Length == 0) return args;
        var r = new List<object>(args.Length + 8);
        for (int i = 0; i < args.Length - 1; i++) r.Add(args[i]);
        object l = args[^1];
        while (l is Cons c) { r.Add(c.car); l = c.cdr; }
        if (l != null) Errors.AssertionViolation("apply", "not a proper list", args[^1]);
        return r.ToArray();
    }

    /// <summary>新系统不支持的形式（define-clr-class-internal，06 §1），执行时报错。</summary>
    public static object Unsupported(string what) =>
      Errors.AssertionViolation(what, "not supported by the compiler yet");

    public static object Break()
    {
        if (Debugger.IsAttached) Debugger.Break();
        return Unspecified.Instance;
    }

    /// <summary>读 letrec 变量时发现尚未初始化（R6RS letrec 限制）。</summary>
    public static object Uninitialized(string name) =>
      Errors.AssertionViolation(name, "attempt to read uninitialized variable");

    /// <summary>($uninitialized)：letrec 中前向变量的初值（编译器 simplify-letrec 使用）。</summary>
    [Primitive("$uninitialized")]
    public static object UninitializedValue() => Cleat.Scheme.Uninitialized.Instance;

    /// <summary>($check-initialized v 'name)：v 是未初始化标记时报 &amp;assertion（R6RS letrec 限制），否则返回 v。</summary>
    [Primitive("$check-initialized")]
    public static object CheckInitialized(object value, object name) =>
      value is Uninitialized
        ? Errors.AssertionViolation(name is Symbol s ? s.Ungensym() : name, "attempt to read uninitialized variable")
        : value;
}
