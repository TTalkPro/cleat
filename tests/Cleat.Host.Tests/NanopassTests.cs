using System.IO;
using NUnit.Framework;

namespace Cleat.Host.Tests;

/// <summary>
/// cleat.nanopass（cleat-083 / 084，designs/19 D2）：tests/nanopass 下的 clojure.test 命名空间在宿主上从源码运行
/// （-I src/clojure -I tests/nanopass；cleat-085 起改为加载编译器程序集）。每个命名空间一个测试，失败时输出 clojure.test 的报告。
/// </summary>
[Category("Host")]
public class NanopassTests
{
    private static string Src => Path.Combine(HostProcessTests.RepoDir, "src", "clojure");
    private static string TestSrc => Path.Combine(HostProcessTests.RepoDir, "tests", "nanopass");

    [TestCase("cleat.nanopass-test")]
    [TestCase("cleat.nanopass-pass-test")]
    [TestCase("cleat.compiler.languages-test")]
    [TestCase("cleat.compiler.pilot-test")]
    public void Namespace(string ns)
    {
        var r = HostProcessTests.CleatIn(HostProcessTests.RepoDir, null, null, "-I", Src, "-I", TestSrc,
            "-e", $"(require '{ns})",
            "-e", $"(let [r (clojure.test/run-tests '{ns})] (System.Environment/Exit (if (zero? (+ (:fail r) (:error r))) 0 1)))");
        Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
        Assert.That(r.Out, Does.Contain("0 failures, 0 errors."), r.ToString());
    }
}
