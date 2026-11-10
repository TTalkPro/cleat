using System;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace Cleat.Host.Tests;

/// <summary>
/// packages/ 下各个包自带的 clojure.test 测试套件（cleat-062 nREPL、cleat-063 neorepl、cleat-066 tools.build 与 tools.namespace）：在宿主上运行包的 test/run.clj，
/// 全部通过时退出码为 0。从源码与从包程序集（cleat-067：两个包各编成一个程序集，加载路径上没有源码）各跑一遍。
/// </summary>
[Category("Packages")]
public class PackageTests
{
    private static HostProcessTests.Result RunPackageTests(params string[] args) =>
        HostProcessTests.CleatInWithTimeout(HostProcessTests.RepoDir, null, null, 1_200_000, args);

    [Test]
    public void Nrepl()
    {
        var r = RunPackageTests("-I", "packages/nrepl/src", "-I", "packages/nrepl/test", "packages/nrepl/test/run.clj");
        Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
        Assert.That(r.Out, Does.Contain("0 failures, 0 errors."));
    }

    private static string[] Namespaces(string package) =>
        Directory.GetFiles(Path.Combine(HostProcessTests.RepoDir, "packages", package, "src"), "*.clj*", SearchOption.AllDirectories)   // .clj 与 .cljc（test.check 多是 .cljc）
            .Select(f => Path.GetRelativePath(Path.Combine(HostProcessTests.RepoDir, "packages", package, "src"), f))
            .Select(r => Path.ChangeExtension(r, null).Replace(Path.DirectorySeparatorChar, '.').Replace('_', '-'))
            .OrderBy(n => n, StringComparer.Ordinal).ToArray();

    [Test]
    public void BothFromPackageAssemblies()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cleat-pkgasm-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(dir);
        try
        {
            var c = RunPackageTests(new[] { "-I", "packages/nrepl/src", "--compile-package", Path.Combine(dir, "Cleat.Nrepl.dll"), "--package", "nrepl" }
                                    .Concat(Namespaces("nrepl")).ToArray());
            Assert.That(c.ExitCode, Is.EqualTo(0), c.ToString());
            c = RunPackageTests(new[] { "-I", dir, "-I", "packages/neorepl/src", "--root", "packages/neorepl/src",
                                        "--compile-package", Path.Combine(dir, "Cleat.Neorepl.dll"), "--package", "neorepl" }
                                .Concat(Namespaces("neorepl")).ToArray());
            Assert.That(c.ExitCode, Is.EqualTo(0), c.ToString());
            // cleat-158：包程序集现在同时产出 PDB（序列点），但每个包仍然只有一个程序集
            Assert.That(Directory.GetFiles(dir, "*.dll").Select(Path.GetFileName).Order(),
                        Is.EqualTo(new[] { "Cleat.Neorepl.dll", "Cleat.Nrepl.dll" }), "one assembly per package");
            Assert.That(Directory.GetFiles(dir, "*.pdb").Select(Path.GetFileName).Order(),
                        Is.EqualTo(new[] { "Cleat.Neorepl.pdb", "Cleat.Nrepl.pdb" }), "each package assembly has its PDB");

            var r = RunPackageTests("-I", dir, "-I", "packages/nrepl/test", "packages/nrepl/test/run.clj");
            Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
            Assert.That(r.Out, Does.Contain("0 failures, 0 errors."));
            // jack-in computes the server's load path with tools.build (cleat-069)
            r = RunPackageTests("-I", dir, "-I", "packages/tools.build/src", "-I", "packages/tools.namespace/src",
                                "-I", "packages/neorepl/test", "packages/neorepl/test/run.clj");
            Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
            Assert.That(r.Out, Does.Contain("0 failures, 0 errors."));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (Exception) { }
        }
    }

    [Test]
    public void ToolsNamespace()
    {
        var r = RunPackageTests("-I", "packages/tools.namespace/src", "-I", "packages/tools.namespace/test",
                                "packages/tools.namespace/test/run.clj");
        Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
        Assert.That(r.Out, Does.Contain("0 failures, 0 errors."));
    }

    /// <summary>
    /// cleat.nanopass（cleat-083 / 084 / 095，designs/19 D2）：cleat-182 起是 packages/nanopass 下的普通用户库（原在 Clojure 编译器的包里）。
    /// 从源码跑一遍，再编成包程序集 Cleat.Nanopass.dll、加载路径上只有它（没有源码）再跑一遍。
    /// </summary>
    [Test]
    public void Nanopass()
    {
        var r = RunPackageTests("-I", "packages/nanopass/src", "-I", "packages/nanopass/test", "packages/nanopass/test/run.clj");
        Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
        Assert.That(r.Out, Does.Contain("0 failures, 0 errors."));

        string dir = Path.Combine(Path.GetTempPath(), "cleat-nanopass-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(dir);
        try
        {
            var c = RunPackageTests(new[] { "-I", "packages/nanopass/src", "--compile-package", Path.Combine(dir, "Cleat.Nanopass.dll"), "--package", "nanopass" }
                                    .Concat(Namespaces("nanopass")).ToArray());
            Assert.That(c.ExitCode, Is.EqualTo(0), c.ToString());
            r = RunPackageTests("-I", dir, "-I", "packages/nanopass/test", "packages/nanopass/test/run.clj");
            Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
            Assert.That(r.Out, Does.Contain("0 failures, 0 errors."));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (Exception) { }
        }
    }

    /// <summary>
    /// clojure.spec.alpha / clojure.core.specs.alpha（cleat-166，packages/spec.alpha，取自 ClojureCLR 的 clr.spec.alpha）与 clojure.test.check
    /// （packages/test.check，取自 clr.test.check）：两个包的上游测试各跑一遍源码、一遍编出的包程序集（加载路径上没有源码）。
    /// spec 的生成器测试要 test.check；test.check 的测试也用 spec。
    /// </summary>
    [Test]
    public void SpecAlphaAndTestCheck()
    {
        var r = RunPackageTests("-I", "packages/spec.alpha/src", "-I", "packages/test.check/src", "-I", "packages/spec.alpha/test",
                                "packages/spec.alpha/test/run.clj");
        Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
        Assert.That(r.Out, Does.Contain("0 failures, 0 errors."));
        r = RunPackageTests("-I", "packages/test.check/src", "-I", "packages/spec.alpha/src", "-I", "packages/test.check/test",
                            "packages/test.check/test/run.clj");
        Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
        Assert.That(r.Out, Does.Contain("0 failures, 0 errors."));

        string dir = Path.Combine(Path.GetTempPath(), "cleat-spec-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(dir);
        try
        {
            var c = RunPackageTests(new[] { "-I", "packages/test.check/src", "--compile-package", Path.Combine(dir, "Cleat.TestCheck.dll"), "--package", "test.check" }
                                    .Concat(Namespaces("test.check")).ToArray());
            Assert.That(c.ExitCode, Is.EqualTo(0), c.ToString());
            c = RunPackageTests(new[] { "-I", dir, "-I", "packages/spec.alpha/src", "--root", "packages/spec.alpha/src",
                                        "--compile-package", Path.Combine(dir, "Cleat.SpecAlpha.dll"), "--package", "spec.alpha" }
                                .Concat(Namespaces("spec.alpha")).ToArray());
            Assert.That(c.ExitCode, Is.EqualTo(0), c.ToString());
            r = RunPackageTests("-I", dir, "-I", "packages/spec.alpha/test", "packages/spec.alpha/test/run.clj");
            Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
            Assert.That(r.Out, Does.Contain("0 failures, 0 errors."));
            r = RunPackageTests("-I", dir, "-I", "packages/test.check/test", "packages/test.check/test/run.clj");
            Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
            Assert.That(r.Out, Does.Contain("0 failures, 0 errors."));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (Exception) { }
        }
    }

    /// <summary>
    /// clojure.data.generators 与 clojure.test.generative（cleat-191，packages/data.generators、packages/test.generative，取自 ClojureCLR 的
    /// clr.data.generators / clr.test.generative）：上游测试各跑源码一遍、编出的包程序集一遍（加载路径上没有源码）。test.generative 的测试是
    /// defspec，经上游 runner 的 run-suite 跑（test/run.clj，2 线程各 1 秒）；它要 tools.namespace（packages/tools.namespace）。
    /// </summary>
    [Test]
    public void DataGeneratorsAndTestGenerative()
    {
        var r = RunPackageTests("-I", "packages/data.generators/src", "-I", "packages/data.generators/test", "packages/data.generators/test/run.clj");
        Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
        Assert.That(r.Out, Does.Contain("0 failures, 0 errors."));
        r = RunPackageTests("-I", "packages/test.generative/src", "-I", "packages/data.generators/src", "-I", "packages/tools.namespace/src",
                            "-I", "packages/test.generative/test", "packages/test.generative/test/run.clj", "1000");
        Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
        Assert.That(r.Out, Does.Contain(" 0 failures."));

        string dir = Path.Combine(Path.GetTempPath(), "cleat-gen-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(dir);
        try
        {
            var c = RunPackageTests(new[] { "-I", "packages/data.generators/src", "--compile-package", Path.Combine(dir, "Cleat.DataGenerators.dll"), "--package", "data.generators" }
                                    .Concat(Namespaces("data.generators")).ToArray());
            Assert.That(c.ExitCode, Is.EqualTo(0), c.ToString());
            c = RunPackageTests(new[] { "-I", "packages/tools.namespace/src", "--compile-package", Path.Combine(dir, "Cleat.ToolsNamespace.dll"), "--package", "tools.namespace" }
                                .Concat(Namespaces("tools.namespace")).ToArray());
            Assert.That(c.ExitCode, Is.EqualTo(0), c.ToString());
            c = RunPackageTests(new[] { "-I", dir, "-I", "packages/test.generative/src", "--root", "packages/test.generative/src",
                                        "--compile-package", Path.Combine(dir, "Cleat.TestGenerative.dll"), "--package", "test.generative" }
                                .Concat(Namespaces("test.generative")).ToArray());
            Assert.That(c.ExitCode, Is.EqualTo(0), c.ToString());
            r = RunPackageTests("-I", dir, "-I", "packages/data.generators/test", "packages/data.generators/test/run.clj");
            Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
            Assert.That(r.Out, Does.Contain("0 failures, 0 errors."));
            r = RunPackageTests("-I", dir, "-I", "packages/test.generative/test", "packages/test.generative/test/run.clj", "1000");
            Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
            Assert.That(r.Out, Does.Contain(" 0 failures."));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (Exception) { }
        }
    }

    /// <summary>
    /// clojure.core.async（cleat-167，packages/core.async，取自 ClojureCLR 的 clr.core.async）与它的 go 宏要的 tools.analyzer(.clr)、tools.reader：
    /// 三个包的上游测试各跑源码一遍、编出的包程序集一遍（加载路径上没有源码）。
    /// </summary>
    [Test]
    public void CoreAsyncWithAnalyzerAndReader()
    {
        string[] src = { "-I", "packages/tools.reader/src", "-I", "packages/tools.analyzer/src", "-I", "packages/core.async/src" };
        foreach (var p in new[] { "tools.reader", "tools.analyzer", "core.async" })
        {
            var r = RunPackageTests(src.Concat(new[] { "-I", $"packages/{p}/test", $"packages/{p}/test/run.clj" }).ToArray());
            Assert.That(r.ExitCode, Is.EqualTo(0), p + ": " + r);
            Assert.That(r.Out, Does.Contain("0 failures, 0 errors."), p);
        }

        string dir = Path.Combine(Path.GetTempPath(), "cleat-async-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var (p, asm) in new[] { ("tools.reader", "Cleat.ToolsReader"), ("tools.analyzer", "Cleat.ToolsAnalyzer"), ("core.async", "Cleat.CoreAsync") })
            {
                var c = RunPackageTests(new[] { "-I", dir, "-I", $"packages/{p}/src", "--root", $"packages/{p}/src",
                                                "--compile-package", Path.Combine(dir, asm + ".dll"), "--package", p }
                                        .Concat(Namespaces(p)).ToArray());
                Assert.That(c.ExitCode, Is.EqualTo(0), p + ": " + c);
            }
            foreach (var p in new[] { "tools.reader", "tools.analyzer", "core.async" })
            {
                var r = RunPackageTests("-I", dir, "-I", $"packages/{p}/test", $"packages/{p}/test/run.clj");
                Assert.That(r.ExitCode, Is.EqualTo(0), p + " (package assemblies): " + r);
                Assert.That(r.Out, Does.Contain("0 failures, 0 errors."), p);
            }
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (Exception) { }
        }
    }

    [Test]
    public void ToolsBuild()
    {
        var r = RunPackageTests("-I", "packages/tools.build/src", "-I", "packages/tools.namespace/src", "-I", "packages/tools.build/test",
                                "packages/tools.build/test/run.clj");
        Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
        Assert.That(r.Out, Does.Contain("0 failures, 0 errors."));
    }

    [Test]
    public void Neorepl()
    {
        var r = RunPackageTests("-I", "packages/nrepl/src", "-I", "packages/tools.build/src", "-I", "packages/tools.namespace/src",
                                "-I", "packages/neorepl/src", "-I", "packages/neorepl/test",
                                "packages/neorepl/test/run.clj");
        Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
        Assert.That(r.Out, Does.Contain("0 failures, 0 errors."));
    }
}
