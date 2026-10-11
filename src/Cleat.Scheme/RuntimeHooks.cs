using System;
using System.Collections.Generic;
using System.Linq;

namespace Cleat.Scheme;

/// <summary>
/// 运行时钩子：C# 代码需要调用的、在 Scheme 中定义的过程。
/// 取代旧系统 IronScheme.Glue.il 按 boot dll 方法名（'#'::'ironscheme.exceptions::raise' 等）硬编码调用的做法。
/// boot 镜像初始化时由 Scheme 调用 <c>($set-runtime-hook! 'raise raise)</c> 登记。
/// 设计：designs/02-runtime.md §6。
/// </summary>
public static class RuntimeHooks
{
    // 与旧 Glue.il 的 15 个方法一一对应，另加 clr-exception-violation（cleat-058）；名字取 Scheme 中的过程名。
    public static Procedure AssertionViolation;          // (assertion-violation who msg irritant ...)
    public static Procedure UndefinedError;              // (undefined-error sym)
    public static Procedure LexicalError;                // (lexical-error msg what)
    public static Procedure SyntaxError;                 // (syntax-error who msg form subform)
    public static Procedure IOPortViolation;             // (i/o-port-violation who msg port)
    public static Procedure FileNotFoundViolation;       // (file-not-found-violation who msg filename)
    public static Procedure FileInUseViolation;          // (file-in-use-violation who filename)
    public static Procedure FileAlreadyExistsViolation;  // (file-already-exists-violation who filename)
    public static Procedure IODecodingError;             // (io-decoding-error)
    public static Procedure IOEncodingError;             // (io-encoding-error)
    public static Procedure Raise;                       // (raise obj)
    public static Procedure RaiseContinuable;            // (raise-continuable obj)
    public static Procedure WithExceptionHandler;        // (with-exception-handler handler thunk)
    public static Procedure Exact;                       // (exact z)
    public static Procedure Inexact;                     // (inexact z)
    public static Procedure ClrExceptionViolation;       // (clr-exception-violation exception)：宿主异常包成条件 raise（cleat-058）

    private static readonly (string Name, Func<Procedure> Get, Action<Procedure> Set)[] s_table =
    {
  ("assertion-violation", () => AssertionViolation, p => AssertionViolation = p),
  ("undefined-error", () => UndefinedError, p => UndefinedError = p),
  ("lexical-error", () => LexicalError, p => LexicalError = p),
  ("syntax-error", () => SyntaxError, p => SyntaxError = p),
  ("i/o-port-violation", () => IOPortViolation, p => IOPortViolation = p),
  ("file-not-found-violation", () => FileNotFoundViolation, p => FileNotFoundViolation = p),
  ("file-in-use-violation", () => FileInUseViolation, p => FileInUseViolation = p),
  ("file-already-exists-violation", () => FileAlreadyExistsViolation, p => FileAlreadyExistsViolation = p),
  ("io-decoding-error", () => IODecodingError, p => IODecodingError = p),
  ("io-encoding-error", () => IOEncodingError, p => IOEncodingError = p),
  ("raise", () => Raise, p => Raise = p),
  ("raise-continuable", () => RaiseContinuable, p => RaiseContinuable = p),
  ("with-exception-handler", () => WithExceptionHandler, p => WithExceptionHandler = p),
  ("exact", () => Exact, p => Exact = p),
  ("inexact", () => Inexact, p => Inexact = p),
  ("clr-exception-violation", () => ClrExceptionViolation, p => ClrExceptionViolation = p),
};

    public static IEnumerable<string> Names => s_table.Select(t => t.Name);

    public static void Set(string name, Procedure proc)
    {
        foreach (var t in s_table)
            if (t.Name == name) { t.Set(proc); return; }
        throw new ArgumentException("unknown runtime hook: " + name, nameof(name));
    }

    public static Procedure Get(string name)
    {
        foreach (var t in s_table)
            if (t.Name == name) return t.Get();
        throw new ArgumentException("unknown runtime hook: " + name, nameof(name));
    }

    /// <summary>清除全部钩子（单元测试用）。</summary>
    public static void Clear()
    {
        foreach (var t in s_table) t.Set(null);
    }

    /// <summary>($set-runtime-hook! 'name proc)</summary>
    [Primitive("$set-runtime-hook!")]
    public static object SetRuntimeHook(object name, object proc)
    {
        var s = name as Symbol;
        var p = proc as Procedure;
        if (s == null || p == null)
            return Errors.AssertionViolation("$set-runtime-hook!", "expected a symbol and a procedure", name, proc);
        if (!Names.Contains(s.Name))
            return Errors.AssertionViolation("$set-runtime-hook!", "unknown runtime hook", name);
        Set(s.Name, p);
        return Unspecified.Instance;
    }

    /// <summary>($runtime-hooks-missing) => 尚未登记的钩子名列表；boot 初始化结束后应为 '()。</summary>
    [Primitive("$runtime-hooks-missing")]
    public static object Missing() =>
      Cons.FromList(s_table.Where(t => t.Get() == null).Select(t => (object)Symbol.Intern(t.Name)).ToList());

    /// <summary>调用一个必须已登记的非报错类钩子（exact、inexact 等）。</summary>
    internal static Procedure Required(Procedure hook, string name) =>
      hook ?? throw new InvalidOperationException($"runtime hook '{name}' is not registered (boot image not loaded?)");
}
