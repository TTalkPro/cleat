using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Cleat.Host.Tests;

/// <summary>
/// cleat-052：宿主的进程级测试。每个测试在自己的临时目录里以子进程运行 <c>dotnet cleat.dll …</c>（工作区的 Scheme 镜像由宿主自动找到），
/// clojure.core 尚未存在（cleat-053），所以只用特殊形式、def、fn*、互操作与 cleat.kernel 原语。
/// </summary>
[Category("Host")]
public class HostProcessTests
{
    private string _dir;

    private static string TestDir => TestContext.CurrentContext.TestDirectory;
    private static string RepoDir => Path.GetFullPath(Path.Combine(TestDir, "..", "..", "..", "..", ".."));
    private static string HostDll =>
      Path.Combine(RepoDir, "src", "Cleat.Host", "bin", new DirectoryInfo(TestDir).Parent.Name, new DirectoryInfo(TestDir).Name, "cleat.dll");

    internal sealed class Result
    {
        public string Out, Err;
        public int ExitCode;
        public TimeSpan Elapsed;
        public override string ToString() => $"exit {ExitCode} in {Elapsed.TotalMilliseconds:F0} ms\n--- stdout:\n{Out}\n--- stderr:\n{Err}";
    }

    /// <summary>运行 cleat；stdin 为 null 时标准输入立即关闭。</summary>
    private Result Cleat(string stdin, params string[] args) => CleatIn(_dir, stdin, null, args);

    private static Result CleatIn(string dir, string stdin, (string, string)[] env, params string[] args)
    {
        Assert.That(File.Exists(HostDll), Is.True, "host not built: " + HostDll);
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = dir,
        };
        psi.ArgumentList.Add(HostDll);
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment.Remove("CLEAT_NO_SIMPLE_EVAL");
        foreach (var (k, v) in env ?? Array.Empty<(string, string)>()) psi.Environment[k] = v;
        var sw = Stopwatch.StartNew();
        using var p = Process.Start(psi);
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        if (stdin != null) p.StandardInput.Write(stdin);
        p.StandardInput.Close();
        if (!p.WaitForExit(180_000))
        {
            try { p.Kill(true); } catch (Exception) { }
            Assert.Fail("cleat timed out: " + string.Join(" ", args));
        }
        p.WaitForExit();
        var r = new Result { Out = outTask.Result.Replace("\r\n", "\n"), Err = errTask.Result.Replace("\r\n", "\n"), ExitCode = p.ExitCode, Elapsed = sw.Elapsed };
        TestContext.Out.WriteLine("$ cleat " + string.Join(" ", args));
        TestContext.Out.WriteLine(r.ToString());
        return r;
    }

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cleat-host-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_dir, true); } catch (Exception) { }
    }

    private string Write(string rel, string text)
    {
        string path = Path.Combine(_dir, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, text);
        return path;
    }

    // 编译一个库（*compile-files* 为真时 RT.load 经 compile 钩子）：clojure.core 的 compile 之前的写法
    private static string CompileExpr(string lib) =>
      "(do (. clojure.lang.Var (pushThreadBindings {(var clojure.core/*compile-files*) true})) " +
      $"(try (clojure.lang.RT/load \"{lib}\") (finally (. clojure.lang.Var (popThreadBindings)))))";

    private const string Lib = @"(ns my.lib)
(. System.Console (WriteLine ""loading my.lib""))
(def answer 42)
(def add2 (fn* add2 [a b] (cleat.kernel/add a b)))
(def multi (fn* ([] :none) ([a] [:one a]) ([a & r] [:many a r])))
(def ^:dynamic *level* 1)
(def counter (fn* [] (let* [n 3] (loop* [i 0] (if (cleat.kernel/lt i n) (recur (cleat.kernel/inc i)) i)))))
(deftype* my.lib/Point my.lib.Point [x y] :implements [clojure.lang.IDeref] (deref [_] [x y]))
";

    private const string UseLib =
      "[my.lib/answer (my.lib/add2 1 2) (my.lib/multi 1 2 3) (my.lib/counter) (. (new my.lib.Point 1 2) deref) (. (var my.lib/*level*) isDynamic)]";
    private const string UseLibResult = "[42 3 [:many 1 (2 3)] 3 [1 2] true]";

    private const string AssemblyOfAdd2 = "(. (. (. (. my.lib/add2 GetType) Assembly) GetName) Name)";

    [Test]
    public void ReplTranscript()
    {
        string input = string.Join("\n",
            "(def x 1)",
            "((fn* [a] (. a ToString)) 42)",
            "x",
            "*1",
            "*3",
            "nope",
            "(. clojure.lang.Numbers (divide 1 0))",
            "(. *e Message)",
            "(in-ns 'foo)",
            "(def y (cleat.kernel/add user/x 1))",
            "y",
            "(clojure.core/in-ns 'user)",
            "(fn* [a] a)",
            "[1 2",
            "") ;
        var r = Cleat(input);
        Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
        var lines = r.Out.Split('\n');
        string[] expected =
        {
            "user=> #=(var user/x)",
            "user=> \"42\"",
            "user=> 1",
            "user=> 1",
            "user=> \"42\"",
            "user=> user=> user=> \"Divide by zero\"",
            "user=> foo",
            "foo=> #=(var foo/y)",
            "foo=> 2",
            "foo=> user",
        };
        Assert.That(lines.Take(expected.Length), Is.EqualTo(expected), r.ToString());
        Assert.That(lines[expected.Length], Does.Match(@"^user=> user\$fn__\d+$"), r.ToString());
        Assert.That(r.Err, Does.Contain("Syntax error (NO_SOURCE_PATH:0:0) Unable to resolve symbol: nope in this context"));
        Assert.That(r.Err, Does.Contain("Execution error (ArithmeticException) at (REPL:7:1).\nDivide by zero"));
        Assert.That(r.Err, Does.Contain("Syntax error reading source at (REPL:"));
        Assert.That(r.Err, Does.Contain("EOF while reading"));
    }

    [Test]
    public void ReplQuitAndStartupTime()
    {
        var first = CleatIn(_dir, ":repl/quit\n", new[] { ("CLEAT_TIME", "1") });
        Assert.That(first.ExitCode, Is.EqualTo(0), first.ToString());
        Assert.That(first.Out, Is.EqualTo("user=> "));
        // 第二次计时（第一次有磁盘缓存的影响）
        var r = CleatIn(_dir, ":repl/quit\n", new[] { ("CLEAT_TIME", "1") });
        Assert.That(r.Err, Does.Match(@"cleat: Scheme image \d+ ms, driver libraries \(fasl\) \d+ ms"), "the precompiled driver libraries should be used: " + r);
        TestContext.Out.WriteLine($"startup to first prompt and exit: {r.Elapsed.TotalMilliseconds:F0} ms ({r.Err.Trim()})");
    }

    [Test]
    public void EvalOptionPrintsEachNonNilValue()
    {
        var r = Cleat(null, "-e", "(cleat.kernel/add 1 2) nil (. \"abc\" Length)", "-e", "(def z 5)", "-e", "[z (. (. z GetType) FullName)]");
        Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
        Assert.That(r.Out, Is.EqualTo("3\n3\n#=(var user/z)\n[5 \"System.Int64\"]\n"));
    }

    [Test]
    public void ScriptFileWithArguments()
    {
        string script = Write("script.clj",
            "(def greet (fn* [n] (. System.String (Concat \"hello \" n))))\n" +
            "(. System.Console (WriteLine (greet (. *command-line-args* first))))\n");
        var r = Cleat(null, script, "world", "x");
        Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
        Assert.That(r.Out, Is.EqualTo("hello world\n"));

        string bad = Write("bad.clj", "(def a 1)\n\n  (. clojure.lang.Numbers (divide a 0))\n");
        var e = Cleat(null, bad);
        Assert.That(e.ExitCode, Is.EqualTo(1), e.ToString());
        Assert.That(e.Err, Does.Contain($"Execution error (ArithmeticException) at ({bad}:3:3).\nDivide by zero"));
    }

    [Test]
    public void ErrorMessagesAreClojureStyle()
    {
        var unresolved = Cleat(null, "-e", "(nope 1)");
        Assert.That(unresolved.ExitCode, Is.EqualTo(1), unresolved.ToString());
        Assert.That(unresolved.Err.Trim(), Is.EqualTo("Syntax error (NO_SOURCE_PATH:1:1) Unable to resolve symbol: nope in this context"));

        var runtime = Cleat(null, "-e", "(def boom (fn* [] (. clojure.lang.RT (nth [1 2] 5))))", "-e", "(boom)");
        Assert.That(runtime.ExitCode, Is.EqualTo(1), runtime.ToString());
        Assert.That(runtime.Err, Does.StartWith("Execution error (ArgumentOutOfRangeException) at (REPL:1:1).\n"));

        string file = Write("syn.clj", "(def ok 1)\n(def bad (fn* [] undefined-thing))\n");
        var syntax = Cleat(null, file);
        Assert.That(syntax.ExitCode, Is.EqualTo(1), syntax.ToString());
        Assert.That(syntax.Err.Trim(), Does.Match("^" + Regex.Escape($"Syntax error ({file}:2:") + @"\d+\) Unable to resolve symbol: undefined-thing in this context$"));
    }

    [Test]
    public void LoadFileSwitchesNamespace()
    {
        Write("other.clj", "(in-ns 'other)\n(def z 3)\n(def here clojure.core/*ns*)\n(do (in-ns 'other2) (def w (cleat.kernel/add other/z 1)))\n");
        var r = Cleat(null, "-e", "(load-file \"other.clj\")", "-e", "[clojure.core/*ns* other/z other/here other2/w]");
        Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
        Assert.That(r.Out, Is.EqualTo("#=(var other2/w)\n[user 3 other 4]\n"));
    }

    [Test]
    public void RequireFromSourceOnTheLoadPath()
    {
        Write("lp/my/lib.clj", Lib);
        var r = Cleat(null, "-I", "lp", "-e", "(clojure.lang.RT/load \"my/lib\")", "-e", UseLib, "-e", AssemblyOfAdd2,
            "-e", "(. Cleat.Core.Runtime.Loader/LoadedAssemblies (ContainsKey \"my.lib.clj\"))");
        Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
        var lines = r.Out.TrimEnd('\n').Split('\n');
        Assert.That(lines[0], Is.EqualTo("loading my.lib"));
        Assert.That(lines[1], Is.EqualTo(UseLibResult));
        Assert.That(lines[2], Does.StartWith("\"eval$"), "loaded from source: fn classes live in eval assemblies");
        Assert.That(lines[3], Is.EqualTo("false"));
    }

    [Test]
    public void CompileToCljDllThenLoadInAFreshProcess()
    {
        string src = Write("lp/my/lib.clj", Lib);
        var c = Cleat(null, "-I", "lp", "-e", CompileExpr("my/lib"));
        Assert.That(c.ExitCode, Is.EqualTo(0), c.ToString());
        Assert.That(c.Out, Is.EqualTo("loading my.lib\n"), "compiling also evaluates each form");
        string dll = Path.Combine(_dir, "lp", "my.lib.clj.dll"), ifc = Path.Combine(_dir, "lp", "my.lib.clj.ifc");
        Assert.That(File.Exists(dll), Is.True, "no " + dll);
        Assert.That(File.Exists(ifc), Is.True, "no " + ifc);
        string ifcText = File.ReadAllText(ifc);
        Assert.That(ifcText, Does.Contain(":assembly \"my.lib.clj\""));
        Assert.That(ifcText, Does.Contain("{:var my.lib/add2, :class \"my.lib$add2, my.lib.clj\", :arities [2], :static false}"));
        Assert.That(ifcText, Does.Contain("{:var my.lib/multi, :class \"my.lib$multi, my.lib.clj\", :arities [0 1 -2], :static false}"));
        Assert.That(ifcText, Does.Not.Contain("my.lib/*level*"), "dynamic Vars are not known functions");

        // 新进程：加载器选 dll（不比源码旧），入口执行全部顶层形式（含副作用），接口文件按 MVID 登记
        var r = Cleat(null, "-I", "lp", "-e", "(clojure.lang.RT/load \"my/lib\")", "-e", UseLib, "-e", AssemblyOfAdd2,
            "-e", "(. Cleat.Core.Runtime.Loader/LoadedAssemblies (ContainsKey \"my.lib.clj\"))",
            "-e", "(. (. Cleat.Core.Runtime.InterfaceFile (FindKnown 'my.lib/multi)) ClassName)",
            "-e", "(. (. (. (new my.lib.Point 1 2) GetType) Assembly) FullName)");
        Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
        Assert.That(r.Out.TrimEnd('\n').Split('\n'), Is.EqualTo(new[]
        {
            "loading my.lib",
            UseLibResult,
            "\"my.lib.clj\"",
            "true",
            "\"my.lib$multi, my.lib.clj\"",
            "\"my.lib.clj, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null\"",
        }), r.ToString());

        // 源码比 dll 新：回到源码
        File.SetLastWriteTimeUtc(src, File.GetLastWriteTimeUtc(dll).AddMinutes(1));
        var s = Cleat(null, "-I", "lp", "-e", "(clojure.lang.RT/load \"my/lib\")", "-e", AssemblyOfAdd2);
        Assert.That(s.ExitCode, Is.EqualTo(0), s.ToString());
        Assert.That(s.Out.Split('\n')[1], Does.StartWith("\"eval$"), s.ToString());
    }

    [Test]
    public void DllCompiledAgainstAnotherClojureImageFallsBackToSource()
    {
        // 两个最小的 .clj.dll 充当两个不同的 Clojure 镜像（--boot 加载的是任何 Cleat 模块；镜像 id 为其 MVID）
        Write("img/image_a.clj", "(def image :a)\n");
        Write("img/image_b.clj", "(def image :b)\n");
        var images = Cleat(null, "-I", "img", "-e", CompileExpr("image_a"), "-e", CompileExpr("image_b"));
        Assert.That(images.ExitCode, Is.EqualTo(0), images.ToString());
        string a = Path.Combine(_dir, "img", "image_a.clj.dll"), b = Path.Combine(_dir, "img", "image_b.clj.dll");

        Write("lp/my/lib.clj", Lib);
        var c = Cleat(null, "--boot", a, "-I", "lp", "-e", CompileExpr("my/lib"));
        Assert.That(c.ExitCode, Is.EqualTo(0), c.ToString());

        var same = Cleat(null, "--boot", a, "-I", "lp", "-e", "(clojure.lang.RT/load \"my/lib\")", "-e", AssemblyOfAdd2);
        Assert.That(same.ExitCode, Is.EqualTo(0), same.ToString());
        Assert.That(same.Out, Is.EqualTo("loading my.lib\n\"my.lib.clj\"\n"));
        Assert.That(same.Err, Is.Empty);

        var other = Cleat(null, "--boot", b, "-I", "lp", "-e", "(clojure.lang.RT/load \"my/lib\")", "-e", UseLib, "-e", AssemblyOfAdd2);
        Assert.That(other.ExitCode, Is.EqualTo(0), other.ToString());
        Assert.That(other.Err, Does.Contain("warning: my.lib.clj.dll: module was compiled against boot image"));
        Assert.That(other.Err, Does.Contain("loading lib.clj instead"));
        var lines = other.Out.TrimEnd('\n').Split('\n');
        Assert.That(lines[^2], Is.EqualTo(UseLibResult));
        Assert.That(lines[^1], Does.StartWith("\"eval$"));
    }

    [Test]
    public void StdinScript()
    {
        var r = Cleat("(def a 2)\n(. System.Console (WriteLine (cleat.kernel/add a 3)))\n", "-");
        Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
        Assert.That(r.Out, Is.EqualTo("5\n"));
    }

    [Test]
    public void SimpleFormsAreNotCompiled()
    {
        var r = Cleat(null, "-e", "(def a 1) (def b [a 2]) (cleat.kernel/add a 1) (. \"x\" Length) (let* [k :k] (k {:k 1}))",
            "-e", "[(. Cleat.Scheme.Clojure.Driver SimpleCount) (. Cleat.Scheme.Clojure.Driver CompiledCount)]",
            "-e", "((fn* [] 1))",
            "-e", "(. Cleat.Scheme.Clojure.Driver CompiledCount)");
        Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
        var lines = r.Out.TrimEnd('\n').Split('\n');
        Assert.That(lines[^3], Is.EqualTo("[6 0]"), "five simple forms (plus the vector itself) and nothing compiled: " + r);
        Assert.That(lines[^1], Is.EqualTo("1"));
    }
}
