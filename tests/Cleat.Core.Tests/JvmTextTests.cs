using System;
using System.Globalization;
using Cleat.Core.Runtime;
using NUnit.Framework;

namespace Cleat.Core.Tests;

// 浮点数的文本（JvmText，cleat-032 起按 JVM 的 Double.toString）：必须往返——读回同一个 double
public class JvmTextTests
{
    /// <summary>
    /// cleat-167：.NET 的最短往返格式在 2 的幂处给出读不回来的 16 位文本（2^-25 → 2.980232238769531E-08，解析成下方相邻的 double）；
    /// test.check 的 edn-roundtrips 属性测试随机撞到。现在退到 17 位，与 JVM 相同。
    /// </summary>
    [Test]
    public void PowersOfTwoRoundTrip()
    {
        double x = Math.Pow(2, -25);
        Assert.That(JvmText.DoubleToString(x), Is.EqualTo("2.9802322387695312E-8"));
        for (int e = -1074; e <= 1023; e++)
        {
            double p = Math.Pow(2, e);
            if (p == 0 || double.IsInfinity(p)) continue;
            string s = JvmText.DoubleToString(p);
            Assert.That(double.Parse(s, CultureInfo.InvariantCulture), Is.EqualTo(p), "2^" + e + " printed as " + s);
        }
    }

    [Test]
    public void RandomDoublesRoundTrip()
    {
        var rnd = new Random(167);
        var bytes = new byte[8];
        for (int i = 0; i < 200_000; i++)
        {
            rnd.NextBytes(bytes);
            double d = BitConverter.ToDouble(bytes, 0);
            if (double.IsNaN(d) || double.IsInfinity(d)) continue;
            string s = JvmText.DoubleToString(d);
            Assert.That(BitConverter.DoubleToInt64Bits(double.Parse(s, CultureInfo.InvariantCulture)), Is.EqualTo(BitConverter.DoubleToInt64Bits(d)), s);
        }
    }
}
