using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;

namespace Cleat.Core.Runtime;

/// <summary>
/// eval 函数的类型反馈剖面（cleat-128）：函数类静态字段保存名字、活动标志、每形参的第一类与调用数。
/// 类型身份直接使用 CLR 的 <see cref="Type"/> 实例引用比较，等价于方法表身份且不需要散列；达到阈值后返回 false，
/// 生成代码写回活动标志，之后只读一次布尔。只有全部形参单态时记入特化候选。本任务不安装特化。
/// </summary>
public static class TypeFeedback
{
    /// <summary>设计 §2.1 的触发阈值。</summary>
    public const int Threshold = 1000;

    private static readonly object s_null = new();
    private static readonly object s_poly = new();
    private static readonly ConcurrentDictionary<string, Report> s_reports = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<object, string> s_typeNames = new();

    private sealed class Report
    {
        public Report(string name, int arity) { Name = name; Arity = arity; Types = new string[arity]; }
        public string Name { get; }
        public int Arity { get; }
        public long Calls;
        public string[] Types;
        public bool Triggered;
    }

    /// <summary>类型初始化器登记剖面；热路径本身不分配。</summary>
    public static void Register(string name, int arity) =>
        s_reports.GetOrAdd(name, n => new Report(n, arity));

    private static object Key(object o) => o is null ? s_null : o.GetType();

    private static void Name(object type, string[] names, int i) =>
        names[i] = s_typeNames.GetOrAdd(type, t => ReferenceEquals(t, s_null) ? "null"
            : ReferenceEquals(t, s_poly) ? "polymorphic" : ((System.Type)t).Name);

    private static void Observe(object[] types, string[] names, int i, object arg)
    {
        var key = Key(arg);
        var seen = types[i];
        if (seen is null)
        {
            types[i] = key;
            Name(key, names, i);
        }
        else if (!ReferenceEquals(seen, key))
        {
            types[i] = s_poly;
            names[i] = "polymorphic";
        }
    }

    private static bool Finish(Report r, long[] counts, object[] types, string[] names)
    {
        r.Types = names;
        var calls = ++counts[0];
        for (var i = 1; i < r.Arity; i++) counts[i] = calls;
        r.Calls = calls;
        if (calls < Threshold) return true;
        r.Triggered = true;
        for (var i = 0; i < r.Arity; i++)
            if (ReferenceEquals(types[i], s_poly)) r.Triggered = false;
        return false;
    }

    private static bool BadShape(Report r, long[] counts, object[] types, string[] names, int args) =>
        r.Arity != args || counts.Length != args || types.Length != args || names.Length != args;

    private static bool Disabled(Report r, long[] counts)
    {
        if (counts[0] >= Threshold) r.Triggered = false;
        return false;
    }

    public static bool Profile(string name, long[] counts, object[] types, string[] names, object a0)
    {
        if (!s_reports.TryGetValue(name, out var r) || BadShape(r, counts, types, names, 1)) return false;
        if (counts[0] >= Threshold) return Disabled(r, counts);
        Observe(types, names, 0, a0);
        return Finish(r, counts, types, names);
    }

    public static bool Profile(string name, long[] counts, object[] types, string[] names, object a0, object a1)
    {
        if (!s_reports.TryGetValue(name, out var r) || BadShape(r, counts, types, names, 2)) return false;
        if (counts[0] >= Threshold) return Disabled(r, counts);
        Observe(types, names, 0, a0);
        Observe(types, names, 1, a1);
        return Finish(r, counts, types, names);
    }

    public static bool Profile(string name, long[] counts, object[] types, string[] names, object a0, object a1, object a2)
    {
        if (!s_reports.TryGetValue(name, out var r) || BadShape(r, counts, types, names, 3)) return false;
        if (counts[0] >= Threshold) return Disabled(r, counts);
        Observe(types, names, 0, a0);
        Observe(types, names, 1, a1);
        Observe(types, names, 2, a2);
        return Finish(r, counts, types, names);
    }

    public static bool Profile(string name, long[] counts, object[] types, string[] names, object a0, object a1, object a2, object a3)
    {
        if (!s_reports.TryGetValue(name, out var r) || BadShape(r, counts, types, names, 4)) return false;
        if (counts[0] >= Threshold) return Disabled(r, counts);
        Observe(types, names, 0, a0);
        Observe(types, names, 1, a1);
        Observe(types, names, 2, a2);
        Observe(types, names, 3, a3);
        return Finish(r, counts, types, names);
    }

    /// <summary>诊断快照：每函数的第一类 / 多态、调用数与触发。</summary>
    public static (string Name, int Arity, long Calls, string[] Types, bool Triggered)[] Snapshot() =>
        s_reports.Values.OrderBy(r => r.Name, StringComparer.Ordinal)
            .Select(r => (r.Name, r.Arity, r.Calls, (string[])r.Types.Clone(), r.Triggered)).ToArray();

    /// <summary>人读的统计；量化脚本打印它。</summary>
    public static string Summary()
    {
        var rows = Snapshot();
        var mono = new Dictionary<string, int>();
        long calls = 0;
        foreach (var r in rows)
        {
            calls += r.Calls;
            foreach (var t in r.Types) mono[t ?? "unseen"] = mono.GetValueOrDefault(t ?? "unseen") + 1;
        }
        var sb = new StringBuilder("typefeedback: reports=").Append(rows.Length)
            .Append(" triggered=").Append(rows.Count(r => r.Triggered))
            .Append(" profiled-calls=").Append(calls)
            .Append(" param-types=[").Append(string.Join(",", mono.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Key + ":" + x.Value)))
            .Append(']');
        foreach (var r in rows.Where(r => r.Triggered))
            sb.Append('\n').Append(r.Name).Append('/').Append(r.Arity).Append(" calls=").Append(r.Calls)
              .Append(" types=[").Append(string.Join(",", r.Types)).Append(']');
        return sb.ToString();
    }
}
