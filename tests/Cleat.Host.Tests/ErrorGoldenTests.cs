using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace Cleat.Host.Tests;

/// <summary>
/// cleat-055 诊断边界 + cleat-035 CLI：tests/errors/{read,expand,macro,runtime,cli}/名字/ 下的黄金文件（jolt 的 test/errors
/// 形态）。read/expand/macro/runtime：每个目录一个 <c>input.clj</c> 与 <c>expected.txt</c>（cleat 的标准错误 + 末行
/// <c>exit: 退出码</c>）；在该目录里运行 <c>cleat input.clj</c>，输出必须逐字相同。cli：目录里是 <c>args.txt</c>（命令行参数，
/// 空格分隔），机器相关的路径归一化为 &lt;host&gt; / &lt;case&gt; / &lt;repo&gt;。
/// 文本按 JVM Clojure 1.12 的 clojure.main/ex-str 对照过（宿主异常的类型名与消息除外）。
/// 重新生成：在目录里 <c>dotnet …/cleat.dll input.clj 2&gt; expected.txt; echo "exit: $?" &gt;&gt; expected.txt</c>，并逐个审阅。
/// </summary>
[Category("Host")]
public class ErrorGoldenTests
{
    private static string ErrorsDir => Path.Combine(HostProcessTests.RepoDir, "tests", "errors");

    public static IEnumerable<TestCaseData> Cases()
    {
        foreach (var kind in new[] { "read", "expand", "macro", "runtime", "cli" })
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
        HostProcessTests.Result r;
        if (name.StartsWith("cli/", System.StringComparison.Ordinal))
        {
            // 命令行用例：args.txt 是参数（空格分隔）；机器相关的路径归一化后再比较
            string[] args = File.ReadAllText(Path.Combine(dir, "args.txt")).TrimEnd('\n').Split(' ');
            r = HostProcessTests.CleatIn(dir, null, null, args);
        }
        else
        {
            r = HostProcessTests.CleatIn(dir, null, null, "input.clj");
        }
        string got = Normalize(dir, r.Err) + "exit: " + r.ExitCode + "\n";
        Assert.That(got, Is.EqualTo(expected), r.ToString());
        Assert.That(got, Does.Not.Contain("Cleat.Scheme"), "no Scheme runtime names in diagnostics");
    }

    /// <summary>cli 黄金文件里的机器相关路径：宿主目录 → &lt;host&gt;，用例目录 → &lt;case&gt;，仓库根 → &lt;repo&gt;。</summary>
    private static string Normalize(string dir, string text)
    {
        string host = Path.GetDirectoryName(HostProcessTests.HostDll) ?? "";
        string repo = HostProcessTests.RepoDir;
        return text.Replace(host, "<host>").Replace(dir, "<case>").Replace(repo, "<repo>").Replace("\\", "/");
    }

    [Test]
    public void EveryKindHasGoldens()
    {
        foreach (var kind in new[] { "read", "expand", "macro", "runtime", "cli" })
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
