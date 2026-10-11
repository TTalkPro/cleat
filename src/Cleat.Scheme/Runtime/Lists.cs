#region License
/* Copyright (c) 2007-2016 Llewellyn Pritchard
 * All rights reserved.
 * This source code is subject to terms and conditions of the BSD License.
 * See docs/license.txt. */
#endregion

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace Cleat.Scheme;

public partial class Builtins
{
    [Primitive("pair?", Flags = PrimitiveFlags.Pure, Inline = "pair?")]
    public static object IsPair(object arg1)
    {
        return GetBool(arg1 is Cons);
    }

    [Primitive("null?", Flags = PrimitiveFlags.Pure, Inline = "null?")]
    public static object IsNull(object arg1)
    {
        return GetBool(arg1 is EmptyList);
    }

    /// <summary>(nil? x)：x 是否为 #nil（.NET null）。与 null?（空表 '()）不同。</summary>
    [Primitive("nil?", Flags = PrimitiveFlags.Pure, Inline = "nil?")]
    public static object IsNil(object arg1)
    {
        return GetBool(arg1 == null);
    }

    [Primitive("list-prim")]
    public static object List(params object[] args)
    {
        return Cleat.Scheme.Cons.FromArray(args);
    }

    [Primitive("list-prim")]
    public static object List()
    {
        return EmptyList.Instance;
    }

    [Primitive("list-prim")]
    public static object List(object arg1)
    {
        return new Cons(arg1);
    }

    [Primitive("list-prim")]
    public static object List(object arg1, object arg2)
    {
        return new Cons(arg1, new Cons(arg2));
    }

    [Primitive("list-prim")]
    public static object List(object arg1, object arg2, object arg3)
    {
        return new Cons(arg1, new Cons(arg2, new Cons(arg3)));
    }

    [Primitive("list-prim")]
    public static object List(object arg1, object arg2, object arg3, object arg4)
    {
        return new Cons(arg1, new Cons(arg2, new Cons(arg3, new Cons(arg4))));
    }

    [Primitive("list-prim")]
    public static object List(object arg1, object arg2, object arg3, object arg4, object arg5)
    {
        return new Cons(arg1, new Cons(arg2, new Cons(arg3, new Cons(arg4, new Cons(arg5)))));
    }

    // overload, no export, bad me!
    public static Cons Cons(object car)
    {
        return new Cons(car);
    }

    [Primitive("cons", Inline = "cons")]
    public static object Cons(object car, object cdr)
    {
        return new Cons(car, cdr);
    }

    internal static object First(object args)
    {
        return Car(args);
    }

    internal static object Second(object args)
    {
        return Car(Cdr(args));
    }

    internal static object Third(object args)
    {
        return Car(Cdr(Cdr(args)));
    }

    internal static Cons LastPair(object args)
    {
        Cons c = Requires<Cleat.Scheme.Cons>(args);
        while (c.cdr is Cons)
        {
            c = c.cdr as Cons;
        }
        return c;
    }

    [Primitive("car", Inline = "car")]
    public static object Car(object args)
    {
        Cons c = args as Cons;
        if (c == null)
        {
            AssertionViolation("car", "not a pair", args);
        }
        return c.car;
    }

    [Primitive("cdr", Inline = "cdr")]
    public static object Cdr(object args)
    {
        Cons c = args as Cons;
        if (c == null)
        {
            AssertionViolation("cdr", "not a pair", args);
        }
        return c.cdr;
    }

    public static object Append(object arg1, object arg2)
    {
        if (arg1 is EmptyList)
        {
            return arg2;
        }
        if (arg2 is EmptyList)
        {
            return arg1;
        }
        Cons c = arg1 as Cons;
        while (c.cdr is Cons next)
        {
            c = next;
        }

        c.cdr = arg2;

        return arg1;
    }

    [Primitive("reverse!")]
    public static object NReverse(object lst)
    {
        Cons list = lst as Cons;

        if (list == null)
        {
            return EmptyList.Instance;
        }

        object prev = EmptyList.Instance;
        Cons next;

        while (true)
        {
            next = list.cdr as Cons;
            list.cdr = prev;

            if (next == null)
            {
                return list;
            }

            prev = list;
            list = next;
        }
    }

    internal static Cons ToImproper(Cons c)
    {
        Cons i = c;
        Cons j = null;

        while (i.cdr is not EmptyList)
        {
            j = i;
            i = i.cdr as Cons;
            if (i == null)
            {
                return c; // improper already
            }
        }

        j.cdr = i.car;
        return c;
    }

    /// <summary>字符串 → 字符表（iron-055：按码点；System.String 不含代理码元，但 .NET 来的串仍按代理对组合）。</summary>
    public static object StringToList(object s)
    {
        var chars = new List<object>();
        if (s is SchemeString ss)
        {
            for (int i = 0; i < ss.Length; i++) chars.Add(CharObject(ss.CodePointAt(i)));
        }
        else
        {
            foreach (int cp in SchemeString.CodePoints(s.ToString())) chars.Add(CharObject(cp));
        }
        return Cleat.Scheme.Cons.FromList(chars);
    }

    /// <summary>码点 → Scheme 字符对象（BMP 内为 char，之外为 SchemeChar）。</summary>
    public static object CharObject(int cp) => cp < 0x10000 ? (object)(char)cp : new SchemeChar(cp);
}
