using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using clojure.lang;

namespace Cleat.Core.Runtime;

/// <summary>
/// 加载器（designs/02 §9、05 §6）：RT.load 的"找文件 / 程序集、决定源码还是 dll、调入口"一半。展开 + 编译的另一半经
/// <see cref="RuntimeHooks"/> 回调，Cleat.Core 本身不引用任何编译器类型。
///
/// 启动顺序：
///   1. 登记原语（cleat-011）；
///   2. <c>RT.Init()</c>（建 clojure.core / user，*ns* 指向 user，调 RuntimeHooks.OnInit）；
///   3. 有 boot 镜像：<c>Loader.LoadModule("~/cleat.boot.dll", false)</c> —— 镜像入口按序初始化全部 core 命名空间并登记钩子
///      （load-source / compile / eval / print-method …）；
///      无镜像：stage0 自己登记钩子（解释器），再从源码 <c>Loader.Load("clojure/core")</c>；
///   4. REPL / 宿主（cleat-030）。
///
/// 加载路径：<c>CLEAT_LOAD_PATH</c>（按 Path.PathSeparator 切分）、入口程序集所在目录、AppContext.BaseDirectory、当前目录；
/// 宿主的 <c>-I</c> 经 <see cref="AddPath"/> 追加。<c>~/</c> 前缀表示运行时基目录（<see cref="BaseDirectory"/>）。
/// </summary>
public static class Loader
{
    private static readonly string[] SourceExtensions = [".cljr", ".cljc", ".clj"];
    private static readonly string[] AssemblyExtensions = [".cljr.dll", ".cljc.dll", ".clj.dll"];

    private static readonly ConcurrentDictionary<string, Assembly> Loaded = new(StringComparer.OrdinalIgnoreCase);

    [ThreadStatic]
    private static bool resolving;

    static Loader()
    {
        LoadPath = InitialLoadPath();
        AssemblyLoadContext.Default.Resolving += OnResolving;
    }

    /// <summary>运行时基目录：AppContext.BaseDirectory（为空时退到 Cleat.Core.dll 所在目录）。</summary>
    public static string BaseDirectory
    {
        get
        {
            string dir = AppContext.BaseDirectory;
            if (string.IsNullOrEmpty(dir))
            {
                dir = Path.GetDirectoryName(typeof(Loader).Assembly.Location);
            }

            return dir ?? Directory.GetCurrentDirectory();
        }
    }

    /// <summary>加载路径（可变；按顺序搜索，先命中者赢）。</summary>
    public static IList<string> LoadPath { get; }

    /// <summary>已经由加载器载入的模块程序集（简单名 → Assembly）：Type.GetType("T, cleat.boot") 与发射器据此找到它们。</summary>
    public static IReadOnlyDictionary<string, Assembly> LoadedAssemblies => Loaded;

    /// <summary>确保静态初始化（挂上 AssemblyLoadContext.Resolving）已经发生。</summary>
    public static void EnsureInitialized()
    {
    }

    public static void AddPath(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir))
        {
            return;
        }

        string full = ResolvePath(dir);
        if (!LoadPath.Contains(full, StringComparer.Ordinal))
        {
            LoadPath.Add(full);
        }
    }

    /// <summary>把 <c>~/x</c> 解析到运行时基目录，其它路径取绝对路径。</summary>
    public static string ResolvePath(string path)
    {
        if (path is null)
        {
            throw new ArgumentNullException(nameof(path));
        }

        if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
        {
            path = Path.Combine(BaseDirectory, path.Substring(2));
        }

        return Path.GetFullPath(path.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>把一个程序集登记到 <see cref="LoadedAssemblies"/>。</summary>
    public static void Register(Assembly asm)
    {
        if (asm is null)
        {
            throw new ArgumentNullException(nameof(asm));
        }

        string name = asm.GetName().Name;
        if (!string.IsNullOrEmpty(name))
        {
            Loaded[name] = asm;
        }
    }

    /// <summary>在已登记的模块程序集里按全名找类型（RT.classForName 的第一站；原来是 Compiler.FindDuplicateType）。</summary>
    public static Type FindType(string fullName)
    {
        foreach (var asm in Loaded.Values)
        {
            Type t = asm.GetType(fullName, false);
            if (t is not null)
            {
                return t;
            }
        }

        return null;
    }

    // ---- 模块程序集 ----

    /// <summary>
    /// 载入一个 .dll（<c>~/</c> 前缀 = 运行时基目录），读 <see cref="CleatModuleAttribute"/>，调入口 <c>public static object Entry()</c>，返回其结果。
    /// inMemory 为真时按字节载入（不锁文件；同名程序集不能二次载入是 AssemblyLoadContext 的限制）。
    /// </summary>
    public static object LoadModule(string path, bool inMemory)
    {
        string full = ResolvePath(path);
        if (!File.Exists(full))
        {
            throw new FileNotFoundException($"module not found: {full}", full);
        }

        Assembly asm;
        if (inMemory)
        {
            using var stream = new MemoryStream(File.ReadAllBytes(full));
            asm = AssemblyLoadContext.Default.LoadFromStream(stream);
        }
        else
        {
            asm = AssemblyLoadContext.Default.LoadFromAssemblyPath(full);
        }

        Register(asm);
        InterfaceFile.TryRegisterBeside(full, asm);   // cleat-022：旁边的 .ifc（MVID 相符才登记）
        return WithLoadBindings(() => Run(asm));
    }

    /// <summary>对已建好的程序集（内存 eval 程序集或已载入的模块）调入口方法。</summary>
    public static object Run(Assembly asm)
    {
        if (asm is null)
        {
            throw new ArgumentNullException(nameof(asm));
        }

        var attr = asm.GetCustomAttribute<CleatModuleAttribute>()
            ?? throw new InvalidOperationException($"assembly '{asm.GetName().Name}' has no [Cleat.Core.CleatModule] attribute (not a Cleat module; was it emitted with an (entry …) clause?)");
        Type entryType = asm.GetType(attr.EntryType, false)
            ?? throw new InvalidOperationException($"module '{asm.GetName().Name}': entry type '{attr.EntryType}' not found");
        MethodInfo entry = entryType.GetMethod(attr.EntryMethod, BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null)
            ?? throw new InvalidOperationException($"module '{asm.GetName().Name}': entry method 'public static {attr.EntryType}.{attr.EntryMethod}()' not found");

        try
        {
            return entry.Invoke(null, null);
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }
    }

    // ---- RT.load ----

    /// <summary>RT.load：按加载路径找 <c>rel.cljr/.cljc/.clj</c> 或 <c>rel(/→.).clj.dll</c>，找不到抛 FileNotFoundException。</summary>
    public static void Load(string relativePath) => TryLoad(relativePath, true);

    /// <summary>
    /// RT.load 的搜索：对加载路径上的每个目录，候选为源码 <c>rel + {.cljr,.cljc,.clj}</c> 与 AOT 程序集 <c>rel(/→.) + {.cljr,.cljc,.clj}.dll</c>；
    /// 第一个有候选的目录胜出，其中 dll 不比源码旧时用 dll（<see cref="LoadModule"/>），否则用源码（<see cref="RuntimeHooks.LoadSource"/>，
    /// <c>*compile-files*</c> 为真时 <see cref="RuntimeHooks.Compile"/>）。然后退到 <c>*ns-load-mappings*</c> 与嵌入资源。
    /// <c>*loaded-libs*</c> 的登记留在 core.clj 的 load-lib 里，这里不碰。
    /// </summary>
    public static bool TryLoad(string relativePath, bool failIfNotFound)
    {
        if (relativePath is null)
        {
            throw new ArgumentNullException(nameof(relativePath));
        }

        string rel = relativePath.TrimStart('/');
        if (!RuntimeBootstrapFlag.DisableFileLoad)
        {
            FileInfo source = null, assembly = null;
            string dllRel = rel.Replace('/', '.');
            foreach (string dir in LoadPath.ToArray())
            {
                source = FirstExisting(dir, rel, SourceExtensions);
                assembly = FirstExisting(dir, dllRel, AssemblyExtensions);
                if (source is not null || assembly is not null)
                {
                    break;
                }
            }

            if (source is null && assembly is null)
            {
                source = SourceExtensions.Select(ext => RT.FindRemappedFile(rel + ext)).FirstOrDefault(fi => fi is not null);
            }

            if (assembly is not null && (source is null || assembly.LastWriteTimeUtc >= source.LastWriteTimeUtc))
            {
                try
                {
                    LoadModule(assembly.FullName, false);
                    return true;
                }
                catch (BootInterface.StaleModuleException e) when (source is not null)
                {
                    // cleat-022：对旧 boot 镜像编译的产物 → 回退源码（designs/04 §6）
                    RT.errPrintWriter().WriteLine($"warning: {assembly.Name}: {e.Message}; loading {source.Name} instead");
                }
            }

            if (source is not null)
            {
                LoadFile(source.FullName, rel + source.Extension);
                return true;
            }
        }

        if (TryLoadEmbedded(rel))
        {
            return true;
        }

        if (failIfNotFound)
        {
            throw new FileNotFoundException(
                $"Could not locate {rel} with extensions .cljr, .cljc, .clj, .cljr.dll, .cljc.dll, or .clj.dll on load path [{string.Join(Path.PathSeparator.ToString(), LoadPath)}]."
                + (rel.Contains('_') ? " Please check that namespaces with dashes use underscores in the Clojure file name." : ""));
        }

        return false;
    }

    /// <summary>load-file：打开文件，交给 LoadSource 钩子，返回最后一个形式的值。</summary>
    public static object LoadFile(string path) => LoadFile(path, null);

    public static object LoadFile(string path, string sourceName)
    {
        string full = ResolvePath(path);
        if (!File.Exists(full))
        {
            throw new FileNotFoundException($"source file not found: {full}", full);
        }

        return WithLoadBindings(() =>
        {
            using var rdr = new StreamReader(full);
            return LoadSource(rdr, full, sourceName ?? Path.GetFileName(full));
        });
    }

    /// <summary>把一段源码交给 LoadSource（或 <c>*compile-files*</c> 为真时的 Compile）钩子，绑定 <c>*file*</c> / <c>*source-path*</c>。</summary>
    public static object LoadSource(TextReader rdr, string sourcePath, string sourceName)
    {
        if (rdr is null)
        {
            throw new ArgumentNullException(nameof(rdr));
        }

        bool compile = RT.booleanCast(RT.CompileFilesVar.deref());
        var hook = compile
            ? RuntimeHooks.Require(RuntimeHooks.Compile, "compile")
            : RuntimeHooks.Require(RuntimeHooks.LoadSource, "load-source");

        Var.pushThreadBindings(RT.mapUniqueKeys(
            RT.SourcePathVar, sourcePath ?? "NO_SOURCE_PATH",
            RT.SourceVar, sourceName ?? "NO_SOURCE_FILE"));
        try
        {
            return hook(rdr, sourcePath, sourceName);
        }
        finally
        {
            Var.popThreadBindings();
        }
    }

    // ---- 内部 ----

    private static List<string> InitialLoadPath()
    {
        var paths = new List<string>();
        void Add(string p)
        {
            if (string.IsNullOrWhiteSpace(p))
            {
                return;
            }

            try
            {
                p = Path.GetFullPath(p);
            }
            catch (Exception)
            {
                return;
            }

            if (!paths.Contains(p, StringComparer.Ordinal))
            {
                paths.Add(p);
            }
        }

        string env = Environment.GetEnvironmentVariable("CLEAT_LOAD_PATH");
        if (!string.IsNullOrEmpty(env))
        {
            foreach (string p in env.Split(Path.PathSeparator))
            {
                Add(p);
            }
        }

        Assembly entry = Assembly.GetEntryAssembly();
        if (entry is not null && !string.IsNullOrEmpty(entry.Location))
        {
            Add(Path.GetDirectoryName(entry.Location));
        }

        Add(AppContext.BaseDirectory);
        Add(Directory.GetCurrentDirectory());
        return paths;
    }

    private static FileInfo FirstExisting(string dir, string rel, string[] extensions)
    {
        foreach (string ext in extensions)
        {
            string probe = Path.Combine(dir, (rel + ext).Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(probe))
            {
                return new FileInfo(probe);
            }
        }

        return null;
    }

    private static bool TryLoadEmbedded(string rel)
    {
        string dotted = rel.Replace('/', '.');
        foreach (string ext in AssemblyExtensions)
        {
            var stream = RT.GetEmbeddedResourceStream(dotted + ext, out _);
            if (stream is not null)
            {
                Assembly asm;
                using (stream)
                {
                    asm = AssemblyLoadContext.Default.LoadFromStream(stream);
                }

                Register(asm);
                WithLoadBindings(() => Run(asm));
                return true;
            }
        }

        foreach (string ext in SourceExtensions)
        {
            string name = dotted + ext;
            var stream = RT.GetEmbeddedResourceStream(name, out Assembly containing);
            if (stream is not null)
            {
                WithLoadBindings(() =>
                {
                    using var rdr = new StreamReader(stream);
                    return LoadSource(rdr, containing.GetName().Name + ":" + name, name);
                });
                return true;
            }
        }

        return false;
    }

    /// <summary>RT.load 外层的绑定：*ns*、*warn-on-reflection*、*unchecked-math* 在加载结束后恢复。</summary>
    private static object WithLoadBindings(Func<object> body)
    {
        Var.pushThreadBindings(RT.mapUniqueKeys(
            RT.CurrentNSVar, RT.CurrentNSVar.deref(),
            RT.WarnOnReflectionVar, RT.WarnOnReflectionVar.deref(),
            RT.UncheckedMathVar, RT.UncheckedMathVar.deref()));
        try
        {
            return body();
        }
        finally
        {
            Var.popThreadBindings();
        }
    }

    private static Assembly OnResolving(AssemblyLoadContext ctx, AssemblyName name)
    {
        if (resolving || string.IsNullOrEmpty(name.Name))
        {
            return null;
        }

        if (Loaded.TryGetValue(name.Name, out var known))
        {
            return known;
        }

        resolving = true;
        try
        {
            foreach (string dir in LoadPath.ToArray())
            {
                string probe = Path.Combine(dir, name.Name + ".dll");
                if (File.Exists(probe))
                {
                    try
                    {
                        var asm = ctx.LoadFromAssemblyPath(probe);
                        Register(asm);
                        return asm;
                    }
                    catch (Exception)
                    {
                        // 不可载入的文件（损坏、版本不符）：继续找下一个目录
                    }
                }
            }

            return null;
        }
        finally
        {
            resolving = false;
        }
    }
}
