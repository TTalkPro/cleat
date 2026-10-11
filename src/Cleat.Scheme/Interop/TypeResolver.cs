using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace Cleat.Scheme;

/// <summary>
/// CLR 类型名解析（移植自旧 ClrGenerators.cs 的 ScanForType / ReadType / GetTypeFast）。
/// 类型说明可以是符号或字符串（含 Int32[] 这样的数组名）、或 (泛型名 类型参数 …) 列表；
/// 先查别名表，再按全名查，再按命名空间列表逐个加前缀查。System 命名空间总是在列表中。
/// </summary>
public static class TypeResolver
{
    /// <summary>与旧系统一致的别名；cons/list/procedure 指向新运行时类型。</summary>
    private static readonly Dictionary<string, string> s_aliases = new Dictionary<string, string>
    {
        ["fixnum"] = "Int32",
        ["int"] = "Int32",
        ["flonum"] = "Double",
        ["double"] = "Double",
        ["string"] = "String",
        ["bool"] = "Boolean",
        ["vector"] = "Object[]",
        ["bytevector"] = "Byte[]",
        ["cons"] = "Cleat.Scheme.Cons",
        ["list"] = "Cleat.Scheme.Cons",
        ["char"] = "Char",
        ["procedure"] = "Cleat.Scheme.Procedure",
        ["hashtable"] = "System.Collections.Hashtable",
        ["fixnum[]"] = "Int32[]",
        ["int[]"] = "Int32[]",
        ["flonum[]"] = "Double[]",
        ["double[]"] = "Double[]",
        ["string[]"] = "String[]",
        ["bool[]"] = "Boolean[]",
        ["vector[]"] = "Object[]",
        ["bytevector[]"] = "Byte[][]",
        ["cons[]"] = "Cleat.Scheme.Cons[]",
        ["list[]"] = "Cleat.Scheme.Cons[]",
        ["char[]"] = "Char[]",
        ["procedure[]"] = "Cleat.Scheme.Procedure[]",
        ["hashtable[]"] = "System.Collections.Hashtable[]",
        // 第三方代码中写死的旧运行时类型名：nanopass 子模块的 implementation-helpers.ironscheme.sls 调用
        // (clr-static-call IronScheme.Runtime.Builtins SourceLocation …)，不改子模块，在此映射（iron-017）
        ["IronScheme.Runtime.Builtins"] = "Cleat.Scheme.Builtins",
    };

    private static readonly ConcurrentDictionary<string, Type> s_compileTimeTypes = new ConcurrentDictionary<string, Type>();
    private static readonly ConcurrentDictionary<string, Type> s_cache = new ConcurrentDictionary<string, Type>();

    /// <summary>登记尚未保存的程序集中的类型（编译期生成的类型；旧 AddCompileTimeType）。</summary>
    public static void AddCompileTimeType(Type t) { s_compileTimeTypes[t.FullName] = t; s_cache.Clear(); }
    public static void RemoveCompileTimeType(Type t) { s_compileTimeTypes.TryRemove(t.FullName, out _); s_cache.Clear(); }

    /// <summary>按全名在登记表与已加载程序集中查公开类型；找不到返回 null。</summary>
    public static Type GetTypeFast(string name)
    {
        if (s_compileTimeTypes.TryGetValue(name, out var ct)) return ct;
        if (s_cache.TryGetValue(name, out var cached)) return cached;
        Type found = null;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm is AssemblyBuilder) continue;
            Type t;
            try { t = asm.GetType(name); } catch { continue; }
            if (t != null && (t.IsPublic || t.IsNestedPublic || (t.IsArray && IsPublicElement(t))))
            {
                found = t;
                break;
            }
        }
        if (found == null) found = LoadByConvention(name);
        if (found != null) s_cache[name] = found;
        return found;
    }

    private static readonly ConcurrentDictionary<string, bool> s_triedAssemblies = new ConcurrentDictionary<string, bool>();

    /// <summary>
    /// 已加载程序集中找不到时，按 .NET 的惯例尝试加载与类型全名或其命名空间同名的程序集
    /// （如 Microsoft.Win32.Registry 在程序集 Microsoft.Win32.Registry 中）。旧系统靠这些程序集碰巧已被加载。
    /// </summary>
    private static Type LoadByConvention(string name)
    {
        string n = name.TrimEnd(']', '[');
        for (int dot = n.Length; dot > 0; dot = n.LastIndexOf('.', dot - 1))
        {
            string candidate = n.Substring(0, dot);
            if (!s_triedAssemblies.TryAdd(candidate, true)) continue;
            Assembly asm;
            try { asm = Assembly.Load(new AssemblyName(candidate)); } catch (Exception) { continue; }
            Type t;
            try { t = asm.GetType(name); } catch (Exception) { continue; }
            if (t != null && (t.IsPublic || t.IsNestedPublic || (t.IsArray && IsPublicElement(t)))) return t;
        }
        return null;
    }

    private static bool IsPublicElement(Type t)
    {
        while (t.HasElementType) t = t.GetElementType();
        return t.IsPublic || t.IsNestedPublic;
    }

    /// <summary>名字 → 类型：别名 → 全名 → 依次加命名空间前缀。</summary>
    public static Type Scan(string name, IEnumerable<string> namespaces)
    {
        if (s_aliases.TryGetValue(name, out var mapped)) name = mapped;
        var t = GetTypeFast(name);
        if (t != null) return t;
        if (!name.StartsWith("System.", StringComparison.Ordinal) && (t = GetTypeFast("System." + name)) != null) return t;
        if (namespaces != null)
            foreach (var ns in namespaces)
                if ((t = GetTypeFast(ns + "." + name)) != null) return t;
        return null;
    }

    /// <summary>类型说明 → 类型；找不到返回 null。</summary>
    public static Type Resolve(object spec, IEnumerable<string> namespaces)
    {
        switch (spec)
        {
            case Type t: return t;
            case Symbol s: return Scan(s.Name, namespaces);
            case string str: return Scan(str, namespaces);
            case Cons c:
                {
                    // (Name arg …)：泛型构造；不支持开放泛型
                    if (c.cdr is EmptyList || !(c.car is Symbol g)) return null;
                    var args = new List<Type>();
                    foreach (var a in (Cons)c.cdr)
                    {
                        var at = Resolve(a, namespaces);
                        if (at == null) return null;
                        args.Add(at);
                    }
                    var gt = Scan(g.Name + "`" + args.Count, namespaces);
                    return gt?.MakeGenericType(args.ToArray());
                }
            default: return null;
        }
    }
}
