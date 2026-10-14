using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using System.Threading;
using Cleat.Core.Expand;
using Cleat.Core.Runtime;
using clojure.lang;
using NUnit.Framework;

namespace Cleat.Host.Tests;

/// <summary>
/// cleat-055：语法边界与值边界——用户看到的只有 Clojure 的数据与函数（原为"看不到 Scheme 元层"的三道边界之二，元层已在 cleat-093 删除，
/// 边界本身仍是编译器的契约）。在测试进程里启动宿主（<see cref="Program.Start"/>：镜像 + 编译器 + 钩子），经 eval / macroexpand-1 钩子驱动
/// Clojure 代码，直接检查返回的 CLR 对象。测试原写于 clojure.core 之前（cleat-053），只用特殊形式、def、fn*、互操作与 cleat.kernel 原语。
/// 诊断边界见 <see cref="ErrorGoldenTests"/>。
/// </summary>
[Category("Host")]
[NonParallelizable]
public class ValueBoundaryTests
{
    private static bool s_started;
    private static Namespace s_ns;

    [OneTimeSetUp]
    public void StartHost()
    {
        if (s_started) return;
        OnBigStack(() => Program.Start(new Program.Options()));
        s_ns = Namespace.findOrCreate(clojure.lang.Symbol.intern("boundary.test"));
        s_started = true;
        EvalAll(@"
          (def list* (fn* [& xs] (. clojure.lang.PersistentList (create xs))))
          (def captured nil)
          (def m (fn* [&form &env a b]
                   (. (var captured) (bindRoot [&form &env]))
                   (list* (quote if) a (list* (quote quote) [b 1 1.5 ""s"" \c :k (quote sym) (quote ns/sym) nil true 2/3 100000000000000000000 1.5M
                                                             {:a #{1}} (quote (do 1)) () []]))))
          (. (var m) (setMacro))
          (def ^:dynamic *dyn* 1)");
    }

    // ———————————————— 运行 ————————————————

    /// <summary>展开器与编译器递归深：在大栈的线程上运行（与宿主相同）。</summary>
    private static T OnBigStack<T>(Func<T> f)
    {
        T result = default;
        Exception error = null;
        var t = new Thread(() =>
        {
            try { result = f(); }
            catch (Exception e) { error = e; }
        }, 256 * 1024 * 1024);
        t.Start();
        t.Join();
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
        return result;
    }

    private static T InNs<T>(Func<T> f) => OnBigStack(() =>
    {
        Var.pushThreadBindings(clojure.lang.RT.map(clojure.lang.RT.CurrentNSVar, s_ns));
        try { return f(); }
        finally
        {
            s_ns = (Namespace)clojure.lang.RT.CurrentNSVar.deref();
            Var.popThreadBindings();
        }
    });

    /// <summary>读并逐个求值全部形式（同一个线程，线程绑定在形式之间保留），返回各个值。</summary>
    private static List<object> EvalAll(string src) => InNs(() =>
    {
        var values = new List<object>();
        Program.EvalReader(new StringReader(src), "boundary", values.Add);
        return values;
    });

    private static object Eval(string src)
    {
        var vs = EvalAll(src);
        return vs.Count == 0 ? null : vs[^1];
    }

    private static object Read(string src) =>
      LispReader.read(new LineNumberingTextReader(new StringReader(src)), true, null, false,
        clojure.lang.RT.map(Keyword.intern("read-cond"), Keyword.intern("allow")));

    private static object Macroexpand1(object form) => InNs(() => RuntimeHooks.Macroexpand1(form, null));

    /// <summary>Clojure 的 macroexpand：重复 macroexpand-1 直到结果与输入是同一个对象。</summary>
    private static object Macroexpand(object form) => InNs(() =>
    {
        while (true)
        {
            object x = RuntimeHooks.Macroexpand1(form, null);
            if (ReferenceEquals(x, form)) return form;
            form = x;
        }
    });

    private static string Print(object x) => clojure.lang.RT.printString(x);

    // ———————————————— Clojure 数据的断言 ————————————————

    private static readonly HashSet<Type> LeafTypes = new()
    {
        typeof(bool), typeof(long), typeof(double), typeof(char), typeof(string), typeof(Keyword), typeof(clojure.lang.Symbol),
        typeof(Var), typeof(Namespace), typeof(Regex), typeof(BigInt), typeof(clojure.lang.BigDecimal), typeof(Ratio),
    };

    /// <summary>
    /// 递归断言 x 只含 Clojure 数据：clojure.lang 的集合（含元数据）、long / double / char / string / bool / nil、关键字、符号、Var、
    /// BigInt / BigDecimal / Ratio、正则、类型、&amp;env 的 LocalBinding、Clojure 函数。Int32 等窄整数、StringBuilder、BigInteger
    /// 与其他宿主对象都不允许。
    /// </summary>
    internal static void AssertClojureData(object x, string path = "value") =>
      Walk(x, path, new HashSet<object>(ReferenceEqualityComparer.Instance));

    /// <summary>
    /// reader 的位置元数据 :line / :column（及 ClojureCLR 的 :source-span）在 Clojure 里就是 int（JVM：java.lang.Integer；
    /// ClojureCLR：Int32）：元数据里这些键的值允许 Int32。
    /// </summary>
    private static readonly HashSet<Keyword> IntPositionKeys = new()
    {
        Keyword.intern("line"), Keyword.intern("column"), Keyword.intern("end-line"), Keyword.intern("end-column"),
        Keyword.intern("source-span"), Keyword.intern("start-line"), Keyword.intern("start-column"),   // ClojureCLR 的 :source-span
    };

    private static void Walk(object x, string path, HashSet<object> seen, bool position = false)
    {
        if (x == null) return;
        var t = x.GetType();
        if (position && x is int) return;
        if (!seen.Add(x)) return;
        if (x is IMeta im && im.meta() is { } meta)
        {
            for (ISeq s = clojure.lang.RT.seq(meta); s != null; s = s.next())
            {
                var e = (IMapEntry)s.first();
                Walk(e.key(), path + "^meta{key}", seen);
                Walk(e.val(), path + "^meta{" + Print(e.key()) + "}", seen, e.key() is Keyword k && IntPositionKeys.Contains(k));
            }
        }
        if (LeafTypes.Contains(t) || x is Type) return;
        switch (x)
        {
            case LocalBinding lb:
                Walk(lb.Name, path + ".Name", seen);
                Walk(lb.Unique, path + ".Unique", seen);
                Walk(lb.Tag, path + ".Tag", seen);
                return;
            case IPersistentMap m:
                for (ISeq s = clojure.lang.RT.seq(m); s != null; s = s.next())
                {
                    var e = (IMapEntry)s.first();
                    Walk(e.key(), path + "{key}", seen);
                    Walk(e.val(), path + "{" + Print(e.key()) + "}", seen, position && e.key() is Keyword pk && IntPositionKeys.Contains(pk));
                }
                return;
            case IPersistentCollection or ISeq:
                int i = 0;
                for (ISeq s = clojure.lang.RT.seq(x); s != null; s = s.next(), i++) Walk(s.first(), $"{path}[{i}]", seen);
                return;
            case AFn:
                return;   // Clojure 函数（fn* 的函数类）
        }
        Assert.Fail($"{path}: not Clojure data: {t.FullName} ({x})");
    }

    [Test]
    public void TheDataCheckerRejectsHostObjects()
    {
        Assert.Throws<AssertionException>(() => AssertClojureData(clojure.lang.RT.vector(1L, 2)));      // Int32
        Assert.Throws<AssertionException>(() => AssertClojureData(clojure.lang.RT.vector(new System.Text.StringBuilder("s"))));
        Assert.Throws<AssertionException>(() => AssertClojureData(clojure.lang.RT.map(Keyword.intern("k"), new System.Diagnostics.Stopwatch())));
        AssertClojureData(Read("[1 1.5 \"s\" \\c :k sym ns/sym nil true 2/3 100000000000000000000 1.5M {:a #{1}} (do 1) () #\"re\"]"));
    }

    // ———————————————— 一、语法边界 ————————————————

    private static readonly string[] SpecialForms =
    {
        "(def x 1)", "(def x)", "(if 1 2 3)", "(do 1 2)", "(let* [a 1] a)", "(loop* [a 1] a)", "(recur 1)", "(fn* [a] a)", "(fn* f ([] 1) ([a] a))",
        "(quote (a b))", "(var x)", "(throw e)", "(try 1 (catch System.Exception e 2) (finally 3))", "(monitor-enter o)", "(monitor-exit o)",
        "(. o ToString)", "(. System.Math (Abs -1))", "(new System.Object)", "(set! x 2)", "(letfn* [f (fn* [] 1)] (f))",
        "(case* x 0 0 :d {0 [0 :a]} :compact :int)", "(deftype* boundary.test/T boundary.test.T [a] :implements [])",
        "(reify* [clojure.lang.IDeref] (deref [_] 1))", "(import* \"System.Text.StringBuilder\")", "(catch System.Exception e 1)", "(finally 1)",
        "(& 1)",
    };

    [Test]
    public void MacroexpandStopsAtEverySpecialForm([ValueSource(nameof(SpecialForms))] string src)
    {
        object form = Read(src);
        Assert.That(Macroexpand1(form), Is.SameAs(form), "macroexpand-1");
        Assert.That(Macroexpand(form), Is.SameAs(form), "macroexpand");
    }

    [Test]
    public void UserMacroSeesAndReturnsOnlyClojureData()
    {
        object form = Read("(m 1 (inner x))");
        object x = Macroexpand1(form);
        AssertClojureData(x, "expansion");
        Assert.That(Print(x), Is.EqualTo("(if 1 (quote [(inner x) 1 1.5 \"s\" \\c :k sym ns/sym nil true 2/3 100000000000000000000N 1.5M {:a #{1}} (do 1) () []]))"));
        var v = (IPersistentVector)((ISeq)((ISeq)x).next().next().first()).next().first();
        Assert.That(v.nth(1), Is.TypeOf<long>());
        Assert.That(v.nth(2), Is.TypeOf<double>());
        Assert.That(v.nth(3), Is.TypeOf<string>());
        Assert.That(v.nth(4), Is.TypeOf<char>());
        Assert.That(v.nth(14), Is.TypeOf<PersistentList>());
        Assert.That(v.nth(15), Is.SameAs(PersistentList.EMPTY), "() stays the empty list, not nil");
        Assert.That(Macroexpand(x), Is.SameAs(x), "if is a special form: macroexpand stops");

        // &form 是原形式（同一个对象），&env 在顶层为 nil
        var captured = (IPersistentVector)InNs(() => ((Var)clojure.lang.RT.var("boundary.test", "captured")).deref());
        AssertClojureData(captured, "[&form &env]");
        Assert.That(captured.nth(0), Is.SameAs(form), "&form is the original form");
        Assert.That(captured.nth(1), Is.Null, "&env is nil at top level");
    }

    [Test]
    public void MacroFormAndEnvAreClojureData([Values(true, false)] bool simple)
    {
        WithSimpleEval(simple, () =>
        {
            Eval("(let* [q 1 r \"s\"] (fn* outer [z & more] (loop* [i 0] (try (m q z) (catch System.Exception e nil)))))");
            var captured = (IPersistentVector)InNs(() => ((Var)clojure.lang.RT.var("boundary.test", "captured")).deref());
            AssertClojureData(captured, "[&form &env]");
            var form = (ISeq)captured.nth(0);
            Assert.That(Print(form), Is.EqualTo("(m q z)"));
            var meta = ((IMeta)form).meta();
            Assert.That(meta.valAt(Keyword.intern("line")), Is.TypeOf<int>(), "reader positions are ints, as in Clojure (JVM: Integer)");
            var env = (IPersistentMap)captured.nth(1);
            var names = new List<string>();
            for (ISeq s = clojure.lang.RT.seq(env); s != null; s = s.next())
            {
                var e = (IMapEntry)s.first();
                Assert.That(e.key(), Is.TypeOf<clojure.lang.Symbol>());
                Assert.That(e.val(), Is.TypeOf<LocalBinding>());
                names.Add(((clojure.lang.Symbol)e.key()).Name);
            }
            Assert.That(names, Is.EquivalentTo(new[] { "q", "r", "outer", "z", "more", "i" }));
        });
    }

    [TestCase("(.ToString 1)", "(. 1 ToString)")]
    [TestCase("(System.Text.StringBuilder. \"a\")", "(new System.Text.StringBuilder \"a\")")]
    [TestCase("(System.Math/Abs -1)", "(. System.Math Abs -1)")]
    [TestCase("(System.String/.Length \"abc\")", "(. \"abc\" Length)")]
    public void HostSugarExpandsToClojureData(string src, string expected)
    {
        object x = Macroexpand1(Read(src));
        AssertClojureData(x, "expansion");
        Assert.That(Print(x), Is.EqualTo(expected));
        object y = Macroexpand(x);
        Assert.That(y, Is.SameAs(x), "the rewritten form is a special form");
    }

    // ———————————————— 二、值边界 ————————————————

    private static void WithSimpleEval(bool simple, Action body)
    {
        bool old = Cleat.Core.Compiler.DriverOps.SimpleEvalEnabled;
        Cleat.Core.Compiler.DriverOps.SimpleEvalEnabled = simple;
        try { body(); }
        finally { Cleat.Core.Compiler.DriverOps.SimpleEvalEnabled = old; }
    }

    /// <summary>（源码, 期望的类型或 null 表示 nil, 期望的打印形式）：快速路径与编译路径都检查，值也必须是 Clojure 数据。</summary>
    private static readonly object[][] Values =
    {
        new object[] { "(def x 1)", typeof(Var), "#'boundary.test/x" },
        new object[] { "(. System.Console WriteLine \"from the value-boundary test\")", null, "nil" },
        new object[] { "(. System.Console (WriteLine))", null, "nil" },
        new object[] { "1", typeof(long), "1" },
        new object[] { "(cleat.kernel/add 1 2)", typeof(long), "3" },
        new object[] { "(cleat.kernel/add 9223372036854775806 1)", typeof(long), "9223372036854775807" },
        new object[] { "(cleat.kernel/inc (. \"abc\" Length))", typeof(long), "4" },
        new object[] { "(. \"abc\" Length)", typeof(long), "3" },
        new object[] { "(. System.Int32 MaxValue)", typeof(long), "2147483647" },
        new object[] { "(. System.Convert (ToByte 7))", typeof(long), "7" },
        new object[] { "(. System.Convert (ToSingle 1.5))", typeof(double), "1.5" },
        new object[] { "\\a", typeof(char), "\\a" },
        new object[] { "(. \"abc\" (get_Chars 1))", typeof(char), "\\b" },
        new object[] { "1.5", typeof(double), "1.5" },
        new object[] { "1/2", typeof(Ratio), "1/2" },
        new object[] { "100000000000000000000", typeof(BigInt), "100000000000000000000N" },
        new object[] { "1.5M", typeof(clojure.lang.BigDecimal), "1.5M" },
        new object[] { "\"s\"", typeof(string), "\"s\"" },
        new object[] { "(. System.String (Concat \"a\" \"b\"))", typeof(string), "\"ab\"" },
        new object[] { "false", typeof(bool), "false" },
        new object[] { "(. \"a\" (Equals \"a\"))", typeof(bool), "true" },
        new object[] { "()", PersistentList.EMPTY.GetType(), "()" },
        new object[] { "(quote ())", PersistentList.EMPTY.GetType(), "()" },
        new object[] { "nil", null, "nil" },
        new object[] { "(if false 1)", null, "nil" },
        new object[] { "(do)", null, "nil" },
        new object[] { "(let* [])", null, "nil" },
        new object[] { "(let* [a 1])", null, "nil" },
        new object[] { "(quote (1 \\a \"s\" sym))", typeof(PersistentList), "(1 \\a \"s\" sym)" },
        new object[] { "[1 (cleat.kernel/add 1 1) \\c]", typeof(PersistentVector), "[1 2 \\c]" },
        new object[] { "{:a (cleat.kernel/add 1 1)}", typeof(PersistentArrayMap), "{:a 2}" },
        new object[] { "(import* \"System.Text.StringBuilder\")", null, "nil" },
        new object[] { "(var x)", typeof(Var), "#'boundary.test/x" },
        new object[] { "(:k {:k 1})", typeof(long), "1" },
        new object[] { "((fn* [a b] (cleat.kernel/add a b)) 1 2)", typeof(long), "3" },
        new object[] { "((fn* [] (if false 1)))", null, "nil" },
        new object[] { "((fn* [] (do)))", null, "nil" },
        new object[] { "((fn* [] (try)))", null, "nil" },
        new object[] { "((fn* [] (def y 1)))", typeof(Var), "#'boundary.test/y" },
        new object[] { "((fn* [] (. System.Console (WriteLine))))", null, "nil" },
        new object[] { "(loop* [i 0] (if (cleat.kernel/lt i 3) (recur (cleat.kernel/inc i)) i))", typeof(long), "3" },
        new object[] { "(try 1 (finally 2))", typeof(long), "1" },
        new object[] { "(try (throw (new System.Exception \"e\")) (catch System.Exception e (. e Message)))", typeof(string), "\"e\"" },
        new object[] { "((fn* [x] (case* x 0 0 :none {0 [0 :zero] 1 [1 :one]} :compact :int)) 1)", typeof(Keyword), ":one" },
        new object[] { "(let* [o (new System.Object)] (monitor-enter o) (monitor-exit o))", null, "nil" },
        // 重复的绑定名（Clojure 取最后一个；原来 psyntax 报 duplicate binding）
        new object[] { "[((fn* [a a] a) 1 2) (loop* [a 1 a 2] a) ((fn* [a & a] a) 1 2) (letfn* [f (fn* [] 1) f (fn* [] 2)] (f))]", typeof(PersistentVector), "[2 2 (2) 2]" },
        new object[] { "(loop* [a 1 a 2] (if (cleat.kernel/lt a 5) (recur 0 (cleat.kernel/inc a)) a))", typeof(long), "5" },
        // 编译单元的常量表按 Equals 去重会合并 Clojure 的 () 与 []、带与不带元数据的符号（codegen 的 constant-index!）
        new object[] { "((fn* [b] [b (quote (1 2)) [1 2] () [] (quote x) (. (quote ^:m x) meta)]) 0)", typeof(PersistentVector), "[0 (1 2) [1 2] () [] x {:m true}]" },
        new object[] { "[(quote (1 2)) [1 2] (quote ()) []]", typeof(PersistentVector), "[(1 2) [1 2] () []]" },
        // letfn* 的初值不是 fn*：原来 Scheme 的 letrec 抛 SchemeException（读未初始化的变量）
        new object[] { "(letfn* [a b b 1] a)", null, "nil" },
        new object[] { "((fn* [] (letfn* [a (fn* [] b) b 1] (a))))", typeof(long), "1" },
    };

    [Test]
    public void ValuesAreClojureRepresentations([ValueSource(nameof(Values))] object[] c, [Values(true, false)] bool simple)
    {
        string src = (string)c[0];
        var expectedType = (Type)c[1];
        WithSimpleEval(simple, () =>
        {
            object v = Eval(src);
            if (expectedType == null) Assert.That(v, Is.Null, src);
            else Assert.That(v, Is.TypeOf(expectedType), src);
            AssertClojureData(v, src);
            Assert.That(v is Var var ? var.ToString() : Print(v), Is.EqualTo((string)c[2]), src);
        });
    }

    [Test]
    public void SetBangOfABoundDynamicVarReturnsTheValue([Values(true, false)] bool simple)
    {
        WithSimpleEval(simple, () =>
        {
            var vs = EvalAll("(. clojure.lang.Var (pushThreadBindings {(var *dyn*) 5})) (set! *dyn* 2) *dyn* (. clojure.lang.Var (popThreadBindings)) *dyn*");
            Assert.That(vs[1], Is.TypeOf<long>().And.EqualTo(2L), "set! returns the new value");
            Assert.That(vs[2], Is.EqualTo(2L));
            Assert.That(vs[4], Is.EqualTo(1L));
        });
    }

    [Test]
    public void DeftypeAndReifyValues()
    {
        Assert.That(Eval("(deftype* boundary.test/M boundary.test.M [^:unsynchronized-mutable a] :implements [clojure.lang.IDeref] (deref [this] (set! a 5)))"),
            Is.InstanceOf<Type>());
        Assert.That(Eval("(. (new boundary.test.M 1) deref)"), Is.TypeOf<long>().And.EqualTo(5L), "set! of a mutable field returns the value");
        object r = Eval("(reify* [clojure.lang.Counted] (count [_] 3))");
        Assert.That(r, Is.InstanceOf<Counted>());
        Assert.That(Eval("(. (reify* [clojure.lang.Counted] (count [_] 3)) count)"), Is.TypeOf<long>().And.EqualTo(3L), "int → long");
        Assert.That(Eval("(. (reify* [clojure.lang.IFn] (invoke [this a a] a)) (invoke 1 2))"), Is.EqualTo(2L), "duplicate method params: the last one wins");
    }

    // —— 函数值：Clojure 的函数是 clojure.lang 的函数类，类名是 ns$name ——

    [Test]
    public void FunctionClassesHaveClojureNames()
    {
        var named = Eval("(fn* foo [] 1)");
        Assert.That(named, Is.InstanceOf<AFunction>());
        Assert.That(named.GetType().FullName, Is.EqualTo("boundary.test$foo"));
        var anon = Eval("(fn* [] 1)");
        Assert.That(anon.GetType().FullName, Does.Match(@"^boundary\.test\$fn__\d+$"));
        var defd = Eval("(do (def bar (fn* [a] a)) bar)");
        Assert.That(defd.GetType().FullName, Is.EqualTo("boundary.test$bar"));
        var inner = Eval("(fn* outer [] (fn* inner [] 1))");
        Assert.That(((IFn)inner).invoke().GetType().FullName, Is.EqualTo("boundary.test$outer$inner"));
        var variadic = Eval("(fn* va [& r] r)");
        Assert.That(variadic, Is.InstanceOf<RestFn>());
        foreach (var f in new[] { named, anon, defd, variadic })
            Assert.That(f.GetType().FullName, Does.Not.Match(@"(^|\$)g\$|#|_Module"), "no generated names leak into class names");
        var e = Assert.Throws<ArityException>(() => Eval("(bar 1 2)"));
        Assert.That(e.Message, Is.EqualTo("Wrong number of args (2) passed to: boundary.test/bar"));
    }

    [Test]
    public void ExceptionsSeenByClojureAreHostExceptions([Values(true, false)] bool simple)
    {
        WithSimpleEval(simple, () =>
        {
            Assert.That(Eval("(try (throw 1) (catch System.Exception e (. (. e GetType) Name)))"), Is.EqualTo("InvalidCastException"));
            Assert.That(Eval("(try (let* [s nil] (. s Length)) (catch System.Exception e (. (. e GetType) Name)))"), Is.EqualTo("NullReferenceException"));
            Assert.That(Eval("(try ((fn* [a] a)) (catch System.Exception e (. (. e GetType) Name)))"), Is.EqualTo("ArityException"));
        });
    }
}
