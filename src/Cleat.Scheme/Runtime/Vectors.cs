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
    [Primitive("vector", Inline = "vector", Flags = PrimitiveFlags.Discard | PrimitiveFlags.Unrestricted, Sig = "(ptr ...) -> (vector)")]
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

    internal static object VectorToList(object[] vec)
    {
        return Cleat.Scheme.Cons.FromArray(vec);
    }

    /// <summary>正规列表 → 向量；不是以 '() 结尾的列表报 assertion-violation。</summary>
    public static object[] ListToVector(object list)
    {
        // 先数长度再填充：不经 ArrayList 扩容与复制（iron-033，展开器封闭 rib 时对大表频繁调用）
        int n = 0;
        object p = list;
        for (; p is Cons c; p = c.cdr) n++;
        if (p is not EmptyList) AssertionViolation("list->vector", "not a proper list", list);
        var v = new object[n];
        int i = 0;
        for (p = list; p is Cons c; p = c.cdr) v[i++] = c.car;
        return v;
    }

    public static T[] ListToVector<T>(object list)
    {
        var l = new List<T>();
        object p = list;
        for (; p is Cons c; p = c.cdr)
        {
            object o = c.car;
            if (o is T t)
            {
                l.Add(t);
            }
            else
            {
                var type = typeof(T);
                if (o == null)
                {
                    AssertionViolation("ListToVector", "type cannot be null", type, FALSE);
                }
                AssertionViolation("ListToVector", "not type of " + type.Namespace + "." + type.Name, o, o.GetType());
            }
        }
        if (p is not EmptyList) AssertionViolation("ListToVector", "not a proper list", list);
        return l.ToArray();
    }

    public static double[] ListToFlonumVector(object list)
    {
        return ListToVector<double>(list);
    }

    public static int[] ListToFixnumVector(object list)
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
