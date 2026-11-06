using System;
using System.IO;
using System.Collections.Generic;
using System.Threading;
using clojure.lang;
using Cleat.Core.Runtime;
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

    // cleat-088：每个必需个数 × 每个实参个数，多出的实参都打包成剩余序列（10 / 20 个实参的重载曾把第 10 个实参原样传入、漏掉 13 ~ 15）
    [Test]
    public void RestFnPacksEveryArityCombination()
    {
        RestFn[] fns = [new RestReq0(), new RestReq1(), new RestReq2(), new RestReq3(), new RestReq4(), new RestReq5(), new RestReq6(), new RestReq7(), new RestReq8(), new RestReq9(), new RestReq10(), new RestReq11(), new RestReq12(), new RestReq13(), new RestReq14(), new RestReq15(), new RestReq16(), new RestReq17(), new RestReq18(), new RestReq19(), new RestReq20()];
        for (int r = 0; r <= 20; r++)
            for (int n = r; n <= 20; n++)
            {
                var args = new object[n];
                for (int k = 0; k < n; k++) args[k] = (long)(k + 1);
                var invoke = typeof(IFn).GetMethod("invoke", Array.ConvertAll(args, _ => typeof(object)));
                var got = RT.printString(invoke.Invoke(fns[r], args));
                var fixedPart = string.Join(" ", Array.ConvertAll(args[..r], a => a.ToString()));
                var restPart = n == r ? "nil" : "(" + string.Join(" ", Array.ConvertAll(args[r..], a => a.ToString())) + ")";
                Assert.That(got, Is.EqualTo("[" + (r == 0 ? "" : fixedPart + " ") + restPart + "]"), $"required {r}, {n} args");
            }
    }

    private sealed class RestReq0 : RestFn
    {
        public override int getRequiredArity() => 0;
        protected override object doInvoke(object rest) => RT.vector(rest);
    }
    private sealed class RestReq1 : RestFn
    {
        public override int getRequiredArity() => 1;
        protected override object doInvoke(object a1, object rest) => RT.vector(a1, rest);
    }
    private sealed class RestReq2 : RestFn
    {
        public override int getRequiredArity() => 2;
        protected override object doInvoke(object a1, object a2, object rest) => RT.vector(a1, a2, rest);
    }
    private sealed class RestReq3 : RestFn
    {
        public override int getRequiredArity() => 3;
        protected override object doInvoke(object a1, object a2, object a3, object rest) => RT.vector(a1, a2, a3, rest);
    }
    private sealed class RestReq4 : RestFn
    {
        public override int getRequiredArity() => 4;
        protected override object doInvoke(object a1, object a2, object a3, object a4, object rest) => RT.vector(a1, a2, a3, a4, rest);
    }
    private sealed class RestReq5 : RestFn
    {
        public override int getRequiredArity() => 5;
        protected override object doInvoke(object a1, object a2, object a3, object a4, object a5, object rest) => RT.vector(a1, a2, a3, a4, a5, rest);
    }
    private sealed class RestReq6 : RestFn
    {
        public override int getRequiredArity() => 6;
        protected override object doInvoke(object a1, object a2, object a3, object a4, object a5, object a6, object rest) => RT.vector(a1, a2, a3, a4, a5, a6, rest);
    }
    private sealed class RestReq7 : RestFn
    {
        public override int getRequiredArity() => 7;
        protected override object doInvoke(object a1, object a2, object a3, object a4, object a5, object a6, object a7, object rest) => RT.vector(a1, a2, a3, a4, a5, a6, a7, rest);
    }
    private sealed class RestReq8 : RestFn
    {
        public override int getRequiredArity() => 8;
        protected override object doInvoke(object a1, object a2, object a3, object a4, object a5, object a6, object a7, object a8, object rest) => RT.vector(a1, a2, a3, a4, a5, a6, a7, a8, rest);
    }
    private sealed class RestReq9 : RestFn
    {
        public override int getRequiredArity() => 9;
        protected override object doInvoke(object a1, object a2, object a3, object a4, object a5, object a6, object a7, object a8, object a9, object rest) => RT.vector(a1, a2, a3, a4, a5, a6, a7, a8, a9, rest);
    }
    private sealed class RestReq10 : RestFn
    {
        public override int getRequiredArity() => 10;
        protected override object doInvoke(object a1, object a2, object a3, object a4, object a5, object a6, object a7, object a8, object a9, object a10, object rest) => RT.vector(a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, rest);
    }
    private sealed class RestReq11 : RestFn
    {
        public override int getRequiredArity() => 11;
        protected override object doInvoke(object a1, object a2, object a3, object a4, object a5, object a6, object a7, object a8, object a9, object a10, object a11, object rest) => RT.vector(a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11, rest);
    }
    private sealed class RestReq12 : RestFn
    {
        public override int getRequiredArity() => 12;
        protected override object doInvoke(object a1, object a2, object a3, object a4, object a5, object a6, object a7, object a8, object a9, object a10, object a11, object a12, object rest) => RT.vector(a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11, a12, rest);
    }
    private sealed class RestReq13 : RestFn
    {
        public override int getRequiredArity() => 13;
        protected override object doInvoke(object a1, object a2, object a3, object a4, object a5, object a6, object a7, object a8, object a9, object a10, object a11, object a12, object a13, object rest) => RT.vector(a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11, a12, a13, rest);
    }
    private sealed class RestReq14 : RestFn
    {
        public override int getRequiredArity() => 14;
        protected override object doInvoke(object a1, object a2, object a3, object a4, object a5, object a6, object a7, object a8, object a9, object a10, object a11, object a12, object a13, object a14, object rest) => RT.vector(a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11, a12, a13, a14, rest);
    }
    private sealed class RestReq15 : RestFn
    {
        public override int getRequiredArity() => 15;
        protected override object doInvoke(object a1, object a2, object a3, object a4, object a5, object a6, object a7, object a8, object a9, object a10, object a11, object a12, object a13, object a14, object a15, object rest) => RT.vector(a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11, a12, a13, a14, a15, rest);
    }
    private sealed class RestReq16 : RestFn
    {
        public override int getRequiredArity() => 16;
        protected override object doInvoke(object a1, object a2, object a3, object a4, object a5, object a6, object a7, object a8, object a9, object a10, object a11, object a12, object a13, object a14, object a15, object a16, object rest) => RT.vector(a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11, a12, a13, a14, a15, a16, rest);
    }
    private sealed class RestReq17 : RestFn
    {
        public override int getRequiredArity() => 17;
        protected override object doInvoke(object a1, object a2, object a3, object a4, object a5, object a6, object a7, object a8, object a9, object a10, object a11, object a12, object a13, object a14, object a15, object a16, object a17, object rest) => RT.vector(a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11, a12, a13, a14, a15, a16, a17, rest);
    }
    private sealed class RestReq18 : RestFn
    {
        public override int getRequiredArity() => 18;
        protected override object doInvoke(object a1, object a2, object a3, object a4, object a5, object a6, object a7, object a8, object a9, object a10, object a11, object a12, object a13, object a14, object a15, object a16, object a17, object a18, object rest) => RT.vector(a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11, a12, a13, a14, a15, a16, a17, a18, rest);
    }
    private sealed class RestReq19 : RestFn
    {
        public override int getRequiredArity() => 19;
        protected override object doInvoke(object a1, object a2, object a3, object a4, object a5, object a6, object a7, object a8, object a9, object a10, object a11, object a12, object a13, object a14, object a15, object a16, object a17, object a18, object a19, object rest) => RT.vector(a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11, a12, a13, a14, a15, a16, a17, a18, a19, rest);
    }
    private sealed class RestReq20 : RestFn
    {
        public override int getRequiredArity() => 20;
        protected override object doInvoke(object a1, object a2, object a3, object a4, object a5, object a6, object a7, object a8, object a9, object a10, object a11, object a12, object a13, object a14, object a15, object a16, object a17, object a18, object a19, object a20, object rest) => RT.vector(a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11, a12, a13, a14, a15, a16, a17, a18, a19, a20, rest);
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

// cleat-121：nth 字符分支用 JVM Character cache 的范围，避免反复读同几个 ASCII 字符时重复装箱
public class CharBoxTests
{
    [Test]
    public void CachedAsciiCharsFromNthAreIdentical()
    {
        Assert.That(RT.nth("abc", 0), Is.SameAs(RT.nth("azz", 0)));
        Assert.That(RT.nth("abc", 1), Is.SameAs(CharBox.Get('b')));
        Assert.That(RT.nth("abc", 2, 'x'), Is.EqualTo('c'));
        Assert.That(RT.nth("abc", 3, 'x'), Is.EqualTo('x'));
        Assert.That(CharBox.Get('ā'), Is.EqualTo('ā'));
    }
}
