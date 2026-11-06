using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace Cleat.Core.Runtime;

/// <summary>
/// 启动分段计时（cleat-159，designs/29 §3.1）：把启动过程按段采样，供 <c>CLEAT_TIME</c> 输出。
/// 没设置 CLEAT_TIME 时调用点自己判 <see cref="Enabled"/>，Begin 与全部采样方法都不执行（不计时、不输出，与 cleat-052 的现状一致；
/// 111 的复核说明"调用了但立即早退"也会把这些方法送进 JIT 队列，短基准的热循环 tier1 时机因此被挪动）。
///
/// 一条时间线按顺序标记，每段是上一个标记到此处的间隔；间隔按 tick 累计、只在输出时换算成毫秒，
/// 所以逐格截断不会把时间漏掉（镜像入口有几十条 in-ns 边界，逐格截断会丢几十毫秒）。
///   host（宿主入口与大栈线程）→ cli（命令行解析）→ paths（找镜像与编译器包的路径探测）→ runtime（原语注册与 RT.Init）→
///   image（程序集加载 / 登记 / 旁边的 .ifc / 模块入口）→ packages（镜像旁的包程序集与加载路径登记）→
///   compiler（require 驱动 + install-hooks!）→ other（输出之前的剩余）。
/// 镜像的模块初始化再按 in-ns 边界细分（<see cref="MarkNamespace"/>）：boot-order.edn 的各条目在镜像入口里顺序执行，
/// 条目之间的边界就是 clojure.core/in-ns 的调用，因此这一列给出"哪个 boot 命名空间的初始化最贵"。
/// 输出第一行保持 cleat-052 以来的格式（HostProcessTests 依赖），分段与细分在随后的行。
/// </summary>
public static class StartupTimings
{
    /// <summary>时间线上的段（枚举的顺序就是打印的顺序）。</summary>
    public enum Segment
    {
        Host, Cli, Paths, Runtime,
        ImageAssembly, ImageRegister, ImageIfc, ImageEntry,
        Packages, CompilerRequire, CompilerHooks, Other,
        // REPL 的三段（cleat-159）：从启动报告之后继续计时，只在 -r 走到首个提示符时输出第二行。
        // UserSetup = user 命名空间 refer clojure.core 与主绑定（所有动作路径共用，不只 REPL）；
        // ReplRequire = clojure.main/repl-requires 那批库；
        // ReplPrompt = 行编辑器 / 提示符本身（不含等输入）
        UserSetup, ReplRequire, ReplPrompt,
    }

    /// <summary>
    /// CLEAT_TIME 是否设置（<see cref="Begin"/> 置位）。调用点的约定是 **先判这个字段再调采样方法**：
    /// 未启用时一条采样语句都不执行，StartupTimings 的方法不进 JIT 队列（111 的复核显示，哪怕只多几个启动期方法，
    /// 热循环的 tier1 安装时机也会被挪动，短基准因此在两个量化档位之间跳）。
    /// </summary>
    public static bool Enabled;

    // cleat-159 复核（111）：这三格子在 Begin 里分配，类本身没有字段初始化器 —— 未启用时读 Enabled 只会跑一个空的类型初始化器，
    // 不分配、也不把下面的方法拖进 JIT 队列（分层 JIT 下启动期的方法数会挪动热循环 tier1 安装的时机）
    private static long[] s_ticks;
    private static Dictionary<string, long> s_nsInit;
    /// <summary>repl-requires 的逐库份额（cleat-159）：按 require 顺序记，首个提示符前输出。</summary>
    private static List<(string lib, long ticks)> s_replRequires;

    /// <summary>上一条 in-ns 进入的命名空间：到下一条边界之间的耗时算它的初始化（in-ns 是"进入"，不是"离开"）。</summary>
    private static string s_lastNs;

    /// <summary>正在跑镜像的模块入口（只有这一段里的 in-ns 才算 boot 条目的边界）。</summary>
    public static bool InBootEntry;

    private static long _freq, _last, _start;

    public static void Begin()
    {
        Enabled = true;
        s_ticks = new long[Enum.GetValues(typeof(Segment)).Length];
        s_nsInit = new Dictionary<string, long>(StringComparer.Ordinal);
        s_replRequires = new List<(string, long)>();
        _freq = Stopwatch.Frequency;
        _start = _last = Stopwatch.GetTimestamp();
    }

    /// <summary>把上一个标记到此处的间隔记进这一段；未启用时什么都不做。</summary>
    public static void Mark(Segment segment)
    {
        if (!Enabled) return;
        long now = Stopwatch.GetTimestamp();
        s_ticks[(int)segment] += now - _last;
        _last = now;
    }

    /// <summary>
    /// 镜像入口执行到一条 in-ns（进入命名空间 <paramref name="ns"/>）：上一段间隔既加进镜像入口，也记在**上一条** in-ns
    /// 进入的命名空间下（in-ns 是进入的边界，边界之前的工作是上一个条目的初始化）。第一条边界之前的是入口序言。
    /// </summary>
    public static void MarkNamespace(string ns)
    {
        if (!Enabled || !InBootEntry || string.IsNullOrEmpty(ns)) return;
        long now = Stopwatch.GetTimestamp();
        long d = now - _last;
        _last = now;
        s_ticks[(int)Segment.ImageEntry] += d;
        Add(d, s_lastNs ?? "(entry prologue)");
        s_lastNs = ns;
    }

    /// <summary>镜像入口跑完：最后一段（最后一个 in-ns 到入口返回）算最后一个条目的初始化。</summary>
    public static void EndBootEntry()
    {
        if (!Enabled) return;
        InBootEntry = false;
        long now = Stopwatch.GetTimestamp();
        long d = now - _last;
        _last = now;
        s_ticks[(int)Segment.ImageEntry] += d;
        if (s_lastNs != null) Add(d, s_lastNs);
        s_lastNs = null;
    }

    private static void Add(long ticks, string name) => s_nsInit[name] = s_nsInit.GetValueOrDefault(name) + ticks;

    /// <summary>
    /// repl-requires 里一个库 require 完成（cleat-159）：把上一段间隔既加进 ReplRequire 段，也按库名单记一份。
    /// 未启用时什么都不做（调用点不需要再判 CLEAT_TIME）。
    /// </summary>
    public static void MarkReplRequire(string lib)
    {
        if (!Enabled) return;
        long now = Stopwatch.GetTimestamp();
        long d = now - _last;
        _last = now;
        s_ticks[(int)Segment.ReplRequire] += d;
        s_replRequires.Add((lib, d));
    }

    private static long Ms(Segment segment) => s_ticks[(int)segment] * 1000 / _freq;
    private static long Ms(params Segment[] segments) => segments.Sum(s => s_ticks[(int)s]) * 1000 / _freq;
    private static long MsTicks(long ticks) => ticks * 1000 / _freq;

    /// <summary>启用的话输出分段：第一行沿用 cleat-052 的格式，随后是各段与镜像条目的份额。</summary>
    /// <param name="compilerKind">编译器的来源（cleat-177 起；cleat-182 起只有 cs）。</param>
    public static void Report(long imageMs, long compilerMs, string compilerKind)
    {
        if (!Enabled) return;
        Mark(Segment.Other);
        _bootEnds = _last;             // 启动报告的起点就是 Other 段的末尾
        long total = MsTicks(Stopwatch.GetTimestamp() - _start);      // 先定 total，之后的格式化不算进去
        var e = new StringBuilder();
        e.Append("cleat: Clojure image ").Append(imageMs).Append(" ms, compiler (")
         .Append(compilerKind).Append(") ").Append(compilerMs).AppendLine(" ms");
        e.Append("cleat: segments host ").Append(Ms(Segment.Host))
         .Append(" / cli ").Append(Ms(Segment.Cli))
         .Append(" / paths ").Append(Ms(Segment.Paths))
         .Append(" / runtime ").Append(Ms(Segment.Runtime))
         .Append(" / image ").Append(Ms(Segment.ImageAssembly, Segment.ImageRegister, Segment.ImageIfc, Segment.ImageEntry))
         .Append(" (assembly ").Append(Ms(Segment.ImageAssembly))
         .Append(" + register ").Append(Ms(Segment.ImageRegister))
         .Append(" + ifc ").Append(Ms(Segment.ImageIfc))
         .Append(" + entry ").Append(Ms(Segment.ImageEntry)).Append(')')
         .Append(" / packages ").Append(Ms(Segment.Packages))
         .Append(" / compiler ").Append(Ms(Segment.CompilerRequire, Segment.CompilerHooks))
         .Append(" (require ").Append(Ms(Segment.CompilerRequire))
         .Append(" + hooks ").Append(Ms(Segment.CompilerHooks)).Append(')')
         .Append(" / other ").Append(Ms(Segment.Other))
         .Append(" = total ").Append(total).AppendLine(" ms");
        if (s_nsInit.Count > 0)
        {
            var top = s_nsInit.OrderByDescending(kv => kv.Value).Take(10).ToList();
            e.Append("cleat: image module init per in-ns boundary (top ").Append(top.Count).Append(" of ")
             .Append(s_nsInit.Count).Append("): ");
            foreach (var (name, ticks) in top) e.Append(name).Append(' ').Append(MsTicks(ticks)).Append(" ms, ");
            e.Remove(e.Length - 2, 2).AppendLine();
        }
        Console.Error.Write(e.ToString());
        s_reported = true;
    }

    /// <summary>启动报告是否已经打过（cleat-161：之后的按需加载要补一行，不能让它看起来是免费的）。</summary>
    private static bool s_reported;

    /// <summary>
    /// 编译器在启动报告**之后**才按需加载完成（cleat-161，designs/29 §3.3）：补一行说那一格花在哪、什么时候花的。
    /// 报告之前加载的（自举的改名工具编译器，仍然 eager）不打这行——那两个标记本来就在报告的 compiler 段里。
    /// 第一行的 <c>compiler (package|source|cs) N ms</c> 格式不变（HostProcessTests 依赖它）。
    /// </summary>
    public static void CompilerLoadedOnDemand(long ms, string compilerKind)
    {
        if (!Enabled || !s_reported) return;
        Console.Error.WriteLine("cleat: compiler loaded on demand: "
            + compilerKind + " " + ms + " ms (require " + Ms(Segment.CompilerRequire)
            + " + hooks " + Ms(Segment.CompilerHooks) + ")");
    }

    private static long _bootEnds;

    /// <summary>
    /// 提示符即将写出（REPL 的读循环在写提示符处调用；必须在阻塞读之前，否则会把用户输入的时间算进来）：
    /// 记一段 <see cref="Segment.ReplPrompt"/> 并输出一次 REPL 行。
    /// </summary>
    public static void ReplPromptShown()
    {
        if (!Enabled) return;
        Mark(Segment.ReplPrompt);
        ReportReplPrompt();
    }

    /// <summary>
    /// REPL 走到首个提示符（cleat-159）：再输出一行，量"启动报告之后到提示符"的份额。
    /// 29 §3.1 的第三种场景要的就是这个数（提示符前宿主还要 require repl-requires 并起行编辑器）。
    /// 只在启用时输出一次；非 REPL 路径不调用，<see cref="Report"/> 的那几行不变。
    /// </summary>
    public static void ReportReplPrompt()
    {
        if (!Enabled || _replReported) return;
        _replReported = true;
        long now = Stopwatch.GetTimestamp();
        long afterBoot = MsTicks(now - _bootEnds);
        var e = new StringBuilder();
        e.Append("cleat: repl setup ").Append(afterBoot).Append(" ms (user ").Append(Ms(Segment.UserSetup))
         .Append(" + repl-requires ").Append(Ms(Segment.ReplRequire))
         .Append(" + prompt ").Append(Ms(Segment.ReplPrompt)).Append(") = first prompt ")
         .Append(MsTicks(now - _start)).AppendLine(" ms after start");
        if (s_replRequires.Count > 0)
        {
            e.Append("cleat: repl-requires per lib:");
            foreach (var (lib, ticks) in s_replRequires) e.Append(' ').Append(lib).Append(' ').Append(MsTicks(ticks)).Append(" ms,");
            e.Remove(e.Length - 1, 1).AppendLine();
        }
        Console.Error.Write(e.ToString());
    }

    private static bool _replReported;
}
