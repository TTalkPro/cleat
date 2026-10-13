using NUnit.Framework;

namespace Cleat.Host.Tests;

/// <summary>
/// packages/ 下各个包自带的 clojure.test 测试套件（cleat-062 nREPL、cleat-063 neorepl）：在宿主上运行包的 test/run.clj，
/// 全部通过时退出码为 0。
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

    [Test]
    public void Neorepl()
    {
        var r = RunPackageTests("-I", "packages/nrepl/src", "-I", "packages/neorepl/src", "-I", "packages/neorepl/test",
                                "packages/neorepl/test/run.clj");
        Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
        Assert.That(r.Out, Does.Contain("0 failures, 0 errors."));
    }
}
