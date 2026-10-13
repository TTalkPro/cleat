using System;
using System.IO;
using Cleat.Core.Runtime;
using Cleat.Emit;
using clojure.lang;
using NUnit.Framework;

namespace Cleat.Core.Tests;

// 发射出的模块程序集回调它（按名字引用）
public static class LoaderProbe
{
    public static string LastHit;
    public static void Hit(string what) => LastHit = what;
}

// cleat-012：运行时钩子与加载器
public class LoaderTests
{
    private string _dir;
    private int _pathIndex = -1;

    [SetUp]
    public void SetUp()
    {
        RuntimeHooks.Reset();
        _dir = Path.Combine(Path.GetTempPath(), "cleat-loader-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_dir, "x"));
        Loader.LoadPath.Insert(0, _dir);
        _pathIndex = 0;
        LoaderProbe.LastHit = null;
    }

    [TearDown]
    public void TearDown()
    {
        RuntimeHooks.Reset();
        if (_pathIndex >= 0)
        {
            Loader.LoadPath.Remove(_dir);
        }

        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Test]
    public void RequireThrowsClearErrorWhenUnregistered()
    {
        var e = Assert.Throws<InvalidOperationException>(() => RuntimeHooks.Require(RuntimeHooks.LoadSource, "load-source"));
        Assert.That(e.Message, Is.EqualTo("runtime hook 'load-source' is not registered (no boot image loaded?)"));

        // the Loader surfaces the same error when it finds a source file but has nobody to run it
        File.WriteAllText(Path.Combine(_dir, "x", "y.clj"), "(ns x.y)");
        e = Assert.Throws<InvalidOperationException>(() => Loader.Load("x/y"));
        Assert.That(e.Message, Does.Contain("'load-source'"));
    }

    [Test]
    public void SetWrapsClojureFnsAndRejectsUnknownNames()
    {
        RuntimeHooks.Set("eval", new Fn1(x => RT.list(Symbol.intern("evaluated"), x)));
        Assert.That(RT.printString(RuntimeHooks.Eval(1L)), Is.EqualTo("(evaluated 1)"));
        RuntimeHooks.Set("eval", null);
        Assert.That(RuntimeHooks.Eval, Is.Null);
        Func<object, object> direct = x => x;
        RuntimeHooks.Set("eval", direct);
        Assert.That(RuntimeHooks.Eval, Is.SameAs(direct));
        Assert.Throws<ArgumentException>(() => RuntimeHooks.Set("no-such-hook", direct));
        Assert.Throws<ArgumentException>(() => RuntimeHooks.Set("eval", "not a function"));
    }

    [Test]
    public void LoadSourceCallsHookWithFileContentAndPaths()
    {
        string file = Path.Combine(_dir, "x", "y.clj");
        File.WriteAllText(file, "(ns x.y)\n(def answer 42)\n");
        string seenText = null, seenPath = null, seenName = null, boundFile = null, boundSourcePath = null;
        Namespace nsInsideHook = null;
        RuntimeHooks.Set("load-source", new Fn3((rdr, path, name) =>
        {
            seenText = ((TextReader)rdr).ReadToEnd();
            seenPath = (string)path;
            seenName = (string)name;
            boundFile = (string)RT.SourcePathVar.deref();
            boundSourcePath = (string)RT.SourceVar.deref();
            RT.CurrentNSVar.set(Namespace.findOrCreate(Symbol.intern("x.y")));
            nsInsideHook = (Namespace)RT.CurrentNSVar.deref();
            return "last-value";
        }));

        Namespace before = (Namespace)RT.CurrentNSVar.deref();
        Loader.Load("x/y");
        Assert.That(seenText, Is.EqualTo("(ns x.y)\n(def answer 42)\n"));
        Assert.That(seenPath, Is.EqualTo(Path.GetFullPath(file)));
        Assert.That(seenName, Is.EqualTo("x/y.clj"));
        Assert.That(boundFile, Is.EqualTo(seenPath), "*file* is bound to the full path while loading");
        Assert.That(boundSourcePath, Is.EqualTo("x/y.clj"), "*source-path* is bound to the source name");
        Assert.That(nsInsideHook.Name.Name, Is.EqualTo("x.y"));
        Assert.That(RT.CurrentNSVar.deref(), Is.SameAs(before), "*ns* is restored after the load");

        // load-file returns the hook's value and reports the file name
        seenName = null;
        Assert.That(Loader.LoadFile(file), Is.EqualTo("last-value"));
        Assert.That(seenName, Is.EqualTo("y.clj"));

        // RT.load and the load-file Var go the same way
        seenPath = null;
        RT.load("x/y");
        Assert.That(seenPath, Is.EqualTo(Path.GetFullPath(file)));
        seenPath = null;
        RT.var("clojure.core", "load-file").invoke(file);
        Assert.That(seenPath, Is.EqualTo(Path.GetFullPath(file)));
    }

    [Test]
    public void CompileFilesTrueUsesTheCompileHook()
    {
        File.WriteAllText(Path.Combine(_dir, "x", "y.clj"), "(ns x.y)");
        int loads = 0, compiles = 0;
        RuntimeHooks.LoadSource = (r, p, n) => { loads++; return null; };
        RuntimeHooks.Compile = (r, p, n) => { compiles++; return null; };
        Var.pushThreadBindings(RT.map(RT.CompileFilesVar, true));
        try { Loader.Load("x/y"); }
        finally { Var.popThreadBindings(); }
        Loader.Load("x/y");
        Assert.That((loads, compiles), Is.EqualTo((1, 1)));
    }

    private static string ModuleLcil(string asmName, string entryType) => $@"
(lcil 1
  (assembly ""{asmName}"" (entry ""{entryType}"" ""Initialize""))
  (type ""{entryType}"" (attrs abstract sealed)
    (method ""Initialize"" (attrs static) (params) (ret ""System.Object"")
      (body (ldstr ""from-dll"") (call ""Cleat.Core.Tests.LoaderProbe"" ""Hit"" (""System.String"") ""System.Void"")
            (ldstr ""module-value"") (ret)))))";

    // cleat-067：包程序集——Initialize 只跑一次，每个文件一个入口，CleatNamespace 特性作索引
    private static string PackageLcil(string asmName, string key) => $@"
(lcil 1
  (assembly ""{asmName}"" (entry ""#Module"" ""Initialize"")
    (attribute ""Cleat.Core.CleatPackageAttribute"" ""{asmName}"")
    (attribute ""Cleat.Core.CleatNamespaceAttribute"" ""{key}"" ""#Module"" ""Load0""))
  (type ""#Module"" (attrs abstract sealed)
    (method ""Initialize"" (attrs static) (params) (ret ""System.Object"")
      (body (ldstr ""init"") (call ""Cleat.Core.Tests.LoaderProbe"" ""Hit"" (""System.String"") ""System.Void"") (ldnull) (ret)))
    (method ""Load0"" (attrs static) (params) (ret ""System.Object"")
      (body (ldstr ""from-package"") (call ""Cleat.Core.Tests.LoaderProbe"" ""Hit"" (""System.String"") ""System.Void"")
            (ldstr ""module-value"") (ret)))))";

    [Test]
    public void PackageAssemblyIndexWinsOverSourceAndRunsTheEntry()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "pkgtest"));
        File.WriteAllText(Path.Combine(_dir, "pkgtest", "a.clj"), "(ns pkgtest.a)");
        string dll = Path.Combine(_dir, "PkgTest.dll");
        LcilEmitter.Save(PackageLcil("PkgTest", "pkgtest/a"), "pkgtest.lcil", dll);
        int hookCalls = 0;
        RuntimeHooks.LoadSource = (r, p, n) => { hookCalls++; return null; };

        PackageIndex.RegisterDirectory(_dir);
        Assert.That(PackageIndex.Contains("pkgtest/a"), "indexed from metadata");
        Assert.That(PackageIndex.Locate("pkgtest/a"), Is.EqualTo(dll));
        Loader.Load("pkgtest/a");
        Assert.That(LoaderProbe.LastHit, Is.EqualTo("from-package"), "the namespace entry ran");
        Assert.That(hookCalls, Is.EqualTo(0), "the source hook was not used");
        Assert.That(Loader.LoadedAssemblies.ContainsKey("PkgTest"), "the package is registered by assembly name");

        // Initialize ran once, before the first entry; a second load runs only the entry
        LoaderProbe.LastHit = null;
        Loader.Load("pkgtest/a");
        Assert.That(LoaderProbe.LastHit, Is.EqualTo("from-package"));

        // *compile-files* loads the source (compiling needs it)
        Var.pushThreadBindings(RT.map(RT.CompileFilesVar, true));
        RuntimeHooks.Compile = (r, p, n) => { hookCalls++; return null; };
        try { Loader.Load("pkgtest/a"); }
        finally { Var.popThreadBindings(); }
        Assert.That(hookCalls, Is.EqualTo(1));
    }

    [Test]
    public void LoadModuleAndRunReportMissingAttributeOrEntry()
    {
        string dll = Path.Combine(_dir, "x.y.clj.dll");
        // a different assembly name: the default AssemblyLoadContext refuses a second "x.y.clj"
        LcilEmitter.Save(ModuleLcil("x.y.mem.clj", "MemEntry"), "x.y.lcil", dll);
        Assert.That(Loader.LoadModule(dll, true), Is.EqualTo("module-value"));
        Assert.That(Loader.FindType("MemEntry").Assembly.GetName().Name, Is.EqualTo("x.y.mem.clj"));

        var noEntry = LcilEmitter.EmitInMemory(@"(lcil 1 (assembly ""no-entry"") (type ""T""))", "no-entry.lcil");
        var e = Assert.Throws<InvalidOperationException>(() => Loader.Run(noEntry));
        Assert.That(e.Message, Does.Contain("[Cleat.Core.CleatModule]"));
        // (a dangling entry type cannot be produced: the emitter checks the (entry …) clause itself)
        e = Assert.Throws<InvalidOperationException>(() => Loader.Run(typeof(LoaderTests).Assembly));
        Assert.That(e.Message, Does.Contain("Cleat.Core.Tests"));
        Assert.Throws<FileNotFoundException>(() => Loader.LoadModule(Path.Combine(_dir, "missing.dll"), false));
    }

    [Test]
    public void MissingLibReportsTheLoadPath()
    {
        var e = Assert.Throws<FileNotFoundException>(() => Loader.Load("no/such_lib"));
        Assert.That(e.Message, Does.Contain("no/such_lib"));
        Assert.That(e.Message, Does.Contain(_dir), "the search path is in the message");
        Assert.That(e.Message, Does.Contain("underscores"));
        Assert.That(Loader.TryLoad("no/such_lib", false), Is.False);
        Assert.Throws<FileNotFoundException>(() => RT.load("no/such_lib"));
    }

    [Test]
    public void AddPathAndTildeResolveAgainstTheRuntimeBase()
    {
        Assert.That(Loader.ResolvePath("~/cleat.boot.dll"), Is.EqualTo(Path.Combine(Loader.BaseDirectory, "cleat.boot.dll")));
        int n = Loader.LoadPath.Count;
        Loader.AddPath(_dir);
        Assert.That(Loader.LoadPath.Count, Is.EqualTo(n), "already present: not added twice");
        string sub = Path.Combine(_dir, "sub");
        Loader.AddPath(sub);
        try
        {
            Assert.That(Loader.LoadPath, Does.Contain(Path.GetFullPath(sub)));
            Assert.That(RT.GetFindFilePaths(), Does.Contain(Path.GetFullPath(sub)), "RT.FindFile sees the Loader's path");
        }
        finally { Loader.LoadPath.Remove(Path.GetFullPath(sub)); }
    }

    [Test]
    public void InitIsIdempotentAndPointsNsAtUser()
    {
        int inits = 0;
        RuntimeHooks.OnInit = () => inits++;
        RT.Init();
        RT.Init();
        Assert.That(RT.IsInitialized);
        Assert.That(inits, Is.LessThanOrEqualTo(1), "OnInit runs at most once (other fixtures may have initialized already)");
        Assert.That(((Namespace)RT.CurrentNSVar.getRawRoot()).Name.Name, Is.EqualTo("user"));
        Assert.That(Namespace.find(Symbol.intern("clojure.core")), Is.Not.Null);
    }

    private sealed class Fn3 : AFn
    {
        private readonly Func<object, object, object, object> _f;
        public Fn3(Func<object, object, object, object> f) { _f = f; }
        public override object invoke(object a, object b, object c) => _f(a, b, c);
    }
}
