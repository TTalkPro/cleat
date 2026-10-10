// cleat-010：运行时互操作 Reflector（无 DLR）的重载决议、属性/字段/索引器、构造、泛型方法、IFn → 委托、数值转换与缓存。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Cleat.Core.Runtime;
using clojure.lang;
using NUnit.Framework;

namespace Cleat.Core.Tests;

public enum Color
{
    Red,
    Green,
    Blue,
}

/// <summary>重载决议与成员访问的夹具。</summary>
public class ReflectorFixture : IDisposable
{
    public int Count;
    public readonly string Tag = "tag";
    public static long StaticCounter;
    public const int Answer = 42;
    public string Name { get; set; } = "fixture";
    public static string StaticName { get; set; } = "static";
    public bool Disposed;

    public string Describe(object o) => "object:" + (o ?? "null");
    public string Describe(string s) => "string:" + (s ?? "null");
    public string Take(long x) => "long:" + x;
    public string Take(double x) => "double:" + x;
    public string Over(long a, double b) => "ld";
    public string Over(double a, long b) => "dl";
    public static string Join(string sep, params object[] parts) => string.Join(sep, parts);
    public static bool TryIt(out int x)
    {
        x = 1;
        return true;
    }

    public string Generic<T>(T x) => typeof(T).Name + ":" + x;
    public int Twice(int x) => 2 * x;
    public float Half(float x) => x / 2;
    public Color Favorite(Color c) => c;
    public void Reset() => Count = 0;
    void IDisposable.Dispose() => Disposed = true;
}

/// <summary>记录字面量 #Cleat.Core.Tests.Pt[1 2] / #Cleat.Core.Tests.Pt{:x 1 :y 2} 的夹具。</summary>
public class Pt
{
    public readonly long X, Y;
    public Pt(long x, long y)
    {
        X = x;
        Y = y;
    }

    public static Pt create(IPersistentMap m) =>
        new Pt(RT.longCast(RT.get(m, Keyword.intern("x"))), RT.longCast(RT.get(m, Keyword.intern("y"))));
}

public class ReflectorTests
{
    private sealed class Fn : AFn
    {
        private readonly Func<object[], object> _f;
        public Fn(Func<object[], object> f) => _f = f;
        public override object invoke() => _f(Array.Empty<object>());
        public override object invoke(object a) => _f(new[] { a });
        public override object invoke(object a, object b) => _f(new[] { a, b });
    }

    private static object Call(Type t, string name, object inst, params object[] args) => Reflector.Call(t, name, inst, args, null, null);

    [Test]
    public void StaticOverloads_ByRuntimeType()
    {
        Assert.That(Call(typeof(Math), "Abs", null, -3L), Is.EqualTo(3L).And.TypeOf<long>());
        Assert.That(Call(typeof(Math), "Abs", null, -2.5), Is.EqualTo(2.5).And.TypeOf<double>());
        Assert.That(Call(typeof(Math), "Max", null, 3L, 9L), Is.EqualTo(9L));
    }

    [Test]
    public void InstanceOverloads_ByArity_WithNarrowing()
    {
        Assert.That(Call(typeof(string), "Substring", "hello", 1L), Is.EqualTo("ello"));
        Assert.That(Call(typeof(string), "Substring", "hello", 1L, 2L), Is.EqualTo("el"));
        Assert.That(Call(null, "ToUpper", "abc"), Is.EqualTo("ABC"));
    }

    [Test]
    public void ParamsArray_PackedAndExplicit()
    {
        Assert.That(Call(typeof(string), "Concat", null, "a", "b", "c"), Is.EqualTo("abc"));
        Assert.That(Call(typeof(string), "Concat", null, "a", "b", "c", "d", "e"), Is.EqualTo("abcde"));
        Assert.That(Call(typeof(ReflectorFixture), "Join", null, "-", 1L, "x", 2.5), Is.EqualTo("1-x-2.5"));
        Assert.That(Call(typeof(ReflectorFixture), "Join", null, "-"), Is.EqualTo(""));
        Assert.That(Reflector.Call(typeof(string), "Concat", null, new object[] { new[] { "x", "y" } }, new[] { typeof(string[]) }, null), Is.EqualTo("xy"));
    }

    [Test]
    public void WideningPrefersSameKind_NullPrefersMostSpecific()
    {
        var f = new ReflectorFixture();
        Assert.That(Call(typeof(ReflectorFixture), "Take", f, 3), Is.EqualTo("long:3"));
        Assert.That(Call(typeof(ReflectorFixture), "Take", f, 3.5f), Is.EqualTo("double:3.5"));
        Assert.That(Call(typeof(ReflectorFixture), "Describe", f, new object[] { null }), Is.EqualTo("string:null"));
        Assert.That(Call(typeof(ReflectorFixture), "Describe", f, 7L), Is.EqualTo("object:7"));
    }

    [Test]
    public void Ambiguity_ListsCandidates()
    {
        var f = new ReflectorFixture();
        var ex = Assert.Throws<InvalidOperationException>(() => Call(typeof(ReflectorFixture), "Over", f, 1L, 2L));
        Assert.That(ex.Message, Does.StartWith("More than one matching instance method found: Over"));
        Assert.That(ex.Message, Does.Contain("Over(Int64, Double)").And.Contain("Over(Double, Int64)"));
    }

    [Test]
    public void Errors_NoMatch_MissingMember_RefOut()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Call(typeof(string), "Substring", "x", "a"));
        Assert.That(ex.Message, Does.StartWith("No matching instance method Substring").And.Contain("String"));
        Assert.Throws<MissingMethodException>(() => Call(typeof(string), "NoSuchMethod", "x"));
        Assert.Throws<MissingMemberException>(() => Reflector.GetField(typeof(string), "NoSuchField", "x"));
        var ns = Assert.Throws<NotSupportedException>(() => Call(typeof(ReflectorFixture), "TryIt", null, 1L));
        Assert.That(ns.Message, Does.Contain("ref/out"));
    }

    [Test]
    public void ZeroArgCall_ReachesPropertiesAndFields_ReturnsClojureNumbers()
    {
        var f = new ReflectorFixture { Count = 5 };
        Assert.That(Call(typeof(string), "Length", "abc"), Is.EqualTo(3L).And.TypeOf<long>());
        Assert.That(Call(typeof(ReflectorFixture), "Count", f), Is.EqualTo(5L));
        Assert.That(Call(typeof(ReflectorFixture), "Name", f), Is.EqualTo("fixture"));
        Assert.That(Call(typeof(ReflectorFixture), "set_Name", f, "n2"), Is.Null);
        Assert.That(f.Name, Is.EqualTo("n2"));
        Assert.That(Call(typeof(ReflectorFixture), "set_Count", f, 9L), Is.Null);
        Assert.That(f.Count, Is.EqualTo(9));
        Assert.That(Call(typeof(ReflectorFixture), "Reset", f), Is.Null);
        Assert.That(f.Count, Is.EqualTo(0));
    }

    [Test]
    public void GetSetField_InstanceStaticConst()
    {
        var f = new ReflectorFixture();
        Assert.That(Reflector.SetField(typeof(ReflectorFixture), "Count", f, 7L), Is.Null);
        Assert.That(Reflector.GetField(typeof(ReflectorFixture), "Count", f), Is.EqualTo(7L).And.TypeOf<long>());
        Assert.That(Reflector.GetField(null, "Tag", f), Is.EqualTo("tag"));
        Assert.That(Reflector.GetField(typeof(ReflectorFixture), "Name", f), Is.EqualTo("fixture"));
        Reflector.SetField(typeof(ReflectorFixture), "Name", f, "renamed");
        Assert.That(f.Name, Is.EqualTo("renamed"));
        Reflector.SetField(typeof(ReflectorFixture), "StaticCounter", null, 11L);
        Assert.That(Reflector.GetField(typeof(ReflectorFixture), "StaticCounter", null), Is.EqualTo(11L));
        Reflector.SetField(typeof(ReflectorFixture), "StaticName", null, "s2");
        Assert.That(Reflector.GetField(typeof(ReflectorFixture), "StaticName", null), Is.EqualTo("s2"));
        Assert.That(Reflector.GetField(typeof(ReflectorFixture), "Answer", null), Is.EqualTo(42L).And.TypeOf<long>());
        Assert.That(Reflector.GetField(typeof(int), "MaxValue", null), Is.EqualTo((long)int.MaxValue));
        Assert.That(Reflector.GetField(typeof(string), "Length", "four"), Is.EqualTo(4L));
        Assert.That(Reflector.GetField(typeof(ReflectorFixture), "Reset", new ReflectorFixture()), Is.Null, "zero-arg method fallback");
        Assert.Throws<InvalidOperationException>(() => Reflector.SetField(typeof(ReflectorFixture), "Tag", f, "x"));
    }

    [Test]
    public void Indexers_ListDictionaryArray()
    {
        var list = new List<object> { "a", "b" };
        Assert.That(Call(typeof(List<object>), "get_Item", list, 1L), Is.EqualTo("b"));
        Assert.That(Call(typeof(List<object>), "set_Item", list, 0L, "z"), Is.Null);
        Assert.That(list[0], Is.EqualTo("z"));
        Assert.That(Call(typeof(IList<object>), "get_Item", list, 0L), Is.EqualTo("z"), "interface-typed call");

        var dict = new Dictionary<string, object> { ["k"] = 1L };
        Assert.That(Call(typeof(Dictionary<string, object>), "get_Item", dict, "k"), Is.EqualTo(1L));
        Call(typeof(Dictionary<string, object>), "set_Item", dict, "k2", 2L);
        Assert.That(dict["k2"], Is.EqualTo(2L));

        var arr = new[] { 1, 2, 3 };
        Assert.That(Call(null, "get_Item", arr, 2L), Is.EqualTo(3L).And.TypeOf<long>());
        Assert.That(Call(typeof(int[]), "set_Item", arr, 0L, 10L), Is.Null);
        Assert.That(arr[0], Is.EqualTo(10));
        Assert.That(Reflector.GetField(typeof(int[]), "Length", arr), Is.EqualTo(3L));
        var grid = new string[2, 2];
        Call(null, "set_Item", grid, 1L, 0L, "x");
        Assert.That(Call(null, "get_Item", grid, 1L, 0L), Is.EqualTo("x"));
        Assert.That(Reflector.NewArray(typeof(double), 4L), Is.TypeOf<double[]>().And.Length.EqualTo(4));
    }

    [Test]
    public void Constructors()
    {
        Assert.That(Reflector.New(typeof(List<object>), Array.Empty<object>()), Is.TypeOf<List<object>>());
        Assert.That(Reflector.New(typeof(string), new object[] { new[] { 'a', 'b' } }), Is.EqualTo("ab"));
        Assert.That(Reflector.New(typeof(StringBuilder), new object[] { "seed" }).ToString(), Is.EqualTo("seed"));
        Assert.That(Reflector.New(typeof(DateTime), new object[] { 2020L, 1L, 2L }), Is.EqualTo(new DateTime(2020, 1, 2)));
        Assert.That(Reflector.New(typeof(TimeSpan), Array.Empty<object>()), Is.EqualTo(default(TimeSpan)));
        Assert.That(((Pt)Reflector.New(typeof(Pt), new object[] { 1L, 2L })).Y, Is.EqualTo(2L));
        var ex = Assert.Throws<InvalidOperationException>(() => Reflector.New(typeof(Pt), new object[] { "a", "b" }));
        Assert.That(ex.Message, Does.StartWith("No matching constructor"));
        var fn = new Fn(a => a[0]);
        Assert.That(Reflector.New(typeof(Func<object, object>), new object[] { fn }), Is.InstanceOf<Func<object, object>>());
    }

    [Test]
    public void GenericMethods_ExplicitTypeArguments()
    {
        var src = new object[] { 1L, 2L, 3L };
        object r = Reflector.Call(typeof(Enumerable), "ToList", null, new object[] { src }, null, new[] { typeof(object) });
        Assert.That(r, Is.TypeOf<List<object>>().And.Count.EqualTo(3));
        var f = new ReflectorFixture();
        Assert.That(Reflector.Call(typeof(ReflectorFixture), "Generic", f, new object[] { "s" }, null, new[] { typeof(string) }), Is.EqualTo("String:s"));
        Assert.That(Reflector.Call(typeof(ReflectorFixture), "Generic", f, new object[] { 5L }, null, new[] { typeof(long) }), Is.EqualTo("Int64:5"));
        var ex = Assert.Throws<InvalidOperationException>(() => Reflector.Call(typeof(ReflectorFixture), "Generic", f, new object[] { 5L }, null, new[] { typeof(long), typeof(long) }));
        Assert.That(ex.Message, Does.Contain("generic argument"));
    }

    [Test]
    public void IFn_ToDelegate_InCallsAndConvert()
    {
        var list = new List<object> { 3L, 1L, 2L };
        var cmp = new Fn(a => (long)Numbers.compare(a[0], a[1]));
        Assert.That(Call(typeof(List<object>), "Sort", list, cmp), Is.Null);
        Assert.That(list, Is.EqualTo(new object[] { 1L, 2L, 3L }));

        var seen = new List<object>();
        var collect = new Fn(a => { seen.Add(a[0]); return null; });
        Reflector.Call(typeof(Array), "ForEach", null, new object[] { new[] { 10, 20 }, collect }, null, new[] { typeof(int) });
        Assert.That(seen, Is.EqualTo(new object[] { 10L, 20L }), "CLR int callback args arrive as long");

        var dbl = new Fn(a => Numbers.multiply(a[0], 2L));
        Func<int, int> d = Reflector.Convert<Func<int, int>>(dbl);
        Assert.That(d(21), Is.EqualTo(42));
        Func<object, object> same = (Func<object, object>)Reflector.MakeDelegate(dbl, typeof(Func<object, object>));
        Assert.That(same(4L), Is.EqualTo(8L));
        Action<object> act = Reflector.Convert<Action<object>>(collect);
        act("x");
        Assert.That(seen.Last(), Is.EqualTo("x"));
        Assert.That(Reflector.Convert<Func<object, object>>(null), Is.Null);
        Assert.That(Reflector.Convert<Func<object, object>>(same), Is.SameAs(same));
        Assert.Throws<InvalidCastException>(() => Reflector.Convert<Func<object, object>>("not a fn"));
    }

    [Test]
    public void Convert_NumericAndFriends()
    {
        Assert.That(Reflector.Convert<int>(3L), Is.EqualTo(3));
        Assert.That(Reflector.Convert<long>(3), Is.EqualTo(3L));
        Assert.That(Reflector.Convert<double>(3L), Is.EqualTo(3.0));
        Assert.That(Reflector.Convert<float>(1.5), Is.EqualTo(1.5f));
        Assert.That(Reflector.Convert<int>(3.9), Is.EqualTo(3), "truncates like (int 3.9)");
        Assert.That(Reflector.Convert<byte>(255L), Is.EqualTo((byte)255));
        Assert.That(Reflector.Convert<decimal>(2L), Is.EqualTo(2m));
        Assert.That(Reflector.Convert<char>(65L), Is.EqualTo('A'));
        Assert.That(Reflector.Convert<long>(BigInt.fromLong(9)), Is.EqualTo(9L));
        Assert.That(Reflector.Convert<object>("o"), Is.EqualTo("o"));
        Assert.That(Reflector.Convert<string>(null), Is.Null);
        Assert.That(Reflector.Convert<int?>(null), Is.Null);
        Assert.That(Reflector.Convert<int?>(4L), Is.EqualTo(4));
        Assert.That(Reflector.Convert<bool>(true), Is.True);
        Assert.That(Reflector.Convert<Color>(Keyword.intern("green")), Is.EqualTo(Color.Green));
        Assert.That(Reflector.Convert<Color>("Blue"), Is.EqualTo(Color.Blue));
        Assert.That(Reflector.Convert<Color>(2L), Is.EqualTo(Color.Blue));
        Assert.That(Reflector.Convert<Color>(Color.Red), Is.EqualTo(Color.Red));

        var ex = Assert.Throws<ArgumentException>(() => Reflector.Convert<int>(long.MaxValue));
        Assert.That(ex.Message, Does.Contain("out of range for int"));
        Assert.Throws<ArgumentException>(() => Reflector.Convert<byte>(256L));
        Assert.That(Assert.Throws<InvalidCastException>(() => Reflector.Convert<string>(5L)).Message, Does.Contain("Int64").And.Contain("String"));
        Assert.That(Assert.Throws<InvalidCastException>(() => Reflector.Convert<int>(null)).Message, Does.Contain("nil"));
        Assert.Throws<InvalidCastException>(() => Reflector.Convert<long>("12"));
        Assert.Throws<InvalidCastException>(() => Reflector.Convert<bool>(1L));
    }

    [Test]
    public void ConvertFrom_ClojureRepresentation()
    {
        Assert.That(Reflector.ConvertFrom(5, typeof(int)), Is.EqualTo(5L).And.TypeOf<long>());
        Assert.That(Reflector.ConvertFrom((short)5, typeof(short)), Is.TypeOf<long>());
        Assert.That(Reflector.ConvertFrom((byte)5, typeof(object)), Is.TypeOf<long>());
        Assert.That(Reflector.ConvertFrom(7u, typeof(uint)), Is.EqualTo(7L));
        Assert.That(Reflector.ConvertFrom(7ul, typeof(ulong)), Is.EqualTo(7L).And.TypeOf<long>());
        Assert.That(Reflector.ConvertFrom(ulong.MaxValue, typeof(ulong)), Is.TypeOf<BigInt>());
        Assert.That(Reflector.ConvertFrom(1.5f, typeof(float)), Is.EqualTo(1.5).And.TypeOf<double>());
        Assert.That(Reflector.ConvertFrom(1.5m, typeof(decimal)), Is.TypeOf<BigDecimal>());
        Assert.That(Reflector.ConvertFrom(Color.Blue, typeof(Color)), Is.EqualTo(Color.Blue));
        Assert.That(Reflector.ConvertFrom("s", typeof(object)), Is.EqualTo("s"));
        Assert.That(Reflector.ConvertFrom(true, typeof(bool)), Is.True);
        Assert.That(Reflector.ConvertFrom('c', typeof(char)), Is.EqualTo('c'));
        Assert.That(Reflector.ConvertFrom(null, typeof(object)), Is.Null);
    }

    [Test]
    public void ReturnValues_AreConverted()
    {
        var f = new ReflectorFixture();
        Assert.That(Call(typeof(ReflectorFixture), "Twice", f, 4L), Is.EqualTo(8L).And.TypeOf<long>());
        Assert.That(Call(typeof(ReflectorFixture), "Half", f, 3L), Is.EqualTo(1.5).And.TypeOf<double>());
        Assert.That(Call(typeof(ReflectorFixture), "Favorite", f, Keyword.intern("blue")), Is.EqualTo(Color.Blue));
        Assert.That(Call(typeof(long), "CompareTo", 5L, 3L), Is.EqualTo(1L), "instance method on a boxed value type");
        Assert.That(Call(typeof(IComparable), "ToString", 5L), Is.EqualTo("5"), "falls back to the runtime type");
        Call(typeof(IDisposable), "Dispose", f);
        Assert.That(f.Disposed, Is.True, "explicit interface implementation via the interface type");
    }

    [Test]
    public void CastAndIs()
    {
        Assert.That(Reflector.Cast(typeof(int), 3.7), Is.EqualTo(3).And.TypeOf<int>());
        Assert.That(Reflector.Cast(typeof(long), 3.7), Is.EqualTo(3L));
        Assert.That(Reflector.Cast(typeof(double), 2L), Is.EqualTo(2.0));
        Assert.That(Reflector.Cast(typeof(char), 97L), Is.EqualTo('a'));
        Assert.That(Reflector.Cast(typeof(bool), null), Is.False);
        Assert.That(Reflector.Cast(typeof(bool), 0L), Is.True);
        Assert.That(Reflector.Cast(typeof(string), "x"), Is.EqualTo("x"));
        Assert.That(Reflector.Cast(typeof(string), null), Is.Null);
        Assert.That(Reflector.Cast(typeof(Color), "Red"), Is.EqualTo(Color.Red));
        Assert.That(Reflector.Cast(typeof(int?), null), Is.Null);
        Assert.That(Reflector.Cast(typeof(object), 1L), Is.EqualTo(1L));
        Assert.That(Reflector.Cast(typeof(Func<object, object>), new Fn(a => a[0])), Is.InstanceOf<Func<object, object>>());
        Assert.Throws<ArgumentException>(() => Reflector.Cast(typeof(int), long.MaxValue));
        Assert.Throws<InvalidCastException>(() => Reflector.Cast(typeof(string), 5L));
        Assert.Throws<InvalidCastException>(() => Reflector.Cast(typeof(int), "5"));
        Assert.Throws<InvalidCastException>(() => Reflector.Cast(typeof(int), null));

        Assert.That(Reflector.Is(typeof(string), "x"), Is.EqualTo(true).And.TypeOf<bool>());
        Assert.That(Reflector.Is(typeof(string), null), Is.False);
        Assert.That(Reflector.Is(typeof(IComparable), 5L), Is.True);
        Assert.That(Reflector.Is(typeof(int), 5L), Is.False);
    }

    [Test]
    public void CachedPath_SameResultAndDelegateReuse()
    {
        var a = Call(typeof(string), "Substring", "cached", 2L);
        var b = Call(typeof(string), "Substring", "cached", 2L);
        Assert.That(a, Is.EqualTo("ched"));
        Assert.That(b, Is.EqualTo(a));
        for (int i = 0; i < 1000; i++) Assert.That(Call(typeof(Math), "Abs", null, (long)-i), Is.EqualTo((long)i));
        Assert.That(Reflector.MakeDelegate(new Fn(x => x[0]), typeof(Func<object, object>)).GetType(), Is.EqualTo(typeof(Func<object, object>)));
        var f = new ReflectorFixture();
        Assert.That(Call(typeof(ReflectorFixture), "Take", f, 1L), Is.EqualTo("long:1"));
        Assert.That(Call(typeof(ReflectorFixture), "Take", f, 1.0), Is.EqualTo("double:1"), "different arg types: different cache entries");
    }

    private static object Read(string s)
    {
        var r = new LineNumberingTextReader(new StringReader(s));
        return LispReader.read(r, true, null, false, PersistentHashMap.EMPTY);
    }

    /// <summary>读取出错时的原始异常（LispReader 在顶层把异常包成 ReaderException）。</summary>
    private static Exception ReadError(string s)
    {
        try
        {
            Read(s);
            return null;
        }
        catch (Exception e)
        {
            while (e is LispReader.ReaderException && e.InnerException != null) e = e.InnerException;
            return e;
        }
    }

    [Test]
    public void LispReader_ReadEval_AndRecordLiterals()
    {
        Assert.That(Read("#=(System.Math/Abs -3)"), Is.EqualTo(3L));
        Assert.That(Read("#=(System.String/Concat \"a\" \"b\")"), Is.EqualTo("ab"));
        Assert.That(Read("#=(System.Text.StringBuilder. \"sb\")").ToString(), Is.EqualTo("sb"));
        Assert.That(Read("#=(System.Text.StringBuilder/new \"sb2\")").ToString(), Is.EqualTo("sb2"));
        Assert.That(Read("#=System.String"), Is.EqualTo(typeof(string)));
        var p = (Pt)Read("#Cleat.Core.Tests.Pt[1 2]");
        Assert.That((p.X, p.Y), Is.EqualTo((1L, 2L)));
        var q = (Pt)Read("#Cleat.Core.Tests.Pt{:x 3 :y 4}");
        Assert.That((q.X, q.Y), Is.EqualTo((3L, 4L)));
        Assert.That(ReadError("#Cleat.Core.Tests.Pt[1]"), Is.TypeOf<ArgumentException>());
        Assert.That(ReadError("#=(no.such/fn 1)"), Is.TypeOf<TypeNotFoundException>(), "a namespace part that is no Clojure namespace names a type");
        var ex = ReadError("#=(nosuchfn 1)");
        Assert.That(ex, Is.TypeOf<InvalidOperationException>());
        Assert.That(ex.Message, Does.StartWith("Can't resolve"));

        Var.pushThreadBindings(RT.map(RT.ReadEvalVar, false));
        try
        {
            Assert.That(ReadError("#=(System.Math/Abs -3)"), Is.TypeOf<InvalidOperationException>());
            Assert.That(ReadError("#Cleat.Core.Tests.Pt[1 2]"), Is.TypeOf<InvalidOperationException>());
        }
        finally
        {
            Var.popThreadBindings();
        }
    }
}
