using System;
using System.Linq;

namespace Cleat.Scheme;

/// <summary>
/// C# 代码报告 Scheme 错误的统一入口。
/// 已登记运行时钩子时，调用 Scheme 定义的条件过程（它们构造条件并 raise，通常不返回）；
/// 未登记时（启动早期、单元测试）抛 <see cref="SchemeRuntimeError"/>。
/// 返回 object 便于写成 <c>return Errors.X(...)</c>。
/// </summary>
public static class Errors
{
    private static object Fallback(string kind, string who, string message, params object[] irritants) =>
      throw new SchemeRuntimeError(kind, who, message, irritants);

    /// <summary>who 可以是符号、字符串或 null（传给 Scheme 时为 #f）。</summary>
    /// <summary>诊断：设置环境变量 IRONSCHEME_TRACE_ERRORS 时，C# 侧报出的断言违例同时把 CLR 调用栈写到标准错误。</summary>
    private static readonly bool s_traceErrors = Environment.GetEnvironmentVariable("IRONSCHEME_TRACE_ERRORS") != null;

    public static object AssertionViolation(object who, string message, params object[] irritants)
    {
        if (s_traceErrors) Console.Error.WriteLine($"[assertion-violation {who}: {message}]\n{Environment.StackTrace}");
        var h = RuntimeHooks.AssertionViolation;
        if (h == null) return Fallback("assertion-violation", who?.ToString(), message, irritants);
        var args = new object[irritants.Length + 2];
        args[0] = who ?? RT.False;
        args[1] = message;
        Array.Copy(irritants, 0, args, 2, irritants.Length);
        return h.InvokeN(args);
    }

    public static object UndefinedError(Symbol symbol)
    {
        var h = RuntimeHooks.UndefinedError;
        return h == null
          ? Fallback("undefined-error", symbol.Ungensym().Name, "attempt to reference unbound identifier", symbol)
          : h.Invoke1(symbol);
    }

    public static object LexicalError(string message, object what)
    {
        var h = RuntimeHooks.LexicalError;
        return h == null ? Fallback("lexical-error", null, message, what) : h.Invoke2(message, what);
    }

    public static object SyntaxError(object who, string message, object form, object subform)
    {
        var h = RuntimeHooks.SyntaxError;
        return h == null ? Fallback("syntax-error", who?.ToString(), message, form, subform) : h.Invoke4(who, message, form, subform);
    }

    public static object IOPortViolation(object who, string message, object port)
    {
        var h = RuntimeHooks.IOPortViolation;
        return h == null ? Fallback("i/o-port-violation", who?.ToString(), message, port) : h.Invoke3(who, message, port);
    }

    public static object FileNotFoundViolation(object who, string message, object filename)
    {
        var h = RuntimeHooks.FileNotFoundViolation;
        return h == null ? Fallback("file-not-found-violation", who?.ToString(), message, filename) : h.Invoke3(who, message, filename);
    }

    public static object FileInUseViolation(object who, object filename)
    {
        var h = RuntimeHooks.FileInUseViolation;
        return h == null ? Fallback("file-in-use-violation", who?.ToString(), "file in use", filename) : h.Invoke2(who, filename);
    }

    public static object FileAlreadyExistsViolation(object who, object filename)
    {
        var h = RuntimeHooks.FileAlreadyExistsViolation;
        return h == null ? Fallback("file-already-exists-violation", who?.ToString(), "file already exists", filename) : h.Invoke2(who, filename);
    }

    public static object IODecodingError()
    {
        var h = RuntimeHooks.IODecodingError;
        return h == null ? Fallback("i/o-decoding-error", null, "decoding error") : h.Invoke0();
    }

    public static object IOEncodingError()
    {
        var h = RuntimeHooks.IOEncodingError;
        return h == null ? Fallback("i/o-encoding-error", null, "encoding error") : h.Invoke0();
    }

    /// <summary>(raise obj)：未登记时把对象包成 <see cref="SchemeException"/> 抛出（与 Scheme 默认处理器一致）。</summary>
    public static object Raise(object obj)
    {
        var h = RuntimeHooks.Raise;
        if (h == null) throw new SchemeException(obj);
        return h.Invoke1(obj);
    }

    public static T RequiresType<T>(object o, string who)
    {
        if (o is T t) return t;
        AssertionViolation(who, "not a " + typeof(T).Name, o);
        return default;
    }
}

/// <summary>Scheme 侧过程的普通（非报错）调用。</summary>
public static class SchemeCalls
{
    public static object Exact(object z) => RuntimeHooks.Required(RuntimeHooks.Exact, "exact").Invoke1(z);
    public static object Inexact(object z) => RuntimeHooks.Required(RuntimeHooks.Inexact, "inexact").Invoke1(z);
    public static object RaiseContinuable(object obj) =>
      RuntimeHooks.Required(RuntimeHooks.RaiseContinuable, "raise-continuable").Invoke1(obj);
    public static object WithExceptionHandler(object handler, object thunk) =>
      RuntimeHooks.Required(RuntimeHooks.WithExceptionHandler, "with-exception-handler").Invoke2(handler, thunk);
}

/// <summary>钩子登记之前（或单元测试中）使用的 .NET 异常。</summary>
public sealed class SchemeRuntimeError : Exception
{
    public string Kind { get; }
    public string Who { get; }
    public object[] Irritants { get; }

    public SchemeRuntimeError(string kind, string who, string message, object[] irritants)
      : base($"{kind}: {who}: {message}")
    {
        Kind = kind; Who = who; Irritants = irritants;
    }
}
