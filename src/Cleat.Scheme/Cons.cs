using System.Collections;
using System.Collections.Generic;

namespace Cleat.Scheme;

/// <summary>序对。字段名 car/cdr 与旧运行时一致（Scheme 源码与编译器内联模板直接访问）。</summary>
public sealed class Cons : IEnumerable<object>
{
    public object car;
    public object cdr;

    public Cons(object car) : this(car, null) { }

    public Cons(object car, object cdr)
    {
        this.car = car;
        this.cdr = cdr;
    }

    /// <summary>由序列构造正规列表；空序列返回 null（即 '()）。</summary>
    public static Cons FromList(IEnumerable list)
    {
        if (list == null) return null;
        Cons first = null, last = null;
        foreach (object o in list)
        {
            var c = new Cons(o);
            if (last == null) first = c; else last.cdr = c;
            last = c;
        }
        return first;
    }

    public static Cons FromArray(params object[] args) => args == null ? null : FromList(args);

    /// <summary>只对正规列表有意义：遇到非序对的 cdr 即停止。</summary>
    public IEnumerator<object> GetEnumerator()
    {
        for (Cons c = this; c != null; c = c.cdr as Cons)
            yield return c.car;
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
