#region License
/* Copyright (c) 2007-2016 Llewellyn Pritchard
 * All rights reserved.
 * This source code is subject to terms and conditions of the BSD License.
 * See docs/license.txt. */
#endregion

using System;
using System.Diagnostics;
using System.Globalization;
using BigInteger = System.Numerics.BigInteger;


namespace Cleat.Scheme;

public partial class Builtins
{
    [Primitive("decompose-flonum")]
    public static object DecomposeFlonum(object flonum)
    {
        double d = (double)flonum;

        BigInteger mantissa;
        BigInteger exponent;
        bool res = d.GetMantissaAndExponent(out mantissa, out exponent);

        if (res)
        {
            return Control.Values(flonum, mantissa, exponent);
        }
        else
        {
            return FALSE;
        }
    }

    // string->number 由 iron-010 以手写数字解析器重新实现（旧版依赖 gppg 生成的解析器）

    [Primitive("inexact=?", Flags = PrimitiveFlags.Pure)]
    public static object InexactEqual(object a, object b)
    {
        return GetBool(ConvertToComplex(a) == ConvertToComplex(b));
    }

    public static int ExactCompare(object a, object b)
    {
        NumberClass f = GetNumberClass(a);
        NumberClass s = GetNumberClass(b);

        NumberClass effective = f & s;

        switch (effective)
        {
            case NumberClass.Integer:
                return ((int)a).CompareTo((int)b);
            case NumberClass.BigInteger:
                return ConvertToBigInteger(a).CompareTo(ConvertToBigInteger(b));
            case NumberClass.Rational:
                RationalParts(a, out var n1, out var d1);
                RationalParts(b, out var n2, out var d2);
                return CompareRationals(n1, d1, n2, d2);

            default:
                AssertionViolation("exact-compare", "not exact", a, b);
                // never reached
                return 0;
        }
    }

    public static int InexactCompare(object a, object b)
    {
        NumberClass f = GetNumberClass(a);
        NumberClass s = GetNumberClass(b);

        NumberClass effective = f & s;

        switch (effective)
        {
            case NumberClass.Real:
                return ((double)a).CompareTo(b);
            default:
                AssertionViolation("inexact-compare", "not a real", a, b);
                // never reached
                return 0;
        }
    }

    /// <summary>精确有理数的分子与分母（分母为正）；整数的分母为 1，不构造 Fraction（构造要求最大公约数）。</summary>
    private static void RationalParts(object o, out BigInteger n, out BigInteger d)
    {
        switch (o)
        {
            case int i: n = i; d = BigInteger.One; return;
            case BigInteger b: n = b; d = BigInteger.One; return;
            case Fraction f: n = f.Numerator; d = f.Denominator; return;
            default: throw new InvalidCastException("not an exact rational: " + o);
        }
    }

    /// <summary>
    /// n1/d1 与 n2/d2（分母为正）比较，-1 / 0 / 1（iron-059）。符号不同、分母相同时不必相乘，否则交叉相乘。
    /// 试过先按位数估计、在 63 位内用 Int128 相乘：小分数时 GetBitLength 每次约 8ns，四次比交叉相乘（约 20ns）还贵，没有采用。
    /// </summary>
    public static int CompareRationals(BigInteger n1, BigInteger d1, BigInteger n2, BigInteger d2)
    {
        int s1 = n1.Sign, s2 = n2.Sign;
        if (s1 != s2) return s1 < s2 ? -1 : 1;
        if (s1 == 0) return 0;
        if (d1 == d2) return n1.CompareTo(n2);
        return (n1 * d2).CompareTo(n2 * d1);
    }

    /// <summary>RealCompare 的结果：a、b 有 NaN，无法比较。</summary>
    public const int Unordered = 2;
    /// <summary>RealCompare 的结果：a 或 b 不是实数。</summary>
    public const int NotReal = 3;

    /// <summary>
    /// 两个实数的比较（iron-059，数的比较的通用路径）：-1 / 0 / 1；有 NaN 为 <see cref="Unordered"/>，有非实数为 <see cref="NotReal"/>。
    /// 精确数与 flonum：±∞ 大于 / 小于一切精确数；有限的 flonum 精确转换为分数后比较。
    /// 原来先把两边都转为 flonum，超出 double 范围的精确数变成 ∞，(&lt; (expt 2 2000) +inf.0) 得 #f。
    /// </summary>
    public static int RealCompare(object a, object b)
    {
        if (a is double x)
        {
            if (b is double y) return double.IsNaN(x) || double.IsNaN(y) ? Unordered : x.CompareTo(y);
            if (!IsExactReal(b)) return NotReal;
            int c = CompareExactWithDouble(b, x);
            return c == Unordered ? c : -c;
        }
        if (b is double d) return IsExactReal(a) ? CompareExactWithDouble(a, d) : NotReal;
        // 精确数之间按类型直接比较，不经 ExactCompare（其中的类型分派比比较本身还贵）
        switch (a)
        {
            case int i:
                switch (b)
                {
                    case int j: return i.CompareTo(j);
                    case BigInteger y: return -y.CompareTo((long)i);
                    case Fraction g: return -CompareFractionWithInteger(g, i);
                }
                break;
            case BigInteger z:
                switch (b)
                {
                    case int j: return z.CompareTo((long)j);
                    case BigInteger y: return z.CompareTo(y);
                    case Fraction g: return -CompareFractionWithInteger(g, z);
                }
                break;
            case Fraction f:
                switch (b)
                {
                    case int j: return CompareFractionWithInteger(f, j);
                    case BigInteger y: return CompareFractionWithInteger(f, y);
                    case Fraction g: return CompareRationals(f.Numerator, f.Denominator, g.Numerator, g.Denominator);
                }
                break;
        }
        return NotReal;
    }

    /// <summary>n/den 与整数 x 比较：比较 n 与 x·den（den &gt; 0）。</summary>
    private static int CompareFractionWithInteger(Fraction f, BigInteger x) => f.Numerator.CompareTo(x * f.Denominator);

    private static bool IsExactReal(object o) => o is int || o is BigInteger || o is Fraction;

    private static int CompareExactWithDouble(object e, double d)
    {
        if (double.IsNaN(d)) return Unordered;
        if (double.IsPositiveInfinity(d)) return -1;
        if (double.IsNegativeInfinity(d)) return 1;
        if (e is int i) return ((double)i).CompareTo(d);   // int 转 double 精确
        if (e is BigInteger b)
        {
            // 与 ⌊d⌋ 比较（double 转 BigInteger 对整数值精确），不经分数：分数要构造大分子并求最大公约数
            double fl = Math.Floor(d);
            int c = b.CompareTo(new BigInteger(fl));
            if (c != 0) return c;
            return fl == d ? 0 : -1;   // b = ⌊d⌋ < d
        }
        if (e is Fraction f) return CompareFractionWithDouble(f.Numerator, f.Denominator, d);
        return Math.Sign(ExactCompare(e, (Fraction)d));
    }

    /// <summary>
    /// n/den（den &gt; 0）与有限的 d 比较，不构造分数（原来经 (Fraction)d：GetComponents 加求最大公约数）。
    /// 先比近似值：n、den 都在 53 位内时 n/den 的 IEEE 除法是正确舍入的，舍入保序，结果 ≠ d 即可定序；
    /// 否则或相等时精确比较：d = m·2^k，比较 n·2^(-k) 与 m·den。
    /// </summary>
    private static int CompareFractionWithDouble(BigInteger n, BigInteger den, double d)
    {
        if (n.GetBitLength() <= 53 && den.GetBitLength() <= 53)
        {
            double r = (double)n / (double)den;
            if (r != d) return r < d ? -1 : 1;
        }
        long bits = BitConverter.DoubleToInt64Bits(d);
        int exp = (int)((bits >> 52) & 0x7FF);
        long frac = bits & 0xFFFFFFFFFFFFFL;
        BigInteger m = exp == 0 ? frac : frac | (1L << 52);
        if (bits < 0) m = -m;
        int k = (exp == 0 ? 1 : exp) - 1075;
        return k >= 0 ? n.CompareTo((m * den) << k) : (n << -k).CompareTo(m * den);
    }

    private static bool IsExact(object obj)
    {
        return obj is int ||
               obj is BigInteger ||
               obj is Fraction ||
               obj is ComplexFraction;
    }

    #region math

    [Primitive("fx+internal", Flags = PrimitiveFlags.Pure, Sig = "(fixnum fixnum) -> (integer)")]
    public static object FxPlusInternal(object ao, object bo)
    {
        int a = (int)ao, b = (int)bo;
        long r = (long)a + b;
        int rr = (int)r;
        if (r != rr)
        {
            return FALSE;
        }
        return RT.Box(rr);
    }

    [Primitive("fx-internal", Flags = PrimitiveFlags.Pure, Sig = "(fixnum fixnum) -> (integer)")]
    public static object FxMinusInternal(object ao, object bo)
    {
        int a = (int)ao, b = (int)bo;
        long r = (long)a - b;
        int rr = (int)r;
        if (r != rr)
        {
            return FALSE;
        }
        return RT.Box(rr);
    }

    [Primitive("fx*internal", Flags = PrimitiveFlags.Pure, Sig = "(fixnum fixnum) -> (integer)")]
    public static object FxMultiplyInternal(object ao, object bo)
    {
        int a = (int)ao, b = (int)bo;
        long r = (long)a * b;
        int rr = (int)r;
        if (r != rr)
        {
            return FALSE;
        }
        return RT.Box(rr);
    }

    [Primitive("fxarithmetic-shift-left-internal", Flags = PrimitiveFlags.Pure, Sig = "(fixnum fixnum) -> (integer)")]
    public static object FxShiftLeftInternal(object ao, object bo)
    {
        int a = (int)ao, b = (int)bo;
        long r = (long)a << b;
        int rr = (int)r;
        if (r != rr)
        {
            return FALSE;
        }
        return RT.Box(rr);
    }

    private enum NumberClass
    {
        Complex = 1,
        Real = 2 | Complex,
        Rational = 4 | Real,
        BigInteger = 8 | Rational,
        Integer = 16 | BigInteger,
        NotANumber = 0
    }

    private static NumberClass GetNumberClass(object obj)
    {
        if (obj is int)
        {
            return NumberClass.Integer;
        }
        else if (obj is double)
        {
            return NumberClass.Real;
        }
        else if (obj is BigInteger)
        {
            return NumberClass.BigInteger;
        }
        else if (obj is Fraction)
        {
            return NumberClass.Rational;
        }
        else if (obj is Complex64 || obj is ComplexFraction)
        {
            return NumberClass.Complex;
        }
        else
        {
            RaiseNumberTypeNotSupported();
            return NumberClass.NotANumber;
        }
    }

    private static int ConvertToInteger(object o)
    {
        if (o is int)
        {
            return (int)o;
        }
        RaiseNumberTypeNotSupported();
        return default(int);
    }

    private static void RaiseNumberTypeNotSupported()
    {
        throw new NotSupportedException("number type not supported");
    }

    protected internal static BigInteger ConvertToBigInteger(object o)
    {
        if (o is int)
        {
            return (int)o;
        }
        if (o is BigInteger)
        {
            return (BigInteger)o;
        }
        RaiseNumberTypeNotSupported();
        return default(int);
    }

    private static Fraction ConvertToRational(object o)
    {
        if (o is int)
        {
            return (int)o;
        }
        if (o is Fraction)
        {
            return (Fraction)o;
        }
        if (o is BigInteger)
        {
            return new Fraction((BigInteger)o, 1);
        }
        if (o is double)
        {
            return (Fraction)(double)o;
        }
        RaiseNumberTypeNotSupported();
        return default(int);
    }

    private static double ConvertToReal(object o)
    {
        return SafeConvert(o);
    }

    private static Complex64 ConvertToComplex(object o)
    {
        if (o is Complex64)
        {
            return (Complex64)o;
        }
        else if (o is ComplexFraction)
        {
            return (ComplexFraction)o;
        }
        else
        {
            return Complex64.MakeReal(ConvertToReal(o));
        }
    }

    private static ComplexFraction ConvertToComplexFraction(object o)
    {
        if (o is ComplexFraction)
        {
            return (ComplexFraction)o;
        }
        else if (o is int)
        {
            return new ComplexFraction((int)o);
        }
        else if (o is BigInteger)
        {
            return new ComplexFraction((BigInteger)o);
        }
        else
        {
            return new ComplexFraction((Fraction)o);
        }
    }

    [Primitive("generic+", Flags = PrimitiveFlags.Pure)]
    public static object Add(object first, object second)
    {
        NumberClass f = GetNumberClass(first);
        NumberClass s = GetNumberClass(second);

        NumberClass effective = f & s;

        switch (effective)
        {
            case NumberClass.Integer:
                {
                    long result = (long)ConvertToInteger(first) + ConvertToInteger(second);
                    int iresult = (int)result;
                    if (result != iresult)
                    {
                        return (BigInteger)result;
                    }
                    else
                    {
                        return RT.Box(iresult);
                    }
                }
            case NumberClass.BigInteger:
                return ToIntegerIfPossible(ConvertToBigInteger(first) + ConvertToBigInteger(second));
            case NumberClass.Rational:
                return IntegerIfPossible(ConvertToRational(first) + ConvertToRational(second));
            case NumberClass.Real:
                return ConvertToReal(first) + ConvertToReal(second);
            case NumberClass.Complex:
                if (IsExact(first) && IsExact(second))
                {
                    return IntegerIfPossible(ConvertToComplexFraction(first) + ConvertToComplexFraction(second));
                }
                return DoubleIfPossible(ConvertToComplex(first) + ConvertToComplex(second));
        }

        RaiseNumberTypeNotSupported();
        return null;
    }

    /// <summary>
    /// (- x)：fixnum、flonum、bignum 直接取负（(- 0.0) 为 -0.0；bignum 取负只翻转符号、共享数位，iron-059），
    /// 其余按 (* -1 x)（iron-033）。
    /// </summary>
    [Primitive("generic-negate", Flags = PrimitiveFlags.Pure)]
    public static object Negate(object x) => x switch
    {
        int i when i != int.MinValue => RT.Box(-i),
        double d => -d,
        BigInteger b => ToIntegerIfPossible(-b),
        _ => Multiply(-1, x)
    };

    [Primitive("generic-", Flags = PrimitiveFlags.Pure)]
    public static object Subtract(object first, object second)
    {
        NumberClass f = GetNumberClass(first);
        NumberClass s = GetNumberClass(second);

        NumberClass effective = f & s;

        switch (effective)
        {
            case NumberClass.Integer:
                {
                    long result = (long)ConvertToInteger(first) - ConvertToInteger(second);
                    int iresult = (int)result;
                    if (result != iresult)
                    {
                        return (BigInteger)result;
                    }
                    else
                    {
                        return RT.Box(iresult);
                    }
                }
            case NumberClass.BigInteger:
                return ToIntegerIfPossible(ConvertToBigInteger(first) - ConvertToBigInteger(second));
            case NumberClass.Rational:
                return IntegerIfPossible(ConvertToRational(first) - ConvertToRational(second));
            case NumberClass.Real:
                return ConvertToReal(first) - ConvertToReal(second);
            case NumberClass.Complex:
                if (IsExact(first) && IsExact(second))
                {
                    return IntegerIfPossible(ConvertToComplexFraction(first) - ConvertToComplexFraction(second));
                }
                return DoubleIfPossible(ConvertToComplex(first) - ConvertToComplex(second));
        }

        RaiseNumberTypeNotSupported();
        return null;
    }

    [Primitive("generic*", Flags = PrimitiveFlags.Pure)]
    public static object Multiply(object first, object second)
    {
        NumberClass f = GetNumberClass(first);
        NumberClass s = GetNumberClass(second);

        NumberClass effective = f & s;

        switch (effective)
        {
            case NumberClass.Integer:
                {
                    long result = (long)ConvertToInteger(first) * ConvertToInteger(second);
                    int iresult = (int)result;
                    if (result != iresult)
                    {
                        return (BigInteger)result;
                    }
                    else
                    {
                        return RT.Box(iresult);
                    }
                }
            case NumberClass.BigInteger:
                return ToIntegerIfPossible(ConvertToBigInteger(first) * ConvertToBigInteger(second));
            case NumberClass.Rational:
                return IntegerIfPossible(ConvertToRational(first) * ConvertToRational(second));
            case NumberClass.Real:
                return ConvertToReal(first) * ConvertToReal(second);
            case NumberClass.Complex:
                if (IsExact(first) && IsExact(second))
                {
                    return IntegerIfPossible(ConvertToComplexFraction(first) * ConvertToComplexFraction(second));
                }
                return DoubleIfPossible(ConvertToComplex(first) * ConvertToComplex(second));
        }

        RaiseNumberTypeNotSupported();
        return null;
    }


    protected internal static object ToIntegerIfPossible(BigInteger i)
    {
        if (i.IsInt32())
        {
            return RT.Box((int)i);
        }
        else
        {
            return i;
        }
    }

    [Primitive("generic/", Flags = PrimitiveFlags.Pure)]
    public static object Divide(object first, object second)
    {
        NumberClass f = GetNumberClass(first);
        NumberClass s = GetNumberClass(second);

        NumberClass effective = f & s;

        try
        {
            switch (effective)
            {
                case NumberClass.Integer:
                case NumberClass.BigInteger:
                    return IntegerIfPossible(new Fraction(ConvertToBigInteger(first), ConvertToBigInteger(second)));
                case NumberClass.Rational:
                    return IntegerIfPossible(ConvertToRational(first) / ConvertToRational(second));
                case NumberClass.Real:
                    return ConvertToReal(first) / ConvertToReal(second);
                case NumberClass.Complex:
                    if (IsExact(first) && IsExact(second))
                    {
                        return IntegerIfPossible(ConvertToComplexFraction(first) / ConvertToComplexFraction(second));
                    }
                    return DoubleIfPossible(ConvertToComplex(first) / ConvertToComplex(second));
            }
        }
        catch (DivideByZeroException)
        {
            return AssertionViolation("/", "divide by zero", first);
        }

        RaiseNumberTypeNotSupported();
        return null;
    }

    [Primitive("generic-div", Flags = PrimitiveFlags.Pure)]
    public static object Div(object first, object second)
    {
        NumberClass f = GetNumberClass(first);
        NumberClass s = GetNumberClass(second);

        NumberClass effective = f & s;

        try
        {
            switch (effective)
            {
                case NumberClass.Integer:
                    {
                        long ff = ConvertToInteger(first);
                        long ss = ConvertToInteger(second);
                        var ffa = Math.Abs(ff);
                        var ssa = Math.Abs(ss);
                        long r = ffa / ssa;
                        if (ff < 0 && ffa != ssa * r)
                        {
                            r++;
                        }
                        if (ff < 0 ^ ss < 0)
                        {
                            r = -r;
                        }
                        return ToIntegerIfPossible(r);
                    }
                case NumberClass.BigInteger:
                    {
                        var ff = ConvertToBigInteger(first);
                        var ss = ConvertToBigInteger(second);
                        var ffa = BigInteger.Abs(ff);
                        var ssa = BigInteger.Abs(ss);
                        var r = ffa / ssa;
                        if (ff.Sign < 0 && ffa != ssa * r)
                        {
                            r++;
                        }
                        if ((ff.Sign < 0) ^ (ss.Sign < 0))
                        {
                            r = -r;
                        }
                        return ToIntegerIfPossible(r);
                    }
                case NumberClass.Rational:
                    {
                        var ff = ConvertToRational(first);
                        var ss = ConvertToRational(second);
                        var ffa = ff.Abs();
                        var ssa = ss.Abs();
                        var r = ffa / ssa;
                        var br = r.Numerator / r.Denominator;

                        if (ff < 0 && r.Numerator != r.Denominator * br)
                        {
                            br++;
                        }
                        if (ff < 0 ^ ss < 0)
                        {
                            br = -br;
                        }
                        return IntegerIfPossible(br);
                    }
                case NumberClass.Real:
                    {
                        var ff = ConvertToReal(first);
                        var ss = ConvertToReal(second);

                        // 与 fldiv 相同（iron-072 对拍 Chez）：除数为正时 floor(x1/x2)，否则 -floor(x1/-x2)；
                        // 原来取绝对值后再定符号，(div -0.0 2) 得 0.0、(mod -0.0 2) 得 -0.0
                        return ss > 0 ? Math.Floor(ff / ss) : -Math.Floor(ff / -ss);
                    }
            }
        }
        catch (DivideByZeroException)
        {
            return AssertionViolation("/", "divide by zero", first);
        }

        RaiseNumberTypeNotSupported();
        return null;
    }

    #endregion

    private static double SafeConvert(object obj)
    {
        try
        {
            if (obj is int)
            {
                return (int)obj;
            }
            if (obj is double)
            {
                return (double)obj;
            }
            if (obj is BigInteger)
            {
                return ((BigInteger)obj).ToFloat64();
            }
            if (obj is Fraction)
            {
                return ((Fraction)obj).ToDouble(null);
            }
            if (obj is Complex64)
            {
                Complex64 c = (Complex64)obj;
                if (c.Imag == 0.0)
                {
                    return c.Real;
                }
                else
                {
                    return (double)AssertionViolation(GetCaller(), "no conversion to real possible", obj);
                }
            }
            //System.Diagnostics.Trace.Assert(!(obj is string));
            return Convert.ToDouble(obj, CultureInfo.InvariantCulture);
        }
        catch (OverflowException)
        {
            var i = (BigInteger)obj;
            return i < 0 ? double.NegativeInfinity : double.PositiveInfinity;
        }
    }

    /// <summary>
    /// 整数平方根 (s, r)：s = ⌊√n⌋，r = n − s²（iron-062，参照 Chez s/5_3.ss 的 exact-integer-sqrt）。
    /// Zimmermann 的 Karatsuba 平方根（INRIA RR-3805）：n &lt; 2^52 时用 Math.Sqrt 取整再修正；
    /// 否则 b = ⌊(位长 + 1) / 4⌋，递归求高位 n >> 2b 的 (s', r')，一次除法 (r'·2^b + n 的 [b, 2b) 位) ÷ 2s' 得 (q, u)，
    /// s = s'·2^b + q，r = u·2^b + n 的低 b 位 − q²；r &lt; 0 时 s 减 1、r 加 2s − 1。代价与一次乘法同阶。
    /// 原来的牛顿迭代从 n/2 起步，2000 位的数要上千次大数除法（29ms 对 Chez 的 8µs）。
    /// </summary>
    internal static (BigInteger S, BigInteger R) SqrtRem(BigInteger n)
    {
        if (n < (BigInteger.One << 52))
        {
            long v = (long)n;
            long q = (long)Math.Sqrt(v);
            while (q * q > v) q--;
            while ((q + 1) * (q + 1) <= v) q++;
            return (q, v - q * q);
        }
        int b = (int)((n.GetBitLength() + 1) >> 2);
        var (s1, r1) = SqrtRem(n >> (2 * b));
        BigInteger mask = (BigInteger.One << b) - 1;
        BigInteger mid = (n >> b) & mask;
        var (q2, u) = BigInteger.DivRem((r1 << b) + mid, s1 << 1);
        BigInteger s = (s1 << b) + q2;
        BigInteger r = (u << b) + (n & mask) - q2 * q2;
        if (r.Sign < 0)
        {
            r += (s << 1) - 1;
            s -= 1;
        }
        return (s, r);
    }

    private static BigInteger ToBigIntegerForSqrt(object num) => num is BigInteger b ? b : new BigInteger((int)num);

    /// <summary>(bignum-sqrt n)：n 是完全平方数时为精确的平方根，否则为 flonum。</summary>
    [Primitive("bignum-sqrt", Flags = PrimitiveFlags.Pure)]
    public static object SqrtBigInteger(object num)
    {
        BigInteger x = ToBigIntegerForSqrt(num);
        if (x <= 1) return ToIntegerIfPossible(x);
        var (s, r) = SqrtRem(x);
        return r.IsZero ? ToIntegerIfPossible(s) : Math.Sqrt(x.ToFloat64());
    }

    /// <summary>(bignum-sqrt-exact n) → (values s r)，n 为非负精确整数（fixnum 或 bignum）。</summary>
    [Primitive("bignum-sqrt-exact", Flags = PrimitiveFlags.Pure)]
    public static object ExactSqrtBigInteger(object num)
    {
        BigInteger x = ToBigIntegerForSqrt(num);
        var (s, r) = SqrtRem(x);
        return Values(ToIntegerIfPossible(s), ToIntegerIfPossible(r));
    }

    private static object DoubleIfPossible(object res)
    {
        if (res is Complex64)
        {
            var c = (Complex64)res;
            if (c.Imag == 0)
            {
                return c.Real;
            }
        }
        return res;
    }

    // improve actually
    private static object IntegerIfPossible(object res)
    {
        if (res is BigInteger)
        {
            return ToIntegerIfPossible((BigInteger)res);
        }
        else if (res is Fraction)
        {
            Fraction f = (Fraction)res;
            if (f.Denominator == 1)
            {
                return ToIntegerIfPossible(f.Numerator);
            }
        }
        else if (res is ComplexFraction)
        {
            ComplexFraction cf = (ComplexFraction)res;
            if (cf.Imag == 0)
            {
                return IntegerIfPossible(cf.Real);
            }
        }
        return res;
    }
}
