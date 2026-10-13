using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace Cleat.Scheme.Tests;

/// <summary>
/// 编译器快照与端到端测试（IronScheme tests/compiler/*.sps，cleat-046 纳入 tests/scheme/compiler/）：
/// 在 Scheme boot 镜像上运行（stage0 --boot：镜像内的编译器编译脚本，eval 走镜像），退出码 0 为通过；
/// compile-library.sps 例外：它把编译好的 (boot lists) 装进当前进程、覆盖解释执行的版本，只在 stage0 + 种子上有意义，
/// 所以和 IronScheme 的 Stage0ScriptTests 一样在 stage0 上运行（约 18 秒）。
/// 以子进程运行：镜像会登记运行时钩子等全局状态，不宜放进单元测试进程。工作目录 src/scheme（脚本按它找种子与快照）。
/// 这些测试是"nanopass 未受影响"的基线（designs/15 §4）。
/// </summary>
[Category("SchemeScript")]
public class SchemeScriptTests
{
    /// <summary>脚本目录：compiler（元层与编译器）、clojure（Clojure 在元层上的库，cleat-049 起）、base（boot 库的语义回归，照 skeg 的 tests/*.sps，cleat-075 起）。</summary>
    private static readonly string[] s_dirs = { "compiler", "clojure", "base" };

    private static string ScriptsDir(string repo, string dir) => Path.Combine(repo, "tests", "scheme", dir);

    /// <summary>tests/scheme/{compiler,clojure,base}/*.sps（测试名为 clojure/、base/ 下的加目录前缀）；slow- 开头的归入 Slow 类别，可用 --filter "TestCategory!=Slow" 跳过。</summary>
    public static IEnumerable<TestCaseData> Scripts()
    {
        string repo = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory ?? ".", "..", "..", "..", "..", ".."));
        return s_dirs.SelectMany(dir => Directory.GetFiles(ScriptsDir(repo, dir), "*.sps").Select(Path.GetFileName).OrderBy(n => n)
          .Select(n =>
          {
              string name = dir == "compiler" ? n : dir + "/" + n;
              var d = new TestCaseData(dir, n).SetName(name);
              return n.StartsWith("slow-") ? d.SetCategory("Slow") : d;
          }));
    }

    /// <summary>在 stage0 + 文本种子上运行的脚本（其余在镜像上）。</summary>
    private static readonly string[] s_onStage0 = { "compile-library.sps" };

    [TestCaseSource(nameof(Scripts))]
    public void Script(string dir, string name)
    {
        Assert.That(File.Exists(SchemePaths.Stage0), Is.True, "stage0 not built: " + SchemePaths.Stage0);
        bool onStage0 = s_onStage0.Contains(name);
        if (!onStage0 && !File.Exists(SchemePaths.Boot)) Assert.Ignore("Scheme boot image not built: " + SchemePaths.Boot + " (dotnet build, or the Bootstrap tests)");
        string script = Path.GetRelativePath(SchemePaths.SchemeDir, Path.Combine(ScriptsDir(SchemePaths.RepoDir, dir), name)).Replace('\\', '/');
        string[] host = onStage0 ? new[] { "boot/boot-seed.core" } : new[] { "--boot", SchemePaths.Boot };
        var r = SchemePaths.Run("dotnet", new[] { SchemePaths.Stage0 }.Concat(SchemePaths.IncludeArgs).Concat(host).Concat(new[] { script }).ToArray(),
                                SchemePaths.SchemeDir, checkExitCode: false);
        TestContext.Out.WriteLine(r.ToString());
        Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
    }
}
