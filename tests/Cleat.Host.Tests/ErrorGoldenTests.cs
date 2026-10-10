using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace Cleat.Host.Tests;

/// <summary>
/// cleat-055 诊断边界：tests/errors/{read,expand,macro,runtime}/名字/ 下的黄金文件（jolt 的 test/errors 形态）。每个目录一个
/// <c>input.clj</c> 与 <c>expected.txt</c>（cleat 的标准错误 + 末行 <c>exit: 退出码</c>）；在该目录里运行 <c>cleat input.clj</c>，
/// 输出必须逐字相同。文本按 JVM Clojure 1.12 的 clojure.main/ex-str 对照过（宿主异常的类型名与消息除外）。
/// 重新生成：在目录里 <c>dotnet …/cleat.dll input.clj 2&gt; expected.txt; echo "exit: $?" &gt;&gt; expected.txt</c>，并逐个审阅。
/// </summary>
[Category("Host")]
public class ErrorGoldenTests
{
    private static string ErrorsDir => Path.Combine(HostProcessTests.RepoDir, "tests", "errors");

    public static IEnumerable<TestCaseData> Cases()
    {
        foreach (var kind in new[] { "read", "expand", "macro", "runtime" })
        {
            string dir = Path.Combine(ErrorsDir, kind);
            if (!Directory.Exists(dir)) continue;
            foreach (var d in Directory.GetDirectories(dir).OrderBy(x => x, System.StringComparer.Ordinal))
                yield return new TestCaseData(kind + "/" + Path.GetFileName(d)).SetName("errors/" + kind + "/" + Path.GetFileName(d));
        }
    }

    [TestCaseSource(nameof(Cases))]
    public void Golden(string name)
    {
        string dir = Path.Combine(ErrorsDir, name.Replace('/', Path.DirectorySeparatorChar));
        string expected = File.ReadAllText(Path.Combine(dir, "expected.txt")).Replace("\r\n", "\n");
        var r = HostProcessTests.CleatIn(dir, null, null, "input.clj");
        string got = r.Err + "exit: " + r.ExitCode + "\n";
        Assert.That(got, Is.EqualTo(expected), r.ToString());
        Assert.That(got, Does.Not.Contain("Cleat.Scheme"), "no Scheme runtime names in diagnostics");
    }

    [Test]
    public void EveryKindHasGoldens()
    {
        foreach (var kind in new[] { "read", "expand", "macro", "runtime" })
            Assert.That(Directory.GetDirectories(Path.Combine(ErrorsDir, kind)), Is.Not.Empty, kind);
    }

    /// <summary>
    /// 栈：缺省不打印（与 Clojure 的 REPL / clojure.main 相同）；CLEAT_TRACE_ERRORS=clojure 打印过滤过的栈——没有 Cleat.Scheme.* 与
    /// Scheme 生成的代码（_Module$…）的帧，留下 Clojure 函数类的帧；CLEAT_TRACE_ERRORS=1 打印完整的 CLR 异常（含 Cleat.Scheme 的帧）。
    /// </summary>
    [Test]
    public void StackTracesHideSchemeFramesUnlessAskedFor()
    {
        string dir = Path.Combine(ErrorsDir, "runtime", "divide-by-zero-in-fn");
        var plain = HostProcessTests.CleatIn(dir, null, null, "input.clj");
        Assert.That(plain.Err, Does.Not.Contain("   at "), plain.ToString());

        var filtered = HostProcessTests.CleatIn(dir, null, new[] { ("CLEAT_TRACE_ERRORS", "clojure") }, "input.clj");
        Assert.That(filtered.Err, Does.StartWith("Execution error (ArithmeticException) at (input.clj:3).\nDivide by zero\nSystem.ArithmeticException: Divide by zero\n"), filtered.ToString());
        Assert.That(filtered.Err, Does.Contain("   at user$f.invoke"), "the Clojure fn frame stays: " + filtered);
        Assert.That(filtered.Err, Does.Not.Contain("Cleat.Scheme"), filtered.ToString());
        Assert.That(filtered.Err, Does.Not.Contain("_Module$"), filtered.ToString());

        var full = HostProcessTests.CleatIn(dir, null, new[] { ("CLEAT_TRACE_ERRORS", "1") }, "input.clj");
        Assert.That(full.Err, Does.Contain("Cleat.Scheme."), "CLEAT_TRACE_ERRORS=1 shows everything: " + full);
    }
}
