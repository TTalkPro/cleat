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
///   cleat [--scheme-boot dll] [--boot dll] [-I dir]… [-e expr]… [-i file]… [-m ns | script args… | -r | -]
///
/// 启动顺序（两个镜像）：
///   1. 登记原语（Scheme 元层的与 cleat.kernel 的）→ RT.Init；
///   2. 加载 Scheme 镜像 cleat.scheme.boot.dll（--scheme-boot，或 CLEAT_SCHEME_BOOT，或自动找 src/boot/）；
///   3. 装入 Clojure 驱动的 Scheme 库（镜像旁 cleat.scheme.clojure/ 的预编译 fasl，没有则从 src/scheme 源码）并运行宿主程序
///      clojure/host.sps：由 Scheme 登记运行时钩子 eval / load-source / compile / macroexpand-1 / report-error（cleat-052）；
///   4. 加载 Clojure 镜像 cleat.boot.dll（--boot，或 CLEAT_BOOT，或自动找；cleat-053 之前没有）并设置 BootInterface.CurrentImageId；
///   5. 按 clojure.main 的语义执行 -e / -i / -m / 脚本，否则 REPL。
/// 主体在 512 MB 栈的线程上运行（展开器与编译器递归深，与 stage0 相同）。
/// </summary>
public static class Program
{
    public sealed class Options
    {
        public string Boot;
        public string SchemeBoot;
        public readonly List<string> IncludeDirs = new List<string>();
        public readonly List<(string Kind, string Value)> Inits = new List<(string, string)>();   // -e / -i
        public string MainNs;          // -m
        public string Script;          // 脚本路径
        public bool Repl;              // -r 或无其他动作
        public string[] ScriptArgs = Array.Empty<string>();
        public bool Help, Version;
        public bool NoBoot;            // --no-boot：不加载 Clojure 镜像（从源码构建镜像时）
        public string CompileBoot;     // --compile-boot out.dll：编译 Clojure 镜像（cleat-053）
        public string BootOrder;       // --boot-order edn（缺省 out.dll 旁的 boot-order.edn）
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
                case "--scheme-boot" when i + 1 < args.Length: o.SchemeBoot = args[++i]; break;
                case "-I" when i + 1 < args.Length: o.IncludeDirs.Add(args[++i]); break;
                case "--no-boot": o.NoBoot = true; break;
                case "--compile-boot" when i + 1 < args.Length: o.CompileBoot = args[++i]; break;
                case "--boot-order" when i + 1 < args.Length: o.BootOrder = args[++i]; break;
                case "-e": case "--eval": if (i + 1 >= args.Length) throw new ArgumentException("-e needs an expression"); o.Inits.Add(("eval", args[++i])); break;
                case "-i": case "--init": if (i + 1 >= args.Length) throw new ArgumentException("-i needs a file"); o.Inits.Add(("init", args[++i])); break;
                case "-m": case "--main": if (i + 1 >= args.Length) throw new ArgumentException("-m needs a namespace"); o.MainNs = args[++i]; i++; goto done;
                case "-r": case "--repl": o.Repl = true; i++; goto done;
                case "-h": case "--help": o.Help = true; return o;
                case "--version": o.Version = true; return o;
                case "-": o.Script = "-"; i++; goto done;
                default:
                    if (a.StartsWith("-", StringComparison.Ordinal) && a.Length > 1) throw new ArgumentException("unknown option: " + a);
                    o.Script = a; i++; goto done;
            }
        }
    done:
        o.ScriptArgs = args.Skip(i).ToArray();
        if (o.Script == null && o.MainNs == null && !o.Repl && o.Inits.Count == 0 && o.CompileBoot == null) o.Repl = true;
        return o;
    }

    public static int Main(string[] args)
    {
        int exitCode = 0;
        var thread = new System.Threading.Thread(() => exitCode = MainBody(args), 512 * 1024 * 1024);
        thread.Start();
        thread.Join();
        return exitCode;
    }

    private static int MainBody(string[] args)
    {
        Options o;
        try { o = Parse(args); }
        catch (ArgumentException e)
        {
            Console.Error.WriteLine("cleat: " + e.Message);
            return Usage();
        }
        if (o.Help) return Usage();
        if (o.Version) { Console.WriteLine("cleat " + CoreInfo.Version); return 0; }
        try
        {
            Start(o);
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
        Console.Error.WriteLine("usage: cleat [--scheme-boot dll] [--boot dll | --no-boot] [-I dir]... [-e expr]... [-i file]... [-m ns args... | script args... | -r | -]");
        Console.Error.WriteLine("       cleat [--boot dll | --no-boot] -I src/clojure --compile-boot out/cleat.boot.dll [--boot-order boot-order.edn]");
        return 2;
    }

    /// <summary>启动耗时（毫秒）：Scheme 镜像、驱动库（含登记钩子）、Clojure 镜像；CLEAT_TIME 设置时写到标准错误。</summary>
    public static long SchemeImageMs, DriverMs, ClojureImageMs;

    /// <summary>初始化运行时、加载两个镜像并登记钩子；返回 Clojure 镜像路径（没有时 null）。</summary>
    public static string Start(Options o)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string schemeBoot = o.SchemeBoot ?? Environment.GetEnvironmentVariable("CLEAT_SCHEME_BOOT") ?? FindBoot("cleat.scheme.boot.dll")
            ?? throw new FileNotFoundException("Scheme boot image cleat.scheme.boot.dll not found (use --scheme-boot or CLEAT_SCHEME_BOOT; it is built by dotnet build into src/boot/)");
        schemeBoot = Path.GetFullPath(schemeBoot);
        Cleat.Scheme.Builtins.CommandLineArgs = new[] { "cleat" };
        Cleat.Scheme.Clojure.ClojureHost.AddSourcePaths(SchemeSourceDir(schemeBoot));
        Cleat.Scheme.PrimitiveRegistry.RegisterAll();
        PrimitiveRegistry.RegisterAll();
        RT.Init();
        // *out* / *err* 的根是不自动刷新的 StreamWriter：换成控制台的（自动刷新，与 REPL 的提示与结果按顺序交错）
        RT.OutVar.bindRoot(Console.Out);
        RT.ErrVar.bindRoot(Console.Error);
        foreach (var d in o.IncludeDirs) Loader.AddPath(Path.GetFullPath(d));
        Cleat.Scheme.ModuleLoader.LoadModule(schemeBoot);
        SchemeImageMs = sw.ElapsedMilliseconds;
        Cleat.Scheme.Clojure.ClojureHost.Start(Cleat.Scheme.Clojure.ClojureHost.LibraryDirFor(schemeBoot));
        DriverMs = sw.ElapsedMilliseconds - SchemeImageMs;
        string boot = o.NoBoot ? null : o.Boot ?? Environment.GetEnvironmentVariable("CLEAT_BOOT") ?? FindBoot("cleat.boot.dll");
        if (boot != null)
        {
            boot = Path.GetFullPath(boot);
            Loader.LoadBootImage(boot);
            // 开发布局（镜像在 src/boot/）：src/clojure 的源码也在加载路径上（JVM 的 clojure.jar 带着源码；按需加载的
            // clojure.core.reducers 等由此可 require）
            string src = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(boot) ?? ".", "..", "clojure"));
            if (File.Exists(Path.Combine(src, "clojure", "core.clj"))) Loader.AddPath(src);
        }
        ClojureImageMs = sw.ElapsedMilliseconds - SchemeImageMs - DriverMs;
        if (Environment.GetEnvironmentVariable("CLEAT_TIME") != null)
            Console.Error.WriteLine($"cleat: Scheme image {SchemeImageMs} ms, driver libraries ({Cleat.Scheme.Clojure.ClojureHost.Mode}) {DriverMs} ms, Clojure image {ClojureImageMs} ms");
        return boot;
    }

    /// <summary>
    /// 自动找镜像：宿主目录，以及当前目录与宿主目录各自的上级目录里的 src/boot/名字（开发布局：宿主在 src/Cleat.Host/bin/…）。
    /// </summary>
    private static string FindBoot(string name)
    {
        var candidates = new List<string> { Path.Combine(AppContext.BaseDirectory, name) };
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var d = new DirectoryInfo(start); d != null; d = d.Parent)
                candidates.Add(Path.Combine(d.FullName, "src", "boot", name));
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Scheme 源码目录（预编译库不可用时的后备）：CLEAT_SCHEME_SOURCE，或镜像目录旁的 ../scheme（开发布局 src/boot 与 src/scheme）。</summary>
    private static string SchemeSourceDir(string schemeBoot)
    {
        string env = Environment.GetEnvironmentVariable("CLEAT_SCHEME_SOURCE");
        if (!string.IsNullOrEmpty(env)) return env;
        string dir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(schemeBoot) ?? ".", "..", "scheme"));
        return File.Exists(Path.Combine(dir, "clojure", "driver.sls")) ? dir : null;
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

    private static int RunActions(Options o)
    {
        var ns = Namespace.findOrCreate(Symbol.intern("user"));
        ReferClojureCore(ns);
        Var.pushThreadBindings(RT.map(RT.CurrentNSVar, ns, RT.CmdLineArgsVar, RT.seq(o.ScriptArgs)));
        try
        {
            if (o.CompileBoot != null) return CompileBootImage(o);
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
                return Repl(Console.In, Console.Out, Console.Error);
            }
            return 0;
        }
        finally { Var.popThreadBindings(); }
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
        // 已运行着 Clojure 镜像时（不动点的第二段）：*loaded-libs* 绑到空集，require 与从源码构建时一样真正加载每个库
        var core = Namespace.find(Symbol.intern("clojure.core"));
        var loadedLibs = core?.FindInternedVar(Symbol.intern("*loaded-libs*"));
        if (loadedLibs != null) Var.pushThreadBindings(RT.map(loadedLibs, new Ref(PersistentTreeSet.EMPTY)));
        try
        {
            Cleat.Scheme.Clojure.Driver.CompileBoot(forms, dll);
        }
        finally
        {
            if (loadedLibs != null) Var.popThreadBindings();
        }
        Console.Error.WriteLine($"cleat: compiled the Clojure boot image ({forms.Count} entries) in {sw.ElapsedMilliseconds} ms -> {dll}");
        return 0;
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

    private static void Print(object x)
    {
        Console.Out.WriteLine(RT.printString(x));
        Console.Out.Flush();
    }

    /// <summary>最简 REPL：逐个形式读—求值—打印，*1 *2 *3 *e，:repl/quit 退出；行编辑留给 cleat-034。</summary>
    public static int Repl(TextReader input, TextWriter output, TextWriter error)
    {
        var eval = RuntimeHooks.Require(RuntimeHooks.Eval, "eval");
        var core = Namespace.findOrCreate(Symbol.intern("clojure.core"));
        Var v1 = Var.intern(core, Symbol.intern("*1"), null).setDynamic(), v2 = Var.intern(core, Symbol.intern("*2"), null).setDynamic(),
            v3 = Var.intern(core, Symbol.intern("*3"), null).setDynamic(), ve = Var.intern(core, Symbol.intern("*e"), null).setDynamic();
        ReferClojureCore((Namespace)RT.CurrentNSVar.deref());
        Var.pushThreadBindings(RT.map(v1, null, v2, null, v3, null, ve, null));
        var r = new LineNumberingTextReader(input);
        var opts = RT.map(Keyword.intern("read-cond"), Keyword.intern("allow"));
        object eof = new object();
        var quit = Keyword.intern("repl", "quit");
        try
        {
            while (true)
            {
                output.Write(((Namespace)RT.CurrentNSVar.deref()).Name + "=> ");
                output.Flush();
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
        }
        finally { Var.popThreadBindings(); }
    }

    private static void Report(Exception e, TextWriter error = null)
    {
        error ??= Console.Error;
        if (RuntimeHooks.ReportError != null) { RuntimeHooks.ReportError(e); return; }
        for (var x = e; x != null; x = x.InnerException)
            error.WriteLine((x == e ? "" : "  caused by: ") + x.GetType().Name + ": " + x.Message);
        if (Environment.GetEnvironmentVariable("CLEAT_TRACE_ERRORS") != null) error.WriteLine(e);
        error.Flush();
    }
}
