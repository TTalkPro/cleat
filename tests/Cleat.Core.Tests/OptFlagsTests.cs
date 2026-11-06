// cleat-164：CLEAT_NO_OPT 精确匹配的分词规则。一个记号是另一个的子串时（164 时的 accinline ⊃ inline；inline 记号在
// cleat-182 删除）不得互相误伤；进程级行为在 Cleat.Host.Tests 的 OptFlagTests 里跑真实宿主验证。
using System.Collections.Generic;
using Cleat.Core.Runtime;
using NUnit.Framework;

namespace Cleat.Core.Tests;

public class OptFlagsTests
{
    [Test]
    public void TokensAreExactWords()
    {
        Assert.That(OptFlags.Tokenize("accinline"), Is.EqualTo(new HashSet<string> { "accinline" }));
        Assert.That(OptFlags.Tokenize("loopinl"), Is.EqualTo(new HashSet<string> { "loopinl" }));
        Assert.That(OptFlags.Tokenize("accinline,boxcache"), Is.EqualTo(new HashSet<string> { "accinline", "boxcache" }));
        Assert.That(OptFlags.Tokenize("accinline boxcache"), Is.EqualTo(new HashSet<string> { "accinline", "boxcache" }));
        Assert.That(OptFlags.Tokenize(" accinline , boxcache "), Is.EqualTo(new HashSet<string> { "accinline", "boxcache" }));
    }

    [Test]
    public void SentinelAndEmptyValuesHaveNoTokens()
    {
        Assert.That(OptFlags.Tokenize("1"), Is.Empty);
        Assert.That(OptFlags.Tokenize(""), Is.Empty);
        Assert.That(OptFlags.Tokenize(null), Is.Empty);
    }
}
