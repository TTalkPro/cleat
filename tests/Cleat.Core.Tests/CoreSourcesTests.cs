using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using clojure.lang;
using NUnit.Framework;

namespace Cleat.Core.Tests;

// cleat-017：src/boot/boot-order.edn 列出的每个 Clojure 源文件都能被 LispReader 完整读出（无 reader 错误）。
// 这同时是 cleat-009 的"读完 core.clj"验收。只读，不展开、不求值。
public class CoreSourcesTests
{
    private static readonly object Eof = new object();

    [Test]
    public void EveryBootFileReadsCompletely()
    {
        RT.Init();
        string root = FindRoot();
        var entries = ReadBootOrder(Path.Combine(root, "src", "boot", "boot-order.edn"));
        Assert.That(entries, Is.Not.Empty);

        var ns = Namespace.findOrCreate(Symbol.intern("cleat.test.core-sources"));
        var readOpts = RT.map(LispReader.OPT_READ_COND, LispReader.COND_ALLOW);
        var table = new StringBuilder("| 文件 | 顶层形式数 |\n|---|---|\n");
        long total = 0;
        var failures = new List<string>();

        Var.pushThreadBindings(RT.mapUniqueKeys(RT.CurrentNSVar, ns, RT.ReadEvalVar, true));
        try
        {
            foreach (var (nsName, rel) in entries)
            {
                string file = Path.Combine(root, "src", "clojure", rel.Replace('/', Path.DirectorySeparatorChar));
                Assert.That(File.Exists(file), Is.True, $"{rel} (ns {nsName}) listed in boot-order.edn but missing");
                int count = 0;
                try
                {
                    using var rdr = new LineNumberingTextReader(new StreamReader(file));
                    while (true)
                    {
                        object form = LispReader.read(rdr, false, Eof, false, readOpts);
                        if (ReferenceEquals(form, Eof)) break;
                        count++;
                    }
                }
                catch (Exception e)
                {
                    failures.Add($"{rel}: after {count} forms: {e.GetType().Name}: {e.Message} {e.InnerException?.Message}");
                }

                table.Append($"| `{rel}` | {count} |\n");
                total += count;
            }
        }
        finally
        {
            Var.popThreadBindings();
        }

        table.Append($"| **合计** | **{total}** |\n");
        TestContext.Out.WriteLine(table.ToString());
        Assert.That(failures, Is.Empty, string.Join("\n", failures));
        Assert.That(total, Is.GreaterThan(1000), "expected > 1000 top-level forms across the boot files");
    }

    [Test]
    public void BootOrderStartsWithKernelThenCore()
    {
        var entries = ReadBootOrder(Path.Combine(FindRoot(), "src", "boot", "boot-order.edn"));
        Assert.That(entries[0].ns, Is.EqualTo("cleat.kernel"));
        Assert.That(entries[0].path, Is.EqualTo("cleat/kernel.clj"));
        Assert.That(entries[1].ns, Is.EqualTo("clojure.core"));
        Assert.That(entries[1].path, Is.EqualTo("clojure/core.clj"));
        Assert.That(entries.Select(e => e.path).Distinct().Count(), Is.EqualTo(entries.Count), "duplicate paths in boot-order.edn");
    }

    private static List<(string ns, string path)> ReadBootOrder(string ednPath)
    {
        var doc = (IPersistentMap)EdnReader.readString(File.ReadAllText(ednPath), PersistentHashMap.EMPTY);
        var entries = (IPersistentVector)doc.valAt(Keyword.intern(null, "entries"));
        var result = new List<(string, string)>();
        for (int i = 0; i < entries.count(); i++)
        {
            var e = (IPersistentMap)entries.nth(i);
            result.Add((e.valAt(Keyword.intern(null, "ns")).ToString(), (string)e.valAt(Keyword.intern(null, "path"))));
        }

        return result;
    }

    private static string FindRoot()
    {
        for (var d = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); d != null; d = d.Parent)
        {
            if (File.Exists(Path.Combine(d.FullName, "Cleat.slnx"))) return d.FullName;
        }

        throw new InvalidOperationException("Cleat.slnx not found above the test directory");
    }
}
