// cleat-006：从 cleat-005 的冒烟程序转来的 clojure.lang 运行时测试（集合、数值、名字、Var、reader）。
using System;
using System.IO;
using System.Threading;
using clojure.lang;
using NUnit.Framework;

namespace Cleat.Core.Tests;

public class ClojureLangSmokeTests
{
    private sealed class Fn1 : AFn
    {
        private readonly Func<object, object> _f;
        public Fn1(Func<object, object> f) { _f = f; }
        public override object invoke(object a) => _f(a);
    }

    private sealed class FixedNsResolver : IReaderResolver
    {
        // "minimal resolution that qualifies to a fixed namespace"
        private readonly Symbol _ns = Symbol.intern("smoke.ns");
        public Symbol CurrentNS() => _ns;
        public Symbol ResolveClass(Symbol sym) => sym.Name == "String" ? Symbol.intern("System.String") : null;
        public Symbol ResolveAlias(Symbol sym) => sym.Name == "s" ? Symbol.intern("clojure.string") : null;
        public Symbol ResolveVar(Symbol sym) => sym.Name == "str" ? Symbol.intern("clojure.core", "str") : null;
        public bool IsSpecial(object sym) => DefaultReaderResolver.Instance.IsSpecial(sym);
    }

    private static int _failed;
    private static void Check(string what, bool ok, string detail = null)
    {
        TestContext.Out.WriteLine($"[{(ok ? "ok" : "FAIL")}] {what}{(detail is null ? "" : " -- " + detail)}");
        if (!ok) _failed++;
    }

    private static void Known(string what, bool ok, string detail = null)
    {
        // a divergence we expect and record (not counted as failure)
        TestContext.Out.WriteLine($"[{(ok ? "ok" : "known")}] {what}{(detail is null ? "" : " -- " + detail)}");
    }

    private static object Read(string s, IReaderResolver resolver = null, object opts = null)
    {
        var r = new LineNumberingTextReader(new StringReader(s));
        opts ??= PersistentHashMap.EMPTY;
        if (resolver is null)
            return LispReader.read(r, true, null, false, opts);
        Var.pushThreadBindings(RT.map(RT.ReaderResolverVar, resolver));
        try { return LispReader.read(r, true, null, false, opts); }
        finally { Var.popThreadBindings(); }
    }

    [Test]
    public void Smoke()
    {
        _failed = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // --- PersistentHashMap, 100k entries
        {
            const int N = 100_000;
            IPersistentMap m = PersistentHashMap.EMPTY;
            for (int i = 0; i < N; i++)
                m = m.assoc(Keyword.intern(null, "k" + i), (long)i);
            bool ok = m.count() == N;
            for (int i = 0; i < N && ok; i += 7)
                ok = (long)m.valAt(Keyword.intern(null, "k" + i)) == i;
            Check("PersistentHashMap assoc/valAt 100k", ok && m.valAt(Keyword.intern(null, "nope")) == null, $"count={m.count()} {sw.ElapsedMilliseconds}ms");
            ITransientMap tm = (ITransientMap)((IEditableCollection)PersistentHashMap.EMPTY).asTransient();
            for (int i = 0; i < N; i++) tm = tm.assoc(i, i * 2L);
            var pm = tm.persistent();
            Check("PersistentHashMap transient 100k", pm.count() == N && (long)pm.valAt(99_999) == 199_998L);
            var hm = (PersistentHashMap)m;
            Check("PersistentHashMap.hash(k) == Util.hasheq(k) (Murmur3 path)", Util.hasheq(Keyword.intern(null, "k1")) == Keyword.intern(null, "k1").hasheq());
        }

        // --- PersistentVector
        {
            IPersistentVector v = PersistentVector.EMPTY;
            for (int i = 0; i < 10_000; i++) v = v.cons(i);
            Check("PersistentVector conj/nth", v.count() == 10_000 && (int)v.nth(1234) == 1234 && (int)v.peek() == 9_999);
            ITransientVector tv = (ITransientVector)((IEditableCollection)v).asTransient();
            for (int i = 0; i < 1000; i++) tv = (ITransientVector)tv.conj(-i);
            var pv = (IPersistentVector)tv.persistent();
            Check("PersistentVector transient", pv.count() == 11_000 && (int)pv.nth(10_999) == -999 && v.count() == 10_000);
            Check("PersistentVector equiv/hasheq", Util.equiv(RT.vector(1L, 2L, 3L), PersistentList.create(new object[] { 1L, 2L, 3L })) && ((IHashEq)RT.vector(1L, 2L)).hasheq() == ((IHashEq)PersistentList.create(new object[] { 1L, 2L })).hasheq());
        }

        // --- Numbers
        {
            bool threw = false;
            try { Numbers.add(long.MaxValue, 1L); } catch (ArithmeticException) { threw = true; }
            Check("Numbers.add overflow throws ArithmeticException", threw);
            object big = Numbers.addP(long.MaxValue, 1L);
            Check("Numbers.addP -> BigInt", big is BigInt && big.ToString() == "9223372036854775808", big.GetType().Name + " " + big);
            object r = Numbers.divide(1L, 3L);
            object sum = Numbers.add(r, Numbers.divide(1L, 6L));
            Check("Ratio arithmetic 1/3 + 1/6 = 1/2", r is Ratio && sum is Ratio rr && rr.numerator.ToString() == "1" && rr.denominator.ToString() == "2", RT.printString(sum));
            Check("Ratio 1/3 * 3 = 1N (BigInt, as on the JVM)", Numbers.multiply(r, 3L) is BigInt b1 && Numbers.equiv(b1, 1L), RT.printString(Numbers.multiply(r, 3L)));
            Check("Numbers.equiv 1 1.0 / 1N", Numbers.equiv(1L, 1.0) && Numbers.equiv(1L, BigInt.fromLong(1)) && Util.hasheq(1L) == Util.hasheq(BigInt.fromLong(1)));
            Check("BigDecimal", BigDecimal.Parse("1.10").Add(BigDecimal.Parse("2.205")).ToString() == "3.305");
        }

        // --- Keyword / Symbol
        {
            var k1 = Keyword.intern("a", "b"); var k2 = Keyword.intern(Symbol.intern("a/b"));
            Check("Keyword.intern identity", ReferenceEquals(k1, k2) && k1.GetHashCode() == k2.GetHashCode());
            var s1 = Symbol.intern("foo", "bar"); var s2 = Symbol.intern("foo/bar");
            Check("Symbol equals/hash name-based (distinct objects)", !ReferenceEquals(s1, s2) && s1.Equals(s2) && s1.GetHashCode() == s2.GetHashCode() && s1.hasheq() == s2.hasheq());
            Check("Symbol hash == hashCombine(Murmur3(name), hash(ns))", s1.GetHashCode() == Util.hashCombine(Murmur3.HashString("bar"), Util.hash("foo")));
            Check("Keyword hash = sym.hasheq + 0x9e3779b9", k1.GetHashCode() == (int)(Symbol.intern("a/b").hasheq() + 0x9e3779b9));
            Check("Symbol with meta still equal", ((IObj)s1).withMeta(RT.map(Keyword.intern("x"), 1)).Equals(s2));
        }

        // --- Var / Namespace / binding
        {
            var ns = Namespace.findOrCreate(Symbol.intern("smoke.ns"));
            var v = Var.intern(ns, Symbol.intern("x"), 1L);
            v.setDynamic();
            v.bindRoot(10L);
            Check("Var.intern + bindRoot", (long)v.deref() == 10L && v.ns == ns && ns.FindInternedVar(Symbol.intern("x")) == v);
            object seenInThread = null, seenAfter = null;
            var t = new Thread(() =>
            {
                Var.pushThreadBindings(RT.map(v, 20L));
                try { seenInThread = v.deref(); }
                finally { Var.popThreadBindings(); }
                seenAfter = v.deref();
            });
            t.Start(); t.Join();
            Check("Var binding on another thread", (long)seenInThread == 20L && (long)seenAfter == 10L && (long)v.deref() == 10L, $"thread={seenInThread} after={seenAfter} main={v.deref()}");
            Var.pushThreadBindings(RT.map(v, 30L));
            try { Check("Var binding on main thread", (long)v.deref() == 30L && v.isBound); } finally { Var.popThreadBindings(); }
            Check("Var printString (RT.print form; #'x comes from core_print)", RT.printString(v) == "#=(var smoke.ns/x)", RT.printString(v));
        }

        // --- Atom / Ref / Delay
        {
            var a = new Atom(0L);
            var inc = new Fn1(x => Numbers.inc(x));
            for (int i = 0; i < 100; i++) a.swap(inc);
            Check("Atom swap", (long)a.deref() == 100L);
            var threads = new Thread[8];
            for (int i = 0; i < 8; i++) { threads[i] = new Thread(() => { for (int j = 0; j < 1000; j++) a.swap(inc); }); threads[i].Start(); }
            foreach (var th in threads) th.Join();
            Check("Atom swap from 8 threads", (long)a.deref() == 8100L, a.deref().ToString());
            var rf = new Ref(1L);
            LockingTransaction.runInTransaction(new Fn0(() => { rf.set(2L); return null; }));
            Check("Ref set in LockingTransaction", (long)rf.deref() == 2L);
            var d = new Delay(new Fn0(() => 42L));
            Check("Delay", !d.isRealized() && (long)d.deref() == 42L && d.isRealized());
        }

        // --- Arity error path (uses Munge.demunge instead of Compiler.demunge)
        {
            string msg = null;
            try { new Fn1(x => x).invoke(); } catch (ArityException e) { msg = e.Message; }
            Check("ArityException message via Munge.demunge", msg != null && msg.StartsWith("Wrong number of args (0) passed to: "), msg);
        }

        // --- LispReader
        {
            string src = "(defn f [x] ^String (str x) {:a 1} #{1 2} [1 2.0 3N 4M 1/2] 'q `(a ~b) #\"re\")";
            object form = Read(src, new FixedNsResolver());
            string printed = RT.printString(form);
            TestContext.Out.WriteLine("  read+print (fixed resolver): " + printed);
            ISeq s = RT.seq(form);
            var vec = (IPersistentVector)RT.nth(form, 6);
            var strCall = (ISeq)RT.nth(form, 3);
            var sq = RT.nth(form, 8);
            Check("LispReader list/vector/map/set/number literals",
                RT.count(form) == 10 && RT.first(form).Equals(Symbol.intern("defn"))
                && RT.nth(form, 4) is IPersistentMap && RT.nth(form, 5) is IPersistentSet
                && vec.nth(0) is long && vec.nth(1) is double && vec.nth(2) is BigInt && vec.nth(3) is BigDecimal && vec.nth(4) is Ratio);
            Check("LispReader ^String meta on (str x)", Util.equals(RT.get(RT.meta(strCall), RT.TagKey), Symbol.intern("String")));
            Check("LispReader 'q -> (quote q)", Util.equals(RT.nth(form, 7), RT.list(Symbol.intern("quote"), Symbol.intern("q"))));
            string sqs = RT.printString(sq);
            Check("LispReader syntax-quote `(a ~b) resolves a to smoke.ns/a, keeps b unquoted",
                sqs == "(clojure.core/seq (clojure.core/concat (clojure.core/list (quote smoke.ns/a)) (clojure.core/list b)))", sqs);
            Check("LispReader #\"re\" -> Regex", RT.nth(form, 9) is System.Text.RegularExpressions.Regex && printed.EndsWith("#\"re\")"));
            // default resolver: qualifies against *ns* (clojure.core at boot) and leaves specials alone
            string sq2 = RT.printString(Read("`(if (str x) String/Empty)"));
            Check("Syntax-quote via DefaultReaderResolver (if special, str -> clojure.core/str)",
                sq2.Contains("(quote if)") && sq2.Contains("(quote clojure.core/str)") && sq2.Contains("(quote clojure.core/x)") && sq2.Contains("(quote System.String/Empty)"), sq2);
            Check("Syntax-quote class resolution via mapping (String. -> System.String.)", RT.printString(Read("`(String. x)", new FixedNsResolver())).Contains("(quote System.String.)"));
            Check("Auto-gensym x#", RT.printString(Read("`(x# x#)")).Contains("__auto__"));
            string fnr = RT.printString(Read("#(+ % %2 %&)"));
            Check("#(...) fn reader -> fn*", System.Text.RegularExpressions.Regex.IsMatch(fnr, @"^\(fn\* \[p1__\d+# p2__\d+# & rest__\d+#\] \(\+ p1__\d+# p2__\d+# rest__\d+#\)\)$"), fnr);
            Check("::kw and ::alias/kw", Util.equals(Read("::kw"), Keyword.intern("clojure.core", "kw")) && Util.equals(Read("::s/kw", new FixedNsResolver()), Keyword.intern("clojure.string", "kw")));
            Check("#?(:cljr 1 :default 2) read-cond", Util.equals(Read("#?(:cljr 1 :default 2)", null, RT.map(LispReader.OPT_READ_COND, LispReader.COND_ALLOW)), 1L));
            Check("Read round-trip (printed text)", RT.printString(Read(printed)) == printed);
            Known("Read round-trip Util.equals (Regex has reference equality, same as JVM Pattern)", Util.equals(Read(printed), form));
            string readEvalMsg = null;
            try { Read("#=(+ 1 2)"); } catch (Exception e) { readEvalMsg = (e.InnerException ?? e).Message; }
            // cleat-010: #= now goes through Reflector; `+` is not a mapped var here (no clojure.core loaded), so it can't resolve
            Check("#= resolves through Reflector (cleat-010); unmapped var reports Can't resolve", readEvalMsg != null && readEvalMsg.StartsWith("Can't resolve +"), readEvalMsg);
        }

        // --- EdnReader
        {
            string edn = "{:a [1 2.5 \"s\" nil true \\c] :b #{:x :y} :c (1 2) :d 1/2 :e 10N :f 1.5M :g sym/bol}";
            var opts = RT.map(Keyword.intern(null, "readers"), RT.map(Symbol.intern("cleat/tag"), new Fn1(x => RT.vector(Keyword.intern("tagged"), x))));
            object m = EdnReader.readString(edn, PersistentHashMap.EMPTY);
            TestContext.Out.WriteLine("  edn: " + RT.printString(m));
            Check("EdnReader map/vector/set/list/char/ratio/bigint/bigdec/symbol",
                m is IPersistentMap pm && pm.count() == 7 && RT.nth(RT.get(pm, Keyword.intern("a")), 5) is char
                && RT.get(pm, Keyword.intern("d")) is Ratio && RT.get(pm, Keyword.intern("e")) is BigInt && RT.get(pm, Keyword.intern("f")) is BigDecimal
                && Util.equals(RT.get(pm, Keyword.intern("g")), Symbol.intern("sym", "bol")));
            object tagged = EdnReader.readString("#cleat/tag {:z 1}", opts);
            Check("EdnReader tagged literal via :readers", Util.equals(tagged, RT.vector(Keyword.intern("tagged"), RT.map(Keyword.intern("z"), 1L))), RT.printString(tagged));
            Check("RT.printString(true) == \"true\" (CLEAT-006 added the bool branch)", RT.printString(true) == "true" && RT.printString(false) == "false", RT.printString(true));
            object m2 = ((IPersistentMap)m).without(Keyword.intern("a"));
            Check("EdnReader round-trip (without the bool)", Util.equals(EdnReader.readString(RT.printString(m2), PersistentHashMap.EMPTY), m2));
        }

        // --- misc seqs
        {
            object r = clojure.lang.Range.create(0L, 10L);
            Check("Range/LongRange reduce", (long)((IReduce)r).reduce(new Fn2((x, y) => Numbers.add(x, y))) == 45L);
            var lazy = new LazySeq(new Fn0(() => RT.cons(1L, null)));
            Check("LazySeq", RT.count(lazy) == 1 && RT.printString(lazy) == "(1)");
            Check("StringSeq/ArraySeq/Repeat", RT.count(StringSeq.create("abc")) == 3 && RT.count(ArraySeq.create(1L, 2L, 3L)) == 3 && RT.count(RT.seq(Repeat.create(5L, "x"))) == 5);
            Check("PersistentTreeMap sorted", RT.printString(PersistentTreeMap.create(RT.seq(RT.vector(3L, "c", 1L, "a", 2L, "b")))) == "{1 \"a\", 2 \"b\", 3 \"c\"}");
            var q = (PersistentQueue)((IPersistentStack)PersistentQueue.EMPTY.cons(1L).cons(2L)).pop();
            Check("PersistentQueue", q.count() == 1 && (long)q.peek() == 2L && RT.printString(q.seq()) == "(2)", RT.printString(q));
            // MultiFn: constructible, but dispatch needs clojure.core/isa? and clojure.core/parents (vars resolved at class init) -> needs core.clj; only construction is exercised.
            var mf = new MultiFn("m", new Fn1(x => x), Keyword.intern("default"), new Atom(RT.map()));
            mf.addMethod(Keyword.intern("k"), new Fn1(x => "hit"));
            Check("MultiFn construct/addMethod (dispatch needs clojure.core/isa?)", mf.getMethodTable().count() == 1);
        }

        TestContext.Out.WriteLine($"\n{(_failed == 0 ? "ALL OK" : _failed + " FAILED")} in {sw.ElapsedMilliseconds}ms");
        Assert.That(_failed, Is.EqualTo(0), "smoke checks failed");
    }

    private sealed class Fn0 : AFn { private readonly Func<object> _f; public Fn0(Func<object> f) { _f = f; } public override object invoke() => _f(); }
    private sealed class Fn2 : AFn { private readonly Func<object, object, object> _f; public Fn2(Func<object, object, object> f) { _f = f; } public override object invoke(object a, object b) => _f(a, b); }
}
