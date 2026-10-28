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

    private static readonly ConcurrentDictionary<string, Assembly> Loaded = new(StringComparer.OrdinalIgnoreCase);

    [ThreadStatic]
    private static bool resolving;

    static Loader()
    {
        LoadPath = InitialLoadPath();
        AssemblyLoadContext.Default.Resolving += OnResolving;
        AssemblyLoadContext.Default.ResolvingUnmanagedDll += OnResolvingUnmanagedDll;
        PackageIndex.EnsureInitialized();
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

        PackageIndex.RegisterDirectory(full);   // 目录里的包程序集（cleat-067）
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

    /// <summary>
    /// deftype* / reify* 定义的类型（cleat-051）：按全名登记，后登记的取代先登记的（同名类型重定义时 classForName 取最新的，
    /// 与 ClojureCLR 的 DynamicClassLoader 相同）。由生成的代码在 deftype* 求值时调用。
    /// </summary>
    private static readonly ConcurrentDictionary<string, Type> DefinedTypes = new(StringComparer.Ordinal);

    public static Type RegisterType(Type t)
    {
        ArgumentNullException.ThrowIfNull(t);
        DefinedTypes[t.FullName] = t;
        TypeResolver.Forget(t.FullName);
        return t;
    }

    /// <summary>只在 deftype* / gen-class 等登记的类型里按全名找（cleat-043：assembly-load 的类名桥接）。</summary>
    public static Type FindDefinedType(string fullName) => DefinedTypes.TryGetValue(fullName, out var t) ? t : null;

    /// <summary>在已登记的类型与模块程序集里按全名找类型（RT.classForName 的第一站；原来是 Compiler.FindDuplicateType）。</summary>
    public static Type FindType(string fullName)
    {
        if (DefinedTypes.TryGetValue(fullName, out var defined))
        {
            return defined;
        }

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

    /// <summary>
    /// AOT 程序集常量里的类型（cleat-063）：deftype 定义在别的包程序集里、还没载入时，经包索引按类型名的前缀找到并载入
    /// （不跑入口；<see cref="PackageIndex.FindType"/>）。
    /// </summary>
    public static Type LoadAssemblyDefining(string typeName) =>
        PackageIndex.FindType(typeName);

    // ---- 加载路径上的普通程序集（cleat-066）----

    private static readonly ConcurrentDictionary<string, (DateTime Stamp, Dictionary<string, string> Types)> TypeIndexes = new(StringComparer.Ordinal);

    /// <summary>
    /// 按全名在加载路径目录里的 <c>*.dll</c>（不递归）中找公有类型并载入其程序集（cleat-066）：<c>dotnet build</c> 的产物、NuGet 包里的
    /// C# 库放进 <c>-I</c> 目录后 Clojure 代码直接用类型名，与 JVM 上类路径里的类按名加载相同。每个目录的类型表按元数据建一次
    /// （目录有变化时重建），不载入程序集。找不到返回 null。
    /// </summary>
    public static Type FindTypeOnLoadPath(string fullName)
    {
        if (string.IsNullOrEmpty(fullName) || RuntimeBootstrapFlag.DisableFileLoad) return null;
        foreach (string dir in LoadPath.ToArray())
        {
            var types = TypeIndex(dir);
            if (types == null || !types.TryGetValue(fullName, out string file)) continue;
            try
            {
                Assembly asm = Loaded.Values.FirstOrDefault(a => string.Equals(a.Location, file, StringComparison.Ordinal))
                    ?? AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => !a.IsDynamic && string.Equals(a.Location, file, StringComparison.Ordinal))
                    ?? AssemblyLoadContext.Default.LoadFromAssemblyPath(file);
                Register(asm);
                Type t = asm.GetType(fullName, false);
                if (t != null) return t;
            }
            catch (Exception)
            {
                // 载入不了（版本冲突、同名程序集已载入）：继续找
            }
        }
        return null;
    }

    private static Dictionary<string, string> TypeIndex(string dir)
    {
        DateTime stamp;
        try
        {
            if (!Directory.Exists(dir)) return null;
            stamp = Directory.GetLastWriteTimeUtc(dir);
        }
        catch (Exception)
        {
            return null;
        }
        if (TypeIndexes.TryGetValue(dir, out var cached) && cached.Stamp == stamp) return cached.Types;
        var types = new Dictionary<string, string>(StringComparer.Ordinal);
        string[] files;
        try { files = Directory.GetFiles(dir, "*.dll"); }
        catch (Exception) { files = Array.Empty<string>(); }
        foreach (string file in files.OrderBy(f => f, StringComparer.Ordinal))
        {
            try
            {
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var pe = new System.Reflection.PortableExecutable.PEReader(fs);
                if (!pe.HasMetadata) continue;
                var md = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
                if (!md.IsAssembly) continue;
                foreach (var h in md.TypeDefinitions)
                {
                    string name = PublicTypeName(md, md.GetTypeDefinition(h));
                    if (name != null) types.TryAdd(name, Path.GetFullPath(file));
                }
            }
            catch (Exception)
            {
                // 不是托管程序集（本机库等）
            }
        }
        TypeIndexes[dir] = (stamp, types);
        return types;
    }

    /// <summary>公有类型的全名（嵌套类型 Outer+Inner）；不是公有的返回 null。</summary>
    private static string PublicTypeName(System.Reflection.Metadata.MetadataReader md, System.Reflection.Metadata.TypeDefinition td)
    {
        var vis = td.Attributes & TypeAttributes.VisibilityMask;
        string name = md.GetString(td.Name);
        if (vis == TypeAttributes.Public)
        {
            string ns = md.GetString(td.Namespace);
            return ns.Length == 0 ? name : ns + "." + name;
        }
        if (vis == TypeAttributes.NestedPublic)
        {
            string outer = PublicTypeName(md, md.GetTypeDefinition(td.GetDeclaringType()));
            return outer == null ? null : outer + "+" + name;
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
            // 已经载入过（如 LoadAssemblyDefining 为常量里的类型先载入了它）就用同一个程序集
            asm = Loaded.Values.FirstOrDefault(a => string.Equals(a.Location, full, StringComparison.Ordinal))
                ?? AssemblyLoadContext.Default.LoadFromAssemblyPath(full);
        }

        // cleat-159：镜像的分段（程序集加载 / 登记 / 旁边的 .ifc / 模块入口）。只在初始化 Clojure 镜像这一段采样，
        // 别的模块加载（eval、包）不进这几格。
        // 未启用（没设 CLEAT_TIME）时走下面这条与 159 之前逐字相同的路径：不开 try/finally、不进 StartupTimings 的任何方法
        // —— 111 的复核表明插桩方法的 JIT 会挪动热循环 tier1 的安装时机，短基准因此在两档之间跳（notes/startup-profile.md §6）
        if (!(StartupTimings.Enabled && BootImageInitializing))
        {
            Register(asm);
            InterfaceFile.TryRegisterBeside(full, asm);   // cleat-022：旁边的 .ifc（MVID 相符才登记）
            return WithLoadBindings(() => Run(asm));
        }
        StartupTimings.Mark(StartupTimings.Segment.ImageAssembly);
        Register(asm);
        StartupTimings.Mark(StartupTimings.Segment.ImageRegister);
        InterfaceFile.TryRegisterBeside(full, asm);
        StartupTimings.Mark(StartupTimings.Segment.ImageIfc);
        StartupTimings.InBootEntry = true;
        try
        {
            return WithLoadBindings(() => Run(asm));
        }
        finally
        {
            StartupTimings.EndBootEntry();
        }
    }

    /// <summary>
    /// Clojure 镜像（cleat.boot.dll，cleat-053）正在初始化：镜像里已经含有 boot 命名空间全部文件的形式（被加载的文件排在
    /// (load …) / (require …) 之前），这期间 RT.load 是空操作；require 照常把库记入 *loaded-libs*。
    /// </summary>
    public static bool BootImageInitializing { get; private set; }

    /// <summary>
    /// 镜像里的文件：初始化期间 RT.load 经过的相对路径（core.clj 的 (load "core_print")、各库的 require）。之后再 RT.load 它们是空操作
    /// ——JVM 里 AOT 库的 __init 类已初始化、RT.load 不再做事，(use 'clojure.core) 因此不会从源码重载整个 core（cleat-099）。
    /// </summary>
    private static readonly HashSet<string> s_imageFiles = new();

    /// <summary>构建下一级镜像时（Program.CompileBootImage）要从源码真正加载镜像里的文件：置真。</summary>
    public static bool ReloadImageFiles { get; set; }

    /// <summary>rel 是否镜像里的文件（见 <see cref="s_imageFiles"/>）。</summary>
    public static bool IsImageFile(string rel) { lock (s_imageFiles) return s_imageFiles.Contains(rel); }

    /// <summary>加载 Clojure 镜像并设置 <see cref="BootInterface.CurrentImageId"/>；返回程序集。</summary>
    public static Assembly LoadBootImage(string path)
    {
        BootImageInitializing = true;
        try
        {
            LoadModule(path, false);
        }
        finally
        {
            BootImageInitializing = false;
        }
        // 镜像里的库：初始化结束时 *loaded-libs* 里的（各文件的 ns 形式登记的，require 因此没经过 RT.load），加上初始化期间 RT.load 经过的
        // （core.clj 里 (load "core_print") 一类与 (RT/load "clojure/core") 本身）
        if (Namespace.find(Symbol.intern("clojure.core"))?.FindInternedVar(Symbol.intern("*loaded-libs*"))?.deref() is IDeref libsRef
            && libsRef.deref() is IPersistentSet libs)
        {
            lock (s_imageFiles)
                for (ISeq s = RT.seq(libs); s != null; s = s.next())
                    if (s.first() is Symbol lib) s_imageFiles.Add(lib.Name.Replace('.', '/').Replace('-', '_'));
        }
        var asm = Loaded.TryGetValue(Path.GetFileNameWithoutExtension(path), out var a) ? a : null;
        if (asm != null) BootInterface.CurrentImageId = asm.ManifestModule.ModuleVersionId;
        return asm;
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

    /// <summary>RT.load：包程序集的索引，再按加载路径找 <c>rel.cljr/.cljc/.clj</c>，找不到抛 FileNotFoundException。</summary>
    public static void Load(string relativePath) => TryLoad(relativePath, true);

    /// <summary>
    /// RT.load 的搜索（designs/17 §2.3）：已登记的包程序集里有 <c>rel</c> 时跑它的入口（<see cref="PackageIndex"/>）；否则加载路径上
    /// 第一个有源码 <c>rel + {.cljr,.cljc,.clj}</c> 的目录（<see cref="RuntimeHooks.LoadSource"/>，<c>*compile-files*</c> 为真时
    /// <see cref="RuntimeHooks.Compile"/>）；然后 <c>*ns-load-mappings*</c> 与嵌入资源。<c>*compile-files*</c> 为真时不用包程序集
    /// （编译要看到源码）。<c>*loaded-libs*</c> 的登记留在 core.clj 的 load-lib 里，这里不碰。
    /// </summary>
    public static bool TryLoad(string relativePath, bool failIfNotFound)
    {
        if (relativePath is null)
        {
            throw new ArgumentNullException(nameof(relativePath));
        }

        string rel = relativePath.TrimStart('/');
        if (BootImageInitializing)
        {
            lock (s_imageFiles) s_imageFiles.Add(rel);
            return true;
        }
        if (!ReloadImageFiles && IsImageFile(rel)) return true;
        if (!RT.booleanCast(RT.CompileFilesVar.deref()) && PackageIndex.Contains(rel))
        {
            WithLoadBindings(() => PackageIndex.TryRun(rel, out object r) ? r : null);
            return true;
        }

        if (!RuntimeBootstrapFlag.DisableFileLoad)
        {
            FileInfo source = null;
            foreach (string dir in LoadPath.ToArray())
            {
                source = FirstExisting(dir, rel, SourceExtensions);
                if (source is not null)
                {
                    break;
                }
            }

            source ??= SourceExtensions.Select(ext => RT.FindRemappedFile(rel + ext)).FirstOrDefault(fi => fi is not null);
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
                $"Could not locate {rel} in a package assembly or with extensions .cljr, .cljc, .clj on load path [{string.Join(Path.PathSeparator.ToString(), LoadPath)}]."
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

        Func<object> load = () => WithLoadBindings(() =>
        {
            using var rdr = new StreamReader(full);
            return LoadSource(rdr, full, sourceName ?? Path.GetFileName(full));
        });
        return RuntimeHooks.OnLoaderThread is { } onLoader ? onLoader(load) : load();
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

    /// <summary>
    /// 原生库（NuGet 包 runtimes/&lt;rid&gt;/native 下的，如 SQLite 的 e_sqlite3，cleat-073）：.NET 按常规找不到时，在加载路径的目录里
    /// 按平台的命名规则找（libX.so / X.so、X.dll、libX.dylib）。tools.build 的 basis 把包的原生资产目录放在加载路径上。
    /// </summary>
    private static IntPtr OnResolvingUnmanagedDll(Assembly requester, string name)
    {
        if (string.IsNullOrEmpty(name)) return IntPtr.Zero;
        string file = Path.GetFileName(name);
        string[] candidates = OperatingSystem.IsWindows()
            ? [file, file + ".dll"]
            : OperatingSystem.IsMacOS()
                ? [file, "lib" + file + ".dylib", file + ".dylib"]
                : [file, "lib" + file + ".so", file + ".so"];
        foreach (string dir in LoadPath.ToArray())
            foreach (string c in candidates)
            {
                string probe = Path.Combine(dir, c);
                if (File.Exists(probe) && System.Runtime.InteropServices.NativeLibrary.TryLoad(probe, out IntPtr handle)) return handle;
            }
        return IntPtr.Zero;
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
