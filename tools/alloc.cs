#!/usr/bin/env -S dotnet run
// 分配采样（.NET 10 单文件程序，cleat-100；从 skeg 的 tools/alloc.cs 搬来）：在本进程里运行 cleat 宿主，统计运行时的
// GCAllocationTick 事件（约每分配 100 KB 采样一次，附对象类型名），按类型汇总分配量，最后给出各代 GC 次数与总分配量。
//
//   dotnet run tools/alloc.cs -- [--top N] <cleat 的参数 …>
//   例：dotnet run tools/alloc.cs -- --top 25 -I bench -m bench.nested-arith
//       dotnet run tools/alloc.cs -- script.clj
//
// 镜像按宿主的规则自动找（src/boot/）。采样是统计性的：类型的分配量 = 落在它上面的采样数 × 采样间隔，占比可信，小量的类型误差大。
// 宿主代码在本进程里运行，所以 cleat 的输出照常出现在标准输出上；报告写到标准错误。
// 单文件程序默认按 AOT 配置（关掉 EventSource 与动态代码）：这里两者都要
#:property ImplicitUsings=enable
#:property Nullable=enable
#:property PublishAot=false
#:property EventSourceSupport=true
using System.Diagnostics.Tracing;
using System.Reflection;
using System.Runtime.Loader;

string root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName((string)AppContext.GetData("EntryPointFilePath")!)!, ".."));
string hostDir = Path.Combine(root, "src", "Cleat.Host", "bin", "Release", "net10.0");
if (!File.Exists(Path.Combine(hostDir, "cleat.dll")))
{
    Console.Error.WriteLine("alloc: build the host first: dotnet build Cleat.slnx -c Release");
    return 2;
}

int top = 30;
bool firstChance = false;
var hostArgs = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--top" && i + 1 < args.Length) { top = int.Parse(args[++i]); continue; }
    if (args[i] == "--first-chance") { firstChance = true; continue; }   // 排错：打印进程里抛出的每个异常
    hostArgs.Add(args[i]);
}
if (firstChance)
    AppDomain.CurrentDomain.FirstChanceException += (_, e) => Console.Error.WriteLine("first-chance: " + e.Exception.GetType().Name + ": " + e.Exception.Message);

AssemblyLoadContext.Default.Resolving += (ctx, name) =>
{
    string p = Path.Combine(hostDir, name.Name + ".dll");
    return File.Exists(p) ? ctx.LoadFromAssemblyPath(p) : null;
};

var listener = new AllocListener();
long allocated0 = GC.GetTotalAllocatedBytes(true);
int[] gc0 = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];

// 宿主可能经 exit 结束进程：报告挂在 ProcessExit 上
AppDomain.CurrentDomain.ProcessExit += (_, _) => Report();

// 在缺省加载上下文里加载（Assembly.LoadFrom 会另开一个上下文，Cleat.Core 被加载两份，生成代码的常量表初始化失败）；
// 宿主目录里的其余程序集（Cleat.Core、Cleat.Compiler、Cleat.Hosting）也先加载进来：编译器按程序集名加载，
// 本进程的 AppContext.BaseDirectory 不是宿主目录，按目录探测找不到
foreach (var dll in new[] { "Cleat.Core.dll", "Cleat.Compiler.dll", "Cleat.Hosting.dll" })
    if (File.Exists(Path.Combine(hostDir, dll))) AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(hostDir, dll));
var host = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(hostDir, "cleat.dll"));
var main = host.GetType("Cleat.Host.Program")?.GetMethod("Main") ?? host.EntryPoint!;
int code = (int)main.Invoke(null, [hostArgs.ToArray()])!;
Report();
return code;

void Report()
{
    if (listener.Reported) return;
    listener.Reported = true;
    Thread.Sleep(200);   // EventListener 的事件在另一个线程上投递
    long total = GC.GetTotalAllocatedBytes(true) - allocated0;
    var rows = listener.Snapshot();
    long sampled = rows.Sum(r => r.Bytes);
    var err = Console.Error;
    err.WriteLine();
    err.WriteLine($"allocated {total / 1048576.0:F1} MB; GC gen0 {GC.CollectionCount(0) - gc0[0]}, gen1 {GC.CollectionCount(1) - gc0[1]}, gen2 {GC.CollectionCount(2) - gc0[2]}; samples {rows.Sum(r => r.Count)}");
    err.WriteLine($"{"type",-60} {"MB",9} {"%",6}");
    foreach (var r in rows.OrderByDescending(r => r.Bytes).Take(top))
        err.WriteLine($"{Trim(r.Type),-60} {r.Bytes / 1048576.0,9:F1} {100.0 * r.Bytes / Math.Max(1, sampled),6:F1}");
}

static string Trim(string s) => s.Length <= 60 ? s : s[..27] + "…" + s[^32..];

internal sealed class AllocListener : EventListener
{
    private readonly Dictionary<string, (long Bytes, long Count)> _byType = new();
    public bool Reported;

    protected override void OnEventSourceCreated(EventSource source)
    {
        // GC 关键字 0x1，Verbose 级别才有 AllocationTick
        if (source.Name == "Microsoft-Windows-DotNETRuntime")
            EnableEvents(source, EventLevel.Verbose, (EventKeywords)0x1);
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        if (e.EventName is null || !e.EventName.StartsWith("GCAllocationTick", StringComparison.Ordinal) || e.Payload is null) return;
        string type = "?";
        long amount = 0;
        for (int i = 0; i < e.PayloadNames!.Count; i++)
        {
            switch (e.PayloadNames[i])
            {
                case "TypeName": type = e.Payload[i] as string ?? "?"; break;
                case "AllocationAmount64": amount = Convert.ToInt64(e.Payload[i]); break;
                case "AllocationAmount" when amount == 0: amount = Convert.ToInt64(e.Payload[i]); break;
            }
        }
        lock (_byType)
        {
            _byType.TryGetValue(type, out var v);
            _byType[type] = (v.Bytes + amount, v.Count + 1);
        }
    }

    public List<(string Type, long Bytes, long Count)> Snapshot()
    {
        lock (_byType) return _byType.Select(kv => (kv.Key, kv.Value.Bytes, kv.Value.Count)).ToList();
    }
}
