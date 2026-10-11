using System;
using Cleat.Scheme;
using NUnit.Framework;

namespace Cleat.Scheme.Tests;

public class ExceptionsTests
{
    [Test]
    public void ClrExceptionGoesToHandler()
    {
        var r = Exceptions.WithClrExceptionHandler(
          new Fn(a => "caught:" + ((Exception)a[0]).GetType().Name),
          new Fn(_ => throw new InvalidOperationException()));
        Assert.That(r, Is.EqualTo("caught:InvalidOperationException"));
    }

    [Test]
    public void NormalReturnPassesThrough()
    {
        Assert.That(Exceptions.WithClrExceptionHandler(new Fn(_ => "h"), new Fn(_ => 42)), Is.EqualTo(42));
    }

    [Test]
    public void ContinuationIsNotIntercepted()
    {
        // (call-with-return (lambda (k) (with-clr-exception-handler (lambda (e) 'handled) (lambda () (k 'escaped)))))
        var r = Control.CallWithReturn(new Fn(a =>
          Exceptions.WithClrExceptionHandler(new Fn(_ => "handled"), new Fn(_ => ((Procedure)a[0]).Invoke1("escaped")))));
        Assert.That(r, Is.EqualTo("escaped"));
    }

    [Test]
    public void StackTraceIsLines()
    {
        var st = (object[])Exceptions.GetStackTrace();
        Assert.That(st, Is.Not.Empty);
    }
}
