using System;
using System.Linq;
using Cleat.Scheme;
using NUnit.Framework;

namespace Cleat.Scheme.Tests;

// 违反约定的原语，只用于验证 Scan 的检查（Scan 本测试程序集时会被发现）
public static class BadPrimitives
{
    [Primitive("bad-return")] public static int BadReturn(object a) => 0;
    [Primitive("bad-param")] public static object BadParam(string s) => s;
    [Primitive("dup")] public static object Dup1(object a) => a;
    [Primitive("dup")] public static object Dup2(object b) => b;
}

[NonParallelizable]
public class PrimitiveRegistryTests
{
    [OneTimeSetUp]
    public void Register() => PrimitiveRegistry.RegisterAll();

    private static Procedure Prim(string name) => (Procedure)Symbol.Intern(name).Value;

    [Test]
    public void AllPrimitivesRegisterIntoValueSlots()
    {
        int n = PrimitiveRegistry.RegisterAll();
        Assert.That(n, Is.EqualTo(PrimitiveRegistry.All.Count));
        foreach (var name in PrimitiveRegistry.All.Keys)
            Assert.That(Symbol.Intern(name).Value, Is.InstanceOf<PrimitiveProcedure>(), name);
    }

    [Test]
    public void OverloadsByArity()
    {
        var gensym = Prim("gensym");
        Assert.That(((Symbol)gensym.Invoke0()).Name, Does.StartWith("g$gen"));
        Assert.That(((Symbol)gensym.Invoke1("x")).Name, Does.StartWith("g$x$"));
        Assert.That(((Symbol)gensym.InvokeN(new object[] { "y" })).Name, Does.StartWith("g$y$"));
        var e = Assert.Throws<SchemeRuntimeError>(() => gensym.Invoke2(1, 2));
        Assert.That(e.Who, Is.EqualTo("gensym"));
    }

    [Test]
    public void ParamsPrimitive()
    {
        var apply = Prim("apply");
        var add = new Add2();
        Assert.That(apply.Invoke2(add, Cons.FromArray(1, 2)), Is.EqualTo(3));
        Assert.That(apply.Invoke3(add, 1, Cons.FromArray(2)), Is.EqualTo(3));
        Assert.That(apply.InvokeN(new object[] { add, 1, 2, EmptyList.Instance }), Is.EqualTo(3));
        Assert.Throws<SchemeRuntimeError>(() => apply.Invoke0());   // 至少 1 个固定参数
    }

    [Test]
    public void AliasesShareImplementation()
    {
        Assert.That(Prim("call-with-return").Invoke1(new Fn(a => ((Procedure)a[0]).Invoke1(9))), Is.EqualTo(9));
        Assert.That(Prim("call-with-return").Invoke1(new Fn(a => 8)), Is.EqualTo(8));
    }

    [Test]
    public void PrimitivePrintsWithItsName()
    {
        Assert.That(Prim("symbol-value").ToString(), Is.EqualTo("#<procedure symbol-value>"));
    }

    [Test]
    public void ScanRejectsConventionViolations()
    {
        var e = Assert.Throws<InvalidOperationException>(() => PrimitiveRegistry.Scan(typeof(BadPrimitives).Assembly));
        Assert.That(e.Message, Does.Contain("bad-return").And.Contain("must return object"));
        Assert.That(e.Message, Does.Contain("bad-param").And.Contain("must be object"));
        Assert.That(e.Message, Does.Contain("dup").And.Contain("duplicate overload"));
    }
}
