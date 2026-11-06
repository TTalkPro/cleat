using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using Symbol = clojure.lang.Symbol;

namespace Cleat.Compiler.Passes.Front;

/// <summary>
/// 不可变的符号表（单链表），语义与 front.clj 的小集合相同：<c>set-add</c> 加在前面、<c>set-union a b</c> 把 a 的元素逐个加到 b 前面、
/// <c>set-minus</c> 保持顺序过滤。顺序决定 letrec-refs 与前向变量的次序（进而决定 L1 的形状），所以照搬。
/// </summary>
internal sealed class SymList : IEnumerable<Symbol>
{
    public static readonly SymList Empty = new SymList(null, null, 0);

    public readonly Symbol Head;
    public readonly SymList Tail;
    public readonly int Count;

    private SymList(Symbol head, SymList tail, int count)
    {
        Head = head;
        Tail = tail;
        Count = count;
    }

    public bool IsEmpty => Count == 0;

    public static SymList Of(Symbol x) => new SymList(x, Empty, 1);

    public SymList Cons(Symbol x) => new SymList(x, this, Count + 1);

    public bool Contains(Symbol x)
    {
        for (var s = this; s.Count > 0; s = s.Tail)
            if (s.Head.Equals(x)) return true;
        return false;
    }

    public SymList Add(Symbol x) => Contains(x) ? this : Cons(x);

    /// <summary>(set-union a b)：a 为空得 b；b 为空得 a；否则按 a 的顺序逐个 set-add 到 b。</summary>
    public static SymList Union(SymList a, SymList b)
    {
        if (a.IsEmpty) return b;
        if (b.IsEmpty) return a;
        var s = b;
        foreach (var x in a) s = s.Add(x);
        return s;
    }

    /// <summary>(reduce set-union () ss)</summary>
    public static SymList UnionAll(IEnumerable<SymList> ss)
    {
        var acc = Empty;
        foreach (var s in ss) acc = Union(acc, s);
        return acc;
    }

    public SymList Minus(IEnumerable<Symbol> xs)
    {
        var set = xs as ICollection<Symbol> ?? new List<Symbol>(xs);
        if (set.Count == 0) return this;
        var keep = new List<Symbol>(Count);
        foreach (var x in this)
            if (!set.Contains(x)) keep.Add(x);
        if (keep.Count == Count) return this;
        var r = Empty;
        for (int i = keep.Count - 1; i >= 0; i--) r = r.Cons(keep[i]);
        return r;
    }

    public IEnumerator<Symbol> GetEnumerator()
    {
        for (var s = this; s.Count > 0; s = s.Tail) yield return s.Head;
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// closures.clj 的小集合（无重复的向量）：<c>set-add</c> 追加在末尾、<c>set-union a b</c> 把 b 的元素逐个追加到 a、<c>set-minus</c> 过滤。
/// 顺序就是 L3 lambda 自由变量表的顺序。
/// </summary>
internal static class SymVec
{
    public static readonly ImmutableArray<Symbol> Empty = ImmutableArray<Symbol>.Empty;

    public static ImmutableArray<Symbol> Add(ImmutableArray<Symbol> s, Symbol x) => s.Contains(x) ? s : s.Add(x);

    public static ImmutableArray<Symbol> Union(ImmutableArray<Symbol> a, ImmutableArray<Symbol> b)
    {
        if (b.IsEmpty) return a;
        var r = a;
        foreach (var x in b) r = Add(r, x);
        return r;
    }

    public static ImmutableArray<Symbol> UnionAll(IEnumerable<ImmutableArray<Symbol>> ss)
    {
        var acc = Empty;
        foreach (var s in ss) acc = Union(acc, s);
        return acc;
    }

    public static ImmutableArray<Symbol> Minus(ImmutableArray<Symbol> s, IEnumerable<Symbol> xs)
    {
        var set = new HashSet<Symbol>(xs);
        if (set.Count == 0) return s;
        var b = ImmutableArray.CreateBuilder<Symbol>(s.Length);
        foreach (var x in s)
            if (!set.Contains(x)) b.Add(x);
        return b.Count == s.Length ? s : b.ToImmutable();
    }
}
