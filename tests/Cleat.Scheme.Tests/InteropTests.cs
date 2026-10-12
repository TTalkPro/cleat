using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Cleat.Scheme;
using NUnit.Framework;
using BigInteger = System.Numerics.BigInteger;

namespace Cleat.Scheme.Tests;

public class InteropSample
{
    public int Field = 1;
    public static string StaticField = "s";
    public string Over(int x) => "int";
    public string Over(double x) => "double";
    public string Over(string x) => "string";
    public string Over(object x) => "object";
}

public class TypeResolverTests
{
    private static readonly string[] s_ns = { "System.Text", "System.Collections.Generic" };

    [Test]
    public void NamesAliasesArraysGenerics()
    {
        Assert.That(TypeResolver.Resolve(Symbol.Intern("String"), null), Is.EqualTo(typeof(string)));
        Assert.That(TypeResolver.Resolve(Symbol.Intern("fixnum"), null), Is.EqualTo(typeof(int)));
        Assert.That(TypeResolver.Resolve(Symbol.Intern("Int32[]"), null), Is.EqualTo(typeof(int[])));
        Assert.That(TypeResolver.Resolve(Symbol.Intern("cons"), null), Is.EqualTo(typeof(Cons)));
        Assert.That(TypeResolver.Resolve(Symbol.Intern("procedure"), null), Is.EqualTo(typeof(Procedure)));
        Assert.That(TypeResolver.Resolve(Symbol.Intern("StringBuilder"), s_ns), Is.EqualTo(typeof(StringBuilder)));
        Assert.That(TypeResolver.Resolve(Symbol.Intern("StringBuilder"), null), Is.Null);
        Assert.That(TypeResolver.Resolve(Cons.FromArray(Symbol.Intern("Dictionary"), Symbol.Intern("String"), Symbol.Intern("Int32")), s_ns),
          Is.EqualTo(typeof(Dictionary<string, int>)));
        Assert.That(TypeResolver.Resolve("System.Collections.Hashtable", null), Is.EqualTo(typeof(Hashtable)));
        Assert.That(TypeResolver.Resolve(Symbol.Intern("NoSuchType"), s_ns), Is.Null);
        Assert.That(TypeResolver.Resolve(Symbol.Intern("Cleat.Scheme.Builtins"), null), Is.EqualTo(typeof(Builtins)));
    }

    [Test]
    public void GetClrTypePrimitive()
    {
        Assert.That(Builtins.GetClrType(Symbol.Intern("System.String")), Is.EqualTo(typeof(string)));
        Assert.That(Builtins.GetClrType(Symbol.Intern("System.Collections.Generic.List`1"), typeof(int)), Is.EqualTo(typeof(List<int>)));
        Assert.That(Builtins.GetClrType(Symbol.Intern("Nope")), Is.SameAs(RT.False));
    }
}

public class InteropCallTests
{
    [Test]
    public void StaticOverloadsByRuntimeTypes()
    {
        Assert.That(Interop.Call(typeof(Math), "Max", null, new object[] { 1, 2 }), Is.EqualTo(2).And.InstanceOf<int>());
        Assert.That(Interop.Call(typeof(Math), "Max", null, new object[] { 1.5, 2.5 }), Is.EqualTo(2.5));
        var s = new InteropSample();
        Assert.That(Interop.Call(typeof(InteropSample), "Over", s, new object[] { 1 }), Is.EqualTo("int"));
        Assert.That(Interop.Call(typeof(InteropSample), "Over", s, new object[] { 1.0 }), Is.EqualTo("double"));
        Assert.That(Interop.Call(typeof(InteropSample), "Over", s, new object[] { "x" }), Is.EqualTo("string"));
        Assert.That(Interop.Call(typeof(InteropSample), "Over", s, new object[] { Symbol.Intern("x") }), Is.EqualTo("object"));
        Assert.That(Interop.Call(typeof(InteropSample), "Over", s, new object[] { new StringBuilder("m") }), Is.EqualTo("object"));   // 可赋值 object 得分高于转换为 string
    }

    [Test]
    public void ExplicitSignature()
    {
        var s = new InteropSample();
        Assert.That(Interop.Call(typeof(InteropSample), "Over", s, new object[] { 1 }, new[] { typeof(object) }, null), Is.EqualTo("object"));
    }

    [Test]
    public void InstanceAndConversions()
    {
        Assert.That(Interop.Call(typeof(string), "ToUpper", "abc", new object[0]), Is.EqualTo("ABC"));
        var sb = new StringBuilder();
        Interop.Call(typeof(StringBuilder), "Append", sb, new object[] { new StringBuilder("mutable") });
        Assert.That(sb.ToString(), Is.EqualTo("mutable"));
        Assert.That(Interop.Call(typeof(string), "Contains", "abc", new object[] { "b" }), Is.SameAs(RT.True));   // bool 归一为单例
        Assert.That(Interop.Call(typeof(Console), "Write", null, new object[] { "" }), Is.SameAs(Unspecified.Instance));
    }

    [Test]
    public void ParamsArray()
    {
        Assert.That(Interop.Call(typeof(string), "Concat", null, new object[] { "a", "b", "c", "d", "e" }), Is.EqualTo("abcde"));
        Assert.That(Interop.Call(typeof(string), "Format", null, new object[] { "{0}-{1}-{2}-{3}", 1, 2, 3, 4 }), Is.EqualTo("1-2-3-4"));
    }

    [Test]
    public void EnumsInAndOut()
    {
        Assert.That(Interop.Call(typeof(string), "Equals", null, new object[] { "ABC", "abc", Symbol.Intern("OrdinalIgnoreCase") }), Is.SameAs(RT.True));
        Assert.That(Interop.Call(typeof(DateTime), "get_DayOfWeek", new DateTime(2026, 10, 8), new object[0]), Is.SameAs(Symbol.Intern("Thursday")));
        var flags = Interop.SymbolToEnum(typeof(AttributeTargets), Cons.FromArray(Symbol.Intern("Class"), Symbol.Intern("Method")));
        Assert.That(flags, Is.EqualTo(AttributeTargets.Class | AttributeTargets.Method));
    }

    [Test]
    public void IndexersAndArrays()
    {
        var list = new List<object> { "a", "b" };
        Assert.That(Interop.Call(typeof(List<object>), "get_Item", list, new object[] { 1 }), Is.EqualTo("b"));
        var ht = new Hashtable { ["k"] = 1 };
        Assert.That(Interop.Call(typeof(Hashtable), "get_Item", ht, new object[] { "k" }), Is.EqualTo(1));
        var arr = new int[3];
        Interop.Call(typeof(int[]), "set_Item", arr, new object[] { 1, 42 });
        Assert.That(Interop.Call(typeof(int[]), "get_Item", arr, new object[] { 1 }), Is.EqualTo(42));
    }

    [Test]
    public void ConstructorsFieldsCastIs()
    {
        Assert.That(Interop.New(typeof(StringBuilder), new object[] { "x" }).ToString(), Is.EqualTo("x"));
        Assert.That(Interop.New(typeof(DateTime), new object[] { 2020, 1, 2 }), Is.EqualTo(new DateTime(2020, 1, 2)));
        Assert.That(Interop.New(typeof(Guid), new object[0]), Is.EqualTo(Guid.Empty));
        var s = new InteropSample();
        Assert.That(Interop.GetField(typeof(InteropSample), "Field", s), Is.EqualTo(1));
        Interop.SetField(typeof(InteropSample), "Field", s, 5);
        Assert.That(s.Field, Is.EqualTo(5));
        Assert.That(Interop.GetField(typeof(InteropSample), "StaticField", null), Is.EqualTo("s"));
        Assert.That(Interop.Cast(typeof(long), 5), Is.EqualTo(5L).And.InstanceOf<long>());
        Assert.That(Interop.Cast(typeof(int), null), Is.EqualTo(0));
        Assert.That(Interop.Is(typeof(string), "x"), Is.SameAs(RT.True));
        Assert.That(Interop.NewArray(typeof(string), 3), Is.EqualTo(new string[3]));
        Assert.That(Interop.ConvertTo(Cons.FromArray(1, 2), typeof(int[])), Is.EqualTo(new[] { 1, 2 }));
        Assert.That(Interop.ConvertTo(Builtins.Add(int.MaxValue, 1), typeof(long)), Is.EqualTo((long)int.MaxValue + 1));
    }

    [Test]
    public void ProceduresAsDelegates()
    {
        var square = new Fn(a => (int)a[0] * (int)a[0]);
        var f = (Func<int, int>)Interop.ToDelegate(typeof(Func<int, int>), square);
        Assert.That(f(7), Is.EqualTo(49));
        var list = new List<int> { 1, 2, 3 };
        var r = (List<string>)Interop.Call(typeof(List<int>), "ConvertAll", list,
          new object[] { new Fn(a => a[0].ToString() + "!") }, null, new[] { typeof(string) });
        Assert.That(r, Is.EqualTo(new[] { "1!", "2!", "3!" }));
        var arr = new object[] { 3, 1, 2 };
        Interop.Call(typeof(Array), "Sort", null, new object[] { arr, new Fn(a => (int)a[0] - (int)a[1]) }, null, new[] { typeof(object) });
        Assert.That(arr, Is.EqualTo(new object[] { 1, 2, 3 }));
        int seen = 0;
        var act = (Action<int>)Interop.ToDelegate(typeof(Action<int>), new Fn(a => { seen = (int)a[0]; return null; }));
        act(9);
        Assert.That(seen, Is.EqualTo(9));
    }

    [Test]
    public void Errors()
    {
        Assert.Throws<SchemeRuntimeError>(() => Interop.Call(typeof(string), "NoSuchMethod", "x", new object[0]));
        Assert.Throws<FormatException>(() => Interop.Call(typeof(int), "Parse", null, new object[] { "not-a-number" }));   // 不包装成 TargetInvocationException
        Assert.Throws<SchemeRuntimeError>(() => Interop.ConvertTo("x", typeof(int)));
    }

    /// <summary>互操作辅助方法（FFI 部分已随 Ffi.cs 删除，designs/15 §1）。</summary>
    [Test]
    public void InteropHelpers()
    {
        Assert.That(Helpers.SymbolToEnum<DayOfWeek>(Symbol.Intern("monday")), Is.EqualTo(DayOfWeek.Monday));
        Assert.That(Helpers.RequiresArray<int>(new object[] { 1, 2 }), Is.EqualTo(new[] { 1, 2 }));
        var d = Helpers.ConvertToDelegate<Func<int>>(new Fn(_ => 3));
        Assert.That(d(), Is.EqualTo(3));
    }
}
