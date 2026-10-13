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
    [Primitive("eq?", Flags = PrimitiveFlags.Pure | PrimitiveFlags.Mifoldable | PrimitiveFlags.Discard | PrimitiveFlags.Unrestricted, Inline = "eq?", Sig = "(ptr ptr) -> (boolean)")]
    public static object IsEqual(object first, object second)
    {
        return GetBool(ReferenceEquals(first, second));
    }

    // can't move this, gets inlined most of the time
    [Primitive("eqv?", Flags = PrimitiveFlags.Pure | PrimitiveFlags.Mifoldable | PrimitiveFlags.Discard | PrimitiveFlags.Unrestricted, Inline = "eqv?", Sig = "(ptr ptr) -> (boolean)")]
    public static object IsEqualValue(object first, object second)
    {
        if (first is Encoding && second is Encoding)
        {
            return GetBool(((Encoding)first).WebName == ((Encoding)second).WebName);
        }

        // double.Equals 认为 0.0 与 -0.0 相等；R6RS eqv? 要求区分（NaN 与自身相等则与 Equals 一致）
        if (first is double a && second is double b)
        {
            return GetBool(EqvDouble(a, b));
        }

        // 字符串按引用（string.Equals 比内容；(eqv? "a" (string-append "a")) 须为 #f），iron-072 对拍 Chez mats
        if (first is string)
        {
            return GetBool(ReferenceEquals(first, second));
        }

        // 复数逐分量按 eqv?（Complex64 的 == 把 ±0.0 看作相等：(eqv? 3.0+0.0i 3.0-0.0i) 须为 #f）
        if (first is Complex64 ca && second is Complex64 cb)
        {
            return GetBool(EqvDouble(ca.Real, cb.Real) && EqvDouble(ca.Imag, cb.Imag));
        }

        return GetBool(Equals(first, second));
    }

    private static bool EqvDouble(double a, double b) =>
      BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b) || (double.IsNaN(a) && double.IsNaN(b));
}
