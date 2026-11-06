using System;
using System.Text.RegularExpressions;
using Fasl = Cleat.Core.Runtime.Fasl;
using clojure.lang;
using NUnit.Framework;

namespace Cleat.Core.Tests;

// cleat-021：对象 → fasl → 对象（cleat-092 起由 Cleat.Core.Runtime.Fasl 读写；Scheme 的 fasl 随元层在 cleat-093 删除）
public class FaslTests
{
    /// <summary>
    /// cleat-092：Clojure 值经 Fasl 写出再读回相同（接口文件旁的 .inl 用它）。常量表的编码与读回见 Cleat.Compiler.Tests 的
    /// ConstantEncoderTests（cleat-182 之前经发射器 Cleat.Emit 的 FaslEncoder 在这里测）。
    /// </summary>
    [Test]
    public void InlineClausesWriteAndReadBack()
    {
        // 写出再读回（接口文件旁的 .inl：L0 子句的 S 表达式，[:cleat/l0 (clause (g$x$1$c) false …)]）
        object v = RT.readString("[:cleat/l0 (clause (g$x$1$c) false (clj-invoke (clj-var-ref 1) g$x$1$c \"s\" 2.5 \\c {:a [1 2]} #{3} nil true))]");
        var back = Fasl.Read(Fasl.Write(v));
        Assert.That(RT.printString(back), Is.EqualTo(RT.printString(v)));
        Assert.That(back, Is.EqualTo(v));
        var withMeta = ((IObj)Symbol.intern("x")).withMeta(RT.map(Keyword.intern("tag"), Symbol.intern("long")));
        Assert.That(((IMeta)Fasl.Read(Fasl.Write(withMeta))).meta(), Is.EqualTo(withMeta.meta()));
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
