using System;
using System.IO;
using clojure.lang;

namespace Cleat.Core.Runtime;

/// <summary>
/// 运行时钩子（designs/02 §6）：Cleat.Core 里需要"展开 + 编译 + 求值"或调用 Clojure 函数的地方不直接引用编译器，
/// 而是经这些槽回调。槽由 boot 镜像的入口（新系统）或 stage0（无镜像时）登记；Clojure 侧经 <c>$set-hook!</c> 原语
/// 调 <see cref="Set"/>。未登记时 <see cref="Require{T}"/> 给出明确错误，而不是 NullReferenceException。
/// </summary>
public static class RuntimeHooks
{
    /// <summary>执行一段源码：(reader, sourcePath, sourceName) → 逐个读 + 展开 + 编译 + 运行，返回最后一个形式的值。</summary>
    public static Func<TextReader, string, string, object> LoadSource { get; set; }

    /// <summary>同 <see cref="LoadSource"/>，但 <c>*compile-files*</c> 为真时还把结果编成包程序集（编译驱动，cleat-028、cleat-067）。stage0 登记与 LoadSource 相同的实现。</summary>
    public static Func<TextReader, string, string, object> Compile { get; set; }

    /// <summary>求值一个形式。</summary>
    public static Func<object, object> Eval { get; set; }

    /// <summary>(form, env) → 展开一层宏；可选。</summary>
    public static Func<object, object, object> Macroexpand1 { get; set; }

    /// <summary>未知对象的打印后备（print-method）；null 表示用 RT.print 的缺省。</summary>
    public static Action<object, TextWriter> PrintMethod { get; set; }

    /// <summary>错误报告（*err* 输出）；可选。</summary>
    public static Action<Exception> ReportError { get; set; }

    /// <summary>
    /// 从源码加载（展开、编译递归深，要大栈）在哪条线程上执行（cleat-067，designs/17 §4.3）：CleatRuntime 登记为"排到它的 512 MB 栈
    /// 工作线程"，惰性启动之后在调用方线程上触发的源码加载（gen-class 初始化器 require 实现命名空间）因此不会栈溢出。null 时就地执行。
    /// </summary>
    public static Func<Func<object>, object> OnLoaderThread { get; set; }

    /// <summary>RT.Init 末尾调用一次（登记原语的占位，cleat-011）；可选。</summary>
    public static Action OnInit { get; set; }

    /// <summary>按名字登记一个 Clojure IFn（或直接给对应委托）；fn 为 null 时清除该槽。</summary>
    public static void Set(string name, object fn)
    {
        if (name is null)
        {
            throw new ArgumentNullException(nameof(name));
        }

        switch (name)
        {
            case "load-source":
                LoadSource = As<Func<TextReader, string, string, object>>(fn, f => (r, p, n) => f.invoke(r, p, n));
                break;
            case "compile":
                Compile = As<Func<TextReader, string, string, object>>(fn, f => (r, p, n) => f.invoke(r, p, n));
                break;
            case "eval":
                Eval = As<Func<object, object>>(fn, f => x => f.invoke(x));
                break;
            case "macroexpand-1":
                Macroexpand1 = As<Func<object, object, object>>(fn, f => (x, env) => f.invoke(x, env));
                break;
            case "print-method":
                PrintMethod = As<Action<object, TextWriter>>(fn, f => (x, w) => f.invoke(x, w));
                break;
            case "report-error":
                ReportError = As<Action<Exception>>(fn, f => e => f.invoke(e));
                break;
            case "on-init":
                OnInit = As<Action>(fn, f => () => f.invoke());
                break;
            default:
                throw new ArgumentException($"unknown runtime hook '{name}' (known: load-source, compile, eval, macroexpand-1, print-method, report-error, on-init)", nameof(name));
        }
    }

    /// <summary>取钩子；未登记时抛出说明"没有 boot 镜像"的 InvalidOperationException。</summary>
    public static T Require<T>(T hook, string name) where T : class
        => hook ?? throw new InvalidOperationException($"runtime hook '{name}' is not registered (no boot image loaded?)");

    /// <summary>清空全部槽（测试用）。</summary>
    public static void Reset()
    {
        LoadSource = null;
        Compile = null;
        Eval = null;
        Macroexpand1 = null;
        PrintMethod = null;
        ReportError = null;
        OnInit = null;
    }

    private static T As<T>(object fn, Func<IFn, T> wrap) where T : class
    {
        return fn switch
        {
            null => null,
            T d => d,
            IFn f => wrap(f),
            _ => throw new ArgumentException($"runtime hook must be a clojure.lang.IFn or a {typeof(T).Name}, got {fn.GetType().FullName}", nameof(fn)),
        };
    }
}
