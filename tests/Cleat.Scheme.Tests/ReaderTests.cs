using System.IO;
using System.Linq;
using Cleat.Scheme;
using NUnit.Framework;
using BigInteger = System.Numerics.BigInteger;

namespace Cleat.Scheme.Tests;

public class ReaderTests
{
    private static object Read(string s) => new SchemeReader(new StringReader(s), "", false).Read();
    private static object[] ReadAll(string s) => new SchemeReader(new StringReader(s), "", false).ReadAll().ToArray();
    private static Symbol S(string n) => Symbol.Intern(n);
    private static object[] L(object o) => ((Cons)o)?.ToArray() ?? new object[0];

    [Test]
    public void Atoms()
    {
        Assert.That(Read("foo"), Is.SameAs(S("foo")));
        Assert.That(Read("42"), Is.EqualTo(42));
        Assert.That(Read("#t"), Is.SameAs(RT.True));
        Assert.That(Read("#false"), Is.SameAs(RT.False));
        Assert.That(Read("#F"), Is.SameAs(RT.False));
        Assert.That(Read("\"a\\nb\""), Is.EqualTo("a\nb"));
        Assert.That(Read(""), Is.SameAs(Builtins.EofObject()));
    }

    [Test]
    public void ListsVectorsBrackets()
    {
        Assert.That(L(Read("(1 2 3)")), Is.EqualTo(new object[] { 1, 2, 3 }));
        Assert.That(L(Read("[a b]")), Is.EqualTo(new object[] { S("a"), S("b") }));
        var dotted = (Cons)Read("(1 . 2)");
        Assert.That(dotted.car, Is.EqualTo(1)); Assert.That(dotted.cdr, Is.EqualTo(2));
        Assert.That(Read("()"), Is.Null);
        Assert.That(Read("#(1 #(2))"), Is.EqualTo(new object[] { 1, new object[] { 2 } }));
        Assert.Throws<ReaderException>(() => Read("(1 2]"));
        Assert.Throws<ReaderException>(() => Read("(1 2"));
    }

    [Test]
    public void InfixDot()
    {
        Assert.That(L(Read("(1 . + . 2)")), Is.EqualTo(new object[] { S("+"), 1, 2 }));
        Assert.That(L(Read("(a . f . b c)")), Is.EqualTo(new object[] { S("f"), S("a"), S("b"), S("c") }));
    }

    [Test]
    public void Abbreviations()
    {
        Assert.That(L(Read("'x")), Is.EqualTo(new object[] { S("quote"), S("x") }));
        Assert.That(L(Read("`x"))[0], Is.SameAs(S("quasiquote")));
        Assert.That(L(Read(",@x"))[0], Is.SameAs(S("unquote-splicing")));
        Assert.That(L(Read("#'x"))[0], Is.SameAs(S("syntax")));
        Assert.That(L(Read("#,@x"))[0], Is.SameAs(S("unsyntax-splicing")));
    }

    [Test]
    public void CommentsAndDirectives()
    {
        Assert.That(ReadAll("; c\n1 #| a #| nested |# b |# 2 #;(ignored 3) 4"), Is.EqualTo(new object[] { 1, 2, 4 }));
        Assert.That(ReadAll("#!r6rs 1"), Is.EqualTo(new object[] { 1 }));
        Assert.That(ReadAll("#!/usr/bin/env scheme-script\n5"), Is.EqualTo(new object[] { 5 }));
        Assert.That(ReadAll("1 #!eof 2 3"), Is.EqualTo(new object[] { 1 }));
        Assert.That(ReadAll("#!fold-case ABC #!no-fold-case ABC"), Is.EqualTo(new object[] { S("abc"), S("ABC") }));
        Assert.That(L(Read("(1 #;2)")), Is.EqualTo(new object[] { 1 }));
    }

    [Test]
    public void Characters()
    {
        Assert.That(Read("#\\a"), Is.EqualTo('a'));                       // BMP：System.Char
        Assert.That(Read("#\\space"), Is.EqualTo(' '));
        Assert.That(Read("#\\x41"), Is.EqualTo('A'));
        Assert.That(Read("#\\x"), Is.EqualTo('x'));
        Assert.That(Read("#\\("), Is.EqualTo('('));
        Assert.That(Read("#\\nul"), Is.EqualTo('\0'));
        Assert.That(Read("#\\x1F600"), Is.EqualTo(new SchemeChar(0x1F600)));  // BMP 之外：SchemeChar
        Assert.That(Read("#\\\U0001F600"), Is.EqualTo(new SchemeChar(0x1F600)));
        Assert.Throws<ReaderException>(() => Read("#\\bogus"));
        Assert.Throws<ReaderException>(() => Read("#\\xD800"));
    }

    [Test]
    public void Strings()
    {
        Assert.That(Read("\"\\x41;\\t\\\\\""), Is.EqualTo("A\t\\"));
        Assert.That(Read("\"a\\\n    b\""), Is.EqualTo("ab"));            // 行接续
        Assert.That(Read("\"a\\  \n  b\""), Is.EqualTo("ab"));            // \ 后有行内空白
        Assert.That(Read("\"x\r\ny\""), Is.EqualTo("x\ny"));              // 裸 \r 被删除
        Assert.That(Read("\"\\q\""), Is.EqualTo("\\q"));                  // 未知转义保留原文
        Assert.Throws<ReaderException>(() => Read("\"abc"));
    }

    [Test]
    public void Identifiers()
    {
        Assert.That(Read("..."), Is.SameAs(S("...")));
        Assert.That(Read("->x"), Is.SameAs(S("->x")));
        Assert.That(Read("+"), Is.SameAs(S("+")));
        Assert.That(Read("set-car!"), Is.SameAs(S("set-car!")));
        Assert.That(Read("Int32[]"), Is.SameAs(S("Int32[]")));            // CLR 数组类型名
        var ab = L(Read("(a[b])"));                                        // a 后的 [ 不是 [] 对：开始新的列表
        Assert.That(ab[0], Is.SameAs(S("a")));
        Assert.That(L(ab[1]), Is.EqualTo(new object[] { S("b") }));
        Assert.That(Read("\\x41;bc"), Is.SameAs(S("Abc")));
        Assert.That(Read("λ"), Is.SameAs(S("λ")));
        Assert.Throws<ReaderException>(() => Read("1abc"));
    }

    [Test]
    public void ValueVectors()
    {
        Assert.That(Read("#vu8(1 2 255)"), Is.EqualTo(new byte[] { 1, 2, 255 }));
        Assert.That(Read("#u8()"), Is.EqualTo(new byte[0]));
        Assert.That(Read("#fx(1 2)"), Is.EqualTo(new[] { 1, 2 }));
        Assert.Throws<ReaderException>(() => Read("#vu8(256)"));
        Assert.Throws<ReaderException>(() => Read("#zz(1)"));
    }

    [Test]
    public void Annotations()
    {
        var r = new SchemeReader(new StringReader("(define x\n  (+ 1 2))"), "a.sls", true);
        var a = (ConsAnnotation)r.Read();
        Assert.That(((Cons)a.source).car, Is.EqualTo("a.sls"));
        Assert.That(((Cons)a.source).cdr, Is.EqualTo("(1,1) - (2,11)"));
        Assert.That(L(a.stripped)[0], Is.SameAs(S("define")));
        var first = (Annotation)((Cons)a.expression).car;
        Assert.That(first.stripped, Is.SameAs(S("define")));
        Assert.That(((Cons)first.source).cdr, Is.EqualTo("(1,2) - (1,8)"));
        var inner = (ConsAnnotation)L(a.expression)[2];
        Assert.That(L(inner.stripped), Is.EqualTo(new object[] { S("+"), 1, 2 }));
        Assert.That(Builtins.SourceLocation("(1,2) - (1,8)").ToString(), Is.Not.Null);
        var loc = (Cons)Builtins.SourceLocation("(1,2) - (3,8)");
        Assert.That(((Cons)loc.car).car, Is.EqualTo(1)); Assert.That(((Cons)loc.car).cdr, Is.EqualTo(2));
    }

    [Test]
    public void StatePersistsAcrossReadsOnSamePort()
    {
        var port = new StringReader("a\n  b");
        new SchemeReader(port, "f", true).Read();
        var b = (Annotation)new SchemeReader(port, "f", true).Read();
        Assert.That(((Cons)b.source).cdr, Is.EqualTo("(2,3) - (2,4)"));
        Assert.That(port.Read(), Is.EqualTo(-1));                          // 逐个数据读取，不多读
    }

    [Test]
    public void ReadDoesNotConsumeBeyondDatum()
    {
        var port = new StringReader("(a) rest");
        new SchemeReader(port, "", false).Read();
        Assert.That(port.ReadToEnd(), Is.EqualTo(" rest"));                // read 与 read-char 可交替使用
    }

    [Test]
    public void LexicalErrorThroughPrimitive()
    {
        var e = Assert.Throws<SchemeRuntimeError>(() => Builtins.ReadAnnotated(new StringReader("(1 . )")));
        Assert.That(e.Kind, Is.EqualTo("lexical-error"));
        Assert.That(e.Message, Does.Contain("at (1,"));
    }
}

public class NumberParserTests
{
    private static object N(string s) => Builtins.ParseNumber(s);

    [Test]
    public void Integers()
    {
        Assert.That(N("0"), Is.EqualTo(0));
        Assert.That(N("-17"), Is.EqualTo(-17));
        Assert.That(N("+5"), Is.EqualTo(5));
        Assert.That(N("#x1F"), Is.EqualTo(31));
        Assert.That(N("#b-101"), Is.EqualTo(-5));
        Assert.That(N("#o17"), Is.EqualTo(15));
        Assert.That(N("12345678901234567890").ToString(), Is.EqualTo("12345678901234567890"));
        Assert.That(N("12345678901234567890"), Is.InstanceOf<BigInteger>());
    }

    [Test]
    public void Rationals()
    {
        Assert.That(N("1/2"), Is.InstanceOf<Fraction>());
        Assert.That(N("4/2"), Is.EqualTo(2).And.InstanceOf<int>());
        Assert.That(N("1/0"), Is.Null);
        Assert.That(N("#x-A/2"), Is.EqualTo(-5));
    }

    [Test]
    public void Decimals()
    {
        Assert.That(N("1.5"), Is.EqualTo(1.5));
        Assert.That(N(".5"), Is.EqualTo(0.5));
        Assert.That(N("1."), Is.EqualTo(1.0));
        Assert.That(N("1e2"), Is.EqualTo(100.0).And.InstanceOf<double>());
        Assert.That(N("1s2"), Is.EqualTo(100.0));
        Assert.That(N("1.5|53"), Is.EqualTo(1.5));
        Assert.That(N("1e400"), Is.EqualTo(double.PositiveInfinity));
        Assert.That(double.IsNegative((double)N("-0.0")), Is.True);
        Assert.That(N("0.1"), Is.EqualTo(0.1));
        Assert.That(N("0e9999999999999999999999"), Is.EqualTo(0.0));        // 指数超出 int 范围
        Assert.That(N("1e9999999999999999999999"), Is.EqualTo(double.PositiveInfinity));
        Assert.That(N("1e-9999999999999999999999"), Is.EqualTo(0.0));
        Assert.That(N("#e1e9999999999999999999999"), Is.Null);
    }

    [Test]
    public void Exactness()
    {
        Assert.That(N("#e1.5"), Is.InstanceOf<Fraction>());
        Assert.That(Builtins.Multiply(N("#e1.5"), 2), Is.EqualTo(3));
        Assert.That(Builtins.Multiply(N("#e1.1"), 10), Is.EqualTo(11));    // 11/10，而非二进制近似
        Assert.That(N("#i3"), Is.EqualTo(3.0).And.InstanceOf<double>());
        Assert.That(N("#i1/4"), Is.EqualTo(0.25));
        Assert.That(N("#x#e10"), Is.EqualTo(16));
        Assert.That(N("#e#x10"), Is.EqualTo(16));
        Assert.That(N("#e#e1"), Is.Null);
        Assert.That(N("#e+inf.0"), Is.Null);
        Assert.That(N("#e1e3"), Is.EqualTo(1000));
    }

    [Test]
    public void NanInf()
    {
        Assert.That(N("+inf.0"), Is.EqualTo(double.PositiveInfinity));
        Assert.That(N("-inf.0"), Is.EqualTo(double.NegativeInfinity));
        Assert.That(double.IsNaN((double)N("+nan.0")), Is.True);
        Assert.That(N("inf.0"), Is.Null);
    }

    [Test]
    public void Complex()
    {
        Assert.That(N("1+2i"), Is.InstanceOf<ComplexFraction>());
        Assert.That(N("+i"), Is.InstanceOf<ComplexFraction>());
        Assert.That(N("-2.5i"), Is.InstanceOf<Complex64>());
        Assert.That(N("1.0+0i"), Is.EqualTo(1.0));                          // 精确 0 虚部：返回实部
        Assert.That(N("1+0.0i"), Is.InstanceOf<Complex64>());
        Assert.That(N("1e2+3i"), Is.InstanceOf<Complex64>());               // 指数中的 + 不是分界
        Assert.That(N("+inf.0i"), Is.InstanceOf<Complex64>());
        Assert.That(N("1@0"), Is.EqualTo(1));
        Assert.That(N("2@0.0"), Is.InstanceOf<double>().Or.InstanceOf<Complex64>());
    }

    [Test]
    public void NotNumbers()
    {
        foreach (var s in new[] { "", "-", "+", "...", "1+", "abc", "#q1", "1..2", "#x1.5", "e5", "1/2/3", "--1" })
            Assert.That(N(s), Is.Null, s);
    }

    [Test]
    public void StringToNumberPrimitive()
    {
        Assert.That(Builtins.StringToNumber("ff", 16), Is.EqualTo(255));
        Assert.That(Builtins.StringToNumber("#b11", 16), Is.EqualTo(3));     // 前缀优先
        Assert.That(Builtins.StringToNumber("12", 7), Is.SameAs(RT.False));
        Assert.That(Builtins.StringToNumber("zz"), Is.SameAs(RT.False));
    }
}
