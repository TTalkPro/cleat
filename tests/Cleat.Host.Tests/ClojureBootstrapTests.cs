using System;
using System.Diagnostics;
using System.IO;
using NUnit.Framework;

namespace Cleat.Host.Tests;

/// <summary>
/// Clojure 编译器的自举不动点（cleat-091，designs/08 §0、19 D7 / G4）：tools/build.cs selfhost 从 src/boot 的镜像与编译器包（stage1）
/// 出发自举两级，stage2 与 stage3 的镜像与编译器包逐字节相同（改名的工具编译器在上一级镜像上编出、以这一级镜像为目标）。
/// stage3 能运行冒烟集；ILVerify（装了的话）通过；dotnet build 从种子发射的 stage1 与 src/boot 相同。每级约 70 秒。
/// </summary>
[Category("Bootstrap")]
[NonParallelizable]
public class ClojureBootstrapTests
{
    private static readonly string s_root = Path.Combine(Path.GetTempPath(), "cleat-clojure-bootstrap");
    private static string Stage3 => Path.Combine(s_root, "stage3");
    private static string Dll => Path.Combine(Stage3, "cleat.boot.dll");

    [Test, Order(1)]
    public void SelfhostReachesAFixedPoint()
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            WorkingDirectory = HostProcessTests.RepoDir,
        };
        foreach (var a in new[] { "run", "tools/build.cs", "--", "selfhost", s_root }) psi.ArgumentList.Add(a);
        // 输出重定向后等待 EOF：外层 dotnet run 起的 MSBuild / Roslyn 服务器不能继承管道残留。
        psi.Environment["UseSharedCompilation"] = "false";
        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        var sw = Stopwatch.StartNew();
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("cannot start dotnet");
        string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        TestContext.Out.WriteLine($"selfhost: {sw.Elapsed.TotalSeconds:F0} s\n{output}");
        Assert.That(p.ExitCode, Is.EqualTo(0), output);
        Assert.That(output, Does.Contain("selfhost: fixed point"), output);
        Assert.That(File.Exists(Dll) && File.Exists(Path.Combine(Stage3, "cleat.compiler.dll")), Is.True, output);
    }

    /// <summary>stage3 是能用的镜像：新进程只加载它与它旁边的编译器包（不碰 src/boot）跑冒烟集。</summary>
    [Test, Order(2)]
    public void Stage3Runs()
    {
        if (!File.Exists(Dll)) Assert.Ignore("stage3 not built");
        var r = HostProcessTests.CleatIn(Stage3, null, null, "--boot", Dll, "-e",
            "[(reduce + (range 100)) (str \"a\" 1) (pr-str {:a [1 2]}) (format \"%d\" 3) (sort-by - [1 3 2])]",
            "-e", "(defrecord P [x y]) (:y (->P 1 2))",
            "-e", "(defprotocol Q (q [this])) (extend-type String Q (q [s] (count s))) (q \"abc\")",
            "-e", "(require 'clojure.string) (clojure.string/upper-case \"a\")");
        Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
        Assert.That(r.Out, Does.Contain("[4950 \"a1\" \"{:a [1 2]}\" \"3\" (3 2 1)]"), r.ToString());
        Assert.That(r.Out, Does.Contain("user.P\n2\n"), r.ToString());
        Assert.That(r.Out, Does.Contain("Q\n3\n"), r.ToString());
        Assert.That(r.Out, Does.EndWith("\"A\"\n"), r.ToString());
    }

    [Test, Order(3)]
    public void Verify()
    {
        if (!File.Exists(Dll)) Assert.Ignore("stage3 not built");
        var tool = Environment.GetEnvironmentVariable("ILVERIFY")
          ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools",
                          OperatingSystem.IsWindows() ? "ilverify.exe" : "ilverify");
        if (!File.Exists(tool)) Assert.Ignore("ilverify not installed (dotnet tool install -g dotnet-ilverify)");
        var hostDir = Path.GetDirectoryName(typeof(Cleat.Host.Program).Assembly.Location);
        var psi = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[]
                 {
                     Dll, "-r", Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location), "*.dll"),
                     "-r", Path.Combine(hostDir, "Cleat.Core.dll"), "-r", Path.Combine(hostDir, "cleat-emit.dll"),
                 })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.That(output, Does.Contain($"All Classes and Methods in {Dll} Verified."), output);
    }

    /// <summary>
    /// cleat-091（designs/08 §0）：dotnet build 从种子发射 stage1，再由它编出 src/boot 的镜像与编译器包；种子是不动点，所以两者与
    /// stage1 逐字节相同（改了 Clojure 源码或编译器而没有重生成种子时不同：tools/build.cs clojure-seed）。
    /// </summary>
    [Test, Order(4)]
    public void BuildFromTheSeedReproducesTheSeed()
    {
        string stage1 = Path.Combine(HostProcessTests.RepoDir, "src", "Cleat.Host", "obj", "seed-stage1");
        if (!File.Exists(Path.Combine(stage1, "cleat.compiler.dll"))) Assert.Ignore("stage1 not emitted (dotnet build -p:BuildBootImage=false?)");
        foreach (var dll in new[] { "cleat.boot.dll", "cleat.compiler.dll" })
            Assert.That(File.ReadAllBytes(Path.Combine(HostProcessTests.RepoDir, "src", "boot", dll)),
                        Is.EqualTo(File.ReadAllBytes(Path.Combine(stage1, dll))), dll + ": src/boot vs the seed's stage1");
    }
}
