using System;
using System.IO;
using Cleat.Core.Expand;
using clojure.lang;
using NUnit.Framework;

namespace Cleat.Core.Tests;

// cleat-013 / cleat-014：展开器 → Lcore
public class ExpanderTests
{
    private Expander _x;
    private Namespace _ns;

    private sealed class MacroFn : AFn
    {
        private readonly Func<object, object, ISeq, object> _f;
        public MacroFn(Func<object, object, ISeq, object> f) { _f = f; }
        public override object applyTo(ISeq args) => _f(RT.first(args), RT.second(args), RT.next(RT.next(args)));
        public override object invoke(object a) => _f(a, null, null);
        public override object invoke(object form, object env, object a) => _f(form, env, RT.list(a));
        public override object invoke(object form, object env, object a, object b) => _f(form, env, RT.list(a, b));
    }

    [SetUp]
    public void SetUp()
    {
        RT.Init();
        _ns = Namespace.findOrCreate(Symbol.intern("cleat.test.expander"));
        var core = Namespace.findOrCreate(Symbol.intern("clojure.core"));
        Var.intern(core, Symbol.intern("instance?"), new MacroFn((f, e, a) => null));
        Var.intern(core, Symbol.intern("identity"), new MacroFn((f, e, a) => null));
        Var.intern(core, Symbol.intern("with-meta"), new MacroFn((f, e, a) => null));
        Var.intern(_ns, Symbol.intern("f"), new MacroFn((f, e, a) => null));
        Var.intern(_ns, Symbol.intern("dyn"), 1L).setDynamic();
        Var.intern(_ns, Symbol.intern("when2"), new MacroFn((form, env, args) => RT.list(Symbol.intern("if"), RT.first(args), RT.second(args), null))).setMacro();
        Var.intern(_ns, Symbol.intern("env-keys"), new MacroFn((form, env, args) => RT.list(Symbol.intern("quote"), RT.keys(env)))).setMacro();
        Var.intern(_ns, Symbol.intern("plus"), new MacroFn((f, e, a) => null)).setMeta(RT.map(
            Keyword.intern("inline"), new MacroFn((f, e, a) => RT.listStar(Symbol.intern("System.Math/Max"), RT.list(f, e))),
            Keyword.intern("inline-arities"), new MacroFn((f, e, a) => (long)f == 2)));
        _ns.refer(Symbol.intern("instance?"), core.FindInternedVar(Symbol.intern("instance?")));
        _ns.refer(Symbol.intern("identity"), core.FindInternedVar(Symbol.intern("identity")));
        _ns.importClass(Symbol.intern("String"), typeof(string));
        _ns.importClass(Symbol.intern("StringBuilder"), typeof(System.Text.StringBuilder));
        Var.pushThreadBindings(RT.map(RT.CurrentNSVar, _ns));
        _x = new Expander { SourcePath = "t.clj" };
    }

    [TearDown]
    public void TearDown() => Var.popThreadBindings();

    private object Read(string s) => LispReader.read(new LineNumberingTextReader(new StringReader(s)), true, null, false, PersistentHashMap.EMPTY);

    private string X(string src) => RT.printString(_x.ExpandTopLevel(Read(src)));

    private string Fails(string src) => Assert.Throws<ExpanderException>(() => _x.ExpandTopLevel(Read(src))).Message;

    [Test]
    public void ConstantsAndCollections()
    {
        Assert.That(X("1"), Is.EqualTo("(const 1)"));
        Assert.That(X("nil"), Is.EqualTo("(const nil)"));
        Assert.That(X(":k"), Is.EqualTo("(const :k)"));
        Assert.That(X("\"s\""), Is.EqualTo("(const \"s\")"));
        Assert.That(X("'(a b)"), Is.EqualTo("(const (a b))"));
        Assert.That(X("[1 2]"), Is.EqualTo("(const [1 2])"));
        Assert.That(X("[1 f]"), Is.EqualTo("(vector (const 1) (var-ref cleat.test.expander/f {:dynamic false, :redef false}))"));
        Assert.That(X("{:a f}"), Is.EqualTo("(map (const :a) (var-ref cleat.test.expander/f {:dynamic false, :redef false}))"));
        Assert.That(X("#{f}"), Is.EqualTo("(set (var-ref cleat.test.expander/f {:dynamic false, :redef false}))"));
        Assert.That(X("^:foo [1]"), Is.EqualTo("(const [1])"));
        Assert.That(X("^{:a 1} [f]"), Is.EqualTo("(with-meta (vector (var-ref cleat.test.expander/f {:dynamic false, :redef false})) (const {:a 1}))"));
    }

    [Test]
    public void SymbolsAndVars()
    {
        Assert.That(X("f"), Is.EqualTo("(var-ref cleat.test.expander/f {:dynamic false, :redef false})"));
        Assert.That(X("dyn"), Is.EqualTo("(var-ref cleat.test.expander/dyn {:dynamic true, :redef false})"));
        Assert.That(X("#'f"), Is.EqualTo("(the-var cleat.test.expander/f)"));
        Assert.That(X("String"), Does.StartWith("(const #=|System.String,"));
        Assert.That(X("System.Int64"), Does.StartWith("(const #=|System.Int64,"));
        Assert.That(X("String/Empty"), Is.EqualTo("(host-call \"System.String\" \"Empty\" true nil ())"));
        Assert.That(Fails("nope"), Does.Contain("Unable to resolve symbol: nope in this context").And.Contain("t.clj:"));
        Assert.That(Fails("no.such/x"), Does.Contain("No such namespace: no.such"));
        Assert.That(Fails("when2"), Does.Contain("Can't take the value of a macro"));
    }

    [Test]
    public void ControlFormsAndLocals()
    {
        Assert.That(X("(if 1 2 3)"), Is.EqualTo("(if (const 1) (const 2) (const 3))"));
        Assert.That(X("(if 1 2)"), Is.EqualTo("(if (const 1) (const 2) (const nil))"));
        Assert.That(X("(do 1 2)"), Is.EqualTo("(do (const 1) (const 2))"));
        Assert.That(X("(do)"), Is.EqualTo("(const nil)"));
        Assert.That(X("(let* [x 1 y x] y)"), Is.EqualTo("(let [x__1 (const 1) y__2 (local x__1)] (local y__2))"));
        Assert.That(X("(loop* [i 0] (if i (recur 1) i))"), Is.EqualTo("(loop [i__1 (const 0)] (if (local i__1) (recur (const 1)) (local i__1)))"));
        Assert.That(X("(letfn* [g (fn* g [] (g))] (g))"), Is.EqualTo("(letfn [g__1 (fn g__2 false (() nil (call (local g__2) ())))] (call (local g__1) ()))"));
        Assert.That(X("(throw f)"), Is.EqualTo("(throw (var-ref cleat.test.expander/f {:dynamic false, :redef false}))"));
        Assert.That(X("(monitor-enter f)"), Is.EqualTo("(monitor-enter (var-ref cleat.test.expander/f {:dynamic false, :redef false}))"));
        Assert.That(Fails("(if 1)"), Does.Contain("Too few arguments to if"));
        Assert.That(Fails("(let* [x] x)"), Does.Contain("matched symbol/value pairs"));
        Assert.That(Fails("(loop* [i 0] (recur 1 2))"), Does.Contain("Mismatched argument count to recur, expected: 1 args, got: 2"));
        Assert.That(Fails("(loop* [i 0] (do (recur 1) 2))"), Does.Contain("Can only recur from tail position"));
        Assert.That(Fails("(loop* [i 0] (try (recur 1)))"), Does.Contain("Cannot recur across try"));
    }

    [Test]
    public void Functions()
    {
        Assert.That(X("(fn* [a b] a)"), Is.EqualTo("(fn nil false ((a__1 b__2) nil (local a__1)))"));
        Assert.That(X("(fn* ([a] a) ([a & r] r))"), Is.EqualTo("(fn nil false ((a__1) nil (local a__1)) ((a__2) r__3 (tag \"clojure.lang.ISeq\" (local r__3))))"));
        Assert.That(X("(fn* me [] me)"), Is.EqualTo("(fn me__1 false (() nil (local me__1)))"));
        Assert.That(X("(^:once fn* [] 1)"), Is.EqualTo("(fn nil true (() nil (const 1)))"));
        Assert.That(X("(fn* [a] (recur a))"), Is.EqualTo("(fn nil false ((a__1) nil (recur (local a__1))))"));
        Assert.That(Fails("(fn* ([a] a) ([b] b))"), Does.Contain("Can't have 2 overloads with the same arity"));
        Assert.That(Fails("(fn* ([a & r] a) ([b & s] b))"), Does.Contain("Can't have more than 1 variadic overload"));
        Assert.That(Fails("(fn* ([a b] a) ([b & s] b))"), Does.Contain("Can't have fixed arity methods with more params than the variadic method"));
        Assert.That(Fails("(fn* [a/b] a)"), Does.Contain("Can't use qualified name as parameter"));
        Assert.That(Fails("(fn* [a & ^String r] a)"), Does.Contain("& arg cannot have type hint"));
        Assert.That(Fails("(fn* [a a1 a2 a3 a4 a5 a6 a7 a8 a9 a10 a11 a12 a13 a14 a15 a16 a17 a18 a19 a20] a)"), Does.Contain("Can't specify more than 20 params"));
    }

    [Test]
    public void DefAndSetBang()
    {
        Assert.That(X("(def x 1)"), Does.StartWith("(def cleat.test.expander/x {:dynamic false, :init true} (const {:line 1, :column 1, :file \"t.clj\"}) (const 1))"));
        Assert.That(_ns.FindInternedVar(Symbol.intern("x")), Is.Not.Null);
        Assert.That(X("(def ^:dynamic *y* \"doc\" 2)"), Does.Contain("{:dynamic true, :init true}").And.Contain(":doc \"doc\""));
        Assert.That(_ns.FindInternedVar(Symbol.intern("*y*")).isDynamic(), Is.True);
        Assert.That(X("(def z)"), Does.Contain("{:dynamic false, :init false}").And.EndWith(" nil)"));
        Assert.That(X("(set! dyn 3)"), Is.EqualTo("(set! (var-ref cleat.test.expander/dyn {:dynamic true, :redef false}) (const 3))"));
        Assert.That(Fails("(def)"), Does.Contain("Too few arguments to def"));
        Assert.That(Fails("(def a b c d)"), Does.Contain("Too many arguments to def"));
        Assert.That(Fails("(set! (let* [] 1) 2)"), Does.Contain("Invalid assignment target"));
    }

    [Test]
    public void MacrosInlineAndEnv()
    {
        Assert.That(X("(when2 1 2)"), Is.EqualTo("(if (const 1) (const 2) (const nil))"));
        Assert.That(X("(let* [a 1] (env-keys))"), Is.EqualTo("(let [a__1 (const 1)] (const (a)))"));
        Assert.That(RT.printString(_x.Macroexpand(Read("(when2 1 2)"))), Is.EqualTo("(if 1 2 nil)"));
        Assert.That(X("(plus 1 2)"), Is.EqualTo("(host-call \"System.Math\" \"Max\" true nil ((const 1) (const 2)))"));
        Assert.That(X("(plus 1 2 3)"), Does.StartWith("(call (var-ref cleat.test.expander/plus"));
        Assert.That(X("(let* [when2 1] (when2 1 2))"), Is.EqualTo("(let [when2__1 (const 1)] (call (local when2__1) ((const 1) (const 2))))"), "locals shadow macros");
    }

    [Test]
    public void Invocations()
    {
        Assert.That(X("(f 1)"), Is.EqualTo("(call (var-ref cleat.test.expander/f {:dynamic false, :redef false}) ((const 1)))"));
        Assert.That(X("(:k f)"), Is.EqualTo("(keyword-invoke :k (var-ref cleat.test.expander/f {:dynamic false, :redef false}))"));
        Assert.That(X("(instance? String f)"), Is.EqualTo("(instance-of \"System.String\" (var-ref cleat.test.expander/f {:dynamic false, :redef false}))"));
        Assert.That(X("((fn* [] 1))"), Is.EqualTo("(call (fn nil false (() nil (const 1))) ())"));
        Assert.That(Fails("(nil 1)"), Does.Contain("Can't call nil"));
    }

    [Test]
    public void HostInterop()
    {
        Assert.That(X("(. f Length)"), Is.EqualTo("(host-call nil \"Length\" false (var-ref cleat.test.expander/f {:dynamic false, :redef false}) ())"));
        Assert.That(X("(.Length f)"), Is.EqualTo("(host-call nil \"Length\" false (var-ref cleat.test.expander/f {:dynamic false, :redef false}) ())"));
        Assert.That(X("(.-Length f)"), Is.EqualTo("(host-field nil \"Length\" false (var-ref cleat.test.expander/f {:dynamic false, :redef false}))"));
        Assert.That(X("(. f (Substring 1 2))"), Is.EqualTo("(host-call nil \"Substring\" false (var-ref cleat.test.expander/f {:dynamic false, :redef false}) ((const 1) (const 2)))"));
        Assert.That(X("(.Substring ^String f 1)"), Is.EqualTo("(host-call \"System.String\" \"Substring\" false (tag \"System.String\" (var-ref cleat.test.expander/f {:dynamic false, :redef false})) ((const 1)))"));
        Assert.That(X("(String/Concat \"a\" \"b\")"), Is.EqualTo("(host-call \"System.String\" \"Concat\" true nil ((const \"a\") (const \"b\")))"));
        Assert.That(X("(. String (Concat \"a\"))"), Is.EqualTo("(host-call \"System.String\" \"Concat\" true nil ((const \"a\")))"));
        Assert.That(X("(String/.Trim f)"), Is.EqualTo("(host-call \"System.String\" \"Trim\" false (tag \"System.String\" (call (var-ref clojure.core/identity {:dynamic false, :redef false}) ((var-ref cleat.test.expander/f {:dynamic false, :redef false})))) ())"));
        Assert.That(X("(StringBuilder. \"x\")"), Is.EqualTo("(host-new \"System.Text.StringBuilder\" ((const \"x\")))"));
        Assert.That(X("(new StringBuilder)"), Is.EqualTo("(host-new \"System.Text.StringBuilder\" ())"));
        Assert.That(X("(StringBuilder/new \"x\")"), Is.EqualTo("(host-new \"System.Text.StringBuilder\" ((const \"x\")))"));
        Assert.That(X("(set! (.-Length f) 1)"), Is.EqualTo("(host-set-field nil \"Length\" false (var-ref cleat.test.expander/f {:dynamic false, :redef false}) (const 1))"));
        Assert.That(X("(.Join ^String f (type-args long) \",\" f)"), Does.Contain("Join"));
        Assert.That(X("(import* \"System.Text.StringBuilder\")"), Is.EqualTo("(import \"System.Text.StringBuilder\")"));
        Assert.That(X("(.Length ^\"System.String[]\" f)"), Does.StartWith("(host-call \"System.String[]\""));
        Assert.That(Fails("(new NoSuchType)"), Does.Contain("Unable to resolve classname: NoSuchType"));
        Assert.That(Fails("(. f)"), Does.Contain("Malformed member expression"));
    }

    [Test]
    public void TryCatchFinally()
    {
        Assert.That(X("(try 1 (catch Exception e e) (finally 2))"),
            Is.EqualTo("(try (const 1) ((catch \"System.Exception\" e__1 (tag \"System.Exception\" (local e__1)))) (const 2))"));
        Assert.That(X("(try 1)"), Is.EqualTo("(const 1)"));
        Assert.That(X("(try 1 (catch System.ArgumentException e 2) (catch Exception e 3))"), Does.Contain("System.ArgumentException").And.Contain("System.Exception"));
        Assert.That(Fails("(try 1 (catch Exception e 2) 3)"), Does.Contain("Only catch or finally clause can follow catch"));
        Assert.That(Fails("(try 1 (finally 2) 3)"), Does.Contain("finally clause must be last"));
        Assert.That(Fails("(try 1 (catch Nope e 2))"), Does.Contain("Unable to resolve classname: Nope"));
    }

    [Test]
    public void CaseStar()
    {
        // (case x 1 :a 2 :b :d) 的 case 宏输出形态
        Assert.That(X("(let* [x 1] (case* x 0 0 :d {1 [1 :a], 2 [2 :b]} :compact :int))"),
            Is.EqualTo("(let [x__1 (const 1)] (case (local x__1) 0 0 1 2 (const :d) ((1 1 (const :a)) (2 2 (const :b))) :compact :int nil))"));
        Assert.That(X("(let* [x 1] (case* x 0 0 :d {7 [\"s\" :a]} :sparse :hash-equiv #{7}))"), Does.Contain("(7 \"s\" (const :a))").And.Contain(":hash-equiv #{7}"));
    }

    public interface IPair { object First(); object Second(); object Nth(long i); object Nth(long i, string s); object Nth(string s, long i); }

    [Test]
    public void DeftypeAndReify()
    {
        _ns.importClass(Symbol.intern("IPair"), typeof(IPair));
        string lc = X("(deftype* P cleat.test.expander.P [a ^:unsynchronized-mutable b] :implements [IPair] (First [this] a) (Second [_] (set! b 2) b) (Nth [_ i] i) (Nth [_ ^long i ^String s] s))");
        Assert.That(lc, Does.StartWith("(deftype \"P\" \"cleat.test.expander.P\" ((field \"a\" a__1 \"System.Object\" false false) (field \"b\" b__2 \"System.Object\" true false)) (\"Cleat.Core.Tests.ExpanderTests+IPair\")"));
        Assert.That(lc, Does.Contain("(method \"Cleat.Core.Tests.ExpanderTests+IPair\" \"First\" this__3 () \"System.Object\" (local a__1))"));
        Assert.That(lc, Does.Contain("(set! (local b__2) (const 2))"));
        Assert.That(lc, Does.Contain("\"Nth\" ___5 ((i__6 \"System.Int64\")) \"System.Object\" (tag \"System.Int64\" (local i__6))"));
        Assert.That(lc, Does.Contain("((i__8 \"System.Int64\") (s__9 \"System.String\"))"));
        Assert.That(lc, Does.Contain(":reify false").And.Contain(":implements [IPair]"));
        Assert.That(X("(reify* [IPair] (First [_] 1) (Second [_] 2) (Nth [_ i] i) (Nth [_ ^long i ^String s] s) (Nth [_ ^String s ^long i] s) (ToString [_] \"p\"))"), Does.Contain("(method nil \"ToString\"").And.Contain(":reify true"));
        Assert.That(Fails("(deftype* P P [a] :implements [IPair] (Nope [this] a))"), Does.Contain("Can't define method not in interfaces: Nope"));
        Assert.That(Fails("(deftype* P P [a] :implements [IPair] (Nth [this i s] s))"), Does.Contain("Must hint overloaded method: Nth"));
        Assert.That(Fails("(deftype* P P [a] :implements [IPair] (First [this] (set! a 1)))"), Does.Contain("Cannot assign to non-mutable: a"));
        Assert.That(Fails("(deftype* P P [a] :implements [String] (First [this] a))"), Does.Contain("only interfaces are supported"));
    }

    [Test]
    public void ReaderResolverFollowsExpanderRules()
    {
        var r = new Expander.ReaderResolver(_x);
        Assert.That(r.CurrentNS(), Is.EqualTo(Symbol.intern("cleat.test.expander")));
        Assert.That(r.ResolveClass(Symbol.intern("String")), Is.EqualTo(Symbol.intern("System.String")));
        Assert.That(r.ResolveVar(Symbol.intern("f")), Is.EqualTo(Symbol.intern("cleat.test.expander", "f")));
        Assert.That(r.IsSpecial(Symbol.intern("if")), Is.True);
        Assert.That(r.IsSpecial(Symbol.intern("when2")), Is.False);
    }

    [Test]
    public void TopLevelDoIsDetected()
    {
        Assert.That(_x.IsTopLevelDo(Read("(do 1 2)"), out var body), Is.True);
        Assert.That(RT.count(body), Is.EqualTo(2));
        Assert.That(_x.IsTopLevelDo(Read("(when2 1 2)"), out _), Is.False);
    }
}
