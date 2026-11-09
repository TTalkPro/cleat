#!/usr/bin/env -S dotnet run
#:property ImplicitUsings=enable
#:property Nullable=enable
// Cleat 的维护命令（.NET 10 单文件程序）。日常构建与测试不需要它：仓库根目录 dotnet build / dotnet test 即可。
//
//   dotnet run tools/build.cs -- <命令> [参数]
//
// 命令：
//   ilverify <dll> …      用 ILVerify 校验发射器生成的程序集（dotnet tool install -g dotnet-ilverify）  [cleat-020]
//   conformance [--stats] 语料统计（cleat-002）
//   conformance --run [--portability common|jvm|all] [--timeout ms] [--report out.edn] [--limit n]
//                         在宿主（Clojure 镜像）上运行语料，按 = 比较，报告通过数与失败分组（tools/conformance.clj）  [cleat-053]
//                         [--results out.edn] 每行结果；[--gate] 按 tests/conformance/cleat-divergences.edn 门禁       [cleat-032]
//   cts cts|test-clojure [--write] [--only 正则]
//                         逐命名空间跑 clojure-test-suite / test_clojure，与 tests/cts-baseline.edn /
//                         tests/test-clojure-baseline.edn 比较（--write 重写基线）（tools/cts.clj）                 [cleat-032]
//   compile-libs [命名空间…]
//                         预编译随附命名空间（boot-order.edn 里 :boot false 的，或命令行给出的）为一个包程序集
//                         src/boot/Cleat.Clojure.Libs.dll（designs/17）；宿主登记 Clojure 镜像旁的它，require 走包程序集
//                         （从源码加载不了的命名空间跳过）                                  [cleat-033、cleat-067]
//   compile-tools         tools.namespace 与 tools.build 编成包程序集 src/boot/tools/（pack 也做；镜像变了之后单独重编用）  [cleat-082]
//   pack [-o 目录] [--version 版本]
//                         运行时的 NuGet 包 Cleat.Runtime（cleat-067，designs/17 §3）与 dotnet tool 包 Cleat.Cli（cleat-071）：
//                         Release 构建、compile-libs、tools.build 的包程序集，再 dotnet pack；默认输出 artifacts/nuget/。推到本地目录源或 BaGetter 后 C# / F#
//                         项目引用它与 cleat 的包程序集即可（运行时第一次用到时自己启动）
//   image-set [目录] [--twice] [--ilverify]
//                         编一整套到独立目录（默认 src/boot-cs/，不动 src/boot）：镜像 cleat.boot.*、随附库 Cleat.Clojure.Libs.dll、
//                         tools/（tools.namespace、tools.build）、packages/（nrepl、neorepl、nanopass、test.check、spec.alpha、tools.reader、tools.analyzer、core.async）。
//                         --twice 再编一套到 <目录>.2 并逐文件比较（G5 确定性）；--ilverify 校验全部程序集（G3）。
//                         在其上跑测试：CLEAT_BOOT=<目录>/cleat.boot.dll dotnet test …（cleat-177 起；旧名 cs-image 仍可用）  [cleat-182]
//   bench [-r 次数] [--write] [--only 正则]
//                         运行 bench/ 下的基准（每个一次宿主进程，逐个打印 BENCH <名字> <毫秒>），聚合最小/中位；
//                         --write 把结果写到 bench/baseline.edn（优化前后对照，cleat-042）
//   publish [-r RID] [-o 目录]
//                         自包含发布（cleat-034）：.NET 运行时随发布，目标机器不需要 SDK 或 .NET；Clojure 镜像、
//                         编译器与 AOT 库随发布。默认 RID 为当前平台，
//                         默认输出 artifacts/publish/cleat-<RID>/。REPL 与脚本直接可用
//   r2r [-o 目录] [--bin 目录] [--boot 目录] [--crossgen2 路径] [--only a.dll,b.dll]
//                         ReadyToRun（cleat-160，designs/29 §3.2）：把 cleat 自己的 C# 运行时、编译器与随附库
//                         用 crossgen2 预编译成机器码，输出到一个**快照目录**（默认 artifacts/r2r/）。
//                         src/boot/*.dll 保持纯 IL——确定性比较与长跑热点布局不受影响。每次都从当前构建
//                         重做（整个目录 1.6 s），没有陈旧问题。
//                         --crossgen2 指工具路径；缺省按当前运行时版本从 NuGet 恢复 Microsoft.NETCore.App.Crossgen2.<RID>
//                         --mibc 可给 dotnet-pgo 生成的启动画像；--partial 只按该画像 rooting 方法（cleat-160）。
using System.Diagnostics;
using System.IO.Compression;
using System.Text.RegularExpressions;

string root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName((string)AppContext.GetData("EntryPointFilePath")!)!, ".."));
string srcDir = Path.Combine(root, "src");
string solution = Path.Combine(root, "Cleat.slnx");
string Bin(string project, string dll) => Path.Combine(srcDir, project, "bin", "Release", "net10.0", dll);
string srcBootDir = Path.Combine(srcDir, "boot");                        // 镜像输出目录（dotnet build 的 GenerateBootImage）与 boot-order.edn
// 当前起作用的镜像目录（cleat-177）：设了 CLEAT_BOOT（如 image-set 编的 src/boot-cs/）时是它所在的目录，否则 src/boot。
// compile-libs / compile-tools / pack / publish / r2r 针对它——随附库要按它旁边的镜像编，打包的镜像与环境里运行的镜像一致。
string bootDir = Environment.GetEnvironmentVariable("CLEAT_BOOT") is { Length: > 0 } envBoot
    ? Path.GetDirectoryName(Path.GetFullPath(envBoot))! : srcBootDir;

try
{
    string command = args.Length > 0 ? args[0] : "help";
    string[] rest = args.Skip(1).ToArray();
    switch (command)
    {
        case "ilverify":
            if (rest.Length == 0) return Usage();
            foreach (var dll in rest) ILVerify(Path.GetFullPath(dll));
            break;
        case "conformance":
            Conformance(rest);
            break;
        case "cts":
            Run("dotnet", new[] { Bin("Cleat.Host", "cleat.dll"), Path.Combine(root, "tools", "cts.clj") }.Concat(rest).ToArray(), root);
            break;
        case "compile-libs":
            BuildCSharp();
            CompileLibs(rest);
            break;
        case "image-set":
        case "cs-image":
            BuildCSharp();
            ImageSet(rest);
            break;
        case "compile-tools":
            BuildCSharp();
            CompileToolLibs();
            break;
        case "publish":
            Publish(rest);
            break;
        case "r2r":
            ReadyToRun(rest);
            break;
        case "pack":
            Pack(rest);
            break;
        case "bench":
            Bench(rest);
            break;
        default:
            return Usage();
    }
    return 0;
}
catch (CommandFailed e)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}

int Usage()
{
    Console.Error.WriteLine("usage: dotnet run tools/build.cs -- ilverify <dll>... | image-set [dir] [--twice] [--ilverify] | conformance [--run] | cts cts|test-clojure | compile-libs [ns...] | publish [-r RID] [-o dir] [--r2r [--mibc path] [--partial]] | r2r [-o dir] [--bin dir] [--boot dir] [--crossgen2 path] [--only a.dll,b.dll] [--mibc path] [--partial] | pack [-o dir] [--version v] [--r2r] | bench [-r n] [--write] [--only re]");
    Console.Error.WriteLine("(to build and test: dotnet build / dotnet test in the repository root)");
    return 2;
}

// 预编译随附命名空间（cleat-033、cleat-067）：boot-order.edn 里 :boot false 且没有 :loaded-by 的条目（也可在命令行点名）。
// 先在一个宿主进程里逐个试 require，从源码都加载不了的（clojure.repl.deps 需要未内置的 clojure/clr/basis）跳过；其余在一个进程里
// 编成一个包程序集 src/boot/Cleat.Clojure.Libs.dll（--compile-package，designs/17 §2）。运行 -c Release 的宿主。
void CompileLibs(string[] a)
{
    string host = Bin("Cleat.Host", "cleat.dll"), clj = Path.Combine(srcDir, "clojure");
    string[] nses = LoadableLibNamespaces(a);
    string dll = Path.Combine(bootDir, "Cleat.Clojure.Libs.dll");
    Run("dotnet", [host, "--boot", Path.Combine(bootDir, "cleat.boot.dll"), "-I", clj, "--compile-package", dll, "--package", "clojure", "--root", clj, .. nses], root);
    Console.WriteLine($"compile-libs: {nses.Length} namespace(s) -> {dll}");
}

// 自包含发布（cleat-034，做法沿用 IronScheme designs/07 §3）：宿主与其 .NET 运行时（含编译器 Cleat.Compiler）+ Clojure 镜像
// + 随附库的包程序集。发布目录本身在加载路径上（AppContext.BaseDirectory），宿主自动找到镜像与库；不裁剪（互操作靠反射元数据）。
void Publish(string[] a)
{
    string rid = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier, outDir = null;
    // cleat-160 收口：全量 R2R 与 MIBC partial 的 56 × 5 全表都有可声明回退（29 §3.2），
    // 默认产物保持纯 IL，R2R 只作 opt-in（--r2r，partial 另需 --mibc）。
    bool r2r = false, partial = false; string? mibc = null;
    for (int i = 0; i < a.Length; i++)
    {
        switch (a[i])
        {
            case "-r" when i + 1 < a.Length: rid = a[++i]; break;
            case "-o" when i + 1 < a.Length: outDir = Path.GetFullPath(a[++i]); break;
            case "--r2r": r2r = true; break;
            case "--no-r2r": r2r = false; break;      // 兼容旧写法；缺省已是纯 IL
            case "--mibc" when i + 1 < a.Length: mibc = Path.GetFullPath(a[++i]); break;
            case "--partial": partial = true; break;
            default: throw new CommandFailed("publish: unexpected argument " + a[i]);
        }
    }
    if (r2r && partial && mibc == null)
        throw new CommandFailed("publish: --partial 需要 --mibc");
    if (!r2r && (partial || mibc != null))
        throw new CommandFailed("publish: --no-r2r 不能与 --partial / --mibc 同用");
    outDir ??= Path.Combine(root, "artifacts", "publish", "cleat-" + rid);
    // cleat-186：先 build（GenerateBootImage 可能重编镜像）再编随附库——反过来的话随附库对着旧镜像
    Run("dotnet", ["build", solution, "-c", "Release", "-v", "quiet", "-nologo"]);
    CompileLibs([]);                      // AOT 库随发布，先刷新
    if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
    Run("dotnet", ["publish", Path.Combine(srcDir, "Cleat.Host", "Cleat.Host.csproj"), "-c", "Release", "-r", rid,
                   "--self-contained", "-o", outDir, "-v", "quiet", "-nologo", "-p:BuildBootImage=false", "-nodeReuse:false"]);
    foreach (var f in new[] { "cleat.boot.dll", "cleat.boot.pdb", "cleat.boot.ifc", "cleat.boot.inl", "boot-order.edn", "Cleat.Clojure.Libs.dll", "Cleat.Clojure.Libs.pdb" })
        if (File.Exists(Path.Combine(bootDir, f))) File.Copy(Path.Combine(bootDir, f), Path.Combine(outDir, f));
    // 保留随包镜像与库包的 PDB；宿主自己的 C# PDB 对运行用户脚本没有用处。
    foreach (var f in Directory.GetFiles(outDir, "*.pdb")
                   .Where(f => !Path.GetFileName(f).StartsWith("cleat.", StringComparison.OrdinalIgnoreCase)
                            && !Path.GetFileName(f).StartsWith("Cleat.Clojure.Libs", StringComparison.Ordinal)))
        File.Delete(f);
    // ReadyToRun（cleat-160，designs/29 §3.2，opt-in）：发布目录里的 cleat 程序集预编译成机器码。
    // 自包含发布的引用闭包就在发布目录里（含 System.Private.CoreLib），版本泡因此与发布出的运行时一致。
    // 镜像的原件（src/boot/*.dll）保持纯 IL——确定性比较的对象不动。
    if (r2r) ReadyToRunInDir(outDir, Crossgen2(), [], mibc, partial);
    Console.WriteLine("published " + Path.Combine(outDir, OperatingSystem.IsWindows() ? "cleat.exe" : "cleat"));
}

// dotnet run tools/build.cs -- r2r [-o 目录] [--bin 宿主 bin 目录] [--boot src/boot] [--crossgen2 路径] [--only 名字表] [--in-place]
// 从当前 Release 构建做一个 ReadyToRun 快照：宿主输出 + src/boot 的 Clojure 程序集（复制过来再预编译）。
// 为什么是快照目录而不是就地改 bin：bin 里的 C# 程序集归 MSBuild 管，下一次构建会把它换回 IL，
// 而 R2R 的镜像记着它编译时看到的 Cleat.Core（版本泡）——两者混在一个目录里迟早会出现
// "新 Core + 旧 R2R 镜像" 的组合。快照目录每次从零重做（1.6 s），要么全 IL 要么全 R2R，没有陈旧问题。
// --in-place 是对照 / 临时验证用的：直接在给出的目录里预编译，不复制（发布目录走的是同一条路，见 Publish）。
void ReadyToRun(string[] a)
{
    string outDir = Path.Combine(root, "artifacts", "r2r"), bin = Path.GetDirectoryName(Bin("Cleat.Host", "cleat.dll"))!;
    string boot = bootDir, cg = null; string? mibc = null; bool inPlace = false, partial = false;
    string[] only = [];
    for (int i = 0; i < a.Length; i++)
        switch (a[i])
        {
            case "-o" when i + 1 < a.Length: outDir = Path.GetFullPath(a[++i]); break;
            case "--bin" when i + 1 < a.Length: bin = Path.GetFullPath(a[++i]); break;
            case "--boot" when i + 1 < a.Length: boot = Path.GetFullPath(a[++i]); break;
            case "--crossgen2" when i + 1 < a.Length: cg = a[++i]; break;
            case "--only" when i + 1 < a.Length: only = a[++i].Split(',', StringSplitOptions.RemoveEmptyEntries); break;
            case "--mibc" when i + 1 < a.Length: mibc = Path.GetFullPath(a[++i]); break;
            case "--partial": partial = true; break;
            case "--in-place": inPlace = true; break;
            default: throw new CommandFailed("r2r: unexpected argument " + a[i]);
        }
    if (Path.GetFullPath(outDir) == Path.GetFullPath(boot))
        throw new CommandFailed("r2r: " + boot + " 是确定性比较的对象（纯 IL），不在这里做 R2R");
    if (!Directory.Exists(inPlace ? outDir : bin))
        throw new CommandFailed("r2r: 没有目录 " + (inPlace ? outDir : bin) + "（先 dotnet build -c Release）");
    if (partial && mibc == null)
        throw new CommandFailed("r2r: --partial 需要 --mibc");
    cg ??= Crossgen2();
    if (!inPlace)
    {
        if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
        // 宿主与其依赖（C# 程序集、deps / runtimeconfig）；bin 的子目录（linux-x64、publish 这些别的目标的产物）不跟过来
        CopyInto(bin, outDir);
        CopyInto(Path.GetDirectoryName(Bin("Cleat.Hosting", "Cleat.Hosting.dll"))!, outDir);
        // Clojure 镜像、随附库（含旁挂的 .ifc / .inl / boot-order.edn）与 tools 的包程序集：
        // src/boot 的原件一律不动，快照目录里的是复制过来再预编译的那份
        CopyInto(boot, outDir);
        if (Directory.Exists(Path.Combine(boot, "tools"))) CopyInto(Path.Combine(boot, "tools"), outDir);
    }

    ReadyToRunInDir(outDir, cg, only, mibc, partial);
    Console.WriteLine("r2r: " + outDir);
}

// 目录里现成的 cleat 程序集逐个预编译（快照目录与发布目录共用这条路）。only 非空时只处理点名的程序集。
void ReadyToRunInDir(string dir, string cg, string[] only, string? mibc = null, bool partial = false)
{
    // 只碰 cleat 自己的产物（Cleat.* / cleat* / clojure 的包程序集）：第三方程序集留在 JIT 上，
    // 自包含发布目录里的运行时程序集也不属于这里（它们本来就带框架的 R2R 代码）。
    // only 显式点名时放宽到宿主，供对照实验；publish / pack 走默认 ShouldR2R。
    // partial R2R 连镜像也允许：方法 rooting 由 MIBC 决定，不是整程序集全量预编译。
    var targets = Directory.GetFiles(dir, "*.dll")
        .Where(f => ((only.Length > 0 ? IsR2RCandidate(Path.GetFileName(f))
                                    : partial ? IsCleatAssembly(Path.GetFileName(f)) : ShouldR2R(Path.GetFileName(f))))
                 && !Path.GetFileName(f).EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase))
        .Where(f => only.Length == 0 || only.Contains(Path.GetFileName(f)))
        .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
        .ToList();
    foreach (var missing in only.Where(n => !targets.Any(t => Path.GetFileName(t) == n)))
        throw new CommandFailed("r2r: 目录 " + dir + " 里没有 " + missing);
    if (targets.Count == 0) { Console.WriteLine("r2r: " + dir + " 里没有可预编译的程序集"); return; }

    // 引用：自包含发布目录里已经带着整个运行时（System.Private.CoreLib 在那儿），共享框架目录只在框架相关产物时用
    var refs = new List<string> { Path.Combine(dir, "*.dll") };
    if (!File.Exists(Path.Combine(dir, "System.Private.CoreLib.dll")))
        refs.Add(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "*.dll"));
    if (partial && mibc == null) throw new CommandFailed("r2r: partial R2R 需要 MIBC");
    if (mibc != null && !File.Exists(mibc)) throw new CommandFailed("r2r: 没有 MIBC " + mibc);

    long before = 0, after = 0;
    foreach (var dll in targets)
    {
        long size0 = new FileInfo(dll).Length;
        before += size0;
        var sw = Stopwatch.StartNew();
        var tmp = dll + ".r2r.tmp";
        // partial 的宿主 / Core 只有画像 roots 会被编译；仍要 -O，否则实验读数对应不上。
        var optimization = partial ? new[] { "-O" } : R2ROptimizationFlags(Path.GetFileName(dll));
        var cgArgs = new List<string>(optimization) { "-o", tmp, "--targetos", TargetOs(), "--targetarch", TargetArch() };
        if (mibc != null) cgArgs.AddRange(["-m", mibc]);
        if (partial) cgArgs.Add("--partial");
        foreach (var r in refs) { cgArgs.Add("-r"); cgArgs.Add(r); }
        cgArgs.Add(dll);
        Run(cg, cgArgs.ToArray());
        File.Move(tmp, dll, true);
        after += new FileInfo(dll).Length;
        Console.WriteLine($"  {Path.GetFileName(dll),26} {size0 / 1024,7} KB → {new FileInfo(dll).Length / 1024,7} KB  ({sw.ElapsedMilliseconds} ms)");
    }
    Console.WriteLine($"r2r: {targets.Count} 个程序集，{before / 1024} KB → {after / 1024} KB（IL 仍在文件里，R2R 只是附带机器码）");
}

// cleat 自己的程序集（cleat-160）：C# 侧的 Cleat.Core / Cleat.Compiler / Cleat.Hosting / 宿主 cleat，
// Clojure 侧的 cleat.boot / Cleat.Clojure.Libs / Cleat.Tools*，以及 AOT 出来的 clojure 包程序集。
bool IsCleatAssembly(string name) =>
    name.StartsWith("Cleat.", StringComparison.Ordinal) || name.StartsWith("cleat", StringComparison.Ordinal)
 || name.StartsWith("clojure", StringComparison.OrdinalIgnoreCase);

// cleat-160 的 R2R 候选。cleat.boot 不在其中：56 项同轮配对显示，镜像只要带 R2R 数据（即使
// --compile-no-methods）就会移动后续 JIT 代码布局，fib/deriv 等长跑热点稳定回退。
bool IsR2RCandidate(string name) =>
    IsCleatAssembly(name) && !name.Equals("cleat.boot.dll", StringComparison.OrdinalIgnoreCase);

// 默认产物链（r2r 快照 / publish / pack）只预编译编译器（Cleat.Compiler，cleat-181 评估：无可声明回退）、随附库与 tools 包程序集。
// cleat.boot.dll 保持 IL：56 项同轮配对显示，镜像只要带 R2R 数据（即使 --compile-no-methods）
// 就会移动后续 JIT 代码布局，fib/deriv 等长跑热点稳定回退。
// 宿主 cleat.dll 保持 IL（cleat-160 时连同已删的发射器 cleat-emit.dll）：它们的 R2R 数据会把 fib 的 tier-1 热循环稳定挪到慢档
// （本机 5/5 约 x1.43）。
// Cleat.Core 也保持 IL（--only 可 opt-in）：Core-only 与 compiler-only 各自通过回退敏感项的
// 同快照 on/off 短矩阵（5 轮），但二者组合时 ray 稳定 x1.100（5/5）——R2R 元数据共同挪动 tier-1
// 热循环落址的组合效应尚无机制口径；默认产物宁可少拿启动收益，也不让长跑热循环出现可声明回退。
bool ShouldR2R(string name) =>
    IsR2RCandidate(name)
 && !name.StartsWith("Cleat.Core", StringComparison.Ordinal)
 && !name.StartsWith("Cleat.Hosting", StringComparison.Ordinal)
 && !name.Equals("cleat.dll", StringComparison.OrdinalIgnoreCase);

string[] R2ROptimizationFlags(string name) =>
    name.StartsWith("Cleat.Core", StringComparison.Ordinal) || name.StartsWith("Cleat.Hosting", StringComparison.Ordinal)
 || name.Equals("cleat.dll", StringComparison.OrdinalIgnoreCase)
    ? ["--Od"] : ["-O"];

// 扁平复制目录里的文件（同名覆盖；子目录不跟）
void CopyInto(string from, string to)
{
    Directory.CreateDirectory(to);
    foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
}

// crossgen2 的 RID 后缀与 --targetos/--targetarch（cleat-160）：本机就是目标平台，交叉发布暂不支持（29 §3.2 只量本机）。
string TargetOs() => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "osx" : "linux";
string TargetArch() => System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
{
    System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
    System.Runtime.InteropServices.Architecture.X64 => "x64",
    var a => throw new CommandFailed("r2r: 不支持的架构 " + a),
};

// 取 crossgen2：CLEAT_CROSSGEN2 覆盖 → 全局包目录 → 按当前运行时版本恢复
// Microsoft.NETCore.App.Crossgen2.<RID>（designs/29 §3.2 说本机 NuGet 缓存里暂时没有，走的就是这条恢复路）。
// 版本泡与运行时补丁版本：10.0.12 的 crossgen2 产物在 10.0.7 上实测仍走 R2R（启动时间一样），所以框架相关的产物
// 不必逐补丁重做；自包含发布（Publish 调这里）用的是发布时的运行时。
string Crossgen2()
{
    if (Environment.GetEnvironmentVariable("CLEAT_CROSSGEN2") is string env && env != "") return env;
    string rid = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier.Replace("musl-", "");
    string ver = Environment.Version.Major + "." + Environment.Version.Minor + "." + Environment.Version.Build;
    string tool = Crossgen2Tool(NuGetPackageRoot(), rid, ver);
    if (!File.Exists(tool)) tool = null;
    // 测试 / 用户项目会用隔离的 NUGET_PACKAGES。如果本机默认缓存已经有同版本工具，就不让 pack 在隔离目录里联网重取。
    if (tool == null)
    {
        string fallback = Crossgen2Tool(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages"), rid, ver);
        if (File.Exists(fallback)) tool = fallback;
    }
    if (tool != null) return tool;
    string projDir = Path.Combine(root, "artifacts", "crossgen2");
    Directory.CreateDirectory(projDir);
    File.WriteAllText(Path.Combine(projDir, "crossgen2.csproj"),
        "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>"
      + "<ItemGroup><PackageDownload Include=\"Microsoft.NETCore.App.Crossgen2." + rid + "\" Version=\"[" + ver + "]\" /></ItemGroup></Project>");
    Run("dotnet", ["restore", Path.Combine(projDir, "crossgen2.csproj"), "-v", "quiet", "-nologo"]);
    tool = Crossgen2Tool(NuGetPackageRoot(), rid, ver);
    if (!File.Exists(tool)) throw new CommandFailed("r2r: 恢复了 Microsoft.NETCore.App.Crossgen2." + rid + " " + ver
        + " 却没找到 " + tool + "（可用 --crossgen2 / CLEAT_CROSSGEN2 指定路径）");
    return tool;
}

string Crossgen2Tool(string packageRoot, string rid, string ver) =>
    Path.Combine(packageRoot, "microsoft.netcore.app.crossgen2." + rid, ver, "tools",
                 OperatingSystem.IsWindows() ? "crossgen2.exe" : "crossgen2");

// NuGet 全局包目录（NUGET_PACKAGES 优先，与 dotnet 一致）
string NuGetPackageRoot() =>
    Environment.GetEnvironmentVariable("NUGET_PACKAGES") is string p && p != "" ? p
  : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

// 运行时的 NuGet 包（cleat-067）：Release 构建（镜像随之生成到 src/boot/）、随附库的包程序集，再打包 src/Cleat.Runtime.Package（包 id Cleat.Runtime）。
void Pack(string[] a)
{
    string outDir = Path.Combine(root, "artifacts", "nuget"), version = null;
    bool r2r = false;
    for (int i = 0; i < a.Length; i++)
    {
        switch (a[i])
        {
            case "-o" when i + 1 < a.Length: outDir = Path.GetFullPath(a[++i]); break;
            case "--version" when i + 1 < a.Length: version = a[++i]; break;
            case "--r2r": r2r = true; break;
            default: throw new CommandFailed("pack: unexpected argument " + a[i]);
        }
    }
    BuildCSharp();
    CompileLibs([]);
    CompileToolLibs();
    // cleat-160（opt-in）：--r2r 时包里的 cleat 程序集也预编译。用快照目录做 crossgen2，src/boot 原件
    // 保持纯 IL；打包完成后按文件名把 nupkg 里的同名 DLL 换成 R2R 版本（IL 仍在，第三方依赖不动）。
    string r2rDir = Path.Combine(root, "artifacts", "pack-r2r");
    if (r2r)
    {
        if (Directory.Exists(r2rDir)) Directory.Delete(r2rDir, true);
        CopyInto(Path.GetDirectoryName(Bin("Cleat.Host", "cleat.dll"))!, r2rDir);
        CopyInto(Path.GetDirectoryName(Bin("Cleat.Hosting", "Cleat.Hosting.dll"))!, r2rDir);
        CopyInto(bootDir, r2rDir);
        if (Directory.Exists(Path.Combine(bootDir, "tools"))) CopyInto(Path.Combine(bootDir, "tools"), r2rDir);
        ReadyToRunInDir(r2rDir, Crossgen2(), []);
    }
    string pkgVersion = version ?? CoreVersion();
    var args = new List<string> { "pack", Path.Combine(srcDir, "Cleat.Runtime.Package", "Cleat.Runtime.Package.csproj"), "-c", "Release", "-o", outDir,
                                  "-v", "quiet", "-nologo", "-p:BuildBootImage=false", "-p:CleatPackBootDir=" + bootDir + Path.DirectorySeparatorChar };
    // PackageVersion, not Version: a global -p:Version reaches every referenced project and changes the assembly versions
    // (Cleat.Core 1.0.1.0 …), and package assemblies compiled against those then require them (cleat-071)
    // (cleat-073) the same default as Cleat.Cli: CoreInfo.Version, not the csproj's <Version>
    args.Add("-p:PackageVersion=" + pkgVersion);
    Run("dotnet", args.ToArray());
    // dotnet tool 包 Cleat.Cli（cleat-071）：宿主，带镜像、驱动库、随附库与 tools.build
    var tool = new List<string> { "pack", Path.Combine(srcDir, "Cleat.Host", "Cleat.Host.csproj"), "-c", "Release", "-o", outDir,
                                  "-v", "quiet", "-nologo", "-p:BuildBootImage=false", "-p:PackAsTool=true", "-p:CleatPackBootDir=" + bootDir + Path.DirectorySeparatorChar,
                                  "-p:PackageVersion=" + (version ?? CoreVersion()) };
    Run("dotnet", tool.ToArray());
    // 只换这两次 pack 刚产出的包：outDir 里允许留旧版本的 nupkg，不能拿当前快照去改它们
    string[] packages =
    [
        Path.Combine(outDir, "Cleat.Runtime." + pkgVersion + ".nupkg"),
        Path.Combine(outDir, "Cleat.Cli." + pkgVersion + ".nupkg"),
    ];
    foreach (var pkg in packages)
    {
        if (!File.Exists(pkg)) throw new CommandFailed("pack: 没找到 " + pkg);
        if (r2r) R2RPackage(pkg, r2rDir);
    }
    Console.WriteLine("packed " + string.Join(" ", packages.Select(Path.GetFileName)));
}

// nupkg 是 zip；把名字匹配的 cleat DLL 的内容替换成同一个 R2R 快照里的文件。目录布局保持原样。
void R2RPackage(string pkg, string r2rDir)
{
    using var zip = ZipFile.Open(pkg, ZipArchiveMode.Update);
    foreach (var entry in zip.Entries.Where(e => ShouldR2R(Path.GetFileName(e.FullName))
                                              && !e.FullName.EndsWith("/" , StringComparison.Ordinal)
                                              && File.Exists(Path.Combine(r2rDir, Path.GetFileName(e.FullName)))).ToList())
    {
        string name = entry.FullName, source = Path.Combine(r2rDir, Path.GetFileName(name));
        entry.Delete();
        zip.CreateEntryFromFile(source, name, CompressionLevel.Optimal);
    }
}

// CoreInfo.Version（Cleat.Cli 的缺省版本，与 Cleat.Runtime 的相同）
string CoreVersion() =>
    System.Text.RegularExpressions.Regex.Match(File.ReadAllText(Path.Combine(srcDir, "Cleat.Core", "Runtime", "CoreInfo.cs")),
        "Version = \"([^\"]+)\"").Groups[1].Value;

// 随附库（boot-order.edn 里 :boot false 且没有 :loaded-by 的命名空间，或命令行点名的）中能从源码加载的：
// 先在一个宿主进程里逐个试 require，加载不了的（clojure.repl.deps 需要未内置的 clojure/clr/basis）跳过
string[] LoadableLibNamespaces(string[] given)
{
    string[] nses = given;
    if (nses.Length == 0)
    {
        var order = File.ReadAllText(Path.Combine(srcBootDir, "boot-order.edn"));
        nses = System.Text.RegularExpressions.Regex.Matches(order, @"\{:ns ([^\s]+)\s+:path [^}]*:boot false\}")
            .Select(m => (ns: m.Groups[1].Value.Trim(), text: m.Value))
            .Where(x => !x.text.Contains(":loaded-by"))
            .Select(x => x.ns)
            .ToArray();
    }
    if (nses.Length == 0) throw new CommandFailed("compile-libs: no namespaces found in boot-order.edn");
    string host = Bin("Cleat.Host", "cleat.dll"), clj = Path.Combine(srcDir, "clojure");
    string probe = "(doseq [n '[" + string.Join(" ", nses) + "]] (println (try (require n) (str \"ok \" n) (catch Exception e (str \"failed \" n \": \" (.Message e))))))";
    var psi = new ProcessStartInfo("dotnet") { WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true };
    // 试加载看的是源码能不能加载：固定在 src/boot 的镜像上（CLEAT_BOOT 指向的目录这时可能正在重编，cleat-177）
    foreach (var x in new[] { host, "--boot", Path.Combine(srcBootDir, "cleat.boot.dll"), "-I", clj, "-e", probe }) psi.ArgumentList.Add(x);
    using (var p = Process.Start(psi) ?? throw new CommandFailed("cannot start dotnet"))
    {
        var lines = p.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        p.WaitForExit();
        foreach (var l in lines.Where(l => l.StartsWith("failed ")))
            Console.Error.WriteLine($"compile-libs: warning: {l.Substring(7)}; skipped");
        nses = lines.Where(l => l.StartsWith("ok ")).Select(l => l.Substring(3).Trim()).ToArray();
    }
    if (nses.Length == 0) throw new CommandFailed("compile-libs: none of the namespaces loads from source");
    return nses;
}

// 一整套产物编到独立目录（cleat-177 起，原名 cs-image；designs/30 阶段 B）。起步镜像用 src/boot 的：编译器的输出与起步镜像无关
// （只要起步镜像是完整的），所以这里不需要自举。--twice 是 G5（确定性），--ilverify 是 G3。
void ImageSet(string[] a)
{
    bool twice = a.Contains("--twice"), verify = a.Contains("--ilverify");
    var positional = a.Where(x => !x.StartsWith("--")).ToArray();
    // 缺省 src/boot-cs/：与 src/boot/ 同一层，宿主按"开发布局"对待（../clojure 上加载路径、tools 从 packages/ 的源码），测试的环境与 src/boot 一致
    string outDir = Path.GetFullPath(positional.Length > 0 ? positional[0] : Path.Combine(srcDir, "boot-cs"));
    var times = BuildCsImage(outDir);
    foreach (var (step, ms) in times) Console.WriteLine($"image-set: {step,-22} {ms,7} ms");
    Console.WriteLine($"image-set: {"total",-22} {times.Sum(t => t.Ms),7} ms -> {outDir}");
    if (twice)
    {
        string again = outDir + ".2";
        BuildCsImage(again);
        var files = Directory.GetFiles(outDir, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(outDir, f)).OrderBy(f => f, StringComparer.Ordinal).ToList();
        var files2 = Directory.GetFiles(again, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(again, f)).OrderBy(f => f, StringComparer.Ordinal).ToList();
        if (!files.SequenceEqual(files2)) throw new CommandFailed($"image-set --twice: the two builds have different files");
        var differ = files.Where(f => !File.ReadAllBytes(Path.Combine(outDir, f)).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(again, f)))).ToList();
        if (differ.Count > 0) throw new CommandFailed("image-set --twice: differ: " + string.Join(", ", differ));
        Console.WriteLine($"image-set --twice: {files.Count} files identical ({again})");
    }
    if (verify)
    {
        string boot = Path.Combine(outDir, "cleat.boot.dll");
        foreach (var dll in Directory.GetFiles(outDir, "*.dll", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
            ILVerify(dll, boot);
        Console.WriteLine("image-set --ilverify: all assemblies verified");
    }
}

List<(string Step, long Ms)> BuildCsImage(string outDir)
{
    if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
    string tools = Path.Combine(outDir, "tools"), packages = Path.Combine(outDir, "packages");
    Directory.CreateDirectory(tools);
    Directory.CreateDirectory(packages);
    string host = Bin("Cleat.Host", "cleat.dll"), clj = Path.Combine(srcDir, "clojure"), boot = Path.Combine(outDir, "cleat.boot.dll");
    var times = new List<(string, long)>();
    void Step(string name, string[] args)
    {
        var sw = Stopwatch.StartNew();
        Run("dotnet", [host, .. args], root);
        times.Add((name, sw.ElapsedMilliseconds));
    }
    string[] Nses(string src) => Directory.GetFiles(src, "*.clj*", SearchOption.AllDirectories)
        .Select(f => Path.ChangeExtension(Path.GetRelativePath(src, f), null).Replace(Path.DirectorySeparatorChar, '.').Replace('_', '-'))
        .OrderBy(n => n, StringComparer.Ordinal).ToArray();
    string Src(string package) => Path.Combine(root, "packages", package, "src");

    Step("image", ["--boot", Path.Combine(srcBootDir, "cleat.boot.dll"), "-I", clj, "--compile-boot", boot, "--boot-order", Path.Combine(srcBootDir, "boot-order.edn")]);
    Step("libs", ["--boot", boot, "-I", clj, "--compile-package", Path.Combine(outDir, "Cleat.Clojure.Libs.dll"), "--package", "clojure",
                  "--root", clj, .. LoadableLibNamespaces([])]);
    foreach (var (package, assembly, name) in new[] { ("tools.namespace", "Cleat.ToolsNamespace", "tools.namespace"), ("tools.build", "Cleat.ToolsBuild", "tools.build") })
        Step(name, ["--boot", boot, "-I", tools, "-I", Src(package), "--compile-package", Path.Combine(tools, assembly + ".dll"),
                    "--package", name, "--root", Src(package), .. Nses(Src(package))]);
    foreach (var (package, assembly, deps) in new[] { ("nrepl", "Cleat.Nrepl", new string[0]), ("neorepl", "Cleat.Neorepl", new[] { "nrepl" }),
                                                      ("nanopass", "Cleat.Nanopass", new string[0]),
                                                      ("test.check", "Cleat.TestCheck", new string[0]), ("spec.alpha", "Cleat.SpecAlpha", new string[0]),
                                                      ("tools.reader", "Cleat.ToolsReader", new string[0]), ("tools.analyzer", "Cleat.ToolsAnalyzer", new string[0]),
                                                      ("core.async", "Cleat.CoreAsync", new string[0]) })
        Step(package, ["--boot", boot, "-I", packages, .. deps.SelectMany(d => new[] { "-I", Src(d) }), "-I", Src(package),
                       "--compile-package", Path.Combine(packages, assembly + ".dll"), "--package", package, "--root", Src(package), .. Nses(Src(package))]);
    return times;
}

// tools.build 与 tools.namespace 编成包程序集放进 src/boot/tools/（cleat-071）：Cleat.Cli 随附，宿主的 -M / -T 用它们
void CompileToolLibs()
{
    string host = Bin("Cleat.Host", "cleat.dll"), outDir = Path.Combine(bootDir, "tools");
    if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
    Directory.CreateDirectory(outDir);
    foreach (var (package, assembly, name) in new[] { ("tools.namespace", "Cleat.ToolsNamespace", "tools.namespace"),
                                                      ("tools.build", "Cleat.ToolsBuild", "tools.build") })
    {
        string src = Path.Combine(root, "packages", package, "src");
        var nses = Directory.GetFiles(src, "*.clj*", SearchOption.AllDirectories)
            .Select(f => Path.ChangeExtension(Path.GetRelativePath(src, f), null).Replace(Path.DirectorySeparatorChar, '.').Replace('_', '-'))
            .OrderBy(n => n, StringComparer.Ordinal);
        Run("dotnet", [host, "--boot", Path.Combine(bootDir, "cleat.boot.dll"), "-I", outDir, "-I", src, "--compile-package", Path.Combine(outDir, assembly + ".dll"),
                       "--package", name, "--root", src, .. nses], root);
    }
    Console.WriteLine("compiled tools.namespace and tools.build -> " + outDir);
}

// 基准（cleat-042）：bench/ 下每个 *.clj 一次宿主进程（-m bench.<名字>），解析 BENCH 行，聚合最小/中位。
// --write 与 bench/baseline.edn 比较（有基线时同时打印相对值），或写入基线。
void Bench(string[] a)
{
    int runs = 3;
    string only = null;
    bool write = a.Contains("--write");
    for (int i = 0; i < a.Length; i++)
    {
        if (a[i] == "-r" && i + 1 < a.Length) runs = int.Parse(a[++i]);
        else if (a[i] == "--only" && i + 1 < a.Length) only = a[++i];
        else if (a[i] == "--write") { }
        else throw new CommandFailed("bench: unexpected argument " + a[i]);
    }
    string benchDir = Path.Combine(root, "bench");
    var names = Directory.GetFiles(benchDir, "*.clj")
        .Select(f => Path.GetFileNameWithoutExtension(f)).OrderBy(n => n, StringComparer.Ordinal)
        .Where(n => only == null || System.Text.RegularExpressions.Regex.IsMatch(n, only)).ToList();
    if (names.Count == 0) throw new CommandFailed("bench: no benchmarks in bench/");
    var results = new Dictionary<string, List<long>>();
    foreach (var name in names)
    {
        results[name] = [];
        for (int r = 0; r < runs; r++)
        {
            var psi = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = false,
                UseShellExecute = false, WorkingDirectory = root,
            };
            psi.ArgumentList.Add(Bin("Cleat.Host", "cleat.dll"));
            psi.ArgumentList.Add("-I");
            psi.ArgumentList.Add(benchDir);
            psi.ArgumentList.Add("-m");
            psi.ArgumentList.Add("bench." + name.Replace('_', '-'));   // 文件名下划线 ↔ 命名空间连字符
            using var p = Process.Start(psi) ?? throw new CommandFailed("cannot start host");
            string output = p.StandardOutput.ReadToEnd(), err = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0) throw new CommandFailed($"bench {name} failed:\n{err}");
            foreach (var line in output.Split('\n'))
                if (line.StartsWith("BENCH ", StringComparison.Ordinal))
                {
                    var parts = line.Split(' ');
                    if (parts.Length >= 3 && long.TryParse(parts[2], out long ms)) results[name].Add(ms);
                }
        }
    }
    var baseline = new Dictionary<string, long>();
    string basePath = Path.Combine(benchDir, "baseline.edn");
    if (File.Exists(basePath))
        foreach (var line in File.ReadAllLines(basePath))
        {
            var m = System.Text.RegularExpressions.Regex.Match(line, @"{:name (\S+) :min (\d+) :median (\d+)}");
            if (m.Success) baseline[m.Groups[1].Value] = long.Parse(m.Groups[2].Value);
        }
    bool failed = false;
    foreach (var name in names)
    {
        var xs = results[name];
        if (xs.Count == 0) { Console.WriteLine($"{name,-16} no samples"); failed = true; continue; }
        xs.Sort();
        long min = xs[0], median = xs[xs.Count / 2];
        string rel = baseline.TryGetValue(name, out var b) ? $"  (基线 min {b}, {100.0 * min / b:F0}%)" : "";
        Console.WriteLine($"{name,-16} min {min,6} ms  median {median,6} ms{rel}");
    }
    if (write)
    {
        var lines = names.Where(n => results[n].Count > 0)
            .Select(n => $"{{:name {n} :min {results[n][0]} :median {results[n][results[n].Count / 2]}}}");
        File.WriteAllLines(basePath, [";; 基准基线（tools/build.cs bench --write 生成；优化对照用）。M10 起点：2026-10-15，cleat-100", .. lines]);
        Console.WriteLine("wrote " + basePath);
    }
    else if (baseline.Count > 0 && failed) throw new CommandFailed("bench: some benchmarks produced no samples");
}

void CopyDir(string from, string to)
{
    Directory.CreateDirectory(to);
    foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
}

// 语料（tests/conformance/corpus.edn，designs/09）。现在只做解析与统计（cleat-002）；对宿主求值在 cleat-018 / cleat-032 接入
void Conformance(string[] a)
{
    string dir = Path.Combine(root, "tests", "conformance");
    var rows = Edn.ReadRows(File.ReadAllText(Path.Combine(dir, "corpus.edn")));
    if (!a.Contains("--run"))
    {
        Console.WriteLine($"rows: {rows.Count}");
        foreach (var g in rows.GroupBy(r => r.GetValueOrDefault(":portability", "?")).OrderBy(g => g.Key))
            Console.WriteLine($"  {g.Key}: {g.Count()}");
        Console.WriteLine($"suites: {rows.Select(r => r[":suite"]).Distinct().Count()}");
        Console.WriteLine($"throws: {rows.Count(r => r.GetValueOrDefault(":expected") == ":throws")}");
        var ids = rows.Select(r => (r[":suite"], r[":label"])).ToHashSet();
        var dup = rows.Count - ids.Count;
        if (dup != 0) Console.WriteLine($"warning: corpus has {dup} duplicate [suite label] ids (jolt keeps them; cleat keys by row index too)");
        var div = Edn.ReadRows(File.ReadAllText(Path.Combine(dir, "known-divergences.edn")))
                     .Where(r => r.ContainsKey(":suite") && r.ContainsKey(":label")).ToList();
        var stale = div.Where(r => !ids.Contains((r[":suite"], r[":label"]))).ToList();
        Console.WriteLine($"known-divergences (jolt): {div.Count} entries, {stale.Count} not in corpus");
        return;
    }
    // --run：宿主运行 tools/conformance.clj（cleat-053 基线；修正失败是 cleat-032）
    var args = new List<string> { Bin("Cleat.Host", "cleat.dll"), Path.Combine(root, "tools", "conformance.clj"), Path.Combine(dir, "corpus.edn") };
    args.AddRange(a.Where(x => x != "--run"));
    Run("dotnet", args.ToArray(), root);
}

// 只编译 C# 项目（不经 MSBuild 生成 boot 镜像）
void BuildCSharp() => Run("dotnet", ["build", solution, "-c", "Release", "-p:BuildBootImage=false", "-v", "quiet", "-nologo"]);

// ILVerify（dotnet tool install -g dotnet-ilverify）：全局工具目录为用户目录下的 .dotnet/tools，可用环境变量 ILVERIFY 指定
void ILVerify(string dll, string? boot = null)
{
    string tool = Environment.GetEnvironmentVariable("ILVERIFY")
      ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools",
                      OperatingSystem.IsWindows() ? "ilverify.exe" : "ilverify");
    if (!File.Exists(tool)) throw new CommandFailed($"ilverify not found at {tool}: dotnet tool install -g dotnet-ilverify (or set ILVERIFY)");
    // 本程序运行在 .NET 10 上，其运行时目录就是要引用的框架程序集
    string runtimeDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
    // Clojure 产物直接链接 cleat.boot.dll 的已知函数（cleat-033）：校验它们时也引用 Clojure 镜像
    string clojureBoot = boot ?? Path.Combine(bootDir, "cleat.boot.dll");
    string[] clojureRef = Path.GetFileName(dll) == "cleat.boot.dll" || !File.Exists(clojureBoot) ? [] : ["-r", clojureBoot];
    // image-set 的 tools/、packages/ 下的包还引用镜像目录里的随附库（cleat-177）
    string bootDirOfDll = Path.GetDirectoryName(clojureBoot)!;
    if (boot != null && !string.Equals(Path.GetFullPath(bootDirOfDll), Path.GetFullPath(Path.GetDirectoryName(dll)!), StringComparison.Ordinal))
        clojureRef = [.. clojureRef, "-r", Path.Combine(bootDirOfDll, "Cleat.Clojure.Libs.dll")];
    Run(tool, [dll,
             "-r", Path.Combine(runtimeDir, "*.dll"),
             "-r", Bin("Cleat.Core", "Cleat.Core.dll"),
             "-r", Bin("Cleat.Host", "cleat.dll"),   // tools.build 的 uber 读 Cleat.Host.Program/ClojureBootPath
             "-r", Path.Combine(Path.GetDirectoryName(dll)!, "*.dll"), ..clojureRef]);
}

void Run(string file, string[] arguments, string? workingDirectory = null, (string, string)[]? env = null)
{
    // 输出被重定向时（如在测试中运行）不保留 MSBuild 节点 / Roslyn 编译服务器进程：它们继承输出管道，
    // 子进程退出后管道仍有写入端，调用方会一直等不到 EOF
    if (file == "dotnet" && arguments.Length > 0 && arguments[0] is "build" or "publish" && Console.IsOutputRedirected)
        arguments = [.. arguments, "-nodeReuse:false", "-p:UseSharedCompilation=false"];
    var psi = new ProcessStartInfo(file) { WorkingDirectory = workingDirectory ?? root, UseShellExecute = false, RedirectStandardInput = true };
    foreach (var a in arguments) psi.ArgumentList.Add(a);
    foreach (var (k, v) in env ?? []) psi.Environment[k] = v;
    using var p = Process.Start(psi) ?? throw new CommandFailed($"cannot start {file}");
    p.StandardInput.Close();   // 构建脚本不读输入
    p.WaitForExit();
    if (p.ExitCode != 0) throw new CommandFailed($"{file} {string.Join(' ', arguments)} failed with exit code {p.ExitCode}");
}

sealed class CommandFailed(string message) : Exception(message);

// 够用的 EDN 读取：只认语料用到的子集（顶层向量或 map 里的 `{:k v …}` 行；值为字符串、关键字、符号、数字、嵌套向量 / map / 集合）。
// 只收集带 :suite 的扁平 map，其字符串与关键字值按文本保存。
static class Edn
{
    public static List<Dictionary<string, string>> ReadRows(string text)
    {
        var rows = new List<Dictionary<string, string>>();
        int i = 0;
        object? v;
        while ((v = Read(text, ref i)) is not null) Collect(v, rows);
        return rows;
    }

    static void Collect(object v, List<Dictionary<string, string>> rows)
    {
        switch (v)
        {
            case List<object> list:
                foreach (var e in list) Collect(e, rows);
                break;
            case Dictionary<object, object> map:
                if (map.Keys.Any(k => k is string s && s == ":suite"))
                {
                    var row = new Dictionary<string, string>();
                    foreach (var kv in map)
                        if (kv.Key is string k && kv.Value is string s) row[k] = s;
                    rows.Add(row);
                }
                else foreach (var kv in map) Collect(kv.Value, rows);
                break;
        }
    }

    // 返回：string（字符串字面量的内容）、string（关键字 / 符号 / 数字的文本，关键字以 : 开头）、List<object>、Dictionary<object,object>；文件尾返回 null
    static object? Read(string t, ref int i)
    {
        Skip(t, ref i);
        if (i >= t.Length) return null;
        char c = t[i];
        if (c == '"') return ReadString(t, ref i);
        if (c == '[' || c == '(') { i++; return ReadSeq(t, ref i, c == '[' ? ']' : ')'); }
        if (c == '{') { i++; return ReadMap(t, ref i); }
        if (c == '#' && i + 1 < t.Length && t[i + 1] == '{') { i += 2; return ReadSeq(t, ref i, '}'); }
        if (c == '#' && i + 1 < t.Length && t[i + 1] == '_') { i += 2; Read(t, ref i); return Read(t, ref i); }
        if (c == '#' || c == '\'' || c == '`' || c == '~' || c == '@' || c == '^') { i++; return Read(t, ref i); }
        if (c == ']' || c == ')' || c == '}') throw new CommandFailed($"edn: unexpected '{c}' at {i}");
        int start = i;
        while (i < t.Length && !char.IsWhiteSpace(t[i]) && t[i] != ',' && "[](){}\"".IndexOf(t[i]) < 0) i++;
        if (c == '\\' && i == start) i++;
        return t[start..i];
    }

    static List<object> ReadSeq(string t, ref int i, char close)
    {
        var list = new List<object>();
        while (true)
        {
            Skip(t, ref i);
            if (i >= t.Length) throw new CommandFailed("edn: unterminated sequence");
            if (t[i] == close) { i++; return list; }
            list.Add(Read(t, ref i) ?? throw new CommandFailed("edn: eof"));
        }
    }

    static Dictionary<object, object> ReadMap(string t, ref int i)
    {
        var map = new Dictionary<object, object>();
        while (true)
        {
            Skip(t, ref i);
            if (i >= t.Length) throw new CommandFailed("edn: unterminated map");
            if (t[i] == '}') { i++; return map; }
            var k = Read(t, ref i) ?? throw new CommandFailed("edn: eof");
            var v = Read(t, ref i) ?? throw new CommandFailed("edn: eof");
            map[k] = v;
        }
    }

    static string ReadString(string t, ref int i)
    {
        var sb = new System.Text.StringBuilder();
        i++;
        while (i < t.Length && t[i] != '"')
        {
            if (t[i] == '\\') { i++; sb.Append(t[i] switch { 'n' => '\n', 't' => '\t', 'r' => '\r', var e => e }); }
            else sb.Append(t[i]);
            i++;
        }
        i++;
        return sb.ToString();
    }

    static void Skip(string t, ref int i)
    {
        while (i < t.Length)
        {
            if (char.IsWhiteSpace(t[i]) || t[i] == ',') i++;
            else if (t[i] == ';') { while (i < t.Length && t[i] != '\n') i++; }
            else break;
        }
    }
}
