using System;
using System.Collections.Generic;
using System.Diagnostics.SymbolStore;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using System.Security.Cryptography;

namespace Cleat.Emit;

public enum EmitMode
{
    /// <summary>PersistedAssemblyBuilder → dll 文件（确定性：MVID 与时间戳由内容哈希得出）。</summary>
    Persisted,
    /// <summary>AssemblyBuilder.RunAndCollect，用于 eval。</summary>
    InMemory,
}

/// <summary>
/// Lcil → 程序集（designs/05-cil-backend.md；规格 LCIL.md）。从作者自己的 IronScheme.Emit（TTalkPro/IronScheme 分支，同一作者，无上游代码）的 Emitter.cs 移植。不引用 Cleat.Core 项目：
/// 运行时类型按名字从已加载或 references 中的程序集解析；互操作规则调用 Core 的 TypeResolver / Reflector。
/// </summary>
public sealed partial class LcilEmitter
{
    // cleat-114：eval 装载的程序集（EmitLoaded）不加 AggressiveOptimization——它跳过分层编译，也就没有动态 PGO。
    // 写成文件的镜像与包照旧加：去掉它会改变 dll 的字节，镜像 id 跟着变，种子里编译器包记的 id 就对不上（要两步重建种子，另做）。
    private bool _tiered;

    public const string CoreAssemblyName = "Cleat.Core";

    /// <summary>额外的程序集查找目录（命令行 -r）；默认含发射器所在目录。</summary>
    public static readonly List<string> ProbeDirectories = new List<string> { AppContext.BaseDirectory };

    static LcilEmitter()
    {
        AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        {
            foreach (var dir in ProbeDirectories)
            {
                var p = Path.Combine(dir, name.Name + ".dll");
                if (File.Exists(p)) return ctx.LoadFromAssemblyPath(Path.GetFullPath(p));
            }
            return null;
        };
    }

    /// <summary>发射到内存并返回程序集。</summary>
    public static Assembly EmitInMemory(string lcil, string file = "lcil") => EmitInMemory(LcilParser.Parse(lcil, file));

    /// <summary>发射到内存；Lcil 用 (live-constants n) 时由 constants 直接提供常量对象。</summary>
    public static Assembly EmitInMemory(string lcil, string file, object[] constants) => EmitInMemory(LcilParser.Parse(lcil, file), constants);

    public static Assembly EmitInMemory(LcilAssembly a) => EmitInMemory(a, null);

    /// <summary>eval 的快速路径：直接发射编译器产出的 Lcil S 表达式（Clojure 数据），不经文本（iron-033）。</summary>
    /// <summary>
    /// 本线程发射时优先查找短类型名的程序集（cleat-139）：运行时特化单元引用原函数所在 eval 单元里按短名定义的类型；
    /// 同名类型被重定义过时，只有先查原程序集才能拿到原函数看到的那个。结果不进静态缓存。
    /// </summary>
    [ThreadStatic] public static Assembly[] PreferredAssemblies;

    public static Assembly EmitInMemoryData(object lcil, string file, object[] constants) =>
      EmitInMemory(LcilParser.FromTree(DataToTree(lcil, file), file), constants);

    /// <summary>编译器产出的 Lcil 数据（ISeq / Symbol / 字符串 / 数）→ SNode 树。</summary>
    private static SList DataToTree(object lcil, string file)
    {
        var tree = ClojureData.Convert(lcil, LoadReference(CoreAssemblyName));
        if (!(tree is SList top) || top.Head != "lcil") throw new LcilException($"{file}: expected a (lcil 1 ...) form");
        return top;
    }

    public static Assembly EmitInMemory(LcilAssembly a, object[] liveConstants)
    {
        if (a.LiveConstants >= 0 && (liveConstants == null || liveConstants.Length != a.LiveConstants))
            throw new LcilException($"{a.File}: expected {a.LiveConstants} live constants");
        // 只对 eval 单元（eval$cN）：编译镜像 / 包期间求值的单元与正在运行的镜像同名，装进默认上下文会按名字绑错
        if (s_tieredEval && a.Name != null && a.Name.StartsWith("eval$", StringComparison.Ordinal)) return EmitLoaded(a, liveConstants);
        var ab = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(a.Name), AssemblyBuilderAccess.RunAndCollect);
        var e = new LcilEmitter(a, ab.DefineDynamicModule(a.Name), false) { _live = liveConstants, _chunked = true };
        e.Build();
        return ab;
    }

    // cleat-114：eval 的代码默认发射成 PE 字节、装载进默认的 AssemblyLoadContext（不可回收）——RunAndCollect 的动态程序集不参与分层编译，
    // 没有动态 PGO（有守卫的去虚拟化，JVM 内联缓存的对应物），静态字段还要多一层间接；编成包从文件加载的同一份代码快 20% ~ 37%。
    // 代价是求值生成的类型不再被回收。CLEAT_NO_OPT 含 tiered 时用原来的 RunAndCollect。
    private static readonly bool s_tieredEval = !(Environment.GetEnvironmentVariable("CLEAT_NO_OPT") ?? "").Contains("tiered");

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Assembly> s_loaded = new();

    /// <summary>
    /// eval 程序集的装载上下文（不可回收，代码照常分层编译）：依赖先取进程里已经装载的同名程序集（正在用的镜像与编译器包，
    /// 不按名字从磁盘另找一份——编译镜像期间磁盘上的同名包是对别的镜像编译的），eval 程序集之间按登记表。
    /// </summary>
    private sealed class EvalLoadContext : AssemblyLoadContext
    {
        public EvalLoadContext() : base("cleat-eval", isCollectible: false) { }

        protected override Assembly Load(AssemblyName name)
        {
            if (name.Name == null) return null;
            if (s_loaded.TryGetValue(name.Name, out var e)) return e;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                if (!asm.IsDynamic && asm.GetName().Name == name.Name) return asm;
            return null;   // 其余（框架）交给默认上下文
        }
    }

    private static readonly EvalLoadContext s_evalContext = new();

    private static Assembly EmitLoaded(LcilAssembly a, object[] liveConstants)
    {
        var ab = new PersistedAssemblyBuilder(new AssemblyName(a.Name), typeof(object).Assembly);
        // cleat-158：debug 模式挂符号文档，连同内存 PDB 一起装载——栈帧拿到 文件:行
        var e = new LcilEmitter(a, ab.DefineDynamicModule(a.Name), true) { _live = liveConstants, _tiered = true };
        e.Build();
        MetadataBuilder metadata = ab.GenerateMetadata(out BlobBuilder ilStream, out BlobBuilder fieldData, out MetadataBuilder pdbBuilder);
        // 内存 portable PDB（与 WritePE 同款的确定性 id）
        Func<IEnumerable<Blob>, BlobContentId> idProvider = blobs =>
        {
            using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var b in blobs) h.AppendData(b.GetBytes());
            return BlobContentId.FromHash(h.GetHashAndReset());
        };
        var pdbBlob = new BlobBuilder();
        var portablePdb = new PortablePdbBuilder(pdbBuilder, metadata.GetRowCounts(), default, idProvider);
        BlobContentId pdbId = portablePdb.Serialize(pdbBlob);
        var debug = new DebugDirectoryBuilder();
        debug.AddCodeViewEntry(a.Name + ".pdb", pdbId, portablePdb.FormatVersion);
        var pe = new ManagedPEBuilder(new PEHeaderBuilder(imageCharacteristics: Characteristics.Dll | Characteristics.ExecutableImage),
                                      new MetadataRootBuilder(metadata), ilStream, fieldData,
                                      debugDirectoryBuilder: debug, deterministicIdProvider: idProvider);
        var blob = new BlobBuilder();
        pe.Serialize(blob);
        if (s_loaded.ContainsKey(a.Name) && Environment.GetEnvironmentVariable("CLEAT_DEBUG_EVAL") == "1") Console.Error.WriteLine("eval: duplicate assembly name " + a.Name);
        var loaded = s_evalContext.LoadFromStream(new MemoryStream(blob.ToArray()), new MemoryStream(pdbBlob.ToArray()));
        s_loaded[a.Name] = loaded;
        return loaded;
    }

    /// <summary>发射并保存为 dll；pdb 为真时同时生成 Portable PDB（同名 .pdb）。</summary>
    public static void Save(string lcil, string file, string outPath, bool pdb = false) => Save(LcilParser.Parse(lcil, file), outPath, pdb);

    /// <summary>直接保存编译器产出的 Lcil S 表达式（不经文本，iron-033）；compile-library 使用。</summary>
    public static void SaveData(object lcil, string file, string outPath, bool pdb) =>
      Save(LcilParser.FromTree(DataToTree(lcil, file), file), outPath, pdb);

    public static void Save(LcilAssembly a, string outPath, bool pdb = false)
    {
        if (a.LiveConstants >= 0) throw new LcilException($"{a.File}: live constants can only be emitted in memory");
        var ab = new PersistedAssemblyBuilder(new AssemblyName(a.Name), typeof(object).Assembly);
        var mb = ab.DefineDynamicModule(a.Name);
        var e = new LcilEmitter(a, mb, pdb);
        e.Build();
        WritePE(ab, a, outPath, pdb);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Assembly, bool> s_depsLoaded =
      new System.Collections.Concurrent.ConcurrentDictionary<Assembly, bool>();

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Type> s_staticTypes =
      new System.Collections.Concurrent.ConcurrentDictionary<string, Type>();

    private readonly LcilAssembly _asm;
    private readonly ModuleBuilder _module;
    private readonly bool _debug;
    private readonly Dictionary<string, TypeBuilder> _types = new Dictionary<string, TypeBuilder>();
    private readonly Dictionary<LcilType, TypeBuilder> _builders = new Dictionary<LcilType, TypeBuilder>();
    private readonly Dictionary<string, List<(LcilMethod M, MethodBase B, Type[] P)>> _methods = new Dictionary<string, List<(LcilMethod, MethodBase, Type[])>>();
    private readonly Dictionary<(string, string), FieldBuilder> _fields = new Dictionary<(string, string), FieldBuilder>();
    private readonly HashSet<(string, string)> _volatile = new HashSet<(string, string)>();
    private readonly List<Assembly> _references = new List<Assembly>();
    private readonly Dictionary<string, ISymbolDocumentWriter> _documents = new Dictionary<string, ISymbolDocumentWriter>();
    private FieldInfo _constantsField;

    // 内存模式下类型很多时分到多个动态程序集（iron-033）：同一动态模块中 TypeBuilder.CreateType 的开销随类型数线性增长，
    // 总耗时是平方级（16000 个类型约 8 秒，每 1000 个一个程序集约 0.3 秒）。生成的成员都是 public，跨程序集引用不受影响；
    // 入口类型与常量表留在主程序集中。dll 模式（PersistedAssemblyBuilder）只能有一个模块，不分块。
    private const int ChunkSize = 1000;
    private bool _chunked;
    private ModuleBuilder _chunk;
    private int _chunkCount, _chunkFill;

    private ModuleBuilder ModuleFor(LcilType t)
    {
        if (!_chunked || t.Name == _asm.EntryType || _asm.Types.Count <= ChunkSize) return _module;
        if (_chunk == null || _chunkFill == ChunkSize)
        {
            string name = _asm.Name + "$" + ++_chunkCount;
            _chunk = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(name), AssemblyBuilderAccess.RunAndCollect).DefineDynamicModule(name);
            _chunkFill = 0;
        }
        _chunkFill++;
        return _chunk;
    }

    private LcilEmitter(LcilAssembly asm, ModuleBuilder module, bool debug)
    {
        _asm = asm;
        _module = module;
        _debug = debug;
        // Cleat.Core 是隐含引用（CleatModule 属性、Clojure 互操作、常量表都要它）
        foreach (var r in new[] { CoreAssemblyName }.Concat(asm.References).Distinct()) _references.Add(LoadReference(r));
        // 互操作类型名在已加载程序集中查找：被引用程序集的依赖也要加载
        // 每个程序集只处理一次：eval 与 compile-library 每次都新建发射器，重复 Assembly.Load 是热点（iron-033）
        foreach (var a in _references.ToList())
            if (s_depsLoaded.TryAdd(a, true))
                foreach (var dep in a.GetReferencedAssemblies())
                    try { Assembly.Load(dep); } catch (FileNotFoundException) { }
    }

    private Exception Err(SNode pos, string msg) => new LcilException(pos != null ? $"{pos.Where(_asm.File)}: {msg}" : $"{_asm.File}: {msg}");

    private static Assembly LoadReference(string name)
    {
        if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && File.Exists(name)) return Assembly.LoadFrom(Path.GetFullPath(name));
        var loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(x => x.GetName().Name == name);
        if (loaded != null) return loaded;
        try { return Assembly.Load(new AssemblyName(name)); }
        catch (FileNotFoundException) { }
        // 默认探测（应用的 deps.json）找不到时：当前目录、库搜索路径（-I）、应用目录下的 名字.dll
        foreach (var dir in ReferenceDirectories())
        {
            string path = Path.Combine(dir, name + ".dll");
            if (File.Exists(path)) return Assembly.LoadFrom(Path.GetFullPath(path));
        }
        throw new LcilException($"referenced assembly not found: {name}");
    }

    private static IEnumerable<string> ReferenceDirectories()
    {
        yield return Environment.CurrentDirectory;
        var core = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(x => x.GetName().Name == CoreAssemblyName);
        if (core?.GetType("Cleat.Core.Runtime.Loader")?.GetProperty("LoadPath")?.GetValue(null) is IEnumerable<string> paths)
            foreach (var p in paths) yield return p;
        yield return AppContext.BaseDirectory;
    }

    // ———————————————— 类型解析 ————————————————

    /// <summary>Lcil 类型引用（§2.4）：本程序集的类型 → 全名查找；后缀 [] 与 &amp;。</summary>
    private Type T(string name, SNode pos)
    {
        if (name.EndsWith("[]", StringComparison.Ordinal)) return T(name.Substring(0, name.Length - 2), pos).MakeArrayType();
        if (name.EndsWith("&", StringComparison.Ordinal)) return T(name.Substring(0, name.Length - 1), pos).MakeByRefType();
        if (_types.TryGetValue(name, out var tb)) return tb;
        // 本单元的类型先于跨实例的静态缓存（缓存里可能是进程中运行着的同名程序集——上一级镜像——的类型）
        if (QualifiedSplit(name) is int q && OwnQualifier(name.Substring(q + 1).Trim()) && _types.TryGetValue(name.Substring(0, q).Trim(), out var mine))
            return mine;
        if (PreferredAssemblies is { } prefer && QualifiedSplit(name) == null)
            foreach (var a in prefer)
                if (a.GetType(name) is { } p && (p.IsPublic || p.IsNestedPublic)) return p;
        if (s_staticTypes.TryGetValue(name, out var cached)) return cached;
        var t = Type.GetType(name);
        if (t == null && QualifiedSplit(name) is int comma)
        {
            // "类型名, 程序集名"（iron-034e：直接引用 boot 镜像或已加载的库产物中的类）。Type.GetType 经运行时的解析
            // （ModuleLoader 登记的程序集）找不到时，按简单名在已加载的程序集中找
            string asmName = name.Substring(comma + 1).Trim(), typeName = name.Substring(0, comma).Trim();
            // [cleat] 本单元自己定义的同名类型优先：Clojure 的 deftype / gen-interface 在 eval 时生成于动态程序集（Lcore 里写
            // "类型, eval$N"），AOT 时同一个类型在本单元里重新定义，引用应指向本单元的（cleat-053）
            if (_types.TryGetValue(typeName, out var own)) return own;
            foreach (var a in _references.Concat(AppDomain.CurrentDomain.GetAssemblies()))
            {
                if (a.GetName().Name != asmName) continue;
                var u = a.GetType(typeName);
                if (u != null && (u.IsPublic || u.IsNestedPublic)) return u;
            }
        }
        if (t == null)
            foreach (var a in _references.Concat(AppDomain.CurrentDomain.GetAssemblies()))
            {
                if (a.IsDynamic) continue;
                var u = a.GetType(name);
                if (u != null && (u.IsPublic || u.IsNestedPublic)) { t = u; break; }
            }
        // 还没加载的框架程序集（cleat-092：生成的代码只引用 Cleat.Core，System.Diagnostics.Process 等不会被依赖顺带带进来；
        // 独立运行的 cleat-emit 发射种子时按命名空间前缀在受信平台程序集里找：System.Diagnostics.ProcessStartInfo → System.Diagnostics.Process.dll 等）
        t ??= FrameworkType(name);
        if (t == null) throw Err(pos, "type not found: " + name);
        // 只缓存非动态程序集中的类型：已加载的程序集不会卸载，结果不变；每次 eval 都新增动态程序集，不缓存时查找越来越慢（iron-033）
        // 库产物（.fasl / 库 dll）可能被重新加载出同名的新副本，运行时按最新的解析：程序集限定名只缓存 boot 的
        if (!t.Assembly.IsDynamic && (QualifiedSplit(name) == null || name.EndsWith(", cleat.boot", StringComparison.Ordinal)))
            s_staticTypes[name] = t;
        return t;
    }

    private static Dictionary<string, string> s_platformAssemblies;

    /// <summary>类型名的命名空间前缀对应的受信平台程序集（TRUSTED_PLATFORM_ASSEMBLIES）里的类型，没有时 null。</summary>
    private static Type FrameworkType(string name)
    {
        if (QualifiedSplit(name) != null || name.IndexOf('[') >= 0) return null;
        s_platformAssemblies ??= (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .GroupBy(f => Path.GetFileNameWithoutExtension(f), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        // 由长到短试命名空间前缀：System.Diagnostics.ProcessStartInfo → System.Diagnostics.ProcessStartInfo、System.Diagnostics、System
        for (int dot = name.Length; dot > 0; dot = name.LastIndexOf('.', dot - 1))
        {
            string prefix = name.Substring(0, dot);
            if (!s_platformAssemblies.TryGetValue(prefix, out var file)) continue;
            Assembly a;
            try { a = Assembly.Load(new AssemblyName(Path.GetFileNameWithoutExtension(file))); }
            catch (Exception) { continue; }
            var u = a.GetType(name);
            if (u != null && (u.IsPublic || u.IsNestedPublic)) return u;
            // 类型转发（System.Diagnostics.Process 的类型在 System.Diagnostics.Process.dll；有的在 System.Private.CoreLib）
            foreach (var r in a.GetReferencedAssemblies())
            {
                try { u = Assembly.Load(r).GetType(name); } catch (Exception) { continue; }
                if (u != null && (u.IsPublic || u.IsNestedPublic)) return u;
            }
        }
        return null;
    }

    /// <summary>方括号外的第一个逗号的位置（程序集限定名），没有时为 null。</summary>
    private static int? QualifiedSplit(string name)
    {
        int depth = 0;
        for (int i = 0; i < name.Length; i++)
            switch (name[i])
            {
                case '[': depth++; break;
                case ']': depth--; break;
                case ',' when depth == 0: return i;
            }
        return null;
    }

    private Type[] Ts(IEnumerable<string> names, SNode pos) => names.Select(n => T(n, pos)).ToArray();

    // ———————————————— 定义 ————————————————

    private void Build()
    {
        // 1. 类型：基类在本程序集中的先定义（按名字索引，避免类型多时平方级查找）
        var byName = new Dictionary<string, LcilType>();
        foreach (var t in _asm.Types) byName.TryAdd(t.Name, t);
        var defining = new HashSet<string>();
        void Define(LcilType t)
        {
            if (_builders.ContainsKey(t)) return;
            if (!defining.Add(t.Name)) throw Err(t.Pos, "cyclic base types");
            if (t.Base != null && byName.TryGetValue(t.Base, out var bt)) Define(bt);
            DefineType(t);
        }
        foreach (var t in _asm.Types) Define(t);
        if (_asm.ConstantCount > 0) DefineConstants();
        if (_asm.EntryType != null) DefineEntry();
        foreach (var (atype, aargs) in _asm.Attributes) ((AssemblyBuilder)_module.Assembly).SetCustomAttribute(Attribute(atype, aargs, null));
        // 2. 字段与方法签名
        foreach (var t in _asm.Types)
        {
            var tb = _builders[t];
            foreach (var f in t.Fields)
            {
                var fa = f.Attrs.Contains("private") ? FieldAttributes.Private : FieldAttributes.Public;
                if (f.IsStatic) fa |= FieldAttributes.Static;
                if (f.Attrs.Contains("readonly")) fa |= FieldAttributes.InitOnly;
                if (_fields.ContainsKey((t.Name, f.Name))) throw Err(t.Pos, $"duplicate field {t.Name}::{f.Name}");
                _fields[(t.Name, f.Name)] = f.IsVolatile
                    ? tb.DefineField(f.Name, T(f.Type, t.Pos), new[] { typeof(System.Runtime.CompilerServices.IsVolatile) }, null, fa)
                    : tb.DefineField(f.Name, T(f.Type, t.Pos), fa);
                if (f.IsVolatile) _volatile.Add((t.Name, f.Name));
            }
            foreach (var m in t.Methods) DefineMethod(t, tb, m);
            // (lazy-start)：没有类型初始化器的类型（接口除外）也加一个，只拉起运行时（designs/17 §4.1）
            if (_asm.LazyStart && !t.Attrs.Contains("interface") && !t.Methods.Any(m => m.IsCctor))
            {
                var cil = tb.DefineTypeInitializer().GetILGenerator();
                EmitEnsureStarted(cil);
                cil.Emit(OpCodes.Ret);
            }
        }
        // 3. 方法体
        foreach (var t in _asm.Types)
            foreach (var (m, b, _) in _methods[t.Name])
                if (m.Body.Count > 0) EmitBody(t, m, b);
        // 4. 完成类型（基类先）
        var done = new HashSet<string>();
        void Create(LcilType t)
        {
            if (!done.Add(t.Name)) return;
            if (t.Base != null && byName.TryGetValue(t.Base, out var bt)) Create(bt);
            _builders[t].CreateType();
        }
        _constantsType?.CreateType();
        foreach (var t in _asm.Types) Create(t);
    }

    /// <summary>call Cleat.Core.Runtime.RuntimeBootstrap.EnsureStarted()：运行时还没启动时启动它（cleat-067）。</summary>
    private void EmitEnsureStarted(ILGenerator il) =>
        il.Emit(OpCodes.Call, LoadReference(CoreAssemblyName).GetType("Cleat.Core.Runtime.RuntimeBootstrap", true)
                                .GetMethod("EnsureStarted", Type.EmptyTypes));

    /// <summary>入口（LCIL.md §7）：校验 public static object M()，写程序集级属性 CleatModule("类型", "方法")。</summary>
    private void DefineEntry()
    {
        var t = _asm.Types.FirstOrDefault(x => x.Name == _asm.EntryType) ?? throw new LcilException($"{_asm.File}: entry type not found: {_asm.EntryType}");
        var m = t.Methods.FirstOrDefault(x => x.Name == _asm.EntryMethod);
        if (m == null || !m.IsStatic || m.IsCctor || m.Params.Count != 0 || m.Ret != "System.Object" || m.Attrs.Contains("private"))
            throw Err(m?.Pos ?? t.Pos, $"entry {_asm.EntryType}::{_asm.EntryMethod} must be a public static method with no parameters returning System.Object");
        ((AssemblyBuilder)_module.Assembly).SetCustomAttribute(Attribute("Cleat.Core.CleatModuleAttribute", new List<string> { _asm.EntryType, _asm.EntryMethod }, t.Pos));
    }

    /// <summary>(attribute "T" "参数" …)：取 T 中参数全为 string 且个数相符的构造函数。</summary>
    private CustomAttributeBuilder Attribute(string type, List<string> args, SNode pos)
    {
        var at = T(type, pos);
        var ctor = at.GetConstructors().FirstOrDefault(c => c.GetParameters().Length == args.Count && c.GetParameters().All(p => p.ParameterType == typeof(string)))
                   ?? throw Err(pos, $"attribute {type} has no constructor taking {args.Count} string(s)");
        return new CustomAttributeBuilder(ctor, args.Cast<object>().ToArray());
    }

    private void DefineType(LcilType t)
    {
        var ta = TypeAttributes.Public | TypeAttributes.BeforeFieldInit;
        if (t.Attrs.Contains("sealed")) ta |= TypeAttributes.Sealed;
        if (t.Attrs.Contains("abstract")) ta |= TypeAttributes.Abstract;
        if (t.Attrs.Contains("interface")) ta = TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract;
        var parent = t.Attrs.Contains("interface") ? null : T(t.Base, t.Pos);
        var tb = ModuleFor(t).DefineType(t.Name, ta, parent, Ts(t.Interfaces, t.Pos));
        foreach (var (atype, aargs) in t.Attributes) tb.SetCustomAttribute(Attribute(atype, aargs, t.Pos));
        _types[t.Name] = tb;
        _builders[t] = tb;
        _methods[t.Name] = new List<(LcilMethod, MethodBase, Type[])>();
    }

    // (attrs protected)：覆写基类的 protected 方法（cleat-043 proxy / gen-class）；跨程序集的 protected internal 也只能覆写为 protected
    private static MethodAttributes Access(LcilMethod m) =>
        m.Attrs.Contains("private") ? MethodAttributes.Private : m.Attrs.Contains("protected") ? MethodAttributes.Family : MethodAttributes.Public;

    private void DefineMethod(LcilType t, TypeBuilder tb, LcilMethod m)
    {
        var ps = Ts(m.Params, m.Pos);
        MethodBase b;
        if (m.IsCctor) b = tb.DefineTypeInitializer();
        else if (m.IsCtor)
        {
            var ca = Access(m) | MethodAttributes.HideBySig;
            b = tb.DefineConstructor(ca, CallingConventions.Standard, ps);
        }
        else
        {
            var ma = Access(m) | MethodAttributes.HideBySig;
            if (m.IsStatic) ma |= MethodAttributes.Static;
            if (m.Attrs.Contains("virtual")) ma |= MethodAttributes.Virtual | MethodAttributes.NewSlot;
            if (m.Attrs.Contains("override")) ma |= MethodAttributes.Virtual;
            if (m.Attrs.Contains("abstract")) ma |= MethodAttributes.Virtual | MethodAttributes.Abstract | MethodAttributes.NewSlot;
            if (m.ImplementsType != null) ma |= MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.NewSlot;
            var mb = tb.DefineMethod(m.Name, ma, T(m.Ret, m.Pos), ps);
            // (attrs optimize)：JIT 直接按优化编译（不经 tier-0）——tier-0 把局部变量与溢出的临时变量都报告为整个方法内活着，
            // 只调用一次的长调用里抓着惰性序列的头（cleat-042 第 5 项）；优化代码按活跃区间报告
            // (attrs async)：运行时 async 方法（MethodImplAttributes.Async = 0x2000，cleat-043）——声明返回 Task<T>，体返回 T，
            // 体里的 AsyncHelpers.Await 由运行时挂起 / 恢复
            // 分层编译时（eval）：模块入口类型（顶层形式的 Init 方法）仍直接按优化编译——它们只运行一次，停在 tier-0 会把临时变量报告为整个方法内活着，
            // (reduce + (map inc (range 3e7))) 抓住惰性序列的头（512 MB 堆下 OutOfMemory）；函数类的方法分层编译
            // 分层编译只给带 (attrs tiered) 的方法（codegen：函数体有循环或自递归的顶层 defn）：只调用一次、没有循环的方法停在 tier-0 时，
            // JIT 为调用实参建的临时变量在调用期间抓着惰性序列（实参无法在 IL 里清掉）；有循环的经 OSR 进入优化代码
            // 写成文件的镜像与包（cleat-117）：codegen 同样给分层的函数类加 tiered，这些方法不加 AggressiveOptimization
            bool optimize = _tiered ? (t.Name == _asm.EntryType || !m.Attrs.Contains("tiered")) : m.Attrs.Contains("optimize") && !m.Attrs.Contains("tiered");
            var impl = (optimize ? MethodImplAttributes.AggressiveOptimization : 0)
                     | (m.Attrs.Contains("async") ? (MethodImplAttributes)0x2000 : 0);
            if (impl != 0) mb.SetImplementationFlags(impl);
            if (m.ImplementsType != null)
            {
                var it = T(m.ImplementsType, m.Pos);
                var target = FindInterfaceMethod(it, m.ImplementsName, ps, m.Pos);
                tb.DefineMethodOverride(mb, target);
            }
            b = mb;
        }
        if (_methods[t.Name].Any(x => x.M.Name == m.Name && x.P.SequenceEqual(ps)))
            throw Err(m.Pos, $"duplicate method {t.Name}::{m.Name}");
        _methods[t.Name].Add((m, b, ps));
    }

    /// <summary>接口 it（可为本 Lcil 中定义的接口）中名为 name、参数类型为 ps 的方法。</summary>
    private MethodInfo FindInterfaceMethod(Type it, string name, Type[] ps, SNode pos)
    {
        if (it is TypeBuilder)
        {
            // 本 Lcil 定义的接口（按 Lcil 的类型名，即全名）及它在 Lcil 中声明的父接口（cleat-053：gen-interface 与实现它的 deftype 同在 Clojure 镜像里）
            string key = _methods.ContainsKey(it.FullName) ? it.FullName : it.Name;
            if (_methods.TryGetValue(key, out var own) && own.FirstOrDefault(x => x.M.Name == name && x.P.SequenceEqual(ps)).B is MethodInfo hit)
                return hit;
            var lt = _asm.Types.FirstOrDefault(x => x.Name == key);
            if (lt != null)
                foreach (var parent in lt.Interfaces)
                {
                    try { return FindInterfaceMethod(T(parent, pos), name, ps, pos); }
                    catch (LcilException) { }
                }
            throw Err(pos, $"interface method not found: {key}::{name}");
        }
        foreach (var i in new[] { it }.Concat(it.GetInterfaces()))
        {
            var m = i.GetMethod(name, BindingFlags.Public | BindingFlags.Instance, null, ps, null);
            if (m != null) return m;
        }
        throw Err(pos, $"interface method not found: {it.FullName}::{name}");
    }

    /// <summary>"类型, 程序集" 的程序集指本单元：eval$N（eval 时生成、AOT 时在本单元重新定义的类型）或本程序集自己的名字。</summary>
    private bool OwnQualifier(string asmName) => asmName.StartsWith("eval$", StringComparison.Ordinal) || asmName == _asm.Name;

    // ———————————————— 成员引用 ————————————————

    private MethodBase Method(MethodRef r, SNode pos)
    {
        var ps = Ts(r.Params, pos);
        MethodBase found = null;
        // "类型, eval$N" 指向本单元重新定义的同名类型时（T 的 cleat-053 规则），成员也在本单元的定义里找（cleat-042）
        string key = r.Type;
        // 程序集名是本程序集自己时同样（镜像里引用自己的类，cleat-091：从种子发射时没有已加载的同名镜像可找）
        if (!_methods.ContainsKey(key) && QualifiedSplit(key) is int comma && _types.ContainsKey(key.Substring(0, comma).Trim())
            && OwnQualifier(key.Substring(comma + 1).Trim()))
            key = key.Substring(0, comma).Trim();
        if (_methods.TryGetValue(key, out var own))
            found = own.FirstOrDefault(x => (x.B is ConstructorInfo ? x.M.IsCctor ? ".cctor" : ".ctor" : x.M.Name) == r.Name && x.P.SequenceEqual(ps)).B;
        if (found == null)
        {
            var t = T(r.Type, pos);
            if (t is TypeBuilder) t = t.BaseType;   // 继承来的成员：在基类中找
            const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
            // 实例构造函数：不含 Static，否则类型有 .cctor 时与无参 .ctor 二义（AsyncFrame）
            if (r.Name == ".ctor") found = t.GetConstructor(All & ~BindingFlags.Static, null, ps, null);
            else
            {
                for (var s = t; s != null && found == null; s = s.BaseType)
                    found = s is TypeBuilder ? null : s.GetMethod(r.Name, All | BindingFlags.DeclaredOnly, null, ps, null);
            }
        }
        if (found == null) throw Err(pos, "method not found: " + r);
        if (found is MethodInfo mi && r.Name != ".ctor")
        {
            var ret = T(r.Ret, pos);
            if (mi.ReturnType != ret) throw Err(pos, $"return type mismatch for {r}: {mi.ReturnType} vs {ret}");
        }
        return found;
    }

    private FieldInfo Field(FieldRef r, SNode pos)
    {
        if (_fields.TryGetValue((r.Type, r.Name), out var fb)) return fb;
        // "类型, eval$N" 指向本单元重新定义的同名类型（同 Method，cleat-042）
        if (QualifiedSplit(r.Type) is int comma && r.Type.Substring(comma + 1).Trim().StartsWith("eval$", StringComparison.Ordinal)
            && _fields.TryGetValue((r.Type.Substring(0, comma).Trim(), r.Name), out var own)) return own;
        var t = T(r.Type, pos);
        if (t is TypeBuilder) t = t.BaseType;
        var f = t?.GetField(r.Name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
        return f ?? throw Err(pos, $"field not found: {r.Type}::{r.Name}");
    }

    // ———————————————— 方法体 ————————————————

    private sealed class BodyContext
    {
        public ILGenerator IL;
        public LcilMethod M;
        public Dictionary<string, Label> Labels = new Dictionary<string, Label>();
        public ISymbolDocumentWriter Doc;
        public string[] Namespaces;
        /// <summary>互操作指令按哪个运行时决议（方法的 runtime 子句，缺省程序集的）。</summary>
        public string Runtime;
        // 互操作用的临时局部变量（object），按需分配、可复用
        public List<LocalBuilder> Temps = new List<LocalBuilder>();
        /// <summary>分层编译时改成"取出并清空"（ldloca + Locals.Take）的 ldloc（cleat-114）</summary>
        public HashSet<Instr> TakeLoads;
    }

    /// <summary>ldloc / stloc / ldloca 的种类与下标；别的指令 null。</summary>
    private static (char Kind, int Index)? LocalOp(Instr ins)
    {
        if (ins.Kind != InstrKind.Cil) return null;
        string n = ins.OpCode.Name;
        char k = n.StartsWith("ldloca", StringComparison.Ordinal) ? 'a' : n.StartsWith("ldloc", StringComparison.Ordinal) ? 'l' : n.StartsWith("stloc", StringComparison.Ordinal) ? 's' : '\0';
        if (k == '\0') return null;
        int dot = n.IndexOf('.');
        if (dot > 0 && n.Length > dot + 1 && char.IsDigit(n[dot + 1])) return (k, n[dot + 1] - '0');
        return ins.Operand switch { int i => (k, i), long l => (k, (int)l), _ => null };
    }

    /// <summary>
    /// cleat-114：分层编译的方法先以 tier-0 运行，tier-0 把所有局部变量报告为整个方法内活着——codegen 只写一次、读一次的 object 临时变量
    /// （内联展开、实参临时变量……）在读过之后仍抓着值；只调用一次的方法（-main 一类）把惰性序列交给长时间运行的被调用者时，头被抓住（512 MB 堆下 OutOfMemory）。
    /// 顶层指令列表里只写一次、读一次、不取地址、读在写之后且中间没有标签与 try 的 object 局部变量，读改成取出并清空（优化代码里 Take 内联成两条指令）。
    /// </summary>
    private static HashSet<Instr> TakeLoadsFor(LcilMethod m)
    {
        var stores = new Dictionary<int, int>(); var loads = new Dictionary<int, int>(); var addr = new HashSet<int>();
        void Count(List<Instr> list)
        {
            foreach (var ins in list)
            {
                if (ins.Kind == InstrKind.Try)
                {
                    Count(ins.TryBody);
                    foreach (var c in ins.Catches) Count(c.Body);
                    if (ins.Finally != null) Count(ins.Finally);
                    continue;
                }
                if (LocalOp(ins) is (char k, int i))
                {
                    if (k == 'a') addr.Add(i);
                    else if (k == 's') stores[i] = stores.GetValueOrDefault(i) + 1;
                    else loads[i] = loads.GetValueOrDefault(i) + 1;
                }
            }
        }
        Count(m.Body);
        var result = new HashSet<Instr>();
        var storeAt = new Dictionary<int, int>();
        for (int p = 0; p < m.Body.Count; p++)
        {
            var ins = m.Body[p];
            if (ins.Kind == InstrKind.Label || ins.Kind == InstrKind.Try) { storeAt.Clear(); continue; }   // 标签 / try 之后不再是直线
            if (LocalOp(ins) is not (char k, int i)) continue;
            if (i >= m.Locals.Count || m.Locals[i] != "System.Object" || addr.Contains(i) || stores.GetValueOrDefault(i) != 1 || loads.GetValueOrDefault(i) != 1) continue;
            if (k == 's') storeAt[i] = p;
            else if (k == 'l' && storeAt.ContainsKey(i)) result.Add(ins);
        }
        return result;
    }

    private static System.Reflection.MethodInfo s_take;

    private void EmitBody(LcilType t, LcilMethod m, MethodBase b)
    {
        var il = b is ConstructorBuilder cb
          ? cb.GetILGenerator()
          : m.MaxStack is int methodMaxStack ? ((MethodBuilder)b).GetILGenerator(methodMaxStack) : ((MethodBuilder)b).GetILGenerator();
        var cx = new BodyContext { IL = il, M = m, Namespaces = m.Namespaces.ToArray(), Runtime = m.Runtime ?? _asm.Runtime };
        if (_asm.LazyStart && m.IsCctor) EmitEnsureStarted(il);
        foreach (var l in m.Locals) il.DeclareLocal(T(l, m.Pos));
        if (_debug && m.Source != null)
        {
            if (!_documents.TryGetValue(m.Source, out var doc)) _documents[m.Source] = doc = _module.DefineDocument(m.Source);
            cx.Doc = doc;
        }
        void DefineLabels(List<Instr> list)
        {
            foreach (var ins in list)
            {
                if (ins.Kind == InstrKind.Label) cx.Labels[(string)ins.Operand] = il.DefineLabel();
                else if (ins.Kind == InstrKind.Try)
                {
                    DefineLabels(ins.TryBody);
                    foreach (var c in ins.Catches) DefineLabels(c.Body);
                    if (ins.Finally != null) DefineLabels(ins.Finally);
                }
            }
        }
        DefineLabels(m.Body);
        if ((_tiered && t.Name != _asm.EntryType) || m.Attrs.Contains("tiered"))
        {
            cx.TakeLoads = TakeLoadsFor(m);
            s_take ??= LoadReference(CoreAssemblyName).GetType("Cleat.Core.Runtime.Locals", true).GetMethod("Take");
        }
        Instrs(cx, m.Body);
    }

    private void Instrs(BodyContext cx, List<Instr> list)
    {
        foreach (var ins in list) Instruction(cx, ins);
    }

    private static readonly Dictionary<string, OpCode> s_longForms = new Dictionary<string, OpCode>();

    private static OpCode LongForm(OpCode op)
    {
        if (op.OperandType != OperandType.ShortInlineBrTarget) return op;
        lock (s_longForms)
        {
            if (!s_longForms.TryGetValue(op.Name, out var l))
                s_longForms[op.Name] = l = LcilParser.TryGetOpCode(op.Name.Substring(0, op.Name.Length - 2), out var x) ? x : op;
            return l;
        }
    }

    private void Instruction(BodyContext cx, Instr ins)
    {
        var il = cx.IL;
        switch (ins.Kind)
        {
            case InstrKind.Label: il.MarkLabel(cx.Labels[(string)ins.Operand]); return;
            case InstrKind.Tail: il.Emit(OpCodes.Tailcall); return;
            case InstrKind.Line:
                if (cx.Doc != null) il.MarkSequencePoint(cx.Doc, ins.L1, ins.C1, ins.L2, ins.C2);
                return;
            case InstrKind.LdConst:
                il.Emit(OpCodes.Ldsfld, _constantsField);
                EmitInt(il, (int)ins.Operand);
                il.Emit(OpCodes.Ldelem_Ref);
                return;
            case InstrKind.Try:
                il.BeginExceptionBlock();
                Instrs(cx, ins.TryBody);
                foreach (var (type, body) in ins.Catches)
                {
                    il.BeginCatchBlock(T(type, ins.Pos));
                    Instrs(cx, body);
                }
                if (ins.Finally != null)
                {
                    il.BeginFinallyBlock();
                    Instrs(cx, ins.Finally);
                }
                il.EndExceptionBlock();
                return;
            case InstrKind.Clr: Clr(cx, ins); return;
        }

        if (cx.TakeLoads != null && cx.TakeLoads.Contains(ins) && LocalOp(ins) is (_, int ti))
        {
            EmitVar(il, OpCodes.Ldloca, ti);
            il.Emit(OpCodes.Call, s_take);
            return;
        }
        var op = ins.OpCode;
        switch (op.OperandType)
        {
            case OperandType.InlineNone: il.Emit(op); break;
            case OperandType.ShortInlineI:
            case OperandType.InlineI:
                if (op == OpCodes.Ldc_I4 || op == OpCodes.Ldc_I4_S) EmitInt(il, checked((int)(long)ins.Operand));
                else if (op.OperandType == OperandType.ShortInlineI) il.Emit(op, checked((byte)(long)ins.Operand));
                else il.Emit(op, checked((int)(long)ins.Operand));
                break;
            case OperandType.InlineI8: il.Emit(op, (long)ins.Operand); break;
            case OperandType.ShortInlineR: il.Emit(op, (float)(double)ins.Operand); break;
            case OperandType.InlineR: il.Emit(op, (double)ins.Operand); break;
            case OperandType.InlineString: il.Emit(op, (string)ins.Operand); break;
            case OperandType.ShortInlineVar:
            case OperandType.InlineVar: EmitVar(il, op, (int)ins.Operand); break;
            case OperandType.ShortInlineBrTarget:
            case OperandType.InlineBrTarget: il.Emit(LongForm(op), cx.Labels[(string)ins.Operand]); break;
            case OperandType.InlineSwitch: il.Emit(op, ((string[])ins.Operand).Select(l => cx.Labels[l]).ToArray()); break;
            case OperandType.InlineType:
            case OperandType.InlineTok: il.Emit(op, T((string)ins.Operand, ins.Pos)); break;
            case OperandType.InlineField:
                {
                    var fr = (FieldRef)ins.Operand;
                    if (_volatile.Contains((fr.Type, fr.Name)) && (op == OpCodes.Ldfld || op == OpCodes.Stfld || op == OpCodes.Ldsfld || op == OpCodes.Stsfld)) il.Emit(OpCodes.Volatile);
                    il.Emit(op, Field(fr, ins.Pos));
                    break;
                }
            case OperandType.InlineMethod:
                var mb = Method((MethodRef)ins.Operand, ins.Pos);
                if (mb is ConstructorInfo ci) il.Emit(op, ci); else il.Emit(op, (MethodInfo)mb);
                break;
            default: throw Err(ins.Pos, "unsupported instruction: " + op.Name);
        }
    }

    private static void EmitInt(ILGenerator il, int n)
    {
        switch (n)
        {
            case -1: il.Emit(OpCodes.Ldc_I4_M1); return;
            case 0: il.Emit(OpCodes.Ldc_I4_0); return;
            case 1: il.Emit(OpCodes.Ldc_I4_1); return;
            case 2: il.Emit(OpCodes.Ldc_I4_2); return;
            case 3: il.Emit(OpCodes.Ldc_I4_3); return;
            case 4: il.Emit(OpCodes.Ldc_I4_4); return;
            case 5: il.Emit(OpCodes.Ldc_I4_5); return;
            case 6: il.Emit(OpCodes.Ldc_I4_6); return;
            case 7: il.Emit(OpCodes.Ldc_I4_7); return;
            case 8: il.Emit(OpCodes.Ldc_I4_8); return;
        }
        if (n >= sbyte.MinValue && n <= sbyte.MaxValue) il.Emit(OpCodes.Ldc_I4_S, (sbyte)n);
        else il.Emit(OpCodes.Ldc_I4, n);
    }

    /// <summary>参数 / 局部变量指令：按下标选最短格式。</summary>
    private static void EmitVar(ILGenerator il, OpCode op, int i)
    {
        string name = op.Name.EndsWith(".s", StringComparison.Ordinal) ? op.Name.Substring(0, op.Name.Length - 2) : op.Name;
        if (i <= 3 && (name == "ldarg" || name == "ldloc" || name == "stloc"))
        {
            LcilParser.TryGetOpCode(name + "." + i, out var shortOp);
            il.Emit(shortOp);
            return;
        }
        LcilParser.TryGetOpCode(name, out var longOp);
        if (i <= 255 && LcilParser.TryGetOpCode(name + ".s", out var sOp)) il.Emit(sOp, (byte)i);
        else il.Emit(longOp, (short)i);
    }

    // ———————————————— 保存 ————————————————

    private static void WritePE(PersistedAssemblyBuilder ab, LcilAssembly a, string outPath, bool pdb)
    {
        MetadataBuilder metadata = ab.GenerateMetadata(out BlobBuilder ilStream, out BlobBuilder fieldData, out MetadataBuilder pdbBuilder);
        // 框架类型改引用契约程序集（cleat-067）：先加上契约的 AssemblyRef，序列化之后改 TypeRef 的解析范围（ContractRetargeting）
        var contracts = ContractRetargeting.AddContractReferences(metadata);
        // 确定性：程序集内容哈希 → MVID / 时间戳 / PDB ID（iron-003）
        Func<IEnumerable<Blob>, BlobContentId> idProvider = blobs =>
        {
            using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var blob in blobs) h.AppendData(blob.GetBytes());
            return BlobContentId.FromHash(h.GetHashAndReset());
        };
        DebugDirectoryBuilder debug = null;
        if (pdb)
        {
            var pdbBlob = new BlobBuilder();
            var portablePdb = new PortablePdbBuilder(pdbBuilder, metadata.GetRowCounts(), default, idProvider);
            BlobContentId pdbId = portablePdb.Serialize(pdbBlob);
            string pdbPath = Path.ChangeExtension(outPath, ".pdb");
            using (var fs = File.Create(pdbPath)) pdbBlob.WriteContentTo(fs);
            debug = new DebugDirectoryBuilder();
            debug.AddCodeViewEntry(Path.GetFileName(pdbPath), pdbId, portablePdb.FormatVersion);
        }
        // 嵌入资源（cleat-067）：资源段里每个资源是 4 字节长度加内容，清单资源行记它在段里的偏移
        BlobBuilder resources = null;
        foreach (var (name, path) in a.Resources)
        {
            resources ??= new BlobBuilder();
            // 相对路径相对于 Lcil 文件所在的目录（种子里的包程序集，cleat-091）
            string full = Path.IsPathRooted(path) || a.File == null ? path : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(a.File)) ?? ".", path);
            byte[] data = File.ReadAllBytes(full);
            metadata.AddManifestResource(ManifestResourceAttributes.Public, metadata.GetOrAddString(name), default, (uint)resources.Count);
            resources.WriteInt32(data.Length);
            resources.WriteBytes(data);
            resources.Align(8);
        }
        var pe = new ManagedPEBuilder(
          new PEHeaderBuilder(imageCharacteristics: Characteristics.Dll | Characteristics.ExecutableImage),
          new MetadataRootBuilder(metadata), ilStream, fieldData, managedResources: resources,
          debugDirectoryBuilder: debug, deterministicIdProvider: idProvider);
        var peBlob = new BlobBuilder();
        pe.Serialize(peBlob);
        var image = peBlob.ToArray();
        ContractRetargeting.Retarget(image, contracts);
        MakeDeterministic(image);
        File.WriteAllBytes(outPath, image);
    }

    /// <summary>
    /// GenerateMetadata 生成随机 MVID：清零 MVID 与 COFF 时间戳 → SHA256 → 回填（iron-003 验证）。
    /// 相同 Lcil 因此得到逐字节相同的 dll。
    /// </summary>
    private static void MakeDeterministic(byte[] image)
    {
        int mvid, ts;
        using (var pe = new PEReader(new MemoryStream(image)))
        {
            var md = pe.GetMetadataReader();
            int heap = md.GetHeapMetadataOffset(HeapIndex.Guid);
            int index = MetadataTokens.GetHeapOffset(md.GetModuleDefinition().Mvid);   // GUID 堆索引从 1 开始
            mvid = pe.PEHeaders.MetadataStartOffset + heap + (index - 1) * 16;
            ts = pe.PEHeaders.CoffHeaderStartOffset + 4;
        }
        Array.Clear(image, mvid, 16);
        Array.Clear(image, ts, 4);
        byte[] hash = SHA256.HashData(image);
        Array.Copy(hash, 0, image, mvid, 16);
        Array.Copy(hash, 16, image, ts, 4);
    }
}
