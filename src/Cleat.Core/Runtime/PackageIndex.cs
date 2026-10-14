using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;

namespace Cleat.Core.Runtime;

/// <summary>
/// 包程序集的索引（cleat-067，designs/17 §2.3）：加载路径（<c>nrepl/server</c>）→ 包程序集里的入口 <c>Load&lt;i&gt;</c>。
/// 程序集级特性 <see cref="CleatNamespaceAttribute"/> 按元数据读（不加载程序集、不加载类型）。登记来源：
///   - 进程里已加载与之后加载的程序集（AssemblyLoad 事件）；
///   - 应用的依赖（TPA，即 deps.json 里的运行时程序集：NuGet 引用的包），第一次查找时扫描一次；
///   - 加载路径目录里的 <c>*.dll</c>（宿主的 <c>-I</c>，<see cref="Loader.AddPath"/>）。
/// 同一个加载路径先登记者赢。入口跑之前先跑一次程序集的 CleatModule 入口 Initialize（镜像核对与预链接）。
/// </summary>
public static class PackageIndex
{
    private sealed class Entry
    {
        public string AssemblyName;
        public string AssemblyPath;     // 加载路径目录里的文件；TPA 与已加载的为 null（按名字载入）
        public string FilePath;         // 程序集文件（诊断与 Locate 用）
        public Assembly Assembly;
        public string EntryType, EntryMethod;
    }

    private const string NamespaceAttribute = "Cleat.Core.CleatNamespaceAttribute";

    private static readonly HashSet<string> RuntimeAssemblies = new(StringComparer.OrdinalIgnoreCase)
    {
        "Cleat.Core.dll", "Cleat.Hosting.dll", "cleat.dll", "cleat-emit.dll", "cleat.boot.dll",
    };

    private static readonly ConcurrentDictionary<string, Entry> s_index = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, bool> s_scannedFiles = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<Assembly, object> s_initialized = new();
    private static readonly ConcurrentDictionary<Assembly, object> s_gates = new();
    private static readonly ConcurrentDictionary<Assembly, bool> s_prepared = new();
    private static int s_tpaScanned;

    static PackageIndex()
    {
        AppDomain.CurrentDomain.AssemblyLoad += (_, e) => RegisterAssembly(e.LoadedAssembly);
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies()) RegisterAssembly(asm);
    }

    /// <summary>确保静态初始化（挂上 AssemblyLoad、登记已加载的程序集）已经发生。</summary>
    public static void EnsureInitialized()
    {
    }

    /// <summary>已加载的程序集：带 CleatNamespace 特性的登记它的条目。</summary>
    public static void RegisterAssembly(Assembly asm)
    {
        if (asm is null || asm.IsDynamic) return;
        IList<CustomAttributeData> attrs;
        try { attrs = asm.GetCustomAttributesData(); }
        catch (Exception) { return; }
        foreach (var a in attrs)
        {
            if (a.AttributeType.FullName != NamespaceAttribute || a.ConstructorArguments.Count != 3) continue;
            Add((string)a.ConstructorArguments[0].Value, new Entry
            {
                AssemblyName = asm.GetName().Name,
                Assembly = asm,
                FilePath = asm.Location,
                EntryType = (string)a.ConstructorArguments[1].Value,
                EntryMethod = (string)a.ConstructorArguments[2].Value,
            });
        }
    }

    /// <summary>一个 dll 文件：按元数据读索引登记（不载入）。返回登记的条目数。</summary>
    public static int RegisterFile(string path) => RegisterFile(path, path);

    private static int RegisterFile(string path, string loadPath)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception) { return 0; }
        if (!s_scannedFiles.TryAdd(full, true)) return 0;
        var found = ReadIndex(full, out string asmName);
        if (found is null) return 0;
        foreach (var (key, type, method) in found)
            Add(key, new Entry { AssemblyName = asmName, AssemblyPath = loadPath is null ? null : full, FilePath = full, EntryType = type, EntryMethod = method });
        return found.Count;
    }

    /// <summary>目录里的 <c>*.dll</c> 都按 <see cref="RegisterFile(string)"/> 登记（不递归）。</summary>
    public static void RegisterDirectory(string dir)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        string[] files;
        try { files = Directory.GetFiles(dir, "*.dll"); }
        catch (Exception) { return; }
        foreach (string f in files) RegisterFile(f);
    }

    /// <summary>加载路径 key（如 <c>nrepl/server</c>）在某个包程序集里。</summary>
    public static bool Contains(string key)
    {
        ScanTrustedAssemblies();
        return s_index.ContainsKey(key);
    }

    /// <summary>含加载路径 key 的包程序集文件；没有时 null（如 neorepl 的 jack-in 把它的目录交给子进程的 -I）。</summary>
    public static string Locate(string key)
    {
        ScanTrustedAssemblies();
        return s_index.TryGetValue(key, out var e) ? (e.Assembly?.Location is { Length: > 0 } l ? l : e.FilePath) : null;
    }

    /// <summary>包程序集里有 key 时跑它的入口（先跑一次程序集的 Initialize），返回 true；没有时 false。</summary>
    public static bool TryRun(string key, out object result)
    {
        result = null;
        ScanTrustedAssemblies();
        if (!s_index.TryGetValue(key, out var e)) return false;
        Assembly asm = EnsureAssembly(e);
        EnsureInitialized(asm);
        Type t = asm.GetType(e.EntryType, false)
            ?? throw new InvalidOperationException($"package '{asm.GetName().Name}': entry type '{e.EntryType}' for {key} not found");
        MethodInfo m = t.GetMethod(e.EntryMethod, BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null)
            ?? throw new InvalidOperationException($"package '{asm.GetName().Name}': entry method '{e.EntryType}.{e.EntryMethod}()' for {key} not found");
        result = Invoke(m);
        return true;
    }

    /// <summary>
    /// 常量里的类型（cleat-063）：按类型名的前缀（a.b.C → a/b）找登记了这个命名空间的包程序集，载入（不跑入口）后在其中找类型；
    /// 再在已载入的包程序集里找。JVM 上类按名从类路径加载，同理。
    /// </summary>
    public static Type FindType(string typeName)
    {
        if (string.IsNullOrEmpty(typeName)) return null;
        ScanTrustedAssemblies();
        string ns = typeName;
        int dot;
        while ((dot = ns.LastIndexOf('.')) > 0)
        {
            ns = ns.Substring(0, dot);
            if (s_index.TryGetValue(ns.Replace('.', '/').Replace('-', '_'), out var e))
            {
                Type t = EnsureAssembly(e).GetType(typeName, false);
                if (t is not null) return t;
            }
        }
        foreach (var e in s_index.Values.Where(e => e.Assembly is not null).Select(e => e.Assembly).Distinct())
        {
            Type t = e.GetType(typeName, false);
            if (t is not null) return t;
        }
        return null;
    }

    // ---- 内部 ----

    private static void Add(string key, Entry e)
    {
        if (string.IsNullOrEmpty(key)) return;
        s_index.TryAdd(key, e);
    }

    /// <summary>应用的依赖（TPA）里框架目录以外的程序集，第一次查找时扫描一次。</summary>
    private static void ScanTrustedAssemblies()
    {
        if (System.Threading.Interlocked.Exchange(ref s_tpaScanned, 1) != 0) return;
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is not string tpa) return;
        string framework = Path.GetDirectoryName(typeof(object).Assembly.Location) ?? "";
        foreach (string f in tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (framework.Length > 0 && f.StartsWith(framework, StringComparison.Ordinal)) continue;
            // 运行时自己的程序集没有索引（包程序集可以叫 Cleat.*，如 Cleat.Nrepl，不能按前缀跳过；cleat-069）
            string name = Path.GetFileName(f);
            if (RuntimeAssemblies.Contains(name) || name.StartsWith("Microsoft.", StringComparison.Ordinal)
                || name.StartsWith("System.", StringComparison.Ordinal)) continue;
            RegisterFile(f, null);
        }
    }

    private static Assembly EnsureAssembly(Entry e)
    {
        if (e.Assembly is not null) return Prepare(e.Assembly);
        lock (e)
        {
            if (e.Assembly is not null) return Prepare(e.Assembly);
            Assembly asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => !a.IsDynamic && a.GetName().Name == e.AssemblyName
                                     && (e.AssemblyPath is null || string.Equals(a.Location, e.AssemblyPath, StringComparison.Ordinal)));
            asm ??= e.AssemblyPath is null
                ? Assembly.Load(new AssemblyName(e.AssemblyName))
                : AssemblyLoadContext.Default.LoadFromAssemblyPath(e.AssemblyPath);
            foreach (var other in s_index.Values.Where(o => o.Assembly is null && o.AssemblyName == e.AssemblyName))
                other.Assembly = asm;
            e.Assembly = asm;
            return Prepare(asm);
        }
    }

    /// <summary>第一次用到包程序集时：登记到加载器（按名找类型）与它嵌入的接口文件（跨包直接链接、内联）。</summary>
    private static Assembly Prepare(Assembly asm)
    {
        if (s_prepared.TryAdd(asm, true))
        {
            Loader.Register(asm);
            InterfaceFile.TryRegisterEmbedded(asm);
        }
        return asm;
    }

    /// <summary>程序集的 CleatModule 入口（Initialize：镜像核对、预链接）只跑一次。</summary>
    private static void EnsureInitialized(Assembly asm)
    {
        if (s_initialized.TryGetValue(asm, out var done) && done is true) return;
        lock (s_gates.GetOrAdd(asm, _ => new object()))
        {
            if (s_initialized.ContainsKey(asm)) return;
            Loader.Run(asm);
            s_initialized[asm] = true;
        }
    }

    private static object Invoke(MethodInfo m)
    {
        try
        {
            return m.Invoke(null, null);
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }
    }

    /// <summary>按元数据读程序集级 CleatNamespace 特性；不是程序集或读不了时 null。</summary>
    private static List<(string key, string type, string method)> ReadIndex(string path, out string asmName)
    {
        asmName = null;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var pe = new PEReader(fs);
            if (!pe.HasMetadata) return null;
            var md = pe.GetMetadataReader();
            if (!md.IsAssembly) return null;
            var def = md.GetAssemblyDefinition();
            asmName = md.GetString(def.Name);
            List<(string, string, string)> found = null;
            foreach (var h in def.GetCustomAttributes())
            {
                var ca = md.GetCustomAttribute(h);
                if (ca.Constructor.Kind != HandleKind.MemberReference) continue;
                var parent = md.GetMemberReference((MemberReferenceHandle)ca.Constructor).Parent;
                if (parent.Kind != HandleKind.TypeReference) continue;
                var tr = md.GetTypeReference((TypeReferenceHandle)parent);
                if (md.GetString(tr.Name) != "CleatNamespaceAttribute" || md.GetString(tr.Namespace) != "Cleat.Core") continue;
                var blob = md.GetBlobReader(ca.Value);
                if (blob.ReadUInt16() != 1) continue;
                string key = blob.ReadSerializedString(), type = blob.ReadSerializedString(), method = blob.ReadSerializedString();
                (found ??= new List<(string, string, string)>()).Add((key, type, method));
            }
            return found;
        }
        catch (Exception)
        {
            return null;    // 不是 PE 文件、读不了：不是包程序集
        }
    }
}
