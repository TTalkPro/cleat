using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using clojure.lang;

namespace Cleat.Core.Runtime;

/// <summary>启动选项（命令行宿主的 Options 与嵌入 API 的 CleatOptions 都映射到它）。</summary>
public sealed class StartupOptions
{
    /// <summary>Clojure 镜像路径；null 时按 CLEAT_BOOT 与 <see cref="Startup.FindBoot"/> 的规则自动找。</summary>
    public string Boot { get; set; }

    /// <summary>加载路径（-I）。</summary>
    public List<string> IncludeDirs { get; } = new();

    /// <summary>正在编的包程序集（--compile-package）：它不登记，否则 require 从旧包加载。</summary>
    public string CompilePackage { get; set; }
}

/// <summary>
/// 运行时的启动（原 Cleat.Host.Program.Start，下沉到这里让命令行宿主 cleat 与嵌入 API Cleat.Hosting 走同一条路而互不引用）：
///   1. 登记 cleat.kernel 的原语 → RT.Init；
///   2. 加载 Clojure 镜像 cleat.boot.dll（--boot，或 CLEAT_BOOT，或自动找 src/boot/）并设置 BootInterface.CurrentImageId，登记镜像旁的
///      包程序集（编译器 cleat.compiler.dll、随附库、tools）；
///   3. require 编译器的驱动 cleat.compiler.driver（镜像旁的包；自举时 CLEAT_COMPILER_NS 指定改名的工具编译器，从 -I 的源码加载），
///      由它登记运行时钩子 eval / load-source / compile / macroexpand-1 与镜像、包的编译入口（cleat-090）。
/// 另有 <see cref="ReferClojureCore"/>（user 对 clojure.core 的 refer）与 <see cref="EvalReader"/>（经 eval 钩子逐形式求值）。
/// </summary>
public static class Startup
{
    /// <summary>运行着的镜像（Start 之后；没有 Clojure 镜像时为 null）。tools.build 的 uber 据此复制运行时（cleat-066）。</summary>
    public static string ClojureBootPath;

    /// <summary>启动耗时（毫秒）：Clojure 镜像、编译器（加载驱动并登记钩子）；CLEAT_TIME 设置时写到标准错误。</summary>
    public static long ClojureImageMs, CompilerMs;

    /// <summary>编译器从镜像旁的包程序集加载（否则从 -I 的源码加载：自举时的改名工具编译器）。</summary>
    public static bool CompilerFromPackage;

    /// <summary>Clojure 编译器的命名空间前缀（CLEAT_COMPILER_NS，缺省 cleat.compiler；驱动是 前缀.driver）。</summary>
    private static string CompilerNs => Environment.GetEnvironmentVariable("CLEAT_COMPILER_NS") is string ns && ns != "" ? ns : "cleat.compiler";

    /// <summary>初始化运行时、加载镜像、加载编译器并登记钩子；返回 Clojure 镜像路径（没有时 null）。</summary>
    public static string Start(StartupOptions o)
    {
        RuntimeBootstrap.MarkStarting();      // 启动期间这条线程上的惰性启动检查直接返回（cleat-067）
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string boot = o.Boot ?? Environment.GetEnvironmentVariable("CLEAT_BOOT") ?? FindBoot("cleat.boot.dll");
        if (boot != null) boot = Path.GetFullPath(boot);
        // 正在编的那个包不登记（cleat-089）：否则 require 从旧包加载，编出的新包没有形式
        bool writing(string f) => o.CompilePackage != null && string.Equals(Path.GetFullPath(o.CompilePackage), Path.GetFullPath(f), StringComparison.Ordinal);
        // 编译器的包程序集（cleat-085，designs/19 D6）在镜像旁。CLEAT_COMPILER_NS 指定别的编译器（自举时改名的工具编译器 cleat.compiler0，
        // 在 -I 的目录里，cleat-091）时不登记——要编的正是 cleat.compiler 的源码。包与镜像不配（镜像重建后没有重编包）时加载包报镜像核对错误：
        // dotnet build 的 GenerateCompilerPackage 按输入增量重编它
        string compiler = boot == null ? null : Path.Combine(Path.GetDirectoryName(boot) ?? ".", "cleat.compiler.dll");
        CompilerFromPackage = compiler != null && CompilerNs == "cleat.compiler" && File.Exists(compiler) && !writing(compiler);
        if (StartupTimings.Enabled) StartupTimings.Mark(StartupTimings.Segment.Paths);   // 上面的路径探测（找镜像与编译器包）也算启动（cleat-159）
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(Num).TypeHandle);   // Num 的标签先初始化（cleat-102）
        PrimitiveRegistry.RegisterAll();
        RT.Init();
        // *out* / *err* 的根是不自动刷新的 StreamWriter：换成控制台的（自动刷新，与 REPL 的提示与结果按顺序交错）
        RT.OutVar.bindRoot(Console.Out);
        RT.ErrVar.bindRoot(Console.Error);
        foreach (var d in o.IncludeDirs) Loader.AddPath(Path.GetFullPath(d));
        Cleat.Core.Compiler.Diagnostics.InstallReportError();
        if (StartupTimings.Enabled) StartupTimings.Mark(StartupTimings.Segment.Runtime);
        if (boot != null)
        {
            ClojureBootPath = boot;
            Loader.LoadBootImage(boot);
            ClojureImageMs = sw.ElapsedMilliseconds;
            // 随附库的包程序集（tools/build.cs compile-libs，cleat-067）在镜像旁；比镜像旧时（镜像重建后没有重编）不用，
            // 这些命名空间从源码加载
            string libs = Path.Combine(Path.GetDirectoryName(boot) ?? ".", "Cleat.Clojure.Libs.dll");
            if (File.Exists(libs) && File.GetLastWriteTimeUtc(libs) >= File.GetLastWriteTimeUtc(boot) && !writing(libs)) PackageIndex.RegisterFile(libs);
            if (CompilerFromPackage) PackageIndex.RegisterFile(compiler);
            // 随附的 tools.build / tools.namespace 包程序集（Cleat.Cli 工具包，cleat-071），同样比镜像旧时不用；
            // 开发布局（镜像在仓库的 src/boot，旁边有 packages/tools.build/src）下不用：那是 pack 留下的构建产物，会落后于源码
            string tools = Path.Combine(Path.GetDirectoryName(boot) ?? ".", "tools");
            bool devLayout = File.Exists(Path.Combine(Path.GetDirectoryName(boot) ?? ".", "..", "..", "packages", "tools.build", "src", "clojure", "tools", "build", "api.clj"));
            if (Directory.Exists(tools) && !devLayout)
                foreach (string f in Directory.GetFiles(tools, "*.dll"))
                    if (File.GetLastWriteTimeUtc(f) >= File.GetLastWriteTimeUtc(boot)) PackageIndex.RegisterFile(f);
            // 开发布局（镜像在 src/boot/）：src/clojure 的源码也在加载路径上（JVM 的 clojure.jar 带着源码；按需加载的
            // clojure.core.reducers 等由此可 require）
            string src = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(boot) ?? ".", "..", "clojure"));
            if (File.Exists(Path.Combine(src, "clojure", "core.clj"))) Loader.AddPath(src);
            if (StartupTimings.Enabled) StartupTimings.Mark(StartupTimings.Segment.Packages);
            // 编译器（cleat-090 / 091）：Clojure 写的驱动登记钩子。--boot 的模块不是带 clojure.core 的镜像时没有编译器（只能跑镜像自己的代码）
            if (RT.var("clojure.core", "require").isBound)
            {
                RT.var("clojure.core", "require").invoke(Symbol.intern(CompilerNs + ".driver"));
                if (StartupTimings.Enabled) StartupTimings.Mark(StartupTimings.Segment.CompilerRequire);
                RT.var(CompilerNs + ".driver", "install-hooks!").invoke();
                if (StartupTimings.Enabled) StartupTimings.Mark(StartupTimings.Segment.CompilerHooks);
            }
            CompilerMs = sw.ElapsedMilliseconds - ClojureImageMs;
        }
        // cleat-159：分段计时（调用点自己判 CLEAT_TIME；未启用时不输出，第一行还是 cleat-052 的格式由 Report 负责）
        if (StartupTimings.Enabled) StartupTimings.Report(ClojureImageMs, CompilerMs, CompilerFromPackage);
        RuntimeBootstrap.MarkStarted();
        return boot;
    }

    /// <summary>
    /// 自动找镜像：宿主目录，以及当前目录与宿主目录各自的上级目录里的 src/boot/名字（开发布局：宿主在 src/Cleat.Host/bin/…）。
    /// </summary>
    public static string FindBoot(string name)
    {
        var candidates = new List<string> { Path.Combine(AppContext.BaseDirectory, name) };
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var d = new DirectoryInfo(start); d != null; d = d.Parent)
                candidates.Add(Path.Combine(d.FullName, "src", "boot", name));
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// 把 clojure.core 的公有 Var 映射进 ns（clojure.main 在 user 里做的 refer；没有映射的名字才加）。clojure.core 加载之前只有 RT 自己
    /// 定义的 in-ns、ns、load-file、*ns*、*file*、*1 … 等。
    /// </summary>
    public static void ReferClojureCore(Namespace ns)
    {
        var core = Namespace.find(Symbol.intern("clojure.core"));
        if (core == null || core == ns) return;
        for (ISeq s = RT.seq(core.getMappings()); s != null; s = s.next())
        {
            var e = (IMapEntry)s.first();
            if (e.val() is Var v && v.Namespace == core && v.isPublic && ns.GetMapping((Symbol)e.key()) == null)
                ns.refer((Symbol)e.key(), v);
        }
    }

    /// <summary>读一段文本的全部形式并逐个经 eval 钩子求值，返回最后一个值。</summary>
    public static object EvalString(string text, string sourceName) => EvalReader(new StringReader(text), sourceName);

    public static object EvalReader(TextReader reader, string sourceName, Action<object> each = null)
    {
        var eval = RuntimeHooks.Require(RuntimeHooks.Eval, "eval");
        var r = new LineNumberingTextReader(reader);
        var opts = RT.map(Keyword.intern("read-cond"), Keyword.intern("allow"));
        object eof = new object(), last = null;
        while (true)
        {
            object form = LispReader.read(r, false, eof, false, opts);
            if (form == eof) return last;
            last = eval(form);
            each?.Invoke(last);
        }
    }
}
