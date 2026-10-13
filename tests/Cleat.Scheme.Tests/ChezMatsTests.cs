using NUnit.Framework;

namespace Cleat.Scheme.Tests;

/// <summary>
/// Chez Scheme mats 作为测试 oracle（cleat-077，照 skeg 的 ChezMats / iron-072）：在 Scheme boot 镜像上运行
/// src/scheme/tests/chez-mats/run.sps，逐条求值 Chez 的 mat 子句，每个文件失败数为 0 才算通过；已知差异
/// （Chez 扩展、不终止的用法等）登记在 tests/chez-mats/known.ss，由 mkknown.py 按 cleat 的结果生成。
/// 用例之间不并行：每个用例是监督者 + 工作进程两个 dotnet 进程（工作进程堆上限 2 GB），一起跑会把机器内存吃光。
/// 全部约 3 ~ 5 分钟（5_3.ms 一个文件约 1 分钟）。
/// </summary>
[Category("ChezMats")]
[NonParallelizable]
public class ChezMatsTests
{
    [TestCase("3.ms")]
    [TestCase("4.ms")]
    [TestCase("5_1.ms")]
    [TestCase("5_2.ms")]
    [TestCase("5_3.ms")]
    [TestCase("5_4.ms")]
    [TestCase("5_5.ms")]
    [TestCase("5_6.ms")]
    [TestCase("5_7.ms")]
    [TestCase("5_8.ms")]
    [TestCase("bytevector.ms")]
    [TestCase("enum.ms")]
    [TestCase("exceptions.ms")]
    [TestCase("fl.ms")]
    [TestCase("fx.ms")]
    [TestCase("hash.ms")]
    [TestCase("ieee.ms")]
    [TestCase("record.ms")]
    public void File(string file)
    {
        Assert.That(System.IO.File.Exists(SchemePaths.Stage0), Is.True, "stage0 not built: " + SchemePaths.Stage0);
        if (!System.IO.File.Exists(SchemePaths.Boot)) Assert.Ignore("Scheme boot image not built: " + SchemePaths.Boot);
        string[] args = { SchemePaths.Stage0, "-I", "lib", "-I", "nanopass", "--boot", SchemePaths.Boot, "tests/chez-mats/run.sps", file };
        var r = SchemePaths.Run("dotnet", args, SchemePaths.SchemeDir, checkExitCode: false);
        TestContext.Out.WriteLine(r.ToString());
        // 有失败时运行器退出码为 1，输出里有 FAIL 行
        Assert.That(r.Output, Does.Match($@"(?m)^{System.Text.RegularExpressions.Regex.Escape(file)}: pass \d+, fail 0,"), r.ToString());
    }
}
