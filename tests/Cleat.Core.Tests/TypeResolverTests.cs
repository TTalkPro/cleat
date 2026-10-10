// cleat-010：TypeResolver（ClrTypeSpec2 类型名语法 + 命名空间前缀 + Clojure 特殊名）。
// 后半部分从 ClojureCLR Csharp.Tests/TypeNameTests2/TypeNameResolvingTests2.cs 移植（去掉依赖 ns 别名 import 的用例）。
using System;
using System.Collections.Generic;
using Cleat.Core.Runtime;
using clojure.lang;
using NUnit.Framework;

namespace Cleat.Core.Tests;

public class Simple
{
}

public class Outer
{
    public class Inner
    {
    }
}

public class OneG<T>
{
}

public class TwoG<T1, T2>
{
}

public class GenParent<T1, T2>
{
    public class Child
    {
        public class GrandChild<T3>
        {
            public class GreatGrandChild<T4, T5>
            {
            }
        }
    }
}

public class TypeResolverTests
{
    private static readonly string[] s_system = { "System" };
    private static readonly string[] s_systemAndGeneric = { "System", "System.Collections.Generic" };

    private static Type Resolve(string name, IEnumerable<string> namespaces = null) => TypeResolver.Resolve(name, namespaces);

    [Test]
    public void FullNames_AndNamespacePrefixes()
    {
        Assert.That(Resolve("System.String"), Is.EqualTo(typeof(string)));
        Assert.That(Resolve("String", s_system), Is.EqualTo(typeof(string)));
        Assert.That(Resolve("String"), Is.Null, "no namespace list: a bare name is not found");
        Assert.That(Resolve("Int32", s_system), Is.EqualTo(typeof(int)));
        Assert.That(Resolve("List`1", s_systemAndGeneric), Is.EqualTo(typeof(List<>)));
        Assert.That(Resolve("StringBuilder", new[] { "System", "System.Text" }), Is.EqualTo(typeof(System.Text.StringBuilder)));
        Assert.That(Resolve("Cleat.Core.Tests.Simple"), Is.EqualTo(typeof(Simple)));
        Assert.That(Resolve("Simple", new[] { "Cleat.Core.Tests" }), Is.EqualTo(typeof(Simple)));
        Assert.That(Resolve("Cleat.Core.Tests.Outer+Inner"), Is.EqualTo(typeof(Outer.Inner)));
    }

    [Test]
    public void Generics_AngleBrackets_Backtick_InferredArity()
    {
        Assert.That(Resolve("System.Collections.Generic.List<System.Object>"), Is.EqualTo(typeof(List<object>)));
        Assert.That(Resolve("List<Object>", s_systemAndGeneric), Is.EqualTo(typeof(List<object>)));
        Assert.That(Resolve("System.Collections.Generic.List`1[System.Object]"), Is.EqualTo(typeof(List<object>)));
        Assert.That(Resolve("System.Collections.Generic.Dictionary`2[System.String,System.Object]"), Is.EqualTo(typeof(Dictionary<string, object>)));
        Assert.That(Resolve("Dictionary<String, List<long>>", s_systemAndGeneric), Is.EqualTo(typeof(Dictionary<string, List<long>>)));
        Assert.That(Resolve("System.Nullable`1[System.Int32]"), Is.EqualTo(typeof(int?)));
        Assert.That(Resolve("System.Collections.Generic.List<int>[]"), Is.EqualTo(typeof(List<int>[])));
        Assert.That(Resolve("System.Collections.Generic.List<System.Object>", null), Is.EqualTo(typeof(List<object>)));
    }

    [Test]
    public void Arrays_ByRef_Pointer_AssemblyQualified()
    {
        Assert.That(Resolve("System.Int32[]"), Is.EqualTo(typeof(int[])));
        Assert.That(Resolve("System.String[,]"), Is.EqualTo(typeof(string[,])));
        Assert.That(Resolve("System.String[][]"), Is.EqualTo(typeof(string[][])));
        Assert.That(Resolve("String[]", s_system), Is.EqualTo(typeof(string[])));
        Assert.That(Resolve("System.String&"), Is.EqualTo(typeof(string).MakeByRefType()));
        Assert.That(Resolve("System.String*"), Is.EqualTo(typeof(string).MakePointerType()));
        Assert.That(Resolve("System.String, System.Private.CoreLib"), Is.EqualTo(typeof(string)));
        Assert.That(Resolve(typeof(List<object>).AssemblyQualifiedName), Is.EqualTo(typeof(List<object>)));
    }

    [TestCase("int", typeof(int))]
    [TestCase("long", typeof(long))]
    [TestCase("float", typeof(float))]
    [TestCase("double", typeof(double))]
    [TestCase("char", typeof(char))]
    [TestCase("byte", typeof(byte))]
    [TestCase("sbyte", typeof(sbyte))]
    [TestCase("short", typeof(short))]
    [TestCase("ushort", typeof(ushort))]
    [TestCase("uint", typeof(uint))]
    [TestCase("ulong", typeof(ulong))]
    [TestCase("bool", typeof(bool))]
    [TestCase("boolean", typeof(bool))]
    [TestCase("decimal", typeof(decimal))]
    [TestCase("void", typeof(void))]
    [TestCase("object", typeof(object))]
    [TestCase("objects", typeof(object[]))]
    [TestCase("ints", typeof(int[]))]
    [TestCase("longs", typeof(long[]))]
    [TestCase("floats", typeof(float[]))]
    [TestCase("doubles", typeof(double[]))]
    [TestCase("chars", typeof(char[]))]
    [TestCase("shorts", typeof(short[]))]
    [TestCase("bytes", typeof(byte[]))]
    [TestCase("booleans", typeof(bool[]))]
    [TestCase("bools", typeof(bool[]))]
    [TestCase("uints", typeof(uint[]))]
    [TestCase("ushorts", typeof(ushort[]))]
    [TestCase("ulongs", typeof(ulong[]))]
    [TestCase("sbytes", typeof(sbyte[]))]
    [TestCase("int[]", typeof(int[]))]
    [TestCase("long[,]", typeof(long[,]))]
    public void SpecialTags(string name, Type expected)
    {
        Assert.That(Resolve(name), Is.EqualTo(expected));
        Assert.That(TypeResolver.SpecialTag(name) ?? Resolve(name), Is.EqualTo(expected));
    }

    [Test]
    public void Unknown_NullOrThrows_AndCache()
    {
        Assert.That(Resolve("No.Such.Type"), Is.Null);
        Assert.That(Resolve("NoSuchType", s_system), Is.Null);
        Assert.That(Resolve("System.Collections.Generic.List<No.Such>"), Is.Null);
        Assert.That(Resolve(""), Is.Null);
        Assert.That(Resolve(null), Is.Null);
        var ex = Assert.Throws<TypeNotFoundException>(() => TypeResolver.ResolveE("NoSuchType", s_system));
        Assert.That(ex.Message, Does.Contain("NoSuchType").And.Contain("System"));
        Assert.That(TypeResolver.ResolveE("System.String", null), Is.EqualTo(typeof(string)));
        Assert.That(Resolve("System.Collections.Generic.List<System.Object>"), Is.SameAs(Resolve("System.Collections.Generic.List<System.Object>")));
        Assert.That(Resolve("NoSuchType", s_system), Is.Null, "a miss stays a miss; only hits are cached");
    }

    // ---- ported from ClojureCLR TypeNameResolvingTests2 ----

    [Test]
    public void Ported_GenericClassName_ResolvesCorrectly()
    {
        Assert.That(Resolve("Cleat.Core.Tests.OneG`1[System.String]"), Is.EqualTo(typeof(OneG<string>)));
        Assert.That(Resolve("Cleat.Core.Tests.TwoG`2[System.String,System.Int32]"), Is.EqualTo(typeof(TwoG<string, int>)));
        Assert.That(
            Resolve("Cleat.Core.Tests.GenParent`2+Child+GrandChild`1+GreatGrandChild`2[System.String, System.Int32, System.Double, System.String,System.Object]"),
            Is.EqualTo(typeof(GenParent<string, int>.Child.GrandChild<double>.GreatGrandChild<string, object>)));
    }

    [Test]
    public void Ported_NonExistentType_ResolvesToNull()
    {
        Assert.That(Resolve("Cleat.Core.Tests.NonExistent"), Is.Null);
        Assert.That(Resolve("Cleat.Core.Tests.Simple+NonExistent"), Is.Null);
        Assert.That(Resolve("Cleat.Core.Tests.OneG`1[Non.Existent]"), Is.Null);
    }

    [Test]
    public void Ported_PointerByRefArray_ResolvesCorrectly()
    {
        Assert.That(Resolve("System.String**"), Is.EqualTo(typeof(string).MakePointerType().MakePointerType()));
        Assert.That(Resolve("System.String*&"), Is.EqualTo(typeof(string).MakePointerType().MakeByRefType()));
        Assert.That(Resolve("System.String[*]"), Is.EqualTo(typeof(string).MakeArrayType(1)));
        Assert.That(Resolve("System.String[,,]"), Is.EqualTo(typeof(string).MakeArrayType(3)));
    }

    [Test]
    public void Ported_Everything_ResolvesCorrectly()
    {
        Assert.That(
            Resolve("Cleat.Core.Tests.GenParent`2+Child+GrandChild`1+GreatGrandChild`2[System.String, System.Int32, System.Double, System.String,System.Object][]**&"),
            Is.EqualTo(typeof(GenParent<string, int>.Child.GrandChild<double>.GreatGrandChild<string, object>).MakeArrayType().MakePointerType().MakePointerType().MakeByRefType()));
    }

    [TestCase("Cleat.Core.Tests.OneG[int]", typeof(OneG<int>))]
    [TestCase("Cleat.Core.Tests.TwoG[int,String]", typeof(TwoG<int, string>))]
    [TestCase("Cleat.Core.Tests.GenParent[int,String]", typeof(GenParent<int, string>))]
    [TestCase("Cleat.Core.Tests.GenParent[int,String]+Child", typeof(GenParent<int, string>.Child))]
    [TestCase("Cleat.Core.Tests.GenParent[int,String]+Child+GrandChild[double]", typeof(GenParent<int, string>.Child.GrandChild<double>))]
    [TestCase("Cleat.Core.Tests.GenParent[int,String]+Child+GrandChild[double]+GreatGrandChild[int,long]", typeof(GenParent<int, string>.Child.GrandChild<double>.GreatGrandChild<int, long>))]
    [TestCase("TwoG[OneG[int],GenParent[long,String]+Child]", typeof(TwoG<OneG<int>, GenParent<long, string>.Child>))]
    [TestCase("TwoG<OneG<int>, GenParent<long, String>+Child>", typeof(TwoG<OneG<int>, GenParent<long, string>.Child>))]
    public void Ported_InferredArityGenericType_ParsesCorrectly(string typename, Type expectedType)
    {
        Assert.That(Resolve(typename, new[] { "System", "Cleat.Core.Tests" }), Is.EqualTo(expectedType));
    }

    [TestCase("String[]", typeof(string[]))]
    [TestCase("Simple[]", typeof(Simple[]))]
    [TestCase("Cleat.Core.Tests.Simple[]", typeof(Simple[]))]
    public void Ported_ArraysOfSimpleNames(string typename, Type expectedType)
    {
        Assert.That(Resolve(typename, new[] { "System", "Cleat.Core.Tests" }), Is.EqualTo(expectedType));
    }

    [Test]
    public void Ported_PointersAndByRefOfSimpleNames()
    {
        var nss = new[] { "System", "Cleat.Core.Tests" };
        Assert.That(Resolve("String*", nss), Is.EqualTo(typeof(string).MakePointerType()));
        Assert.That(Resolve("Simple**", nss), Is.EqualTo(typeof(Simple).MakePointerType().MakePointerType()));
        Assert.That(Resolve("Simple&", nss), Is.EqualTo(typeof(Simple).MakeByRefType()));
    }
}
