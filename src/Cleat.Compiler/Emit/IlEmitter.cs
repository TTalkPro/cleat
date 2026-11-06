using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.SymbolStore;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using Cleat.Compiler.Lower;
using Cleat.Core.Runtime;

namespace Cleat.Compiler.Emit;

/// <summary>
/// L5 → CIL（designs/30 §2 D4）：直接驱动 ILGenerator。两阶段：先定义全部类型（基类先）、字段与方法签名，再逐个方法生成方法体，最后基类先 CreateType。
/// 内存模式（eval）缺省发射成 PE 字节装进不可回收的装载上下文（分层编译、动态 PGO，cleat-114）；CLEAT_NO_OPT=tiered 时用 RunAndCollect 的动态程序集
/// （每 1000 个类型一个，CreateType 的开销随同模块类型数增长）。持久化见 <see cref="Save"/>（cleat-176）。
/// </summary>
public sealed partial class IlEmitter
{
    private static readonly bool s_tieredEval = !Cleat.Core.Runtime.OptFlags.Off("tiered");
    private const int ChunkSize = 1000;
    public const string ConstantsTypeName = "$Constants";

    private readonly UnitDecl _unit;
    private readonly ModuleBuilder _module;
    private readonly bool _debug, _tiered, _chunked;
    private readonly object[] _live;
    private readonly Dictionary<ClassDecl, TypeBuilder> _types = new Dictionary<ClassDecl, TypeBuilder>(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, ClassDecl> _byName = new Dictionary<string, ClassDecl>(StringComparer.Ordinal);
    private readonly Dictionary<MethodDecl, MethodBase> _methods = new Dictionary<MethodDecl, MethodBase>(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<MethodDecl, Type[]> _params = new Dictionary<MethodDecl, Type[]>(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<FieldDecl, FieldBuilder> _fields = new Dictionary<FieldDecl, FieldBuilder>(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, ISymbolDocumentWriter> _documents = new Dictionary<string, ISymbolDocumentWriter>(StringComparer.Ordinal);
    private TypeBuilder _constantsType;
    private FieldBuilder _constantsField;
    private ModuleBuilder _chunk;
    private int _chunkCount, _chunkFill;

    private IlEmitter(UnitDecl unit, ModuleBuilder module, bool debug, bool tiered, bool chunked, object[] live)
    {
        _unit = unit;
        _module = module;
        _debug = debug;
        _tiered = tiered;
        _chunked = chunked;
        _live = live;
        foreach (var c in unit.AllClasses) _byName[c.Name] = c;
    }

    // ———————————————— 入口 ————————————————

    /// <summary>发射内存模式的编译单元（常量对象在 unit.Constants 里），返回装载好的程序集。</summary>
    public static Assembly EmitInMemory(UnitDecl unit)
    {
        if (!unit.Live) throw new InvalidOperationException("EmitInMemory: unit is not live");
        var live = unit.Constants.ToArray();
        if (s_tieredEval) return EmitLoaded(unit, live);
        var ab = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(unit.Name), AssemblyBuilderAccess.RunAndCollect);
        var e = new IlEmitter(unit, ab.DefineDynamicModule(unit.Name), false, false, true, live);
        e.Build();
        return ab;
    }

    private static readonly ConcurrentDictionary<string, Assembly> s_loaded = new ConcurrentDictionary<string, Assembly>();

    /// <summary>
    /// eval 程序集的装载上下文（不可回收，代码照常分层编译）：依赖先取进程里已经装载的同名程序集（正在用的镜像与包），eval 程序集之间按登记表。
    /// </summary>
    private sealed class EvalLoadContext : AssemblyLoadContext
    {
        public EvalLoadContext() : base("cleat-cs-eval", isCollectible: false) { }

        protected override Assembly Load(AssemblyName name)
        {
            if (name.Name == null) return null;
            if (s_loaded.TryGetValue(name.Name, out var e)) return e;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                if (!asm.IsDynamic && asm.GetName().Name == name.Name) return asm;
            return null;
        }
    }

    private static readonly EvalLoadContext s_evalContext = new EvalLoadContext();

    private static Assembly EmitLoaded(UnitDecl unit, object[] live)
    {
        var ab = new PersistedAssemblyBuilder(new AssemblyName(unit.Name), typeof(object).Assembly);
        var e = new IlEmitter(unit, ab.DefineDynamicModule(unit.Name), true, true, false, live);
        e.Build();
        var (pe, pdb) = Serialize(ab, unit.Name);
        var loaded = s_evalContext.LoadFromStream(new MemoryStream(pe), new MemoryStream(pdb));
        s_loaded[unit.Name] = loaded;
        return loaded;
    }

    private static (byte[] Pe, byte[] Pdb) Serialize(PersistedAssemblyBuilder ab, string name)
    {
        MetadataBuilder metadata = ab.GenerateMetadata(out BlobBuilder ilStream, out BlobBuilder fieldData, out MetadataBuilder pdbBuilder);
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
        debug.AddCodeViewEntry(name + ".pdb", pdbId, portablePdb.FormatVersion);
        var pe = new ManagedPEBuilder(new PEHeaderBuilder(imageCharacteristics: Characteristics.Dll | Characteristics.ExecutableImage),
                                      new MetadataRootBuilder(metadata), ilStream, fieldData,
                                      debugDirectoryBuilder: debug, deterministicIdProvider: idProvider);
        var blob = new BlobBuilder();
        pe.Serialize(blob);
        return (blob.ToArray(), pdbBlob.ToArray());
    }

    // ———————————————— 构建 ————————————————

    private ModuleBuilder ModuleFor(ClassDecl c)
    {
        int total = _unit.Classes.Count + 1;
        if (!_chunked || ReferenceEquals(c, _unit.Module) || total <= ChunkSize) return _module;
        if (_chunk == null || _chunkFill == ChunkSize)
        {
            string name = _unit.Name + "$" + ++_chunkCount;
            _chunk = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(name), AssemblyBuilderAccess.RunAndCollect).DefineDynamicModule(name);
            _chunkFill = 0;
        }
        _chunkFill++;
        return _chunk;
    }

    private void Build()
    {
        var classes = _unit.AllClasses.ToList();
        // 1. 类型：基类在本单元中的先定义
        var defining = new HashSet<ClassDecl>(ReferenceEqualityComparer.Instance);
        void Define(ClassDecl c)
        {
            if (_types.ContainsKey(c)) return;
            if (!defining.Add(c)) throw new InvalidOperationException($"emit: cyclic base types at {c.Name}");
            if (c.Base is ClassDecl bc) Define(bc);
            else if (c.Base is string bs && _byName.TryGetValue(bs, out var bn)) Define(bn);
            DefineType(c);
        }
        foreach (var c in classes) Define(c);
        if (_unit.Constants.Count > 0) DefineConstants();
        if (_unit.Entry != null)
            ((AssemblyBuilder)_module.Assembly).SetCustomAttribute(StringAttribute("Cleat.Core.CleatModuleAttribute", new[] { _unit.Module.Name, _unit.Entry.Name }));
        foreach (var (type, args) in _unit.Attributes) ((AssemblyBuilder)_module.Assembly).SetCustomAttribute(StringAttribute(type, args));
        // 2. 字段与方法签名
        foreach (var c in classes)
        {
            var tb = _types[c];
            foreach (var f in c.Fields) DefineField(tb, f);
            foreach (var m in c.Methods) DefineMethod(c, tb, m);
            if (_unit.LazyStart && !c.Interface && !c.Methods.Any(m => m.Kind == MethodKind.Cctor))
            {
                var il = tb.DefineTypeInitializer().GetILGenerator();
                il.Emit(OpCodes.Call, Rt.EnsureStarted);
                il.Emit(OpCodes.Ret);
            }
        }
        // 3. 方法体
        foreach (var c in classes)
            foreach (var m in c.Methods)
                if (!m.AbstractMethod) EmitBody(c, m);
        // 4. 完成类型（基类先）
        var done = new HashSet<ClassDecl>(ReferenceEqualityComparer.Instance);
        void Create(ClassDecl c)
        {
            if (!done.Add(c)) return;
            if (c.Base is ClassDecl bc) Create(bc);
            else if (c.Base is string bs && _byName.TryGetValue(bs, out var bn)) Create(bn);
            foreach (var i in c.Interfaces)
                if (i is ClassDecl ic) Create(ic);
            _types[c].CreateType();
        }
        _constantsType?.CreateType();
        foreach (var c in classes) Create(c);
    }

    private CustomAttributeBuilder StringAttribute(string type, string[] args)
    {
        var at = T(type);
        var ctor = at.GetConstructors().FirstOrDefault(c => c.GetParameters().Length == args.Length && c.GetParameters().All(p => p.ParameterType == typeof(string)))
                   ?? throw new InvalidOperationException($"emit: attribute {type} has no constructor taking {args.Length} string(s)");
        return new CustomAttributeBuilder(ctor, args.Cast<object>().ToArray());
    }

    private void DefineType(ClassDecl c)
    {
        var ta = TypeAttributes.Public | TypeAttributes.BeforeFieldInit;
        if (c.Sealed) ta |= TypeAttributes.Sealed;
        if (c.Abstract) ta |= TypeAttributes.Abstract;
        if (c.Interface) ta = TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract;
        var parent = c.Interface ? null : T(c.Base);
        var tb = ModuleFor(c).DefineType(c.Name, ta, parent, c.Interfaces.Select(T).ToArray());
        foreach (var (type, args) in c.Attributes) tb.SetCustomAttribute(StringAttribute(type, args));
        _types[c] = tb;
    }

    private void DefineField(TypeBuilder tb, FieldDecl f)
    {
        var fa = f.Private ? FieldAttributes.Private : FieldAttributes.Public;
        if (f.Static) fa |= FieldAttributes.Static;
        if (f.ReadOnly) fa |= FieldAttributes.InitOnly;
        _fields[f] = f.Volatile
            ? tb.DefineField(f.Name, T(f.Type), new[] { typeof(IsVolatile) }, null, fa)
            : tb.DefineField(f.Name, T(f.Type), fa);
    }

    private void DefineMethod(ClassDecl c, TypeBuilder tb, MethodDecl m)
    {
        var ps = m.Params.Select(T).ToArray();
        _params[m] = ps;
        var access = m.Private ? MethodAttributes.Private : m.Protected ? MethodAttributes.Family : MethodAttributes.Public;
        MethodBase b;
        if (m.Kind == MethodKind.Cctor) b = tb.DefineTypeInitializer();
        else if (m.Kind == MethodKind.Ctor) b = tb.DefineConstructor(access | MethodAttributes.HideBySig, CallingConventions.Standard, ps);
        else
        {
            var ma = access | MethodAttributes.HideBySig;
            if (m.Static) ma |= MethodAttributes.Static;
            if (m.Virtual) ma |= MethodAttributes.Virtual | MethodAttributes.NewSlot;
            if (m.Override) ma |= MethodAttributes.Virtual;
            if (m.AbstractMethod) ma |= MethodAttributes.Virtual | MethodAttributes.Abstract | MethodAttributes.NewSlot;
            if (m.ImplementsType != null) ma |= MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.NewSlot;
            var mb = tb.DefineMethod(m.Name, ma, T(m.Ret), ps);
            // 分层编译（eval）：模块入口类型的方法与不带 tiered 的方法直接按优化编译（tier-0 把临时变量报告为整个方法内活着，抓住惰性序列的头）
            bool optimize = _tiered ? (ReferenceEquals(c, _unit.Module) || !m.Tiered) : m.Optimize && !m.Tiered;
            var impl = (optimize ? MethodImplAttributes.AggressiveOptimization : 0) | (m.Async ? (MethodImplAttributes)0x2000 : 0);
            if (impl != 0) mb.SetImplementationFlags(impl);
            if (m.ImplementsType != null) tb.DefineMethodOverride(mb, FindInterfaceMethod(T(m.ImplementsType), m.ImplementsName, ps));
            b = mb;
        }
        _methods[m] = b;
    }

    private MethodInfo FindInterfaceMethod(Type it, string name, Type[] ps)
    {
        if (it is TypeBuilder)
        {
            var decl = _types.First(kv => ReferenceEquals(kv.Value, it)).Key;
            foreach (var m in decl.Methods)
                if (m.Name == name && _params.TryGetValue(m, out var mp) && mp.SequenceEqual(ps)) return (MethodInfo)_methods[m];
            foreach (var parent in decl.Interfaces)
            {
                try { return FindInterfaceMethod(T(parent), name, ps); }
                catch (InvalidOperationException) { }
            }
            throw new InvalidOperationException($"emit: interface method not found: {decl.Name}::{name}");
        }
        foreach (var i in new[] { it }.Concat(it.GetInterfaces()))
        {
            var m = i.GetMethod(name, BindingFlags.Public | BindingFlags.Instance, null, ps, null);
            if (m != null) return m;
        }
        throw new InvalidOperationException($"emit: interface method not found: {it.FullName}::{name}");
    }

    private void DefineConstants()
    {
        _constantsType = _module.DefineType(ConstantsTypeName, TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.Class);
        _constantsField = _constantsType.DefineField("c", typeof(object[]), FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.InitOnly);
        var il = _constantsType.DefineTypeInitializer().GetILGenerator();
        if (_unit.LazyStart) il.Emit(OpCodes.Call, Rt.EnsureStarted);
        if (_live != null)
        {
            // 内存模式的活常量：登记到 ConstantPool，类型初始化器按编号取回
            il.Emit(OpCodes.Ldc_I4, ConstantPool.Register(_live));
            il.Emit(OpCodes.Call, typeof(ConstantPool).GetMethod(nameof(ConstantPool.Take)));
        }
        else EmitPackedConstants(il);
        il.Emit(OpCodes.Stsfld, _constantsField);
        il.Emit(OpCodes.Ret);
    }

    // ———————————————— 引用的决议 ————————————————

    private static readonly ConcurrentDictionary<string, Type> s_staticTypes = new ConcurrentDictionary<string, Type>(StringComparer.Ordinal);

    /// <summary>TypeRef → Type：Type 原样；ClassDecl → 本单元的 TypeBuilder；字符串 → 本单元同名类型，否则在已装载的程序集里找（后缀 [] / &amp;）。</summary>
    private Type T(object r)
    {
        switch (r)
        {
            case Type t: return t;
            case ClassDecl c: return _types.TryGetValue(c, out var tb) ? tb : throw new InvalidOperationException($"emit: class {c.Name} is not in this unit");
            case string s: return TypeNamed(s);
            default: throw new InvalidOperationException($"emit: not a type reference: {r ?? "null"}");
        }
    }

    private Type TypeNamed(string name)
    {
        if (name.EndsWith("[]", StringComparison.Ordinal)) return TypeNamed(name.Substring(0, name.Length - 2)).MakeArrayType();
        if (name.EndsWith("&", StringComparison.Ordinal)) return TypeNamed(name.Substring(0, name.Length - 1)).MakeByRefType();
        if (_byName.TryGetValue(name, out var own)) return _types[own];
        int comma = QualifiedSplit(name);
        if (comma >= 0 && _byName.TryGetValue(name.Substring(0, comma).Trim(), out var own2)) return _types[own2];
        return ResolveExternal(name) ?? throw new InvalidOperationException($"emit: type not found: {name}");
    }

    /// <summary>本单元之外的类型名（与原发射器 Cleat.Emit 的查找顺序相同：Type.GetType、"类型, 程序集"、已装载的程序集、TypeResolver）。</summary>
    internal static Type ResolveExternal(string name)
    {
        if (s_staticTypes.TryGetValue(name, out var cached)) return cached;
        var t = Type.GetType(name, false);
        int comma = QualifiedSplit(name);
        if (t == null && comma >= 0)
        {
            string asmName = name.Substring(comma + 1).Trim(), typeName = name.Substring(0, comma).Trim();
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies().Reverse())
            {
                if (a.GetName().Name != asmName) continue;
                var u = a.GetType(typeName);
                if (u != null && (u.IsPublic || u.IsNestedPublic)) { t = u; break; }
            }
        }
        if (t == null)
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies().Reverse())
            {
                if (a.IsDynamic) continue;
                var u = a.GetType(name);
                if (u != null && (u.IsPublic || u.IsNestedPublic)) { t = u; break; }
            }
        t ??= TypeResolver.Resolve(name, Array.Empty<string>()) as Type;
        if (t != null && !t.Assembly.IsDynamic && comma < 0 && !s_loaded.Values.Contains(t.Assembly)) s_staticTypes[name] = t;
        return t;
    }

    private static int QualifiedSplit(string name)
    {
        int depth = 0;
        for (int i = 0; i < name.Length; i++)
            switch (name[i])
            {
                case '[': depth++; break;
                case ']': depth--; break;
                case ',' when depth == 0: return i;
            }
        return -1;
    }

    private MethodBase M(object r)
    {
        switch (r)
        {
            case MethodBase mb: return mb;
            case MethodDecl md: return _methods.TryGetValue(md, out var b) ? b : throw new InvalidOperationException($"emit: method {md} is not in this unit");
            case MethodRef mr: return Resolve(mr);
            default: throw new InvalidOperationException($"emit: not a method reference: {r ?? "null"}");
        }
    }

    /// <summary>引用的所属类型若是本单元的类（ClassDecl、同名或"名字, 程序集"字符串、本单元的 TypeBuilder），返回它；否则 null。</summary>
    private ClassDecl OwnClass(object owner)
    {
        switch (owner)
        {
            case ClassDecl c: return c;
            case string s:
                if (_byName.TryGetValue(s, out var c1)) return c1;
                int q = QualifiedSplit(s);
                if (q >= 0 && _byName.TryGetValue(s.Substring(0, q).Trim(), out var c2)) return c2;
                if (s.EndsWith("[]", StringComparison.Ordinal) || s.EndsWith("&", StringComparison.Ordinal)) return null;
                return TypeNamed(s) is TypeBuilder tb0 ? OwnClass(tb0) : null;
            case TypeBuilder tb:
                foreach (var kv in _types)
                    if (ReferenceEquals(kv.Value, tb)) return kv.Key;
                return null;
            default: return null;
        }
    }

    private MethodBase Resolve(MethodRef r)
    {
        var ps = r.Params.Select(T).ToArray();
        ClassDecl own = OwnClass(r.Owner);
        if (own != null)
        {
            foreach (var m in own.Methods)
                if ((r.Ctor ? m.Kind == MethodKind.Ctor : m.Name == r.Name) && _params[m].SequenceEqual(ps)) return _methods[m];
            throw new InvalidOperationException($"emit: method not found: {own.Name}::{(r.Ctor ? ".ctor" : r.Name)}");
        }
        var owner = T(r.Owner);
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        MethodBase found = r.Ctor
            ? owner.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, ps, null)   // 不带 Static：否则无参时与 .cctor 歧义
            : owner.GetMethod(r.Name, all, null, ps, null);
        return found ?? throw new InvalidOperationException($"emit: method not found: {owner.FullName}::{(r.Ctor ? ".ctor" : r.Name)}({string.Join(", ", ps.Select(p => p.Name))})");
    }

    private FieldInfo F(object r, out bool isVolatile)
    {
        isVolatile = false;
        switch (r)
        {
            case FieldInfo fi:
                return fi;   // 外部字段的 volatile. 由 codegen 显式发（静态决议的 "volatile-field"），这里不再补
            case FieldDecl fd:
                isVolatile = fd.Volatile;
                return _fields.TryGetValue(fd, out var fb) ? fb : throw new InvalidOperationException($"emit: field {fd.Owner.Name}::{fd.Name} is not in this unit");
            case FieldRef fr:
                {
                    ClassDecl own = OwnClass(fr.Owner);
                    if (own != null)
                    {
                        var fd2 = own.Field(fr.Name);
                        isVolatile = fd2.Volatile;
                        return _fields[fd2];
                    }
                    var owner = T(fr.Owner);
                    var f = owner.GetField(fr.Name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                            ?? throw new InvalidOperationException($"emit: field not found: {owner.FullName}::{fr.Name}");
                    return f;
                }
            default: throw new InvalidOperationException($"emit: not a field reference: {r ?? "null"}");
        }
    }

    // ———————————————— 方法体 ————————————————

    private sealed class BodyCx
    {
        public ILGenerator IL;
        public MethodDecl M;
        public ISymbolDocumentWriter Doc;
        public readonly Dictionary<Lbl, Label> Labels = new Dictionary<Lbl, Label>(ReferenceEqualityComparer.Instance);

        /// <summary>改成 ldloca + Locals.Take 的 ldloc（分层编译的方法，<see cref="TakeLoadsFor"/>）；null 表示不改。</summary>
        public HashSet<Il> TakeLoads;
    }

    /// <summary>局部变量指令：('l' 读 / 's' 写 / 'a' 取地址, 下标)，不是时 null。</summary>
    private static (char Kind, int Index)? LocalOp(Ins ins)
    {
        if (ins is not Il il) return null;
        var op = il.Op;
        if (op == OpCodes.Ldloca || op == OpCodes.Ldloca_S) return ('a', Convert.ToInt32(il.Arg));
        if (op == OpCodes.Ldloc || op == OpCodes.Ldloc_S) return ('l', Convert.ToInt32(il.Arg));
        if (op == OpCodes.Stloc || op == OpCodes.Stloc_S) return ('s', Convert.ToInt32(il.Arg));
        if (op == OpCodes.Ldloc_0) return ('l', 0);
        if (op == OpCodes.Ldloc_1) return ('l', 1);
        if (op == OpCodes.Ldloc_2) return ('l', 2);
        if (op == OpCodes.Ldloc_3) return ('l', 3);
        if (op == OpCodes.Stloc_0) return ('s', 0);
        if (op == OpCodes.Stloc_1) return ('s', 1);
        if (op == OpCodes.Stloc_2) return ('s', 2);
        if (op == OpCodes.Stloc_3) return ('s', 3);
        return null;
    }

    /// <summary>
    /// cleat-114（Cleat.Emit TakeLoadsFor 的移植）：分层编译的方法先以 tier-0 运行，tier-0 把所有局部变量报告为整个方法内活着——codegen 只写一次、
    /// 读一次的 object 临时变量（内联展开、实参临时变量……）在读过之后仍抓着值。顶层指令列表里只写一次、读一次、不取地址、读在写之后且中间没有标签与 try
    /// 的 object 局部变量，读改成取出并清空（优化代码里 Take 内联成两条指令）。
    /// </summary>
    private static HashSet<Il> TakeLoadsFor(MethodDecl m)
    {
        var stores = new Dictionary<int, int>();
        var loads = new Dictionary<int, int>();
        var addr = new HashSet<int>();
        void Count(List<Ins> list)
        {
            foreach (var ins in list)
            {
                if (ins is TryIns t)
                {
                    Count(t.Body);
                    foreach (var (_, code) in t.Catches) Count(code);
                    if (t.Finally != null) Count(t.Finally);
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
        static bool IsObject(object l) => l is Type t ? t == typeof(object) : l as string == "System.Object";
        var result = new HashSet<Il>(ReferenceEqualityComparer.Instance);
        var storeAt = new Dictionary<int, int>();
        for (int p = 0; p < m.Body.Count; p++)
        {
            var ins = m.Body[p];
            if (ins is LabelIns or TryIns)
            {
                storeAt.Clear();   // 标签 / try 之后不再是直线
                continue;
            }
            if (LocalOp(ins) is not (char k, int i)) continue;
            if (i >= m.Locals.Count || !IsObject(m.Locals[i]) || addr.Contains(i) || stores.GetValueOrDefault(i) != 1 || loads.GetValueOrDefault(i) != 1) continue;
            if (k == 's') storeAt[i] = p;
            else if (k == 'l' && storeAt.ContainsKey(i)) result.Add((Il)ins);
        }
        return result;
    }

    private void EmitBody(ClassDecl c, MethodDecl m)
    {
        var b = _methods[m];
        var il = b is ConstructorBuilder cb ? cb.GetILGenerator() : ((MethodBuilder)b).GetILGenerator();
        var cx = new BodyCx { IL = il, M = m };
        if (_unit.LazyStart && m.Kind == MethodKind.Cctor) il.Emit(OpCodes.Call, Rt.EnsureStarted);
        foreach (var l in m.Locals) il.DeclareLocal(T(l));
        if (_debug && m.Source != null)
        {
            if (!_documents.TryGetValue(m.Source, out var doc)) _documents[m.Source] = doc = _module.DefineDocument(m.Source);
            cx.Doc = doc;
        }
        void DefineLabels(List<Ins> list)
        {
            foreach (var ins in list)
            {
                if (ins is LabelIns li) cx.Labels[li.L] = il.DefineLabel();
                else if (ins is TryIns t)
                {
                    DefineLabels(t.Body);
                    foreach (var (_, code) in t.Catches) DefineLabels(code);
                    if (t.Finally != null) DefineLabels(t.Finally);
                }
            }
        }
        DefineLabels(m.Body);
        // 分层编译：eval 单元里模块入口类型以外的方法、带 Tiered 的方法（镜像与包的分层函数类，cleat-117）
        if ((_tiered && !ReferenceEquals(c, _unit.Module)) || m.Tiered) cx.TakeLoads = TakeLoadsFor(m);
        try
        {
            foreach (var ins in m.Body) Instruction(cx, ins);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            throw new InvalidOperationException($"emit: {c.Name}::{m.Name}: {e.Message}", e);
        }
    }

    private Label L(BodyCx cx, object l) =>
        l is Lbl lbl && cx.Labels.TryGetValue(lbl, out var x) ? x : throw new InvalidOperationException($"emit: undefined label {l}");

    private void Instruction(BodyCx cx, Ins ins)
    {
        var il = cx.IL;
        switch (ins)
        {
            case LabelIns li: il.MarkLabel(cx.Labels[li.L]); return;
            case Line ln:
                if (cx.Doc != null) il.MarkSequencePoint(cx.Doc, ln.L1, ln.C1, ln.L2, ln.C2);
                return;
            case LdConst lc:
                il.Emit(OpCodes.Ldsfld, _constantsField ?? throw new InvalidOperationException("emit: ldconst without constants"));
                EmitInt(il, lc.Index);
                il.Emit(OpCodes.Ldelem_Ref);
                return;
            case TryIns t:
                il.BeginExceptionBlock();
                foreach (var x in t.Body) Instruction(cx, x);
                foreach (var (type, code) in t.Catches)
                {
                    il.BeginCatchBlock(T(type));
                    foreach (var x in code) Instruction(cx, x);
                }
                if (t.Finally != null)
                {
                    il.BeginFinallyBlock();
                    foreach (var x in t.Finally) Instruction(cx, x);
                }
                il.EndExceptionBlock();
                return;
        }
        var i = (Il)ins;
        if (cx.TakeLoads != null && cx.TakeLoads.Contains(i) && LocalOp(i) is (_, int ti))
        {
            EmitVar(il, OpCodes.Ldloca, ti);
            il.Emit(OpCodes.Call, Rt.LocalsTake);
            return;
        }
        var op = i.Op;
        switch (op.OperandType)
        {
            case OperandType.InlineNone: il.Emit(op); break;
            case OperandType.ShortInlineI:
            case OperandType.InlineI:
                if (op == OpCodes.Ldc_I4 || op == OpCodes.Ldc_I4_S) EmitInt(il, Convert.ToInt32(i.Arg));
                else if (op.OperandType == OperandType.ShortInlineI) il.Emit(op, Convert.ToByte(i.Arg));
                else il.Emit(op, Convert.ToInt32(i.Arg));
                break;
            case OperandType.InlineI8: il.Emit(op, Convert.ToInt64(i.Arg)); break;
            case OperandType.ShortInlineR: il.Emit(op, Convert.ToSingle(i.Arg)); break;
            case OperandType.InlineR: il.Emit(op, Convert.ToDouble(i.Arg)); break;
            case OperandType.InlineString: il.Emit(op, (string)i.Arg); break;
            case OperandType.ShortInlineVar:
            case OperandType.InlineVar: EmitVar(il, op, Convert.ToInt32(i.Arg)); break;
            case OperandType.ShortInlineBrTarget:
            case OperandType.InlineBrTarget: il.Emit(LongForm(op), L(cx, i.Arg)); break;
            case OperandType.InlineSwitch: il.Emit(op, ((Lbl[])i.Arg).Select(l => L(cx, l)).ToArray()); break;
            case OperandType.InlineType:
                il.Emit(op, T(i.Arg));
                break;
            case OperandType.InlineTok:
                if (i.Arg is FieldInfo or FieldDecl or FieldRef) il.Emit(op, F(i.Arg, out _));
                else il.Emit(op, T(i.Arg));
                break;
            case OperandType.InlineField:
                {
                    var f = F(i.Arg, out bool vol);
                    if (vol && (op == OpCodes.Ldfld || op == OpCodes.Stfld || op == OpCodes.Ldsfld || op == OpCodes.Stsfld)) il.Emit(OpCodes.Volatile);
                    il.Emit(op, f);
                    break;
                }
            case OperandType.InlineMethod:
                var mb = M(i.Arg);
                if (mb is ConstructorInfo ci) il.Emit(op, ci);
                else il.Emit(op, (MethodInfo)mb);
                break;
            default: throw new InvalidOperationException("emit: unsupported instruction " + op.Name);
        }
    }

    private static OpCode LongForm(OpCode op)
    {
        if (op.OperandType != OperandType.ShortInlineBrTarget) return op;
        if (op == OpCodes.Br_S) return OpCodes.Br;
        if (op == OpCodes.Brfalse_S) return OpCodes.Brfalse;
        if (op == OpCodes.Brtrue_S) return OpCodes.Brtrue;
        if (op == OpCodes.Beq_S) return OpCodes.Beq;
        if (op == OpCodes.Bge_S) return OpCodes.Bge;
        if (op == OpCodes.Bgt_S) return OpCodes.Bgt;
        if (op == OpCodes.Ble_S) return OpCodes.Ble;
        if (op == OpCodes.Blt_S) return OpCodes.Blt;
        if (op == OpCodes.Bne_Un_S) return OpCodes.Bne_Un;
        if (op == OpCodes.Leave_S) return OpCodes.Leave;
        return op;
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
        if (op == OpCodes.Ldarg || op == OpCodes.Ldarg_S)
        {
            switch (i)
            {
                case 0: il.Emit(OpCodes.Ldarg_0); return;
                case 1: il.Emit(OpCodes.Ldarg_1); return;
                case 2: il.Emit(OpCodes.Ldarg_2); return;
                case 3: il.Emit(OpCodes.Ldarg_3); return;
            }
            if (i <= 255) il.Emit(OpCodes.Ldarg_S, (byte)i); else il.Emit(OpCodes.Ldarg, (short)i);
            return;
        }
        if (op == OpCodes.Ldloc || op == OpCodes.Ldloc_S)
        {
            switch (i)
            {
                case 0: il.Emit(OpCodes.Ldloc_0); return;
                case 1: il.Emit(OpCodes.Ldloc_1); return;
                case 2: il.Emit(OpCodes.Ldloc_2); return;
                case 3: il.Emit(OpCodes.Ldloc_3); return;
            }
            if (i <= 255) il.Emit(OpCodes.Ldloc_S, (byte)i); else il.Emit(OpCodes.Ldloc, (short)i);
            return;
        }
        if (op == OpCodes.Stloc || op == OpCodes.Stloc_S)
        {
            switch (i)
            {
                case 0: il.Emit(OpCodes.Stloc_0); return;
                case 1: il.Emit(OpCodes.Stloc_1); return;
                case 2: il.Emit(OpCodes.Stloc_2); return;
                case 3: il.Emit(OpCodes.Stloc_3); return;
            }
            if (i <= 255) il.Emit(OpCodes.Stloc_S, (byte)i); else il.Emit(OpCodes.Stloc, (short)i);
            return;
        }
        if (op == OpCodes.Starg || op == OpCodes.Starg_S) { if (i <= 255) il.Emit(OpCodes.Starg_S, (byte)i); else il.Emit(OpCodes.Starg, (short)i); return; }
        if (op == OpCodes.Ldarga || op == OpCodes.Ldarga_S) { if (i <= 255) il.Emit(OpCodes.Ldarga_S, (byte)i); else il.Emit(OpCodes.Ldarga, (short)i); return; }
        if (op == OpCodes.Ldloca || op == OpCodes.Ldloca_S) { if (i <= 255) il.Emit(OpCodes.Ldloca_S, (byte)i); else il.Emit(OpCodes.Ldloca, (short)i); return; }
        il.Emit(op, (short)i);
    }

    // 持久化的常量表与 Save 在 cleat-176
    private partial void EmitPackedConstants(ILGenerator il);
}
