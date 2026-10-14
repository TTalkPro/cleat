using System;
using System.Collections.Concurrent;

namespace Cleat.Core.Compiler;

/// <summary>
/// 编译期生成、尚未保存的类型（deftype* 的桩与本进程定义的类型）按全名登记，类型名决议先查这里（cleat-082 从元层的
/// TypeResolver 搬进 Cleat.Core）。
/// </summary>
public static class CompileTimeTypes
{
    private static readonly ConcurrentDictionary<string, Type> s_types = new ConcurrentDictionary<string, Type>();

    /// <summary>登记或替换（同名类型重定义时取最新的）。</summary>
    public static void Add(Type t) => s_types[t.FullName] = t;

    public static void Remove(Type t) => s_types.TryRemove(t.FullName, out _);

    /// <summary>按全名取；没有登记返回 null。</summary>
    public static Type Get(string name) => name != null && s_types.TryGetValue(name, out var t) ? t : null;

    /// <summary>类型名 → 类型：先查登记表，再按 Cleat.Core 的类型名语法解析；找不到返回 null。</summary>
    public static Type Resolve(string name) => Get(name) ?? Runtime.TypeResolver.Resolve(name, Array.Empty<string>());
}
