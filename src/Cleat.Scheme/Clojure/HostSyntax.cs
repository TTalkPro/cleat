using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cleat.Core.Runtime;
using clojure.lang;

namespace Cleat.Scheme.Clojure;

/// <summary>
/// 展开期的宿主类型规则（cleat-050），供 <c>(clojure syntax)</c> 调用：类型名 / 类型提示的决议与 deftype* / reify* 的接口方法匹配。
/// 规则与错误信息逐条照 C# 展开器（Cleat.Core.Expand.Expander 的 MaybeType / TagType / MaybeArrayType / GatherMethods /
/// ParseTypeMethod，cleat-013/014）；那里是私有方法，这里是 Scheme 侧可调用的静态入口。
/// 返回值只有 Type、字符串、布尔与 object[]（经 Scheme 互操作不变形）；错误以字符串返回，由调用方报语法错误。
/// </summary>
public static class HostSyntax
{
    private static readonly char[] s_triggerTypeChars = ['`', ',', '[', '&', '*'];

    private static readonly Dictionary<string, Type> s_specialTags = new Dictionary<string, Type>
    {
        ["int"] = typeof(int), ["long"] = typeof(long), ["float"] = typeof(float), ["double"] = typeof(double), ["char"] = typeof(char),
        ["short"] = typeof(short), ["byte"] = typeof(byte), ["sbyte"] = typeof(sbyte), ["ushort"] = typeof(ushort), ["uint"] = typeof(uint),
        ["ulong"] = typeof(ulong), ["bool"] = typeof(bool), ["boolean"] = typeof(bool), ["decimal"] = typeof(decimal), ["void"] = typeof(void),
        ["object"] = typeof(object), ["objects"] = typeof(object[]), ["ints"] = typeof(int[]), ["longs"] = typeof(long[]), ["floats"] = typeof(float[]),
        ["doubles"] = typeof(double[]), ["chars"] = typeof(char[]), ["shorts"] = typeof(short[]), ["bytes"] = typeof(byte[]), ["booleans"] = typeof(bool[]),
        ["bools"] = typeof(bool[]), ["uints"] = typeof(uint[]), ["ushorts"] = typeof(ushort[]), ["ulongs"] = typeof(ulong[]), ["sbytes"] = typeof(sbyte[]),
    };

    private static Namespace CurrentNs => (Namespace)clojure.lang.RT.CurrentNSVar.deref();

    private static Type ClassForName(string name)
    {
        // 正在展开的 deftype 的桩优先于同名的旧定义（重定义同名类型时 this 的提示不能指向旧类型，cleat-032）
        lock (s_pendingStubs) if (s_pendingStubs.TryGetValue(name, out var pending)) return pending;
        try { return clojure.lang.RT.classForName(name); }
        catch (Exception) { return null; }
    }

    /// <summary>HostExpr.MaybeType：form（符号或 Type）是否指一个类型；isLocal 为真表示同名的词法变量遮蔽了简单名。</summary>
    public static Type MaybeType(object form, bool isLocal)
    {
        if (form is Type t0) return t0;
        if (form is not clojure.lang.Symbol sym || sym.Namespace != null) return null;
        string n = sym.Name;
        if (n.IndexOf('.') > 0 || n[^1] == ']' || n.IndexOfAny(s_triggerTypeChars) >= 0)
            // Scheme 的解析器认得桩与本进程生成的类型；ClojureCLR 的泛型语法（BlockingCollection`1[System.Object]）由 Cleat.Core 的解析（cleat-053）
            return PendingStub(n) ?? TypeResolver.Resolve(n, Array.Empty<string>()) ?? Cleat.Core.Runtime.TypeResolver.Resolve(n, Array.Empty<string>());
        if (isLocal) return null;
        if (CurrentNs.GetMapping(sym) is Type t) return PendingStub(t.FullName) ?? t;
        return StubForTag(n) ?? ClassForName(n);
    }

    /// <summary>JVM 风格的数组类型 <c>long/1</c>（ClojureCLR 的 HostExpr.MaybeArrayType）。</summary>
    public static Type MaybeArrayType(object form)
    {
        if (form is not clojure.lang.Symbol sym || sym.Namespace == null || !Util.IsPosDigit(sym.Name)) return null;
        var comp = TagType(clojure.lang.Symbol.intern(sym.Namespace));
        if (comp == null) return null;
        int dim = sym.Name[0] - '0';
        for (int i = 0; i < dim; i++) comp = comp.MakeArrayType();
        return comp;
    }

    /// <summary>元数据的 :tag（符号、字符串或 Type）→ 符号；没有则 null。</summary>
    public static clojure.lang.Symbol TagOf(object o)
    {
        object tag = clojure.lang.RT.get(clojure.lang.RT.meta(o), clojure.lang.RT.TagKey);
        return tag switch
        {
            clojure.lang.Symbol s => s,
            string str => clojure.lang.Symbol.intern(null, str),
            Type t => clojure.lang.Symbol.intern(null, t.FullName),
            _ => null,
        };
    }

    /// <summary>类型提示 → Type；解析不了返回 null（提示只是提示）。</summary>
    public static Type TagType(object tagObj)
    {
        clojure.lang.Symbol tag = tagObj switch
        {
            clojure.lang.Symbol s => s,
            string str => clojure.lang.Symbol.intern(null, str),
            Type t => clojure.lang.Symbol.intern(null, t.FullName),
            _ => null,
        };
        if (tag == null) return tagObj as Type;
        if (tag.Namespace == null && s_specialTags.TryGetValue(tag.Name, out var st)) return st;
        var at = MaybeArrayType(tag);
        if (at != null) return at;
        if (tag.Namespace != null) return null;
        if (CurrentNs?.GetMapping(tag) is Type mapped) return PendingStub(mapped.FullName) ?? mapped;
        return StubForTag(tag.Name) ?? ClassForName(tag.Name);
    }

    /// <summary>Lcore 里的类型名：全名；动态程序集里的类型加程序集名。</summary>
    public static string TypeName(Type t) => t.Assembly.IsDynamic ? t.FullName + ", " + t.Assembly.GetName().Name : t.FullName;

    public static string Munge(string name) => clojure.lang.Munge.munge(name);

    public static bool IsPosDigit(string s) => Util.IsPosDigit(s);

    // —— deftype* / reify* ——

    /// <summary>接口符号 → Type 数组；错误为字符串（"Unable to resolve classname: X"、"only interfaces are supported, had: X"）。</summary>
    public static object ResolveInterfaces(object syms)
    {
        var r = new List<object>();
        for (ISeq s = clojure.lang.RT.seq(syms); s != null; s = s.next())
        {
            object o = s.first();
            Type t = o as Type ?? (o is clojure.lang.Symbol sym ? MaybeType(sym, false) : null);
            if (t == null) return "Unable to resolve classname: " + o;
            if (!t.IsInterface) return "only interfaces are supported, had: " + t.Name;
            r.Add(t);
        }
        return r.ToArray();
    }

    private sealed class Candidate
    {
        public MethodInfo Method;
        public Type Interface;   // 来自哪个接口（Object 的虚方法为 null）
    }

    private static Dictionary<string, List<Candidate>> GatherMethods(IEnumerable<Type> ifaces)
    {
        var all = new Dictionary<string, List<Candidate>>();
        void Add(Type iface, MethodInfo m)
        {
            if (m.IsStatic) return;
            if (!all.TryGetValue(m.Name, out var list)) all[m.Name] = list = new List<Candidate>();
            if (list.Any(c => c.Method == m)) return;
            list.Add(new Candidate { Method = m, Interface = iface });
        }
        foreach (var t in ifaces)
        {
            foreach (var m in t.GetMethods()) Add(t, m);
            foreach (var it in t.GetInterfaces()) foreach (var m in it.GetMethods()) Add(it, m);
        }
        foreach (var m in typeof(object).GetMethods(BindingFlags.Public | BindingFlags.Instance))
            if (m.IsVirtual && !m.IsFinal) Add(null, m);
        return all;
    }

    // —— deftype* 方法体里引用正在定义的类型（cleat-051）——
    // 展开方法体时类型还不存在：先在一个可回收的动态程序集里定义同名的桩类型（公开 object 字段 + 构造函数）并登记，
    // 于是 (new Foo …)、(. this -x)、^Foo 提示在展开期都能解析；运行时 ClojureOps 的互操作方法经 Real 把桩换成真正的类型
    // （deftype* 求值时登记的同名类型），保存的 dll 里类型常量按名字解析，本来就是真正的类型。与 ClojureCLR 的做法相同。

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Type, object> s_stubs = new();

    // 方法体里还可以用 deftype 的短名字（tagname）引用它（Clojure 的 COMPILE_STUB_SYM）：按（命名空间，短名字）登记桩，
    // 只在定义它的命名空间里解析；deftype 宏随后 import 真正的类型，命名空间映射优先（cleat-053）
    private static readonly Dictionary<string, Type> s_stubTags = new Dictionary<string, Type>();

    // 全名 → 正在展开的 deftype 的桩；deftype* 求值登记真正的类型时去掉（ClojureOps.DefineType，cleat-032）
    private static readonly Dictionary<string, Type> s_pendingStubs = new Dictionary<string, Type>();

    private static Type PendingStub(string fullName)
    {
        lock (s_pendingStubs) return s_pendingStubs.TryGetValue(fullName, out var t) ? t : null;
    }

    public static void ClearPendingStub(string fullName)
    {
        lock (s_pendingStubs) s_pendingStubs.Remove(fullName);
    }

    private static Type StubForTag(string name)
    {
        var ns = CurrentNs;
        if (ns == null) return null;
        lock (s_stubTags) return s_stubTags.TryGetValue(ns.Name.Name + "/" + name, out var t) ? t : null;
    }

    // 桩 → deftype 声明的接口（cleat-042：方法体里 (.m this …) 按接口静态决议，ClojureStaticCalls）
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Type, Type[]> s_stubInterfaces = new();

    internal static Type[] StubInterfaces(Type stub) => s_stubInterfaces.TryGetValue(stub, out var ifs) ? ifs : null;

    public static Type DefineStub(string name, object fieldNames, string tagName, object interfaces)
    {
        var t = DefineStub(name, fieldNames, tagName);
        if (interfaces is object[] ifs) s_stubInterfaces.AddOrUpdate(t, ifs.OfType<Type>().ToArray());
        return t;
    }

    public static Type DefineStub(string name, object fieldNames, string tagName)
    {
        var t = DefineStub(name, fieldNames);
        lock (s_pendingStubs) s_pendingStubs[name] = t;
        if (CurrentNs != null && !string.IsNullOrEmpty(tagName))
            lock (s_stubTags) s_stubTags[CurrentNs.Name.Name + "/" + tagName] = t;
        return t;
    }

    public static Type DefineStub(string name, object fieldNames)
    {
        var asmName = new AssemblyName("cleat$stub$" + Guid.NewGuid().ToString("N"));
        var ab = System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(asmName, System.Reflection.Emit.AssemblyBuilderAccess.RunAndCollect);
        var tb = ab.DefineDynamicModule(asmName.Name).DefineType(name, TypeAttributes.Public | TypeAttributes.Sealed);
        var fields = ((object[])fieldNames).Cast<string>().ToArray();
        foreach (var f in fields) tb.DefineField(f, typeof(object), FieldAttributes.Public);
        var ctor = tb.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, fields.Select(_ => typeof(object)).ToArray());
        var il = ctor.GetILGenerator();
        il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
        il.Emit(System.Reflection.Emit.OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes));
        il.Emit(System.Reflection.Emit.OpCodes.Ret);
        var t = tb.CreateType();
        s_stubs.AddOrUpdate(t, null);
        Cleat.Scheme.TypeResolver.AddCompileTimeType(t);
        return t;
    }

    public static bool IsStub(object t) => t is Type ty && s_stubs.TryGetValue(ty, out _);

    /// <summary>桩类型 → 同名的真正类型（已登记时）；其余原样。</summary>
    public static Type Real(Type t) =>
      t != null && s_stubs.TryGetValue(t, out _) ? Cleat.Core.Runtime.Loader.FindType(t.FullName) ?? t : t;

    /// <summary>类型名（TypeName 的结果）是否值类型（cleat-051：deftype 方法的参数装箱、返回值转换；void 也算）。</summary>
    public static bool IsValueType(string name)
    {
        var t = TypeResolver.Resolve(name, Array.Empty<string>());
        return t != null && t.IsValueType;
    }

    /// <summary>
    /// deftype* / reify* 的类型布局（cleat-051）：ifaces 为 ResolveInterfaces 的结果，specs 为各方法的 TypeMethod 结果。
    /// 返回 object[] { 接口全集（含继承的接口，类型名数组）, 桩数组 }；桩为接口中没有被方法直接实现的方法
    /// object[] { 接口名, 方法名, 返回类型名, 参数类型名数组, 目标方法下标（同名同参的方法，转调它）或 null（抛 NotImplementedException） }。
    /// reify 的 IObj.withMeta 与 IMeta.meta 由编译器生成，不列入。CLR 要求实现全部接口方法（JVM 不要求）。
    /// </summary>
    public static object InterfaceLayout(object ifaces, object specs, bool reify)
    {
        var all = new List<Type>();
        foreach (Type t in ((object[])ifaces).Cast<Type>())
        {
            if (!all.Contains(t)) all.Add(t);
            foreach (var it in t.GetInterfaces()) if (!all.Contains(it)) all.Add(it);
        }
        var ms = ((object[])specs).Cast<object[]>().ToList();
        static string[] Ps(object o) => ((object[])o).Cast<string>().ToArray();
        var stubs = new List<object>();
        foreach (var it in all)
        {
            foreach (var m in it.GetMethods())
            {
                if (m.IsStatic) continue;
                var ps = m.GetParameters().Select(p => TypeName(p.ParameterType)).ToArray();
                if (reify && ((it == typeof(IObj) && m.Name == "withMeta") || (it == typeof(IMeta) && m.Name == "meta"))) continue;
                string iname = TypeName(it);
                if (ms.Any(sp => (string)sp[0] == iname && (string)sp[1] == m.Name && Ps(sp[3]).SequenceEqual(ps))) continue;
                int target = ms.FindIndex(sp => (string)sp[1] == m.Name && Ps(sp[3]).SequenceEqual(ps));
                stubs.Add(new object[] { iname, m.Name, TypeName(m.ReturnType), ps.Cast<object>().ToArray(), target < 0 ? null : (object)target });
            }
        }
        return new object[] { all.Select(t => (object)TypeName(t)).ToArray(), stubs.ToArray() };
    }

    /// <summary>
    /// deftype* / reify* 的一个方法 (name [this args*] body…)：按名字（可为 Iface.name 显式实现）、参数个数与类型提示在接口
    /// （含继承的接口与 Object 的虚方法）中选定方法。返回 object[] { 接口名|null, 方法名, 返回类型名, 参数类型名数组 }，
    /// 或错误字符串（文本与 C# 展开器相同）。
    /// </summary>
    private static readonly Dictionary<string, string> s_objectMethodNames = new Dictionary<string, string>
    {
        ["toString"] = "ToString", ["hashCode"] = "GetHashCode", ["equals"] = "Equals",
    };

    public static object TypeMethod(object ifaces, object nameSym, object parms)
    {
        if (nameSym is not clojure.lang.Symbol dotName) return "Method name must be a symbol: " + nameSym;
        string methodName = dotName.Name;
        Type explicitIface = null;
        int idx = dotName.Name.LastIndexOf('.');
        if (idx >= 0)
        {
            explicitIface = ClassForName(dotName.Name.Substring(0, idx));
            if (explicitIface == null)
                return $"Unable to find interface {dotName.Name.Substring(0, idx)} for explicit method implementation: {dotName.Name}";
            methodName = dotName.Name.Substring(idx + 1);
        }
        methodName = Munge(methodName);
        if (parms is not IPersistentVector pv) return "Method parameters must be a vector: " + dotName;
        if (pv.count() == 0 || pv.nth(0) is not clojure.lang.Symbol) return "Must supply at least one argument for 'this' in: " + dotName;
        int argc = pv.count() - 1;
        var candidates = GatherMethods(((object[])ifaces).Cast<Type>());
        var matches = (candidates.TryGetValue(methodName, out var list) ? list : new List<Candidate>())
          .Where(c => c.Method.GetParameters().Length == argc && (explicitIface == null || c.Interface == explicitIface)).ToList();
        // java.lang.Object 的三个方法的 JVM 名字（Object (toString [_] …)，可移植代码的常见写法）→ System.Object 的同名虚方法（cleat-032）
        if (matches.Count == 0 && explicitIface == null && s_objectMethodNames.TryGetValue(methodName, out var clrName)
            && candidates.TryGetValue(clrName, out var objList))
            matches = objList.Where(c => c.Interface == null && c.Method.GetParameters().Length == argc).ToList();
        if (matches.Count == 0) return $"Can't define method not in interfaces: {dotName.Name}";
        // 不同接口里签名相同（参数与返回类型）的方法是同一个方法（ClojureCLR 按签名收集）：取第一个，其余接口的由桩转调
        // （cleat-053：gvec 的 count 同时来自 IPersistentCollection 与 Counted）
        matches = matches.GroupBy(c => string.Join(",", c.Method.GetParameters().Select(p => p.ParameterType.FullName)) + "->" + c.Method.ReturnType.FullName)
                         .Select(g => g.First()).ToList();
        var hinted = new Type[argc];
        bool anyHint = TagOf(dotName) != null;
        for (int i = 0; i < argc; i++)
        {
            if (pv.nth(i + 1) is not clojure.lang.Symbol p) return "Params must be Symbols";
            var tag = TagOf(p);
            if (tag != null)
            {
                anyHint = true;
                hinted[i] = TagType(tag);
                if (hinted[i] == null) return "Unable to resolve classname: " + tag;
            }
        }
        Candidate chosen;
        // 只差在返回类型、且其中一个的返回类型可赋给其余全部（ISeq.cons 返回 ISeq，IPersistentCollection.cons 返回
        // IPersistentCollection）：JVM 上是协变返回加桥接方法，不需要提示；取最具体的那个，其余接口方法由桩转调（cleat-032）
        Candidate covariant = null;
        if (matches.Count > 1 && !anyHint)
        {
            var sig0 = matches[0].Method.GetParameters().Select(p => p.ParameterType).ToArray();
            if (matches.All(c => c.Method.GetParameters().Select(p => p.ParameterType).SequenceEqual(sig0)))
                covariant = matches.FirstOrDefault(c => matches.All(o => o.Method.ReturnType.IsAssignableFrom(c.Method.ReturnType)));
        }
        if (matches.Count == 1) chosen = matches[0];
        else if (covariant != null) chosen = covariant;
        else
        {
            if (!anyHint) return "Must hint overloaded method: " + dotName.Name;
            var retHint = TagType(TagOf(dotName));
            var exact = matches.Where(c =>
            {
                var ps = c.Method.GetParameters();
                for (int i = 0; i < argc; i++) if ((hinted[i] ?? typeof(object)) != ps[i].ParameterType) return false;
                return retHint == null || c.Method.ReturnType == retHint;
            }).ToList();
            if (exact.Count != 1) return "Can't find matching overloaded method: " + dotName.Name;
            chosen = exact[0];
        }
        var m = chosen.Method;
        return new object[]
        {
            chosen.Interface == null ? null : TypeName(chosen.Interface),
            m.Name,
            TypeName(m.ReturnType),
            m.GetParameters().Select(p => (object)TypeName(p.ParameterType)).ToArray(),
        };
    }
}
