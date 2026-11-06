using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using clojure.lang;
using clojure.lang.TypeName2;

namespace Cleat.Core.Runtime;

/// <summary>
/// 类型名决议（designs/02 §8、05 §5）：ClojureCLR 的类型名语法（ClrTypeSpec2：泛型 <c>List`1[T]</c>、数组 <c>T[]</c>、
/// 嵌套 <c>A+B</c>、程序集限定名），另加 C# 风格的尖括号泛型 <c>List&lt;T&gt;</c> 与 Clojure 的特殊名
/// （<c>long</c> → Int64、<c>longs</c> → Int64[] …）。简单名按给定的命名空间前缀顺序尝试（编译器传方法所在命名空间的 clr-namespaces）。
/// 最终查找交给 RT.classForName（已加载程序集 + DependencyContext）。成功的结果按（名字，命名空间列表）缓存；
/// 当前 Clojure 命名空间的 import 别名不在这里查（展开器负责，04 §7），以免缓存跨命名空间串扰。
/// </summary>
public static class TypeResolver
{
    private static readonly ConcurrentDictionary<(string Name, string Namespaces), Type> s_cache = new ConcurrentDictionary<(string, string), Type>();

    /// <summary>忘掉名字为 name 的缓存项（deftype* 重定义同名类型时，cleat-051）。</summary>
    public static void Forget(string name)
    {
        foreach (var key in s_cache.Keys)
            if (key.Name == name) s_cache.TryRemove(key, out _);
    }

    /// <summary>解析类型名；找不到返回 null。</summary>
    public static Type Resolve(string name, IEnumerable<string> namespaces)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        name = name.Trim();
        string[] nss = namespaces == null ? Array.Empty<string>() : namespaces.Where(n => !string.IsNullOrEmpty(n)).ToArray();
        var key = (name, string.Join("\n", nss));
        if (s_cache.TryGetValue(key, out Type cached)) return cached;
        Type t = ResolveUncached(name, nss);
        if (t != null) s_cache.TryAdd(key, t);
        return t;
    }

    /// <summary>解析类型名；找不到抛 TypeNotFoundException。</summary>
    public static Type ResolveE(string name, IEnumerable<string> namespaces)
    {
        Type t = Resolve(name, namespaces);
        if (t != null) return t;
        string[] nss = namespaces == null ? Array.Empty<string>() : namespaces.ToArray();
        string tried = nss.Length == 0 ? "" : " (namespaces tried: " + string.Join(", ", nss) + ")";
        throw new TypeNotFoundException("Unable to resolve type: " + name + tried);
    }

    /// <summary>Clojure 的特殊类型名（int long … objects longs …）；不是则返回 null。</summary>
    public static Type SpecialTag(string name) => ClrTypeSpec.MaybeSpecialTag(name);

    private static Type ResolveUncached(string name, string[] nss)
    {
        Type special = ClrTypeSpec.MaybeSpecialTag(name);
        if (special != null) return special;

        ClrTypeSpec spec = ClrTypeSpec.Parse(NormalizeGenerics(name));
        if (spec == null) return null;
        try
        {
            return spec.Resolve(
                asmName => LoadAssembly(asmName),
                (asm, typename, ignoreCase, allowSpecialTags) => ResolveSimple(asm, typename, nss),
                false,
                false,
                true);
        }
        catch (Exception)
        {
            // 泛型元数不符、约束不满足等：按“找不到”处理
            return null;
        }
    }

    private static Assembly LoadAssembly(AssemblyName asmName)
    {
        try
        {
            return Assembly.Load(asmName);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>不带修饰的单个名字：程序集内查找；否则特殊名 → 原名 → 各命名空间前缀。</summary>
    private static Type ResolveSimple(Assembly asm, string typename, string[] nss)
    {
        if (asm != null) return asm.GetType(typename, false);

        Type t = ClrTypeSpec.MaybeSpecialTag(typename);
        if (t != null) return t;

        t = RT.classForName(typename, null, false);
        if (t != null) return t;

        foreach (string ns in nss)
        {
            t = RT.classForName(ns + "." + typename, null, false);
            if (t != null) return t;
        }
        return null;
    }

    /// <summary>C# 风格的 <c>List&lt;T&gt;</c> → ClrTypeSpec 的 <c>List[T]</c>（方括号既可做数组也可做泛型参数）。</summary>
    private static string NormalizeGenerics(string name)
    {
        if (name.IndexOf('<') < 0) return name;
        return name.Replace('<', '[').Replace('>', ']');
    }
}
