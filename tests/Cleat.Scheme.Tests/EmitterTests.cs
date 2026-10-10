using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Cleat.Emit;
using NUnit.Framework;

namespace Cleat.Scheme.Tests;

/// <summary>发射器（iron-019）：手写 Lcil 样例在内存与持久化两种模式下生成并运行。</summary>
public class EmitterTests
{
    private sealed class Thunk : Procedure
    {
        private readonly Func<object> _f;
        public Thunk(Func<object> f) { _f = f; }
        public override object Invoke0() => _f();
    }

    private static string s_tempDir;

    [OneTimeSetUp]
    public void Init()
    {
        s_tempDir = Path.Combine(Path.GetTempPath(), "iron-emit-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(s_tempDir);
    }

    [OneTimeTearDown]
    public void Cleanup()
    {
        try { Directory.Delete(s_tempDir, true); } catch (Exception) { }   // 已加载的 dll 在部分平台上删不掉
    }

    public static IEnumerable<EmitMode> Modes => new[] { EmitMode.InMemory, EmitMode.Persisted };

    internal static Assembly Build(string sample, EmitMode mode)
    {
        var a = LcilParser.Parse(LcilParserTests.Sample(sample), sample);
        if (mode == EmitMode.InMemory) return LcilEmitter.EmitInMemory(a);
        var path = Path.Combine(s_tempDir, Path.ChangeExtension(sample, ".dll"));
        LcilEmitter.Save(a, path);
        return Assembly.LoadFile(path);
    }

    private static object Call(Assembly asm, string type, string method, params object[] args) =>
      asm.GetType(type).GetMethod(method).Invoke(null, args);

    [TestCaseSource(nameof(Modes))]
    public void Factorial(EmitMode mode)
    {
        var asm = Build("fact.lcil", mode);
        Assert.That(Call(asm, "Fact", "Loop", 20L), Is.EqualTo(2432902008176640000L));
        Assert.That(Call(asm, "Fact", "Rec", 20L), Is.EqualTo(2432902008176640000L));
        Assert.That(Call(asm, "Fact", "Rec", 0L), Is.EqualTo(1L));
    }

    [TestCaseSource(nameof(Modes))]
    public void ClosureCounter(EmitMode mode)
    {
        var asm = Build("counter.lcil", mode);
        var c = (Procedure)Call(asm, "Make", "Counter", 10);
        c.Invoke0(); c.Invoke0();
        Assert.That(c.Invoke0(), Is.EqualTo(13));
        var d = (Procedure)Call(asm, "Make", "Counter", 0);
        Assert.That(d.Invoke0(), Is.EqualTo(1));
        Assert.Throws<SchemeRuntimeError>(() => c.Invoke1(1));   // 未重写的个数：Procedure 的参数个数错误
    }

    [TestCaseSource(nameof(Modes))]
    public void TryFinallyAndCatch(EmitMode mode)
    {
        var asm = Build("tryfinally.lcil", mode);
        int after = 0;
        var count = new Thunk(() => { after++; return null; });
        Assert.That(Call(asm, "TF", "Run", new Thunk(() => 42), count), Is.EqualTo(42));
        Assert.That(after, Is.EqualTo(1));
        var e = Assert.Throws<TargetInvocationException>(() => Call(asm, "TF", "Run", new Thunk(() => throw new ArgumentException("x")), count));
        Assert.That(e.InnerException, Is.InstanceOf<ArgumentException>());
        Assert.That(after, Is.EqualTo(2));
        Assert.That(Call(asm, "TF", "Catch", new Thunk(() => throw new InvalidOperationException("boom"))), Is.EqualTo("boom"));
        Assert.That(Call(asm, "TF", "Catch", new Thunk(() => "ok")), Is.EqualTo("ok"));
    }

    [TestCaseSource(nameof(Modes))]
    public void MutualTailRecursionMillionDeep(EmitMode mode)
    {
        var asm = Build("mutual.lcil", mode);
        var even = (Procedure)Call(asm, "Mutual", "Make");
        Assert.That(even.Invoke1(1000001), Is.EqualTo(false));
        Assert.That(even.Invoke1(1000000), Is.EqualTo(true));
    }

    [TestCaseSource(nameof(Modes))]
    public void Interop(EmitMode mode)
    {
        var asm = Build("interop.lcil", mode);
        Assert.That(Call(asm, "IO", "Upper", "abc"), Is.EqualTo("ABC"));
        Assert.That(Call(asm, "IO", "MaxViaInterop", 3, 7), Is.EqualTo(7));
        Assert.That(Call(asm, "IO", "Max", 3, 7), Is.EqualTo(7));
        Assert.That(Call(asm, "IO", "Max", 2.5, 1.0), Is.EqualTo(2.5));
        Assert.That(Call(asm, "IO", "Upper2", "abc"), Is.EqualTo("ABC"));
        Assert.That(Call(asm, "IO", "Sub", "hello", 1, 2), Is.EqualTo("el"));
        Assert.That(Call(asm, "IO", "Len", "abcd"), Is.EqualTo(4));
        Assert.That(Call(asm, "IO", "Fields"), Is.EqualTo(int.MaxValue));
        Assert.That(Call(asm, "IO", "NewSB", "x").ToString(), Is.EqualTo("x"));
        Assert.That(Call(asm, "IO", "IsString", "s"), Is.EqualTo(true));
        Assert.That(Call(asm, "IO", "IsString", 1), Is.EqualTo(false));
        Assert.That(Call(asm, "IO", "ToByte", -127), Is.EqualTo(129));
        Assert.That(Call(asm, "IO", "IntArray", 3), Is.EqualTo(new int[3]));
        Assert.That(Call(asm, "IO", "TypeOf"), Is.EqualTo(typeof(int)));
        // 转换失败与运行时一致：报 assertion-violation
        Assert.That(Assert.Throws<TargetInvocationException>(() => Call(asm, "IO", "Sub", "hello", "x", 2)).InnerException, Is.InstanceOf<SchemeRuntimeError>());
    }

    /// <summary>静态决议的互操作直接调用目标方法，决议不了的走 Interop.Call。</summary>
    [Test]
    public void InteropResolution()
    {
        var asm = Build("interop.lcil", EmitMode.Persisted);
        Assert.That(Callees(asm, "Upper2"), Does.Contain("ToUpperInvariant").And.Not.Contain("Call"));
        Assert.That(Callees(asm, "Sub"), Does.Contain("Substring").And.Not.Contain("Call"));
        Assert.That(Callees(asm, "Max"), Does.Contain("Call"));
        Assert.That(Callees(asm, "Len"), Does.Contain("Call"));
    }

    /// <summary>粗略扫描方法体中的 call / callvirt / newobj 目标名字（测试用）。</summary>
    private static List<string> Callees(Assembly asm, string method)
    {
        var m = asm.GetType("IO").GetMethod(method);
        var il = m.GetMethodBody().GetILAsByteArray();
        var r = new List<string>();
        for (int i = 0; i + 4 < il.Length; i++)
        {
            if (il[i] != 0x28 && il[i] != 0x6F && il[i] != 0x73) continue;
            int token = BitConverter.ToInt32(il, i + 1);
            try { r.Add(m.Module.ResolveMethod(token).Name); } catch (Exception) { }
        }
        return r;
    }

    [Test]
    public void PersistedOutputIsDeterministic()
    {
        var a = LcilParser.Parse(LcilParserTests.Sample("interop.lcil"), "interop.lcil");
        string p1 = Path.Combine(s_tempDir, "det1.dll"), p2 = Path.Combine(s_tempDir, "det2.dll");
        LcilEmitter.Save(a, p1);
        LcilEmitter.Save(LcilParser.Parse(LcilParserTests.Sample("interop.lcil"), "interop.lcil"), p2);
        Assert.That(File.ReadAllBytes(p1), Is.EqualTo(File.ReadAllBytes(p2)));
    }

    [Test]
    public void EmitErrors()
    {
        string Lcil(string body) => "(lcil 1 (assembly \"e\") (type \"T\" (method \"M\" (attrs static) (params) (ret \"System.Object\") (body " + body + "))))";
        Assert.That(Assert.Throws<LcilException>(() => LcilEmitter.EmitInMemory(Lcil("(newobj \"No.Such\" ()) (ret)"))).Message, Does.EndWith("type not found: No.Such"));
        Assert.That(Assert.Throws<LcilException>(() => LcilEmitter.EmitInMemory(Lcil("(call \"System.Math\" \"Nope\" () \"System.Void\") (ldnull) (ret)"))).Message, Does.Contain("method not found"));
        Assert.That(Assert.Throws<LcilException>(() => LcilEmitter.EmitInMemory(Lcil("(clr-type \"NoSuchType\") (ret)"))).Message, Does.EndWith("type not found: NoSuchType"));
    }
}
