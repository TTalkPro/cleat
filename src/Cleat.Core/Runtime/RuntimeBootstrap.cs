using System;
using System.Reflection;
using System.Threading;

namespace Cleat.Core.Runtime;

/// <summary>
/// 惰性启动（cleat-067，designs/17 §4）：包程序集的每个类型初始化器开头调用 <see cref="EnsureStarted"/>（发射器按 Lcil 的
/// <c>(lazy-start)</c> 插入），所以 C# / F# 代码不调用任何初始化 API 就能用 gen-class、defrecord 等生成的类型——相当于 JVM 上
/// gen-class 类的静态初始化经 clojure.lang.RT 拉起运行时。
///
/// 宿主（Program.Start）启动时自己登记 <see cref="MarkStarting"/> / <see cref="MarkStarted"/>；否则第一次调用时按名字载入
/// Cleat.Hosting（Cleat.Core 不引用它：依赖方向是 Hosting → Scheme → Core）并以缺省选项调 <c>CleatRuntime.Init</c>。
/// 并发的首次调用只启动一次，其余线程等它完成；正在启动的线程自己（启动期间的加载）直接返回。已启动时代价是一次 volatile 读。
/// </summary>
public static class RuntimeBootstrap
{
    private const int None = 0, Starting = 1, Started = 2;

    private static volatile int s_state;
    private static volatile int s_startingThread = -1;
    private static readonly object s_gate = new object();
    private static readonly ManualResetEventSlim s_ready = new ManualResetEventSlim(false);
    private static Exception s_failure;

    public static bool IsStarted => s_state == Started;

    /// <summary>运行时没启动时启动它（见类说明）。</summary>
    public static void EnsureStarted()
    {
        if (s_state == Started || s_startingThread == Environment.CurrentManagedThreadId) return;
        bool start = false;
        lock (s_gate)
        {
            if (s_state == None && s_failure == null)
            {
                s_state = Starting;
                start = true;
            }
        }
        if (start) Start();
        else s_ready.Wait();
        if (s_failure != null)
            throw new InvalidOperationException("the cleat runtime failed to start: " + s_failure.Message, s_failure);
    }

    /// <summary>启动过程开始（宿主或 Hosting 在启动线程上调用）：这条线程上的 <see cref="EnsureStarted"/> 直接返回。</summary>
    public static void MarkStarting()
    {
        lock (s_gate)
        {
            if (s_state == None) s_state = Starting;
            s_startingThread = Environment.CurrentManagedThreadId;
        }
    }

    /// <summary>运行时已启动（镜像已加载、钩子已登记）：等待的线程继续。</summary>
    public static void MarkStarted()
    {
        lock (s_gate)
        {
            s_state = Started;
            s_startingThread = -1;
        }
        s_ready.Set();
    }

    private static void Start()
    {
        try
        {
            Assembly hosting;
            try
            {
                hosting = Assembly.Load(new AssemblyName("Cleat.Hosting"));
            }
            catch (Exception e)
            {
                throw new InvalidOperationException(
                    "a cleat package was used before the cleat runtime was started, and Cleat.Hosting.dll could not be loaded to start it " +
                    "(reference the Cleat.Runtime package, or call Cleat.Core.Hosting.CleatRuntime.Init first)", e);
            }
            var runtime = hosting.GetType("Cleat.Core.Hosting.CleatRuntime", true);
            var init = runtime.GetMethod("Init", BindingFlags.Public | BindingFlags.Static)
                ?? throw new MissingMethodException("Cleat.Core.Hosting.CleatRuntime", "Init");
            try
            {
                init.Invoke(null, new object[init.GetParameters().Length]);
            }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            }
            MarkStarted();
        }
        catch (Exception e)
        {
            lock (s_gate)
            {
                s_failure = e;
                s_state = None;
                s_startingThread = -1;
            }
            s_ready.Set();
        }
    }
}
