using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;

namespace Cleat.Host.Tests;

/// <summary>
/// cleat-067 的验收（designs/17 §6）：tools/build.cs pack 打出运行时包 Cleat.Runtime，一个 Clojure 包（gen-class、defrecord、函数）
/// 编成包程序集再打成 nupkg，两者放进本地目录源（与 BaGetter 同一机制）；一个 C# 与一个 F# 控制台项目只引用那个 Clojure 包，
/// **不调用任何初始化 API**：gen-class 的构造函数、实例方法、静态方法、Main，defrecord 的实例，CleatRuntime.Var 都能用，
/// 并发的首次使用只启动一次运行时（命名空间只加载一次）。
/// </summary>
[Category("Bootstrap"), NonParallelizable]
public class NuGetColdLoadTests
{
    private const string Version = "0.1.0-coldload";

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

    private string _dir;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cleat-nuget-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_dir, true); } catch (Exception) { }
    }

    [Test]
    public void CSharpAndFSharpProjectsColdLoadAPackageFromALocalFeed()
    {
        string feed = Path.Combine(_dir, "feed");
        Directory.CreateDirectory(feed);
        // 隔离的全局包目录（同一版本号的旧包不会被复用）；其余依赖从用户的全局包目录取（回退目录），不需要网络
        string userPackages = Environment.GetEnvironmentVariable("NUGET_PACKAGES")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        File.WriteAllText(Path.Combine(_dir, "NuGet.config"), $@"<?xml version=""1.0"" encoding=""utf-8""?>
<configuration>
  <packageSources><clear /><add key=""local"" value=""{feed}"" /></packageSources>
  <fallbackPackageFolders><add key=""user"" value=""{userPackages}"" /></fallbackPackageFolders>
</configuration>
");
        var env = new[] { ("NUGET_PACKAGES", Path.Combine(_dir, "packages")) };

        // 1. 运行时包
        Run(HostProcessTests.RepoDir, null, "dotnet", "run", "tools/build.cs", "--", "pack", "-o", feed, "--version", Version);
        Assert.That(File.Exists(Path.Combine(feed, $"Cleat.Runtime.{Version}.nupkg")), Is.True);

        // 2. Clojure 包：编成包程序集，打成 nupkg（依赖 Cleat.Runtime）
        string src = Path.Combine(_dir, "demo", "src");
        Directory.CreateDirectory(Path.Combine(src, "demo"));
        File.WriteAllText(Path.Combine(src, "demo", "greeter.clj"), GreeterSource);
        string pkgDir = Path.Combine(_dir, "demo", "pack");
        Directory.CreateDirectory(pkgDir);
        var c = HostProcessTests.CleatIn(_dir, null, null, "-I", src, "--compile-package", Path.Combine(pkgDir, "Demo.dll"), "--package", "demo", "demo.greeter");
        Assert.That(c.ExitCode, Is.EqualTo(0), c.ToString());
        File.WriteAllText(Path.Combine(pkgDir, "Demo.csproj"), $@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <PackageId>Demo</PackageId>
    <Version>1.0.0</Version>
    <IncludeBuildOutput>false</IncludeBuildOutput>
    <NoWarn>NU5128</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <None Include=""Demo.dll"" Pack=""true"" PackagePath=""lib/net10.0/"" />
    <PackageReference Include=""Cleat.Runtime"" Version=""{Version}"" />
  </ItemGroup>
</Project>
");
        Run(pkgDir, env, "dotnet", "pack", "-o", feed, "-nologo", "-v", "quiet");
        Assert.That(File.Exists(Path.Combine(feed, "Demo.1.0.0.nupkg")), Is.True);

        // 3. C# 项目：只引用 Demo
        string cs = App("cs", "csproj", "Program.cs", CSharpProgram);
        var r = Run(cs, env, "dotnet", "run", "-nologo", "-v", "quiet");
        Assert.That(Lines(r), Is.EqualTo(new[]
        {
            "hello ann",
            "#demo.greeter.Point{:x 1, :y 2}",
            "3",
            "HEY!",
            "main [x y]",
            "1",
        }), r);

        // 4. F# 项目
        string fs = App("fs", "fsproj", "Program.fs", FSharpProgram);
        r = Run(fs, env, "dotnet", "run", "-nologo", "-v", "quiet");
        Assert.That(Lines(r), Is.EqualTo(new[]
        {
            "hello fred",
            "#demo.greeter.Point{:x 3, :y 4}",
            "7",
            "FSHARP!",
            "1",
        }), r);
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
