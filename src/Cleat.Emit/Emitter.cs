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
/// Lcil → 程序集（designs/05-cil-backend.md；规格 LCIL.md）。从 IronScheme（BSD）的 Emitter.cs 移植。不引用 Cleat.Core 项目：
/// 运行时类型按名字从已加载或 references 中的程序集解析；互操作规则调用 Core 的 TypeResolver / Reflector。
/// </summary>
public sealed partial class LcilEmitter
{
    public const string CoreAssemblyName = "Cleat.Core";
    /// <summary>Scheme 元层运行时（designs/15 §1）：(runtime scheme) 的互操作、常量表的 fasl 读回都在这里。</summary>
    public const string SchemeAssemblyName = "Cleat.Scheme";

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

    /// <summary>eval 的快速路径：直接发射编译器产出的 Lcil S 表达式（Scheme 或 Clojure 数据），不经文本（iron-033）。</summary>
    public static Assembly EmitInMemoryData(object lcil, string file, object[] constants) =>
      EmitInMemory(LcilParser.FromTree(DataToTree(lcil, file), file), constants);

    /// <summary>编译器产出的 Lcil 数据 → SNode 树：按顶层对象所属的程序集选 Scheme（Cons / Symbol）或 Clojure（ISeq / Symbol）的转换。</summary>
    private static SList DataToTree(object lcil, string file)
    {
        var asm = lcil?.GetType().Assembly;
        var tree = asm != null && asm.GetName().Name == SchemeAssemblyName ? SchemeData.Convert(lcil, asm) : ClojureData.Convert(lcil, LoadReference(CoreAssemblyName));
        if (!(tree is SList top) || top.Head != "lcil") throw new LcilException($"{file}: expected a (lcil 1 ...) form");
        return top;
    }

    public static Assembly EmitInMemory(LcilAssembly a, object[] liveConstants)
    {
        if (a.LiveConstants >= 0 && (liveConstants == null || liveConstants.Length != a.LiveConstants))
            throw new LcilException($"{a.File}: expected {a.LiveConstants} live constants");
        var ab = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(a.Name), AssemblyBuilderAccess.RunAndCollect);
        var e = new LcilEmitter(a, ab.DefineDynamicModule(a.Name), false) { _live = liveConstants, _chunked = true };
        e.Build();
        return ab;
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
        // Cleat.Core 是隐含引用（CleatModule 属性、Clojure 互操作、活常量都要它）；Cleat.Scheme 在用到时也是（Scheme 互操作、编码的常量表由它的 Fasl 读回）
        var implicitRefs = new List<string> { CoreAssemblyName };
        if (asm.Constants.Count > 0 || asm.Runtime == LcilRuntime.Scheme || asm.Types.Any(t => t.Methods.Any(m => m.Runtime == LcilRuntime.Scheme)))
            implicitRefs.Add(SchemeAssemblyName);
        foreach (var r in implicitRefs.Concat(asm.References).Distinct()) _references.Add(LoadReference(r));
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
        var scheme = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(x => x.GetName().Name == SchemeAssemblyName);
        if (scheme?.GetType("Cleat.Scheme.Builtins")?.GetProperty("IncludePaths")?.GetValue(null) is IEnumerable<string> includes)
            foreach (var p in includes) yield return p;
        yield return AppContext.BaseDirectory;
    }

    // ———————————————— 类型解析 ————————————————

    /// <summary>Lcil 类型引用（§2.4）：本程序集的类型 → 全名查找；后缀 [] 与 &amp;。</summary>
    private Type T(string name, SNode pos)
    {
        if (name.EndsWith("[]", StringComparison.Ordinal)) return T(name.Substring(0, name.Length - 2), pos).MakeArrayType();
        if (name.EndsWith("&", StringComparison.Ordinal)) return T(name.Substring(0, name.Length - 1), pos).MakeByRefType();
        if (_types.TryGetValue(name, out var tb)) return tb;
        if (s_staticTypes.TryGetValue(name, out var cached)) return cached;
        var t = Type.GetType(name);
        if (t == null && QualifiedSplit(name) is int comma)
        {
            // "类型名, 程序集名"（iron-034e：直接引用 boot 镜像或已加载的库产物中的类）。Type.GetType 经运行时的解析
            // （ModuleLoader 登记的程序集）找不到时，按简单名在已加载的程序集中找
            string asmName = name.Substring(comma + 1).Trim(), typeName = name.Substring(0, comma).Trim();
            foreach (var a in _references.Concat(AppDomain.CurrentDomain.GetAssemblies()))
            {
                if (a.IsDynamic || a.GetName().Name != asmName) continue;
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
        if (t == null) throw Err(pos, "type not found: " + name);
        // 只缓存非动态程序集中的类型：已加载的程序集不会卸载，结果不变；每次 eval 都新增动态程序集，不缓存时查找越来越慢（iron-033）
        // 库产物（.fasl / 库 dll）可能被重新加载出同名的新副本，运行时按最新的解析：程序集限定名只缓存 boot 的
        if (!t.Assembly.IsDynamic && (QualifiedSplit(name) == null || name.EndsWith(", cleat.boot", StringComparison.Ordinal)))
            s_staticTypes[name] = t;
        return t;
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

    private void DefineMethod(LcilType t, TypeBuilder tb, LcilMethod m)
    {
        var ps = Ts(m.Params, m.Pos);
        MethodBase b;
        if (m.IsCctor) b = tb.DefineTypeInitializer();
        else if (m.IsCtor)
        {
            var ca = (m.Attrs.Contains("private") ? MethodAttributes.Private : MethodAttributes.Public) | MethodAttributes.HideBySig;
            b = tb.DefineConstructor(ca, CallingConventions.Standard, ps);
        }
        else
        {
            var ma = (m.Attrs.Contains("private") ? MethodAttributes.Private : MethodAttributes.Public) | MethodAttributes.HideBySig;
            if (m.IsStatic) ma |= MethodAttributes.Static;
            if (m.Attrs.Contains("virtual")) ma |= MethodAttributes.Virtual | MethodAttributes.NewSlot;
            if (m.Attrs.Contains("override")) ma |= MethodAttributes.Virtual;
            if (m.Attrs.Contains("abstract")) ma |= MethodAttributes.Virtual | MethodAttributes.Abstract | MethodAttributes.NewSlot;
            if (m.ImplementsType != null) ma |= MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.NewSlot;
            var mb = tb.DefineMethod(m.Name, ma, T(m.Ret, m.Pos), ps);
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
        if (it is TypeBuilder && _methods.TryGetValue(it.Name, out var own))
            return own.FirstOrDefault(x => x.M.Name == name && x.P.SequenceEqual(ps)).B as MethodInfo ?? throw Err(pos, $"interface method not found: {it.Name}::{name}");
        foreach (var i in new[] { it }.Concat(it.GetInterfaces()))
        {
            var m = i.GetMethod(name, BindingFlags.Public | BindingFlags.Instance, null, ps, null);
            if (m != null) return m;
        }
        throw Err(pos, $"interface method not found: {it.FullName}::{name}");
    }

    // ———————————————— 成员引用 ————————————————

    private MethodBase Method(MethodRef r, SNode pos)
    {
        var ps = Ts(r.Params, pos);
        MethodBase found = null;
        if (_methods.TryGetValue(r.Type, out var own))
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
    }

    private void EmitBody(LcilType t, LcilMethod m, MethodBase b)
    {
        var il = b is ConstructorBuilder cb ? cb.GetILGenerator() : ((MethodBuilder)b).GetILGenerator();
        var cx = new BodyContext { IL = il, M = m, Namespaces = m.Namespaces.ToArray(), Runtime = m.Runtime ?? _asm.Runtime };
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
        var pe = new ManagedPEBuilder(
          new PEHeaderBuilder(imageCharacteristics: Characteristics.Dll | Characteristics.ExecutableImage),
          new MetadataRootBuilder(metadata), ilStream, fieldData,
          debugDirectoryBuilder: debug, deterministicIdProvider: idProvider);
        var peBlob = new BlobBuilder();
        pe.Serialize(peBlob);
        var image = peBlob.ToArray();
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
