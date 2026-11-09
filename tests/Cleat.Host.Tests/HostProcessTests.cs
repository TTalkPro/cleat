using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Cleat.Host.Tests;

/// <summary>
/// cleat-052：宿主的进程级测试。每个测试在自己的临时目录里以子进程运行 <c>dotnet cleat.dll …</c>（工作区的镜像与编译器包由宿主自动找到），
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

    /// <summary>
    /// REPL 的 Tab 补全（cleat-034）认别名：(require '[clojure.string :as cstr]) 之后 cstr/ 补出 clojure.string 的公有 Var（保留 cstr/ 前缀），
    /// 不含 refer 进来的 clojure.core 函数与私有 Var；cst 补出 cstr/；参数提示也认别名。
    /// </summary>
    [Test]
    public void ReplCompletionResolvesAliases()
    {
        var r = Cleat(null, "-e", "(require '[clojure.string :as cstr])",
            "-e", "(vec (Cleat.Host.Program/ReplCompletions \"cstr/jo\"))",
            "-e", "(let [c (set (Cleat.Host.Program/ReplCompletions \"cstr/\"))] [(contains? c \"cstr/upper-case\") (contains? c \"cstr/map\") (some #(not (.StartsWith ^String % \"cstr/\")) c)])",
            "-e", "(vec (Cleat.Host.Program/ReplCompletions \"clojure.string/blan\"))",
            "-e", "(some #{\"cstr/\"} (Cleat.Host.Program/ReplCompletions \"cst\"))",
            "-e", "[(Cleat.Host.Program/ReplHint \"cstr/join\") (Cleat.Host.Program/ReplHint \"inc\") (Cleat.Host.Program/ReplHint \"nope/x\")]");
        Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
        // -e 不打印 nil；:arglists 是列表时提示也要有（cleat-162 顺带修：原来只认向量，defn 的函数从来没有提示）
        Assert.That(r.Out, Is.EqualTo("[\"cstr/join\"]\n[true false nil]\n[\"clojure.string/blank?\"]\n\"cstr/\"\n[\"[coll] [separator coll]\" \"[x]\" nil]\n"), r.ToString());
    }

    [Test]
    public void ReplQuitAndStartupTime()
    {
        var first = CleatIn(_dir, ":repl/quit\n", new[] { ("CLEAT_TIME", "1") });
        Assert.That(first.ExitCode, Is.EqualTo(0), first.ToString());
        Assert.That(first.Out, Is.EqualTo("user=> "));
        // 第二次计时（第一次有磁盘缓存的影响）
        var r = CleatIn(_dir, ":repl/quit\n", new[] { ("CLEAT_TIME", "1") });
        // 编译器是 C# 程序集（cleat-182 之前也可以是 Clojure 编译器的包 package / 源码 source）
        Assert.That(r.Err, Does.Match(@"cleat: Clojure image \d+ ms, compiler \(cs\) \d+ ms"), "the compiler (Cleat.Compiler) should be used: " + r);
        // cleat-159（29 §3.1 第 2 条的第三种场景）：REPL 走到首个提示符再补一行，量启动报告之后的那截
        // （repl-requires 与行编辑器/提示符本身）。first prompt 是从宿主计时起点算起的总时间。
        Assert.That(r.Err, Does.Match(@"cleat: repl setup \d+ ms \(user \d+ \+ repl-requires \d+ \+ prompt \d+\) = first prompt \d+ ms after start"),
                    "the REPL should report its own setup segment: " + r);
        // cleat-159：repl-requires 再按库细分（画像里 clojure.repl.deps 的失败尝试是最贵的一格）
        Assert.That(r.Err, Does.Match(@"cleat: repl-requires per lib: .*clojure\.repl.* ms.*clojure\.pprint.* ms.*clojure\.repl\.deps.* ms"),
                    "repl-requires should report per-library shares: " + r);
        TestContext.Out.WriteLine($"startup to first prompt and exit: {r.Elapsed.TotalMilliseconds:F0} ms ({r.Err.Trim()})");
    }

    /// <summary>
    /// cleat-159（designs/29 §3.1）：CLEAT_TIME 的分段计时。段：host（宿主入口与大栈线程）/ cli（命令行解析）/
    /// paths（找镜像与编译器包的路径探测）/ runtime（原语注册与 RT.Init）/ image（程序集 + 登记 + .ifc + 模块入口）/
    /// packages（镜像旁的包登记）/
    /// compiler（require 驱动 + install-hooks!）/ other；镜像的模块初始化再按 in-ns 边界分桶（boot-order 条目的份额）。
    /// 各段是整数毫秒的截断差，加总不超过 total（本测试核这一条）；未设置 CLEAT_TIME 时不输出（上面的用例）。
    /// </summary>
    [Test]
    public void StartupTimeSegments()
    {
        var r = CleatIn(_dir, null, new[] { ("CLEAT_TIME", "1") }, "-e", "nil");
        Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
        var segs = System.Text.RegularExpressions.Regex.Match(r.Err,
            @"cleat: segments host (\d+) / cli (\d+) / paths (\d+) / runtime (\d+) / image (\d+) \(assembly (\d+) \+ register (\d+) \+ ifc (\d+) \+ entry (\d+)\) / packages (\d+) / compiler (\d+) \(require (\d+) \+ hooks (\d+)\) / other (\d+) = total (\d+) ms");
        Assert.That(segs.Success, Is.True, "分段行没出来: " + r);
        long G(int i) => long.Parse(segs.Groups[i].Value);
        // 顶层段与子段各自自洽：image = assembly+register+ifc+entry，compiler = require+hooks
        // （tick 累计后每格单独截断成毫秒，子段之和可以比父段少几毫秒）
        Assert.That(G(5), Is.EqualTo(G(6) + G(7) + G(8) + G(9)).Within(4), "image 的子段: " + segs.Value);
        Assert.That(G(11), Is.EqualTo(G(12) + G(13)).Within(4), "compiler 的子段: " + segs.Value);
        long top = G(1) + G(2) + G(3) + G(4) + G(5) + G(10) + G(11) + G(14), total = G(15);
        Assert.That(top, Is.LessThanOrEqualTo(total), "各段都是截断差，加总不该超过 total: " + segs.Value);
        Assert.That(total - top, Is.LessThan(20), "漏计的时间不该超过 20 ms（8 个顶层段的截断）: " + segs.Value);
        // 镜像的模块初始化按 in-ns 边界分桶：core 与 pprint 都在镜像里
        Assert.That(r.Err, Does.Match(@"image module init per in-ns boundary \(top \d+ of \d+\):[^\n]*clojure\.core \d+ ms"), r.ToString());
        TestContext.Out.WriteLine(r.Err.Trim());
    }

    /// <summary>
    /// cleat-161（designs/29 §3.3）：编译器延迟加载。
    ///   1. 平凡形式（常量 / 字面量 / quote）不为编译器付那一格：CLEAT_TIME 的 compiler 段是 0，也没有补行；
    ///   2. 第一个要编译的形式按需加载，在启动报告之后补一行（require + hooks）；
    ///   3. CLEAT_NO_SIMPLE_EVAL 设置时快速路径关掉（与驱动侧的简单形式解释器同一口径）；
    ///   4. 只跑包程序集里的 AOT 代码不加载编译器。
    /// 第一行 <c>compiler (cs) N ms</c> 的格式不变（上面的 ReplQuitAndStartupTime 依赖它）。
    /// </summary>
    [Test]
    public void CompilerLoadsOnDemandOnly()
    {
        const string onDemand = @"cleat: compiler loaded on demand: cs \d+ ms \(require \d+ \+ hooks \d+\)";
        var trivial = CleatIn(_dir, null, new[] { ("CLEAT_TIME", "1") },
            "-e", "nil", "-e", "42", "-e", "[1 2]", "-e", "{:a 1}", "-e", ":kw", "-e", "'x", "-e", "()");
        Assert.That(trivial.ExitCode, Is.EqualTo(0), trivial.ToString());
        Assert.That(trivial.Err, Does.Match(@"compiler \(cs\) 0 ms"), trivial.ToString());
        Assert.That(trivial.Err, Does.Not.Contain("loaded on demand"), trivial.ToString());
        // nil 不打印（clojure.main 的口径）；空表求值还是它自己
        Assert.That(trivial.Out, Is.EqualTo("42\n[1 2]\n{:a 1}\n:kw\nx\n()\n"), trivial.ToString());

        var need = CleatIn(_dir, null, new[] { ("CLEAT_TIME", "1") }, "-e", "(+ 1 2)");
        Assert.That(need.Out.Trim(), Is.EqualTo("3"), need.ToString());
        Assert.That(need.Err, Does.Match(onDemand), need.ToString());

        var noSimple = CleatIn(_dir, null, new[] { ("CLEAT_TIME", "1"), ("CLEAT_NO_SIMPLE_EVAL", "1") }, "-e", "nil");
        Assert.That(noSimple.Err, Does.Match(onDemand), noSimple.ToString());

        // 包程序集里的 ns（验收 2）：-m 跑它的 -main，全程不加载编译器。
        // （-e 的调用形式要 eval，编译器总要加载，那条路测不出这个口径。）
        Write("lp/my/lib.clj", Lib + "(defn -main [& args] (println \"main from the package\" (count args)))\n");
        var built = Cleat(null, "-I", "lp", "--compile-package", Path.Combine(_dir, "lp", "my.lib.dll"),
            "--package", "my.lib", "--root", Path.Combine(_dir, "lp"), "my.lib");
        Assert.That(built.ExitCode, Is.EqualTo(0), built.ToString());
        var aot = CleatIn(_dir, null, new[] { ("CLEAT_TIME", "1") }, "-I", "lp", "-m", "my.lib", "a", "b");
        Assert.That(aot.ExitCode, Is.EqualTo(0), aot.ToString());
        Assert.That(aot.Err, Does.Not.Contain("loaded on demand"), aot.ToString());
        Assert.That(aot.Out, Does.Contain("main from the package 2"), aot.ToString());
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

    /// <summary>包程序集里嵌入的资源（cleat-067：接口文件 cleat.ifc）的文本；没有时 null。按元数据读，不载入程序集。</summary>
    public static string EmbeddedResource(string dll, string name)
    {
        using var pe = new System.Reflection.PortableExecutable.PEReader(File.OpenRead(dll));
        var md = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
        foreach (var h in md.ManifestResources)
        {
            var r = md.GetManifestResource(h);
            if (md.GetString(r.Name) != name) continue;
            var section = pe.GetSectionData(pe.PEHeaders.CorHeader!.ResourcesDirectory.RelativeVirtualAddress);
            var reader = section.GetReader((int)r.Offset, section.Length - (int)r.Offset);
            int length = reader.ReadInt32();
            return Encoding.UTF8.GetString(reader.ReadBytes(length));
        }
        return null;
    }

    [Test]
    public void CompileToPackageAssemblyThenLoadInAFreshProcess()
    {
        Write("lp/my/lib.clj", Lib);
        var c = Cleat(null, "-I", "lp", "-e", CompileExpr("my/lib"));
        Assert.That(c.ExitCode, Is.EqualTo(0), c.ToString());
        Assert.That(c.Out, Is.EqualTo("loading my.lib\n"), "compiling also evaluates each form");
        // cleat-067：一个包程序集（my/lib.clj → my.lib.dll），接口文件嵌在里面，旁边没有 .ifc
        string dll = Path.Combine(_dir, "lp", "my.lib.dll");
        Assert.That(File.Exists(dll), Is.True, "no " + dll);
        Assert.That(File.Exists(Path.Combine(_dir, "lp", "my.lib.ifc")), Is.False);
        string ifcText = EmbeddedResource(dll, "cleat.ifc");
        Assert.That(ifcText, Is.Not.Null, "the interface file is embedded");
        Assert.That(ifcText, Does.Contain(":assembly \"my.lib\""));
        Assert.That(ifcText, Does.Contain("{:var my.lib/add2, :class \"my.lib$add2, my.lib\", :arities [2], :static true}"));
        Assert.That(ifcText, Does.Contain("{:var my.lib/multi, :class \"my.lib$multi, my.lib\", :arities [0 1 -2], :static true}"));
        Assert.That(ifcText, Does.Not.Contain("my.lib/*level*"), "dynamic Vars are not known functions");

        // 新进程：-I 目录里的包程序集登记了 my/lib，require 跑它的入口（含副作用），嵌入的接口文件登记
        var r = Cleat(null, "-I", "lp", "-e", "(clojure.lang.RT/load \"my/lib\")", "-e", UseLib, "-e", AssemblyOfAdd2,
            "-e", "(. Cleat.Core.Runtime.Loader/LoadedAssemblies (ContainsKey \"my.lib\"))",
            "-e", "(. (. Cleat.Core.Runtime.InterfaceFile (FindKnown 'my.lib/multi)) ClassName)",
            "-e", "(. (. (. (new my.lib.Point 1 2) GetType) Assembly) FullName)");
        Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
        Assert.That(r.Out.TrimEnd('\n').Split('\n'), Is.EqualTo(new[]
        {
            "loading my.lib",
            UseLibResult,
            "\"my.lib\"",
            "true",
            "\"my.lib$multi, my.lib\"",
            "\"my.lib, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null\"",
        }), r.ToString());

        // 没有包程序集时从源码加载
        File.Delete(dll);
        var s = Cleat(null, "-I", "lp", "-e", "(clojure.lang.RT/load \"my/lib\")", "-e", AssemblyOfAdd2);
        Assert.That(s.ExitCode, Is.EqualTo(0), s.ToString());
        Assert.That(s.Out.Split('\n')[1], Does.StartWith("\"eval$"), s.ToString());
    }

    [Test]
    public void PackageCompiledAgainstAnotherClojureImageIsRejected()
    {
        // 另一个"镜像"：任何 Cleat 模块的 MVID 都可以当镜像 id（--target-image，cleat-091 自举时给下一级镜像编包用）；
        // 包程序集的入口核对正在运行的镜像的 id
        Write("img/image_b.clj", "(def image :b)\n");
        var images = Cleat(null, "-I", "img", "-e", CompileExpr("image_b"));
        Assert.That(images.ExitCode, Is.EqualTo(0), images.ToString());
        string b = Path.Combine(_dir, "img", "image_b.dll");

        Write("lp/my/lib.clj", Lib);
        var c = Cleat(null, "-I", "lp", "--compile-package", Path.Combine(_dir, "lp", "my.lib.dll"), "--package", "my.lib",
            "--root", Path.Combine(_dir, "lp"), "--target-image", b, "my.lib");
        Assert.That(c.ExitCode, Is.EqualTo(0), c.ToString());

        // cleat-067：不再回退源码（开发时一律从源码加载，包程序集只在构建时产生），报错让人重编
        var other = Cleat(null, "-I", "lp", "-e", "(clojure.lang.RT/load \"my/lib\")");
        Assert.That(other.ExitCode, Is.Not.EqualTo(0), other.ToString());
        Assert.That(other.Err, Does.Contain("module was compiled against boot image"));
        Assert.That(other.Err, Does.Contain("recompile"));
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

    // Engineering（cleat-162 / 182，原名 Bootstrap）：发布、打包、示例工程这类端到端测试，nightly 层跑
    /// <summary>
    /// cleat-186：镜像旁的随附库 Cleat.Clojure.Libs.dll 按它记的镜像 id（CleatBootImageAttribute）决定用不用，不看文件时间：
    /// 对着本镜像编的、即使文件比镜像旧也用；对着别的镜像编的不用（没有源码可退回时在标准错误说明），而不是用到时报 StaleModuleException。
    /// </summary>
    [Test]
    public void BundledLibsAreUsedOnlyWithTheImageTheyWereCompiledAgainst()
    {
        string srcBoot = Path.Combine(RepoDir, "src", "boot");
        string Copy(string name)
        {
            string d = Path.Combine(_dir, name);
            Directory.CreateDirectory(d);
            foreach (var f in new[] { "cleat.boot.dll", "cleat.boot.pdb", "cleat.boot.ifc", "cleat.boot.inl" })
                File.Copy(Path.Combine(srcBoot, f), Path.Combine(d, f));
            return d;
        }
        string clj = Path.Combine(RepoDir, "src", "clojure");
        // 一个不同的镜像（-g 的镜像：不做直接链接，id 不同）与对着它编的随附库
        string other = Path.Combine(_dir, "other");
        Directory.CreateDirectory(other);
        var b = CleatInWithTimeout(_dir, null, new[] { ("CLEAT_NO_INLINE", "1") }, 600_000, "--boot", Path.Combine(srcBoot, "cleat.boot.dll"), "-I", clj,
                                   "--compile-boot", Path.Combine(other, "cleat.boot.dll"), "--boot-order", Path.Combine(srcBoot, "boot-order.edn"));
        Assert.That(b.ExitCode, Is.EqualTo(0), b.ToString());
        string[] Libs(string boot, string outDll) =>
            new[] { "--boot", boot, "-I", clj, "--compile-package", outDll, "--package", "clojure", "--root", clj, "clojure.test.tap" };
        var c = CleatInWithTimeout(_dir, null, null, 600_000, Libs(Path.Combine(other, "cleat.boot.dll"), Path.Combine(other, "Cleat.Clojure.Libs.dll")));
        Assert.That(c.ExitCode, Is.EqualTo(0), c.ToString());

        // 对着本镜像编的、文件时间比镜像旧：用（require 不需要源码）
        string good = Copy("good");
        c = CleatInWithTimeout(_dir, null, null, 600_000, Libs(Path.Combine(good, "cleat.boot.dll"), Path.Combine(good, "Cleat.Clojure.Libs.dll")));
        Assert.That(c.ExitCode, Is.EqualTo(0), c.ToString());
        File.SetLastWriteTimeUtc(Path.Combine(good, "Cleat.Clojure.Libs.dll"), File.GetLastWriteTimeUtc(Path.Combine(good, "cleat.boot.dll")).AddHours(-1));
        var r = CleatIn(_dir, null, null, "--boot", Path.Combine(good, "cleat.boot.dll"), "-e", "(require 'clojure.test.tap)", "-e", ":tap-ok");
        Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
        Assert.That(r.Out, Does.Contain(":tap-ok"), r.ToString());
        Assert.That(r.Err, Does.Not.Contain("another boot image"), r.ToString());

        // 对着别的镜像编的：不用；这里没有源码可退回，require 失败，标准错误说明原因（不是 StaleModuleException）
        string bad = Copy("bad");
        File.Copy(Path.Combine(other, "Cleat.Clojure.Libs.dll"), Path.Combine(bad, "Cleat.Clojure.Libs.dll"));
        r = CleatIn(_dir, null, null, "--boot", Path.Combine(bad, "cleat.boot.dll"), "-e", "(require 'clojure.test.tap)");
        Assert.That(r.ExitCode, Is.Not.EqualTo(0), r.ToString());
        Assert.That(r.Err, Does.Contain("Cleat.Clojure.Libs.dll was compiled against another boot image; not using it"), r.ToString());
        Assert.That(r.Err + r.Out, Does.Not.Contain("StaleModuleException"), r.ToString());
    }

    [Test, Category("Engineering"), NonParallelizable]
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
        // 编译器随发布（宿主的输出里）；Clojure 编译器的包 cleat.compiler.dll 在 cleat-182 删除
        Assert.That(File.Exists(Path.Combine(outDir, "Cleat.Compiler.dll")), Is.True, "编译器随发布");
        Assert.That(File.Exists(Path.Combine(outDir, "cleat.compiler.dll")), Is.False, "no Clojure compiler package");
        Assert.That(File.Exists(Path.Combine(outDir, "Cleat.Clojure.Libs.dll")), Is.True, "随附库的包程序集随发布");
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
        Assert.That(EmbeddedResource(Path.Combine(_dir, "lp2", "link.a.dll"), "cleat.ifc"), Does.Contain(":static true"), "接口文件登记 invokeStatic");

        Write("lp2/link/b.clj", "(ns link.b\n  (:require [link.a]))\n(defn twice [x] (link.a/inc2 (link.a/inc2 x)))\n");
        var cb = Cleat(null, "-I", "lp2", "-e", "(require 'link.a)", "-e", "(binding [*compile-files* true] (compile 'link.b))");
        Assert.That(cb.ExitCode, Is.EqualTo(0), cb.ToString());
        byte[] bDll = File.ReadAllBytes(Path.Combine(_dir, "lp2", "link.b.dll"));
        Assert.That(Encoding.ASCII.GetString(bDll), Does.Contain("invokeStatic"), "B 的 IL 里有直接调用");

        var r = Cleat(null, "-I", "lp2", "-e", "(require 'link.b)", "-e", "[(link.b/twice 1)]");
        Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
        Assert.That(r.Out.TrimEnd('\n').Split('\n')[^1], Is.EqualTo("[3]"), r.ToString());

        // with-redefs 换掉 link.a/inc2 的根：受保护的直接调用退回普通调用，看到新根
        var w = Cleat(null, "-I", "lp2", "-e", "(require 'link.b)",
            "-e", "(with-redefs [link.a/inc2 (fn* [x] (clojure.core/* 10 x))] (link.b/twice 1))");
        Assert.That(w.ExitCode, Is.EqualTo(0), w.ToString());
        Assert.That(w.Out.TrimEnd('\n').Split('\n')[^1], Is.EqualTo("100"), w.ToString());

        // :reload 重新执行包程序集里命名空间的入口（副作用再发生一次）
        var rl = Cleat(null, "-I", "lp2", "-e", "(require 'link.a)", "-e", "(require 'link.a :reload)");
        Assert.That(rl.ExitCode, Is.EqualTo(0), rl.ToString());
        Assert.That(Regex.Matches(rl.Out, "loading link.a").Count, Is.EqualTo(2), rl.ToString());
    }

    [Test]
    public void SimpleFormsAreNotCompiled()
    {
        var r = Cleat(null, "-e", "(set! (. Cleat.Core.Compiler.DriverOps SimpleCount) 0) (set! (. Cleat.Core.Compiler.DriverOps CompiledCount) 0)",
            "-e", "(def a 1) (def b [a 2]) (cleat.kernel/add a 1) (. \"x\" Length) (let* [k :k] (k {:k 1}))",
            "-e", "[(. Cleat.Core.Compiler.DriverOps SimpleCount) (. Cleat.Core.Compiler.DriverOps CompiledCount)]",
            "-e", "((fn* [] 1))",
            "-e", "(. Cleat.Core.Compiler.DriverOps CompiledCount)");
        Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
        var lines = r.Out.TrimEnd('\n').Split('\n');
        Assert.That(lines[^3], Is.EqualTo("[7 0]"), "the two set! and five simple forms (plus the vector itself) and nothing compiled: " + r);
        Assert.That(lines[^1], Is.EqualTo("1"));
    }
}
