using System;
using System.Diagnostics;
using System.IO;
using NUnit.Framework;

namespace Cleat.Host.Tests;

/// <summary>
/// 镜像构建的确定性（G5，designs/30 D8；cleat-182 起取代 Clojure 编译器的自举不动点 ClojureBootstrapTests 与种子守卫）：
/// dotnet build 由编译器从源码两步编出 src/boot 的镜像（不加载镜像编出 stage1，再在 stage1 上编）。这里在临时目录把这两步重走一遍，
/// 结果与 src/boot 逐字节相同（构建两次相同）；src/boot 的镜像也是不动点（在它上面再编一遍相同）。重编的镜像能运行冒烟集、ILVerify
/// （装了的话）通过。每次 --compile-boot 约 10 ~ 15 秒。
/// </summary>
[Category("Determinism")]
[NonParallelizable]
public class DeterminismTests
{
    private static readonly string s_root = Path.Combine(Path.GetTempPath(), "cleat-determinism");
    private static string Clj => Path.Combine(HostProcessTests.RepoDir, "src", "clojure");
    private static string Order => Path.Combine(HostProcessTests.RepoDir, "src", "boot", "boot-order.edn");
    private static string BuildBoot => Path.Combine(HostProcessTests.RepoDir, "src", "boot", "cleat.boot.dll");
    private static string Stage2 => Path.Combine(s_root, "stage2", "cleat.boot.dll");

    private static void CompileBoot(string boot, string output)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var sw = Stopwatch.StartNew();
        string[] from = boot == null ? new[] { "--no-boot" } : new[] { "--boot", boot };
        var r = HostProcessTests.CleatInWithTimeout(s_root, null, null, 600_000,
            [.. from, "-I", Clj, "--compile-boot", output, "--boot-order", Order]);
        TestContext.Out.WriteLine($"{(boot == null ? "--no-boot" : boot)} -> {output}: {sw.Elapsed.TotalSeconds:F1} s");
        Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
    }

    private static void AssertSameImage(string a, string b, string what)
    {
        foreach (var ext in new[] { ".dll", ".ifc", ".inl" })
            Assert.That(File.ReadAllBytes(Path.ChangeExtension(a, ext)), Is.EqualTo(File.ReadAllBytes(Path.ChangeExtension(b, ext))),
                        "cleat.boot" + ext + ": " + what);
    }

    /// <summary>从零（没有任何镜像）两步编出的镜像与 dotnet build 编出的 src/boot 逐字节相同。</summary>
    [Test, Order(1)]
    public void BuildFromScratchReproducesTheBuildImage()
    {
        if (!File.Exists(BuildBoot)) Assert.Ignore("src/boot not built (dotnet build -p:BuildBootImage=false?)");
        if (Directory.Exists(s_root)) Directory.Delete(s_root, true);
        Directory.CreateDirectory(s_root);
        string stage1 = Path.Combine(s_root, "stage1", "cleat.boot.dll");
        CompileBoot(null, stage1);
        CompileBoot(stage1, Stage2);
        AssertSameImage(Stage2, BuildBoot, "built from scratch again vs src/boot");
    }

    /// <summary>src/boot 的镜像是编译器的不动点：在它上面再编一遍，镜像与 .ifc / .inl 逐字节相同（cleat-181）。</summary>
    [Test, Order(2)]
    public void BuildImageIsAFixedPoint()
    {
        if (!File.Exists(BuildBoot)) Assert.Ignore("src/boot not built (dotnet build -p:BuildBootImage=false?)");
        Directory.CreateDirectory(s_root);
        string again = Path.Combine(s_root, "again", "cleat.boot.dll");
        CompileBoot(BuildBoot, again);
        AssertSameImage(again, BuildBoot, "rebuilt on src/boot vs src/boot");
    }

    /// <summary>重编的镜像是能用的：新进程只加载它（不碰 src/boot）跑冒烟集。</summary>
    [Test, Order(3)]
    public void RebuiltImageRuns()
    {
        if (!File.Exists(Stage2)) Assert.Ignore("stage2 not built");
        var r = HostProcessTests.CleatIn(Path.GetDirectoryName(Stage2), null, null, "--boot", Stage2, "-e",
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

    [Test, Order(4)]
    public void Verify()
    {
        if (!File.Exists(Stage2)) Assert.Ignore("stage2 not built");
        var tool = Environment.GetEnvironmentVariable("ILVERIFY")
          ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools",
                          OperatingSystem.IsWindows() ? "ilverify.exe" : "ilverify");
        if (!File.Exists(tool)) Assert.Ignore("ilverify not installed (dotnet tool install -g dotnet-ilverify)");
        var hostDir = Path.GetDirectoryName(typeof(Cleat.Host.Program).Assembly.Location);
        var psi = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[]
                 {
                     Stage2, "-r", Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location), "*.dll"),
                     "-r", Path.Combine(hostDir, "Cleat.Core.dll"),
                 })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.That(output, Does.Contain($"All Classes and Methods in {Stage2} Verified."), output);
    }
}
