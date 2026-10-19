using System;
using System.Collections.Concurrent;
using System.Reflection;
using clojure.lang;
using System.Threading;

namespace Cleat.Core.Runtime;

/// <summary>
/// cleat-133 / 135 / 136 的运行时签名特化钩子。生成的通用入口先观察调用次数；达到阈值后把函数类名与实参类型交给
/// 编译器登记的回调。回调返回特化 IFn 时，通用入口把它写入函数类静态字段；返回 null 或未登记编译器时，
/// 调用始终留在原通用体，因此本类型只提供接合点，不改变任何可见语义。cleat-135 增加进程级生成上限，
/// 防止多签名 / 多函数的按需生成无限消耗内存。cleat-136 另记录逐出程序集的弱引用，用于本地 GC 到达性诊断。
/// </summary>
public static class RuntimeSigSpec
{
    /// <summary>
    /// 观察阈值：通用调用达到这么多次请求一次运行时编译（cleat-139 起默认 8）。要赶在通用体分层升级（30 次调用 + 延迟）之前：
    /// 晚了切换到特化体要从 tier-0 重新预热，mbrot 在第 128 次调用切换时慢 50%，从头特化则持平；编译在后台，低阈值不占调用线程。
    /// CLEAT_RTSPEC_THRESHOLD 覆盖；设为 1 是验证用的压力模式，几乎所有候选第一次调用就重新编译。
    /// </summary>
    public static readonly int Threshold = ReadThreshold();

    static RuntimeSigSpec()
    {
        // CLEAT_RTSPEC_STATS=1：进程退出时在 stderr 打印运行时编译次数（诊断用，不影响计时）
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CLEAT_RTSPEC_STATS")))
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
                Console.Error.WriteLine($"rtspec: compiled {CompiledCount} failed {FailedCount} compile-ms {Interlocked.Read(ref s_compileTicks) * 1000 / System.Diagnostics.Stopwatch.Frequency}");
    }

    private static int ReadThreshold()
    {
        var s = Environment.GetEnvironmentVariable("CLEAT_RTSPEC_THRESHOLD");
        return int.TryParse(s, out var n) && n > 0 ? n : 8;
    }

    // 编译器的代码生成表是进程级共享的：编译单元与运行时补生成互斥。eval 的编译单元可以等待，
    // 运行时补生成只尝试一次，忙就放弃（下个周期再试）；同一线程在编译单元里不补生成。
    private static readonly object s_unitLock = new();
    [ThreadStatic] private static int t_unitDepth;

    /// <summary>codegen 的 generate-unit 进入 / 退出；可重入。</summary>
    public static void EnterUnit()
    {
        Monitor.Enter(s_unitLock);
        t_unitDepth++;
    }

    public static void ExitUnit()
    {
        t_unitDepth--;
        Monitor.Exit(s_unitLock);
    }

    /// <summary>
    /// cleat-138：(函数类名, 实参, 通用函数) → <see cref="RuntimeSigCompiled"/>；null 表示该签名不能特化。由 Clojure 编译器登记。
    /// </summary>
    public static Func<string, object[], object, object> SpecCompiler { get; set; }

    private static long s_compiled, s_failed, s_compileTicks;
    private static readonly bool s_debug = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CLEAT_RTSPEC_DEBUG"));

    /// <summary>成功 / 失败的运行时编译次数；诊断与测试用。</summary>
    public static long CompiledCount => Interlocked.Read(ref s_compiled);
    public static long FailedCount => Interlocked.Read(ref s_failed);

    /// <summary>
    /// 生成入口达到阈值时调用（cleat-138）：key 为 0 或机制关闭时返回 null（入口永久回通用体）；
    /// 否则建表并按当前实参生成首个条目；首个条目失败同样返回 null。
    /// </summary>
    public static object Install(string className, int mask, object[] args, object generic)
    {
        if (!Enabled || SpecCompiler is null) return null;
        var key = RuntimeSigKey.Of(mask, args);
        if (key == 0) return null;
        var table = new RuntimeSigTable(className, mask, generic);
        if (Background)
        {
            // 后台编译（cleat-139，JIT 的做法）：本次与编译完成前的调用都走通用体，编译时间不落在调用线程上
            table.Request(key, args);
            return table;
        }
        // 同步模式：首个签名都生成不了（未登记 IR、证明失败、忙）就不建表，入口永久回通用体
        return table.Seed(key, args) ? table : null;
    }

    /// <summary>
    /// 编译期已生成的条目登记（cleat-140）：调用路径类型已经证明时，在目标函数第一次被调用前填表。
    /// 竞态语义与 <see cref="Install"/> 相同：表可能刚被首次调用建出，已有序照旧；字段写回是幂等的。
    /// </summary>
    public static void SeedCompiled(object generic, int mask, RuntimeSigCompiled compiled)
    {
        var type = generic.GetType();
        var field = type.GetField("__rtSpec", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
        if (field is null) return;
        var table = field.GetValue(null) as RuntimeSigTable;
        if (table is null)
        {
            table = new RuntimeSigTable(type.FullName ?? type.Name, mask, generic);
            field.SetValue(null, table);
        }
        table.SeedCompiled(compiled);
    }

    /// <summary>InstallRtSpec 调用点使用的登记入口（cleat-140）：编译单元里已生成特化类，装配时只填目标签名表。</summary>
    public static void SeedCompiled(object generic, long key, int mask, bool guarded, IFn fn)
        => SeedCompiled(generic, mask, new RuntimeSigCompiled(fn, key, guarded, null));

    /// <summary>
    /// 后台编译（缺省）：运行时编译放到一个专用后台线程依次进行，调用线程不等待。CLEAT_RTSPEC_SYNC=1 改回在触发的
    /// 调用线程上同步编译（测试要确定的生成时机时用）。
    /// </summary>
    public static bool Background { get; set; } = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CLEAT_RTSPEC_SYNC"));

    private static readonly System.Collections.Concurrent.BlockingCollection<Action> s_queue = new();
    private static Thread s_worker;

    /// <summary>把编译任务排进后台线程；线程第一次用时启动（大栈：编译器递归深）。</summary>
    internal static void Enqueue(Action work)
    {
        if (s_worker is null)
        {
            lock (s_queue)
            {
                if (s_worker is null)
                {
                    var t = new Thread(() =>
                    {
                        foreach (var w in s_queue.GetConsumingEnumerable())
                        {
                            try { w(); } catch { }
                        }
                    }, 256 * 1024 * 1024) { IsBackground = true, Name = "cleat-rtspec" };
                    t.Start();
                    s_worker = t;
                }
            }
        }
        s_queue.Add(work);
    }

    /// <summary>调用编译器回调；忙、重入、预算耗尽或任何异常都返回 null。</summary>
    internal static RuntimeSigCompiled Compile(string className, object[] args, object generic)
    {
        var compiler = SpecCompiler;
        if (!Enabled || compiler is null || !CanGenerate() || t_unitDepth > 0) return null;
        // 后台线程可以等 eval 的编译单元结束；调用线程上的同步编译只尝试一次
        if (Background && Thread.CurrentThread == s_worker) Monitor.Enter(s_unitLock);
        else if (!Monitor.TryEnter(s_unitLock)) return null;
        t_unitDepth++;
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            var raw = compiler(className, args, generic);
            var r = raw as RuntimeSigCompiled;
            if (r is null) Interlocked.Increment(ref s_failed); else Interlocked.Increment(ref s_compiled);
            if (r is null && s_debug) Console.Error.WriteLine($"rtspec: {className} -> {raw?.GetType().FullName ?? "null"}");
            return r;
        }
        catch (Exception e)
        {
            Interlocked.Increment(ref s_failed);
            if (s_debug) Console.Error.WriteLine($"rtspec: {className} threw {e.GetType().Name}: {e.Message}");
            return null;
        }
        finally
        {
            Interlocked.Add(ref s_compileTicks, System.Diagnostics.Stopwatch.GetTimestamp() - t0);
            t_unitDepth--;
            Monitor.Exit(s_unitLock);
        }
    }

    /// <summary>进程里允许按需生成的特化程序集上限；保守首版预算。</summary>
    public const int MaxGenerated = 4096;

    private static int _generated;

    // 逐出后的程序集只保存弱引用；诊断本身不延长 RunAndCollect 程序集的生命周期。
    private static readonly ConcurrentQueue<WeakReference> s_retired = new();

    /// <summary>cleat-133 ~ 136 的旧协议（只为旧种子保留，cleat-141 重生成种子后删除）。</summary>
    public static Func<string, object[], object, object> Compiler { get; set; }

    /// <summary>构建镜像时关闭：编译器自身的函数会执行超过阈值，递归触发运行时编译会自举死循环。</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>是否还能新增一个运行时生成程序集；达上限后既有特化继续使用，新签名只回通用体。</summary>
    public static bool CanGenerate() => System.Threading.Volatile.Read(ref _generated) < MaxGenerated;

    /// <summary>运行时特化程序集成功发射后登记；失败不占预算。</summary>
    public static void MarkGenerated() => Interlocked.Increment(ref _generated);

    /// <summary>签名表逐出条目时登记程序集弱引用；null / 已失效引用忽略。</summary>
    public static void RecordRetiredAssembly(WeakReference assembly)
    {
        if (assembly is not null) s_retired.Enqueue(assembly);
    }

    /// <summary>累计逐出的程序集弱引用数；不触发 GC。</summary>
    public static int RetiredAssemblyCount() => s_retired.Count;

    /// <summary>强制完整 GC 后仍可达的已逐出程序集数；测试 / 诊断用。</summary>
    public static int AliveRetiredAssemblies()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var alive = 0;
        foreach (var assembly in s_retired)
        {
            if (assembly.IsAlive) alive++;
        }
        return alive;
    }

    /// <summary>生成代码在慢路径调用；异常一律视作不能特化，调用方回退通用体。</summary>
    public static object Promote(string className, object[] args, object generic)
    {
        var compiler = Compiler;
        if (!Enabled) return null;
        if (compiler is null) return null;
        try
        {
            return compiler(className, args, generic);
        }
        catch
        {
            return null;
        }
    }
}
