using System.Collections;
using System.Collections.Generic;

namespace Cleat.Scheme;

/// <summary>
/// 序对。字段名 car/cdr 与旧运行时一致：它们是内部表示，编译器生成的代码直接读写（内联的 car / cdr / set-car!），
/// 所以保持公开。宿主代码按 Clojure 的方式经接口访问（designs/13-exceptions.md §7）：
/// 列表是 <see cref="IReadOnlyList{T}"/>（同 Clojure 的 java.util.List），<see cref="First"/> / <see cref="Next"/> /
/// <see cref="More"/> 与 Clojure 的 first / next / more 同义——表尾不要用 <c>cdr == null</c> 判断（表尾是 '()）。
/// </summary>
public sealed class Cons : IReadOnlyList<object>
{
    public object car;
    public object cdr;

    /// <summary>第一个元素（同 Clojure 的 first）。</summary>
    public object First => car;

    /// <summary>剩余部分的下一个序对；到表尾（或表尾不是 '() 的非正规表）为 null（同 Clojure 的 next）。</summary>
    public Cons Next => cdr as Cons;

    /// <summary>剩余部分；到表尾为 '()（<see cref="EmptyList.Instance"/>，同 Clojure 的 more / rest）。</summary>
    public object More => cdr;

    /// <summary>元素个数（沿 cdr 数序对，O(n)；非正规表只数到最后一个序对）。</summary>
    public int Count
    {
        get
        {
            int n = 0;
            for (Cons c = this; c != null; c = c.cdr as Cons) n++;
            return n;
        }
    }

    /// <summary>第 index 个元素（O(index)）。</summary>
    public object this[int index]
    {
        get
        {
            if (index >= 0)
                for (Cons c = this; c != null; c = c.cdr as Cons, index--)
                    if (index == 0) return c.car;
            throw new System.ArgumentOutOfRangeException(nameof(index));
        }
    }

    public Cons(object car) : this(car, EmptyList.Instance) { }

    public Cons(object car, object cdr)
    {
        this.car = car;
        this.cdr = cdr;
    }

    /// <summary>由序列构造正规列表；空序列返回 '()（<see cref="EmptyList.Instance"/>）。</summary>
    public static object FromList(IEnumerable list)
    {
        if (list == null) return EmptyList.Instance;
        Cons first = null, last = null;
        foreach (object o in list)
        {
            var c = new Cons(o);
            if (last == null) first = c; else last.cdr = c;
            last = c;
        }
        return (object)first ?? EmptyList.Instance;
    }

    public static object FromArray(params object[] args) => FromList(args);

    /// <summary>只对正规列表有意义：遇到非序对的 cdr 即停止。</summary>
    public IEnumerator<object> GetEnumerator()
    {
        for (Cons c = this; c != null; c = c.cdr as Cons)
            yield return c.car;
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
