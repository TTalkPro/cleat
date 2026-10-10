#region License
/* Copyright (c) 2007-2016 Llewellyn Pritchard
 * All rights reserved.
 * This source code is subject to terms and conditions of the BSD License.
 * See docs/license.txt. */
#endregion

using System;
using System.Collections;
using System.Collections.Generic;

namespace Cleat.Scheme;

public partial class Builtins
{
    [Primitive("vector", Inline = "vector")]
    public static object Vector(params object[] args)
    {
        return args;
    }

    [Primitive("vector-append")]
    public static object VectorAppend(params object[] args)
    {
        ArrayList all = new ArrayList();
        foreach (var e in args)
        {
            all.AddRange(RequiresNotNull<object[]>(e));
        }
        return all.ToArray();
    }

    internal static Cons VectorToList(object[] vec)
    {
        return Cleat.Scheme.Cons.FromArray(vec);
    }

    internal static object[] ListToVector(Cons e)
    {
        // 先数长度再填充：不经 ArrayList 扩容与复制（iron-033，展开器封闭 rib 时对大表频繁调用）
        int n = 0;
        for (var p = e; p != null; p = Requires<Cons>(p.cdr)) n++;
        var v = new object[n];
        for (int i = 0; e != null; e = (Cons)e.cdr) v[i++] = e.car;
        return v;
    }

    public static object[] ListToVector(object args)
    {
        Cons e = Requires<Cons>(args);
        return ListToVector(e);
    }

    public static T[] ListToVector<T>(Cons list)
    {
        var l = new List<T>();
        while (list != null)
        {
            object o = list.car;
            if (o is T)
            {
                l.Add((T)o);
            }
            else
            {
                var t = typeof(T);
                if (o == null)
                {
                    AssertionViolation("ListToVector", "type cannot be null", t, FALSE);
                }
                AssertionViolation("ListToVector", "not type of " + t.Namespace + "." + t.Name, o, o.GetType());
            }

            list = Requires<Cons>(list.cdr);

        }
        return l.ToArray();
    }

    public static double[] ListToFlonumVector(Cons list)
    {
        return ListToVector<double>(list);
    }

    public static int[] ListToFixnumVector(Cons list)
    {
        return ListToVector<int>(list);
    }

    [Primitive("vector-filter")]
    public static object VectorFilter(object proc, object vector)
    {
        Procedure p = RequiresNotNull<Procedure>(proc);
        object[] v = RequiresNotNull<object[]>(vector);

        List<object> output = new List<object>();

        for (int i = 0; i < v.Length; i++)
        {
            if (IsTrue(p.Call(v[i])))
            {
                output.Add(v[i]);
            }
        }
        return output.ToArray();
    }
}
