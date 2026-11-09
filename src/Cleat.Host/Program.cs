using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cleat.Core.Runtime;
using clojure.lang;

namespace Cleat.Host;

/// <summary>
/// 命令行宿主（designs/02-runtime.md §9、cleat-030、cleat-052）。
///
///   cleat [--boot dll] [-I dir]… [-e expr]… [-i file]… [-m ns | script args… | -r | -]
///
/// 启动顺序：
///   1. 登记 cleat.kernel 的原语 → RT.Init；
///   2. 加载 Clojure 镜像 cleat.boot.dll（--boot，或 CLEAT_BOOT，或自动找 src/boot/）并设置 BootInterface.CurrentImageId，登记镜像旁的
///      包程序集（随附库、tools）；
///   3. 登记编译器（C#，程序集 Cleat.Compiler，designs/30）的惰性入口：第一次用到编译时加载，由它登记运行时钩子
///      eval / load-source / compile / macroexpand-1 与镜像、包的编译入口（cleat-090 / 161）；
///   4. 按 clojure.main 的语义执行 -e / -i / -m / 脚本，否则 REPL。
/// 主体在 512 MB 栈的线程上运行（分析器与编译器递归深）。
/// </summary>
public static class Program
{
    public sealed class Options
    {
        public string Boot;
        public readonly List<string> IncludeDirs = new List<string>();
        public readonly List<(string Kind, string Value)> Inits = new List<(string, string)>();   // -e / -i
        public string MainNs;          // -m
        public string Script;          // 脚本路径
        public bool Repl;              // -r 或无其他动作
        public string[] ScriptArgs = Array.Empty<string>();
        public bool Help, Version;
        public string CompileBoot;     // --compile-boot out.dll：编译 Clojure 镜像（cleat-053）
        public bool NoBoot;            // --no-boot：不加载镜像（cleat-181）
        public string BootOrder;       // --boot-order edn（缺省 out.dll 旁的 boot-order.edn）
        public string CompilePackage;  // --compile-package out.dll：编包程序集（cleat-067），其余参数是命名空间
        public string PackageName;     // --package 名字（缺省 dll 的文件名）
        public readonly List<string> PackageRoots = new List<string>();      // --root 源码根目录（缺省全部 -I）
        public string TargetImage;     // --target-image 镜像.dll：包的镜像核对用它的 id（缺省正在运行的镜像；cleat-091 起，为另一个镜像编包）
        public readonly List<string> PackageNamespaces = new List<string>();
        public string Report;          // --report stdio|stderr：REPL / -e 的错误报告写到哪（clojure.main 的语义）
        public bool Debug;             // -g：调试优先（cleat-158），关跨 fn 内联，让每个 Clojure 函数保留物理栈帧
        public string DepsAliases;     // -M:alias…（clj -M）：按 cleat.edn 的别名建 basis 再运行（cleat-071），其余参数在 ScriptArgs
        public string ExecMode, ExecAlias;   // -X:alias… / -T:alias（clj -X / -T）：调函数，参数 map；函数名与键值对在 ScriptArgs（cleat-071、072）
        public string SDeps;           // -Sdeps EDN：并在 cleat.edn 之上（clj -Sdeps）
        public bool SPath, Prepare;    // -Spath：打印加载路径；-P：只解析与下载依赖（clj -Spath / -P）
    }

    public static Options Parse(string[] args)
    {
        var o = new Options();
        int i = 0;
        for (; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "--boot" when i + 1 < args.Length: o.Boot = args[++i]; break;
                case "--no-boot": o.NoBoot = true; break;   // 不加载镜像（cleat-181：C# 编译器从源码编镜像）
                case "-I" when i + 1 < args.Length: o.IncludeDirs.Add(args[++i]); break;
                case "--compile-boot" when i + 1 < args.Length: o.CompileBoot = args[++i]; break;
                case "--boot-order" when i + 1 < args.Length: o.BootOrder = args[++i]; break;
                case "--compile-package" when i + 1 < args.Length: o.CompilePackage = args[++i]; break;
                case "--package" when i + 1 < args.Length: o.PackageName = args[++i]; break;
                case "--root" when i + 1 < args.Length: o.PackageRoots.Add(args[++i]); break;
                case "--target-image" when i + 1 < args.Length: o.TargetImage = args[++i]; break;
                case "--report" when i + 1 < args.Length && (args[i + 1] == "stdio" || args[i + 1] == "stderr"): o.Report = args[++i]; break;
                case "-e": case "--eval": if (i + 1 >= args.Length) throw new ArgumentException("-e needs an expression"); o.Inits.Add(("eval", args[++i])); break;
                case "-i": case "--init": if (i + 1 >= args.Length) throw new ArgumentException("-i needs a file"); o.Inits.Add(("init", args[++i])); break;
                case "-m": case "--main": if (i + 1 >= args.Length) throw new ArgumentException("-m needs a namespace"); o.MainNs = args[++i]; i++; goto done;
                case "-r": case "--repl": o.Repl = true; i++; goto done;
                case "-g": o.Debug = true; break;
                case "-h": case "--help": o.Help = true; return o;
                case "--version": o.Version = true; return o;
                case "-": o.Script = "-"; i++; goto done;
                case var m when m.StartsWith("-M", StringComparison.Ordinal) && (m.Length == 2 || m[2] == ':'):
                    o.DepsAliases = a; i++; goto done;
                case var x when x.StartsWith("-X", StringComparison.Ordinal) && (x.Length == 2 || x[2] == ':'):
                    o.ExecMode = "X"; o.ExecAlias = a; i++; goto done;
                case var t when t.StartsWith("-T", StringComparison.Ordinal) && t.Length > 2:
                    // -T:build (an alias) and -Tbuild (clj's installed-tool spelling) both name the tool
                    o.ExecMode = "T"; o.ExecAlias = t[2] == ':' ? a : "-T:" + a.Substring(2); i++; goto done;
                case "-Sdeps" when i + 1 < args.Length: o.SDeps = args[++i]; break;
                case "-Spath": o.SPath = true; break;
                case "-P": o.Prepare = true; break;
                case var al when al.StartsWith("-A", StringComparison.Ordinal):
                    throw new ArgumentException("-A is not supported (deprecated in the Clojure CLI): use -M:alias for a REPL or -main, -X:alias for a function");
                default:
                    if (a.StartsWith("-", StringComparison.Ordinal) && a.Length > 1) throw new ArgumentException("unknown option: " + a);
                    if (o.CompilePackage != null) { o.PackageNamespaces.Add(a); break; }
                    o.Script = a; i++; goto done;
            }
        }
    done:
        o.ScriptArgs = args.Skip(i).ToArray();
        if (o.Script == null && o.MainNs == null && !o.Repl && o.Inits.Count == 0 && o.CompileBoot == null && o.CompilePackage == null
            && o.DepsAliases == null && o.ExecMode == null && !o.SPath && !o.Prepare) o.Repl = true;
        return o;
    }

    public static int Main(string[] args)
    {
        // cleat-159：CLEAT_TIME 没设置时连 Begin 都不调，StartupTimings 的方法一个都不进 JIT 队列（时间线起点含大栈线程的创建）
        if (Environment.GetEnvironmentVariable("CLEAT_TIME") != null) StartupTimings.Begin();
        if (GcProfile.ValidateEnvironment() is string gcError)
        {
            Console.Error.WriteLine("cleat: " + gcError);
            return 2;
        }
        int exitCode = 0;
        var thread = new System.Threading.Thread(() => exitCode = MainBody(args), 512 * 1024 * 1024);
        thread.Start();
        thread.Join();
        return exitCode;
    }

    private static int MainBody(string[] args)
    {
        if (StartupTimings.Enabled) StartupTimings.Mark(StartupTimings.Segment.Host);     // 宿主入口与线程创建（cleat-159）
        Options o;
        try { o = Parse(args); }
        catch (ArgumentException e)
        {
            Console.Error.WriteLine("cleat: " + e.Message);
            return Usage();
        }
        if (StartupTimings.Enabled) StartupTimings.Mark(StartupTimings.Segment.Cli);      // 命令行解析（cleat-159）
        if (o.Help) return Usage();
        if (o.Version) { Console.WriteLine(CoreInfo.Banner); return 0; }
        // -g 必须在编译器加载前生效：FormAnalyzer.InlineCalls 的初始值读这个变量
        if (o.Debug) Environment.SetEnvironmentVariable("CLEAT_NO_INLINE", "1");
        try
        {
            Start(o);
            Cleat.Core.Compiler.Diagnostics.ReportToStdOut = ReportToStdOut = o.Report == "stdio";
            return RunActions(o);
        }
        catch (Exception e)
        {
            Report(e);
            return 1;
        }
        finally
        {
            Console.Out.Flush();
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("usage: cleat [--boot dll] [-I dir]... [-g] [-e expr]... [-i file]... [-m ns args... | script args... | -r | -]");
        Console.Error.WriteLine("       cleat [--boot dll] -I src/clojure --compile-boot out/cleat.boot.dll [--boot-order boot-order.edn]");
        Console.Error.WriteLine("       cleat -I src... --compile-package out/Name.dll [--package name] [--root dir]... [--target-image boot.dll] ns...");
        Console.Error.WriteLine("       cleat [-Sdeps EDN] [-Spath | -P] -M[:alias...] [options and args...]   run the project (cleat.edn), as clj -M");
        Console.Error.WriteLine("       cleat [-Sdeps EDN] -X[:alias...] [ns/fn] [:key value]...   call a function with a map, as clj -X");
        Console.Error.WriteLine("       cleat -T:alias function [:key value]...    a tool function without the project's deps (-T:build: build.clj), as clj -T");
        return 2;
    }

    /// <summary>运行着的镜像（Start 之后；没有 Clojure 镜像时为 null）。tools.build 的 uber 据此复制运行时（cleat-066）。启动逻辑在 <see cref="Startup"/>。</summary>
    public static string ClojureBootPath => Startup.ClojureBootPath;

    /// <summary>初始化运行时、加载镜像、加载编译器并登记钩子（<see cref="Startup.Start"/>）；返回 Clojure 镜像路径（没有时 null）。</summary>
    public static string Start(Options o)
    {
        var so = new StartupOptions { Boot = o.Boot, CompilePackage = o.CompilePackage, NoBoot = o.NoBoot };
        so.IncludeDirs.AddRange(o.IncludeDirs);
        return Startup.Start(so);
    }

    /// <summary>user 对 clojure.core 的 refer（<see cref="Startup.ReferClojureCore"/>）。</summary>
    public static void ReferClojureCore(Namespace ns) => Startup.ReferClojureCore(ns);

    private static int RunActions(Options o)
    {
        var ns = Namespace.findOrCreate(Symbol.intern("user"));
        ReferClojureCore(ns);
        Var.pushThreadBindings(MainBindings(ns, o));
        if (StartupTimings.Enabled) StartupTimings.Mark(StartupTimings.Segment.UserSetup);    // cleat-159：user 命名空间的 refer 与主绑定
        try
        {
            if (o.CompileBoot != null) return CompileBootImage(o);
            if (o.CompilePackage != null) return CompilePackageAssembly(o);
            if (o.SPath || o.Prepare) return PathOrPrepare(o);
            if (o.DepsAliases != null) return RunDeps(o);
            if (o.ExecMode != null) return RunExec(o);
            foreach (var (kind, value) in o.Inits)
            {
                if (kind == "eval") EvalReader(new StringReader(value), "<eval>", v => { if (v != null) Print(v); });   // clojure.main：每个非 nil 值都打印
                else Loader.LoadFile(value);
            }
            if (o.MainNs != null)
            {
                Loader.Load(o.MainNs.Replace('.', '/').Replace('-', '_'));
                var main = Namespace.find(Symbol.intern(o.MainNs))?.FindInternedVar(Symbol.intern("-main"))
                           ?? throw new InvalidOperationException($"namespace {o.MainNs} has no -main");
                ((IFn)main.deref()).applyTo(RT.seq(o.ScriptArgs));
                return 0;
            }
            if (o.Script == "-") { EvalReader(Console.In, "<stdin>"); return 0; }
            if (o.Script != null) { Loader.LoadFile(o.Script); return 0; }
            if (o.Repl)
            {
                ReplRequires();
                if (StartupTimings.Enabled) StartupTimings.Mark(StartupTimings.Segment.ReplRequire);     // repl-requires（doc/source/pst 那批库）
                return !Console.IsInputRedirected && !Console.IsOutputRedirected
                    ? InteractiveRepl(Console.Out, Console.Error)
                    : Repl(Console.In, Console.Out, Console.Error);
            }
            return 0;
        }
        finally { Var.popThreadBindings(); }
    }

    /// <summary>
    /// clojure.main/with-bindings 的绑定（cleat-032）：脚本、-e、-m 与 REPL 都在其中运行，*1 *2 *3 *e 为 nil、
    /// *print-namespace-maps* 为 true、其余可 set! 的 Var 绑定为当前值（*compile-path* 不绑：compile 的包程序集输出位置见 AotPaths.CompileOutputPath）。clojure.core 未加载（--no-boot）时只绑 *ns* 与 *command-line-args*。
    /// </summary>
    private static IPersistentMap MainBindings(Namespace ns, Options o)
    {
        var m = RT.map(RT.CurrentNSVar, ns, RT.CmdLineArgsVar, RT.seq(o.ScriptArgs));
        var core = Namespace.find(Symbol.intern("clojure.core"));
        if (core?.FindInternedVar(Symbol.intern("*print-namespace-maps*")) == null) return m;
        Var V(string n) => core.FindInternedVar(Symbol.intern(n));
        foreach (var n in new[] { "*warn-on-reflection*", "*math-context*", "*print-meta*", "*print-length*", "*print-level*",
                                  "*data-readers*", "*default-data-reader-fn*", "*unchecked-math*", "*assert*" })
            if (V(n) is Var v) m = m.assoc(v, v.deref());
        m = m.assoc(V("*print-namespace-maps*"), true);
        foreach (var n in new[] { "*1", "*2", "*3", "*e" })
            if (V(n) is Var v && v.isDynamic()) m = m.assoc(v, null);
        return m;
    }

    /// <summary>
    /// 编译 Clojure 镜像（cleat-053，designs/08 §3）：按 boot-order.edn 依次 load cleat.kernel、clojure.core，require 其余
    /// <c>:boot true</c> 且不是 <c>:loaded-by</c> 的命名空间，全部加载到的形式编成一个程序集。
    /// </summary>
    private static int CompileBootImage(Options o)
    {
        string dll = Path.GetFullPath(o.CompileBoot);
        string orderPath = o.BootOrder ?? Path.Combine(Path.GetDirectoryName(dll) ?? ".", "boot-order.edn");
        var forms = BootForms(File.ReadAllText(orderPath));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Startup.EnsureCompiler();     // cleat-161：编译入口由驱动登记，先按需加载它（--compile-* 本来就要用编译器）
        // 已运行着 Clojure 镜像时（不动点的第二段）：*loaded-libs* 绑到空集，require 与从源码构建时一样真正加载每个库
        var core = Namespace.find(Symbol.intern("clojure.core"));
        var loadedLibs = core?.FindInternedVar(Symbol.intern("*loaded-libs*"));
        if (loadedLibs != null) Var.pushThreadBindings(RT.map(loadedLibs, new Ref(PersistentTreeSet.EMPTY)));
        Cleat.Core.Runtime.Loader.ReloadImageFiles = true;   // 镜像里的文件要从源码重新加载（平时 RT.load 它们是空操作）
        try
        {
            CompileHook(Cleat.Core.Compiler.DriverOps.CompileBootHook).invoke(forms, dll);
        }
        finally
        {
            Cleat.Core.Runtime.Loader.ReloadImageFiles = false;
            if (loadedLibs != null) Var.popThreadBindings();
        }
        Console.Error.WriteLine($"cleat: compiled the Clojure boot image ({forms.Count} entries) in {sw.ElapsedMilliseconds} ms -> {dll}");
        return 0;
    }

    /// <summary>编译器登记的编译入口（CompilerHooks.Install）；没有编译器时报错。
    /// 调用前先 <see cref="Startup.EnsureCompiler"/>（cleat-161：编译器现在可能还没加载）。</summary>
    private static IFn CompileHook(IFn hook) =>
      hook ?? throw new InvalidOperationException("the compiler is not loaded (assembly Cleat.Compiler is not available to this host)");

    /// <summary>
    /// 编包程序集（cleat-067，designs/17 §2.2）：依次 require 命令行上的命名空间，--root（缺省全部 -I）下被加载到的源码文件编成一个
    /// 程序集，每个文件一个入口，程序集级特性 CleatPackage / CleatNamespace 作索引。
    /// </summary>
    private static int CompilePackageAssembly(Options o)
    {
        string dll = Path.GetFullPath(o.CompilePackage);
        if (o.PackageNamespaces.Count == 0) throw new ArgumentException("--compile-package needs at least one namespace");
        Directory.CreateDirectory(Path.GetDirectoryName(dll)!);
        Startup.EnsureCompiler();     // cleat-161：同上，编译入口要编译器
        var forms = o.PackageNamespaces
            .Select(ns => (object)RT.list(Symbol.intern("clojure.core", "require"), RT.list(Symbol.intern("quote"), Symbol.intern(ns))))
            .ToList();
        var roots = (o.PackageRoots.Count > 0 ? o.PackageRoots : o.IncludeDirs).Select(Path.GetFullPath).ToList();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string imageId = o.TargetImage == null ? null : Cleat.Core.Compiler.DriverOps.Mvid(Path.GetFullPath(o.TargetImage)).ToString();
        CompileHook(Cleat.Core.Compiler.DriverOps.CompilePackageHook).invoke(o.PackageName ?? Path.GetFileNameWithoutExtension(dll), forms, roots, dll, imageId);
        Console.Error.WriteLine($"cleat: compiled package {Path.GetFileNameWithoutExtension(dll)} in {sw.ElapsedMilliseconds} ms -> {dll}");
        return 0;
    }

    /// <summary>
    /// cleat -M:alias… args（clj -M，cleat-071）：tools.build 的 deps-args 按 cleat.edn 与别名算出 basis 的加载路径与别名的
    /// :cleat-opts / :main-opts；加载路径加进本进程，再按 那些选项 + 其余参数 重新解析命令行运行（同一进程，不另起 cleat）。
    /// </summary>
    private static int RunDeps(Options o)
    {
        EnsureToolsBuild();
        RequireNs("clojure.tools.build.main");
        var r = (IPersistentMap)RT.var("clojure.tools.build.main", "deps-args").invoke(o.DepsAliases, o.SDeps);
        foreach (var p in (System.Collections.IEnumerable)r.valAt(Keyword.intern("load-path")))
            Loader.AddPath(Path.GetFullPath(p.ToString()!));
        var args = ((System.Collections.IEnumerable)r.valAt(Keyword.intern("args"))).Cast<object>().Select(x => x.ToString()!)
            .Concat(o.ScriptArgs).ToArray();
        var o2 = Parse(args);
        if (o2.Help) return Usage();
        if (o2.DepsAliases != null || o2.ExecMode != null || o2.CompileBoot != null || o2.CompilePackage != null)
            throw new ArgumentException("an alias's :main-opts can't run -M, -T, --compile-boot or --compile-package");
        o2.Report ??= o.Report;
        foreach (var d in o2.IncludeDirs) Loader.AddPath(Path.GetFullPath(d));
        return RunActions(o2);
    }

    /// <summary>
    /// cleat -X:alias… [ns/fn] :key value… 与 cleat -T:alias fn :key value…（clj -X / -T，cleat-072）：tools.build 的 exec! 在本进程里
    /// 加上加载路径（-X：项目的 basis；-T：工具别名的 :deps / :paths，缺省只有项目根目录），调函数，参数是一个 map。
    /// </summary>
    private static int RunExec(Options o)
    {
        EnsureToolsBuild();
        RequireNs("clojure.tools.build.main");
        RT.var("clojure.tools.build.main", "exec!").invoke(o.ExecMode, o.ExecAlias, RT.seq(o.ScriptArgs), o.SDeps);
        return 0;
    }

    /// <summary>
    /// -Spath（打印加载路径，按路径分隔符连接）与 -P（只解析、下载依赖，不运行）：用 -M / -X 的别名（没有时项目本身）建 basis。
    /// </summary>
    private static int PathOrPrepare(Options o)
    {
        EnsureToolsBuild();
        RequireNs("clojure.tools.build.main");
        string alias = o.DepsAliases ?? (o.ExecMode == "X" ? "-M" + o.ExecAlias.Substring(2) : "-M");
        var r = (IPersistentMap)RT.var("clojure.tools.build.main", "deps-args").invoke(alias, o.SDeps);
        if (o.SPath)
        {
            var path = ((System.Collections.IEnumerable)r.valAt(Keyword.intern("load-path"))).Cast<object>().Select(x => x.ToString());
            Console.Out.WriteLine(string.Join(Path.PathSeparator, path));
        }
        return 0;
    }

    private static void RequireNs(string ns) =>
        RT.var("clojure.core", "require").invoke(Symbol.intern(ns));

    /// <summary>
    /// tools.build 在哪（cleat-071）：随附的包程序集（镜像旁的 tools/，Start 登记）；或加载路径上已有它的源码；或开发布局下仓库的
    /// packages/tools.build/src 与 packages/tools.namespace/src（从镜像与宿主目录往上找）。都没有时报错。
    /// </summary>
    private static void EnsureToolsBuild()
    {
        const string api = "clojure/tools/build/api";
        if (PackageIndex.Contains(api)) return;
        if (Loader.LoadPath.Any(d => File.Exists(Path.Combine(d, api + ".clj")))) return;
        var starts = new[] { Path.GetDirectoryName(ClojureBootPath ?? "") ?? "", AppContext.BaseDirectory };
        foreach (var start in starts.Where(s => s.Length > 0))
            for (var d = new DirectoryInfo(start); d != null; d = d.Parent)
            {
                string tb = Path.Combine(d.FullName, "packages", "tools.build", "src"), tn = Path.Combine(d.FullName, "packages", "tools.namespace", "src");
                if (File.Exists(Path.Combine(tb, api + ".clj")) && Directory.Exists(tn))
                {
                    Loader.AddPath(tb);
                    Loader.AddPath(tn);
                    return;
                }
            }
        throw new InvalidOperationException("tools.build is not available for -M / -T: this cleat has no bundled tools.build (the Cleat.Cli tool has it) "
            + "and none is on the load path (-I .../tools.build/src -I .../tools.namespace/src)");
    }

    /// <summary>boot-order.edn → 加载形式：kernel 与 core 用 RT.load（core 之前没有 require），其余 (clojure.core/require 'ns)。</summary>
    public static List<object> BootForms(string edn)
    {
        var order = (IPersistentMap)EdnReader.readString(edn, PersistentHashMap.EMPTY);
        var forms = new List<object>();
        Keyword K(string n) => Keyword.intern(n);
        for (ISeq s = RT.seq(order.valAt(K("entries"))); s != null; s = s.next())
        {
            var e = (IPersistentMap)s.first();
            if (!RT.booleanCast(e.valAt(K("boot"))) || e.valAt(K("loaded-by")) != null) continue;
            var ns = (Symbol)e.valAt(K("ns"));
            string path = (string)e.valAt(K("path"));
            if (ns.Name == "cleat.kernel" || ns.Name == "clojure.core")
                forms.Add(RT.list(Symbol.intern("clojure.lang.RT", "load"), path.Substring(0, path.LastIndexOf('.'))));
            else
                forms.Add(RT.list(Symbol.intern("clojure.core", "require"), RT.list(Symbol.intern("quote"), ns)));
        }
        return forms;
    }

    /// <summary>clojure.main 的 REPL 在 user 里 (apply require clojure.main/repl-requires)（doc、source、pst …）；clojure.main 已加载时照做。</summary>
    public static void ReplRequires()
    {
        var main = Namespace.find(Symbol.intern("clojure.main"));
        var reqs = main?.FindInternedVar(Symbol.intern("repl-requires"));
        var require = Namespace.find(Symbol.intern("clojure.core"))?.FindInternedVar(Symbol.intern("require"));
        if (reqs == null || require == null) return;
        // 逐个 require：不在镜像里、加载路径上又没有源码的库（clojure.repl.deps）跳过
        for (ISeq s = RT.seq(reqs.deref()); s != null; s = s.next())
        {
            try { ((IFn)require.deref()).invoke(s.first()); }
            catch (Exception) { }
            if (StartupTimings.Enabled) StartupTimings.MarkReplRequire(s.first() + "");    // cleat-159：repl-requires 的逐库份额
        }
    }

    /// <summary>读一段文本的全部形式并逐个经 eval 钩子求值，返回最后一个值（<see cref="Startup.EvalReader"/>）。</summary>
    public static object EvalString(string text, string sourceName) => Startup.EvalString(text, sourceName);

    public static object EvalReader(TextReader reader, string sourceName, Action<object> each = null) => Startup.EvalReader(reader, sourceName, each);

    private static void Print(object x)
    {
        Console.Out.WriteLine(RT.printString(x));
        Console.Out.Flush();
    }

    /// <summary>REPL 的 *1 *2 *3 *e（clojure.main 的绑定）+ user 对 clojure.core 的 refer；返回要 pushThreadBindings 的映射。</summary>
    private static IPersistentMap ReplBindings(out Var v1, out Var v2, out Var v3, out Var ve)
    {
        var core = Namespace.findOrCreate(Symbol.intern("clojure.core"));
        v1 = Var.intern(core, Symbol.intern("*1"), null).setDynamic();
        v2 = Var.intern(core, Symbol.intern("*2"), null).setDynamic();
        v3 = Var.intern(core, Symbol.intern("*3"), null).setDynamic();
        ve = Var.intern(core, Symbol.intern("*e"), null).setDynamic();
        ReferClojureCore((Namespace)RT.CurrentNSVar.deref());
        return RT.map(v1, null, v2, null, v3, null, ve, null);
    }

    /// <summary>最简 REPL：逐个形式读—求值—打印，*1 *2 *3 *e，:repl/quit 退出。标准输入被重定向时用（终端上走 InteractiveRepl）。</summary>
    public static int Repl(TextReader input, TextWriter output, TextWriter error)
    {
        var eval = Startup.EvalEntry();     // cleat-161：编译器没加载时逐形式走 EvalForm，首个平凡输入不为它加载编译器
        var r = new LineNumberingTextReader(input);
        var opts = RT.map(Keyword.intern("read-cond"), Keyword.intern("allow"));
        object eof = new object();
        var quit = Keyword.intern("repl", "quit");
        Var.pushThreadBindings(ReplBindings(out var v1, out var v2, out var v3, out var ve));
        try
        {
            while (true)
            {
                output.Write(((Namespace)RT.CurrentNSVar.deref()).Name + "=> ");
                output.Flush();
                if (StartupTimings.Enabled) StartupTimings.ReplPromptShown();       // cleat-159：首个提示符（输入被重定向的 REPL）
                object form;
                try
                {
                    form = LispReader.read(r, false, eof, false, opts);
                }
                catch (Exception e)
                {
                    Report(e, error);
                    ve.set(e);
                    continue;
                }
                if (form == eof) { output.WriteLine(); return 0; }
                if (quit.Equals(form)) return 0;
                EvalPrint(eval, output, error, form, v1, v2, v3, ve);
            }
        }
        finally { Var.popThreadBindings(); }
    }

    /// <summary>终端 REPL（cleat-034）：行编辑器读入一个完整输入（可跨行、可含多个形式），逐个求值打印。终端不支持所需的控制时由调用方退回 Repl。</summary>
    private static int InteractiveRepl(TextWriter output, TextWriter error)
    {
        var eval = Startup.EvalEntry();     // cleat-161：同上（终端 REPL 的提示符不因为编译器而变晚）
        var opts = RT.map(Keyword.intern("read-cond"), Keyword.intern("allow"));
        object eof = new object();
        var quit = Keyword.intern("repl", "quit");
        Var.pushThreadBindings(ReplBindings(out var v1, out var v2, out var v3, out var ve));
        try
        {
            var editor = new LineEditor(new ReplServices(),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cleat_history"));
            output.WriteLine(CoreInfo.Banner);   // clojure.main 的 REPL 先打 "Clojure 1.x"；输入被重定向时（Repl）不打，脚本化的输出不变
            while (true)
            {
                if (StartupTimings.Enabled) StartupTimings.ReplPromptShown();       // cleat-159：提示符即将由行编辑器写出（阻塞读之前）
                string text = editor.Read(((Namespace)RT.CurrentNSVar.deref()).Name + "=> ", "  ");
                if (text == null) { output.WriteLine(); return 0; }
                if (text.Trim().Length == 0) continue;
                var r = new LineNumberingTextReader(new StringReader(text));
                while (true)
                {
                    object form;
                    try { form = LispReader.read(r, false, eof, false, opts); }
                    catch (Exception e) { Report(e, error); ve.set(e); break; }
                    if (form == eof) break;
                    if (quit.Equals(form)) return 0;
                    EvalPrint(eval, output, error, form, v1, v2, v3, ve);
                }
            }
        }
        finally { Var.popThreadBindings(); }
    }

    /// <summary>一个形式的求值—打印—*1 *2 *3 轮转；异常交给 Report 并记入 *e。</summary>
    private static void EvalPrint(Func<object, object> eval, TextWriter output, TextWriter error,
        object form, Var v1, Var v2, Var v3, Var ve)
    {
        try
        {
            object val = eval(form);
            output.WriteLine(RT.printString(val));
            output.Flush();
            v3.set(v2.deref()); v2.set(v1.deref()); v1.set(val);
        }
        catch (Exception e)
        {
            Report(e, error);
            ve.set(e);
        }
    }

    /// <summary>REPL 的 Tab 补全候选（诊断与测试用：<c>(Cleat.Host.Program/ReplCompletions "cstr/jo")</c>），与行编辑器用的同一实现。</summary>
    public static string[] ReplCompletions(string prefix) => new ReplServices().Complete(prefix).ToArray();

    /// <summary>REPL 的参数提示（同上，诊断与测试用）。</summary>
    public static string ReplHint(string name) => new ReplServices().Hint(name);

    /// <summary>REPL 行编辑器的补全与提示（cleat-034）：Var（当前命名空间的映射与 ns/name）、命名空间名、关键字；提示取 Var 的 :arglists。</summary>
    private sealed class ReplServices : IReplServices
    {
        public IReadOnlyList<string> Complete(string prefix)
        {
            var ns = (Namespace)RT.CurrentNSVar.deref();
            if (prefix.StartsWith(":", StringComparison.Ordinal))
            {
                // 关键字：进程内存活的关键字；无限定的词只补无限定的关键字
                string p = prefix.Substring(1), tail = prefix.Contains('/') ? p : null;
                return Keyword.All()
                    .Select(k => k.Symbol)
                    .Where(s => tail != null ? s.ToString().StartsWith(tail, StringComparison.Ordinal)
                                             : s.Name.StartsWith(p, StringComparison.Ordinal) && s.Namespace == null)
                    .Select(s => ":" + s.ToString())
                    .Distinct().OrderBy(x => x, StringComparer.Ordinal).Take(500).ToList();
            }
            int slash = prefix.IndexOf('/');
            var result = new List<string>();
            if (slash > 0)
            {
                // ns/name 与 alias/name：限定部分先按当前命名空间的别名决议（(require '[clojure.string :as cstr]) 之后的 cstr/），
                // 再按命名空间全名；候选保留用户写的限定部分。只列该命名空间自己定义的公有 Var（不含 refer 进来的映射与私有 Var）
                string qualifier = prefix.Substring(0, slash), name = prefix.Substring(slash + 1);
                var qsym = Symbol.intern(qualifier);
                var target = ns.LookupAlias(qsym) ?? Namespace.find(qsym);
                if (target != null)
                    for (ISeq s = RT.seq(target.getMappings()); s != null; s = s.next())
                        if (s.first() is IMapEntry e && e.key() is Symbol sym && e.val() is Var v && v.ns == target && v.isPublic
                            && sym.Name.StartsWith(name, StringComparison.Ordinal))
                            result.Add(qualifier + "/" + sym.Name);
            }
            else
            {
                for (ISeq s = RT.seq(ns.getMappings()); s != null; s = s.next())
                    if (((IMapEntry)s.first()).key() is Symbol sym && sym.Name.StartsWith(prefix, StringComparison.Ordinal))
                        result.Add(sym.Name);
                // 当前命名空间的别名（补成 alias/，接着按 Tab 补成员）与命名空间名（require / alias 用）：读到的映射之后
                for (ISeq s = RT.seq(ns.getAliases()); s != null; s = s.next())
                    if (((IMapEntry)s.first()).key() is Symbol a && a.Name.StartsWith(prefix, StringComparison.Ordinal))
                        result.Add(a.Name + "/");
                for (ISeq s = RT.seq(Namespace.all); s != null; s = s.next())
                    if (s.first() is Namespace m && m.Name.Name.StartsWith(prefix, StringComparison.Ordinal))
                        result.Add(m.Name.Name);
            }
            return result.Distinct().OrderBy(x => x, StringComparer.Ordinal).Take(500).ToList();
        }

        public string Hint(string name)
        {
            var ns = (Namespace)RT.CurrentNSVar.deref();
            var sym = Symbol.intern(name);
            if (sym.Namespace != null)
            {
                var q = Symbol.intern(sym.Namespace);
                ns = ns.LookupAlias(q) ?? Namespace.find(q);      // 别名（cstr/join）与全名；都找不到时没有提示
                if (ns == null) return null;
            }
            if (ns?.GetMapping(Symbol.intern(sym.Name)) is not Var v) return null;
            // defn 写的 :arglists 是列表 '([x] [x y])，手写的也可能是向量：按任何集合读
            var arglists = v.meta()?.valAt(Keyword.intern("arglists")) as IPersistentCollection;
            if (arglists == null || arglists.count() == 0) return null;
            var parts = new List<string>();
            for (ISeq s = RT.seq(arglists); s != null; s = s.next())
            {
                string text = RT.printString(s.first());       // "(inc2 x)"（名字是定义时的）
                if (text != null) parts.Add(text);
            }
            return parts.Count > 0 ? string.Join(" ", parts) : null;
        }
    }

    /// <summary>--report stdio：错误报告写到 *out* 而不是 *err*（clojure.main 的 --report 语义，cleat-034）。</summary>
    public static bool ReportToStdOut;

    private static void Report(Exception e, TextWriter error = null)
    {
        if (RuntimeHooks.ReportError != null) { RuntimeHooks.ReportError(e); return; }
        error ??= ReportToStdOut ? Console.Out : Console.Error;
        for (var x = e; x != null; x = x.InnerException)
            error.WriteLine((x == e ? "" : "  caused by: ") + x.GetType().Name + ": " + x.Message);
        if (Environment.GetEnvironmentVariable("CLEAT_TRACE_ERRORS") != null) error.WriteLine(e);
        error.Flush();
    }
}
