using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using clojure.lang;

namespace Cleat.Core.Compiler;

/// <summary>
/// proxy 与 gen-class 的类型布局（cleat-043）：在展开期用反射算出要生成的 CLR 类型的成员，编译器据此发射类型，
/// 编译器本身不反射。生成的是真正的子类：基类的 public / protected 虚方法与抽象方法按原签名覆写（属性、事件的访问器也是方法），
/// 新接口的方法按原签名实现（名字冲突时显式实现），基类的 public / protected 构造函数按原签名转发。
/// </summary>
public static partial class HostSyntax
{
    /// <summary>覆写或实现时可以按原签名生成的类型：不含 by-ref、指针、ref struct、未绑定的泛型参数。</summary>
    private static bool Expressible(Type t) =>
        !t.IsByRef && !t.IsPointer && !t.IsByRefLike && !t.ContainsGenericParameters && !t.IsFunctionPointer;

    private static bool Expressible(MethodInfo m) =>
        !m.IsGenericMethodDefinition && Expressible(m.ReturnType) && m.GetParameters().All(p => Expressible(p.ParameterType));

    private static bool Accessible(MethodBase m) => m.IsPublic || m.IsFamily || m.IsFamilyOrAssembly;

    private static string SigKey(MethodInfo m) => m.Name + "(" + string.Join(",", m.GetParameters().Select(p => TypeName(p.ParameterType))) + ")";

    private static object[] Names(IEnumerable<Type> ts) => ts.Select(t => (object)TypeName(t)).ToArray();

    private static Type ResolveClass(object o) => o as Type ?? (o is clojure.lang.Symbol s ? MaybeType(s, false) : o is string str ? ClassForName(str) : null);

    /// <summary>
    /// 基类可以覆写的实例方法：public / protected、virtual 且非 sealed，按签名取最派生的那个（被 new 隐藏的不算）。
    /// Object.Finalize 不覆写（否则每个实例都进终结队列）。
    /// </summary>
    private static List<MethodInfo> OverridableMethods(Type super)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var bySig = new Dictionary<string, MethodInfo>();
        for (var t = super; t != null; t = t.BaseType)
            foreach (var m in t.GetMethods(flags | BindingFlags.DeclaredOnly))
            {
                if (!Accessible(m)) continue;
                var key = SigKey(m);
                if (!bySig.ContainsKey(key)) bySig[key] = m;   // 从最派生的类型往上走：先见到的是可见的那个
            }
        return bySig.Values
          .Where(m => m.IsVirtual && !m.IsFinal && !(m.Name == "Finalize" && m.GetParameters().Length == 0))
          .ToList();
    }

    /// <summary>接口全集（含继承的接口），去重保序。</summary>
    private static List<Type> AllInterfaces(IEnumerable<Type> ifaces)
    {
        var all = new List<Type>();
        foreach (var t in ifaces)
        {
            if (!all.Contains(t)) all.Add(t);
            foreach (var it in t.GetInterfaces()) if (!all.Contains(it)) all.Add(it);
        }
        return all;
    }

    private static string SafeName(string s) => new string(s.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray());

    /// <summary>
    /// 类型要实现的方法，一项为 object[] { 方法名（本类型中的名字）, 返回类型名, 参数类型名数组, 访问性 "public"|"protected",
    /// 种类 "override"|"implement", 被覆写 / 实现的方法 { 声明类型名, 名字 }, 缺省行为 "base"|"throw", 映射键（方法的原名）,
    /// 能否装箱分派（false 时只生成抛错的桩）}。
    /// </summary>
    private static List<object[]> ClassMethods(Type super, List<Type> newIfaces, out string error)
    {
        error = null;
        var result = new List<object[]>();
        var used = new HashSet<string>();
        static string[] Ps(MethodInfo m) => m.GetParameters().Select(p => TypeName(p.ParameterType)).ToArray();
        foreach (var m in OverridableMethods(super))
        {
            bool ok = Expressible(m);
            if (!ok && !m.IsAbstract) continue;
            if (m.IsGenericMethodDefinition) { error = $"Cannot override generic method {m.DeclaringType.FullName}.{m.Name}"; return null; }
            used.Add(SigKey(m));
            result.Add(new object[]
            {
                m.Name, TypeName(m.ReturnType), Ps(m).Cast<object>().ToArray(), m.IsPublic ? "public" : "protected",
                "override", new object[] { TypeName(m.DeclaringType), m.Name }, m.IsAbstract ? "throw" : "base", m.Name, ok,
            });
        }
        foreach (var it in newIfaces)
        {
            // 基类已经实现的接口：经基类（可能已被覆写的）虚方法分派，不重新实现
            if (it.IsAssignableFrom(super)) continue;
            foreach (var m in it.GetMethods())
            {
                if (m.IsStatic || !m.IsAbstract) continue;   // 静态成员、带缺省实现的接口方法不实现
                if (m.IsGenericMethodDefinition) { error = $"Cannot implement generic interface method {it.FullName}.{m.Name}"; return null; }
                bool ok = Expressible(m);
                var key = SigKey(m);
                // 同名同参的方法已经有了（基类方法或别的接口）：显式实现
                string name = used.Add(key) ? m.Name : TypeName(it) + "." + m.Name;
                result.Add(new object[]
                {
                    name, TypeName(m.ReturnType), Ps(m).Cast<object>().ToArray(), "public",
                    "implement", new object[] { TypeName(it), m.Name }, "throw", m.Name, ok,
                });
            }
        }
        return result;
    }

    /// <summary>基类的 public / protected 构造函数的参数类型（类型名数组的数组）；参数要能按原签名转发。</summary>
    private static object[] BaseConstructors(Type super) =>
        super.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
             .Where(c => Accessible(c) && c.GetParameters().All(p => Expressible(p.ParameterType)))
             .Select(c => (object)c.GetParameters().Select(p => (object)TypeName(p.ParameterType)).ToArray())
             .ToArray();

    // —— gen-class（cleat-043）——

    private static object Opt(IPersistentMap m, string key) => m.valAt(Keyword.intern(null, key));

    private static string OptName(IPersistentMap m, string key)
    {
        var v = Opt(m, key);
        return v switch { null => null, clojure.lang.Symbol s => s.ToString(), Keyword k => k.Name, string str => str, _ => v.ToString() };
    }

    /// <summary>gen-class 选项里的类型：Type、原始类型名（int、void、ints…）、符号或字符串的类名（无点的简单名先按导入再按 System.）。</summary>
    private static Type GenClassType(object x, out string error)
    {
        error = null;
        Type t = null;
        switch (x)
        {
            case Type ty: t = ty; break;
            case clojure.lang.Symbol s when s.Namespace == null && s_specialTags.TryGetValue(s.Name, out var prim): t = prim; break;
            case clojure.lang.Symbol s:
                t = MaybeType(s, false) ?? (s.Name.Contains('.') ? null : ClassForName("System." + s.Name));
                break;
            case string str:
                t = s_specialTags.TryGetValue(str, out var p2) ? p2 : ClassForName(str) ?? CompileTimeTypes.Resolve(str);
                break;
            case ISeq or IPersistentList:
                error = "by-ref parameters are not supported in gen-class: " + x;
                return null;
        }
        if (t == null) error = "Unable to resolve classname: " + x;
        return t;
    }

    private static string OverloadName(string name, IEnumerable<string> paramTypeNames)
    {
        var ps = paramTypeNames.Select(n => ResolveTypeName(n)?.Name ?? n).ToList();
        return ps.Count == 0 ? name + "-void" : name + "-" + string.Join("-", ps);
    }

    private static Type ResolveTypeName(string n) => ClassForName(n) ?? CompileTimeTypes.Resolve(n);

    private static string VarField(string name) => Munge(name + "__var");

    /// <summary>
    /// gen-class 的类型布局（ClojureCLR GenClass 的语义，按 CLR 的规则生成真正的类型）：options 为选项映射（:name :extends :implements
    /// :init :post-init :constructors :methods :main :factory :state :exposes :exposes-methods :prefix :impl-ns :load-impl-ns）。
    /// 返回 object[] {
    ///   0 类名, 1 基类名, 2 接口名数组, 3 实现命名空间, 4 是否加载实现命名空间,
    ///   5 Var 字段 { 字段名, Var 名（前缀 + 名字）} 数组,
    ///   6 构造函数 { 参数类型名数组, 基类构造函数参数类型名数组 } 数组,
    ///   7 init 的 Var 字段名或 null, 8 post-init 的 Var 字段名或 null, 9 state 字段名或 null,
    ///   10 方法数组：{ 方法名, 返回类型名, 参数类型名数组, 访问性, 种类 "override"|"implement"|"new"|"static",
    ///                被覆写 / 实现的方法 {声明类型名, 名字} 或 null, 缺省行为 "base"|"throw", Var 字段, 重载 Var 字段或 null, 可分派? },
    ///   11 factory 名或 null, 12 main 的 Var 字段或 null,
    ///   13 exposes：{ 声明类型名, 字段名, 字段类型名, 静态?, 取值方法名或 null, 赋值方法名或 null } 数组,
    ///   14 exposes-methods：{ 公开名, 返回类型名, 参数类型名数组, 声明类型名, 基类方法名 } 数组 }，或错误字符串。
    /// </summary>
    public static object GenClassLayout(string currentNs, object options)
    {
        if (options is not IPersistentMap opts) return "gen-class options must be a map";
        string name = OptName(opts, "name");
        if (name == null) return "gen-class requires :name";
        string err;
        Type super = typeof(object);
        if (Opt(opts, "extends") is object ext) { super = GenClassType(ext, out err); if (super == null) return err; }
        if (super.IsSealed || super.IsValueType || super.IsInterface) return "gen-class :extends must be a non-sealed class: " + super.FullName;
        var ifaces = new List<Type>();
        for (ISeq s = clojure.lang.RT.seq(Opt(opts, "implements")); s != null; s = s.next())
        {
            var t = GenClassType(s.first(), out err);
            if (t == null) return err;
            if (!t.IsInterface) return "gen-class :implements must be interfaces, had: " + t.FullName;
            ifaces.Add(t);
        }
        string prefix = OptName(opts, "prefix") ?? "-";
        string implNs = OptName(opts, "impl-ns") ?? currentNs;
        bool loadImpl = Opt(opts, "load-impl-ns") is not (false or null) || !opts.containsKey(Keyword.intern(null, "load-impl-ns"));
        var all = AllInterfaces(ifaces);
        var methods = ClassMethods(super, all, out err);
        if (methods == null) return err;
        static object[] ToObjs(IEnumerable<string> xs) => xs.Cast<object>().ToArray();
        static string[] Strs(object o) => ((object[])o).Cast<string>().ToArray();

        // :methods [[名字 [参数类型 …] 返回类型] …]，^{:static true} 为静态方法；与继承的方法同名同参的忽略（JVM：不要重复）
        var existing = new HashSet<string>(methods.Select(m => (string)m[7] + "(" + string.Join(",", Strs(m[2])) + ")"));
        var entries = methods.Select(m => new List<object>(m)).ToList();
        for (ISeq s = clojure.lang.RT.seq(Opt(opts, "methods")); s != null; s = s.next())
        {
            var spec = s.first() as IPersistentVector;
            if (spec == null || spec.count() < 3) return "Malformed gen-class method signature: " + s.first();
            string mname = spec.nth(0) is clojure.lang.Symbol ms ? ms.Name : spec.nth(0).ToString();
            var ps = new List<string>();
            for (ISeq p = clojure.lang.RT.seq(spec.nth(1)); p != null; p = p.next())
            {
                var t = GenClassType(p.first(), out err);
                if (t == null) return err;
                ps.Add(TypeName(t));
            }
            var ret = GenClassType(spec.nth(2), out err);
            if (ret == null) return err;
            bool isStatic = spec is IMeta im && im.meta() is IPersistentMap mm && clojure.lang.RT.booleanCast(mm.valAt(Keyword.intern(null, "static")));
            if (!existing.Add(mname + "(" + string.Join(",", ps) + ")")) continue;
            entries.Add(new List<object> { mname, TypeName(ret), ToObjs(ps), "public", isStatic ? "static" : "new", null, "throw", mname, true });
        }

        // Var 字段：每个方法名一个（前缀 + 名字），同名多签名的再按签名一个（前缀 + 名字-参数类型短名…，无参数为 -void）
        var varFields = new Dictionary<string, string>();   // 字段名 → Var 名
        string AddVar(string n) { var f = VarField(n); varFields[f] = prefix + n; return f; }
        var byName = entries.GroupBy(e => (string)e[7]).ToDictionary(g => g.Key, g => g.Count());
        var outMethods = new List<object>();
        foreach (var e in entries)
        {
            string key = (string)e[7];
            string regular = AddVar(key);
            string overload = byName[key] > 1 ? AddVar(OverloadName(key, Strs(e[2]))) : null;
            outMethods.Add(new object[] { e[0], e[1], e[2], e[3], e[4], e[5], e[6], regular, overload, e[8] });
        }

        // 构造函数：缺省为基类的可访问构造函数（签名不变）；:constructors {[参数类型 …] [基类参数类型 …]} 时按它（要配 :init）
        var ctors = new List<object>();
        if (Opt(opts, "constructors") is IPersistentMap cmap && cmap.count() > 0)
        {
            for (ISeq s = clojure.lang.RT.seq(cmap); s != null; s = s.next())
            {
                var entry = (IMapEntry)s.first();
                var ps = new List<string>(); var sps = new List<string>();
                for (ISeq p = clojure.lang.RT.seq(entry.key()); p != null; p = p.next())
                { var t = GenClassType(p.first(), out err); if (t == null) return err; ps.Add(TypeName(t)); }
                for (ISeq p = clojure.lang.RT.seq(entry.val()); p != null; p = p.next())
                { var t = GenClassType(p.first(), out err); if (t == null) return err; sps.Add(TypeName(t)); }
                var sTypes = sps.Select(ResolveTypeName).ToArray();
                var bc = super.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, sTypes, null);
                if (bc == null || !Accessible(bc)) return "gen-class: no accessible constructor " + super.FullName + "(" + string.Join(", ", sps) + ")";
                ctors.Add(new object[] { ToObjs(ps), ToObjs(sps) });
            }
        }
        else
            foreach (object[] ps in BaseConstructors(super)) ctors.Add(new object[] { ps, ps });
        if (ctors.Count == 0) return "gen-class: no accessible constructor in " + super.FullName;

        string init = OptName(opts, "init"), postInit = OptName(opts, "post-init"), state = OptName(opts, "state");
        string initField = init != null ? AddVar(init) : null;
        string postInitField = postInit != null ? AddVar(postInit) : null;
        bool main = clojure.lang.RT.booleanCast(Opt(opts, "main"));
        string mainField = main ? AddVar("main") : null;
        string factory = OptName(opts, "factory");

        // :exposes {受保护字段 {:get 名字 :set 名字}}
        var exposes = new List<object>();
        for (ISeq s = clojure.lang.RT.seq(Opt(opts, "exposes")); s != null; s = s.next())
        {
            var entry = (IMapEntry)s.first();
            string fname = entry.key() is clojure.lang.Symbol fs ? fs.Name : entry.key().ToString();
            var f = super.GetField(fname, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            if (f == null || f.IsPrivate || f.IsAssembly) return "gen-class :exposes: no accessible field " + fname + " in " + super.FullName;
            var spec = entry.val() as IPersistentMap;
            string get = spec == null ? null : OptName(spec, "get"), set = spec == null ? null : OptName(spec, "set");
            exposes.Add(new object[] { TypeName(f.DeclaringType), f.Name, TypeName(f.FieldType), f.IsStatic, get, set });
        }
        // :exposes-methods {基类方法名 公开名}：该名字的每个可覆写的基类方法各一个公开的非虚入口
        var exposesMethods = new List<object>();
        for (ISeq s = clojure.lang.RT.seq(Opt(opts, "exposes-methods")); s != null; s = s.next())
        {
            var entry = (IMapEntry)s.first();
            string sname = entry.key() is clojure.lang.Symbol ss ? ss.Name : entry.key().ToString();
            string pub = entry.val() is clojure.lang.Symbol ps ? ps.Name : entry.val().ToString();
            var targets = OverridableMethods(super).Where(m => m.Name == sname && !m.IsAbstract && Expressible(m)).ToList();
            if (targets.Count == 0) return "gen-class :exposes-methods: no overridable method " + sname + " in " + super.FullName;
            foreach (var m in targets)
                exposesMethods.Add(new object[] { pub, TypeName(m.ReturnType), ToObjs(m.GetParameters().Select(p => TypeName(p.ParameterType))), TypeName(m.DeclaringType), m.Name });
        }

        return new object[]
        {
            name, TypeName(super), Names(all), implNs, loadImpl,
            varFields.Select(kv => (object)new object[] { kv.Key, kv.Value }).ToArray(),
            ctors.ToArray(), initField, postInitField, state,
            outMethods.ToArray(), factory, mainField, exposes.ToArray(), exposesMethods.ToArray(),
        };
    }

    /// <summary>
    /// proxy 的类型布局：bases 为基类（可省，缺省 Object）与接口（符号或 Type）。返回 object[] { 类名, 基类名, 接口名数组（含 IProxy）,
    /// 构造函数数组, 方法数组（见 ClassMethods）}，或错误字符串。类名照 JVM 的 proxy-name：命名空间.proxy$基类$接口短名…$哈希，
    /// 只取决于基类与接口，所以同一组基类在一个编译单元里只生成一个类型。
    /// </summary>
    public static object ProxyLayout(string nsName, object bases)
    {
        var types = new List<Type>();
        for (ISeq s = clojure.lang.RT.seq(bases); s != null; s = s.next())
        {
            var t = ResolveClass(s.first());
            if (t == null) return "Unable to resolve classname: " + s.first();
            types.Add(t);
        }
        Type super = types.Count > 0 && !types[0].IsInterface ? types[0] : typeof(object);
        var ifaces = types.Where(t => t != super).ToList();
        if (super.IsSealed || super.IsValueType) return "Cannot proxy sealed class: " + super.FullName;
        if (super.ContainsGenericParameters) return "Cannot proxy open generic class: " + super.FullName;
        foreach (var it in ifaces) if (!it.IsInterface) return "only one class may be proxied, had: " + it.FullName;
        var all = AllInterfaces(ifaces);
        var methods = ClassMethods(super, all, out var error);   // IProxy 的三个方法由编译器生成
        if (!all.Contains(typeof(IProxy))) all.Add(typeof(IProxy));
        if (methods == null) return error;
        var ctors = BaseConstructors(super);
        if (ctors.Length == 0) return "Cannot proxy class without accessible constructors: " + super.FullName;
        var inames = new SortedSet<string>(ifaces.Select(t => t.FullName), StringComparer.Ordinal);
        int hash = clojure.lang.Util.hasheq(PersistentTreeSet.create(clojure.lang.RT.seq(inames.ToArray())));
        string name = nsName.Replace('-', '_') + ".proxy$" + SafeName(super.Name)
                      + string.Concat(inames.Select(n => "$" + SafeName(n.Substring(n.LastIndexOf('.') + 1))))
                      + "$" + hash.ToString("X");
        return new object[] { name, TypeName(super), Names(all), ctors, methods.Cast<object>().ToArray() };
    }
}
