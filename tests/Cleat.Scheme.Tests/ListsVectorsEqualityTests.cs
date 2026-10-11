using System.Linq;
using Cleat.Scheme;
using NUnit.Framework;

namespace Cleat.Scheme.Tests;

public class ListsVectorsEqualityTests
{
    [Test]
    public void PairsAndLists()
    {
        var p = (Cons)Builtins.Cons(1, 2);
        Assert.That(Builtins.Car(p), Is.EqualTo(1));
        Assert.That(Builtins.Cdr(p), Is.EqualTo(2));
        Assert.That(Builtins.IsPair(p), Is.SameAs(RT.True));
        Assert.That(Builtins.IsNull(EmptyList.Instance), Is.SameAs(RT.True));   // '() 是 EmptyList（cleat-058）
        Assert.That(Builtins.IsNull(null), Is.SameAs(RT.False));
        Assert.That(Builtins.IsNil(null), Is.SameAs(RT.True));
        Assert.That(Builtins.IsNil(EmptyList.Instance), Is.SameAs(RT.False));
        Assert.That(Builtins.IsNull(p), Is.SameAs(RT.False));
        Assert.Throws<SchemeRuntimeError>(() => Builtins.Car(null));
    }

    [Test]
    public void ListPrimAllArities()
    {
        var lp = new PrimitiveProcedure("list-prim", PrimitiveRegistry.All["list-prim"]);
        Assert.That(lp.Invoke0(), Is.SameAs(EmptyList.Instance));
        Assert.That(((Cons)lp.Invoke3(1, 2, 3)).ToArray(), Is.EqualTo(new object[] { 1, 2, 3 }));
        Assert.That(((Cons)lp.InvokeN(new object[] { 1, 2, 3, 4, 5 })).Count(), Is.EqualTo(5));
        Assert.That(((Cons)lp.InvokeN(Enumerable.Range(0, 9).Cast<object>().ToArray())).Count(), Is.EqualTo(9));
    }

    [Test]
    public void AppendAndReverseBang()
    {
        var a = Builtins.Append(Cons.FromArray(1, 2), Cons.FromArray(3));
        Assert.That(((Cons)a).ToArray(), Is.EqualTo(new object[] { 1, 2, 3 }));
        var r = Builtins.NReverse(Cons.FromArray(1, 2, 3));
        Assert.That(((Cons)r).ToArray(), Is.EqualTo(new object[] { 3, 2, 1 }));
    }

    [Test]
    public void Vectors()
    {
        Assert.That(Builtins.Vector(1, 2), Is.EqualTo(new object[] { 1, 2 }));
        Assert.That(Builtins.VectorAppend(new object[] { 1 }, new object[] { 2, 3 }), Is.EqualTo(new object[] { 1, 2, 3 }));
        var odd = new Fn(a => RT.Bool((int)a[0] % 2 == 1));
        Assert.That(Builtins.VectorFilter(odd, new object[] { 1, 2, 3 }), Is.EqualTo(new object[] { 1, 3 }));
        Assert.That(Builtins.ListToVector(Cons.FromArray(1, 2)), Is.EqualTo(new object[] { 1, 2 }));
    }

    [Test]
    public void EqAndEqv()
    {
        var s = Symbol.Intern("a");
        Assert.That(Builtins.IsEqual(s, Symbol.Intern("a")), Is.SameAs(RT.True));
        Assert.That(Builtins.IsEqualValue(100000, 100000), Is.SameAs(RT.True));          // eqv? 比较数值
        Assert.That(Builtins.IsEqualValue(1.5, 1.5), Is.SameAs(RT.True));
        Assert.That(Builtins.IsEqualValue(1, 1.0), Is.SameAs(RT.False));                 // 精确性不同
        Assert.That(Builtins.IsEqualValue(new SchemeChar(65), new SchemeChar(65)), Is.SameAs(RT.True));
        var big = Builtins.Add(int.MaxValue, 1);
        Assert.That(Builtins.IsEqualValue(big, Builtins.Add(int.MaxValue, 1)), Is.SameAs(RT.True));
        Assert.That(Builtins.IsEqualValue(new Cons(1, 2), new Cons(1, 2)), Is.SameAs(RT.False));
    }

    [Test]
    public void Not()
    {
        Assert.That(Builtins.Not(RT.False), Is.SameAs(RT.True));
        Assert.That(Builtins.Not(null), Is.SameAs(RT.True));                 // #nil 为假（cleat-058）
        Assert.That(Builtins.Not(EmptyList.Instance), Is.SameAs(RT.False));   // '() 为真
        Assert.That(Builtins.Not(0), Is.SameAs(RT.False));
    }
}
