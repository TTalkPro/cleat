using Cleat.Emit;
using NUnit.Framework;

namespace Cleat.Core.Tests;

// cleat-021：编码 → fasl（读回测试在 FaslTests，依赖 clojure.lang 类型）
public class FaslEncoderTests
{
    private static byte[] Encode(string table)
    {
        var items = new SExprReader("(" + table + ")", "c").ReadAll();
        return FaslEncoder.EncodeTable(((SList)items[0]).Items, "c");
    }

    [Test]
    public void EncodesEveryFormDeterministically()
    {
        const string table = @"(nil) (true) (false) (long -5) (double ""3FF0000000000000"") (bigint ""123456789012345678901234567890"") (bigdec ""1.5"") (ratio ""1/3"")
          (char 65) (str ""héllo"") (sym ""x"") (sym ""clojure.core"" ""inc"") (kw ""a"") (kw ""ns"" ""b"") (var ""user"" ""f"")
          (list (long 1) (long 2)) (vec) (set (kw ""a"")) (sorted-set (long 1)) (queue (long 1)) (map (kw ""k"") (long 1)) (sorted-map (long 1) (str ""v""))
          (meta (map (kw ""tag"") (sym ""String"")) (sym ""s"")) (regex ""a+"") (inst ""2026-10-10T00:00:00Z"") (uuid ""00000000-0000-0000-0000-000000000000"") (type ""System.String"")
          (record ""user.R"" (long 1)) (def 0 (vec (ref 0)))";
        var a = Encode(table);
        var b = Encode(table);
        Assert.That(a, Is.EqualTo(b));
        Assert.That(a[..6], Is.EqualTo(FaslEncoder.Magic));
        Assert.That(a[6], Is.EqualTo(FaslEncoder.Version));
        Assert.That(a[7], Is.EqualTo(FaslEncoder.Table));
        Assert.That(a[8], Is.EqualTo(29), "table length");
    }

    [Test]
    public void RejectsBadEncodings()
    {
        Assert.That(Assert.Throws<LcilException>(() => Encode("(frob 1)")).Message, Does.Contain("unknown encoding: frob"));
        Assert.That(Assert.Throws<LcilException>(() => Encode("(ref 3)")).Message, Does.Contain("undefined object 3"));
        Assert.That(Assert.Throws<LcilException>(() => Encode("(def 1 (nil)) (def 1 (nil))")).Message, Does.Contain("duplicate"));
        Assert.That(Assert.Throws<LcilException>(() => Encode("(double \"abc\")")).Message, Does.Contain("16 hexadecimal"));
        Assert.That(Assert.Throws<LcilException>(() => Encode("(map (kw \"a\"))")).Message, Does.Contain("key / value pairs"));
        Assert.That(Assert.Throws<LcilException>(() => Encode("(char 70000)")).Message, Does.Contain("UTF-16"));
    }
}
