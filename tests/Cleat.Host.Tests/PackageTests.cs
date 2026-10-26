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
        Directory.GetFiles(Path.Combine(HostProcessTests.RepoDir, "packages", package, "src"), "*.clj", SearchOption.AllDirectories)
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
