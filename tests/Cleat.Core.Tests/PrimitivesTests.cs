using System.Linq;
using Cleat.Core.Runtime;
using clojure.lang;
using NUnit.Framework;

namespace Cleat.Core.Tests;

// cleat-011：原语表、cleat.kernel 绑定
public class PrimitivesTests
{
    [Test]
    public void EveryEntryResolvesToAStaticMethod()
    {
        foreach (var e in Primitives.Table) Assert.That(e.MethodInfo.IsStatic, Is.True, e.ToString());
        Assert.That(Primitives.Table.Select(e => e.ToString()).Distinct().Count(), Is.EqualTo(Primitives.Table.Count), "no duplicate entries");
    }

    [Test]
    public void KernelVarsInvokePrimitives()
    {
        var ns = PrimitiveRegistry.RegisterAll();
        object Call(string name, params object[] args) => ns.FindInternedVar(Symbol.intern(name)).getRawRoot() is IFn f ? f.applyTo(RT.seq(args)) : null;
        Assert.That(Call("first", RT.vector(1L, 2L)), Is.EqualTo(1L));
        Assert.That(Call("add", 1L, 2L), Is.EqualTo(3L));
        Assert.That(Call("lt", 1L, 2L), Is.EqualTo(true));
        Assert.That(Call("count", RT.list(1L, 2L, 3L)), Is.EqualTo(3L), "int return converts to long");
        Assert.That(Call("nth", RT.vector(1L, 2L), 1L), Is.EqualTo(2L), "long argument converts to int parameter");
        Assert.That(Call("list0"), Is.Null, "(list) is nil as a seq");
        Assert.That(RT.printString(Call("vector", 1L, 2L)), Is.EqualTo("[1 2]"), "params overload takes all args");
        Assert.That(Call("boolean-cast", new object[] { null }), Is.EqualTo(false));
        Assert.Throws<ArityException>(() => Call("first"));
        Assert.That(Primitives.Find(typeof(RT), "first", new[] { typeof(object) }).Inline, Is.True);
    }

}
