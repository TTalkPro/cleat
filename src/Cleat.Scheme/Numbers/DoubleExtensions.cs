#region License
/* Copyright (c) 2007-2016 Llewellyn Pritchard
 * All rights reserved.
 * This source code is subject to terms and conditions of the BSD License.
 * See docs/license.txt. */
#endregion

using System;
using BigInteger = System.Numerics.BigInteger;

namespace Cleat.Scheme;

internal static class DoubleExtensions
{
    private const long MANTISSA_MASK = (long)(ulong.MaxValue >> (64 - 52));
    private const long EXPONENT_MASK = (long)(ulong.MaxValue >> (64 - 11));
    private const long SIGN_MASK = 0x1;

    public static long GetMantissa(this double d)
    {
        var r = BitConverter.DoubleToInt64Bits(d);
        return GetMantissa(r);
    }

    private static long GetMantissa(long r)
    {
        return r & MANTISSA_MASK;
    }

    public static int GetExponent(this double d)
    {
        var r = BitConverter.DoubleToInt64Bits(d);
        return GetExponent(r);
    }

    private static int GetExponent(long r)
    {
        r >>= 52;
        return (int)(r & EXPONENT_MASK);
    }

    public static int GetSign(this double d)
    {
        var r = BitConverter.DoubleToInt64Bits(d);
        return GetSign(r);
    }

    private static int GetSign(long r)
    {
        r >>= 63;
        return (int)(r & SIGN_MASK);
    }

    private static readonly BigInteger s_mantissa = (ulong.MaxValue >> (64 - 52)) + 1;

    public static bool GetMantissaAndExponent(this double d, out BigInteger mantissa, out BigInteger exponent)
    {
        if (double.IsNaN(d))
        {
            mantissa = exponent = 0;
            return false;
        }

        if (double.IsNegativeInfinity(d))
        {
            mantissa = -1;
            exponent = 0;
            return false;
        }

        if (double.IsPositiveInfinity(d))
        {
            mantissa = 1;
            exponent = 0;
            return false;
        }

        var r = BitConverter.DoubleToInt64Bits(d);

        var man = GetMantissa(r);
        var exp = GetExponent(r);

        const int BIAS = 1075;
        if (exp == 0)
        {
            exponent = exp - BIAS;
            mantissa = (man << 1);
        }
        else
        {
            exponent = exp - BIAS;
            mantissa = man + s_mantissa;
        }

        return true;
    }

    public static bool GetComponents(this double d, out BigInteger numerator, out BigInteger denominator)
    {
        if (double.IsNaN(d))
        {
            numerator = denominator = 0;
            return false;
        }

        if (double.IsNegativeInfinity(d))
        {
            numerator = -1;
            denominator = 0;
            return false;
        }

        if (double.IsPositiveInfinity(d))
        {
            numerator = 1;
            denominator = 0;
            return false;
        }

        if (d == 0.0)
        {
            numerator = 0;
            denominator = 1;
            return true;
        }

        var r = BitConverter.DoubleToInt64Bits(d);

        var m = GetMantissa(r);
        var e = GetExponent(r);
        var s = GetSign(r) == 0 ? 1 : -1;

        const int BIAS = 1023;
        var re = e - BIAS;

        var exp = (((BigInteger)1) << Math.Abs(re));

        if (e == 0)
        {
            denominator = s * exp * (s_mantissa >> 1);
            numerator = m;
            return true;
        }

        if (re < 0)
        {
            denominator = s_mantissa * s * exp;
            numerator = (s_mantissa + m);
        }
        else
        {
            denominator = s_mantissa * s;
            numerator = exp * (s_mantissa + m);
        }

        return true;
    }

}
