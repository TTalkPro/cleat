using System;
using System.Linq;
using Cleat.Scheme;
using NUnit.Framework;

namespace Cleat.Scheme.Tests;

[NonParallelizable]
public class RuntimeHooksTests
{
    [TearDown]
    public void ClearHooks() => RuntimeHooks.Clear();

    /// <summary>模拟 Scheme 侧条件过程：把收到的参数包成"条件"并 raise（抛 SchemeException）。</summary>
    private static Procedure Raising(string kind) =>
      new Fn(args => throw new SchemeException(new Cons(Symbol.Intern(kind), Cons.FromArray(args))));

    private static object[] RaisedArgs(TestDelegate d, string kind)
    {
        var e = Assert.Throws<SchemeException>(d);
        var cond = (Cons)e.Condition;
        Assert.That(cond.car, Is.SameAs(Symbol.Intern(kind)));
        return ((Cons)cond.cdr)?.ToArray() ?? Array.Empty<object>();
    }

    [Test]
    public void SixteenHooksLegacyGluePlusClrExceptionViolation()
    {
        Assert.That(RuntimeHooks.Names.ToArray(), Is.EquivalentTo(new[] {
    "assertion-violation", "undefined-error", "lexical-error", "syntax-error", "i/o-port-violation",
    "file-not-found-violation", "file-in-use-violation", "file-already-exists-violation",
    "io-decoding-error", "io-encoding-error", "raise", "raise-continuable", "with-exception-handler",
    "exact", "inexact", "clr-exception-violation" }));
    }

    [Test]
    public void SetRuntimeHookPrimitive()
    {
        var p = Raising("raise");
        Assert.That(RuntimeHooks.SetRuntimeHook(Symbol.Intern("raise"), p), Is.SameAs(Unspecified.Instance));
        Assert.That(RuntimeHooks.Raise, Is.SameAs(p));
        Assert.Throws<SchemeRuntimeError>(() => RuntimeHooks.SetRuntimeHook(Symbol.Intern("no-such-hook"), p));
        Assert.Throws<SchemeRuntimeError>(() => RuntimeHooks.SetRuntimeHook("raise", p));   // 名字必须是符号
        Assert.Throws<ArgumentException>(() => RuntimeHooks.Set("no-such-hook", p));
    }

    [Test]
    public void MissingHooksAreReported()
    {
        Assert.That(((Cons)RuntimeHooks.Missing()).Count(), Is.EqualTo(16));
        RuntimeHooks.SetRuntimeHook(Symbol.Intern("exact"), new Fn(a => a[0]));
        var missing = ((Cons)RuntimeHooks.Missing()).ToArray();
        Assert.That(missing, Has.Length.EqualTo(15));
        Assert.That(missing, Does.Not.Contain(Symbol.Intern("exact")));
        foreach (var n in RuntimeHooks.Names) RuntimeHooks.Set(n, new Fn(a => null));
        Assert.That(RuntimeHooks.Missing(), Is.Null);   // '()
    }

    [Test]
    public void AssertionViolationSpreadsIrritants()
    {
        RuntimeHooks.AssertionViolation = Raising("assertion-violation");
        var args = RaisedArgs(() => Errors.AssertionViolation("car", "not a pair", 1, 2), "assertion-violation");
        Assert.That(args, Is.EqualTo(new object[] { "car", "not a pair", 1, 2 }));
    }

    [Test]
    public void AssertionViolationWithoutWhoPassesFalse()
    {
        RuntimeHooks.AssertionViolation = Raising("assertion-violation");
        var args = RaisedArgs(() => Errors.AssertionViolation(null, "oops"), "assertion-violation");
        Assert.That(args[0], Is.SameAs(RT.False));
    }

    [Test]
    public void ArityErrorGoesThroughHook()
    {
        RuntimeHooks.AssertionViolation = Raising("assertion-violation");
        var args = RaisedArgs(() => new Add2().Invoke1(1), "assertion-violation");
        Assert.That(args[0], Is.EqualTo("Add2"));
        Assert.That(args[1], Is.EqualTo("incorrect number of arguments"));
    }

    [Test]
    public void UnboundSymbolGoesThroughUndefinedErrorHook()
    {
        RuntimeHooks.UndefinedError = Raising("undefined-error");
        var s = Symbol.Gensym("nowhere");
        var args = RaisedArgs(() => SymbolPrimitives.SymbolValue(s), "undefined-error");
        Assert.That(args, Is.EqualTo(new object[] { s }));
    }

    [Test]
    public void OtherErrorHooksReceiveTheirArguments()
    {
        RuntimeHooks.LexicalError = Raising("lexical-error");
        Assert.That(RaisedArgs(() => Errors.LexicalError("bad token", "#q"), "lexical-error"), Is.EqualTo(new object[] { "bad token", "#q" }));

        RuntimeHooks.SyntaxError = Raising("syntax-error");
        Assert.That(RaisedArgs(() => Errors.SyntaxError("if", "bad", 1, 2), "syntax-error"), Is.EqualTo(new object[] { "if", "bad", 1, 2 }));

        RuntimeHooks.IOPortViolation = Raising("i/o-port-violation");
        Assert.That(RaisedArgs(() => Errors.IOPortViolation("read", "closed", 9), "i/o-port-violation"), Is.EqualTo(new object[] { "read", "closed", 9 }));

        RuntimeHooks.FileNotFoundViolation = Raising("file-not-found-violation");
        Assert.That(RaisedArgs(() => Errors.FileNotFoundViolation("open", "no file", "x.txt"), "file-not-found-violation"), Is.EqualTo(new object[] { "open", "no file", "x.txt" }));

        RuntimeHooks.FileInUseViolation = Raising("file-in-use-violation");
        Assert.That(RaisedArgs(() => Errors.FileInUseViolation("open", "x.txt"), "file-in-use-violation"), Is.EqualTo(new object[] { "open", "x.txt" }));

        RuntimeHooks.FileAlreadyExistsViolation = Raising("file-already-exists-violation");
        Assert.That(RaisedArgs(() => Errors.FileAlreadyExistsViolation("open", "x.txt"), "file-already-exists-violation"), Is.EqualTo(new object[] { "open", "x.txt" }));

        RuntimeHooks.IODecodingError = Raising("io-decoding-error");
        Assert.That(RaisedArgs(() => Errors.IODecodingError(), "io-decoding-error"), Is.Empty);

        RuntimeHooks.IOEncodingError = Raising("io-encoding-error");
        Assert.That(RaisedArgs(() => Errors.IOEncodingError(), "io-encoding-error"), Is.Empty);
    }

    [Test]
    public void FallbackWhenNotRegistered()
    {
        var e = Assert.Throws<SchemeRuntimeError>(() => Errors.LexicalError("bad", "x"));
        Assert.That(e.Kind, Is.EqualTo("lexical-error"));
        // raise 未登记时与 Scheme 默认处理器一致：包成 SchemeException
        var se = Assert.Throws<SchemeException>(() => Errors.Raise("cond"));
        Assert.That(se.Condition, Is.EqualTo("cond"));
    }

    [Test]
    public void NonErrorHooks()
    {
        var ioe = Assert.Throws<InvalidOperationException>(() => SchemeCalls.Exact(1.0));
        Assert.That(ioe.Message, Does.Contain("'exact' is not registered"));

        RuntimeHooks.Exact = new Fn(a => (int)(double)a[0]);
        Assert.That(SchemeCalls.Exact(2.0), Is.EqualTo(2));

        RuntimeHooks.RaiseContinuable = new Fn(a => "handled:" + a[0]);
        Assert.That(SchemeCalls.RaiseContinuable("w"), Is.EqualTo("handled:w"));

        RuntimeHooks.WithExceptionHandler = new Fn(a => ((Procedure)a[1]).Invoke0());
        Assert.That(SchemeCalls.WithExceptionHandler(new Fn(_ => null), new Fn(_ => 5)), Is.EqualTo(5));
    }
}
