using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;

namespace Cleat.Core.Runtime;

/// <summary>
/// 宿主调用的 by-ref 实参（cleat-183，ClojureCLR 的 <c>(by-ref x)</c>）：<c>ref</c> / <c>out</c> 形参。编译器把调用改写成
/// "调用 → 结果数组 → 逐个 set! 写回局部"：本类做运行时（反射）这条路，返回 <c>[结果, 第一个 by-ref 位置的新值, …]</c>，
/// 值按 <see cref="Reflector.ConvertFrom"/> 转成 Clojure 表示。编译期能唯一决议时编译器直接发射 <c>ldloca</c>（同样的结果数组）。
/// <para>重载：形参个数相同、by-ref 形参恰好在 <c>mask</c> 标出的位置上（by-ref 实参只匹配 by-ref 形参，反之亦然）；
/// 其余位置按 Reflector 的匹配等级；<c>out</c> 位置不看实参（调用前的值不传进去），<c>ref</c> 位置看局部当前的值。</para>
/// </summary>
public static class ByRefCalls
{
    // ref 位置的值类型也进键：选重载要看它（out 位置不看，但进键无害）
    private readonly record struct Key(Type Type, string Name, string Kind, long Mask, string ArgTypes);

    private static readonly ConcurrentDictionary<Key, MethodBase> s_selected = new();
    private static readonly ConcurrentDictionary<Type, Func<object, object>> s_converters = new();
    private static readonly MethodInfo s_convert = typeof(Reflector).GetMethod(nameof(Reflector.Convert), new[] { typeof(object) });

    /// <summary>kind 为 "static" / "instance" / "new"；instance 时 operands[0] 是接收者（type 为 null 时按它的运行时类型）。</summary>
    public static object[] Invoke(string kind, Type type, string name, long mask, object[] operands)
    {
        operands ??= Array.Empty<object>();
        object inst = null;
        object[] args = operands;
        if (kind == "instance")
        {
            inst = operands[0] ?? throw new NullReferenceException("by-ref call to " + name + " on nil");
            args = operands.Skip(1).ToArray();
            type ??= inst.GetType();
        }
        if (type == null) throw new ArgumentNullException(nameof(type), "by-ref call needs a type");
        var key = new Key(type, name, kind, mask, string.Join(",", args.Select((a, i) => (((mask >> i) & 1) != 0 ? "&" : "") + (a?.GetType().AssemblyQualifiedName ?? "nil"))));
        var m = s_selected.GetOrAdd(key, k => Select(k, args));
        var ps = m.GetParameters();
        var call = new object[ps.Length];
        for (int i = 0; i < ps.Length; i++)
        {
            var p = ps[i];
            if (p.ParameterType.IsByRef)
                call[i] = IsOut(p) ? null : ToClr(args[i], p.ParameterType.GetElementType());
            else
                call[i] = ToClr(args[i], p.ParameterType);
        }
        object ret = m is ConstructorInfo c
            ? c.Invoke(BindingFlags.DoNotWrapExceptions, null, call, null)
            : m.Invoke(inst, BindingFlags.DoNotWrapExceptions, null, call, null);
        var result = new object[1 + BitOperations.PopCount((ulong)mask)];
        result[0] = m is MethodInfo mi ? (mi.ReturnType == typeof(void) ? null : Reflector.ConvertFrom(ret, mi.ReturnType)) : ret;
        int j = 1;
        for (int i = 0; i < ps.Length; i++)
            if (((mask >> i) & 1) != 0) result[j++] = Reflector.ConvertFrom(call[i], ps[i].ParameterType.GetElementType());
        return result;
    }

    /// <summary>结果数组的第 i 个元素（编译器写回局部时用）。</summary>
    public static object Nth(object result, object i) => ((object[])result)[System.Convert.ToInt32(i)];

    /// <summary><c>out</c> 形参（C# 的 out：Out 而非 In）。</summary>
    public static bool IsOut(ParameterInfo p) => p.IsOut && !p.IsIn;

    private static object ToClr(object v, Type t)
    {
        if (v == null || t == typeof(object) || t.IsInstanceOfType(v) && !t.IsValueType) return v;
        if (t.IsValueType && v.GetType() == t) return v;
        var f = s_converters.GetOrAdd(t, tt =>
        {
            var gm = s_convert.MakeGenericMethod(tt);
            return x => gm.Invoke(null, BindingFlags.DoNotWrapExceptions, null, new[] { x }, null);
        });
        return f(v);
    }

    /// <summary>形状相符（个数、by-ref 位置）的候选：编译期决议（<see cref="ClojureStaticCalls.ResolveByRef"/>）与运行时共用。</summary>
    internal static List<MethodBase> Candidates(Type type, string name, string kind, long mask, int n)
    {
        IEnumerable<MethodBase> all;
        if (kind == "new")
            all = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        else
        {
            bool st = kind == "static";
            var flags = BindingFlags.Public | (st ? BindingFlags.Static : BindingFlags.Instance) | BindingFlags.FlattenHierarchy;
            IEnumerable<Type> types = !st && type.IsInterface ? new[] { type }.Concat(type.GetInterfaces()) : new[] { type };
            all = types.SelectMany(t => t.GetMethods(flags)).Where(m => m.Name == name && !m.IsGenericMethodDefinition && !m.ContainsGenericParameters);
        }
        return all.Where(m =>
        {
            var ps = m.GetParameters();
            if (ps.Length != n) return false;
            for (int i = 0; i < n; i++)
            {
                var pt = ps[i].ParameterType;
                bool byRef = pt.IsByRef;
                if (byRef != (((mask >> i) & 1) != 0)) return false;
                var et = byRef ? pt.GetElementType() : pt;
                if (et.IsPointer || et.IsByRefLike || et.IsByRef) return false;
            }
            return true;
        }).Distinct().ToList();
    }

    private static MethodBase Select(Key k, object[] args)
    {
        var cands = Candidates(k.Type, k.Name, k.Kind, k.Mask, args.Length);
        var scored = new List<(MethodBase M, int[] Levels)>();
        foreach (var m in cands)
        {
            var ps = m.GetParameters();
            var levels = new int[ps.Length];
            bool ok = true;
            for (int i = 0; i < ps.Length && ok; i++)
            {
                var p = ps[i];
                if (p.ParameterType.IsByRef)
                    levels[i] = IsOut(p) ? 0 : Reflector.MatchLevelOf(p.ParameterType.GetElementType(), args[i], refSlot: true);
                else
                    levels[i] = Reflector.MatchLevelOf(p.ParameterType, args[i], refSlot: false);
                ok = levels[i] >= 0;
            }
            if (ok) scored.Add((m, levels));
        }
        if (scored.Count == 0)
            throw new MissingMethodException(
                $"No {(k.Kind == "new" ? "constructor" : "method " + k.Name)} on {clojure.lang.Util.NameForType(k.Type)} takes {args.Length} argument(s) "
                + $"with by-ref parameters exactly at position(s) {string.Join(", ", Enumerable.Range(0, args.Length).Where(i => ((k.Mask >> i) & 1) != 0))}"
                + (cands.Count > 0 ? " for these argument types" : ""));
        bool Better((MethodBase M, int[] Levels) a, (MethodBase M, int[] Levels) b)
        {
            bool strict = false;
            for (int i = 0; i < a.Levels.Length; i++)
            {
                if (a.Levels[i] > b.Levels[i]) return false;
                if (a.Levels[i] < b.Levels[i]) strict = true;
            }
            return strict;
        }
        var best = scored.Where(a => !scored.Any(b => !ReferenceEquals(a.M, b.M) && Better(b, a))).ToList();
        if (best.Count == 1) return best[0].M;
        // 同签名（派生类型 new 隐藏、接口继承）：取声明类型最靠近叶子的
        var sig0 = best[0].M.GetParameters().Select(p => p.ParameterType).ToArray();
        if (best.All(b => b.M.GetParameters().Select(p => p.ParameterType).SequenceEqual(sig0)))
            return best.FirstOrDefault(a => best.All(b => b.M.DeclaringType.IsAssignableFrom(a.M.DeclaringType))).M ?? best[0].M;
        throw new InvalidOperationException(
            $"More than one matching {(k.Kind == "new" ? "constructor" : "method " + k.Name)} with by-ref arguments on {clojure.lang.Util.NameForType(k.Type)}: "
            + string.Join("; ", best.Select(b => b.M.ToString())));
    }
}
