#region License
/* Copyright (c) 2007-2016 Llewellyn Pritchard
 * All rights reserved.
 * This source code is subject to terms and conditions of the BSD License.
 * See docs/license.txt. */
#endregion

using System;
using System.Reflection;
using System.Text;

namespace Cleat.Scheme;

public partial class Builtins
{
    // can't move this, gets inlined most of the time
    [Primitive("eq?", Flags = PrimitiveFlags.Pure, Inline = "eq?")]
    public static object IsEqual(object first, object second)
    {
        return GetBool(ReferenceEquals(first, second));
    }

    // can't move this, gets inlined most of the time
    [Primitive("eqv?", Flags = PrimitiveFlags.Pure, Inline = "eqv?")]
    public static object IsEqualValue(object first, object second)
    {
        if (first is Encoding && second is Encoding)
        {
            return GetBool(((Encoding)first).WebName == ((Encoding)second).WebName);
        }

        // double.Equals 认为 0.0 与 -0.0 相等；R6RS eqv? 要求区分（NaN 与自身相等则与 Equals 一致）
        if (first is double a && second is double b)
        {
            return GetBool(BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b) || (double.IsNaN(a) && double.IsNaN(b)));
        }

        return GetBool(Equals(first, second));
    }
}
