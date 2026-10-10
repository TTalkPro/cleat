#!/usr/bin/env -S dotnet run
#:property ImplicitUsings=enable
#:property Nullable=enable
// Cleat 的维护命令（.NET 10 单文件程序）。日常构建与测试不需要它：仓库根目录 dotnet build / dotnet test 即可。
//
//   dotnet run tools/build.cs -- <命令> [参数]
//
// 命令：
//   scheme-stage1 [输出目录]
//                         编译 C# 项目后，stage0 执行文本种子 src/scheme/boot/boot-seed.core、运行
//                         boot/ironscheme-buildscript-nano.sps，把 cleat.scheme.boot.{lcil,dll,ifc} 输出到指定目录
//                         （默认 src/boot/，此时等同 dotnet build 的 GenerateSchemeBootImage，但总是重新生成）  [cleat-046]
//   seed [--stage0]       重新生成 Scheme 文本种子 src/scheme/boot/boot-seed.core（IronScheme 05 §5）：默认在新系统上运行
//                         （stage0 --boot src/boot/cleat.scheme.boot.dll），--stage0 由 stage0 执行现有种子来运行；产物相同 [cleat-046]
//   stage1 [输出目录] [--boot 镜像]
//                         编译 C# 项目后，宿主按 src/boot/boot-order.edn 从 src/clojure 源码编译 Clojure 镜像 cleat.boot.{dll,lcil,ifc}
//                         （默认 src/boot/，等同 dotnet build 的 GenerateBootImage 但总是重新生成；--boot 时在该镜像上编译，
//                         即不动点的第二段）                                                                         [cleat-053]
//   ilverify <dll> …      用 ILVerify 校验发射器生成的程序集（dotnet tool install -g dotnet-ilverify）  [cleat-020]
//   conformance [--stats] 语料统计（cleat-002）
//   conformance --run [--portability common|jvm|all] [--timeout ms] [--report out.edn] [--limit n]
//                         在宿主（Clojure 镜像）上运行语料，按 = 比较，报告通过数与失败分组（tools/conformance.clj）  [cleat-053]
//                         [--results out.edn] 每行结果；[--gate] 按 tests/conformance/cleat-divergences.edn 门禁       [cleat-032]
//   cts cts|test-clojure [--write] [--only 正则]
//                         逐命名空间跑 clojure-test-suite / test_clojure，与 tests/cts-baseline.edn /
//                         tests/test-clojure-baseline.edn 比较（--write 重写基线）（tools/cts.clj）                 [cleat-032]
//   compile-libs          预编译随附命名空间为 .clj.dll                                                [cleat-033]
using System.Diagnostics;

string root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName((string)AppContext.GetData("EntryPointFilePath")!)!, ".."));
string srcDir = Path.Combine(root, "src");
string solution = Path.Combine(root, "Cleat.slnx");
string Bin(string project, string dll) => Path.Combine(srcDir, project, "bin", "Release", "net10.0", dll);
string schemeDir = Path.Combine(srcDir, "scheme");                       // Scheme 元层源码（boot/、lib/、nanopass/），脚本的工作目录
string bootDir = Path.Combine(srcDir, "boot");                           // 镜像输出目录
string schemeBoot = Path.Combine(bootDir, "cleat.scheme.boot.dll");
string stage0 = Bin("Cleat.Stage0", "stage0.dll");

try
{
    string command = args.Length > 0 ? args[0] : "help";
    string[] rest = args.Skip(1).ToArray();
    switch (command)
    {
        case "scheme-stage1":
            BuildCSharp();
            SchemeStage1(rest);
            break;
        case "seed":
            Seed(rest.Contains("--stage0"));
            break;
        case "stage1":
            BuildCSharp();
            Stage1(rest);
            break;
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
            NotYet("compile-libs", "cleat-033");
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
    Console.Error.WriteLine("usage: dotnet run tools/build.cs -- scheme-stage1 [outdir] | seed [--stage0] | stage1 [outdir] | ilverify <dll>... | conformance [--run] | cts cts|test-clojure | compile-libs");
    Console.Error.WriteLine("(to build and test: dotnet build / dotnet test in the repository root)");
    return 2;
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

// Clojure 镜像（cleat-053）：宿主 --compile-boot，见 Cleat.Host.csproj 的 GenerateBootImage
void Stage1(string[] a)
{
    int bi = Array.IndexOf(a, "--boot");
    string? withBoot = bi >= 0 && bi + 1 < a.Length ? Path.GetFullPath(a[bi + 1]) : null;
    var positional = a.Where((x, i) => x != "--boot" && (bi < 0 || i != bi + 1)).ToArray();
    string outDir = positional.Length > 0 ? Path.GetFullPath(positional[0]) : bootDir;
    Directory.CreateDirectory(outDir);
    var args = new List<string> { Bin("Cleat.Host", "cleat.dll") };
    args.AddRange(withBoot != null ? new[] { "--boot", withBoot } : new[] { "--no-boot" });
    args.AddRange(new[] { "-I", Path.Combine(srcDir, "clojure"), "--compile-boot", Path.Combine(outDir, "cleat.boot.dll"),
                          "--boot-order", Path.Combine(bootDir, "boot-order.edn") });
    Run("dotnet", args.ToArray(), root);
}

void NotYet(string command, string task) => throw new CommandFailed($"{command}: not implemented yet (task {task})");

// 只编译 C# 项目（不经 MSBuild 生成 boot 镜像）
void BuildCSharp() => Run("dotnet", ["build", solution, "-c", "Release", "-p:BuildBootImage=false", "-v", "quiet", "-nologo"]);

// stage0 执行种子 → 运行 nano 构建脚本 → stage1 Scheme boot 镜像（IronScheme 05 §3 Phase A）。在 src/scheme 目录下运行：
// 库搜索路径 lib（编译器、(ironscheme clr)、nanopass 的适配库）与 nanopass（框架本身）
void SchemeStage1(string[] a)
{
    string outDir = a.Length > 0 ? Path.GetFullPath(a[0]) : bootDir;
    Run("dotnet", [stage0, "-I", "lib", "-I", "nanopass", "boot/boot-seed.core", "boot/ironscheme-buildscript-nano.sps", outDir], schemeDir);
}

// 文本种子再生成（IronScheme 05 §5）：seed-buildscript.sps 只展开 boot 源码、不编译，写回 boot/boot-seed.core
void Seed(bool onStage0)
{
    BuildCSharp();
    if (onStage0)
    {
        // 脚本会覆盖 boot-seed.core，stage0 执行的是它的副本
        string tmp = Directory.CreateTempSubdirectory("cleat-seed").FullName;
        try
        {
            string copy = Path.Combine(tmp, "boot-seed.core");
            File.Copy(Path.Combine(schemeDir, "boot", "boot-seed.core"), copy);
            Run("dotnet", [stage0, "-I", "lib", copy, "boot/seed-buildscript.sps", "./"], schemeDir);
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }
    else
    {
        if (!File.Exists(schemeBoot)) throw new CommandFailed($"{schemeBoot} not found: run dotnet build first, or use 'seed --stage0'");
        Run("dotnet", [stage0, "-I", "lib", "--boot", schemeBoot, "boot/seed-buildscript.sps", "./"], schemeDir);
    }
}

// ILVerify（dotnet tool install -g dotnet-ilverify）：全局工具目录为用户目录下的 .dotnet/tools，可用环境变量 ILVERIFY 指定
void ILVerify(string dll)
{
    string tool = Environment.GetEnvironmentVariable("ILVERIFY")
      ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools",
                      OperatingSystem.IsWindows() ? "ilverify.exe" : "ilverify");
    if (!File.Exists(tool)) throw new CommandFailed($"ilverify not found at {tool}: dotnet tool install -g dotnet-ilverify (or set ILVERIFY)");
    // 本程序运行在 .NET 10 上，其运行时目录就是要引用的框架程序集
    string runtimeDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
    // 编译产物可能直接调用 Scheme boot 镜像中的过程（IronScheme iron-034e）：校验它们时引用镜像；校验镜像自己时不重复
    string[] bootRef = Path.GetFileName(dll) == Path.GetFileName(schemeBoot) || !File.Exists(schemeBoot) ? [] : ["-r", schemeBoot];
    Run(tool, [dll,
             "-r", Path.Combine(runtimeDir, "*.dll"),
             "-r", Bin("Cleat.Core", "Cleat.Core.dll"),
             "-r", Bin("Cleat.Scheme", "Cleat.Scheme.dll"),
             "-r", Bin("Cleat.Emit", "cleat-emit.dll"),
             "-r", Path.Combine(Path.GetDirectoryName(dll)!, "*.dll"), ..bootRef]);
}

void Run(string file, string[] arguments, string? workingDirectory = null)
{
    // 输出被重定向时（如在测试中运行）不保留 MSBuild 节点进程：它们继承输出管道，调用方会一直等不到输出结束
    if (file == "dotnet" && arguments.Length > 0 && arguments[0] is "build" or "publish" && Console.IsOutputRedirected)
        arguments = [.. arguments, "-nodeReuse:false"];
    var psi = new ProcessStartInfo(file) { WorkingDirectory = workingDirectory ?? root, UseShellExecute = false, RedirectStandardInput = true };
    foreach (var a in arguments) psi.ArgumentList.Add(a);
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
