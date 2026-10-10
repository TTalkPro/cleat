using Cleat.Scheme;
using NUnit.Framework;
using BigInteger = System.Numerics.BigInteger;

namespace Cleat.Scheme.Tests;

public class NumbersTests
{
    [Test]
    public void FixnumArithmeticStaysFixnum()
    {
        Assert.That(Builtins.Add(1, 2), Is.EqualTo(3).And.InstanceOf<int>());
        Assert.That(Builtins.Subtract(1, 2), Is.EqualTo(-1));
        Assert.That(Builtins.Multiply(6, 7), Is.EqualTo(42));
    }

    [Test]
    public void OverflowPromotesToBignum()
    {
        var r = Builtins.Add(int.MaxValue, 1);
        Assert.That(r, Is.InstanceOf<BigInteger>());
        Assert.That(r.ToString(), Is.EqualTo("2147483648"));
        var m = Builtins.Multiply(int.MaxValue, int.MaxValue);
        Assert.That(m.ToString(), Is.EqualTo("4611686014132420609"));
    }

    [Test]
    public void BignumDemotesWhenSmall()
    {
        var big = Builtins.Add(int.MaxValue, 1);
        Assert.That(Builtins.Subtract(big, 1), Is.EqualTo(int.MaxValue).And.InstanceOf<int>());
    }

    [Test]
    public void ExactDivisionGivesFraction()
    {
        var r = Builtins.Divide(1, 3);
        Assert.That(r, Is.InstanceOf<Fraction>());
        Assert.That(Builtins.Multiply(r, 3), Is.EqualTo(1));
        Assert.That(Builtins.Divide(6, 3), Is.EqualTo(2).And.InstanceOf<int>());
    }

    [Test]
    public void InexactContagion()
    {
        Assert.That(Builtins.Add(1, 0.5), Is.EqualTo(1.5));
        Assert.That(Builtins.Divide(1.0, 4), Is.EqualTo(0.25));
    }

    [Test]
    public void ComplexArithmetic()
    {
        var i = Complex64.Make(0, 1);
        var r = Builtins.Multiply(i, i);
        Assert.That(r, Is.EqualTo(-1.0));
    }

    [Test]
    public void FxInternalReturnsFalseOnOverflow()
    {
        Assert.That(Builtins.FxPlusInternal(1, 2), Is.EqualTo(3));
        Assert.That(Builtins.FxPlusInternal(int.MaxValue, 1), Is.SameAs(RT.False));
        Assert.That(Builtins.FxMultiplyInternal(65536, 65536), Is.SameAs(RT.False));
        Assert.That(Builtins.FxShiftLeftInternal(1, 31), Is.SameAs(RT.False));
    }

    [Test]
    public void Comparisons()
    {
        Assert.That(Builtins.ExactCompare(1, 2), Is.LessThan(0));
        Assert.That(Builtins.ExactCompare(Builtins.Divide(1, 2), Builtins.Divide(1, 3)), Is.GreaterThan(0));
        Assert.That(Builtins.InexactEqual(1.0, 1), Is.SameAs(RT.True));
    }

    [Test]
    public void BignumSqrtAndDecompose()
    {
        var big = Builtins.Multiply(Builtins.Add(int.MaxValue, 1), Builtins.Add(int.MaxValue, 1));
        // 对应 exact-integer-sqrt：返回 (平方根, 余数) 两个值
        var sr = ((MultipleValues)Builtins.ExactSqrtBigInteger(big)).ToArray(2);
        Assert.That(sr[0].ToString(), Is.EqualTo("2147483648"));
        Assert.That(sr[1], Is.EqualTo(0));
        var parts = Builtins.DecomposeFlonum(1.5);
        Assert.That(parts, Is.InstanceOf<MultipleValues>());
    }

    // ——— 大整数与有理数转 double 的舍入（iron-059）———

    /// <summary>有限 double 的精确值 m·2^e（m 为整数）。</summary>
    private static (BigInteger m, int e) Exact(double x)
    {
        long bits = System.BitConverter.DoubleToInt64Bits(x);
        int exp = (int)((bits >> 52) & 0x7FF);
        long frac = bits & 0xFFFFFFFFFFFFFL;
        BigInteger m = exp == 0 ? frac : frac | (1L << 52);
        int e = (exp == 0 ? 1 : exp) - 1075;
        return (bits < 0 ? -m : m, e);
    }

    /// <summary>|n/d - x| 与 |n/d - y| 比较（精确）。</summary>
    private static int CompareDistance(BigInteger n, BigInteger d, double x, double y)
    {
        // |n/d - m·2^e| ∝ |n·2^k - m·d·2^(e+k)|，取公共的 2 的幂
        var (mx, ex) = Exact(x);
        var (my, ey) = Exact(y);
        int k = System.Math.Max(0, -System.Math.Min(ex, ey));
        BigInteger Dist(BigInteger m, int e) => BigInteger.Abs((n << k) - ((m * d) << (e + k)));
        return Dist(mx, ex).CompareTo(Dist(my, ey));
    }

    private static void AssertNearest(BigInteger n, BigInteger d)
    {
        double r = BigIntegers.RationalToDouble(n, d);
        Assert.That(double.IsNaN(r), Is.False);
        if (double.IsInfinity(r))
        {
            // 超过 double.MaxValue 与 2^1024 的中点
            var big = (BigInteger.One << 1024) - (BigInteger.One << 970);
            Assert.That(BigInteger.Abs(n) >= big * d, Is.True, $"{n}/{d}");
            return;
        }
        foreach (double other in new[] { System.Math.BitIncrement(r), System.Math.BitDecrement(r) })
        {
            if (double.IsInfinity(other)) continue;
            int c = CompareDistance(n, d, r, other);
            Assert.That(c <= 0, Is.True, $"{n}/{d} → {r:R}，{other:R} 更近");
            if (c == 0) Assert.That(Exact(r).m.IsEven || r == 0, Is.True, $"{n}/{d} 平局应取偶");
        }
    }

    [Test]
    public void RationalToDoubleIsCorrectlyRounded()
    {
        var rnd = new System.Random(59);
        BigInteger Random(int bits)
        {
            var bytes = new byte[bits / 8 + 2];
            rnd.NextBytes(bytes);
            bytes[^1] = 0;
            return new BigInteger(bytes) >> (bytes.Length * 8 - 8 - bits);
        }
        for (int i = 0; i < 3000; i++)
        {
            var n = Random(rnd.Next(1, 2200)) + 1;
            var d = Random(rnd.Next(1, 2200)) + 1;
            AssertNearest(rnd.Next(2) == 0 ? n : -n, d);
            AssertNearest(n, BigInteger.One);
        }
        // 边界：上溢、次正规、平局取偶
        var one = BigInteger.One;
        Assert.That(BigIntegers.RationalToDouble(one << 1024, one), Is.EqualTo(double.PositiveInfinity));
        Assert.That(BigIntegers.RationalToDouble((one << 1024) - (one << 970), one), Is.EqualTo(double.PositiveInfinity));
        Assert.That(BigIntegers.RationalToDouble((one << 1024) - (one << 970) - 1, one), Is.EqualTo(double.MaxValue));
        Assert.That(BigIntegers.RationalToDouble(one, one << 1074), Is.EqualTo(double.Epsilon));
        Assert.That(BigIntegers.RationalToDouble(one, one << 1075), Is.EqualTo(0.0));
        Assert.That(BigIntegers.RationalToDouble(3, one << 1076), Is.EqualTo(double.Epsilon));
        Assert.That(BigIntegers.RationalToDouble(-(one << 3000), one << 10), Is.EqualTo(double.NegativeInfinity));
        Assert.That(BigIntegers.RationalToDouble((one << 53) + 1, one), Is.EqualTo(9007199254740992.0));
        Assert.That(BigIntegers.RationalToDouble((one << 53) + 3, one), Is.EqualTo(9007199254740996.0));
        Assert.That(BigIntegers.RationalToDouble(one << 2000, (one << 2000) * 3), Is.EqualTo(1.0 / 3));
    }

    [Test]
    public void BignumToDoubleOutsideRange()
    {
        Assert.That(Builtins.ToInexact(BigInteger.Pow(2, 1100)), Is.EqualTo(double.PositiveInfinity));
        Assert.That(Builtins.ToInexact(-BigInteger.Pow(10, 400)), Is.EqualTo(double.NegativeInfinity));
        // 分子分母都超出 double 范围（原来得 ∞/∞ = NaN）
        var f = new Fraction(BigInteger.Pow(10, 400) + 1, BigInteger.Pow(10, 400) * 4);
        Assert.That((double)f, Is.EqualTo(0.25));
    }

    [Test]
    public void BignumRadixConversionRoundTrips()
    {
        // 参照：逐位 div/mod（原 Scheme 实现的算法）
        static string Reference(BigInteger v, int radix)
        {
            if (v.IsZero) return "0";
            var sb = new System.Text.StringBuilder();
            var a = BigInteger.Abs(v);
            while (!a.IsZero) { int d = (int)(a % radix); sb.Insert(0, (char)(d < 10 ? '0' + d : 'a' + d - 10)); a /= radix; }
            return (v.Sign < 0 ? "-" : "") + sb;
        }
        var rnd = new System.Random(59);
        foreach (int radix in new[] { 2, 8, 10, 16 })
        {
            for (int i = 0; i < 300; i++)
            {
                var bytes = new byte[rnd.Next(1, 200)];
                rnd.NextBytes(bytes);
                var v = new BigInteger(bytes);
                string text = BigIntegers.ToString(v, radix);
                Assert.That(text, Is.EqualTo(Reference(v, radix)), $"radix {radix}");
                string digits = text.TrimStart('-');
                Assert.That(BigIntegers.TryParse(digits, radix, out var back), Is.True);
                Assert.That(back, Is.EqualTo(BigInteger.Abs(v)));
                Assert.That(BigIntegers.TryParse(digits.ToUpperInvariant(), radix, out back) && back == BigInteger.Abs(v), Is.True);
            }
        }
        Assert.That(BigIntegers.TryParse("12a", 10, out _), Is.False);
        Assert.That(BigIntegers.TryParse("1012", 2, out _), Is.False);
        Assert.That(BigIntegers.TryParse("", 16, out _), Is.False);
        Assert.That(BigIntegers.TryParse("000000000000000000000000000ff", 16, out var x) && x == 255, Is.True);
        Assert.That(BigIntegers.TryParse("2222222222222222222222", 3, out var y) && y == BigInteger.Pow(3, 22) - 1, Is.True);
    }

    [Test]
    public void LargeDecimalOutputMatchesBcl()
    {
        // 分治路径（约 1000 位以上）与 BCL 的结果逐字相同；含低半全为 0、补 0 跨越多层的值
        var cases = new System.Collections.Generic.List<BigInteger>
        {
            BigInteger.Pow(10, 5000), BigInteger.Pow(10, 5000) - 1, BigInteger.Pow(10, 5000) + 1,
            BigInteger.Pow(10, 256) * BigInteger.Pow(10, 4000) + 7, BigInteger.Pow(3, 30000), -BigInteger.Pow(7, 9000),
            BigInteger.Pow(2, 3300), BigInteger.Pow(2, 3301), BigInteger.Pow(10, 1024) * 12345,
        };
        var rnd = new System.Random(59);
        for (int i = 0; i < 40; i++)
        {
            var bytes = new byte[rnd.Next(400, 12000)];
            rnd.NextBytes(bytes);
            cases.Add(new BigInteger(bytes));
        }
        foreach (var v in cases)
            Assert.That(BigIntegers.ToDecimalString(v), Is.EqualTo(v.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        // 第二轮用上已缓存的幂
        foreach (var v in cases)
            Assert.That(BigIntegers.ToString(v, 10), Is.EqualTo(v.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Test]
    public void RealCompareMatchesExactComparison()
    {
        // 参照：把 flonum 精确转为分数后比较（原来的做法）
        static int Reference(object e, double d) => System.Math.Sign(Builtins.ExactCompare(e, (Fraction)d));
        var rnd = new System.Random(59);
        BigInteger Random(int bits)
        {
            var bytes = new byte[bits / 8 + 2];
            rnd.NextBytes(bytes);
            bytes[^1] = 0;
            return new BigInteger(bytes) >> (bytes.Length * 8 - 8 - bits);
        }
        var doubles = new System.Collections.Generic.List<double> { 0.0, -0.0, 0.5, -0.5, 1.0 / 3, 0.1, 1e300, -1e300, 1e-300, double.Epsilon, -double.Epsilon, double.MaxValue, 4294967296.5 };
        for (int i = 0; i < 2000; i++)
        {
            var n = Random(rnd.Next(1, 300)) * (rnd.Next(2) == 0 ? 1 : -1);
            var den = Random(rnd.Next(1, 300)) + 1;
            object e = Builtins.Divide(n, den);   // 结果已规范化：可能是整数
            double near = Builtins.ToInexact(e) is double x ? x : 0.0;
            var ds = new System.Collections.Generic.List<double>(doubles) { near, System.Math.BitIncrement(near), System.Math.BitDecrement(near), rnd.NextDouble() * System.Math.Pow(2, rnd.Next(-1100, 1000)) };
            foreach (double d in ds)
            {
                if (double.IsInfinity(d)) continue;
                Assert.That(Builtins.RealCompare(e, d), Is.EqualTo(Reference(e, d)), $"{e} vs {d:R}");
                Assert.That(Builtins.RealCompare(d, e), Is.EqualTo(-Reference(e, d)), $"{d:R} vs {e}");
            }
        }
        // 恰好相等：分数等于 double 的精确值
        foreach (double d in new[] { 0.5, 0.375, 1e-300, double.Epsilon, 3.0 / 1024 })
            Assert.That(Builtins.RealCompare((Fraction)d, d), Is.EqualTo(0));
        Assert.That(Builtins.RealCompare(new Fraction(1, 3), double.NaN), Is.EqualTo(Builtins.Unordered));
        Assert.That(Builtins.RealCompare(new Fraction(1, 3), double.PositiveInfinity), Is.EqualTo(-1));
        Assert.That(Builtins.RealCompare(double.NegativeInfinity, new Fraction(-1, 3)), Is.EqualTo(-1));
        Assert.That(Builtins.RealCompare("x", 1.0), Is.EqualTo(Builtins.NotReal));
    }

    [Test]
    public void RationalComparisonMatchesCrossMultiplication()
    {
        // 参照：直接交叉相乘
        static int Reference(BigInteger n1, BigInteger d1, BigInteger n2, BigInteger d2) => (n1 * d2).CompareTo(n2 * d1);
        var rnd = new System.Random(59);
        BigInteger Random(int bits)
        {
            var bytes = new byte[bits / 8 + 2];
            rnd.NextBytes(bytes);
            bytes[^1] = 0;
            return new BigInteger(bytes) >> (bytes.Length * 8 - 8 - bits);
        }
        var cases = new System.Collections.Generic.List<(BigInteger, BigInteger, BigInteger, BigInteger)>();
        for (int i = 0; i < 3000; i++)
        {
            int b1 = rnd.Next(1, 140), b2 = rnd.Next(1, 140), b3 = rnd.Next(1, 140);
            // 让两边的位数之和接近：第四个数的位数取 b1+b2-b3 附近（差 -2..2），覆盖位数估计的边界
            int b4 = System.Math.Max(1, b1 + b2 - b3 + rnd.Next(-2, 3));
            var n1 = Random(b1) | (BigInteger.One << (b1 - 1));
            var d2 = Random(b2) | (BigInteger.One << (b2 - 1));
            var n2 = Random(b3) | (BigInteger.One << (b3 - 1));
            var d1 = Random(b4) | (BigInteger.One << (b4 - 1));
            if (rnd.Next(4) == 0) { n1 = -n1; n2 = -n2; }
            if (rnd.Next(8) == 0) n2 = -n2;
            cases.Add((n1, d1, n2, d2));
        }
        cases.Add((1, 3, 1, 3)); cases.Add((2, 6, 1, 3)); cases.Add((0, 1, 0, 5)); cases.Add((-1, 2, 1, 2));
        cases.Add((BigInteger.One << 62, 3, (BigInteger.One << 62) + 1, 3));   // 分母相同
        cases.Add((3, 4, 1, 1)); cases.Add((4, 3, 1, 1)); cases.Add((7, 8, 3, 4));   // 位数之和差 1 但大小相反
        foreach (var (n1, d1, n2, d2) in cases)
        {
            Assert.That(Builtins.CompareRationals(n1, d1, n2, d2), Is.EqualTo(Reference(n1, d1, n2, d2)), $"{n1}/{d1} vs {n2}/{d2}");
            Assert.That(Builtins.CompareRationals(n2, d2, n1, d1), Is.EqualTo(-Reference(n1, d1, n2, d2)));
        }
        // 经 ExactCompare：int、BigInteger、分数混合
        object[] values = { 0, 1, -1, 7, int.MaxValue, int.MinValue, BigInteger.Pow(2, 40), -BigInteger.Pow(3, 50),
            new Fraction(1, 3), new Fraction(-7, 2), new Fraction(BigInteger.Pow(2, 100) + 1, BigInteger.Pow(2, 100)), new Fraction(1, BigInteger.Pow(10, 30)) };
        foreach (var a in values)
            foreach (var b in values)
            {
                var fa = a is Fraction x ? x : a is int i ? (Fraction)i : new Fraction((BigInteger)a, 1);
                var fb = b is Fraction y ? y : b is int j ? (Fraction)j : new Fraction((BigInteger)b, 1);
                Assert.That(System.Math.Sign(Builtins.ExactCompare(a, b)), Is.EqualTo(Reference(fa.Numerator, fa.Denominator, fb.Numerator, fb.Denominator)), $"{a} vs {b}");
            }
    }
}
