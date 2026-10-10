using System.Linq;
using Cleat.Scheme;
using NUnit.Framework;

namespace Cleat.Scheme.Tests;

[NonParallelizable]
public class RecordsTests
{
    private static object[] Fields(params (string kind, string name)[] fs) =>
      fs.Select(f => (object)Cons.FromArray(Symbol.Intern(f.kind), Symbol.Intern(f.name))).ToArray();

    private static RecordTypeDescriptor Rtd(string name, object parent, params (string, string)[] fields) =>
      (RecordTypeDescriptor)Records.MakeRecordTypeDescriptor(Symbol.Intern(name), parent ?? RT.False, RT.False, RT.False, RT.False, Fields(fields));

    private static Procedure Ctor(RecordTypeDescriptor rtd, object parentRcd = null, Procedure protocol = null) =>
      (Procedure)Records.RecordConstructor(Records.MakeRecordConstructorDescriptor(rtd, parentRcd ?? RT.False, (object)protocol ?? RT.False));

    [Test]
    public void BasicRecord()
    {
        var point = Rtd("point", null, ("immutable", "x"), ("mutable", "y"));
        var make = Ctor(point);
        var p = make.Invoke2(1, 2);
        Assert.That(((Procedure)Records.RecordPredicate(point)).Invoke1(p), Is.SameAs(RT.True));
        Assert.That(((Procedure)Records.RecordAccessor(point, 0)).Invoke1(p), Is.EqualTo(1));
        var sety = (Procedure)Records.RecordMutator(point, 1);
        sety.Invoke2(p, 9);
        Assert.That(((Procedure)Records.RecordAccessor(point, 1)).Invoke1(p), Is.EqualTo(9));
        Assert.That(Records.RecordMutator(point, 0), Is.SameAs(RT.False));      // 不可变字段（与旧系统一致）
        Assert.That(Records.IsRecord(p), Is.SameAs(RT.True));
        Assert.That(Records.RecordRtd(p), Is.SameAs(point));
    }

    [Test]
    public void ConstructorArityChecked()
    {
        var make = Ctor(Rtd("pair2", null, ("immutable", "a"), ("immutable", "b")));
        var e = Assert.Throws<SchemeRuntimeError>(() => make.Invoke1(1));
        Assert.That(e.Message, Does.Contain("expected 2 got 1"));
    }

    [Test]
    public void InheritanceAndPredicates()
    {
        var a = Rtd("a", null, ("immutable", "x"));
        var b = Rtd("b", a, ("immutable", "y"));
        var ib = Ctor(b).Invoke2(1, 2);                    // 父字段在前
        var ia = Ctor(a).Invoke1(3);
        Assert.That(a.Predicate.Invoke1(ib), Is.SameAs(RT.True));
        Assert.That(b.Predicate.Invoke1(ia), Is.SameAs(RT.False));
        Assert.That(((Procedure)Records.RecordAccessor(a, 0)).Invoke1(ib), Is.EqualTo(1));
        Assert.That(((Procedure)Records.RecordAccessor(b, 0)).Invoke1(ib), Is.EqualTo(2));
        Assert.Throws<SchemeRuntimeError>(() => ((Procedure)Records.RecordAccessor(b, 0)).Invoke1(ia));
        Assert.That(b.TotalFieldCount, Is.EqualTo(2));
        Assert.That(b.GetAllFields().Select(f => f.Name), Is.EqualTo(new[] { "x", "y" }));
    }

    [Test]
    public void ProtocolsAlongParentChain()
    {
        // (define-record-type a (fields x) (protocol (lambda (n) (lambda (v) (n (* v 10))))))
        var a = Rtd("pa", null, ("immutable", "x"));
        var aRcd = Records.MakeRecordConstructorDescriptor(a, RT.False, new Fn(n => new Fn(v => ((Procedure)n[0]).Invoke1((int)v[0] * 10))));
        // b 的 protocol：(lambda (n) (lambda (v w) ((n v) w)))
        var b = Rtd("pb", a, ("immutable", "y"));
        var bRcd = Records.MakeRecordConstructorDescriptor(b, aRcd,
          new Fn(n => new Fn(vw => ((Procedure)((Procedure)n[0]).Invoke1(vw[0])).Invoke1(vw[1]))));
        var r = (Record)((Procedure)Records.RecordConstructor(bRcd)).Invoke2(1, 2);
        Assert.That(r.Fields, Is.EqualTo(new object[] { 10, 2 }));
    }

    [Test]
    public void DefaultProtocolWithParentProtocol()
    {
        // 父有 protocol、子没有：子默认 protocol 把前 1 个参数交给父层
        var a = Rtd("da", null, ("immutable", "x"));
        var aRcd = Records.MakeRecordConstructorDescriptor(a, RT.False, new Fn(n => new Fn(v => ((Procedure)n[0]).Invoke1((int)v[0] + 100))));
        var b = Rtd("db", a, ("immutable", "y"));
        var bRcd = Records.MakeRecordConstructorDescriptor(b, aRcd, RT.False);
        var r = (Record)((Procedure)Records.RecordConstructor(bRcd)).Invoke2(1, 2);
        Assert.That(r.Fields, Is.EqualTo(new object[] { 101, 2 }));
    }

    [Test]
    public void NongenerativeReturnsSameRtd()
    {
        var uid = Symbol.Intern("point-uid-1234");
        object Make() => Records.MakeRecordTypeDescriptor(Symbol.Intern("ngpoint"), RT.False, uid, RT.False, RT.False, Fields(("immutable", "x")));
        var r1 = Make();
        Assert.That(Make(), Is.SameAs(r1));
        Assert.That(Records.LookupNongenerative("ngpoint", "point-uid-1234"), Is.SameAs(r1));
        Assert.That(((RecordTypeDescriptor)r1).Generative, Is.SameAs(RT.False));
    }

    [Test]
    public void OpaqueAndSealed()
    {
        var o = (RecordTypeDescriptor)Records.MakeRecordTypeDescriptor(Symbol.Intern("opq"), RT.False, RT.False, RT.True, RT.True, Fields(("immutable", "x")));
        var inst = Ctor(o).Invoke1(1);
        Assert.That(Records.IsRecord(inst), Is.SameAs(RT.False));
        Assert.Throws<SchemeRuntimeError>(() => Records.RecordRtd(inst));
        Assert.That(o.Predicate.Invoke1(inst), Is.SameAs(RT.True));            // 谓词仍然可用
        Assert.Throws<SchemeRuntimeError>(() => Rtd("child-of-sealed", o));     // 父类型 sealed
    }

    [Test]
    public void InvalidFieldSpec()
    {
        Assert.Throws<SchemeRuntimeError>(() => Records.MakeRecordTypeDescriptor(Symbol.Intern("bad"), RT.False, RT.False, RT.False, RT.False,
          new object[] { Cons.FromArray(Symbol.Intern("weird"), Symbol.Intern("x")) }));
    }

    [Test]
    public void InspectionMembers()
    {
        var r = Rtd("insp", null, ("mutable", "f"));
        Assert.That(r.Name, Is.EqualTo("insp"));
        Assert.That(r.Fields[0].Name, Is.EqualTo("f"));
        Assert.That(r.Fields[0].Mutable, Is.SameAs(RT.True));
        Assert.That(r.Sealed, Is.SameAs(RT.False));
        Assert.That(r.Generative, Is.SameAs(RT.True));
        Assert.That(r.uid, Is.SameAs(RT.False));
    }
}

[NonParallelizable]
public class ConditionsTests
{
    private static RecordTypeDescriptor s_condRtd, s_messageRtd, s_whoRtd;

    [OneTimeSetUp]
    public void DefineConditionTypes()
    {
        // 模拟 boot/conditions.sls：(define-record-type &condition (nongenerative)) 及两个子类型
        s_condRtd = (RecordTypeDescriptor)Records.MakeRecordTypeDescriptor(Symbol.Intern("&condition"), RT.False, Symbol.Intern("cond-uid"), RT.False, RT.False, new object[0]);
        s_messageRtd = (RecordTypeDescriptor)Records.MakeRecordTypeDescriptor(Symbol.Intern("&message"), s_condRtd, RT.False, RT.False, RT.False,
          new object[] { Cons.FromArray(Symbol.Intern("immutable"), Symbol.Intern("message")) });
        s_whoRtd = (RecordTypeDescriptor)Records.MakeRecordTypeDescriptor(Symbol.Intern("&who"), s_condRtd, RT.False, RT.False, RT.False,
          new object[] { Cons.FromArray(Symbol.Intern("immutable"), Symbol.Intern("who")) });
    }

    private static object Make(RecordTypeDescriptor rtd, params object[] args) =>
      ((Procedure)Records.RecordConstructor(Records.MakeRecordConstructorDescriptor(rtd, RT.False, RT.False))).InvokeN(args);

    [Test]
    public void ConditionTypesAreRegisteredGlobally()
    {
        Assert.That(Symbol.Intern("&message-rtd").Value, Is.SameAs(s_messageRtd));
        Records.MakeRecordConstructorDescriptor(s_messageRtd, RT.False, RT.False);
        Assert.That(Symbol.Intern("&message-rcd").Value, Is.InstanceOf<RecordConstructorDescriptor>());
        Assert.That(s_messageRtd.IsCondition, Is.True);
    }

    [Test]
    public void SimpleConditionIsConditionRecord()
    {
        var m = Make(s_messageRtd, "oops");
        Assert.That(m, Is.InstanceOf<Condition>());
        Assert.That(Records.IsRecord(m), Is.SameAs(RT.True));
    }

    [Test]
    public void CompoundConditions()
    {
        var m = Make(s_messageRtd, "oops");
        var w = Make(s_whoRtd, "car");
        var c = Conditions.MakeCondition(m, Conditions.MakeCondition(w, m));     // 嵌套展开
        Assert.That(c, Is.InstanceOf<CompoundCondition>());
        Assert.That(((Cons)Conditions.SimpleConditions(c)).Count(), Is.EqualTo(3));
        Assert.That(Records.IsRecord(c), Is.SameAs(RT.False));
        Assert.That(((Cons)Conditions.SimpleConditions(m)).ToArray(), Is.EqualTo(new[] { m }));

        var isWho = (Procedure)Conditions.ConditionPredicate(s_whoRtd);
        Assert.That(isWho.Invoke1(c), Is.SameAs(RT.True));
        Assert.That(isWho.Invoke1(m), Is.SameAs(RT.False));

        var who = (Procedure)Conditions.ConditionAccessor(s_whoRtd, (Procedure)Records.RecordAccessor(s_whoRtd, 0));
        Assert.That(who.Invoke1(c), Is.EqualTo("car"));
        Assert.That(who.Invoke1(m), Is.SameAs(RT.False));
    }

    [Test]
    public void SingleArgumentCondition()
    {
        var m = Make(s_messageRtd, "x");
        Assert.That(Conditions.MakeCondition(m), Is.SameAs(m));
        Assert.That(Conditions.MakeCondition("not-a-condition"), Is.Null);     // 与旧系统一致
    }

    [Test]
    public void NonConditionRtdRejected()
    {
        var plain = (RecordTypeDescriptor)Records.MakeRecordTypeDescriptor(Symbol.Intern("plain"), RT.False, RT.False, RT.False, RT.False, new object[0]);
        Assert.Throws<SchemeRuntimeError>(() => Conditions.ConditionPredicate(plain));
    }
}
