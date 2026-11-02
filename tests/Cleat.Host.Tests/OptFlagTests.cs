// cleat-164：CLEAT_NO_OPT 精确匹配的进程级行为。旧的子串匹配让 accinline（含 "inline"）顺带关掉 inline；
// 现在按逗号 / 空白分词后精确匹配，验证 accinline / inline / loopinl 三者互不影响、"1" 仍全关。
using System;
using System.IO;
using NUnit.Framework;

namespace Cleat.Host.Tests;

[Category("Host")]
public class OptFlagTests
{
    private static string TempDir() => Path.Combine(Path.GetTempPath(), "cleat-opt-" + Guid.NewGuid().ToString("N").Substring(0, 8));

    private static string Flags(string dir, string noOpt)
    {
        Directory.CreateDirectory(dir);
        var script = "(do (require (quote cleat.compiler.optflags)) " +
                     "(print (if (cleat.compiler.optflags/opt-off? \"inline\") \"I\" \"i\") " +
                     "(if (cleat.compiler.optflags/opt-off? \"accinline\") \"A\" \"a\") " +
                     "(if (cleat.compiler.optflags/opt-off? \"loopinl\") \"L\" \"l\")))";
        var r = HostProcessTests.CleatIn(dir, null, noOpt == null ? Array.Empty<(string, string)>() : new[] { ("CLEAT_NO_OPT", noOpt) }, "-e", script);
        Assert.That(r.ExitCode, Is.Zero, r.ToString());
        return r.Out.Trim().Replace(" ", "");
    }

    [Test]
    public void UnsetLeavesAllOn()
    {
        Assert.That(Flags(TempDir(), null), Is.EqualTo("ial"));
    }

    [Test]
    public void AccinlineDoesNotTouchInlineOrLoopinl()
    {
        Assert.That(Flags(TempDir(), "accinline"), Is.EqualTo("iAl"));
        Assert.That(Flags(TempDir(), "accinline boxcache"), Is.EqualTo("iAl"));
    }

    [Test]
    public void InlineDoesNotTouchLoopinlOrAccinline()
    {
        Assert.That(Flags(TempDir(), "inline"), Is.EqualTo("Ial"));
        Assert.That(Flags(TempDir(), "inline,loopinl"), Is.EqualTo("IaL"));
    }

    [Test]
    public void OneTurnsEverythingOff()
    {
        Assert.That(Flags(TempDir(), "1"), Is.EqualTo("IAL"));
    }
}
