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
/// 测试原写于 clojure.core 之前（只用特殊形式、def、fn*、互操作与 cleat.kernel 原语）；cleat-053 起宿主自动加载 Clojure 镜像
/// src/boot/cleat.boot.dll，打印走 print-method，REPL 照 clojure.main 做 repl-requires。
/// </summary>
[Category("Host")]
public class HostProcessTests
{
    private string _dir;

    private static string TestDir => TestContext.CurrentContext.TestDirectory;
    internal static string RepoDir => Path.GetFullPath(Path.Combine(TestDir, "..", "..", "..", "..", ".."));
    internal static string HostDll =>
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

    internal static Result CleatIn(string dir, string stdin, (string, string)[] env, params string[] args) =>
        CleatInWithTimeout(dir, stdin, env, 180_000, args);

    internal static Result CleatInWithTimeout(string dir, string stdin, (string, string)[] env, int timeoutMs, params string[] args)
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
        if (!p.WaitForExit(timeoutMs))
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
        // clojure.core 在镜像里（cleat-053）：值按 print-method 打印（#'user/x、命名空间与函数为 #object[…]）
        string[] expected =
        {
            "^user=> #'user/x$",
            "^user=> \"42\"$",
            "^user=> 1$",
            "^user=> 1$",
            "^user=> \"42\"$",
            "^user=> user=> user=> \"Divide by zero\"$",
            "^user=> #object\\[Namespace 0x[0-9a-f]+ \"foo\"\\]$",
            "^foo=> #'foo/y$",
            "^foo=> 2$",
            "^foo=> #object\\[Namespace 0x[0-9a-f]+ \"user\"\\]$",
            "^user=> #object\\[user\\$fn__\\d+ 0x[0-9a-f]+ \"user\\$fn__\\d+\"\\]$",
        };
        for (int i = 0; i < expected.Length; i++)
            Assert.That(lines[i], Does.Match(expected[i]), r.ToString());
        Assert.That(r.Err, Does.Contain("Syntax error compiling at (REPL:0:0).\nUnable to resolve symbol: nope in this context"));
        Assert.That(r.Err, Does.Contain("Execution error (ArithmeticException) at (REPL:7).\nDivide by zero"));
        Assert.That(r.Err, Does.Contain("Syntax error reading source at (REPL:"));
        Assert.That(r.Err, Does.Contain("EOF while reading"));
    }

    /// <summary>cleat-030 的验收（cleat-053 起 clojure.core 在 Clojure 镜像里）：REPL 里 require clojure.string 并调用；doc 来自 repl-requires。</summary>
    [Test]
    public void ReplRequireClojureString()
    {
        var r = Cleat("(require 'clojure.string)\n(clojure.string/upper-case \"a\")\n(str/join \",\" [1 2])\n(require '[clojure.string :as str])\n(str/join \",\" [1 2])\n(with-out-str (doc inc))\n");
        Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
        var lines = r.Out.Split('\n');
        Assert.That(lines[0], Is.EqualTo("user=> nil"), r.ToString());
        Assert.That(lines[1], Is.EqualTo("user=> \"A\""), r.ToString());
        Assert.That(lines[2], Is.EqualTo("user=> user=> nil"), r.ToString());   // str/ 未定义别名：语法错误，然后 require :as
        Assert.That(lines[3], Is.EqualTo("user=> \"1,2\""), r.ToString());
        Assert.That(lines[4], Does.StartWith("user=> \"-------------------------\\nclojure.core/inc"), r.ToString());
        Assert.That(r.Err, Does.Contain("No such namespace: str"), r.ToString());
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
        Assert.That(r.Out, Is.EqualTo("3\n3\n#'user/z\n[5 \"System.Int64\"]\n"));
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
        Assert.That(e.Err, Does.Contain("Execution error (ArithmeticException) at (bad.clj:3).\nDivide by zero"), "paths under the current directory are relative");
    }

    [Test]
    public void ErrorMessagesAreClojureStyle()
    {
        var unresolved = Cleat(null, "-e", "(nope 1)");
        Assert.That(unresolved.ExitCode, Is.EqualTo(1), unresolved.ToString());
        Assert.That(unresolved.Err.Trim(), Is.EqualTo("Syntax error compiling at (REPL:1:1).\nUnable to resolve symbol: nope in this context"));

        var runtime = Cleat(null, "-e", "(def boom (fn* [] (. clojure.lang.RT (nth [1 2] 5))))", "-e", "(boom)");
        Assert.That(runtime.ExitCode, Is.EqualTo(1), runtime.ToString());
        Assert.That(runtime.Err, Does.StartWith("Execution error (ArgumentOutOfRangeException) at (REPL:1).\n"));

        string file = Write("syn.clj", "(def ok 1)\n(def bad (fn* [] undefined-thing))\n");
        var syntax = Cleat(null, file);
        Assert.That(syntax.ExitCode, Is.EqualTo(1), syntax.ToString());
        Assert.That(syntax.Err.Trim(), Does.Match("^" + Regex.Escape("Syntax error compiling at (syn.clj:2:") + @"\d+\)\.\nUnable to resolve symbol: undefined-thing in this context$"));
    }

    [Test]
    public void LoadFileSwitchesNamespace()
    {
        Write("other.clj", "(in-ns 'other)\n(def z 3)\n(def here clojure.core/*ns*)\n(do (in-ns 'other2) (def w (cleat.kernel/add other/z 1)))\n");
        var r = Cleat(null, "-e", "(load-file \"other.clj\")", "-e", "[(. clojure.core/*ns* Name) other/z (. other/here Name) other2/w]");
        Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
        Assert.That(r.Out, Is.EqualTo("#'other2/w\n[user 3 other 4]\n"));
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
        Assert.That(ifcText, Does.Contain("{:var my.lib/add2, :class \"my.lib$add2, my.lib.clj\", :arities [2], :static true}"));
        Assert.That(ifcText, Does.Contain("{:var my.lib/multi, :class \"my.lib$multi, my.lib.clj\", :arities [0 1 -2], :static true}"));
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
    public void ReportTargetFollowsClojureMainSemantics()
    {
        // cleat-034：错误报告默认写 stderr，--report stdio 时写 stdout（clojure.main 的 --report 语义）
        var e = Cleat(null, "-e", "(throw (ex-info \"boom\" {:k 1}))");
        Assert.That(e.ExitCode, Is.Not.EqualTo(0), e.ToString());
        Assert.That(e.Err, Does.Contain("boom"));
        Assert.That(e.Out, Is.Empty);
        var s = Cleat(null, "--report", "stdio", "-e", "(throw (ex-info \"boom\" {:k 1}))");
        Assert.That(s.ExitCode, Is.Not.EqualTo(0), s.ToString());
        Assert.That(s.Out, Does.Contain("boom"));
        Assert.That(s.Err, Does.Not.Contain("boom"));
    }

    [Test, Category("Bootstrap"), NonParallelizable]
    public void PublishedDirectoryRunsStandalone()
    {
        // cleat-034 的验收：tools/build.cs publish 的目录在宿主之外可运行（-e、脚本、随发布的 AOT 库）。
        // 无 SDK 机器上的运行在验收时用无 .NET 的容器验证过；这里冒烟发布产物本身。
        string outDir = Path.Combine(_dir, "pub");
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            WorkingDirectory = RepoDir,
        };
        psi.ArgumentList.Add("dotnet");
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("tools/build.cs");
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add("publish");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(outDir);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("cannot start dotnet");
        string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.That(p.ExitCode, Is.EqualTo(0), output);
        Assert.That(File.Exists(Path.Combine(outDir, "cleat")) || File.Exists(Path.Combine(outDir, "cleat.exe")), Is.True, output);
        Assert.That(File.Exists(Path.Combine(outDir, "cleat.boot.dll")), Is.True, "Clojure 镜像随发布");
        Assert.That(File.Exists(Path.Combine(outDir, "cleat.scheme.boot.dll")), Is.True, "Scheme 镜像随发布");
        Assert.That(File.Exists(Path.Combine(outDir, "clojure.core.server.clj.dll")), Is.True, "AOT 库随发布");
        string cleat = Path.Combine(outDir, OperatingSystem.IsWindows() ? "cleat.exe" : "cleat");
        var r = CleatIn(outDir, null, null, "-e", "(+ 1 2)", "-e", "(require 'clojure.test.tap)", "-e", ":published-ok");
        Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
        var lines = r.Out.TrimEnd('\n').Split('\n');
        Assert.That(lines[0], Is.EqualTo("3"), r.ToString());
        Assert.That(lines[^1], Is.EqualTo(":published-ok"), r.ToString());
    }

    [Test]
    public void StdinScript()
    {
        var r = Cleat("(def a 2)\n(. System.Console (WriteLine (cleat.kernel/add a 3)))\n", "-");
        Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
        Assert.That(r.Out, Is.EqualTo("5\n"));
    }

    [Test]
    public void DirectLinkingAcrossUnitsAndWithRedefs()
    {
        // cleat-033：A 先编译成 dll；B 在 A 的接口文件已登记后编译，对 A 的已知函数直接 call invokeStatic
        // （boot 镜像的函数同样如此）。直接调用带守卫：with-redefs / 重定义把根换成别的函数类时退回普通
        // IFn.invoke，重定义的语义不丢。
        Write("lp2/link/a.clj", "(ns link.a)\n(. System.Console (WriteLine \"loading link.a\"))\n(defn inc2 [x] (clojure.core/inc x))\n");
        var ca = Cleat(null, "-I", "lp2", "-e", "(binding [*compile-files* true] (compile 'link.a))");
        Assert.That(ca.ExitCode, Is.EqualTo(0), ca.ToString());
        Assert.That(File.ReadAllText(Path.Combine(_dir, "lp2", "link.a.clj.ifc")), Does.Contain(":static true"), "接口文件登记 invokeStatic");

        Write("lp2/link/b.clj", "(ns link.b\n  (:require [link.a]))\n(defn twice [x] (link.a/inc2 (link.a/inc2 x)))\n");
        var cb = Cleat(null, "-I", "lp2", "-e", "(require 'link.a)", "-e", "(binding [*compile-files* true] (compile 'link.b))");
        Assert.That(cb.ExitCode, Is.EqualTo(0), cb.ToString());
        byte[] bDll = File.ReadAllBytes(Path.Combine(_dir, "lp2", "link.b.clj.dll"));
        Assert.That(Encoding.ASCII.GetString(bDll), Does.Contain("invokeStatic"), "B 的 IL 里有直接调用");

        var r = Cleat(null, "-I", "lp2", "-e", "(require 'link.b)", "-e", "[(link.b/twice 1)]");
        Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
        Assert.That(r.Out.TrimEnd('\n').Split('\n')[^1], Is.EqualTo("[3]"), r.ToString());

        // with-redefs 换掉 link.a/inc2 的根：受保护的直接调用退回普通调用，看到新根
        var w = Cleat(null, "-I", "lp2", "-e", "(require 'link.b)",
            "-e", "(with-redefs [link.a/inc2 (fn* [x] (clojure.core/* 10 x))] (link.b/twice 1))");
        Assert.That(w.ExitCode, Is.EqualTo(0), w.ToString());
        Assert.That(w.Out.TrimEnd('\n').Split('\n')[^1], Is.EqualTo("100"), w.ToString());

        // :reload 重新执行 dll 的入口（副作用再发生一次）
        var rl = Cleat(null, "-I", "lp2", "-e", "(require 'link.a)", "-e", "(require 'link.a :reload)");
        Assert.That(rl.ExitCode, Is.EqualTo(0), rl.ToString());
        Assert.That(Regex.Matches(rl.Out, "loading link.a").Count, Is.EqualTo(2), rl.ToString());
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
