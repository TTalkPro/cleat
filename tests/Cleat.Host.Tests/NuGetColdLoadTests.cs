using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;

namespace Cleat.Host.Tests;

/// <summary>
/// cleat-067 的验收（designs/17 §6）：tools/build.cs pack 打出运行时包 Cleat.Runtime，一个 Clojure 包（gen-class、defrecord、函数）
/// 用 tools.build（cleat-066）编成包程序集再打成 nupkg，两者放进本地目录源（与 BaGetter 同一机制）；一个 C# 与一个 F# 控制台项目只引用那个 Clojure 包，
/// **不调用任何初始化 API**：gen-class 的构造函数、实例方法、静态方法、Main，defrecord 的实例，CleatRuntime.Var 都能用，
/// 并发的首次使用只启动一次运行时（命名空间只加载一次）。
/// </summary>
[Category("Bootstrap"), NonParallelizable]
public class NuGetColdLoadTests
{
    // at least the running version (CoreInfo.Version, 1.0.0): packages built with the default :runtime-version depend on
    // Cleat.Runtime >= it (and in SemVer 1.0.0-anything sorts below 1.0.0); the fixture's NuGet caches are isolated
    private const string Version = "1.0.0";

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
        // 其余依赖从用户的全局包目录取（回退目录），不需要网络
        string userPackages = Environment.GetEnvironmentVariable("NUGET_PACKAGES")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
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

    /// <summary>
    /// cleat-066 的验收：packages/nrepl、packages/neorepl 用各自的 build.clj（tools.build）编包程序集、打包、装进本地源；
    /// 只依赖 Cleat.Neorepl 的项目经 create-basis 从源 restore 到 Cleat.Neorepl 与（传递的）Cleat.Nrepl，运行 neorepl。
    /// </summary>
    [Test]
    public void NreplAndNeoreplBuiltWithToolsBuildRestoreFromTheFeed()
    {
        foreach (var (package, id) in new[] { ("nrepl", "cleat.nrepl.1.8.0"), ("neorepl", "cleat.neorepl.0.1.0") })
        {
            // 包目录复制出来再构建（target/ 不写进仓库）
            string dir = Path.Combine(_dir, package);
            string from = Path.Combine(HostProcessTests.RepoDir, "packages", package);
            foreach (var f in Directory.GetFiles(Path.Combine(from, "src"), "*", SearchOption.AllDirectories))
            {
                string to = Path.Combine(dir, "src", Path.GetRelativePath(Path.Combine(from, "src"), f));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(f, to);
            }
            File.Copy(Path.Combine(from, "cleat.edn"), Path.Combine(dir, "cleat.edn"));
            File.Copy(Path.Combine(from, "build.clj"), Path.Combine(dir, "build.clj"));
            // neorepl 的 Cleat.Nrepl 依赖从本地源 restore：nuget.config 只留本地源（不连网）
            File.WriteAllText(Path.Combine(dir, "nuget.config"),
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<configuration><packageSources><clear /></packageSources></configuration>\n");
            var b = HostProcessTests.CleatInWithTimeout(dir, null, _env, 600_000,
                ToolsBuild.Concat(new[] { "-I", ".", "-m", "clojure.tools.build.main", "install", ":local-feed", Edn(_feed) }).ToArray());
            Assert.That(b.ExitCode, Is.EqualTo(0), b.ToString());
            Assert.That(File.Exists(Path.Combine(_feed, id + ".nupkg")), Is.True, id);
        }

        string app = Path.Combine(_dir, "app");
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(app, "cleat.edn"), "{:deps {Cleat.Neorepl {:nuget/version \"0.1.0\"}}}");
        File.WriteAllText(Path.Combine(app, "nuget.config"),
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<configuration><packageSources><clear /></packageSources></configuration>\n");
        string run = "(require '[clojure.tools.build.api :as b])" +
            $"(let [basis (b/create-basis {{:local-feed {Edn(_feed)}}})]" +
            "  (prn (sort (map str (keys (:libs basis)))))" +
            "  (b/process (b/cleat-command {:basis basis :main 'neorepl.main :main-args [\"--help\"]})))";
        var r = HostProcessTests.CleatIn(app, null, _env, ToolsBuild.Concat(new[] { "-e", run }).ToArray());
        Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
        Assert.That(r.Out, Does.StartWith("(\"Cleat.Neorepl\" \"Cleat.Nrepl\")\n"), "Cleat.Runtime stays off the load path");
        Assert.That(r.Out, Does.Contain("Usage: neorepl [COMMAND] [options]"));
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
