using System.Linq;
using System.Text;
using Cleat.Scheme;
using NUnit.Framework;

namespace Cleat.Scheme.Tests;

public class SymbolTests
{
    [Test]
    public void InternReturnsSameObject()
    {
        Assert.That(Symbol.Intern("car"), Is.SameAs(Symbol.Intern("car")));
        Assert.That(Symbol.Intern("car"), Is.Not.SameAs(Symbol.Intern("cdr")));
    }

    [Test]
    public void InternIsThreadSafe()
    {
        var results = Enumerable.Range(0, 64).AsParallel().Select(_ => Symbol.Intern("concurrent-sym")).ToArray();
        Assert.That(results.Distinct().Count(), Is.EqualTo(1));
    }

    [Test]
    public void GensymIsUniqueAndReinternable()
    {
        var g1 = Symbol.Gensym("x");
        var g2 = Symbol.Gensym("x");
        Assert.That(g1, Is.Not.SameAs(g2));
        Assert.That(g1.Name, Does.StartWith("g$x$").And.EndWith("$" + Symbol.TickString));
        // 从 dll 重新加载时按名字驻留，必须得到同一个对象
        Assert.That(Symbol.Intern(g1.Name), Is.SameAs(g1));
    }

    [Test]
    public void DeterministicGensymUsesScopeAndCounter()
    {
        var saved = SymbolPrimitives.GensymScope();
        RuntimeOptions.DeterministicGensym = true;
        try
        {
            string[] Run()
            {
                SymbolPrimitives.GensymScope(Cons.FromArray(Symbol.Intern("rnrs"), Symbol.Intern("base")));
                var outer = SymbolPrimitives.GensymScope();
                var a = Symbol.Gensym("x").Name;
                // 嵌套的库展开：进入另一个作用域再恢复，外层计数接着走；库名中的 $ 改写为 _
                SymbolPrimitives.GensymScope(Cons.FromArray(Symbol.Intern("example"), Symbol.Intern("$lib")));
                var inner = Symbol.Gensym().Name;
                SymbolPrimitives.GensymScope(outer);
                var b = Symbol.Gensym().Name;
                return new[] { a, inner, b };
            }
            var first = Run();
            Assert.That(first, Is.EqualTo(new[] { "g$x$1$rnrs.base", "g$gen1$example._lib", "g$gen2$rnrs.base" }));
            Assert.That(Run(), Is.EqualTo(first));
            SymbolPrimitives.GensymScope(RT.False);
            Assert.That(Symbol.Gensym("y").Name, Does.Match(@"^g\$y\$\d+\$%top$"));
            Assert.That(Symbol.Intern("g$x$1$rnrs.base").Ungensym(), Is.SameAs(Symbol.Intern("x")));
        }
        finally
        {
            RuntimeOptions.DeterministicGensym = false;
            SymbolPrimitives.GensymScope(saved);
        }
        Assert.That(Symbol.Gensym("z").Name, Does.EndWith("$" + Symbol.TickString));
    }

    [Test]
    public void AnonymousGensymFormat()
    {
        Assert.That(Symbol.Gensym().Name, Does.Match(@"^g\$gen\d+\$"));
    }

    [Test]
    public void UngensymFollowsLegacyRule()
    {
        Assert.That(Symbol.Gensym("foo").Ungensym(), Is.SameAs(Symbol.Intern("foo")));
        Assert.That(Symbol.Intern("plain").Ungensym(), Is.SameAs(Symbol.Intern("plain")));
        Assert.That(Symbol.UngensymName("g$a$b"), Is.EqualTo("a"));
        Assert.That(Symbol.UngensymName("a$"), Is.EqualTo("a$"));
    }

    [Test]
    public void GensymOfGensymUsesUndecoratedName()
    {
        var g = Symbol.Gensym("loop");
        var gg = (Symbol)SymbolPrimitives.Gensym(g);
        Assert.That(gg.Ungensym(), Is.SameAs(Symbol.Intern("loop")));
    }
}

public class ValueSlotTests
{
    [Test]
    public void ReadWriteValueSlot()
    {
        var s = Symbol.Gensym("loc");
        Assert.That(SymbolPrimitives.IsSymbolBound(s), Is.SameAs(RT.False));
        SymbolPrimitives.SetSymbolValue(s, 42);
        Assert.That(SymbolPrimitives.SymbolValue(s), Is.EqualTo(42));
        Assert.That(SymbolPrimitives.IsSymbolBound(s), Is.SameAs(RT.True));
        SymbolPrimitives.RemoveLocation(s);
        Assert.That(s.IsBound, Is.False);
    }

    [Test]
    public void UnboundReferenceRaisesUndefinedError()
    {
        var s = Symbol.Gensym("nowhere");
        var e = Assert.Throws<SchemeRuntimeError>(() => SymbolPrimitives.SymbolValue(s));
        Assert.That(e.Kind, Is.EqualTo("undefined-error"));
        Assert.That(e.Who, Is.EqualTo("nowhere"));
    }

    [Test]
    public void SetReturnsUnspecified()
    {
        Assert.That(SymbolPrimitives.SetSymbolValue(Symbol.Gensym(), 1), Is.SameAs(Unspecified.Instance));
    }

    [Test]
    public void NonSymbolIsAssertionViolation()
    {
        var e = Assert.Throws<SchemeRuntimeError>(() => SymbolPrimitives.SymbolValue("not-a-symbol"));
        Assert.That(e.Kind, Is.EqualTo("assertion-violation"));
    }
}

public class StringSymbolTests
{
    [Test]
    public void StringAndMutableStringToSymbol()
    {
        Assert.That(SymbolPrimitives.StringToSymbol("abc"), Is.SameAs(Symbol.Intern("abc")));
        Assert.That(SymbolPrimitives.StringToSymbol(new StringBuilder("abc")), Is.SameAs(Symbol.Intern("abc")));
        Assert.That(SymbolPrimitives.SymbolToString(Symbol.Intern("abc")), Is.EqualTo("abc"));
    }
}

public class ValueTests
{
    [Test]
    public void Booleans()
    {
        Assert.That(RT.IsTrue(RT.False), Is.False);
        Assert.That(RT.IsTrue(RT.True), Is.True);
        Assert.That(RT.IsTrue(null), Is.True);              // '() 为真
        Assert.That(RT.IsTrue(0), Is.True);
        Assert.That(RT.IsTrue(false), Is.False);            // 任何装箱的 false 都为假
        Assert.That(RT.Bool(true), Is.SameAs(RT.True));
    }

    [Test]
    public void SmallIntBoxCache()
    {
        Assert.That(RT.Box(7), Is.SameAs(RT.Box(7)));
        Assert.That(RT.Box(-1024), Is.SameAs(RT.Box(-1024)));
        Assert.That(RT.Box(100000), Is.EqualTo(100000));
    }

    [Test]
    public void ConsFromListAndEnumerate()
    {
        var l = Cons.FromArray(1, 2, 3);
        Assert.That(l.ToArray(), Is.EqualTo(new object[] { 1, 2, 3 }));
        Assert.That(Cons.FromArray(), Is.Null);
        var improper = new Cons(1, 2);
        Assert.That(improper.ToArray(), Is.EqualTo(new object[] { 1 }));
    }

    [Test]
    public void MultipleValuesArityCheck()
    {
        var mv = new MultipleValues(1, 2);
        Assert.That(mv.ToArray(2), Has.Length.EqualTo(2));
        var e = Assert.Throws<SchemeRuntimeError>(() => mv.ToArray(3));
        Assert.That(e.Kind, Is.EqualTo("assertion-violation"));
    }

    [Test]
    public void SingletonsPrintNicely()
    {
        Assert.That(Unspecified.Instance.ToString(), Is.EqualTo("#<unspecified>"));
        Assert.That(new SchemeChar(0x1F600).ToString(), Is.EqualTo("\U0001F600"));
    }

    [Test]
    public void PrimitivesAreAnnotated()
    {
        var names = typeof(SymbolPrimitives).GetMethods()
          .SelectMany(m => m.GetCustomAttributes(typeof(PrimitiveAttribute), false).Cast<PrimitiveAttribute>())
          .Select(a => a.Name).Distinct().OrderBy(n => n).ToArray();
        Assert.That(names, Is.EqualTo(new[] { "$gensym-realm", "$gensym-scope", "gensym", "remove-location", "set-symbol-value!", "string->symbol",
    "symbol->string", "symbol-bound?", "symbol-value", "ungensym" }));
    }
}
