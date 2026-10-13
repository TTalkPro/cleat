using NUnit.Framework;

namespace Cleat.Scheme.Tests;

/// <summary>
/// R6RS 符合性门禁（cleat-061，照 skeg 的 Conformance.R6RS）：在 Scheme boot 镜像上运行 src/scheme/tests/r6rs/run.sps
/// （Racket 的 R6RS 测试套件，经 IronScheme / skeg 纳入），必须全部通过。原来的基线是两处文本端口缺陷（output-port-buffer-mode、
/// get-line 对 \r 的处理），cleat-075 按 skeg iron-074 修好。
/// 约 10 秒，子进程运行（镜像会登记全局状态）。
/// </summary>
[Category("Conformance")]
public class R6RSConformanceTests
{
    [Test]
    public void R6RSTestSuiteMatchesItsBaseline()
    {
        Assert.That(System.IO.File.Exists(SchemePaths.Stage0), Is.True, "stage0 not built: " + SchemePaths.Stage0);
        if (!System.IO.File.Exists(SchemePaths.Boot)) Assert.Ignore("Scheme boot image not built: " + SchemePaths.Boot);
        string[] args = { SchemePaths.Stage0, "-I", "lib", "-I", "nanopass", "--boot", SchemePaths.Boot, "tests/r6rs/run.sps" };
        var r = SchemePaths.Run("dotnet", args, SchemePaths.SchemeDir, checkExitCode: false);
        TestContext.Out.WriteLine(r.ToString());
        Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
        // 整行匹配：子串匹配会让 "12 of …" 冒充 "2 of …"（skeg d530d46 的教训）
        Assert.That(r.Output, Does.Match(@"(?m)^8971 tests passed$"), r.ToString());
    }
}
