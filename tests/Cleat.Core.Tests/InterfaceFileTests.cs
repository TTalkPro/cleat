using System;
using System.IO;
using System.Reflection;
using Cleat.Core.Runtime;
using clojure.lang;
using NUnit.Framework;

namespace Cleat.Core.Tests;

// cleat-022：接口文件、已知函数登记、镜像 id 核对
public class InterfaceFileTests
{
    [Test]
    public void RoundTripsThroughEdn()
    {
        var f = new InterfaceFile { AssemblyName = "x.y.clj", ImageId = Guid.NewGuid() };
        f.Entries.Add(new InterfaceFile.Entry { Var = Symbol.intern("x.y", "f"), ClassName = "x.y$f, x.y.clj", Arities = new[] { 1, 2, -3 }, HasStatic = true });
        f.Entries.Add(new InterfaceFile.Entry { Var = Symbol.intern("x.y", "a"), ClassName = "x.y$a, x.y.clj", Arities = new[] { 0 }, HasStatic = false });
        var back = InterfaceFile.Parse(f.ToEdn());
        Assert.That(back.AssemblyName, Is.EqualTo("x.y.clj"));
        Assert.That(back.ImageId, Is.EqualTo(f.ImageId));
        Assert.That(back.Entries, Has.Count.EqualTo(2));
        Assert.That(back.Entries[0].Var, Is.EqualTo(Symbol.intern("x.y", "a")), "sorted by var");
        Assert.That(back.Entries[1].Arities, Is.EqualTo(new[] { 1, 2, -3 }));
        Assert.That(back.Entries[1].HasStatic, Is.True);
        Assert.That(f.ToEdn(), Is.EqualTo(f.ToEdn()));
    }

    [Test]
    public void LoaderRegistersMatchingInterfaceFileOnly()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cleat-ifc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string dll = Path.Combine(dir, "ifcmod.dll");
        TestAssemblies.Save(dll, "ifcmod", "Entry", [new("Initialize", TestAssemblies.HitThenReturn(null, "ok"))],
                            TestAssemblies.Attribute<CleatModuleAttribute>("Entry", "Initialize"));
        var mvid = AssemblyName.GetAssemblyName(dll);
        using var pe = new System.Reflection.PortableExecutable.PEReader(File.OpenRead(dll));
        var md = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
        var id = md.GetGuid(md.GetModuleDefinition().Mvid);
        // 不相符的接口文件：忽略
        var stale = new InterfaceFile { AssemblyName = "ifcmod", ImageId = Guid.NewGuid() };
        stale.Entries.Add(new InterfaceFile.Entry { Var = Symbol.intern("ifc.test", "stale"), ClassName = "X", Arities = new[] { 0 } });
        stale.Save(InterfaceFile.PathFor(dll));
        InterfaceFile.ClearKnown();
        Assert.That(Loader.LoadModule(dll, true), Is.EqualTo("ok"));
        Assert.That(InterfaceFile.FindKnown(Symbol.intern("ifc.test", "stale")), Is.Null);
        // 相符的接口文件：登记（用 Register 直接验证，同名程序集不能二次加载）
        var asm = Loader.LoadedAssemblies["ifcmod"];
        var good = new InterfaceFile { AssemblyName = "ifcmod", ImageId = id };
        good.Entries.Add(new InterfaceFile.Entry { Var = Symbol.intern("ifc.test", "good"), ClassName = "Entry, ifcmod", Arities = new[] { 0 }, HasStatic = true });
        Assert.That(InterfaceFile.Register(good, asm), Is.True);
        Assert.That(InterfaceFile.FindKnown(Symbol.intern("ifc.test", "good")).ClassName, Is.EqualTo("Entry, ifcmod"));
        Assert.That(InterfaceFile.Register(stale, asm), Is.False);
    }

    [Test]
    public void BootInterfaceCheck()
    {
        var saved = BootInterface.CurrentImageId;
        try
        {
            BootInterface.CurrentImageId = null;
            Assert.DoesNotThrow(() => BootInterface.Check(Guid.NewGuid().ToString()));
            var id = Guid.NewGuid();
            BootInterface.CurrentImageId = id;
            Assert.DoesNotThrow(() => BootInterface.Check(id.ToString()));
            var e = Assert.Throws<BootInterface.StaleModuleException>(() => BootInterface.Check(Guid.NewGuid().ToString()));
            Assert.That(e.Message, Does.Contain("recompile"));
        }
        finally { BootInterface.CurrentImageId = saved; }
    }
}
