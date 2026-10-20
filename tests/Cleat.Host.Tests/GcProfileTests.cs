using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace Cleat.Host.Tests;

/// <summary>cleat-148：GC profile 名只是启动环境的选择器；宿主 Main 在 GC 初始化后只做一致性校验。</summary>
[Category("Host")]
public class GcProfileTests
{
    internal const string Profile = Cleat.Host.GcProfile.EnvironmentVariable;
    internal const string Server = Cleat.Host.GcProfile.ServerVariable;
    internal const string Gen0 = Cleat.Host.GcProfile.Gen0Variable;
    private readonly Dictionary<string, string> _saved = new();

    [SetUp]
    public void SaveEnvironment()
    {
        foreach (var name in (string[])[Profile, Server, Gen0, "CLEAT_NO_OPT"])
        {
            _saved[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [TearDown]
    public void RestoreEnvironment()
    {
        foreach (var (name, value) in _saved) Environment.SetEnvironmentVariable(name, value);
        _saved.Clear();
    }

    [Test]
    public void NoDeclarationKeepsTheDefault()
    {
        Assert.That(Cleat.Host.GcProfile.ValidateSelection(), Is.Null);
        Environment.SetEnvironmentVariable(Profile, "default");
        Assert.That(Cleat.Host.GcProfile.ValidateSelection(), Is.Null);
    }

    [Test]
    public void BatchRequiresLauncherEnvironmentAndObservedGc()
    {
        Environment.SetEnvironmentVariable(Profile, "batch");
        Assert.That(Cleat.Host.GcProfile.ValidateSelection(), Does.Contain("must be selected before launch"));
        Environment.SetEnvironmentVariable(Server, "1");
        Assert.That(Cleat.Host.GcProfile.ValidateSelection(), Does.Contain("must be selected before launch"));
        Environment.SetEnvironmentVariable(Gen0, "0x10000000");
        Assert.That(Cleat.Host.GcProfile.ValidateSelection(), Is.Null);
    }

    [Test]
    public void UnknownValueAndNoOptFallbackAreHandled()
    {
        Environment.SetEnvironmentVariable(Profile, "fast");
        Assert.That(Cleat.Host.GcProfile.ValidateSelection(), Does.Contain("unknown GC profile"));
        Environment.SetEnvironmentVariable("CLEAT_NO_OPT", "gcprofile");
        Assert.That(Cleat.Host.GcProfile.ValidateSelection(), Is.Null);
    }

    [Test]
    public void BatchProcessRunsClojureAndMismatchFailsBeforeRuntime()
    {
        var r = HostProcessTests.CleatIn(HostProcessTests.RepoDir, null,
            new[] { (Profile, "batch"), (Server, "1"), (Gen0, "0x10000000") }, "-e", "(+ 20 22)");
        Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
        Assert.That(r.Out, Is.EqualTo("42\n"), r.ToString());

        var bad = HostProcessTests.CleatIn(HostProcessTests.RepoDir, null,
            new[] { (Profile, "batch") }, "-e", "(+ 20 22)");
        Assert.That(bad.ExitCode, Is.EqualTo(2), bad.ToString());
        Assert.That(bad.Err, Does.Contain("must be selected before launch"), bad.ToString());

        var off = HostProcessTests.CleatIn(HostProcessTests.RepoDir, null,
            new[] { (Profile, "fast"), ("CLEAT_NO_OPT", "gcprofile") }, "-e", "(+ 20 22)");
        Assert.That(off.ExitCode, Is.EqualTo(0), off.ToString());
        Assert.That(off.Out, Is.EqualTo("42\n"), off.ToString());

        var ignoredBatch = HostProcessTests.CleatIn(HostProcessTests.RepoDir, null,
            new[] { (Profile, "batch"), ("CLEAT_NO_OPT", "gcprofile") }, "-e", "(+ 20 22)");
        Assert.That(ignoredBatch.ExitCode, Is.EqualTo(0), ignoredBatch.ToString());
        Assert.That(ignoredBatch.Out, Is.EqualTo("42\n"), ignoredBatch.ToString());
    }
}
