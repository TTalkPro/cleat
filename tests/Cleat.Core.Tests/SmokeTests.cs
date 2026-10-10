using Cleat.Core.Runtime;
using NUnit.Framework;

namespace Cleat.Core.Tests;

public class SmokeTests
{
    [Test]
    public void CoreInfoHasVersion() => Assert.That(CoreInfo.Version, Is.Not.Empty);
}
