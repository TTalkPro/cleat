using System;

namespace Cleat.Core.Runtime;

/// <summary>
/// clojure.math 里 System.Math 没有直接对应的函数（cleat-032）：按 java.lang.Math 的语义实现（Clojure 1.11 的 clojure.math API
/// 是可移植的 Clojure 代码会用的那一套）。整数溢出抛 ArithmeticException "long overflow"（与 JVM 的消息相同）。
/// </summary>
public static class ClojureMath
{
    private static ArithmeticException Overflow() => new ArithmeticException("long overflow");

    public static long AddExact(long x, long y)
    {
        long r = unchecked(x + y);
        if (((x ^ r) & (y ^ r)) < 0) throw Overflow();
        return r;
    }

    public static long SubtractExact(long x, long y)
    {
        long r = unchecked(x - y);
        if (((x ^ y) & (x ^ r)) < 0) throw Overflow();
        return r;
    }

    public static long MultiplyExact(long x, long y)
    {
        try { return checked(x * y); }
        catch (OverflowException) { throw Overflow(); }
    }

    public static long IncrementExact(long a) => a == long.MaxValue ? throw Overflow() : a + 1;

    public static long DecrementExact(long a) => a == long.MinValue ? throw Overflow() : a - 1;

    public static long NegateExact(long a) => a == long.MinValue ? throw Overflow() : -a;

    public static long FloorDiv(long x, long y)
    {
        if (y == 0) throw new ArithmeticException("/ by zero");
        if (x == long.MinValue && y == -1) return long.MinValue;
        long q = x / y;
        if ((x % y != 0) && ((x ^ y) < 0)) q--;
        return q;
    }

    public static long FloorMod(long x, long y)
    {
        if (y == 0) throw new ArithmeticException("/ by zero");
        if (y == -1) return 0;
        long m = x % y;
        if (m != 0 && ((m ^ y) < 0)) m += y;
        return m;
    }

    /// <summary>Math.round(double)：floor(a + 1/2) 的精确版本；NaN → 0，超出 long 时饱和。</summary>
    public static long Round(double a)
    {
        if (double.IsNaN(a)) return 0;
        double f = Math.Floor(a);
        double r = (a - f) >= 0.5 ? f + 1 : f;
        if (r >= 9.2233720368547758E18) return long.MaxValue;
        if (r <= -9.2233720368547758E18) return long.MinValue;
        return (long)r;
    }

    /// <summary>Math.rint：最接近的整数值，两边一样近取偶数。</summary>
    public static double Rint(double a) => Math.Round(a, MidpointRounding.ToEven);

    public static double Signum(double d) => d == 0.0 || double.IsNaN(d) ? d : Math.CopySign(1.0, d);

    public static double ToRadians(double deg) => deg * 0.017453292519943295;

    public static double ToDegrees(double rad) => rad * 57.29577951308232;

    public static int GetExponent(double d)
    {
        long bits = BitConverter.DoubleToInt64Bits(d);
        return (int)((bits >> 52) & 0x7FF) - 1023;
    }

    public static double Ulp(double d)
    {
        if (double.IsNaN(d)) return d;
        if (double.IsInfinity(d)) return double.PositiveInfinity;
        d = Math.Abs(d);
        if (d == double.MaxValue) return Math.Pow(2, 971);
        return Math.BitIncrement(d) - d;
    }

    public static double NextUp(double d) => double.IsNaN(d) ? d : d == 0.0 ? double.Epsilon : Math.BitIncrement(d);

    public static double NextDown(double d) => double.IsNaN(d) ? d : d == 0.0 ? -double.Epsilon : Math.BitDecrement(d);

    public static double NextAfter(double start, double direction)
    {
        if (double.IsNaN(start) || double.IsNaN(direction)) return double.NaN;
        if (start == direction) return direction;
        return direction > start ? NextUp(start) : NextDown(start);
    }

    public static double Scalb(double d, long scale) => Math.ScaleB(d, (int)Math.Clamp(scale, int.MinValue, int.MaxValue));

    public static double Hypot(double x, double y) => double.Hypot(x, y);

    public static double Expm1(double x)
    {
        if (double.IsNaN(x) || x == 0.0) return x;
        if (double.IsPositiveInfinity(x)) return x;
        if (double.IsNegativeInfinity(x)) return -1.0;
        double u = Math.Exp(x);
        if (u == 1.0) return x;
        double um1 = u - 1.0;
        if (um1 == -1.0) return -1.0;
        if (double.IsInfinity(u)) return u;
        return um1 * x / Math.Log(u);
    }

    public static double Log1p(double x)
    {
        if (double.IsNaN(x) || x == 0.0) return x;
        double u = 1.0 + x;
        if (u == 1.0) return x;
        if (double.IsInfinity(u)) return Math.Log(u);
        return Math.Log(u) * x / (u - 1.0);
    }

    public static double Random() => System.Random.Shared.NextDouble();
}
