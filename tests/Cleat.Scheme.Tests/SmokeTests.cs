using System.Linq;
using Cleat.Scheme;
using NUnit.Framework;

namespace Cleat.Scheme.Tests;

public class SmokeTests
{
    [Test]
    public void CoreAssemblyLoads()
    {
        Assert.That(CoreInfo.Version, Is.Not.Empty);
    }

    /// <summary>Cleat.Scheme 只引用 BCL 与 Cleat.Core（CleatModuleAttribute、fasl 的 Clojure 数据）；不引用任何 IronScheme 程序集。</summary>
    [Test]
    public void SchemeReferencesOnlyCoreAndBcl()
    {
        var refs = typeof(CoreInfo).Assembly.GetReferencedAssemblies();
        foreach (var r in refs)
        {
            Assert.That(r.Name, Does.Not.StartWith("IronScheme"));
            Assert.That(r.Name == "Cleat.Core" || r.Name.StartsWith("System") || r.Name == "netstandard" || r.Name == "mscorlib", Is.True, r.Name);
        }
        Assert.That(typeof(Cleat.Core.CleatModuleAttribute).Assembly.GetReferencedAssemblies().Select(r => r.Name), Does.Not.Contain("Cleat.Scheme"), "Cleat.Core must not reference Cleat.Scheme");
    }
}
