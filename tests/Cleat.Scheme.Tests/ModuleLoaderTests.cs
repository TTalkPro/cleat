using System;
using System.IO;
using System.Linq;
using Cleat.Emit;
using NUnit.Framework;

namespace Cleat.Scheme.Tests;

/// <summary>程序集入口约定与加载器（iron-021）。</summary>
public class ModuleLoaderTests
{
    private sealed class Thunk : Procedure
    {
        private readonly Func<object> _f;
        public Thunk(Func<object> f) { _f = f; }
        public override object Invoke0() => _f();
    }

    private static string Save(string sample)
    {
        var path = Path.Combine(Path.GetTempPath(), "iron-modules-" + Guid.NewGuid().ToString("N"), Path.ChangeExtension(sample, ".dll"));
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        LcilEmitter.Save(LcilParserTests.Sample(sample), sample, path);
        return path;
    }

    private static void CheckMiniBoot(object result)
    {
        Assert.That(result, Is.SameAs(Unspecified.Instance));
        var square = (Procedure)Symbol.Intern("mini-square").Value;
        Assert.That(square.Invoke1(7), Is.EqualTo(49));
        Assert.That(Symbol.Intern("mini-greeting").Value, Is.EqualTo("hello from miniboot"));
        var hook = RuntimeHooks.Get("with-exception-handler");
        Assert.That(hook.GetType().Name, Is.EqualTo("mini$weh"));
        // C# 代码经钩子调用 boot 镜像中的过程
        Assert.That(SchemeCalls.WithExceptionHandler(square, new Thunk(() => "thunk result")), Is.EqualTo("thunk result"));
    }

    private static void WithHookRestored(Action body)
    {
        var saved = RuntimeHooks.Get("with-exception-handler");
        try { body(); }
        finally { RuntimeHooks.Set("with-exception-handler", saved); }
    }

    [Test]
    public void MiniBootImageFromFile() => WithHookRestored(() =>
    {
        var path = Save("miniboot.lcil");
        CheckMiniBoot(ModuleLoader.LoadModule(path));
        Assert.That(ModuleLoader.EntryPoint(ModuleLoader.LoadAssembly(path)).Name, Is.EqualTo("Initialize"));
    });

    [Test]
    public void MiniBootImageLoadedAsBytes() => WithHookRestored(() =>
    {
        var path = Save("miniboot.lcil");
        Symbol.Intern("mini-square").Value = Unbound.Instance;
        CheckMiniBoot(ModuleLoader.LoadModule(path, inMemory: true));
    });

    [Test]
    public void MiniBootImageInMemory() => WithHookRestored(() =>
    {
        Symbol.Intern("mini-square").Value = Unbound.Instance;
        var asm = LcilEmitter.EmitInMemory(LcilParserTests.Sample("miniboot.lcil"), "miniboot.lcil");
        CheckMiniBoot(ModuleLoader.Run(asm));
    });

    [Test]
    public void LibraryDll()
    {
        var path = Save("minilib.lcil");
        var content = ((Cons)ModuleLoader.LoadLibraryDll(path)).ToList();
        Assert.That(content, Has.Count.EqualTo(13));
        Assert.That(content[0], Is.SameAs(Symbol.Intern("g$mini$1$%top")));
        Assert.That(((Cons)content[1]).Select(x => ((Symbol)x).Name), Is.EqualTo(new[] { "mini", "lib" }));
        Assert.That(((Procedure)content[9]).Invoke0(), Is.SameAs(Unspecified.Instance));
        Assert.That(content[12], Is.EqualTo(true));
    }

    [Test]
    public void Errors()
    {
        // 没有入口属性的程序集
        var plain = Path.Combine(Path.GetTempPath(), "iron-modules-" + Guid.NewGuid().ToString("N") + ".dll");
        LcilEmitter.Save(LcilParserTests.Sample("fact.lcil"), "fact.lcil", plain);
        Assert.That(Assert.Throws<InvalidOperationException>(() => ModuleLoader.LoadModule(plain)).Message, Does.Contain("not a Cleat module"));
        // 入口不合约定：发射时报错
        const string Bad = "(lcil 1 (assembly \"bad\" (entry \"M\" \"Init\")) (type \"M\" (method \"Init\" (attrs static) (params \"System.Object\") (ret \"System.Object\") (body (ldarg 0) (ret)))))";
        Assert.That(Assert.Throws<LcilException>(() => LcilEmitter.EmitInMemory(Bad)).Message, Does.Contain("must be a public static method with no parameters"));
        Assert.Throws<SchemeRuntimeError>(() => ModuleLoader.LoadModule("no/such/file.dll"));
        Assert.That(ModuleLoader.ResolvePath("~/x.dll"), Is.EqualTo(Path.Combine(Builtins.ApplicationDirectory, "x.dll")));
        Assert.That(ModuleLoader.ResolvePath("a/b.dll"), Is.EqualTo("a/b.dll"));
    }
}
