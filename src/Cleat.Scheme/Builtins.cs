using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Cleat.Scheme;

/// <summary>
/// 从旧运行时移植的原语所在的部分类，以及它们共用的辅助方法。
/// 保留类名 Builtins：Scheme 源码中有 (clr-static-call Builtins …) 的直接调用。
/// 非静态类：旧代码中 Records 等类继承它以共享辅助方法。
/// </summary>
public partial class Builtins
{
    public static readonly object TRUE = RT.True;
    public static readonly object FALSE = RT.False;
    public static readonly object Unspecified = Cleat.Scheme.Unspecified.Instance;

    public static object GetBool(bool b) => RT.Bool(b);

    public static bool IsTrue(object o) => RT.IsTrue(o);

    /// <summary>多值（单值不包装）。</summary>
    public static object Values(params object[] args) => Control.Values(args);

    public static object AssertionViolation(object who, string message, params object[] irritants) =>
      Errors.AssertionViolation(who, message, irritants);

    #region 旧 Glue 方法的同名转发（移植代码沿用这些名字；经运行时钩子调用 Scheme 过程）

    public static object UndefinedError(object sym) => Errors.UndefinedError((Symbol)sym);
    public static object LexicalError(string message, object what) => Errors.LexicalError(message, what);
    public static object SyntaxError(object who, object message, object form, object subform) => Errors.SyntaxError(who, message?.ToString(), form, subform);
    public static object IOPortViolation(object who, object message, object port) => Errors.IOPortViolation(who, message?.ToString(), port);
    public static object FileNotFoundViolation(object who, object message, object filename) => Errors.FileNotFoundViolation(who, message?.ToString(), filename);
    public static object FileInUseViolation(object who, object filename) => Errors.FileInUseViolation(who, filename);
    public static object FileAlreadyExistsViolation(object who, object filename) => Errors.FileAlreadyExistsViolation(who, filename);
    public static object IODecodingError() => Errors.IODecodingError();
    public static object IOEncodingError() => Errors.IOEncodingError();
    public static object Raise(object obj) => Errors.Raise(obj);
    public static object RaiseContinueable(object obj) => SchemeCalls.RaiseContinuable(obj);
    public static object WithExceptionHandler(object handler, object thunk) => SchemeCalls.WithExceptionHandler(handler, thunk);
    public static object Exact(object obj) => SchemeCalls.Exact(obj);
    public static object Inexact(object obj) => SchemeCalls.Inexact(obj);

    #endregion

    /// <summary>出错时沿调用栈找到最近的 [Primitive] 方法，以其名字作为 who（只在出错路径上使用）。</summary>
    protected internal static object GetCaller()
    {
        var st = new StackTrace(1);
        for (int i = 0; i < st.FrameCount; i++)
        {
            MethodBase m = st.GetFrame(i)?.GetMethod();
            if (m == null) continue;
            // 一个方法可能带多个 [Primitive]（别名），取第一个
            var a = m.GetCustomAttributes<PrimitiveAttribute>(false).FirstOrDefault();
            if (a != null) return Symbol.Intern(a.Name);
        }
        return RT.False;
    }

    private static string GetTypeName(Type t) => t.Name;

    /// <summary>obj 为 null 或 T；否则报错。</summary>
    protected internal static T Requires<T>(object obj)
    {
        if (obj == null) return default;
        if (obj is T t) return t;
        return (T)AssertionViolation(GetCaller(), "expected type: " + GetTypeName(typeof(T)), obj);
    }

    protected internal static object RequiresNotNull(object obj) =>
      obj ?? AssertionViolation(GetCaller(), "argument cannot be null");

    /// <summary>obj 必须是 T（可变字符串可当作 string）；否则报错。</summary>
    protected internal static T RequiresNotNull<T>(object obj)
    {
        if (obj is T t) return t;
        if (obj == null) return (T)AssertionViolation(GetCaller(), "argument cannot be null");
        if (typeof(T) == typeof(string) && (obj is SchemeString || obj is StringBuilder)) return (T)(object)obj.ToString();
        // 输入输出双向文本端口：按需要取出其输出端或输入端
        if (typeof(T) == typeof(System.IO.TextWriter) && obj is CustomTextReaderWriter rw) return (T)(object)rw.output;
        if (typeof(T) == typeof(System.IO.TextReader) && obj is CustomTextReaderWriter rw2) return (T)(object)rw2.input;
        return (T)AssertionViolation(GetCaller(), "expected type: " + GetTypeName(typeof(T)), obj);
    }

    protected internal static void RequiresCondition(bool condition, string message)
    {
        if (!condition) AssertionViolation(GetCaller(), message);
    }
}
