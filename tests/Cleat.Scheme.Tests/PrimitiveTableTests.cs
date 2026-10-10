using System.IO;
using Cleat.Scheme;
using NUnit.Framework;

namespace Cleat.Scheme.Tests;

public class PrimitiveTableTests
{
    private static string FindTable()
    {
        for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir != null; dir = dir.Parent)
        {
            var p = Path.Combine(dir.FullName, "src", "scheme", "boot", "primitives.ss");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    [Test]
    public void CheckedInTableIsUpToDate()
    {
        var path = FindTable();
        Assert.That(path, Is.Not.Null, "primitives.ss not found");
        Assert.That(File.ReadAllText(path), Is.EqualTo(Cleat.PrimGen.Program.Generate()),
          "src/scheme/boot/primitives.ss 已过期：运行 primgen（dotnet build 会自动生成）");
    }
}

public class UnsafePrimitivesTests
{
    [Test]
    public void Fixnums()
    {
        Assert.That(Unsafe.FxAdd(int.MaxValue, 1), Is.EqualTo(int.MinValue));       // 不检查溢出
        Assert.That(Unsafe.FxNeg(5), Is.EqualTo(-5));
        Assert.That(Unsafe.FxSub(5, 7), Is.EqualTo(-2));
        Assert.That(Unsafe.FxDiv(-7, 2), Is.EqualTo(-3));                          // 截断除法（与旧系统一致）
        Assert.That(Unsafe.FxMod(-7, 2), Is.EqualTo(-1));
        Assert.That(Unsafe.FxShl(1, 4), Is.EqualTo(16));
        Assert.That(Unsafe.FxShr(-16, 2), Is.EqualTo(-4));
        Assert.That(Unsafe.FxEven(4), Is.SameAs(RT.True));
        Assert.That(Unsafe.FxOdd(4), Is.SameAs(RT.False));
        Assert.That(Unsafe.FxLt(1, 2), Is.SameAs(RT.True));
    }

    [Test]
    public void FlonumsPairsVectors()
    {
        Assert.That(Unsafe.FlDiv(1.0, 4.0), Is.EqualTo(0.25));
        Assert.That(Unsafe.FlNeg(1.5), Is.EqualTo(-1.5));
        Assert.That(Unsafe.Car(new Cons(1, 2)), Is.EqualTo(1));
        Assert.Throws<System.InvalidCastException>(() => Unsafe.Car("x"));     // 不做类型检查：直接抛 .NET 异常
        var v = new object[] { 1, 2 };
        Unsafe.VectorSet(v, 0, "a");
        Assert.That(Unsafe.VectorRef(v, 0), Is.EqualTo("a"));
        var bv = new byte[] { 1, 2 };
        Unsafe.ByteVectorSet(bv, 1, 200);
        Assert.That(Unsafe.ByteVectorRef(bv, 1), Is.EqualTo(200));
    }

    [Test]
    public void RegisteredWithInlineTemplates()
    {
        var o = PrimitiveRegistry.All["$fx+"][0];
        Assert.That(o.Attribute.Inline, Is.EqualTo("$fx+"));
        Assert.That(o.Attribute.Flags.HasFlag(PrimitiveFlags.Unsafe), Is.True);
        Assert.That(PrimitiveRegistry.All["car"][0].Attribute.Inline, Is.EqualTo("car"));
    }
}
