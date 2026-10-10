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
            NotYet("conformance", "cleat-002");
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

void NotYet(string command, string task) => throw new CommandFailed($"{command}: not implemented yet (task {task})");

void BuildCSharp() => Run("dotnet", ["build", solution, "-c", "Release", "-p:BuildBootImage=false", "-v", "quiet", "-nologo"]);

void ILVerify(string dll)
{
    string coreDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
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
