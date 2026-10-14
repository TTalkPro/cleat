using System;
using System.IO;
using Cleat.Core.Runtime;
using clojure.lang;
using NUnit.Framework;

namespace Cleat.Core.Tests;

// cleat-030：宿主的命令行与 REPL（用假的 eval 钩子；真正的求值由编译器的驱动登记）
public class HostTests
{
    [Test]
    public void ParsesClojureMainStyleArguments()
    {
        var o = Cleat.Host.Program.Parse(new[] { "--boot", "b.dll", "-I", "lib", "-e", "(+ 1 2)", "-i", "x.clj", "script.clj", "a", "-b" });
        Assert.That(o.Boot, Is.EqualTo("b.dll"));
        Assert.That(o.IncludeDirs, Is.EqualTo(new[] { "lib" }));
        Assert.That(o.Inits, Is.EqualTo(new[] { ("eval", "(+ 1 2)"), ("init", "x.clj") }));
        Assert.That(o.Script, Is.EqualTo("script.clj"));
        Assert.That(o.ScriptArgs, Is.EqualTo(new[] { "a", "-b" }));
        Assert.That(o.Repl, Is.False);
        var m = Cleat.Host.Program.Parse(new[] { "-m", "my.ns", "1", "2" });
        Assert.That(m.MainNs, Is.EqualTo("my.ns"));
        Assert.That(m.ScriptArgs, Is.EqualTo(new[] { "1", "2" }));
        Assert.That(Cleat.Host.Program.Parse(Array.Empty<string>()).Repl, Is.True);
        Assert.That(Cleat.Host.Program.Parse(new[] { "-e", "1" }).Repl, Is.False);
        Assert.That(Cleat.Host.Program.Parse(new[] { "-r" }).Repl, Is.True);
        Assert.Throws<ArgumentException>(() => Cleat.Host.Program.Parse(new[] { "--frob" }));
    }

    [Test]
    public void ReplEvaluatesPrintsAndTracksHistory()
    {
        var saved = RuntimeHooks.Eval;
        RuntimeHooks.Eval = form => form is ISeq s && Symbol.intern("boom").Equals(RT.first(s)) ? throw new InvalidOperationException("kaboom") : (object)RT.printString(form).Length;
        try
        {
            RT.Init();
            Var.pushThreadBindings(RT.map(RT.CurrentNSVar, Namespace.findOrCreate(Symbol.intern("user"))));
            try
            {
                var output = new StringWriter();
                var error = new StringWriter();
                int code = Cleat.Host.Program.Repl(new StringReader("(a b)\n[1 2 3]\n(boom)\n*1\n:repl/quit\n(never)"), output, error);
                Assert.That(code, Is.EqualTo(0));
                Assert.That(output.ToString(), Is.EqualTo("user=> 5\nuser=> 7\nuser=> user=> 2\nuser=> "));
                Assert.That(error.ToString(), Does.Contain("InvalidOperationException: kaboom"));
            }
            finally { Var.popThreadBindings(); }
        }
        finally { RuntimeHooks.Eval = saved; }
    }

    [Test]
    public void EvalStringRequiresTheHook()
    {
        var saved = RuntimeHooks.Eval;
        RuntimeHooks.Eval = null;
        try
        {
            var e = Assert.Throws<InvalidOperationException>(() => Cleat.Host.Program.EvalString("1", "<t>"));
            Assert.That(e.Message, Does.Contain("runtime hook 'eval' is not registered"));
            RuntimeHooks.Eval = form => form;
            Assert.That(Cleat.Host.Program.EvalString("1 2 3", "<t>"), Is.EqualTo(3L));
        }
        finally { RuntimeHooks.Eval = saved; }
    }
}
