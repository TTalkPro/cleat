using System;
using System.Text.RegularExpressions;
using Cleat.Emit;
using Fasl = Cleat.Core.Runtime.Fasl;
using clojure.lang;
using NUnit.Framework;

namespace Cleat.Core.Tests;

// cleat-021：编码 → fasl → 对象；对象 → fasl → 对象（cleat-092 起由 Cleat.Core.Runtime.Fasl 读写；Scheme 的 fasl 随元层在 cleat-093 删除）
public class FaslTests
{
    private static object[] ReadTable(string table)
    {
        var items = new SExprReader("(" + table + ")", "c").ReadAll();
        var bytes = FaslEncoder.EncodeTable(((SList)items[0]).Items, "c");
        return Fasl.ReadTable(bytes);
    }

    /// <summary>
    /// cleat-092：常量表由 Cleat.Core.Runtime.Fasl 读回；Clojure 值经它写出再读回相同；未知标签报错。
    /// </summary>
    [Test]
    public void CoreFaslReadsClojureTablesAndRoundTripsValues()
    {
        var items = new SExprReader(@"((nil) (true) (long -5) (fix 7) (double ""3FF0000000000000"") (double ""4000000000000000"") (bigint ""123456789012345678901234567890"") (bigdec ""1.5"") (ratio ""1/3"")
          (char 65) (str ""héllo"") (sym ""clojure.core"" ""inc"") (kw ""ns"" ""b"") (var ""cleat.test.fasl"" ""g"")
          (list (long 1) (long 2)) (vec (long 1)) (set (kw ""a"")) (sorted-set (long 3) (long 1)) (queue (long 1) (long 2)) (map (kw ""k"") (long 1)) (sorted-map (long 2) (str ""b"") (long 1) (str ""a""))
          (meta (map (kw ""tag"") (sym ""String"")) (sym ""s"")) (regex ""a+"") (uuid ""7f2a1c2e-0000-4000-8000-000000000001"") (type ""System.String""))", "c").ReadAll();
        var bytes = FaslEncoder.EncodeTable(((SList)items[0]).Items, "c", LcilRuntime.Clojure);
        var t = Cleat.Core.Runtime.Fasl.ReadTable(bytes);
        Assert.That(t[0], Is.Null);
        Assert.That(t[1], Is.EqualTo(true));
        Assert.That(t[2], Is.EqualTo(-5L));
        Assert.That(t[3], Is.TypeOf<int>().And.EqualTo(7), "fix reads back as Int32");
        Assert.That(t[4], Is.EqualTo(1.0));
        Assert.That(t[5], Is.EqualTo(2.0));
        Assert.That(RT.printString(t[6]), Is.EqualTo("123456789012345678901234567890N"));
        Assert.That(t[9], Is.EqualTo('A'));
        Assert.That(t[10], Is.EqualTo("héllo"));
        Assert.That(t[11], Is.EqualTo(Symbol.intern("clojure.core", "inc")));
        Assert.That(t[12], Is.EqualTo(Keyword.intern("ns", "b")));
        Assert.That(t[13], Is.InstanceOf<Var>());
        Assert.That(RT.printString(t[14]), Is.EqualTo("(1 2)"));
        Assert.That(t[15], Is.InstanceOf<IPersistentVector>());
        Assert.That(RT.printString(t[17]), Is.EqualTo("#{1 3}"));
        Assert.That(RT.printString(t[19]), Is.EqualTo("{:k 1}"));
        Assert.That(RT.printString(t[20]), Is.EqualTo("{1 \"a\", 2 \"b\"}"));
        Assert.That(((IMeta)t[21]).meta().valAt(Keyword.intern("tag")), Is.EqualTo(Symbol.intern("String")));
        Assert.That(t[24], Is.EqualTo(typeof(string)));

        // 写出再读回（接口文件旁的 .inl：L0 子句的 S 表达式，[:cleat/l0 (clause (g$x$1$c) false …)]）
        object v = RT.readString("[:cleat/l0 (clause (g$x$1$c) false (clj-invoke (clj-var-ref 1) g$x$1$c \"s\" 2.5 \\c {:a [1 2]} #{3} nil true))]");
        var back = Cleat.Core.Runtime.Fasl.Read(Cleat.Core.Runtime.Fasl.Write(v));
        Assert.That(RT.printString(back), Is.EqualTo(RT.printString(v)));
        Assert.That(back, Is.EqualTo(v));
        var withMeta = ((IObj)Symbol.intern("x")).withMeta(RT.map(Keyword.intern("tag"), Symbol.intern("long")));
        Assert.That(((IMeta)Cleat.Core.Runtime.Fasl.Read(Cleat.Core.Runtime.Fasl.Write(withMeta))).meta(), Is.EqualTo(withMeta.meta()));

        // 未知的标签（IronScheme 联合编码里 Scheme 的 '()）不认
        var bad = FaslEncoder.EncodeTable(((SList)new SExprReader("((nil))", "c").ReadAll()[0]).Items, "c");
        bad[^1] = 0x05;
        Assert.Throws<System.IO.InvalidDataException>(() => Cleat.Core.Runtime.Fasl.ReadTable(bad));
        Assert.Throws<LcilException>(() => FaslEncoder.EncodeTable(((SList)new SExprReader("((empty))", "c").ReadAll()[0]).Items, "c"), "Scheme encodings are gone");
    }

    [Test]
    public void EncodedConstantsReadBackAsClojureData()
    {
        var t = ReadTable(@"(nil) (true) (long -5) (double ""3FF0000000000000"") (bigint ""123456789012345678901234567890"") (bigdec ""1.5"") (ratio ""1/3"")
          (char 65) (str ""héllo"") (sym ""x"") (sym ""clojure.core"" ""inc"") (kw ""a"") (kw ""ns"" ""b"") (var ""cleat.test.fasl"" ""f"")
          (list (long 1) (long 2)) (vec (long 1)) (set (kw ""a"")) (sorted-set (long 3) (long 1)) (queue (long 1) (long 2)) (map (kw ""k"") (long 1)) (sorted-map (long 2) (str ""b"") (long 1) (str ""a""))
          (meta (map (kw ""tag"") (sym ""String"")) (sym ""s"")) (regex ""a+"") (uuid ""7f2a1c2e-0000-4000-8000-000000000001"") (type ""System.String"")
          (def 0 (vec (long 7))) (vec (ref 0) (ref 0))");
        Assert.That(t[0], Is.Null);
        Assert.That(t[1], Is.EqualTo(true));
        Assert.That(t[2], Is.EqualTo(-5L));
        Assert.That(t[3], Is.EqualTo(1.0));
        Assert.That(RT.printString(t[4]), Is.EqualTo("123456789012345678901234567890N"));
        Assert.That(RT.printString(t[5]), Is.EqualTo("1.5M"));
        Assert.That(RT.printString(t[6]), Is.EqualTo("1/3"));
        Assert.That(t[7], Is.EqualTo('A'));
        Assert.That(t[8], Is.EqualTo("héllo"));
        Assert.That(t[9], Is.EqualTo(Symbol.intern("x")));
        Assert.That(t[10], Is.EqualTo(Symbol.intern("clojure.core", "inc")));
        Assert.That(t[11], Is.SameAs(Keyword.intern("a")));
        Assert.That(t[12], Is.SameAs(Keyword.intern("ns", "b")));
        Assert.That(t[13], Is.InstanceOf<Var>());
        Assert.That(((Var)t[13]).ToString(), Is.EqualTo("#'cleat.test.fasl/f"));
        Assert.That(RT.printString(t[14]), Is.EqualTo("(1 2)"));
        Assert.That(t[14], Is.InstanceOf<IPersistentList>());
        Assert.That(RT.printString(t[15]), Is.EqualTo("[1]"));
        Assert.That(RT.printString(t[16]), Is.EqualTo("#{:a}"));
        Assert.That(RT.printString(t[17]), Is.EqualTo("#{1 3}"));
        Assert.That(t[17], Is.InstanceOf<PersistentTreeSet>());
        Assert.That(t[18], Is.InstanceOf<PersistentQueue>());
        Assert.That(RT.printString(t[18]), Is.EqualTo("(1 2)"));
        Assert.That(RT.printString(t[19]), Is.EqualTo("{:k 1}"));
        Assert.That(RT.printString(t[20]), Is.EqualTo("{1 \"a\", 2 \"b\"}"));
        Assert.That(RT.printString(((IObj)t[21]).meta()), Is.EqualTo("{:tag String}"));
        Assert.That(t[22], Is.InstanceOf<Regex>());
        Assert.That(t[23], Is.EqualTo(Guid.Parse("7f2a1c2e-0000-4000-8000-000000000001")));
        Assert.That(t[24], Is.EqualTo(typeof(string)));
        var shared = (IPersistentVector)t[26];
        Assert.That(shared.nth(0), Is.SameAs(shared.nth(1)), "ref shares the def'd object");
        Assert.That(shared.nth(0), Is.SameAs(t[25]));
    }

    [Test]
    public void PackedTableRoundTrips()
    {
        var items = new SExprReader("((long 1) (str \"x\"))", "c").ReadAll();
        var raw = FaslEncoder.EncodeTable(((SList)items[0]).Items, "c");
        var ms = new System.IO.MemoryStream();
        using (var z = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionLevel.Optimal, true)) z.Write(raw, 0, raw.Length);
        var table = Fasl.ReadPackedTable(ms.ToArray());
        Assert.That(table, Is.EqualTo(new object[] { 1L, "x" }));
    }

    [Test]
    public void ObjectsWriteAndReadBack()
    {
        var meta = RT.map(Keyword.intern("line"), 3L);
        var sym = (Symbol)Symbol.intern("a", "b").withMeta(meta);
        var shared = RT.vector(1L, 2L);
        object[] values =
        {
            null, true, 42L, 2.5, 'c', "s", Keyword.intern("k"), Symbol.intern("q"), sym,
            RT.list(1L, RT.vector(2L, 3L)), RT.map(Keyword.intern("a"), RT.set(RT.vector(1L))), PersistentTreeMap.create(RT.seq(RT.vector(2L, "b", 1L, "a"))),
            RT.vector(shared, shared), new Regex("x"), Guid.Empty, typeof(long),
            LispReader.MatchNumber("1/3"), LispReader.MatchNumber("99999999999999999999N"), LispReader.MatchNumber("1.5M"),
        };
        foreach (var v in values)
        {
            var back = Fasl.Read(Fasl.Write(v));
            if (v is Regex) Assert.That(back.ToString(), Is.EqualTo("x"));
            else Assert.That(RT.printString(back), Is.EqualTo(RT.printString(v)), RT.printString(v));
        }
        var symBack = (Symbol)Fasl.Read(Fasl.Write(sym));
        Assert.That(RT.printString(symBack.meta()), Is.EqualTo("{:line 3}"));
        var sharedBack = (IPersistentVector)Fasl.Read(Fasl.Write(RT.vector(shared, shared)));
        Assert.That(sharedBack.nth(0), Is.SameAs(sharedBack.nth(1)));
        Assert.That(Fasl.Write(RT.vector(1L, 2L)), Is.EqualTo(Fasl.Write(RT.vector(1L, 2L))), "deterministic");
        Assert.Throws<System.IO.InvalidDataException>(() => Fasl.Write(new object()));
    }
}
