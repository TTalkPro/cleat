using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Cleat.Host.Tests;

/// <summary>
/// cleat-067 的验收（designs/17 §6）与 cleat-066、cleat-069 的端到端验收（都要运行时包，fixture 只打一次）：tools/build.cs pack 打出运行时包 Cleat.Runtime，一个 Clojure 包（gen-class、defrecord、函数）
/// 用 tools.build（cleat-066）编成包程序集再打成 nupkg，两者放进本地目录源（与 BaGetter 同一机制）；一个 C# 与一个 F# 控制台项目只引用那个 Clojure 包，
/// **不调用任何初始化 API**：gen-class 的构造函数、实例方法、静态方法、Main，defrecord 的实例，CleatRuntime.Var 都能用，
/// 并发的首次使用只启动一次运行时（命名空间只加载一次）。
/// </summary>
[Category("Bootstrap"), NonParallelizable]
public class NuGetColdLoadTests
{
    // at least the running version (CoreInfo.Version, 1.0.1): packages built with the default :runtime-version depend on
    // Cleat.Runtime >= it (and in SemVer 1.0.1-anything sorts below 1.0.1); the fixture's NuGet caches are isolated
    private const string Version = "1.0.1";

    private const string GreeterSource = @"(ns demo.greeter
  (:require [clojure.string :as str])
  (:gen-class
   :name demo.Greeter
   :main true
   :init init
   :state state
   :constructors {[String] []}
   :methods [[greet [] String]
             ^:static [shout [String] String]]))

(defonce loads (atom 0))
(swap! loads inc)

(defrecord Point [x y])

(defn -init [name] [[] name])
(defn -greet [this] (str ""hello "" (.state this)))
(defn -shout [s] (str (str/upper-case s) ""!""))
(defn -main [& args] (println ""main"" (vec args)))
(defn add [a b] (+ a b))
";

    private const string CSharpProgram = @"using System;
using System.Linq;
using System.Threading.Tasks;
using Cleat.Core.Hosting;

// the first uses race on three threads: a gen-class instance, a defrecord instance and a Var
var a = Task.Run(() => new demo.Greeter(""ann"").greet());
var b = Task.Run(() => CleatRuntime.Print(new demo.greeter.Point(1L, 2L)));
var c = Task.Run(() => CleatRuntime.Var(""demo.greeter"", ""add"").invoke(1L, 2L).ToString());
Task.WaitAll(a, b, c);
Console.WriteLine(a.Result);
Console.WriteLine(b.Result);
Console.WriteLine(c.Result);
Console.WriteLine(demo.Greeter.shout(""hey""));
demo.Greeter.Main(new[] { ""x"", ""y"" });
Console.WriteLine(CleatRuntime.Eval(""@demo.greeter/loads""));
";

    private const string FSharpProgram = @"open Cleat.Core.Hosting

let g = demo.Greeter(""fred"")
printfn ""%s"" (g.greet())
printfn ""%s"" (CleatRuntime.Print(demo.greeter.Point(box 3L, box 4L)))
printfn ""%O"" (CleatRuntime.Var(""demo.greeter"", ""add"").invoke(box 2L, box 5L))
printfn ""%s"" (demo.Greeter.shout ""fsharp"")
printfn ""%O"" (CleatRuntime.Eval(""@demo.greeter/loads""))
";

    private string _root, _feed;
    private (string, string)[] _env;
    private string _dir;

    /// <summary>运行时包只打一次（tools/build.cs pack，约 2 分钟），放进本 fixture 的本地源；全局包目录隔离（同一版本号的旧包不会被复用）。</summary>
    [OneTimeSetUp]
    public void PackRuntime()
    {
        _root = Path.Combine(Path.GetTempPath(), "cleat-nuget-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        _feed = Path.Combine(_root, "feed");
        Directory.CreateDirectory(_feed);
        // 其余依赖（FSharp.Core、Microsoft.Extensions.DependencyModel …）从用户的全局包目录取（回退目录），不需要网络。
        // 回退目录是过滤过的：用户缓存里 cleat 自己的包（如从 BaGetter 装过的 Cleat.*）不能盖过本 fixture 构建的
        string userCache = Environment.GetEnvironmentVariable("NUGET_PACKAGES")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        string userPackages = Path.Combine(_root, "fallback");
        Directory.CreateDirectory(userPackages);
        if (Directory.Exists(userCache))
            foreach (var d in Directory.GetDirectories(userCache))
            {
                string name = Path.GetFileName(d);
                if (name.StartsWith("cleat.", StringComparison.OrdinalIgnoreCase) || name.StartsWith("web.", StringComparison.OrdinalIgnoreCase)
                    || name == "demo") continue;
                Directory.CreateSymbolicLink(Path.Combine(userPackages, name), d);
            }
        File.WriteAllText(Path.Combine(_root, "NuGet.config"), $@"<?xml version=""1.0"" encoding=""utf-8""?>
<configuration>
  <packageSources><clear /><add key=""local"" value=""{_feed}"" /></packageSources>
  <fallbackPackageFolders><add key=""user"" value=""{userPackages}"" /></fallbackPackageFolders>
</configuration>
");
        _env = new[] { ("NUGET_PACKAGES", Path.Combine(_root, "packages")) };
        Run(HostProcessTests.RepoDir, null, "dotnet", "run", "tools/build.cs", "--", "pack", "-o", _feed, "--version", Version);
        Assert.That(File.Exists(Path.Combine(_feed, $"Cleat.Runtime.{Version}.nupkg")), Is.True);
    }

    [OneTimeTearDown]
    public void RemoveRoot()
    {
        try { Directory.Delete(_root, true); } catch (Exception) { }
    }

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(_root, TestContext.CurrentContext.Test.Name);
        Directory.CreateDirectory(_dir);
    }

    private static string ToolsBuildPath(string package) => Path.Combine(HostProcessTests.RepoDir, "packages", package, "src");

    /// <summary>tools.build 的加载路径（cleat-066）。</summary>
    private static readonly string[] ToolsBuild =
        { "-I", ToolsBuildPath("tools.build"), "-I", ToolsBuildPath("tools.namespace") };

    private static string Edn(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    [Test]
    public void CSharpAndFSharpProjectsColdLoadAPackageFromALocalFeed()
    {
        // Clojure 包：tools.build 的 compile-clj 编成包程序集、pack 打成 nupkg（依赖 Cleat.Runtime），放进本地源
        string project = Path.Combine(_dir, "demo");
        Directory.CreateDirectory(Path.Combine(project, "src", "demo"));
        File.WriteAllText(Path.Combine(project, "src", "demo", "greeter.clj"), GreeterSource);
        File.WriteAllText(Path.Combine(project, "cleat.edn"), "{:paths [\"src\"]}");
        string build = "(require '[clojure.tools.build.api :as b])" +
            "(let [basis (b/create-basis)]" +
            "  (b/compile-clj {:basis basis :class-dir \"target/classes\" :lib 'Demo :package \"demo\"})" +
            $"  (b/pack {{:basis basis :lib 'Demo :version \"1.0.0\" :class-dir \"target/classes\" :nupkg-file \"target/Demo.1.0.0.nupkg\" :runtime-version {Edn(Version)}}})" +
            $"  (b/install {{:nupkg-file \"target/Demo.1.0.0.nupkg\" :local-feed {Edn(_feed)}}}))";
        var c = HostProcessTests.CleatIn(project, null, _env, ToolsBuild.Concat(new[] { "-e", build }).ToArray());
        Assert.That(c.ExitCode, Is.EqualTo(0), c.ToString());
        Assert.That(File.Exists(Path.Combine(_feed, "demo.1.0.0.nupkg")), Is.True);

        // C# 项目：只引用 Demo
        string cs = App("cs", "csproj", "Program.cs", CSharpProgram);
        var r = Run(cs, _env, "dotnet", "run", "-nologo", "-v", "quiet");
        Assert.That(Lines(r), Is.EqualTo(new[]
        {
            "hello ann",
            "#demo.greeter.Point{:x 1, :y 2}",
            "3",
            "HEY!",
            "main [x y]",
            "1",
        }), r);

        // F# 项目
        string fs = App("fs", "fsproj", "Program.fs", FSharpProgram);
        r = Run(fs, _env, "dotnet", "run", "-nologo", "-v", "quiet");
        Assert.That(Lines(r), Is.EqualTo(new[]
        {
            "hello fred",
            "#demo.greeter.Point{:x 3, :y 4}",
            "7",
            "FSHARP!",
            "1",
        }), r);
    }

    private static readonly string NoSources =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<configuration><packageSources><clear /></packageSources></configuration>\n";

    private bool _clojurePackagesBuilt;

    /// <summary>
    /// packages/ 下的 tools.namespace、tools.build、nrepl、neorepl 按依赖顺序用各自的 build.clj（tools.build）编包程序集、打包、
    /// 装进本 fixture 的本地源（只做一次）。包目录复制出来再构建（target/ 不写进仓库）；nuget.config 清空源，依赖只从本地源 restore（不连网）。
    /// </summary>
    private void BuildClojurePackages()
    {
        if (_clojurePackagesBuilt) return;
        foreach (var (package, id) in new[] { ("tools.namespace", "cleat.toolsnamespace.1.5.2"), ("tools.build", "cleat.toolsbuild.0.10.16"),
                                             ("nrepl", "cleat.nrepl.1.8.1"), ("neorepl", "cleat.neorepl.0.1.1") })
        {
            string dir = Path.Combine(_root, "sources", package);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            string from = Path.Combine(HostProcessTests.RepoDir, "packages", package);
            CopyTree(Path.Combine(from, "src"), Path.Combine(dir, "src"));
            File.Copy(Path.Combine(from, "cleat.edn"), Path.Combine(dir, "cleat.edn"));
            File.Copy(Path.Combine(from, "build.clj"), Path.Combine(dir, "build.clj"));
            File.WriteAllText(Path.Combine(dir, "nuget.config"), NoSources);
            var b = HostProcessTests.CleatInWithTimeout(dir, null, _env, 600_000,
                ToolsBuild.Concat(new[] { "-I", ".", "-m", "clojure.tools.build.main", "install", ":local-feed", Edn(_feed) }).ToArray());
            Assert.That(b.ExitCode, Is.EqualTo(0), b.ToString());
            Assert.That(File.Exists(Path.Combine(_feed, id + ".nupkg")), Is.True, id);
        }
        _clojurePackagesBuilt = true;
    }

    private static void CopyTree(string from, string to, Func<string, bool>? skip = null)
    {
        foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(from, f);
            if (skip != null && skip(rel.Replace('\\', '/'))) continue;
            string target = Path.Combine(to, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(f, target);
        }
    }

    /// <summary>
    /// cleat-066 的验收：packages/nrepl、packages/neorepl（与 tools.build、tools.namespace）用 tools.build 构建、装进本地源；
    /// 只依赖 Cleat.Neorepl 的项目经 create-basis 从源 restore 到它与传递的依赖（Cleat.Runtime 不进加载路径），运行 neorepl。
    /// </summary>
    [Test]
    public void NreplAndNeoreplBuiltWithToolsBuildRestoreFromTheFeed()
    {
        BuildClojurePackages();
        string app = Path.Combine(_dir, "app");
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(app, "cleat.edn"), "{:deps {Cleat.Neorepl {:nuget/version \"0.1.1\"}}}");
        File.WriteAllText(Path.Combine(app, "nuget.config"), NoSources);
        string run = "(require '[clojure.tools.build.api :as b])" +
            $"(let [basis (b/create-basis {{:local-feed {Edn(_feed)}}})]" +
            "  (prn (sort (map str (keys (:libs basis)))))" +
            "  (b/process (b/cleat-command {:basis basis :main 'neorepl.main :main-args [\"--help\"]})))";
        var r = HostProcessTests.CleatIn(app, null, _env, ToolsBuild.Concat(new[] { "-e", run }).ToArray());
        Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
        Assert.That(r.Out, Does.StartWith("(\"Cleat.Neorepl\" \"Cleat.Nrepl\" \"Cleat.ToolsBuild\" \"Cleat.ToolsNamespace\")\n"),
            "Cleat.Runtime stays off the load path");
        Assert.That(r.Out, Does.Contain("Usage: neorepl [COMMAND] [options]"));
    }

    /// <summary>
    /// cleat-069 的验收：examples/WebServer 以本地源代替 BaGetter（同一机制）——clojure.test 通过（cleat-build test）；
    /// dotnet build host 一条命令编 C# 库、Clojure 包程序集与 C# 主机；主机里 C# 直接用 gen-class 的 Web.Handlers 服务四个接口；
    /// cleat-run -M:neorepl（neorepl 来自源）连进主机进程内的 nREPL 热替换处理函数；jack-in 起的服务的加载路径有项目代码与 C# 类型。
    /// </summary>
    [Test]
    public async Task ExampleProjectBuildsServesAndDevelopsInteractively()
    {
        Assume.That(OperatingSystem.IsWindows(), Is.False, "the launchers used here are sh scripts");
        BuildClojurePackages();
        string app = Path.Combine(_dir, "WebServer");
        CopyTree(Path.Combine(HostProcessTests.RepoDir, "examples", "WebServer"), app,
                 rel => rel.StartsWith("target/") || rel.StartsWith(".cleat/") || rel.Contains("/bin/") || rel.Contains("/obj/")
                        || rel == ".nrepl-port" || rel == "nuget.config");
        File.WriteAllText(Path.Combine(app, "nuget.config"),
            $"<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<configuration><packageSources><clear /><add key=\"local\" value=\"{_feed}\" /></packageSources></configuration>\n");
        string cleat = Path.ChangeExtension(HostProcessTests.HostDll, null);
        var env = _env.Append(("CLEAT", cleat)).ToArray();
        string[] Main(params string[] args) => ToolsBuild.Concat(new[] { "-I", ".", "-m", "clojure.tools.build.main" }).Concat(args).ToArray();

        // the Clojure tests (prep builds the C# library into target/dotnet)
        var t = HostProcessTests.CleatInWithTimeout(app, null, env, 600_000, Main("test"));
        Assert.That(t.ExitCode, Is.EqualTo(0), t.ToString());
        Assert.That(t.Out, Does.Contain("0 failures, 0 errors."));

        // dotnet build: the C# library, the Clojure package assembly, the C# host
        string cleatBuild = Path.Combine(HostProcessTests.RepoDir, "packages", "tools.build", "bin", "cleat-build");
        Run(app, env, "dotnet", "build", "host", "-nologo", "-v", "q", "-p:CleatBuild=" + cleatBuild, "-p:Cleat=" + cleat);
        Assert.That(File.Exists(Path.Combine(app, "target", "classes", "Web.Handlers.dll")), Is.True);

        int http = FreePort(), nrepl = FreePort();
        var psi = new ProcessStartInfo("dotnet") { WorkingDirectory = app, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { Path.Combine(app, "host", "bin", "Debug", "net10.0", "WebServer.dll"), "--urls", $"http://127.0.0.1:{http}" })
            psi.ArgumentList.Add(a);
        psi.Environment["WEB_NREPL_PORT"] = nrepl.ToString();
        using var host = Process.Start(psi)!;
        var hostOut = host.StandardOutput.ReadToEndAsync();
        var hostErr = host.StandardError.ReadToEndAsync();
        try
        {
            using var client = new System.Net.Http.HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{http}") };
            for (int i = 0; ; i++)
            {
                try { await client.GetStringAsync("/slug?title=warm"); break; }
                catch (System.Net.Http.HttpRequestException) when (i < 240 && !host.HasExited) { await Task.Delay(250); }
            }
            Assert.That(await client.GetStringAsync("/hello?name=cleat"), Is.EqualTo("{\"name\":\"cleat\",\"message\":\"hello, cleat!\"}"));
            using (var sum = await client.PostAsync("/sum", new System.Net.Http.StringContent("[1,2,3,4]", Encoding.UTF8, "application/json")))
                Assert.That(await sum.Content.ReadAsStringAsync(), Is.EqualTo("{\"sum\":10}"));
            Assert.That(await client.GetStringAsync("/slug?title=Hello,%20Cleat%20World"), Is.EqualTo("{\"slug\":\"hello-cleat-world\"}"));
            var counts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.GetStringAsync("/stats")));
            Assert.That(counts.Distinct().Count(), Is.EqualTo(8), string.Join(",", counts));

            // neorepl (from the feed) into the running host: redefine a handler
            var e = HostProcessTests.CleatIn(app, null, env, Main("-M:neorepl", "eval", "-p", nrepl.ToString(),
                "(in-ns 'web.handlers) (defn greeting-text [n] (str \"redefined, \" n))"));
            Assert.That(e.ExitCode, Is.EqualTo(0), e.ToString());
            Assert.That(await client.GetStringAsync("/hello?name=cleat"), Is.EqualTo("{\"name\":\"cleat\",\"message\":\"redefined, cleat\"}"));
        }
        finally
        {
            try { host.Kill(true); } catch (Exception) { }
            host.WaitForExit(10_000);
            TestContext.Out.WriteLine("host: " + await hostOut + await hostErr);
        }

        // jack-in: neorepl starts a server for the project; its load path is the project's basis
        var server = new ProcessStartInfo(cleat) { WorkingDirectory = app, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in Main("-M:neorepl", "server")) server.ArgumentList.Add(a);
        foreach (var (k, v) in env) server.Environment[k] = v;
        using var jack = Process.Start(server)!;
        try
        {
            var url = jack.StandardOutput.ReadLineAsync();
            Assert.That(url.Wait(180_000), Is.True, "the jack-in server never started");
            Assert.That(url.Result, Does.StartWith("nrepl://"));
            var r = HostProcessTests.CleatIn(app, null, env, Main("-M:neorepl", "eval",
                "(require 'web.handlers) [(web.handlers/greeting-text \"jack-in\") (WebCore.Slug/Make \"A B\") (.Message (.Greet (Web.Handlers.) \"x\"))]"));
            Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
            // eval prints each form's value: (require …) is nil
            Assert.That(r.Out.Trim().Split('\n'), Is.EqualTo(new[] { "nil", "[\"hello, jack-in!\" \"a-b\" \"hello, x!\"]" }));
        }
        finally
        {
            // SIGTERM: the server's shutdown hook stops nrepl.cmdline too
            try { Process.Start("kill", jack.Id.ToString())?.WaitForExit(5000); } catch (Exception) { }
            if (!jack.WaitForExit(15_000)) try { jack.Kill(true); } catch (Exception) { }
        }
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        int port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private string App(string name, string ext, string file, string program)
    {
        string dir = Path.Combine(_dir, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, file), program);
        File.WriteAllText(Path.Combine(dir, "App." + ext), $@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>disable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    {(ext == "fsproj" ? @"<Compile Include=""Program.fs"" />" : "")}
    <PackageReference Include=""Demo"" Version=""1.0.0"" />
  </ItemGroup>
</Project>
");
        return dir;
    }

    private static string[] Lines(string output) =>
        output.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static string Run(string dir, (string, string)[] env, string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false, WorkingDirectory = dir,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var (k, v) in env ?? Array.Empty<(string, string)>()) psi.Environment[k] = v;
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_NOLOGO"] = "1";
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("cannot start " + file);
        p.StandardInput.Close();
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(1_200_000))
        {
            try { p.Kill(true); } catch (Exception) { }
            Assert.Fail($"timed out: {file} {string.Join(" ", args)}");
        }
        p.WaitForExit();
        string output = outTask.Result, err = errTask.Result;
        TestContext.Out.WriteLine($"$ {file} {string.Join(" ", args)}  (in {dir})\n{output}\n{err}");
        Assert.That(p.ExitCode, Is.EqualTo(0), $"{file} {string.Join(" ", args)} failed:\n{output}\n{err}");
        return output;
    }
}
