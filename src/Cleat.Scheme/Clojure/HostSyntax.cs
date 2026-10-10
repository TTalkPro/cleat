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
            return TypeResolver.Resolve(n, Array.Empty<string>());
        if (isLocal) return null;
        if (CurrentNs.GetMapping(sym) is Type t) return t;
        return ClassForName(n);
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
        if (CurrentNs?.GetMapping(tag) is Type mapped) return mapped;
        return ClassForName(tag.Name);
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

    /// <summary>
    /// deftype* / reify* 的一个方法 (name [this args*] body…)：按名字（可为 Iface.name 显式实现）、参数个数与类型提示在接口
    /// （含继承的接口与 Object 的虚方法）中选定方法。返回 object[] { 接口名|null, 方法名, 返回类型名, 参数类型名数组 }，
    /// 或错误字符串（文本与 C# 展开器相同）。
    /// </summary>
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
        if (matches.Count == 0) return $"Can't define method not in interfaces: {dotName.Name}";
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
        if (matches.Count == 1) chosen = matches[0];
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
