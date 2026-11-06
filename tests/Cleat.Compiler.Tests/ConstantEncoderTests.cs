using System;
using System.Text.RegularExpressions;
using Cleat.Compiler.Emit;
using clojure.lang;
using NUnit.Framework;
using Fasl = Cleat.Core.Runtime.Fasl;

namespace Cleat.Compiler.Tests;

// 持久化单元的常量表：ConstantEncoder 写、Cleat.Core.Runtime.Fasl 读（cleat-021 / 092 的 FaslTests 原来经已删的发射器 Cleat.Emit 编码，cleat-182 挪到这里）
public class ConstantEncoderTests
{
    private static object V(string s) => RT.readString(s);

    [Test]
    public void ConstantsReadBackAsClojureData()
    {
        var sym = ((IObj)Symbol.intern("s")).withMeta(RT.map(Keyword.intern("tag"), Symbol.intern("String")));
        object[] values =
        {
            null, true, false, -5L, 7, 1.0, 2.0, V("123456789012345678901234567890N"), V("1.5M"), V("1/3"),
            'A', "héllo", Symbol.intern("x"), Symbol.intern("clojure.core", "inc"), Keyword.intern("a"), Keyword.intern("ns", "b"),
            RT.var("cleat.test.fasl", "f"), V("(1 2)"), V("[1]"), V("#{:a}"), PersistentTreeSet.create(RT.seq(RT.vector(3L, 1L))),
            V("{:k 1}"), PersistentTreeMap.create(RT.seq(RT.vector(2L, "b", 1L, "a"))), sym, new Regex("a+"), typeof(string),
        };
        var t = Fasl.ReadTable(ConstantEncoder.EncodeTable(values));
        Assert.That(t, Has.Length.EqualTo(values.Length));
        Assert.That(t[0], Is.Null);
        Assert.That(t[1], Is.EqualTo(true));
        Assert.That(t[2], Is.EqualTo(false));
        Assert.That(t[3], Is.EqualTo(-5L));
        Assert.That(t[4], Is.TypeOf<int>().And.EqualTo(7), "fix reads back as Int32");
        Assert.That(t[5], Is.EqualTo(1.0));
        Assert.That(t[6], Is.EqualTo(2.0));
        Assert.That(RT.printString(t[7]), Is.EqualTo("123456789012345678901234567890N"));
        Assert.That(RT.printString(t[8]), Is.EqualTo("1.5M"));
        Assert.That(RT.printString(t[9]), Is.EqualTo("1/3"));
        Assert.That(t[10], Is.EqualTo('A'));
        Assert.That(t[11], Is.EqualTo("héllo"));
        Assert.That(t[12], Is.EqualTo(Symbol.intern("x")));
        Assert.That(t[13], Is.EqualTo(Symbol.intern("clojure.core", "inc")));
        Assert.That(t[14], Is.SameAs(Keyword.intern("a")));
        Assert.That(t[15], Is.SameAs(Keyword.intern("ns", "b")));
        Assert.That(((Var)t[16]).ToString(), Is.EqualTo("#'cleat.test.fasl/f"));
        Assert.That(t[17], Is.InstanceOf<IPersistentList>());
        Assert.That(RT.printString(t[17]), Is.EqualTo("(1 2)"));
        Assert.That(t[18], Is.InstanceOf<IPersistentVector>());
        Assert.That(RT.printString(t[19]), Is.EqualTo("#{:a}"));
        Assert.That(t[20], Is.InstanceOf<PersistentTreeSet>());
        Assert.That(RT.printString(t[20]), Is.EqualTo("#{1 3}"));
        Assert.That(RT.printString(t[21]), Is.EqualTo("{:k 1}"));
        Assert.That(RT.printString(t[22]), Is.EqualTo("{1 \"a\", 2 \"b\"}"));
        Assert.That(RT.printString(((IObj)t[23]).meta()), Is.EqualTo("{:tag String}"));
        Assert.That(t[24], Is.InstanceOf<Regex>());
        Assert.That(t[24].ToString(), Is.EqualTo("a+"));
        Assert.That(t[25], Is.EqualTo(typeof(string)));
    }

    /// <summary>哈希 map / set 的项按编码文本排序：两次编码逐字节相同（镜像确定性，G5）。</summary>
    [Test]
    public void EncodingIsDeterministic()
    {
        object[] values = { V("{:b 2 :a 1 \"c\" [3]}"), V("#{:z :y 1 \"x\"}"), RT.var("clojure.core", "map"), typeof(long) };
        Assert.That(ConstantEncoder.EncodeTable(values), Is.EqualTo(ConstantEncoder.EncodeTable(values)));
        var reordered = ConstantEncoder.EncodeTable(new object[] { V("{\"c\" [3] :a 1 :b 2}"), V("#{\"x\" 1 :y :z}"), RT.var("clojure.core", "map"), typeof(long) });
        Assert.That(reordered, Is.EqualTo(ConstantEncoder.EncodeTable(values)), "independent of hash iteration order");
        Assert.Throws<InvalidOperationException>(() => ConstantEncoder.EncodeTable(new object[] { new object() }));
    }

    [Test]
    public void PackedTableRoundTrips()
    {
        var raw = ConstantEncoder.EncodeTable(new object[] { 1L, "x" });
        var ms = new System.IO.MemoryStream();
        using (var z = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionLevel.Optimal, true)) z.Write(raw, 0, raw.Length);
        Assert.That(Fasl.ReadPackedTable(ms.ToArray()), Is.EqualTo(new object[] { 1L, "x" }));
    }

    /// <summary>未知的标签（IronScheme 联合编码里 Scheme 的 '()）不认。</summary>
    [Test]
    public void UnknownTagIsRejected()
    {
        var bad = ConstantEncoder.EncodeTable(new object[] { null });
        bad[^1] = 0x05;
        Assert.Throws<System.IO.InvalidDataException>(() => Fasl.ReadTable(bad));
    }
}
