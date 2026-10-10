using System.IO;
using NUnit.Framework;

namespace Cleat.Host.Tests;

/// <summary>
/// 符合性基线的门禁（cleat-032，designs/09 §1、§3）：
///   语料：tools/conformance.clj --gate——:common 的每个失败行都在 tests/conformance/cleat-divergences.edn 里，表里的行都仍然失败；
///   clojure-test-suite / test_clojure：tools/cts.clj——每个命名空间的 clojure.test 计数与基线（tests/cts-baseline.edn、
///   tests/test-clojure-baseline.edn）完全相同，更好或更差都失败（有意地用 --write 更新基线）。
/// 每项约 1 分钟。
/// </summary>
[Category("Conformance")]
[NonParallelizable]
public class ConformanceTests
{
    private static string Repo => HostProcessTests.RepoDir;

    private static void Gate(params string[] args)
    {
        var r = HostProcessTests.CleatInWithTimeout(Repo, null, null, 900_000, args);
        Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
    }

    [Test]
    public void CorpusCommonRowsMatchTheDivergenceTable() =>
        Gate(Path.Combine("tools", "conformance.clj"), Path.Combine("tests", "conformance", "corpus.edn"), "--gate");

    [Test]
    public void ClojureTestSuiteMatchesItsBaseline() => Gate(Path.Combine("tools", "cts.clj"), "cts");

    [Test]
    public void TestClojureMatchesItsBaseline() => Gate(Path.Combine("tools", "cts.clj"), "test-clojure");
}
