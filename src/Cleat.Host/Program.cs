using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cleat.Core.Runtime;
using clojure.lang;

namespace Cleat.Host;

/// <summary>
/// 命令行宿主（designs/02-runtime.md §9、cleat-030）。
///
///   cleat [--boot dll] [-I dir]… [-e expr]… [-i file]… [-m ns | script args… | -r | -]
///
/// 启动顺序：登记原语 → RT.Init → 加载 boot 镜像（--boot，或 CLEAT_BOOT，或自动找 src/boot/cleat.boot.dll）
/// → 镜像登记运行时钩子（load-source / eval …）→ 按 clojure.main 的语义执行 -e / -i / -m / 脚本，否则 REPL。
/// 没有镜像时只能跑 stage0（cleat-015）或报错。
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
                case "-I" when i + 1 < args.Length: o.IncludeDirs.Add(args[++i]); break;
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
        if (o.Script == null && o.MainNs == null && !o.Repl && o.Inits.Count == 0) o.Repl = true;
        return o;
    }

    public static int Main(string[] args)
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
    }

    private static int Usage()
    {
        Console.Error.WriteLine("usage: cleat [--boot dll] [-I dir]... [-e expr]... [-i file]... [-m ns args... | script args... | -r | -]");
        return 2;
    }

    /// <summary>初始化运行时并加载 boot 镜像（找不到镜像时不报错，由后续动作在钩子缺失时报错）。</summary>
    public static string Start(Options o)
    {
        PrimitiveRegistry.RegisterAll();
        RT.Init();
        foreach (var d in o.IncludeDirs) Loader.AddPath(Path.GetFullPath(d));
        string boot = o.Boot ?? Environment.GetEnvironmentVariable("CLEAT_BOOT") ?? FindBoot();
        if (boot != null)
        {
            Loader.LoadModule(boot, false);
            if (Loader.LoadedAssemblies.TryGetValue(Path.GetFileNameWithoutExtension(boot), out var asm))
                BootInterface.CurrentImageId = asm.ManifestModule.ModuleVersionId;
        }
        return boot;
    }

    /// <summary>自动找镜像：宿主目录、当前目录及其上级的 src/boot/cleat.boot.dll。</summary>
    private static string FindBoot()
    {
        var candidates = new List<string> { Path.Combine(AppContext.BaseDirectory, "cleat.boot.dll") };
        for (var d = new DirectoryInfo(Directory.GetCurrentDirectory()); d != null; d = d.Parent)
            candidates.Add(Path.Combine(d.FullName, "src", "boot", "cleat.boot.dll"));
        return candidates.FirstOrDefault(File.Exists);
    }

    private static int RunActions(Options o)
    {
        var ns = Namespace.findOrCreate(Symbol.intern("user"));
        Var.pushThreadBindings(RT.map(RT.CurrentNSVar, ns, RT.CmdLineArgsVar, RT.seq(o.ScriptArgs)));
        try
        {
            foreach (var (kind, value) in o.Inits)
            {
                if (kind == "eval") Print(EvalString(value, "<eval>"));
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
            if (o.Repl) return Repl(Console.In, Console.Out, Console.Error);
            return 0;
        }
        finally { Var.popThreadBindings(); }
    }

    /// <summary>读一段文本的全部形式并逐个经 eval 钩子求值，返回最后一个值。</summary>
    public static object EvalString(string text, string sourceName) => EvalReader(new StringReader(text), sourceName);

    public static object EvalReader(TextReader reader, string sourceName)
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
