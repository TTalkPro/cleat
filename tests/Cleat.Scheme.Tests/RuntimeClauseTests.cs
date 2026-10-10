using System.Linq;
using System.Reflection;
using Cleat.Emit;
using NUnit.Framework;

namespace Cleat.Scheme.Tests;

/// <summary>
/// cleat-045：Lcil 的 runtime 子句（LCIL.md §2、§5、§8）。互操作指令按方法所属的运行时决议（缺省程序集的，再缺省 clojure）；
/// 常量表的联合编码一张表里可以混用两边的标签，只有 str / sym / list / vec 按程序集的 runtime 解释。
/// </summary>
public class RuntimeClauseTests
{
    private static object[] Table(string constants, string runtime)
    {
        var items = new SExprReader("(" + constants + ")", "c").ReadAll();
        return Fasl.ReadTable(FaslEncoder.EncodeTable(((SList)items[0]).Items, "c", runtime));
    }

    [Test]
    public void MixedTableUnderSchemeRuntime()
    {
        var t = Table(@"(sym ""x"") (str ""s"") (list (fix 1) (fix 2)) (vec (fix 1)) (kw ""a"") (long 7) (map (kw ""k"") (sym ""v"")) (unspecified)", LcilRuntime.Scheme);
        Assert.That(t[0], Is.SameAs(Symbol.Intern("x")));
        Assert.That(t[1], Is.EqualTo("s"));
        Assert.That(t[2], Is.InstanceOf<Cons>());
        Assert.That(((Cons)t[2]).Count(), Is.EqualTo(2));
        Assert.That(t[3], Is.InstanceOf<object[]>());
        Assert.That(t[4], Is.SameAs(clojure.lang.Keyword.intern("a")), "Clojure 标签在 scheme 表里照样读回");
        Assert.That(t[5], Is.EqualTo(7L));
        Assert.That(((clojure.lang.IPersistentMap)t[6]).valAt(clojure.lang.Keyword.intern("k")), Is.SameAs(Symbol.Intern("v")), "map 的值按 scheme 解释 sym");
        Assert.That(t[7], Is.SameAs(Unspecified.Instance));
    }

    [Test]
    public void MixedTableUnderClojureRuntime()
    {
        var t = Table(@"(sym ""ns"" ""x"") (str ""s"") (list (long 1) (long 2)) (vec (long 1)) (fix 3) (pair (sym ""a"") (nil)) (chr 955)", LcilRuntime.Clojure);
        Assert.That(t[0], Is.EqualTo(clojure.lang.Symbol.intern("ns", "x")));
        Assert.That(t[1], Is.EqualTo("s"));
        Assert.That(t[2], Is.InstanceOf<clojure.lang.IPersistentList>());
        Assert.That(t[3], Is.InstanceOf<clojure.lang.IPersistentVector>());
        Assert.That(t[4], Is.EqualTo(3), "Scheme 标签在 clojure 表里照样读回");
        Assert.That(((Cons)t[5]).car, Is.EqualTo(clojure.lang.Symbol.intern("a")), "pair 是 Scheme 序对，car 按 clojure 解释 sym");
        Assert.That(t[6], Is.EqualTo('λ'));
        Assert.That(Assert.Throws<LcilException>(() => Table("(sym \"a\" \"b\")", LcilRuntime.Scheme)).Message, Does.Contain("takes 1 operand"), "scheme 的 sym 只有名字");
        Assert.That(Assert.Throws<LcilException>(() => Table("(nil)", "forth")).Message, Does.Contain("unknown runtime"));
    }

    private const string Lcil = @"
(lcil 1
  (assembly ""rt"" (references ""Cleat.Scheme""))
  (type ""R"" (attrs abstract sealed)
    (method ""MaxClojure"" (attrs static) (params ""System.Object"" ""System.Object"") (ret ""System.Object"")
      (clr-namespaces ""System"")
      (body (ldarg 0) (ldarg 1) (clr-call ""Math"" ""Max"" #t 2) (ret)))
    (method ""MaxScheme"" (attrs static) (params ""System.Object"" ""System.Object"") (ret ""System.Object"")
      (runtime scheme) (clr-namespaces ""System"")
      (body (ldarg 0) (ldarg 1) (clr-call ""Math"" ""Max"" #t 2) (ret)))
    (method ""SetClojure"" (attrs static) (params) (ret ""System.Object"")
      (body (ldc.i4 0) (box ""System.Boolean"") (clr-field-set ""Cleat.Scheme.ModuleLoader"" ""ShowImports"" #t) (ret)))
    (method ""SetScheme"" (attrs static) (params) (ret ""System.Object"")
      (runtime scheme)
      (body (ldc.i4 0) (box ""System.Boolean"") (clr-field-set ""Cleat.Scheme.ModuleLoader"" ""ShowImports"" #t) (ret)))))";

  /// <summary>同一程序集里按方法选运行时：Math.Max 的 int 结果在 clojure 下转成 long，在 scheme 下保持 fixnum；字段赋值的结果 nil / 未指定值。</summary>
    [Test]
    public void MethodLevelRuntimeSelectsInteropRules()
    {
        var asm = LcilEmitter.EmitInMemory(Lcil, "rt");
        object Call(string m, params object[] args) => asm.GetType("R").GetMethod(m).Invoke(null, args);
        Assert.That(Call("MaxClojure", 3L, 7L), Is.EqualTo(7L).And.TypeOf<long>());
        Assert.That(Call("MaxScheme", 3, 7), Is.EqualTo(7).And.TypeOf<int>());
        // 静态字段赋值（静态决议）：结果按各自的约定
        Assert.That(Call("SetClojure"), Is.Null);
        Assert.That(Call("SetScheme"), Is.SameAs(Unspecified.Instance));
        Assert.That(ModuleLoader.ShowImports, Is.False);
        Assert.That(asm.GetReferencedAssemblies(), Has.Some.Matches<AssemblyName>(n => n.Name == "Cleat.Scheme"));
    }
}
