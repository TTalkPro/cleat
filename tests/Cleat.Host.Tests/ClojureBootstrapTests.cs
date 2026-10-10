using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace Cleat.Host.Tests;

/// <summary>
/// Clojure 镜像的自举不动点（cleat-053，designs/08 §3 第二段、§7）：
///   A：宿主不加载 Clojure 镜像（--no-boot），从 src/clojure 源码按 boot-order.edn 加载并编译 → A/cleat.boot.dll；
///   B：宿主加载 A，再从源码编译一遍 → B；C：加载 B 编译 → C。
/// 判据：B 与 C 的 Lcil 文本一致、dll 逐字节一致（A 不同：它展开 core.clj 时用的是 RT 的引导 ns 宏等，B / C 用镜像里的）；
/// C 能运行冒烟集；ILVerify（装了的话）通过。每段约 20 秒。
/// </summary>
[Category("Bootstrap")]
[NonParallelizable]
public class ClojureBootstrapTests
{
    private static readonly string s_root = Path.Combine(Path.GetTempPath(), "cleat-clojure-bootstrap");
    private static string Dir(string stage) => Path.Combine(s_root, stage);
    private static string Dll(string stage) => Path.Combine(Dir(stage), "cleat.boot.dll");
    private static string Lcil(string stage) => Path.Combine(Dir(stage), "cleat.boot.lcil");
    private static string Src => Path.Combine(HostProcessTests.RepoDir, "src", "clojure");
    private static string BootOrder => Path.Combine(HostProcessTests.RepoDir, "src", "boot", "boot-order.edn");

    private static void Build(string stage, string from)
    {
        if (from != null && !File.Exists(Dll(from))) Assert.Ignore($"stage {from} not built");
        Directory.CreateDirectory(Dir(stage));
        var args = (from == null ? new[] { "--no-boot" } : new[] { "--boot", Dll(from) })
            .Concat(new[] { "-I", Src, "--compile-boot", Dll(stage), "--boot-order", BootOrder }).ToArray();
        var sw = Stopwatch.StartNew();
        var r = HostProcessTests.CleatIn(Dir(stage), null, null, args);
        TestContext.Out.WriteLine($"stage {stage}{(from == null ? " (from source)" : " (on " + from + ")")}: {sw.Elapsed.TotalSeconds:F1} s\n{r.Err}");
        Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
        Assert.That(File.Exists(Dll(stage)) && File.Exists(Lcil(stage)) && File.Exists(Path.ChangeExtension(Dll(stage), ".ifc")), Is.True, r.ToString());
    }

    [Test, Order(1)]
    public void StageA_FromSource() => Build("A", null);

    [Test, Order(2)]
    public void StageB_OnA() => Build("B", "A");

    [Test, Order(3)]
    public void StageC_OnB() => Build("C", "B");

    [Test, Order(4)]
    public void Fixpoint()
    {
        if (!File.Exists(Dll("C"))) Assert.Ignore("stage C not built");
        Assert.That(File.ReadAllText(Lcil("C")), Is.EqualTo(File.ReadAllText(Lcil("B"))), "B / C Lcil");
        Assert.That(File.ReadAllBytes(Dll("C")), Is.EqualTo(File.ReadAllBytes(Dll("B"))), "B / C dll");
        foreach (var s in new[] { "A", "C" })
            TestContext.Out.WriteLine($"sha256 {Sha(Dll(s))}  {s}/cleat.boot.dll ({new FileInfo(Dll(s)).Length} bytes); lcil {Sha(Lcil(s))}");
    }

    private static string Sha(string path) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>C 是能用的镜像：新进程只加载它（不碰源码）跑冒烟集。</summary>
    [Test, Order(5)]
    public void StageC_Runs()
    {
        if (!File.Exists(Dll("C"))) Assert.Ignore("stage C not built");
        var r = HostProcessTests.CleatIn(Dir("C"), null, null, "--boot", Dll("C"), "-e",
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

    [Test, Order(6)]
    public void Verify()
    {
        if (!File.Exists(Dll("C"))) Assert.Ignore("stage C not built");
        var tool = Environment.GetEnvironmentVariable("ILVERIFY")
          ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools",
                          OperatingSystem.IsWindows() ? "ilverify.exe" : "ilverify");
        if (!File.Exists(tool)) Assert.Ignore("ilverify not installed (dotnet tool install -g dotnet-ilverify)");
        var hostDir = Path.GetDirectoryName(typeof(Cleat.Host.Program).Assembly.Location);
        var psi = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[]
                 {
                     Dll("C"), "-r", Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location), "*.dll"),
                     "-r", Path.Combine(hostDir, "Cleat.Core.dll"), "-r", Path.Combine(hostDir, "Cleat.Scheme.dll"),
                     "-r", Path.Combine(hostDir, "cleat-emit.dll"),
                     "-r", Path.Combine(HostProcessTests.RepoDir, "src", "boot", "cleat.scheme.boot.dll"),
                 })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.That(output, Does.Contain($"All Classes and Methods in {Dll("C")} Verified."), output);
    }
}
