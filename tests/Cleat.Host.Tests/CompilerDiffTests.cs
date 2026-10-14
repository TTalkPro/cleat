using System;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace Cleat.Host.Tests;

/// <summary>
/// cleat-085（designs/19 D5）：逐形式转储（CLEAT_DUMP_IR）、影子模式（CLEAT_COMPILER=shadow）与 tools/compiler-diff.clj。
/// 同一组形式跑两次，Scheme 一侧的转储在临时名规范化后相同；改变一个 pass（关掉 lower-loops）时差分定位到形式与阶段。
/// </summary>
[Category("Host")]
public class CompilerDiffTests
{
    private string _dir;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cleat-085-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static readonly string[] s_forms =
    {
        "-e", "(defn f [x & ys] (loop [i 0 acc []] (if (< i x) (recur (inc i) (conj acc i)) (into acc ys))))",
        "-e", "(defmacro twice [e] `(do ~e ~e))",
        "-e", "(twice (f 3 :a))",
        "-e", "(let [m {:a 1}] (try (:a m) (catch Exception e (throw e)) (finally nil)))",
    };

    private HostProcessTests.Result Dump(string dir, params (string, string)[] extra)
    {
        var env = new[] { ("CLEAT_DUMP_IR", dir), ("CLEAT_NO_SIMPLE_EVAL", "1"), ("CLEAT_COMPILER", "shadow") }.Concat(extra).ToArray();
        var r = HostProcessTests.CleatIn(_dir, null, env, s_forms);
        Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
        return r;
    }

    private static HostProcessTests.Result Diff(string a, string b) =>
        HostProcessTests.CleatIn(HostProcessTests.RepoDir, null, null, "-I", Path.Combine(HostProcessTests.RepoDir, "src", "clojure"),
            Path.Combine(HostProcessTests.RepoDir, "tools", "compiler-diff.clj"), "diff", a, b);

    [Test]
    public void SchemeDumpsAreStableAndShadowRecordsSources()
    {
        string a = Path.Combine(_dir, "a"), b = Path.Combine(_dir, "b");
        Dump(a);
        Dump(b);
        string sch = File.ReadAllText(Path.Combine(a, "scheme", "eval.ir"));
        foreach (var stage in new[] { "p0", "l0", "sl", "cf", "c1", "in", "l1", "l2", "l3", "lcil" })
            Assert.That(sch, Does.Contain(" " + stage + "\n"), "stage " + stage);
        Assert.That(File.ReadAllText(Path.Combine(a, "clojure", "eval.ir")), Does.Contain("src\n\"(def f"), "影子模式记下源形式");
        var d = Diff(Path.Combine(a, "scheme"), Path.Combine(b, "scheme"));
        Assert.That(d.ExitCode, Is.EqualTo(0), d.ToString());
        Assert.That(d.Out, Does.Contain(" 0 different"), d.ToString());
    }

    [Test]
    public void DiffLocatesTheFirstChangedStage()
    {
        string a = Path.Combine(_dir, "a"), b = Path.Combine(_dir, "b");
        Dump(a);
        Dump(b, ("CLEAT_SCHEME_NO_034", "loops"));
        var d = Diff(Path.Combine(a, "scheme"), Path.Combine(b, "scheme"));
        Assert.That(d.ExitCode, Is.EqualTo(1), d.ToString());
        Assert.That(d.Out, Does.Contain("stage l0"), "lower-loops 之后（l0）开始不同");
        Assert.That(d.Out, Does.Not.Contain("stage p0"), "parse-core 的输出相同");
        Assert.That(d.Out, Does.Contain("source: \"(def f"), "报告附源形式");
    }
}
