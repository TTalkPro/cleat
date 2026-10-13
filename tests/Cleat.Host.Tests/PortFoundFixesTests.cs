using System;
using System.IO;
using NUnit.Framework;

namespace Cleat.Host.Tests;

/// <summary>
/// cleat-064：移植 nREPL / neorepl 时发现的缺陷——宏展开出的 def 没有源位置（Var 的 :line / :column 恒为 0）、
/// 名字以 - 开头的协议方法分派不到 deftype / defrecord 的实现、带超时的 deref 对出错的 future 返回值而不抛出、
/// ReaderException 的消息是 .NET 的默认文本、spit 不截断文件也不认 :append。
/// </summary>
[Category("Host")]
public class PortFoundFixesTests
{
    private string _dir;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cleat-fixes-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_dir, true); } catch (Exception) { }
    }

    private string Eval(params string[] exprs)
    {
        var args = new string[exprs.Length * 2];
        for (int i = 0; i < exprs.Length; i++) { args[2 * i] = "-e"; args[2 * i + 1] = exprs[i]; }
        var r = HostProcessTests.CleatIn(_dir, null, null, args);
        Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
        return r.Out;
    }

    [Test]
    public void DefsFromMacrosRecordTheMacroCallsPosition()
    {
        string src = Path.Combine(_dir, "pos.clj");
        File.WriteAllText(src, "(ns pos)\n\n(defn f [] 1)\n\n  (defmacro mydef [n] (list 'def n 1))\n(mydef b)\n(def c 2)\n");
        string output = Eval(
            "(load-file \"pos.clj\")",
            // defmacro 展开成顶层 (do (defn mydef …) …)：do 的子形式也带位置
            "(prn (mapv #(select-keys (meta %) [:line :column]) [#'pos/f #'pos/b #'pos/c #'pos/mydef]))",
            "(prn (pos? (:line (meta #'clojure.core/map))))",
            // 用户层面的 macroexpand 结果仍不带元数据（与 JVM 相同）
            "(prn (meta (macroexpand '(defn g [] 1))))");
        Assert.That(output, Does.Contain("[{:line 3, :column 1} {:line 6, :column 1} {:line 7, :column 1} {:line 5, :column 3}]"));
        Assert.That(output, Does.Contain("true\nnil"));
    }

    [Test]
    public void ProtocolMethodsWhoseNamesStartWithADash()
    {
        string output = Eval(
            "(defprotocol P (-m [x y]))",
            "(deftype R [] P (-m [_ y] y))",
            "(defrecord Q [] P (-m [_ y] (* 2 y)))",
            "(extend-protocol P String (-m [s y] (str s y)))",
            "(prn [(-m (R.) 1) (-m (->Q) 2) (-m \"a\" 3)])",
            // 字段访问的 -name 照旧
            "(deftype F [a])",
            "(prn [(.-a (F. 5)) (. (F. 6) -a)])");
        Assert.That(output, Does.Contain("[1 4 \"a3\"]"));
        Assert.That(output, Does.Contain("[5 6]"));
    }

    [Test]
    public void DerefWithTimeoutRethrowsTheFuturesError()
    {
        string output = Eval(
            "(prn (try (deref (future (throw (Exception. \"boom\"))) 5000 :timeout) (catch Exception e (.Message (.InnerException e)))))",
            "(prn (deref (future (System.Threading.Thread/Sleep 2000) :late) 10 :timeout))",
            "(prn (deref (future 42) Int64/MaxValue :timeout))");
        Assert.That(output, Does.Contain("\"boom\"\n:timeout\n42"));
    }

    [Test]
    public void ReaderExceptionsCarryTheirCausesMessage()
    {
        string output = Eval("(prn (try (read-string \")\") (catch Exception e (.Message e))))");
        Assert.That(output, Does.Contain("Unmatched delimiter: )"));
    }

    [Test]
    public void SpitTruncatesAndAppends()
    {
        string output = Eval(
            "(spit \"s.txt\" \"hello world\")",
            "(spit \"s.txt\" \"ab\")",
            "(prn (slurp \"s.txt\"))",
            "(spit \"s.txt\" \"cd\" :append true)",
            "(prn (slurp \"s.txt\"))");
        Assert.That(output, Does.Contain("\"ab\"\n\"abcd\""));
    }
}
