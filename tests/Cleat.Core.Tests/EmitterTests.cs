using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Cleat.Emit;
using NUnit.Framework;

namespace Cleat.Core.Tests;

// 发射器用的公开基类与接口（生成的类型按名字引用它们）
public abstract class TestFn
{
    public virtual object Invoke() => throw new InvalidOperationException("arity 0");
    public virtual object Invoke(object a) => throw new InvalidOperationException("arity 1");
}

public interface ITestSeq
{
    object First();
    object Rest();
}

public sealed class Marker : Attribute
{
    public string Name { get; }
    public Marker(string name) { Name = name; }
}

// cleat-020：Lcil → 程序集（内存与持久化）
public class EmitterTests
{
    private const string Counter = @"
(lcil 1
  (assembly ""counter"")
  (type ""Counter"" (base ""Cleat.Core.Tests.TestFn"") (attrs sealed)
    (field ""n"" ""System.Object"")
    (method "".ctor"" (attrs ctor) (params ""System.Object"")
      (body (ldarg 0) (call ""Cleat.Core.Tests.TestFn"" "".ctor"" () ""System.Void"")
            (ldarg 0) (ldarg 1) (stfld ""Counter"" ""n"") (ret)))
    (method ""Invoke"" (attrs override) (params) (ret ""System.Object"")
      (body (ldarg 0)
            (ldarg 0) (ldfld ""Counter"" ""n"") (unbox.any ""System.Int64"") (ldc.i8 1) (add) (box ""System.Int64"")
            (stfld ""Counter"" ""n"")
            (ldarg 0) (ldfld ""Counter"" ""n"") (ret))))
  (type ""Make"" (attrs abstract sealed)
    (method ""Counter"" (attrs static) (params ""System.Object"") (ret ""Cleat.Core.Tests.TestFn"")
      (body (ldarg 0) (newobj ""Counter"" (""System.Object"")) (ret)))))";

    [Test]
    public void ClosureCounterInMemory()
    {
        var asm = LcilEmitter.EmitInMemory(Counter, "counter.lcil");
        var make = asm.GetType("Make").GetMethod("Counter");
        var c = (TestFn)make.Invoke(null, new object[] { 10L });
        Assert.That(c.Invoke(), Is.EqualTo(11L));
        Assert.That(c.Invoke(), Is.EqualTo(12L));
        Assert.Throws<InvalidOperationException>(() => c.Invoke(1));
    }

    private const string Seq = @"
(lcil 1
  (assembly ""seq"" (entry ""Entry"" ""Initialize"") (attribute ""Cleat.Core.Tests.Marker"" ""asm""))
  (type ""IOwn"" (attrs interface) (method ""Tag"" (attrs abstract) (params) (ret ""System.Object"")))
  (type ""Cell"" (interfaces ""Cleat.Core.Tests.ITestSeq"" ""IOwn"") (attribute ""Cleat.Core.Tests.Marker"" ""cell"")
    (field ""head"" ""System.Object"" volatile)
    (field ""tail"" ""System.Object"" public)
    (method "".ctor"" (attrs ctor) (params ""System.Object"" ""System.Object"")
      (body (ldarg 0) (call ""System.Object"" "".ctor"" () ""System.Void"")
            (ldarg 0) (ldarg 1) (stfld ""Cell"" ""head"")
            (ldarg 0) (ldarg 2) (stfld ""Cell"" ""tail"") (ret)))
    ;; 显式接口实现：私有方法绑定到 ITestSeq.First
    (method ""first"" (attrs private) (implements ""Cleat.Core.Tests.ITestSeq"" ""First"") (params) (ret ""System.Object"")
      (body (ldarg 0) (ldfld ""Cell"" ""head"") (ret)))
    ;; 隐式实现：同名公开 virtual
    (method ""Rest"" (attrs virtual) (params) (ret ""System.Object"")
      (body (ldarg 0) (ldfld ""Cell"" ""tail"") (ret)))
    (method ""Tag"" (attrs virtual) (params) (ret ""System.Object"")
      (body (ldstr ""cell"") (ret))))
  (type ""Entry"" (attrs abstract sealed)
    ;; switch + try/finally：按参数分派，finally 累加到静态字段
    (field ""fin"" ""System.Int64"" static)
    (method ""Pick"" (attrs static) (params ""System.Int32"") (ret ""System.Object"") (locals ""System.Object"")
      (body
        (try
          (body (ldarg 0) (switch (A B)) (ldstr ""other"") (stloc 0) (leave End)
                (label A) (ldstr ""zero"") (stloc 0) (leave End)
                (label B) (ldstr ""one"") (stloc 0) (leave End))
          (finally (ldsfld ""Entry"" ""fin"") (ldc.i8 1) (add) (stsfld ""Entry"" ""fin"")))
        (label End) (ldloc 0) (ret)))
    (method ""Initialize"" (attrs static) (params) (ret ""System.Object"")
      (body (ldc.i8 1) (box ""System.Int64"") (ldnull) (newobj ""Cell"" (""System.Object"" ""System.Object"")) (ret)))))";

    [Test]
    public void InterfacesVolatileSwitchTryAndAttributesInMemory()
    {
        var asm = LcilEmitter.EmitInMemory(Seq, "seq.lcil");
        CheckSeq(asm);
    }

    private static void CheckSeq(Assembly asm)
    {
        var entry = asm.GetCustomAttribute<CleatModuleAttribute>();
        Assert.That(entry.EntryType, Is.EqualTo("Entry"));
        Assert.That(asm.GetCustomAttribute<Marker>().Name, Is.EqualTo("asm"));
        var cellType = asm.GetType("Cell");
        Assert.That(cellType.GetCustomAttribute<Marker>().Name, Is.EqualTo("cell"));
        var head = cellType.GetField("head");
        Assert.That(head.GetRequiredCustomModifiers(), Does.Contain(typeof(System.Runtime.CompilerServices.IsVolatile)));
        var cell = (ITestSeq)asm.GetType(entry.EntryType).GetMethod(entry.EntryMethod).Invoke(null, null);
        Assert.That(cell.First(), Is.EqualTo(1L));
        Assert.That(cell.Rest(), Is.Null);
        Assert.That(cellType.GetMethod("First"), Is.Null, "explicit implementation keeps its own name");
        Assert.That(asm.GetType("IOwn").IsInterface);
        Assert.That(cellType.GetInterfaces().Select(i => i.Name), Does.Contain("IOwn"));
        var pick = asm.GetType("Entry").GetMethod("Pick");
        Assert.That(pick.Invoke(null, new object[] { 0 }), Is.EqualTo("zero"));
        Assert.That(pick.Invoke(null, new object[] { 1 }), Is.EqualTo("one"));
        Assert.That(pick.Invoke(null, new object[] { 7 }), Is.EqualTo("other"));
        Assert.That(asm.GetType("Entry").GetField("fin").GetValue(null), Is.EqualTo(3L));
    }

    [Test]
    public void PersistedAssemblyIsDeterministicAndLoadable()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cleat-emit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string a = Path.Combine(dir, "a.dll"), b = Path.Combine(dir, "b.dll");
        LcilEmitter.Save(Seq, "seq.lcil", a);
        LcilEmitter.Save(Seq, "seq.lcil", b);
        Assert.That(File.ReadAllBytes(a), Is.EqualTo(File.ReadAllBytes(b)), "two saves must be byte-identical");
        var asm = Assembly.LoadFrom(a);
        CheckSeq(asm);
    }

    [Test]
    public void EmitErrorsNameTheProblem()
    {
        var e = Assert.Throws<LcilException>(() => LcilEmitter.EmitInMemory(@"(lcil 1 (assembly ""x"") (type ""T"" (method ""m"" (attrs static) (params) (ret ""System.Object"") (body (call ""System.String"" ""Nope"" () ""System.String"") (ret)))))", "x.lcil"));
        Assert.That(e.Message, Does.Contain("method not found"));
        e = Assert.Throws<LcilException>(() => LcilEmitter.EmitInMemory(@"(lcil 1 (assembly ""x"") (type ""T"" (base ""No.Such.Type"")))", "x.lcil"));
        Assert.That(e.Message, Does.Contain("type not found: No.Such.Type"));
    }
}
