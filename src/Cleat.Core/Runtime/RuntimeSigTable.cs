using System;
using System.Reflection;
using System.Threading;
using clojure.lang;

namespace Cleat.Core.Runtime;

/// <summary>编译器回调的结果：特化 IFn、它实际固定了哪些槽位（有效 key）、是否可能抛 TypedBailout（R 模式）。</summary>
public sealed class RuntimeSigCompiled
{
    public RuntimeSigCompiled(IFn fn, long key, bool guarded, Assembly assembly)
    {
        Fn = fn;
        Key = key;
        Guarded = guarded;
        Assembly = assembly;
    }

    public IFn Fn { get; }

    /// <summary>特化体真正按类型码固定的槽位；没固定的槽位为 0（object 通配）。</summary>
    public long Key { get; }

    /// <summary>R 模式（纯体、原始返回）：体内可能抛 TypedBailout，由表转成未命中后走通用体重算。</summary>
    public bool Guarded { get; }

    public Assembly Assembly { get; }
}

/// <summary>
/// cleat-138 的签名表（designs/27 §2.3）：函数类静态字段 `__rtSpec` 持有它。入口按形参掩码对实参算单一 key，
/// 先在条目里找相同 key；没有时选最具体的兼容条目（0 槽位通配）并把结果作为别名缓存；都不兼容返回 <see cref="Miss"/>，
/// 生成代码随即执行原通用体。未命中周期达到阈值时按当前实参请编译器补生成；失败的 key 记负表不再重试。
/// 每函数最多 <see cref="MaxEntries"/> 条（含别名），表满时 LRU 逐出空闲条目，释放特化 IFn 的强引用。
/// </summary>
public sealed class RuntimeSigTable
{
    /// <summary>未命中的哨兵；特化函数合法返回 nil，因此不能用 null 表示未命中。</summary>
    public static readonly object Miss = new();

    public const int MaxEntries = 16;
    public const int MaxNegative = 16;
    public const long EvictionIdleMilliseconds = 1000;

    private sealed class Entry
    {
        public Entry(long key, RuntimeSigCompiled compiled, bool alias)
        {
            Key = key;
            Fn = compiled.Fn;
            Guarded = compiled.Guarded;
            Compiled = compiled;
            Alias = alias;
            Assembly = compiled.Assembly is null ? null : new WeakReference(compiled.Assembly);
            LastUse = Environment.TickCount64;
        }

        public readonly long Key;
        public readonly IFn Fn;
        public readonly bool Guarded;
        public readonly bool Alias;
        public readonly RuntimeSigCompiled Compiled;
        public readonly WeakReference Assembly;
        public long Hits;
        public long LastUse;
    }

    private readonly string _className;
    private readonly int _mask;
    private readonly object _generic;
    private readonly object _sync = new();
    private volatile Entry[] _entries = Array.Empty<Entry>();
    private long[] _negative = Array.Empty<long>();
    private int _misses;

    public RuntimeSigTable(string className, int mask, object generic)
    {
        _className = className;
        _mask = mask;
        _generic = generic;
    }

    /// <summary>当前条目数（含别名）；诊断与测试用。</summary>
    public int Count => _entries.Length;

    /// <summary>非别名条目数；诊断与测试用。</summary>
    public int CompiledCount
    {
        get
        {
            var n = 0;
            foreach (var e in _entries) if (!e.Alias) n++;
            return n;
        }
    }

    public int Mask => _mask;

    public object Invoke(object a1)
    {
        var key = RuntimeSigKey.Of(_mask, a1);
        var e = Find(key) ?? Slow(key, new[] { a1 });
        if (e is null) return Miss;
        Touch(e);
        if (!e.Guarded) return e.Fn.invoke(a1);
        try { return e.Fn.invoke(a1); } catch (TypedBailout) { return Miss; }
    }

    public object Invoke(object a1, object a2)
    {
        var key = RuntimeSigKey.Of(_mask, a1, a2);
        var e = Find(key) ?? Slow(key, new[] { a1, a2 });
        if (e is null) return Miss;
        Touch(e);
        if (!e.Guarded) return e.Fn.invoke(a1, a2);
        try { return e.Fn.invoke(a1, a2); } catch (TypedBailout) { return Miss; }
    }

    public object Invoke(object a1, object a2, object a3)
    {
        var key = RuntimeSigKey.Of(_mask, a1, a2, a3);
        var e = Find(key) ?? Slow(key, new[] { a1, a2, a3 });
        if (e is null) return Miss;
        Touch(e);
        if (!e.Guarded) return e.Fn.invoke(a1, a2, a3);
        try { return e.Fn.invoke(a1, a2, a3); } catch (TypedBailout) { return Miss; }
    }

    public object Invoke(object a1, object a2, object a3, object a4)
    {
        var key = RuntimeSigKey.Of(_mask, a1, a2, a3, a4);
        var e = Find(key) ?? Slow(key, new[] { a1, a2, a3, a4 });
        if (e is null) return Miss;
        Touch(e);
        if (!e.Guarded) return e.Fn.invoke(a1, a2, a3, a4);
        try { return e.Fn.invoke(a1, a2, a3, a4); } catch (TypedBailout) { return Miss; }
    }

    public object Invoke(object a1, object a2, object a3, object a4, object a5)
    {
        var key = RuntimeSigKey.Of(_mask, a1, a2, a3, a4, a5);
        var e = Find(key) ?? Slow(key, new[] { a1, a2, a3, a4, a5 });
        if (e is null) return Miss;
        Touch(e);
        if (!e.Guarded) return e.Fn.invoke(a1, a2, a3, a4, a5);
        try { return e.Fn.invoke(a1, a2, a3, a4, a5); } catch (TypedBailout) { return Miss; }
    }

    public object Invoke(object a1, object a2, object a3, object a4, object a5, object a6)
    {
        var key = RuntimeSigKey.Of(_mask, a1, a2, a3, a4, a5, a6);
        var e = Find(key) ?? Slow(key, new[] { a1, a2, a3, a4, a5, a6 });
        if (e is null) return Miss;
        Touch(e);
        if (!e.Guarded) return e.Fn.invoke(a1, a2, a3, a4, a5, a6);
        try { return e.Fn.invoke(a1, a2, a3, a4, a5, a6); } catch (TypedBailout) { return Miss; }
    }

    public object Invoke(object a1, object a2, object a3, object a4, object a5, object a6, object a7)
    {
        var key = RuntimeSigKey.Of(_mask, a1, a2, a3, a4, a5, a6, a7);
        var e = Find(key) ?? Slow(key, new[] { a1, a2, a3, a4, a5, a6, a7 });
        if (e is null) return Miss;
        Touch(e);
        if (!e.Guarded) return e.Fn.invoke(a1, a2, a3, a4, a5, a6, a7);
        try { return e.Fn.invoke(a1, a2, a3, a4, a5, a6, a7); } catch (TypedBailout) { return Miss; }
    }

    public object Invoke(object a1, object a2, object a3, object a4, object a5, object a6, object a7, object a8)
    {
        var key = RuntimeSigKey.Of(_mask, a1, a2, a3, a4, a5, a6, a7, a8);
        var e = Find(key) ?? Slow(key, new[] { a1, a2, a3, a4, a5, a6, a7, a8 });
        if (e is null) return Miss;
        Touch(e);
        if (!e.Guarded) return e.Fn.invoke(a1, a2, a3, a4, a5, a6, a7, a8);
        try { return e.Fn.invoke(a1, a2, a3, a4, a5, a6, a7, a8); } catch (TypedBailout) { return Miss; }
    }

    /// <summary>9～16 参：生成入口传入一次性实参数组。</summary>
    public object InvokeArray(object[] args)
    {
        var key = RuntimeSigKey.Of(_mask, args);
        var e = Find(key) ?? Slow(key, (object[])args.Clone());
        if (e is null) return Miss;
        Touch(e);
        if (!e.Guarded) return e.Fn.applyTo(RT.arrayToList(args));
        try { return e.Fn.applyTo(RT.arrayToList(args)); } catch (TypedBailout) { return Miss; }
    }

    /// <summary>热路径：相同 key 的线性比较（条目少，long 比较）。</summary>
    private Entry Find(long key)
    {
        foreach (var e in _entries)
            if (e.Key == key) return e;
        return null;
    }

    private static void Touch(Entry e)
    {
        // 每 256 次命中刷新一次最近使用时间：热路径不必每次读时钟；只影响表满时的逐出判断。
        if ((++e.Hits & 255) == 0) e.LastUse = Environment.TickCount64;
    }

    /// <summary>
    /// 慢路径：选最具体的兼容条目并缓存为别名；没有兼容条目时计未命中，周期到了按当前实参补生成。
    /// key 为 0（没有值得特化的类型）只找兼容条目，不生成。
    /// </summary>
    private Entry Slow(long key, object[] args)
    {
        var best = BestCompatible(key);
        if (best is not null)
        {
            Add(key, best.Compiled, alias: true);
            return best;
        }
        if (key == 0 || IsNegative(key)) return null;
        var misses = Interlocked.Increment(ref _misses);
        if ((uint)misses % (uint)RuntimeSigSpec.Threshold != 0) return null;
        return Promote(key, args);
    }

    private Entry BestCompatible(long key)
    {
        Entry best = null;
        var bestScore = -1;
        foreach (var e in _entries)
        {
            if (e.Alias || !RuntimeSigKey.Compatible(e.Key, key)) continue;
            var score = RuntimeSigKey.Specificity(e.Key);
            if (score > bestScore) { best = e; bestScore = score; }
        }
        return best;
    }

    private bool IsNegative(long key)
    {
        foreach (var k in _negative)
            if (k == key) return true;
        return false;
    }

    /// <summary>按当前实参请编译器补生成 key 的特化；失败记负表。返回可直接调用的条目或 null。</summary>
    private Entry Promote(long key, object[] args)
    {
        if (!Monitor.TryEnter(_sync)) return null;
        try
        {
            var existing = Find(key) ?? BestCompatible(key);
            if (existing is not null) return existing;
            var compiled = RuntimeSigSpec.Compile(_className, args, _generic);
            if (compiled is null)
            {
                if (_negative.Length < MaxNegative)
                {
                    var neg = new long[_negative.Length + 1];
                    Array.Copy(_negative, neg, _negative.Length);
                    neg[^1] = key;
                    _negative = neg;
                }
                return null;
            }
            var entry = Add(compiled.Key, compiled, alias: false);
            if (entry is null) return null;
            // 有效 key 可能比实参 key 宽（某些槽位没有固定）；实参 key 另记一条别名，下次一次比较命中。
            if (compiled.Key != key) Add(key, compiled, alias: true);
            return entry;
        }
        finally
        {
            Monitor.Exit(_sync);
        }
    }

    /// <summary>复制替换条目数组；满了先逐出最久未用且空闲的条目，逐不出就放弃（下个周期再试）。</summary>
    private Entry Add(long key, RuntimeSigCompiled compiled, bool alias)
    {
        lock (_sync)
        {
            var entries = _entries;
            foreach (var e in entries)
                if (e.Key == key) return e;
            var entry = new Entry(key, compiled, alias);
            Entry[] next;
            if (entries.Length < MaxEntries)
            {
                next = new Entry[entries.Length + 1];
                Array.Copy(entries, next, entries.Length);
                next[^1] = entry;
            }
            else
            {
                var victim = SelectEviction(entries);
                if (victim is null) return null;
                next = new Entry[entries.Length];
                var at = 0;
                foreach (var e in entries)
                    if (!ReferenceEquals(e, victim)) next[at++] = e;
                next[at] = entry;
                if (!victim.Alias && !SharedFn(next, victim.Fn)) RuntimeSigSpec.RecordRetiredAssembly(victim.Assembly);
                // 逐出编译条目时连带它的别名，避免别名继续抓着特化 IFn。
                if (!victim.Alias) next = WithoutFn(next, victim.Fn);
            }
            _entries = next;
            return entry;
        }
    }

    private static bool SharedFn(Entry[] entries, IFn fn)
    {
        foreach (var e in entries)
            if (!e.Alias && ReferenceEquals(e.Fn, fn)) return true;
        return false;
    }

    private static Entry[] WithoutFn(Entry[] entries, IFn fn)
    {
        var n = 0;
        foreach (var e in entries)
            if (!ReferenceEquals(e.Fn, fn)) n++;
        if (n == entries.Length) return entries;
        var r = new Entry[n];
        var at = 0;
        foreach (var e in entries)
            if (!ReferenceEquals(e.Fn, fn)) r[at++] = e;
        return r;
    }

    private static Entry SelectEviction(Entry[] entries)
    {
        Entry victim = null;
        var now = Environment.TickCount64;
        foreach (var e in entries)
        {
            var lastUse = e.LastUse;
            if (now - lastUse < EvictionIdleMilliseconds) continue;
            // 同样空闲时先逐出别名：别名只是缓存，丢掉的代价最小。
            if (victim is null || (e.Alias && !victim.Alias) || (e.Alias == victim.Alias && lastUse < victim.LastUse)) victim = e;
        }
        return victim;
    }

    /// <summary>首个条目：入口达到阈值时由 <see cref="RuntimeSigSpec.Install"/> 建表后立即按实参补生成。</summary>
    internal void Seed(long key, object[] args)
    {
        if (key != 0) Promote(key, args);
    }
}
