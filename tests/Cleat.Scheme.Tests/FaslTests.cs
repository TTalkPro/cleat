using System;
using System.IO;
using System.Linq;
using System.Text;
using Cleat.Emit;
using NUnit.Framework;
using BigInteger = System.Numerics.BigInteger;

namespace Cleat.Scheme.Tests;

/// <summary>常量 fasl（iron-020）：Core 直接读写往返，以及 Lcil 常量表经发射器生成后读回。</summary>
public class FaslTests
{
    private static Symbol S(string n) => Symbol.Intern(n);
    private static object RoundTrip(object o) => Fasl.Read(Fasl.Write(o));

    private static RecordTypeDescriptor Rtd(string name, object uid, object parent = null, params string[] fields) =>
      (RecordTypeDescriptor)Cleat.Scheme.Records.MakeRecordTypeDescriptor(S(name), parent ?? RT.False, uid, RT.False, RT.False,
        fields.Select(f => (object)Cons.FromArray(S("mutable"), S(f))).ToArray());

    private static Record Make(RecordTypeDescriptor rtd, params object[] fields) =>
      (Record)((Procedure)Cleat.Scheme.Records.RecordConstructor(Cleat.Scheme.Records.MakeRecordConstructorDescriptor(rtd, RT.False, RT.False))).InvokeN(fields);

    [Test]
    public void Atoms()
    {
        object[] atoms =
        {
    null, true, false, Unspecified.Instance, Builtins.EofObject(), 0, -5, int.MaxValue, 1.5, -0.0, double.NaN, double.PositiveInfinity,
    'a', new SchemeChar(0x1F600), "", "strλ😀", S("sym"), S("g$x$1$%seed"),
    Builtins.ParseNumber("123456789012345678901234567890"), Builtins.ParseNumber("-2/3"),
    Builtins.ParseNumber("1+2i"), Builtins.ParseNumber("1.5-2.5i"), new byte[] { 1, 2, 255 },
  };
        foreach (var a in atoms)
        {
            var r = RoundTrip(a);
            if (a is double d) Assert.That(BitConverter.DoubleToInt64Bits((double)r), Is.EqualTo(BitConverter.DoubleToInt64Bits(d)), "double " + d);
            else if (a is byte[] bv) Assert.That(r, Is.EqualTo(bv));
            // 含 BMP 外字符的字符串解码为宽 SchemeString（iron-055），按内容比较
            else if (a is string str) Assert.That(SchemeString.ContentEquals(r, SchemeString.Normalize(str)), Is.True, $"{a} → {r}");
            else Assert.That(Builtins.IsTrue(Builtins.IsEqualValue(r, a)), Is.True, $"{a} → {r}");
        }
        Assert.That(RoundTrip(S("sym")), Is.SameAs(S("sym")));
        Assert.That(RoundTrip("strλ😀"), Is.InstanceOf<SchemeString>(), "含 BMP 外字符：宽串");
        Assert.That(RoundTrip("strλ"), Is.InstanceOf<string>(), "纯 BMP：System.String");
        // 可变串解码为可变 SchemeString（iron-055；旧的 StringBuilder 也按可变串写出）
        Assert.That(RoundTrip(SchemeString.MutableFrom("m")), Is.InstanceOf<SchemeString>().And.Matches<SchemeString>(x => x.IsMutable));
        Assert.That(RoundTrip(new StringBuilder("m")), Is.InstanceOf<SchemeString>().And.Matches<SchemeString>(x => x.IsMutable));
        Assert.That(RoundTrip(Builtins.ParseNumber("123456789012345678901234567890")), Is.InstanceOf<BigInteger>());
    }

    [Test]
    public void SharingAndCycles()
    {
        var shared = new Cons(1, new Cons(2, null));
        var v = new object[] { shared, shared, "x" };
        var r = (object[])RoundTrip(v);
        Assert.That(r[0], Is.SameAs(r[1]));
        // 环：表尾指回表头、向量包含自己
        var ring = new Cons(1, new Cons(2, null));
        ((Cons)ring.cdr).cdr = ring;
        var rr = (Cons)RoundTrip(ring);
        Assert.That(((Cons)rr.cdr).cdr, Is.SameAs(rr));
        var self = new object[1];
        self[0] = self;
        var sr = (object[])RoundTrip(self);
        Assert.That(sr[0], Is.SameAs(sr));
        // 表中间被共享的序对
        var tail = new Cons(3, null);
        var two = new object[] { new Cons(1, new Cons(2, tail)), tail };
        var tr = (object[])RoundTrip(two);
        Assert.That(((Cons)((Cons)((Cons)tr[0]).cdr).cdr), Is.SameAs(tr[1]));
    }

    [Test]
    public void LongListDoesNotRecurse()
    {
        object l = null;
        for (int i = 0; i < 200000; i++) l = new Cons(i, l);
        var r = (Cons)RoundTrip(l);
        Assert.That(r.car, Is.EqualTo(199999));
        Assert.That(r.Count(), Is.EqualTo(200000));
    }

    [Test]
    public void RecordConstants()
    {
        var point = Rtd("fasl-point", S("fasl-point-uid-1"), null, "x", "y");
        var p = Make(point, 1, Make(point, 2, 3));
        var r = (Record)RoundTrip(p);
        Assert.That(r.Rtd, Is.SameAs(point), "非生成性 RTD 按 uid 取回同一对象");
        Assert.That(((Record)r.Fields[1]).Fields[1], Is.EqualTo(3));
        // 生成性 RTD：新建，但同一 fasl 内共享
        var gen = Rtd("fasl-gen", RT.False, null, "a");
        var pair = new object[] { Make(gen, 1), Make(gen, 2) };
        var gr = (object[])RoundTrip(pair);
        Assert.That(((Record)gr[0]).Rtd, Is.SameAs(((Record)gr[1]).Rtd));
        Assert.That(((Record)gr[0]).Rtd, Is.Not.SameAs(gen));
        Assert.That(((Record)gr[0]).Rtd.Name, Is.EqualTo("fasl-gen"));
        // 子类型与条件
        var child = Rtd("fasl-child", S("fasl-child-uid-1"), point, "z");
        var c = (Record)RoundTrip(Make(child, 1, 2, 3));
        Assert.That(c.Rtd, Is.SameAs(child));
        Assert.That(c.Fields, Is.EqualTo(new object[] { 1, 2, 3 }));
        // 记录引用自己
        var node = Make(point, 0, 0);
        node.Fields[1] = node;
        var nr = (Record)RoundTrip(node);
        Assert.That(nr.Fields[1], Is.SameAs(nr));
    }

    [Test]
    public void Annotations()
    {
        var src = new Cons("file.ss", "(1,1) - (1,5)");
        var a = (Annotation)Builtins.MakeAnnotation(Cons.FromArray(S("f"), 1), src, Cons.FromArray(S("f"), 1));
        var r = (Annotation)RoundTrip(a);
        Assert.That(r, Is.InstanceOf<ConsAnnotation>());
        Assert.That(((Cons)r.source).cdr, Is.EqualTo("(1,1) - (1,5)"));
    }

    [Test]
    public void EqHashtablesAndCompoundConditions()
    {
        var ht = new System.Collections.Hashtable();
        ht[S("b")] = 2;
        ht[S("a")] = Cons.FromArray(1);
        ht[Hashtables.ToNull(null)] = "null-key";
        var both = (object[])RoundTrip(new object[] { ht, ht });
        Assert.That(both[0], Is.SameAs(both[1]));
        var r = (System.Collections.Hashtable)both[0];
        Assert.That(r[S("b")], Is.EqualTo(2));
        Assert.That(r[Hashtables.ToNull(null)], Is.EqualTo("null-key"));
        // 同内容的表写出相同字节（条目按键排序）
        var ht2 = new System.Collections.Hashtable { [S("a")] = 1, [S("b")] = 2, [S("c")] = 3 };
        var ht3 = new System.Collections.Hashtable { [S("c")] = 3, [S("b")] = 2, [S("a")] = 1 };
        Assert.That(Fasl.Write(ht2), Is.EqualTo(Fasl.Write(ht3)));

        var condition = Rtd("&condition", S("fasl-condition-uid"));
        var message = Rtd("&fasl-message", S("fasl-message-uid"), condition, "message");
        var cc = new CompoundCondition(new object[] { Make(message, "m"), Make(message, "n") });
        var rc = RoundTrip(cc);
        Assert.That(rc, Is.InstanceOf<CompoundCondition>());
        var parts = ((Cons)Conditions.SimpleConditions(rc)).ToList();
        Assert.That(parts.Count, Is.EqualTo(2));
        Assert.That(((Record)parts[1]).Fields[0], Is.EqualTo("n"));
        Assert.That(parts[0], Is.InstanceOf<Condition>());
    }

    [Test]
    public void SerializePort()
    {
        var ms = new MemoryStream();
        Fasl.SerializePort(Cons.FromArray(S("a"), "b", 3), ms);
        ms.Position = 0;
        var r = (Cons)Fasl.DeserializePort(ms);
        Assert.That(r.car, Is.SameAs(S("a")));
        Assert.Throws<SchemeRuntimeError>(() => Fasl.Write(new object()));
    }

    // —— 经发射器：Lcil 常量表 → fasl → $Constants.c ——

    private const string ConstLcil = @"
(lcil 1
  (assembly ""consts"" (references ""Cleat.Scheme"") (runtime scheme))
  (constants
    (0 (def 0 (list (sym ""a"") (fix 1) (str ""x\x3bb;"") (flo ""3FF8000000000000"") (big ""-123456789012345678901234567890"") (rat ""1/3""))))
    (1 (vec (ref 0) (ref 0) (bv ""01ff"") (chr 128512) (cpx (fix 1) (fix -2)) (dlist (true) (false) (nil)) (dlist (fix 1) (fix 2))))
    (2 (def 1 (pair (unspecified) (ref 1))))
    (3 (rec (def 2 (rtd ""lcil-point"" (sym ""lcil-point-uid"") (false) #f #f ((immutable ""x"") (mutable ""y""))))
            (fix 1) (rec (ref 2) (fix 2) (mstr ""m""))))
    (4 (ann (list (sym ""f"")) (pair (str ""f.ss"") (str ""(1,1) - (1,3)"")) (list (sym ""f"")))))
  (type ""C"" (attrs abstract sealed)
    (method ""Get"" (attrs static) (params ""System.Int32"") (ret ""System.Object"")
      (body
        (ldarg 0) (switch (k0 k1 k2 k3 k4))
        (ldnull) (ret)
        (label k0) (ldconst 0) (ret)
        (label k1) (ldconst 1) (ret)
        (label k2) (ldconst 2) (ret)
        (label k3) (ldconst 3) (ret)
        (label k4) (ldconst 4) (ret)))))";

    [TestCaseSource(typeof(EmitterTests), nameof(EmitterTests.Modes))]
    public void ConstantTableThroughEmitter(EmitMode mode)
    {
        var a = LcilParser.Parse(ConstLcil, "consts");
        System.Reflection.Assembly asm;
        if (mode == EmitMode.InMemory) asm = LcilEmitter.EmitInMemory(a);
        else
        {
            var path = Path.Combine(Path.GetTempPath(), "iron-consts-" + Guid.NewGuid().ToString("N") + ".dll");
            LcilEmitter.Save(a, path);
            asm = System.Reflection.Assembly.LoadFile(path);
        }
        object Get(int k) => asm.GetType("C").GetMethod("Get").Invoke(null, new object[] { k });
        var c0 = (Cons)Get(0);
        Assert.That(c0.car, Is.SameAs(S("a")));
        var items = c0.ToList();
        Assert.That(items[2], Is.EqualTo("xλ"));
        Assert.That(items[3], Is.EqualTo(1.5));
        Assert.That(items[4].ToString(), Is.EqualTo("-123456789012345678901234567890"));
        Assert.That(items[5], Is.InstanceOf<Fraction>());
        var c1 = (object[])Get(1);
        Assert.That(c1[0], Is.SameAs(c0), "def/ref 跨常量共享");
        Assert.That(c1[1], Is.SameAs(c0));
        Assert.That(c1[2], Is.EqualTo(new byte[] { 1, 255 }));
        Assert.That(c1[3], Is.EqualTo(new SchemeChar(0x1F600)));
        Assert.That(c1[4], Is.InstanceOf<ComplexFraction>());
        Assert.That(((Cons)c1[5]).Count(), Is.EqualTo(2));
        Assert.That(((Cons)c1[6]).cdr, Is.EqualTo(2), "dlist：(1 . 2)");
        var c2 = (Cons)Get(2);
        Assert.That(c2.cdr, Is.SameAs(c2), "环");
        var c3 = (Record)Get(3);
        Assert.That(c3.Rtd.Name, Is.EqualTo("lcil-point"));
        Assert.That(((Record)c3.Fields[1]).Rtd, Is.SameAs(c3.Rtd));
        Assert.That(((Record)c3.Fields[1]).Fields[1], Is.InstanceOf<SchemeString>().And.Matches<SchemeString>(x => x.IsMutable));   // mstr（iron-055）
        Assert.That(Cleat.Scheme.Records.LookupNongenerative("lcil-point", "lcil-point-uid"), Is.SameAs(c3.Rtd));
        Assert.That(Get(4), Is.InstanceOf<ConsAnnotation>());
        Assert.That(Get(0), Is.SameAs(c0), "常量只读一次");
    }

    [Test]
    public void EncodingErrors()
    {
        string L(string enc) => "(lcil 1 (assembly \"e\") (constants (0 " + enc + ")) (type \"T\" (method \"M\" (attrs static) (params) (ret \"System.Object\") (body (ldconst 0) (ret)))))";
        Assert.That(Assert.Throws<LcilException>(() => LcilEmitter.EmitInMemory(L("(frob)"))).Message, Does.Contain("unknown encoding: frob"));
        Assert.That(Assert.Throws<LcilException>(() => LcilEmitter.EmitInMemory(L("(ref 3)"))).Message, Does.Contain("undefined object 3"));
        Assert.That(Assert.Throws<LcilException>(() => LcilEmitter.EmitInMemory(L("(flo \"xyz\")"))).Message, Does.Contain("16 hexadecimal digits"));
    }
}
