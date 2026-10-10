using Cleat.Emit;
using NUnit.Framework;

namespace Cleat.Core.Tests;

// cleat-019：Lcil 规格（src/Cleat.Emit/LCIL.md）的读取与校验
public class LcilParserTests
{
    private static LcilAssembly Parse(string body) => LcilParser.Parse("(lcil 1 (assembly \"t\") " + body + ")", "t.lcil");

    private static string Fails(string body)
    {
        var e = Assert.Throws<LcilException>(() => Parse(body));
        return e.Message;
    }

    [Test]
    public void ParsesTypesFieldsMethodsAndClauses()
    {
        var a = Parse(@"
          (constants (0 (nil)) (1 (long 2)))
          (type ""I"" (attrs interface) (method ""m"" (attrs abstract) (params ""System.Object"") (ret ""System.Object"")))
          (type ""T"" (base ""System.Object"") (attrs sealed) (interfaces ""I"") (attribute ""A"" ""x"")
            (field ""f"" ""System.Object"" volatile)
            (field ""s"" ""System.Int64"" static private)
            (method "".ctor"" (attrs ctor) (params)
              (body (ldarg 0) (call ""System.Object"" "".ctor"" () ""System.Void"") (ret)))
            (method ""I.m"" (attrs private) (implements ""I"" ""m"") (params ""System.Object"") (ret ""System.Object"")
              (locals ""System.Object"")
              (body (ldarg 1) (stloc 0) (ldconst 1) (pop) (ldloc 0)
                    (switch (L1 L2)) (label L1) (label L2)
                    (try (body (ldarg 1) (stloc 0)) (catch ""System.Exception"" (pop)) (finally (nop)))
                    (ldloc 0) (ret))))");
        Assert.That(a.Types, Has.Count.EqualTo(2));
        var t = a.Types[1];
        Assert.That(t.Interfaces, Is.EqualTo(new[] { "I" }));
        Assert.That(t.Fields[0].IsVolatile, Is.True);
        Assert.That(t.Fields[1].IsStatic, Is.True);
        Assert.That(t.Methods[1].ImplementsType, Is.EqualTo("I"));
        Assert.That(t.Methods[1].ImplementsName, Is.EqualTo("m"));
        Assert.That(t.Attributes[0].Type, Is.EqualTo("A"));
        Assert.That(a.Constants, Has.Count.EqualTo(2));
    }

    [Test]
    public void ReportsErrorsWithPositions()
    {
        Assert.That(Fails("(type \"T\" (method \"m\" (params) (ret \"System.Object\") (body (br L9) (ret))))"), Does.Contain("undefined label L9").And.Contain("t.lcil:"));
        Assert.That(Fails("(type \"T\" (method \"m\" (params) (ret \"System.Object\") (body (label L) (label L) (ret))))"), Does.Contain("duplicate label"));
        Assert.That(Fails("(type \"T\" (method \"m\" (params) (ret \"System.Object\") (body (ldarg 3) (ret))))"), Does.Contain("argument index out of range"));
        Assert.That(Fails("(type \"T\" (method \"m\" (params) (ret \"System.Object\") (body (ldloc 0) (ret))))"), Does.Contain("local index out of range"));
        Assert.That(Fails("(type \"T\" (method \"m\" (params) (ret \"System.Object\") (body (ldconst 0) (ret))))"), Does.Contain("constant index out of range"));
        Assert.That(Fails("(constants (1 (nil)))"), Does.Contain("constant index must be 0"));
        Assert.That(Fails("(type \"T\" (method \"m\" (params) (ret \"System.Object\") (body (tail) (ldnull) (ret))))"), Does.Contain("(tail) must be followed by call"));
        Assert.That(Fails("(type \"T\" (method \"m\" (params) (ret \"System.Object\") (body (line 1 1 1 2) (ldnull) (ret))))"), Does.Contain("needs a (source"));
        Assert.That(Fails("(type \"T\" (method \"m\" (implements \"I\" \"m\") (params) (ret \"System.Object\") (body (ldnull) (ret))))"), Does.Contain("does not list it"));
        Assert.That(Fails("(type \"I\" (attrs interface) (field \"f\" \"System.Object\"))"), Does.Contain("interface cannot have instance fields"));
        Assert.That(Fails("(type \"I\" (attrs interface) (method \"m\" (params) (ret \"System.Object\") (body (ret))))"), Does.Contain("must be abstract"));
        Assert.That(Fails("(type \"T\" (method \"m\" (params) (ret \"System.Object\") (body (frobnicate) (ret))))"), Does.Contain("unknown instruction: frobnicate"));
        Assert.That(Fails("(type \"T\" (method \"m\" (params) (ret \"System.Object\") (body (ldc.i4) (ret))))"), Does.Contain("wrong number of operands"));
        Assert.That(Fails("(type \"T\" (method \"x\" (attrs ctor) (params) (body (ret))))"), Does.Contain("must be named .ctor"));
        Assert.That(Fails("(type \"T\") (type \"T\")"), Does.Contain("duplicate type T"));
        Assert.That(Fails("(type \"T\" (field \"f\" \"System.Object\" flying))"), Does.Contain("unknown field attribute: flying"));
    }

    [Test]
    public void ReaderHandlesLexicalForms()
    {
        var items = new SExprReader("; comment\n#| block |# (a [b \"s\\x41;\\n\"] #t #f 12 -3.5 #x1F)", "x").ReadAll();
        var l = (SList)items[0];
        Assert.That(l.Count, Is.EqualTo(7));
        Assert.That(((SStr)((SList)l[1])[1]).Value, Is.EqualTo("sA\n"));
        Assert.That(((SInt)l[6]).Value, Is.EqualTo(31));
        Assert.That(((SReal)l[5]).Value, Is.EqualTo(-3.5));
        Assert.Throws<LcilException>(() => new SExprReader("(a", "x").ReadAll());
        Assert.Throws<LcilException>(() => new SExprReader("(a]", "x").ReadAll());
    }
}
