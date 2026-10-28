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
///   3. 编译器（cleat-090 / 091，延迟加载 cleat-161）：驱动 cleat.compiler.driver（镜像旁的包；自举时 CLEAT_COMPILER_NS
///      指定改名的工具编译器，从 -I 的源码加载）登记运行时钩子 eval / load-source / compile / macroexpand-1 与镜像、包的
///      编译入口。Start **不再无条件加载它**——只登记 <see cref="EnsureCompiler"/> 作为惰性入口，第一次真正用到编译时
///      （读上面四个钩子、<see cref="EvalForm"/> 的非平凡形式）才加载。
///      例外：自举的改名工具编译器（CLEAT_COMPILER_NS != cleat.compiler）保持现状，仍然 eager。
/// 另有 <see cref="ReferClojureCore"/>（user 对 clojure.core 的 refer）与 <see cref="EvalReader"/>（经 <see cref="EvalForm"/> 逐形式求值）。
/// </summary>
public static class Startup
{
    /// <summary>运行着的镜像（Start 之后；没有 Clojure 镜像时为 null）。tools.build 的 uber 据此复制运行时（cleat-066）。</summary>
    public static string ClojureBootPath;

    /// <summary>启动耗时（毫秒）：Clojure 镜像、编译器（加载驱动并登记钩子；cleat-161 起可能是启动报告之后按需加载的那次，
    /// 没加载时是 0）；CLEAT_TIME 设置时写到标准错误。</summary>
    public static long ClojureImageMs, CompilerMs;

    /// <summary>编译器从镜像旁的包程序集加载（否则从 -I 的源码加载：自举时的改名工具编译器）。</summary>
    public static bool CompilerFromPackage;

    /// <summary>Clojure 编译器的命名空间前缀（CLEAT_COMPILER_NS，缺省 cleat.compiler；驱动是 前缀.driver）。</summary>
    private static string CompilerNs => Environment.GetEnvironmentVariable("CLEAT_COMPILER_NS") is string ns && ns != "" ? ns : "cleat.compiler";

    // ---- 编译器的延迟加载（cleat-161，designs/29 §3.3）----

    /// <summary>编译器已加载（钩子已登记）。volatile：惰性入口外部也读它。</summary>
    private static volatile bool s_compilerLoaded;

    /// <summary>这次启动没有可用的编译器（没有镜像，或 clojure.core/require 未绑定）：EnsureCompiler 之后零成本返回。</summary>
    private static bool s_compilerUnavailable;

    /// <summary>惰性入口已登记（Start 走到"有编译器但还没加载"）：平凡形式的快速求值只在这条路径上生效（cleat-161）。</summary>
    private static bool s_compilerLazy;

    private static readonly object s_compilerLock = new();

    /// <summary>正在加载编译器的那条线程：驱动自己也要读这些钩子（load-source / eval），重入时直接返回，不能自己等自己。</summary>
    [ThreadStatic] private static bool t_loadingCompiler;

    /// <summary>要 require 的驱动命名空间（Start 定下，之后不再读环境变量）。</summary>
    private static string s_driverNs;

    /// <summary>编译器是否已经加载（cleat-161：诊断与测试用）。</summary>
    public static bool CompilerLoaded => s_compilerLoaded;

    /// <summary>初始化运行时、加载镜像、登记编译器的惰性入口；返回 Clojure 镜像路径（没有时 null）。</summary>
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
            // 编译器（cleat-090 / 091，延迟加载 cleat-161）：Clojure 写的驱动登记钩子。--boot 的模块不是带 clojure.core 的
            // 镜像时没有编译器（只能跑镜像自己的代码）。这里只定下"能不能加载、加载哪个驱动"，不在这里加载：
            // -e nil 与只调 AOT 函数的 -m / -X / -T 整格（159 量到 206 ~ 212 ms）都不该付。
            s_driverNs = CompilerNs + ".driver";
            if (!RT.var("clojure.core", "require").isBound) s_compilerUnavailable = true;
            else if (CompilerNs != "cleat.compiler") EnsureCompiler();     // 自举的改名工具编译器：保持现状，仍然 eager
            else
            {
                s_compilerLazy = true;
                RuntimeHooks.EnsureCompiler = EnsureCompiler;
            }
        }
        // cleat-159：分段计时（调用点自己判 CLEAT_TIME；未启用时不输出，第一行还是 cleat-052 的格式由 Report 负责）
        if (StartupTimings.Enabled) StartupTimings.Report(ClojureImageMs, CompilerMs, CompilerFromPackage);
        RuntimeBootstrap.MarkStarted();
        return boot;
    }

    /// <summary>
    /// 加载编译器驱动并登记钩子（cleat-161，designs/29 §3.3）：幂等，第一次真正需要编译 / 求值时才执行。
    /// 同一条线程重入直接返回（加载编译器这条路自己要用这些钩子，不能自己等自己）；别的线程在锁上等它加载完。
    /// 没有编译器可用（没跑过 Start、没有镜像、require 未绑定）时零成本返回，调用方照旧拿到"未登记"的明确错误。
    /// </summary>
    public static void EnsureCompiler()
    {
        if (s_compilerLoaded || s_compilerUnavailable) return;
        // 没跑过 Start（或没有镜像、require 未绑定）：没有可加载的编译器，记成不可用后零成本返回
        if (s_driverNs is null || !RT.var("clojure.core", "require").isBound) { s_compilerUnavailable = true; return; }
        if (t_loadingCompiler) return;
        lock (s_compilerLock)
        {
            if (s_compilerLoaded || s_compilerUnavailable) return;
            if (s_driverNs is null) { s_compilerUnavailable = true; return; }
            if (t_loadingCompiler) return;
            t_loadingCompiler = true;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                RT.var("clojure.core", "require").invoke(Symbol.intern(s_driverNs));
                if (StartupTimings.Enabled) StartupTimings.Mark(StartupTimings.Segment.CompilerRequire);
                RT.var(s_driverNs, "install-hooks!").invoke();
                if (StartupTimings.Enabled) StartupTimings.Mark(StartupTimings.Segment.CompilerHooks);
                CompilerMs = sw.ElapsedMilliseconds;
                s_compilerLoaded = true;
            }
            finally { t_loadingCompiler = false; }
        }
        // 启动报告之后才加载（延迟加载的正路）：补一行，别让 CLEAT_TIME 的 compiler 段看起来是免费的
        if (StartupTimings.Enabled) StartupTimings.CompilerLoadedOnDemand(CompilerMs, CompilerFromPackage);
    }

    /// <summary>
    /// 求值一个形式（cleat-161）：编译器还没加载时，**平凡形式**（自求值常量、quote、递归不含符号的字面量集合）
    /// 直接在 C# 侧给出值，不为它加载编译器；其余先 EnsureCompiler 再走 eval 钩子。
    /// <c>CLEAT_NO_SIMPLE_EVAL</c> 设置时不走快速路径（与驱动侧的简单形式解释器同一口径）。
    /// 惰性入口没登记（没跑过 Start，或宿主 / 测试自己登记的 eval 钩子）时与 cleat-161 之前逐字同路。
    /// </summary>
    public static object EvalForm(object form)
    {
        if (s_compilerLazy && !s_compilerLoaded)
        {
            if (Cleat.Core.Compiler.DriverOps.SimpleEvalEnabled && SimpleForms.Try(form, out object trivial)) return trivial;
            EnsureCompiler();
        }
        return RuntimeHooks.Require(RuntimeHooks.Eval, "eval")(form);
    }

    /// <summary>
    /// 取求值入口（cleat-161）：编译器还没加载且惰性入口在时给 <see cref="EvalForm"/>（首个平凡输入不触发加载），
    /// 否则就是 eval 钩子本身——没有编译器时与以前一样在这里报"未登记"，而不是读出一个形式之后。
    /// </summary>
    public static Func<object, object> EvalEntry() =>
        s_compilerLazy && !s_compilerLoaded ? EvalForm : RuntimeHooks.Require(RuntimeHooks.Eval, "eval");

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

    /// <summary>读一段文本的全部形式并逐个求值（cleat-161：经 <see cref="EvalForm"/>，平凡形式不为它加载编译器），返回最后一个值。</summary>
    public static object EvalString(string text, string sourceName) => EvalReader(new StringReader(text), sourceName);

    public static object EvalReader(TextReader reader, string sourceName, Action<object> each = null)
    {
        var eval = EvalEntry();     // cleat-161：惰性入口在且编译器没加载时逐形式走 EvalForm
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
