#!/usr/bin/env -S dotnet run
#:property ImplicitUsings=enable
#:property Nullable=enable
// Cleat 的维护命令（.NET 10 单文件程序）。日常构建与测试不需要它：仓库根目录 dotnet build / dotnet test 即可。
//
//   dotnet run tools/build.cs -- <命令> [参数]
//
// 命令：
//   stage1 [输出目录]     编译 C# 项目后，用 stage0 从源码加载 boot 命名空间并运行 src/boot/build-boot.clj，
//                         把 cleat.boot.lcil 与 cleat.boot.dll 输出到指定目录（默认 src/boot/）       [cleat-029]
//   seed [--stage0]       重新生成 Lcore 种子 src/boot/boot-seed.edn（designs/08 §5）                  [cleat-036]
//   ilverify <dll> …      用 ILVerify 校验发射器生成的程序集（dotnet tool install -g dotnet-ilverify）  [cleat-020]
//   conformance [选项]    运行 tests/conformance 的语料（designs/09）                                   [cleat-002]
//   compile-libs          预编译随附命名空间为 .clj.dll                                                [cleat-033]
using System.Diagnostics;

string root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName((string)AppContext.GetData("EntryPointFilePath")!)!, ".."));
string srcDir = Path.Combine(root, "src");
string solution = Path.Combine(root, "Cleat.slnx");
string Bin(string project, string dll) => Path.Combine(srcDir, project, "bin", "Release", "net10.0", dll);

try
{
    string command = args.Length > 0 ? args[0] : "help";
    string[] rest = args.Skip(1).ToArray();
    switch (command)
    {
        case "stage1":
            BuildCSharp();
            NotYet("stage1", "cleat-029");
            break;
        case "seed":
            NotYet("seed", "cleat-036");
            break;
        case "ilverify":
            if (rest.Length == 0) return Usage();
            foreach (var dll in rest) ILVerify(Path.GetFullPath(dll));
            break;
        case "conformance":
            Conformance(rest);
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
    Console.Error.WriteLine("usage: dotnet run tools/build.cs -- stage1 [outdir] | seed [--stage0] | ilverify <dll>... | conformance | compile-libs");
    Console.Error.WriteLine("(to build and test: dotnet build / dotnet test in the repository root)");
    return 2;
}

// 语料（tests/conformance/corpus.edn，designs/09）。现在只做解析与统计（cleat-002）；对宿主求值在 cleat-018 / cleat-032 接入
void Conformance(string[] a)
{
    string dir = Path.Combine(root, "tests", "conformance");
    var rows = Edn.ReadRows(File.ReadAllText(Path.Combine(dir, "corpus.edn")));
    if (a.Contains("--stats") || a.Length == 0)
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
    throw new CommandFailed("conformance: evaluation on a host is not implemented yet (cleat-018)");
}

void NotYet(string command, string task) => throw new CommandFailed($"{command}: not implemented yet (task {task})");

void BuildCSharp() => Run("dotnet", ["build", solution, "-c", "Release", "-p:BuildBootImage=false", "-v", "quiet", "-nologo"]);

void ILVerify(string dll)
{
    string coreDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
    Run("ilverify", [dll, "-r", Path.Combine(coreDir, "*.dll"), "-r", Path.Combine(Path.GetDirectoryName(dll)!, "*.dll")]);
}

void Run(string file, string[] arguments, string? workingDirectory = null)
{
    var psi = new ProcessStartInfo(file) { WorkingDirectory = workingDirectory ?? root, UseShellExecute = false };
    foreach (var a in arguments) psi.ArgumentList.Add(a);
    using var p = Process.Start(psi) ?? throw new CommandFailed($"cannot start {file}");
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
