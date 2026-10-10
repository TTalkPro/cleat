using System;
using System.IO;
using System.Threading;
using clojure.lang;
using NUnit.Framework;

namespace Cleat.Core.Tests;

// cleat-007 / cleat-008 / cleat-009：Var 与引用类型、函数模型、reader
public class VarTests
{
    [Test]
    public void RootAndThreadBindings()
    {
        var ns = Namespace.findOrCreate(Symbol.intern("cleat.test.vars"));
        var v = Var.intern(ns, Symbol.intern("x"), 1L);
        Assert.That(v.deref(), Is.EqualTo(1L));
        Assert.That(v.getRawRoot(), Is.EqualTo(1L));
        v.setDynamic();
        object seenInOtherThread = null;
        Var.pushThreadBindings(RT.map(v, 2L));
        try
        {
            Assert.That(v.deref(), Is.EqualTo(2L));
            Assert.That(v.getRawRoot(), Is.EqualTo(1L), "root unchanged by binding");
            var t = new Thread(() => seenInOtherThread = v.deref());
            t.Start(); t.Join();
            Assert.That(seenInOtherThread, Is.EqualTo(1L), "bindings are per thread (ThreadStatic), not inherited");
            v.set(3L);
            Assert.That(v.deref(), Is.EqualTo(3L));
        }
        finally { Var.popThreadBindings(); }
        Assert.That(v.deref(), Is.EqualTo(1L));
        v.alterRoot(new Fn1(x => (long)x + 10), null);
        Assert.That(v.deref(), Is.EqualTo(11L));
        Assert.That(v.isBound);
        Assert.That(ns.FindInternedVar(Symbol.intern("x")), Is.SameAs(v));
    }

    [Test]
    public void SetWithoutBindingFails()
    {
        var v = Var.intern(Namespace.findOrCreate(Symbol.intern("cleat.test.vars")), Symbol.intern("y"), 0L).setDynamic();
        Assert.Throws<InvalidOperationException>(() => v.set(1L));
    }

    [Test]
    public void MacroAndMetaFlags()
    {
        var v = Var.intern(Namespace.findOrCreate(Symbol.intern("cleat.test.vars")), Symbol.intern("m"), 0L);
        Assert.That(v.IsMacro, Is.False);
        v.setMacro();
        Assert.That(v.IsMacro, Is.True);
        Assert.That(RT.booleanCast(RT.get(v.meta(), Keyword.intern("macro"))), Is.True);
    }
}

public class FunctionModelTests
{
    private sealed class Add : AFunction
    {
        public override object invoke(object a, object b) => Numbers.add(a, b);
    }

    private sealed class Rest2 : RestFn
    {
        public override int getRequiredArity() => 2;
        protected override object doInvoke(object a, object b, object rest) => RT.list(a, b, rest);
    }

    [Test]
    public void FixedArityErrorsNameTheFunction()
    {
        var f = new Add();
        Assert.That(f.invoke(1L, 2L), Is.EqualTo(3L));
        var e = Assert.Throws<ArityException>(() => f.invoke(1L));
        Assert.That(e.Message, Does.StartWith("Wrong number of args (1) passed to: "));
        Assert.That(e.Actual, Is.EqualTo(1));
        Assert.That(f.applyTo(RT.list(1L, 2L)), Is.EqualTo(3L));
        Assert.Throws<ArityException>(() => f.applyTo(RT.list(1L, 2L, 3L)));
    }

    [Test]
    public void RestFnPacksExtraArgs()
    {
        var f = new Rest2();
        Assert.That(RT.printString(f.invoke(1L, 2L)), Is.EqualTo("(1 2 nil)"));
        Assert.That(RT.printString(f.invoke(1L, 2L, 3L, 4L)), Is.EqualTo("(1 2 (3 4))"));
        Assert.That(RT.printString(f.applyTo(RT.list(1L, 2L, 3L))), Is.EqualTo("(1 2 (3))"));
        Assert.Throws<ArityException>(() => f.invoke(1L));
        Assert.That(((IFn)f).invoke(1L, 2L, 3L, 4L, 5L, 6L, 7L, 8L, 9L, 10L, 11L, 12L, 13L, 14L, 15L, 16L, 17L, 18L, 19L, 20L, 21L, 22L), Is.Not.Null, "beyond 20 args goes through the params overload");
    }

    [Test]
    public void KeywordsAndCollectionsAreFunctions()
    {
        var m = RT.map(Keyword.intern("a"), 1L);
        Assert.That(Keyword.intern("a").invoke(m), Is.EqualTo(1L));
        Assert.That(Keyword.intern("b").invoke(m, "dflt"), Is.EqualTo("dflt"));
        Assert.That(((IFn)m).invoke(Keyword.intern("a")), Is.EqualTo(1L));
        Assert.That(((IFn)RT.vector(10L, 20L)).invoke(1L), Is.EqualTo(20L));
        Assert.That(((IFn)PersistentHashSet.create(1L)).invoke(1L), Is.EqualTo(1L));
    }
}

public class ReaderTests
{
    private static object Read(string s)
    {
        var opts = RT.map(Keyword.intern("read-cond"), Keyword.intern("allow"));
        return LispReader.read(new LineNumberingTextReader(new StringReader(s)), true, null, false, opts);
    }

    private static string Print(string s) => RT.printString(Read(s));

    [SetUp]
    public void SetUp()
    {
        var ns = Namespace.findOrCreate(Symbol.intern("cleat.test.reader"));
        Var.intern(ns, Symbol.intern("f"), 0L);
        Var.pushThreadBindings(RT.map(RT.CurrentNSVar, ns));
    }

    [TearDown]
    public void TearDown() => Var.popThreadBindings();

    [Test]
    public void LiteralsRoundTrip()
    {
        Assert.That(Print("[1 2.0 3N 4M 1/2 \"s\" \\a :k ::l ns/sym #{1} {:a 1} (1 . 2)]"), Is.EqualTo("[1 2.0 3N 4M 1/2 \"s\" \\a :k :cleat.test.reader/l ns/sym #{1} {:a 1} (1 . 2)]"));
        Assert.That(Print("#\"a+b\""), Is.EqualTo("#\"a+b\""));
        Assert.That(Print("'q"), Is.EqualTo("(quote q)"));
        Assert.That(Print("@x"), Is.EqualTo("(clojure.core/deref x)"));
        Assert.That(Print("#(+ % %2)"), Does.Match(@"\(fn\* \[p1__\d+# p2__\d+#\] \(\+ p1__\d+# p2__\d+#\)\)"));
        Assert.That(Print("[1 #_2 3]"), Is.EqualTo("[1 3]"));
        Assert.That(Print("true"), Is.EqualTo("true"));
    }

    [Test]
    public void SyntaxQuoteResolvesAgainstCurrentNamespace()
    {
        // f 是当前命名空间的 Var；g 未定义也按当前命名空间限定；String. 保持；.m 保持；特殊形式不限定；x# 自动 gensym
        Assert.That(Print("`(f g (.m x) (if a b) x#)"),
            Does.Match(@"\(clojure\.core/seq \(clojure\.core/concat \(clojure\.core/list \(quote cleat\.test\.reader/f\)\) \(clojure\.core/list \(quote cleat\.test\.reader/g\)\) \(clojure\.core/list \(clojure\.core/seq \(clojure\.core/concat \(clojure\.core/list \(quote \.m\)\) \(clojure\.core/list \(quote cleat\.test\.reader/x\)\)\)\)\) \(clojure\.core/list \(clojure\.core/seq \(clojure\.core/concat \(clojure\.core/list \(quote if\)\) \(clojure\.core/list \(quote cleat\.test\.reader/a\)\) \(clojure\.core/list \(quote cleat\.test\.reader/b\)\)\)\)\) \(clojure\.core/list \(quote x__\d+__auto__\)\)\)\)"));
        Assert.That(Print("`~x"), Is.EqualTo("x"));
        Assert.That(Print("`(a ~@b)"), Is.EqualTo("(clojure.core/seq (clojure.core/concat (clojure.core/list (quote cleat.test.reader/a)) b))"));
    }

    [Test]
    public void ReaderConditionalsSeeCleatFeatures()
    {
        Assert.That(Read("#?(:clj 1 :cleat 2 :default 3)"), Is.EqualTo(2L));
        Assert.That(Read("#?(:cljr 1 :default 3)"), Is.EqualTo(1L));
        Assert.That(Read("#?(:cleat-clr 1 :default 3)"), Is.EqualTo(1L));
        Assert.That(Read("#?(:cleat-llvm 1 :default 3)"), Is.EqualTo(3L));
        Assert.That(Print("[1 #?@(:cleat [2 3] :default [9]) 4]"), Is.EqualTo("[1 2 3 4]"));
    }

    [Test]
    public void SourcePositionsAndMetadata()
    {
        var form = (IObj)Read("\n  (a b)");
        Assert.That(RT.get(form.meta(), RT.LineKey), Is.EqualTo(2));
        Assert.That(RT.get(form.meta(), RT.ColumnKey), Is.EqualTo(3));
        var tagged = (IObj)Read("^String s");
        Assert.That(RT.get(tagged.meta(), RT.TagKey), Is.EqualTo(Symbol.intern("String")));
        var kw = (IObj)Read("^:dynamic x");
        Assert.That(RT.get(kw.meta(), Keyword.intern("dynamic")), Is.EqualTo(true));
    }

    [Test]
    public void ReadEvalIsGated()
    {
        Var.pushThreadBindings(RT.map(RT.ReadEvalVar, false));
        try
        {
            var e = Assert.Throws<LispReader.ReaderException>(() => Read("#=(+ 1 2)"));
            Assert.That(e.InnerException, Is.InstanceOf<InvalidOperationException>());
        }
        finally { Var.popThreadBindings(); }
    }

    [Test]
    public void EdnReaderBasics()
    {
        Assert.That(RT.printString(EdnReader.readString("{:a [1 2] :b #{x}}", PersistentHashMap.EMPTY)), Is.EqualTo("{:a [1 2], :b #{x}}"));
    }
}

internal sealed class Fn1 : AFn
{
    private readonly Func<object, object> _f;
    public Fn1(Func<object, object> f) { _f = f; }
    public override object invoke(object a) => _f(a);
}
