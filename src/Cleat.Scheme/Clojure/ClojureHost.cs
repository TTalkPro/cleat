using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace Cleat.Scheme.Clojure;

/// <summary>
/// 宿主装入 Clojure 驱动的 Scheme 库（cleat-052）。Scheme 镜像加载之后调用 <see cref="Start"/>：
/// <list type="number">
/// <item>预编译的库（<c>src/boot/cleat.scheme.clojure/cleat-host.manifest</c>，<c>clojure/build-host.sps</c> 在构建时生成）：按序装入
/// "library" 行的 fasl，运行 "program" 行（<c>clojure/host.sps</c>：登记 Cleat.Core 的运行时钩子）；"deferred" 行（编译器与 nanopass，
/// 只有 AOT 编译需要）在第一次 compile 时由驱动经 <see cref="EnsureCompileLibraries"/> 装入；</item>
/// <item>没有预编译的库、或它们是对另一个镜像编译的（库 id / 镜像 id 不符）：退回源码，以 <c>load/program</c> 运行 <c>clojure/host.sps</c>
/// （库搜索路径要在镜像加载之前由 <see cref="AddSourcePaths"/> 加入），约慢 10 秒。</item>
/// </list>
/// </summary>
public static class ClojureHost
{
    public const string ManifestName = "cleat-host.manifest";

    /// <summary>"fasl"（预编译）或 "source"（源码）；未启动时为 null。</summary>
    public static string Mode { get; private set; }

    /// <summary>Start 的耗时（毫秒）。</summary>
    public static long StartMilliseconds { get; private set; }

    private static readonly List<string> s_deferred = new List<string>();
    private static bool s_deferredLoaded;
    private static string s_sourceDir;

    /// <summary>镜像目录旁的预编译库目录：<c>&lt;镜像目录&gt;/cleat.scheme.clojure</c>。</summary>
    public static string LibraryDirFor(string schemeBootImage) =>
      Path.Combine(Path.GetDirectoryName(Path.GetFullPath(schemeBootImage)) ?? ".", "cleat.scheme.clojure");

    /// <summary>
    /// 源码库的搜索路径（src/scheme、其下的 lib 与 nanopass）。展开器在镜像初始化时读取库搜索路径，所以要在加载镜像之前调用；
    /// 预编译的库可用时不会用到。
    /// </summary>
    public static void AddSourcePaths(string schemeSourceDir)
    {
        if (schemeSourceDir == null || !Directory.Exists(schemeSourceDir)) return;
        s_sourceDir = Path.GetFullPath(schemeSourceDir);
        Builtins.AddIncludePath(s_sourceDir);
        Builtins.AddIncludePath(Path.Combine(s_sourceDir, "lib"));
        Builtins.AddIncludePath(Path.Combine(s_sourceDir, "nanopass"));
    }

    /// <summary>装入驱动的库并登记钩子；libraryDir 为预编译库目录（可为 null）。</summary>
    public static void Start(string libraryDir, TextWriter warnings = null)
    {
        var sw = Stopwatch.StartNew();
        warnings ??= Console.Error;
        string manifest = libraryDir == null ? null : Path.Combine(libraryDir, ManifestName);
        try
        {
            if (manifest != null && File.Exists(manifest))
            {
                try
                {
                    StartFromManifest(manifest);
                    Mode = "fasl";
                    return;
                }
                catch (Exception e) when (s_sourceDir != null)
                {
                    warnings.WriteLine($"cleat: precompiled Scheme libraries in {libraryDir} could not be used ({Describe(e)}); loading them from source (slow; rebuild to refresh)");
                }
            }
            else if (s_sourceDir != null)
                warnings.WriteLine($"cleat: no precompiled Scheme libraries ({manifest ?? "no Scheme boot image directory"}); loading them from source (slow)");
            if (s_sourceDir == null)
                throw new InvalidOperationException($"the Clojure driver libraries were not found: no {ManifestName} in {libraryDir ?? "(none)"} and no Scheme source directory");
            RunProgram(Path.Combine(s_sourceDir, "clojure", "host.sps"));
            Mode = "source";
        }
        finally
        {
            StartMilliseconds = sw.ElapsedMilliseconds;
        }
    }

    private static string Describe(Exception e) => e is SchemeException se ? se.Condition?.ToString() ?? e.Message : e.GetType().Name + ": " + e.Message;

    private static void StartFromManifest(string manifest)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(manifest));
        s_deferred.Clear();
        foreach (string raw in File.ReadAllLines(manifest))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(";", StringComparison.Ordinal)) continue;
            string[] parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) throw new InvalidDataException($"{manifest}: bad line: {line}");
            string file = Path.Combine(dir, parts[1]);
            switch (parts[0])
            {
                case "library":
                case "program":
                    LoadFasl(file);
                    break;
                case "deferred":
                    s_deferred.Add(file);
                    break;
                default:
                    throw new InvalidDataException($"{manifest}: bad line: {line}");
            }
        }
    }

    /// <summary>装入一个 fasl（库：安装；程序：核对依赖库后运行），即 Scheme 的 (load "x.fasl")。</summary>
    private static void LoadFasl(string file)
    {
        var loadContent = Symbol.Intern("load-fasl-content").Value as Procedure
          ?? throw new InvalidOperationException("load-fasl-content is not defined (Scheme boot image not loaded?)");
        object content = ModuleLoader.LoadModule(file, inMemory: true);
        loadContent.Invoke2(content, file);
    }

    /// <summary>从源码运行一个顶层程序（stage0 的 <c>(load/program 文件)</c>）。</summary>
    private static void RunProgram(string file)
    {
        var eval = Symbol.Intern("eval-embedded").Value as Procedure
          ?? throw new InvalidOperationException("eval-embedded is not defined (Scheme boot image not loaded?)");
        eval.Invoke1(Cons.FromArray(Symbol.Intern("load/program"), file.Replace('\\', '/')));
    }

    /// <summary>驱动第一次 AOT 编译前调用：装入延后的库（编译器、nanopass、(clojure compile)）。源码模式下什么也不做（展开器按需从源码装入）。</summary>
    public static void EnsureCompileLibraries()
    {
        if (s_deferredLoaded) return;
        s_deferredLoaded = true;
        foreach (string f in s_deferred) LoadFasl(f);
    }
}
