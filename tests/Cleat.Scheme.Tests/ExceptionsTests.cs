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
    public void SchemeExceptionIsAClrException()
    {
        // Scheme 条件以 SchemeException 传播；with-clr-exception-handler 收到的是异常对象本身（不转成条件）
        var r = Exceptions.WithClrExceptionHandler(
          new Fn(a => ((SchemeException)a[0]).Condition),
          new Fn(_ => throw new SchemeException("cond")));
        Assert.That(r, Is.EqualTo("cond"));
    }

    [Test]
    public void StackTraceIsLines()
    {
        var st = (object[])Exceptions.GetStackTrace();
        Assert.That(st, Is.Not.Empty);
    }
}
