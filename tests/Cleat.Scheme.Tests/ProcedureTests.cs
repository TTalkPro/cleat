using System;
using System.Linq;
using Cleat.Scheme;
using NUnit.Framework;

namespace Cleat.Scheme.Tests;

// 模拟编译器生成的三种过程类

/// <summary>(lambda (a b) (+ a b))</summary>
internal sealed class Add2 : Procedure
{
    public override object Invoke2(object a, object b) => (int)a + (int)b;
}

/// <summary>(lambda (a . rest) (cons a rest))</summary>
internal sealed class ConsRest : VariadicProcedure
{
    public override object InvokeN(object[] args) =>
      args.Length < 1 ? ArityError(args.Length) : new Cons(args[0], RestList(args, 1));
}

/// <summary>(case-lambda [(a) 'one] [(a b c) 'three])</summary>
internal sealed class OneOrThree : Procedure
{
    public override object Invoke1(object a) => "one";
    public override object Invoke3(object a, object b, object c) => "three";
}

/// <summary>测试用：把 C# 委托包成过程。</summary>
internal sealed class Fn : VariadicProcedure
{
    private readonly Func<object[], object> _f;
    public Fn(Func<object[], object> f) { _f = f; }
    public override object InvokeN(object[] args) => _f(args);
}

public class ProcedureDispatchTests
{
    private static SchemeRuntimeError Arity(TestDelegate d)
    {
        var e = Assert.Throws<SchemeRuntimeError>(d);
        Assert.That(e.Message, Does.Contain("incorrect number of arguments"));
        return e;
    }

    [Test]
    public void FixedArity()
    {
        var p = new Add2();
        Assert.That(p.Invoke2(1, 2), Is.EqualTo(3));
        Assert.That(p.InvokeN(new object[] { 1, 2 }), Is.EqualTo(3));     // 统一入口分派到 Invoke2
        Assert.That(Arity(() => p.Invoke1(1)).Who, Is.EqualTo("Add2"));
        Arity(() => p.InvokeN(new object[] { 1, 2, 3, 4, 5 }));
    }

    [Test]
    public void Variadic()
    {
        var p = new ConsRest();
        Assert.That(((Cons)p.Invoke3(1, 2, 3)).ToArray(), Is.EqualTo(new object[] { 1, 2, 3 }));
        Assert.That(((Cons)p.Invoke1(1)).cdr, Is.SameAs(EmptyList.Instance));
        Assert.That(((Cons)p.InvokeN(Enumerable.Range(0, 6).Cast<object>().ToArray())).Count(), Is.EqualTo(6));
        Arity(() => p.Invoke0());
    }

    [Test]
    public void CaseLambdaLike()
    {
        var p = new OneOrThree();
        Assert.That(p.InvokeN(new object[] { 1 }), Is.EqualTo("one"));
        Assert.That(p.InvokeN(new object[] { 1, 2, 3 }), Is.EqualTo("three"));
        Arity(() => p.Invoke2(1, 2));
    }

    [Test]
    public void Printing()
    {
        Assert.That(new Add2().ToString(), Is.EqualTo("#<procedure Add2>"));
    }

    [Test]
    public void BoxHoldsValue()
    {
        var b = new Box(1);
        b.Value = 2;
        Assert.That(b.Value, Is.EqualTo(2));
    }
}

public class ApplyTests
{
    [Test]
    public void SpreadsLastList()
    {
        var add = new Add2();
        Assert.That(Control.Apply(add, Cons.FromArray(1, 2)), Is.EqualTo(3));
        Assert.That(Control.Apply(add, 1, Cons.FromArray(2)), Is.EqualTo(3));
        Assert.That(Control.Apply(add, 1, 2, EmptyList.Instance), Is.EqualTo(3));
    }

    [Test]
    public void ManyArgumentsGoThroughInvokeN()
    {
        var r = (Cons)Control.Apply(new ConsRest(), 1, 2, Cons.FromArray(3, 4, 5, 6, 7));
        Assert.That(r.ToArray(), Is.EqualTo(new object[] { 1, 2, 3, 4, 5, 6, 7 }));
    }

    [Test]
    public void Errors()
    {
        Assert.Throws<SchemeRuntimeError>(() => Control.Apply(new Add2()));
        Assert.Throws<SchemeRuntimeError>(() => Control.Apply(new Add2(), 1, new Cons(2, 3)));   // 非正规列表
        Assert.Throws<SchemeRuntimeError>(() => Control.Apply("not-a-procedure", null));
    }
}

public class CallWithValuesTests
{
    [Test]
    public void MultipleSingleAndZeroValues()
    {
        Assert.That(Control.CallWithValues(new Fn(_ => Control.Values(1, 2)), new Add2()), Is.EqualTo(3));
        Assert.That(Control.CallWithValues(new Fn(_ => 5), new Fn(a => a.Length)), Is.EqualTo(1));
        Assert.That(Control.CallWithValues(new Fn(_ => Control.Values()), new Fn(a => a.Length)), Is.EqualTo(0));
    }

    [Test]
    public void ValuesOfOneIsTheValue()
    {
        Assert.That(Control.Values(7), Is.EqualTo(7));
        Assert.That(Control.Values(1, 2), Is.InstanceOf<MultipleValues>());
    }
}

public class EscapeContinuationTests
{
    private static object CallCC(Func<Procedure, object> body) => Control.CallWithReturn(new Fn(a => body((Procedure)a[0])));

    [Test]
    public void EscapesWithValue()
    {
        // (call-with-return (lambda (k) (+ 1 (k 42)))) => 42
        Assert.That(CallCC(k => 1 + (int)k.Invoke1(42)), Is.EqualTo(42));
    }

    [Test]
    public void NormalReturn()
    {
        Assert.That(CallCC(k => 7), Is.EqualTo(7));
    }

    [Test]
    public void EscapesWithMultipleValues()
    {
        var r = CallCC(k => k.Invoke2(1, 2));
        Assert.That(((MultipleValues)r).ToArray(), Is.EqualTo(new object[] { 1, 2 }));
    }

    [Test]
    public void InnerEscapesToOuterFrame()
    {
        // (call-with-return (lambda (outer) (call-with-return (lambda (inner) (outer 'out))) 'not-reached))
        var r = CallCC(outer => { CallCC(inner => outer.Invoke1("out")); return "not-reached"; });
        Assert.That(r, Is.EqualTo("out"));
    }

    [Test]
    public void InvalidAfterFrameExits()
    {
        Procedure saved = null;
        CallCC(k => { saved = k; return 0; });
        var e = Assert.Throws<SchemeRuntimeError>(() => saved.Invoke1(1));
        Assert.That(e.Message, Does.Contain("no longer valid"));
    }

    [Test]
    public void OtherExceptionsPassThroughAndInvalidate()
    {
        Procedure saved = null;
        Assert.Throws<InvalidOperationException>(() => CallCC(k => { saved = k; throw new InvalidOperationException(); }));
        Assert.That(((EscapeContinuation)saved).IsValid, Is.False);
    }
}
