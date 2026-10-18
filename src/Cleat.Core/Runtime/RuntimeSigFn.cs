using System;
using System.Collections.Generic;
using System.Threading;
using clojure.lang;

namespace Cleat.Core.Runtime;

/// <summary>
/// cleat-133 / 134 / 135 的运行时签名表：每个函数的 `__rtSpec` 仍是一个 `RuntimeSigFn`，
/// 内部按精确 `Type[]` 保存多个已生成 IFn。热路径只线性比较少量签名且不分配；新签名 miss 时先回通用体，
/// 连续 miss 后通过编译器回调按需生成，再复制替换表数组。类型化体放弃推测时抛 TypedBailout，
/// 由生成入口的 catch 回到通用体，避免递归调用本包装器。
/// </summary>
public sealed class RuntimeSigFn
{
    /// <summary>每个函数允许同时驻留的精确签名数；防止多态站点无限生成。</summary>
    public const int MaxSignatures = 8;

    private sealed class Entry
    {
        public Entry(Type[] types, IFn fn) { Types = types; Fn = fn; }
        public Type[] Types { get; }
        public IFn Fn { get; }
    }

    private readonly string _className;
    private readonly object _sync = new();
    private volatile Entry[] _entries;
    private int _misses;
    private bool _promotionBlocked;

    /// <summary>兼容 cleat-133 / 134 的旧 seed：单签名表项没有类名，不会尝试补生成新签名。</summary>
    public RuntimeSigFn(Type[] types, IFn specialized) : this(types, specialized, null, false) { }

    /// <summary>兼容已编入 seed 的三参构造；回退不再递归调用通用包装器。</summary>
    public RuntimeSigFn(Type[] types, IFn specialized, IFn generic) : this(types, specialized, null, false) { }

    /// <summary>cleat-135：带类名的表项可以在新签名 miss 时请编译器补生成。</summary>
    public RuntimeSigFn(Type[] types, IFn specialized, string className)
        : this(types, specialized, className, true) { }

    private RuntimeSigFn(Type[] types, IFn specialized, string className, bool _)
    {
        _className = className;
        _entries = new[] { new Entry((Type[])types.Clone(), specialized) };
    }

    public object Invoke()
    {
        var e = _entries[0];
        if (e.Types.Length != 0) throw TypedBailout.Instance;
        return e.Fn.invoke();
    }

    public object Invoke(object a1)
    {
        if (Find(a1) is not { } fn) return Miss(a1);
        return fn.invoke(a1);
    }

    public object Invoke(object a1, object a2)
    {
        if (Find(a1, a2) is not { } fn) return Miss(a1, a2);
        return fn.invoke(a1, a2);
    }

    public object Invoke(object a1, object a2, object a3)
    {
        if (Find(a1, a2, a3) is not { } fn) return Miss(a1, a2, a3);
        return fn.invoke(a1, a2, a3);
    }

    public object Invoke(object a1, object a2, object a3, object a4)
    {
        if (Find(a1, a2, a3, a4) is not { } fn) return Miss(a1, a2, a3, a4);
        return fn.invoke(a1, a2, a3, a4);
    }

    public object Invoke(object a1, object a2, object a3, object a4, object a5)
    {
        if (Find(a1, a2, a3, a4, a5) is not { } fn) return Miss(a1, a2, a3, a4, a5);
        return fn.invoke(a1, a2, a3, a4, a5);
    }

    public object Invoke(object a1, object a2, object a3, object a4, object a5, object a6)
    {
        if (Find(a1, a2, a3, a4, a5, a6) is not { } fn) return Miss(a1, a2, a3, a4, a5, a6);
        return fn.invoke(a1, a2, a3, a4, a5, a6);
    }

    public object Invoke(object a1, object a2, object a3, object a4, object a5, object a6, object a7)
    {
        if (Find(a1, a2, a3, a4, a5, a6, a7) is not { } fn) return Miss(a1, a2, a3, a4, a5, a6, a7);
        return fn.invoke(a1, a2, a3, a4, a5, a6, a7);
    }

    public object Invoke(object a1, object a2, object a3, object a4, object a5, object a6, object a7, object a8)
    {
        if (Find(a1, a2, a3, a4, a5, a6, a7, a8) is not { } fn) return Miss(a1, a2, a3, a4, a5, a6, a7, a8);
        return fn.invoke(a1, a2, a3, a4, a5, a6, a7, a8);
    }

    /// <summary>超过常驻重载 arity 的调用接受一次临时数组；9～20 参由生成入口传入。</summary>
    public object InvokeArray(object[] args)
    {
        if (Find(args) is not { } fn) return Miss((object[])args.Clone());
        return fn.applyTo(RT.arrayToList(args));
    }

    private IFn Find(object a1)
    {
        foreach (var e in _entries)
            if (Match(e.Types, a1)) return e.Fn;
        return null;
    }

    private IFn Find(object a1, object a2)
    {
        foreach (var e in _entries)
            if (Match(e.Types, a1, a2)) return e.Fn;
        return null;
    }

    private IFn Find(object a1, object a2, object a3)
    {
        foreach (var e in _entries)
            if (Match(e.Types, a1, a2, a3)) return e.Fn;
        return null;
    }

    private IFn Find(object a1, object a2, object a3, object a4)
    {
        foreach (var e in _entries)
            if (Match(e.Types, a1, a2, a3, a4)) return e.Fn;
        return null;
    }

    private IFn Find(object a1, object a2, object a3, object a4, object a5)
    {
        foreach (var e in _entries)
            if (Match(e.Types, a1, a2, a3, a4, a5)) return e.Fn;
        return null;
    }

    private IFn Find(object a1, object a2, object a3, object a4, object a5, object a6)
    {
        foreach (var e in _entries)
            if (Match(e.Types, a1, a2, a3, a4, a5, a6)) return e.Fn;
        return null;
    }

    private IFn Find(object a1, object a2, object a3, object a4, object a5, object a6, object a7)
    {
        foreach (var e in _entries)
            if (Match(e.Types, a1, a2, a3, a4, a5, a6, a7)) return e.Fn;
        return null;
    }

    private IFn Find(object a1, object a2, object a3, object a4, object a5, object a6, object a7, object a8)
    {
        foreach (var e in _entries)
            if (Match(e.Types, a1, a2, a3, a4, a5, a6, a7, a8)) return e.Fn;
        return null;
    }

    private IFn Find(object[] args)
    {
        foreach (var e in _entries)
        {
            var types = e.Types;
            if (types.Length != args.Length) continue;
            var ok = true;
            for (var i = 0; i < args.Length; i++)
            {
                if (args[i] is null || args[i].GetType() != types[i]) { ok = false; break; }
            }
            if (ok) return e.Fn;
        }
        return null;
    }

    private static bool Match(Type[] types, object a1)
        => types.Length == 1 && a1 is not null && a1.GetType() == types[0];

    private static bool Match(Type[] types, object a1, object a2)
        => types.Length == 2 && a1 is not null && a2 is not null
           && a1.GetType() == types[0] && a2.GetType() == types[1];

    private static bool Match(Type[] types, object a1, object a2, object a3)
        => types.Length == 3 && a1 is not null && a2 is not null && a3 is not null
           && a1.GetType() == types[0] && a2.GetType() == types[1] && a3.GetType() == types[2];

    private static bool Match(Type[] types, object a1, object a2, object a3, object a4)
        => types.Length == 4 && SameSlot(types, 0, a1) && SameSlot(types, 1, a2)
            && SameSlot(types, 2, a3) && SameSlot(types, 3, a4);

    private static bool Match(Type[] types, object a1, object a2, object a3, object a4, object a5)
        => types.Length == 5 && Match(types, a1, a2, a3, a4) && SameSlot(types, 4, a5);

    private static bool Match(Type[] types, object a1, object a2, object a3, object a4, object a5, object a6)
        => types.Length == 6 && Match(types, a1, a2, a3, a4, a5) && SameSlot(types, 5, a6);

    private static bool Match(Type[] types, object a1, object a2, object a3, object a4, object a5, object a6, object a7)
        => types.Length == 7 && Match(types, a1, a2, a3, a4, a5, a6) && SameSlot(types, 6, a7);

    private static bool Match(Type[] types, object a1, object a2, object a3, object a4, object a5, object a6, object a7, object a8)
        => types.Length == 8 && Match(types, a1, a2, a3, a4, a5, a6, a7) && SameSlot(types, 7, a8);

    private static bool SameSlot(Type[] types, int i, object arg)
        => arg is not null && arg.GetType() == types[i];

    /// <summary>miss 的调用回通用体；偶发签名不付编译成本，连续 miss 才按原阈值请求补生成。</summary>
    private object Miss(params object[] args)
    {
        Promote(args);
        throw TypedBailout.Instance;
    }

    private void Promote(object[] args)
    {
        if (_className is null || _promotionBlocked) return;
        if (Interlocked.Increment(ref _misses) != RuntimeSigSpec.Threshold) return;
        if (!RuntimeSigSpec.Enabled || RuntimeSigSpec.Compiler is null || !RuntimeSigSpec.CanGenerate()) return;
        if (!Monitor.TryEnter(_sync)) return;

        try
        {
            if (_promotionBlocked || _entries.Length >= MaxSignatures) return;
            var candidate = RuntimeSigSpec.Promote(_className, args, this) as RuntimeSigFn;
            if (candidate is null)
            {
                // 证明失败 / 全局预算耗尽后暂停重试，避免不支持的热签名反复进入运行时编译。
                _promotionBlocked = true;
                return;
            }

            var entries = _entries;
            var added = new List<Entry>();
            foreach (var e in candidate._entries)
            {
                var exists = false;
                foreach (var old in entries)
                {
                    if (SameTypes(old.Types, e.Types)) { exists = true; break; }
                }
                if (!exists) added.Add(e);
            }

            var room = MaxSignatures - entries.Length;
            if (added.Count > room) added.RemoveRange(room, added.Count - room);
            if (added.Count == 0)
            {
                _promotionBlocked = true;
                return;
            }

            var next = new Entry[entries.Length + added.Count];
            Array.Copy(entries, next, entries.Length);
            added.CopyTo(next, entries.Length);
            _entries = next;
        }
        finally
        {
            Monitor.Exit(_sync);
        }
    }

    private static bool SameTypes(Type[] left, Type[] right)
    {
        if (left.Length != right.Length) return false;
        for (var i = 0; i < left.Length; i++)
        {
            if (left[i] != right[i]) return false;
        }
        return true;
    }
}
