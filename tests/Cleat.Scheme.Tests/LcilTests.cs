using System;
using System.IO;
using System.Linq;
using Cleat.Emit;
using NUnit.Framework;

namespace Cleat.Scheme.Tests;

/// <summary>Lcil 读取器与对象模型（iron-018）。</summary>
public class LcilParserTests
{
    public static string Sample(string name) => File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "samples", name));

    [TestCase("fact.lcil")]
    [TestCase("counter.lcil")]
    [TestCase("tryfinally.lcil")]
    [TestCase("mutual.lcil")]
    [TestCase("interop.lcil")]
    public void SamplesParse(string name)
    {
        var asm = LcilParser.Parse(Sample(name), name);
        Assert.That(asm.Types, Is.Not.Empty);
        Assert.That(asm.Types.SelectMany(t => t.Methods).All(m => m.Body.Count > 0));
    }

    [Test]
    public void ModelDetails()
    {
        var asm = LcilParser.Parse(Sample("interop.lcil"), "interop.lcil");
        Assert.That(asm.Name, Is.EqualTo("interop"));
        Assert.That(asm.References, Is.EqualTo(new[] { "Cleat.Scheme" }));
        var sub = asm.Types[0].Methods.Single(m => m.Name == "Sub");
        var clr = sub.Body.Single(i => i.Kind == InstrKind.Clr);
        Assert.That((clr.ClrOp, clr.ClrType, clr.ClrMember, clr.ClrStatic, clr.ClrArgc), Is.EqualTo(("call", "String", "Substring", false, 2)));
        Assert.That(clr.ClrParams, Is.EqualTo(new[] { "Int32", "Int32" }));
        var len = asm.Types[0].Methods.Single(m => m.Name == "Len").Body.Single(i => i.Kind == InstrKind.Clr);
        Assert.That(len.ClrType, Is.Null);
        var tf = LcilParser.Parse(Sample("tryfinally.lcil"), "tf");
        var tr = tf.Types[0].Methods[0].Body[0];
        Assert.That(tr.Kind, Is.EqualTo(InstrKind.Try));
        Assert.That(tr.Finally, Has.Count.EqualTo(3));
        var call = LcilParser.Parse(Sample("fact.lcil"), "f").Types[0].Methods[1].Body.Single(i => i.Operand is MethodRef);
        Assert.That(call.Operand.ToString(), Is.EqualTo("Fact::Rec(System.Int64)"));
    }

    [Test]
    public void ReaderData()
    {
        var items = new SExprReader("(a \"x\\x3bb;\\n\" -12 #x1F 1.5e3 #t [b] #| c |# ; d\n e)", "t").ReadAll();
        var l = (SList)items[0];
        Assert.That(((SStr)l[1]).Value, Is.EqualTo("xλ\n"));
        Assert.That(((SInt)l[2]).Value, Is.EqualTo(-12));
        Assert.That(((SInt)l[3]).Value, Is.EqualTo(31));
        Assert.That(((SReal)l[4]).Value, Is.EqualTo(1500.0));
        Assert.That(((SBool)l[5]).Value, Is.True);
        Assert.That(((SList)l[6]).Head, Is.EqualTo("b"));
        Assert.That(((SSym)l[7]).Name, Is.EqualTo("e"));
        Assert.That((l[7].Line, l[7].Col), Is.EqualTo((2, 2)));
    }

    private static string Wrap(string body, string locals = "") =>
      "(lcil 1 (assembly \"t\")\n (type \"T\"\n  (method \"M\" (attrs static) (params \"System.Object\") (ret \"System.Object\") " + locals + "\n   (body " + body + "))))";

    [TestCase("(frob)", "t:4:10: unknown instruction: frob")]
    [TestCase("(br nowhere) (ret)", "t:4:10: undefined label nowhere")]
    [TestCase("(label a) (label a) (ret)", "t:4:20: duplicate label a")]
    [TestCase("(ldarg 1) (ret)", "t:4:10: argument index out of range: 1")]
    [TestCase("(ldloc 0) (ret)", "t:4:10: local index out of range: 0")]
    [TestCase("(ldconst 0) (ret)", "t:4:10: constant index out of range")]
    [TestCase("(ldc.i4) (ret)", "t:4:10: wrong number of operands for ldc.i4")]
    [TestCase("(ldarg 0) (tail) (ret)", "t:4:20: (tail) must be followed by call or callvirt")]
    [TestCase("(ldstr 5) (ret)", "t:4:17: expected a string for string operand")]
    [TestCase("(clr-call \"T\" \"m\" maybe 0) (ret)", "t:4:28: expected #t or #f")]
    public void ErrorsCarryPositions(string body, string message)
    {
        var e = Assert.Throws<LcilException>(() => LcilParser.Parse(Wrap(body), "t"));
        Assert.That(e.Message, Is.EqualTo(message));
    }

    [Test]
    public void StructuralErrors()
    {
        Assert.That(Assert.Throws<LcilException>(() => LcilParser.Parse("(lcil 2 (assembly \"a\"))", "t")).Message, Is.EqualTo("t:1:7: unsupported Lcil version 2"));
        Assert.That(Assert.Throws<LcilException>(() => LcilParser.Parse("(lcil 1)", "t")).Message, Is.EqualTo("t:1:1: missing assembly clause"));
        Assert.That(Assert.Throws<LcilException>(() => LcilParser.Parse("(lcil 1 (assembly \"a\") (constants (1 (nil))))", "t")).Message, Is.EqualTo("t:1:36: constant index must be 0"));
        Assert.That(Assert.Throws<LcilException>(() => LcilParser.Parse("(lcil 1 (assembly \"a\")", "t")).Message, Is.EqualTo("t:1:1: unterminated list"));
    }
}
