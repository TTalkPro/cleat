using System.Linq;
using System.Threading;
using Cleat.Scheme;
using NUnit.Framework;

namespace Cleat.Scheme.Tests;

[NonParallelizable]
public class MiscTests
{
    [Test]
    public void ListStar()
    {
        Assert.That(((Cons)Builtins.ListStar(1, 2, Cons.FromArray(3))).ToArray(), Is.EqualTo(new object[] { 1, 2, 3 }));
        Assert.That(((Cons)Builtins.ListStar(1, 2)).cdr, Is.EqualTo(2));
        Assert.That(((Cons)Builtins.ListStar(1, new object[] { 2, 3, 4, Cons.FromArray(5) })).ToArray(), Is.EqualTo(new object[] { 1, 2, 3, 4, 5 }));
    }

    [Test]
    public void VoidAndUnspecified()
    {
        Assert.That(Builtins.Void(), Is.SameAs(Unspecified.Instance));
        Assert.That(Builtins.IsUnspecified(Builtins.Void()), Is.SameAs(RT.True));
    }

    [Test]
    public void StringSplitUsesDelimiters()
    {
        Assert.That(Builtins.StringSplit("a?b?c", "?"), Is.EqualTo(new[] { "a", "b", "c" }));
        Assert.That(Builtins.StringSplit("/x/y", "/"), Is.EqualTo(new[] { "", "x", "y" }));
        Assert.That(Builtins.StringSplit("a b"), Is.EqualTo(new[] { "a", "b" }));   // 无分隔符时按空白
    }

    [Test]
    public void StringToXml()
    {
        var x = (Cons)Builtins.StringToXml("<a k=\"v\">t<b/></a>");
        Assert.That(x.car, Is.SameAs(Symbol.Intern("a")));
        var items = x.ToArray();
        Assert.That(((Cons)items[1]).car, Is.SameAs(Symbol.Intern("k")));
        Assert.That(items[2], Is.EqualTo("t"));
        Assert.That(((Cons)items[3]).car, Is.SameAs(Symbol.Intern("b")));
    }

    [Test]
    public void WithTimeout()
    {
        Assert.That(Builtins.WithTimeout(new Fn(_ => 5), 1000), Is.EqualTo(5));
        var e = Assert.Throws<SchemeRuntimeError>(() => Builtins.WithTimeout(new Fn(_ => { Thread.Sleep(2000); return 0; }), 50));
        Assert.That(e.Message, Does.Contain("exceeded limit"));
    }

    [Test]
    public void TimeItReturnsValue()
    {
        Assert.That(Builtins.TimeIt("test", new Fn(_ => 7)), Is.EqualTo(7));
    }

    [Test]
    public void Flags()
    {
        Assert.That(Builtins.IsDebugMode(), Is.SameAs(RT.False));
        Builtins.IsDebugMode(RT.True);
        Assert.That(Builtins.IsDebugMode(), Is.SameAs(RT.True));
        Builtins.IsDebugMode(RT.False);
    }

    [Test]
    public void CommandLineAndPaths()
    {
        Builtins.CommandLineArgs = new[] { "prog", "x" };
        Assert.That(((Cons)Builtins.CommandLine()).ToArray(), Is.EqualTo(new object[] { "prog", "x" }));
        var paths = ((Cons)Builtins.GetLibraryPaths()).ToArray();
        Assert.That(paths, Does.Contain("."));
    }

    [Test]
    public void BuildscriptHelpers()
    {
        var ht = (System.Collections.Hashtable)Builtins.MakePrimlocHashtable(Cons.FromArray(new Cons(Symbol.Intern("car"), Symbol.Intern("loc"))));
        Assert.That(ht[Symbol.Intern("car")], Is.SameAs(Symbol.Intern("loc")));
        var s = Symbol.Gensym("g");
        Builtins.SetSymbolValueFast(s, 1);
        Assert.That(s.Value, Is.EqualTo(1));
        Assert.That(Helpers.LongToBignum(5L), Is.EqualTo(5).And.InstanceOf<int>());
        Assert.That(Helpers.LongToBignum(long.MaxValue).ToString(), Is.EqualTo(long.MaxValue.ToString()));
        Assert.That(Helpers.UnwrapValue(new MultipleValues(3)), Is.EqualTo(3));
    }
}
