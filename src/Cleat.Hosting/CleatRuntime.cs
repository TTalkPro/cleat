using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Cleat.Host;
using clojure.lang;

namespace Cleat.Core.Hosting;

/// <summary>初始化选项；全部可省。</summary>
public sealed class CleatOptions
{
    /// <summary>Scheme boot 镜像路径；缺省时按宿主的规则自动找（应用目录、各级 src/boot/）。</summary>
    public string SchemeBoot { get; set; }

    /// <summary>Clojure boot 镜像路径；缺省自动找，找不到时从 src/clojure 源码不可用（宿主同规则）。</summary>
    public string ClojureBoot { get; set; }

    /// <summary>额外的加载路径（require / load 按它找源码与 .clj.dll）。</summary>
    public IList<string> LoadPaths { get; } = new List<string>();

    /// <summary>*command-line-args* 的值。</summary>
    public IList<string> CommandLine { get; } = new List<string>();
}

/// <summary>
/// C# 程序嵌入 cleat 的入口（cleat-035）。类名沿用 IronScheme 的 SchemeRuntime 叫 CleatRuntime——
/// 名为 Cleat 的类无法在 Cleat.* 命名空间树之外引用（会被根命名空间 Cleat 遮住）：<see cref="Init"/> 加载 Scheme 与 Clojure 镜像并登记钩子（与宿主完全同一路径），
/// <see cref="Eval"/> 求值源码、<see cref="Require"/> 加载命名空间、<see cref="Var"/> 取命名空间里的函数调用。
/// 展开器与编译器不是线程安全的、需要大栈：所有调用经锁排到一条 512 MB 栈的工作线程上串行执行（Kestrel 的并发请求因此安全）。
/// Clojure 的异常原样传出（ex-info 是 ExceptionInfo，错误位置在 Data["cleat.error/location"]）。
/// </summary>
public static class CleatRuntime
{
    private static readonly object s_gate = new object();
    private static BlockingCollection<(Func<object>, TaskCompletionSource<object>)> s_queue;
    private static bool s_initialized;

    public static bool IsInitialized => s_initialized;

    /// <summary>初始化运行时（加载两个镜像，约 1 s）；重复调用无效果。之后的调用（包括并发的）都串行在工作线程上。</summary>
    public static void Init(CleatOptions options = null)
    {
        lock (s_gate)
        {
            if (s_initialized) return;
            s_queue = new BlockingCollection<(Func<object>, TaskCompletionSource<object>)>();
            var thread = new Thread(WorkerLoop, 512 * 1024 * 1024) { IsBackground = true, Name = "cleat" };
            thread.Start();
            MarshalLocal<object>(() => { InitBody(options ?? new CleatOptions()); return null; });
            s_initialized = true;
        }
    }

    /// <summary>求值源码文本中的全部形式，返回最后一个值（在当前命名空间 user 里）。</summary>
    public static object Eval(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        return Marshal(() => Program.EvalReader(new StringReader(code), "embedded"));
    }

    /// <summary>require 一个命名空间（不在加载路径上时抛 FileNotFoundException）。</summary>
    public static void Require(string ns)
    {
        ArgumentNullException.ThrowIfNull(ns);
        Marshal<object>(() =>
        {
            var require = Namespace.find(Symbol.intern("clojure.core"))?.FindInternedVar(Symbol.intern("require"))
              ?? throw new InvalidOperationException("clojure.core/require is not loaded (no boot image?)");
            require.invoke(Symbol.intern(ns));
            return null;
        });
    }

    /// <summary>命名空间里的 Var 的值（函数）；调用用 invoke(arg…)（IFn）。没有该 Var 或值不是函数时抛异常。</summary>
    public static IFn Var(string ns, string name)
    {
        ArgumentNullException.ThrowIfNull(ns);
        ArgumentNullException.ThrowIfNull(name);
        return Marshal<IFn>(() =>
        {
            var v = Namespace.find(Symbol.intern(ns))?.FindInternedVar(Symbol.intern(name))
              ?? throw new InvalidOperationException($"no such var: {ns}/{name}");
            return v.deref() as IFn ?? throw new InvalidOperationException($"the value of {ns}/{name} is not a function");
        });
    }

    /// <summary>值的 Clojure 文本形式（pr-str 的输出）。</summary>
    public static string Print(object value) => Marshal(() => RT.printString(value));

    // ———————————————— 工作线程 ————————————————

    private static void WorkerLoop()
    {
        foreach (var (body, done) in s_queue.GetConsumingEnumerable())
        {
            try { done.SetResult(body()); }
            catch (Exception e) { done.SetException(e); }
        }
    }

    private static T Marshal<T>(Func<T> body)
    {
        if (!s_initialized) throw new InvalidOperationException("cleat is not initialized: call CleatRuntime.Init() first");
        return MarshalLocal(body);
    }

    private static T MarshalLocal<T>(Func<T> body)
    {
        var done = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        s_queue.Add((() => body(), done));
        return (T)done.Task.GetAwaiter().GetResult();
    }

    /// <summary>在启动时调用，发出 Init 未完成的信号（Marshal 的 s_initialized 检查只在完成后为真，嵌套调用走不到）。</summary>
    private static void InitBody(CleatOptions o)
    {
        var hostOptions = new Program.Options { SchemeBoot = o.SchemeBoot, Boot = o.ClojureBoot };
        foreach (var p in o.LoadPaths) hostOptions.IncludeDirs.Add(Path.GetFullPath(p));
        Program.Start(hostOptions);
        // Eval / Require 的环境：user 命名空间（refer clojure.core）+ *command-line-args*；绑定推给工作线程的余生
        var ns = Namespace.findOrCreate(Symbol.intern("user"));
        Program.ReferClojureCore(ns);
        clojure.lang.Var.pushThreadBindings(RT.map(
            RT.CurrentNSVar, ns,
            RT.CmdLineArgsVar, RT.seq(o.CommandLine)));
    }
}
