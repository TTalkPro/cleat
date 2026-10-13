using System;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace Cleat.Host.Tests;

/// <summary>
/// packages/ 下各个包自带的 clojure.test 测试套件（cleat-062 nREPL、cleat-063 neorepl）：在宿主上运行包的 test/run.clj，
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
            var c = RunPackageTests(new[] { "-I", "packages/nrepl/src", "--compile-package", Path.Combine(dir, "Nrepl.dll"), "--package", "nrepl" }
                                    .Concat(Namespaces("nrepl")).ToArray());
            Assert.That(c.ExitCode, Is.EqualTo(0), c.ToString());
            c = RunPackageTests(new[] { "-I", dir, "-I", "packages/neorepl/src", "--root", "packages/neorepl/src",
                                        "--compile-package", Path.Combine(dir, "Neorepl.dll"), "--package", "neorepl" }
                                .Concat(Namespaces("neorepl")).ToArray());
            Assert.That(c.ExitCode, Is.EqualTo(0), c.ToString());
            Assert.That(Directory.GetFiles(dir).Select(Path.GetFileName).Order(), Is.EqualTo(new[] { "Neorepl.dll", "Nrepl.dll" }), "one assembly per package");

            var r = RunPackageTests("-I", dir, "-I", "packages/nrepl/test", "packages/nrepl/test/run.clj");
            Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
            Assert.That(r.Out, Does.Contain("0 failures, 0 errors."));
            r = RunPackageTests("-I", dir, "-I", "packages/neorepl/test", "packages/neorepl/test/run.clj");
            Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
            Assert.That(r.Out, Does.Contain("0 failures, 0 errors."));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (Exception) { }
        }
    }

    [Test]
    public void Neorepl()
    {
        var r = RunPackageTests("-I", "packages/nrepl/src", "-I", "packages/neorepl/src", "-I", "packages/neorepl/test",
                                "packages/neorepl/test/run.clj");
        Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
        Assert.That(r.Out, Does.Contain("0 failures, 0 errors."));
    }
}
