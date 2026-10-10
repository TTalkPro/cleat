using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using NUnit.Framework;

namespace Cleat.Scheme.Tests;

/// <summary>以子进程运行 stage0 / 构建脚本的测试共用的路径与进程辅助（从 IronScheme.Tests 的 TestRunner 纳入，cleat-046）。</summary>
internal static class SchemePaths
{
    private static string TestDir => TestContext.CurrentContext.TestDirectory;
    public static string Configuration => new DirectoryInfo(TestDir).Parent.Name;
    public static string Framework => new DirectoryInfo(TestDir).Name;
    /// <summary>仓库根目录（测试目录为 tests/项目/bin/配置/框架）。</summary>
    public static string RepoDir => Path.GetFullPath(Path.Combine(TestDir, "..", "..", "..", "..", ".."));
    public static string SrcDir => Path.Combine(RepoDir, "src");
    /// <summary>运行 Scheme 脚本的工作目录 src/scheme（boot/、lib/、nanopass/ 所在处）。</summary>
    public static string SchemeDir => Path.Combine(SrcDir, "scheme");
    public static string Stage0 => Path.Combine(SrcDir, "Cleat.Stage0", "bin", Configuration, Framework, "stage0.dll");
    /// <summary>工作区的 Scheme boot 镜像（dotnet build 的 GenerateSchemeBootImage 生成）；可用环境变量 CLEAT_SCHEME_BOOT 覆盖。</summary>
    public static string Boot => Environment.GetEnvironmentVariable("CLEAT_SCHEME_BOOT") ?? Path.Combine(SrcDir, "boot", "cleat.scheme.boot.dll");
    /// <summary>stage0 的库搜索路径：lib（编译器、(ironscheme clr)、nanopass 适配库）与 nanopass（框架）。</summary>
    public static readonly string[] IncludeArgs = { "-I", "lib", "-I", "nanopass" };

    internal sealed class ProcessResult
    {
        public string Output;
        public string Error;
        public int ExitCode;
        public TimeSpan Elapsed;
        public override string ToString() => $"exit {ExitCode} in {Elapsed.TotalSeconds:F1} s\nOutput:\n{Output}\nError:\n{Error}";
    }

    /// <summary>运行子进程（标准输入关闭），超时默认 30 分钟；checkExitCode 时退出码非 0 即失败。</summary>
    internal static ProcessResult Run(string exe, string[] args, string workingDirectory, int timeoutMs = 1800000, bool checkExitCode = true)
    {
        var error = new StringBuilder();
        var output = new StringBuilder();
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            StandardOutputEncoding = Encoding.UTF8,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = new Process { StartInfo = psi };
        p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (error) error.AppendLine(e.Data); };
        p.OutputDataReceived += (s, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        var sw = Stopwatch.StartNew();
        Assert.That(p.Start(), Is.True, $"could not start {exe}");
        p.BeginErrorReadLine();
        p.BeginOutputReadLine();
        p.StandardInput.Close();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(true); } catch (Exception) { }
            Assert.Fail($"{exe} {string.Join(" ", args)} timed out");
        }
        p.WaitForExit();
        var r = new ProcessResult
        {
            Output = output.ToString().TrimEnd('\r', '\n'),
            Error = error.ToString().TrimEnd('\r', '\n'),
            ExitCode = p.ExitCode,
            Elapsed = sw.Elapsed,
        };
        if (checkExitCode) Assert.That(p.ExitCode, Is.EqualTo(0), r.ToString());
        return r;
    }
}
