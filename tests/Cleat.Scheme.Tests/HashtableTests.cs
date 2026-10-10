using System.Collections;
using System.Linq;
using Cleat.Scheme;
using NUnit.Framework;

namespace Cleat.Scheme.Tests;

public class HashtableTests
{
    // (make-hashtable string-hash string=?) 的效果：按字符串内容比较
    private static HashtableEx StringTable() => new HashtableEx(new HashComparer(
      new Fn(a => a[0].ToString().GetHashCode()),
      new Fn(a => RT.Bool(a[0].ToString() == a[1].ToString()))));

    [Test]
    public void CustomHashAndEquivalence()
    {
        var ht = StringTable();
        ht[new System.Text.StringBuilder("k")] = 1;
        Assert.That(ht["k"], Is.EqualTo(1));          // 可变与不可变字符串按内容相等
        Assert.That(ht.HashFunction, Is.InstanceOf<Procedure>());
        Assert.That(ht.EqualityFunction, Is.InstanceOf<Procedure>());
    }

    [Test]
    public void NullKeyMapping()
    {
        var ht = new Hashtable();
        ht[Hashtables.ToNull(null)] = "empty-list";        // '() 作为键
        Assert.That(Hashtables.FromNull(Hashtables.ToNull(null)), Is.Null);
        var entries = ((MultipleValues)Hashtables.HashtableEntries(ht)).ToArray(2);
        Assert.That((object[])entries[0], Is.EqualTo(new object[] { null }));
        Assert.That((object[])entries[1], Is.EqualTo(new object[] { "empty-list" }));
    }

    [Test]
    public void MapAndForEach()
    {
        var ht = new Hashtable { [1] = 10, [2] = 20 };
        var sums = ((Cons)Hashtables.HashtableMap(ht, new Fn(a => (int)a[0] + (int)a[1]))).ToArray();   // 返回列表
        Assert.That(sums, Is.EquivalentTo(new object[] { 11, 22 }));
        int total = 0;
        Hashtables.HashtableForEach(ht, new Fn(a => { total += (int)a[1]; return null; }));
        Assert.That(total, Is.EqualTo(30));
    }

    [Test]
    public void ReadOnlyCopyRejectsMutation()
    {
        var ht = StringTable();
        ht["a"] = 1;
        var ro = ht.MakeReadOnly();
        Assert.That(ro["a"], Is.EqualTo(1));
        Assert.Throws<SchemeRuntimeError>(() => ro["b"] = 2);
        Assert.Throws<SchemeRuntimeError>(() => ro.Clear());
    }

    [Test]
    public void CloneKeepsComparer()
    {
        var ht = StringTable();
        ht["a"] = 1;
        var c = (HashtableEx)ht.Clone();
        Assert.That(c[new System.Text.StringBuilder("a")], Is.EqualTo(1));
    }
}
