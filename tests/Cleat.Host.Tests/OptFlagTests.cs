// cleat-164：CLEAT_NO_OPT 精确匹配的进程级行为。旧的子串匹配让 accinline（含 "inline"）顺带关掉 inline；
// 现在按逗号 / 空白分词后精确匹配，验证 accinline / loopinl / num 互不影响、子串（inline，cleat-182 起已不是记号）不误伤、"1" 仍全关。
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
        // 编译器读的是 Cleat.Core.Runtime.OptFlags（cleat-182 之前 Clojure 编译器读 cleat.compiler.optflags，同一口径）
        var script = "(print (if (Cleat.Core.Runtime.OptFlags/Off \"accinline\") \"A\" \"a\") " +
                     "(if (Cleat.Core.Runtime.OptFlags/Off \"loopinl\") \"L\" \"l\") " +
                     "(if (Cleat.Core.Runtime.OptFlags/Off \"num\") \"N\" \"n\"))";
        var r = HostProcessTests.CleatIn(dir, null, noOpt == null ? Array.Empty<(string, string)>() : new[] { ("CLEAT_NO_OPT", noOpt) }, "-e", script);
        Assert.That(r.ExitCode, Is.Zero, r.ToString());
        return r.Out.Trim().Replace(" ", "");
    }

    [Test]
    public void UnsetLeavesAllOn()
    {
        Assert.That(Flags(TempDir(), null), Is.EqualTo("aln"));
    }

    [Test]
    public void AccinlineDoesNotTouchLoopinlOrNum()
    {
        Assert.That(Flags(TempDir(), "accinline"), Is.EqualTo("Aln"));
        Assert.That(Flags(TempDir(), "accinline boxcache"), Is.EqualTo("Aln"));
    }

    [Test]
    public void SubstringsAreNotTokens()
    {
        // inline 是 accinline 的子串，也不再是记号（cleat-182）：什么都不关
        Assert.That(Flags(TempDir(), "inline"), Is.EqualTo("aln"));
        Assert.That(Flags(TempDir(), "inline,loopinl"), Is.EqualTo("aLn"));
    }

    [Test]
    public void OneTurnsEverythingOff()
    {
        Assert.That(Flags(TempDir(), "1"), Is.EqualTo("ALN"));
    }
}
