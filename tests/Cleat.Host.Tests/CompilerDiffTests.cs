using System;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace Cleat.Host.Tests;

/// <summary>
/// cleat-085（designs/19 D5）：逐形式转储（CLEAT_DUMP_IR）、影子模式（CLEAT_COMPILER=shadow）与 tools/compiler-diff.clj。
/// 同一组形式跑两次，Scheme 一侧的转储在临时名规范化后相同；改变一个 pass（关掉 lower-loops）时差分定位到形式与阶段。
/// </summary>
[Category("Host")]
public class CompilerDiffTests
{
    private string _dir;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cleat-085-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static readonly string[] s_forms =
    {
        "-e", "(defn f [x & ys] (loop [i 0 acc []] (if (< i x) (recur (inc i) (conj acc i)) (into acc ys))))",
        "-e", "(defmacro twice [e] `(do ~e ~e))",
        "-e", "(twice (f 3 :a))",
        "-e", "(let [m {:a 1}] (try (:a m) (catch Exception e (throw e)) (finally nil)))",
    };

    private HostProcessTests.Result Dump(string dir, params (string, string)[] extra)
    {
        var env = new[] { ("CLEAT_DUMP_IR", dir), ("CLEAT_NO_SIMPLE_EVAL", "1"), ("CLEAT_COMPILER", "shadow") }.Concat(extra).ToArray();
        var r = HostProcessTests.CleatIn(_dir, null, env, s_forms);
        Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
        return r;
    }

    private static HostProcessTests.Result Diff(string a, string b) =>
        HostProcessTests.CleatIn(HostProcessTests.RepoDir, null, null, "-I", Path.Combine(HostProcessTests.RepoDir, "src", "clojure"),
            Path.Combine(HostProcessTests.RepoDir, "tools", "compiler-diff.clj"), "diff", a, b);

    [Test]
    public void SchemeDumpsAreStableAndShadowRecordsSources()
    {
        string a = Path.Combine(_dir, "a"), b = Path.Combine(_dir, "b");
        Dump(a);
        Dump(b);
        string sch = File.ReadAllText(Path.Combine(a, "scheme", "eval.ir"));
        foreach (var stage in new[] { "p0", "l0", "sl", "cf", "c1", "in", "l1", "l2", "l3", "lcil" })
            Assert.That(sch, Does.Contain(" " + stage + "\n"), "stage " + stage);
        Assert.That(File.ReadAllText(Path.Combine(a, "clojure", "eval.ir")), Does.Contain("src\n\"(def f"), "影子模式记下源形式");
        var d = Diff(Path.Combine(a, "scheme"), Path.Combine(b, "scheme"));
        Assert.That(d.ExitCode, Is.EqualTo(0), d.ToString());
        Assert.That(d.Out, Does.Contain(" 0 different"), d.ToString());
    }

    /// <summary>
    /// cleat-086 的 G1@L0：影子模式下 Clojure 前端对各类形式给出的 L0（p0）与 Scheme 前端规范化后相同，语法错误的文本也相同
    /// （小函数内联关掉：Clojure 编译器的 .inl 在 cleat-090 做）。完整的输入集用 tools/build.cs ir-dump --shadow --corpus 验收。
    /// </summary>
    [Test]
    public void ShadowFrontEndMatchesSchemeAtL0()
    {
        string a = Path.Combine(_dir, "s");
        var env = new[] { ("CLEAT_DUMP_IR", a), ("CLEAT_NO_SIMPLE_EVAL", "1"), ("CLEAT_COMPILER", "shadow"), ("CLEAT_NO_INLINE", "1") };
        string[] forms =
        {
            "(defn ^long fib [^long n] (if (< n 2) n (+ (fib (dec n)) (fib (- n 2)))))",
            "(defn f ([] 0) ([x] (f x 1)) ([x y & more] (reduce + x (cons y more))))",
            "(letfn [(ev? [n] (if (zero? n) true (od? (dec n)))) (od? [n] (if (zero? n) false (ev? (dec n))))] (ev? 10))",
            "(let [x 1 x (inc x) {:keys [a b] :or {a 2}} {:b 3}] (loop [i 0 acc []] (if (< i 3) (recur (inc i) (conj acc (+ x a b i))) acc)))",
            "(try (/ 1 0) (catch ArithmeticException e (.Message e)) (catch :default e :other) (finally (println \"done\")))",
            "(case (rand-int 3) 0 :zero 1 :one (2 3) :many :default)",
            "(defrecord P [x y] Object (ToString [_] (str x \",\" y)))",
            "(deftype T [^:volatile-mutable n] clojure.lang.IDeref (deref [_] (set! n (inc n)) n))",
            "(defprotocol Q (q [this] [this x]))",
            "(extend-type String Q (q ([s] (count s)) ([s x] (str s x))))",
            "(let [r (reify clojure.lang.IDeref (deref [_] 42))] [@r (.ToString (System.Text.StringBuilder. \"x\")) (Math/Abs -1) System.Int32/MaxValue (. \"abc\" (Substring 1)) (.-Length \"ab\")])",
            "(def ^:dynamic *d* 1)",
            "(binding [*d* 2] (set! *d* 3) [*d* #'*d* (var *d*) '(a b) ^{:m 1} [1 2] ^{:m (inc 1)} {:k [*d*]} #{1 :a \"s\"} #\"re\" \\c nil])",
            "(proxy [Object] [] (ToString [] \"p\"))",
            "(fn self [x] (if (pos? x) (self (dec x)) (with-meta [x] {:z 1})))",
            "(recur 1)",
            "(let [x])",
            "(def)",
            "(fn [x x] x)",
            "(nosuch/thing 1)",
            "(.foo)",
        };
        // 后面 6 个是语法错误：包进 try，进程不中断（顶层定义形式不能包，defprotocol 等要在顶层）
        var args = forms.Select((f, i) => i < forms.Length - 6 ? f : "(try (eval '" + f + ") (catch Exception e (str \"caught \" (.Message e))))")
            .SelectMany(f => new[] { "-e", f }).ToArray();
        var r = HostProcessTests.CleatIn(_dir, null, env, args);
        Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
        var d = HostProcessTests.CleatIn(HostProcessTests.RepoDir, null, null, "-I", Path.Combine(HostProcessTests.RepoDir, "src", "clojure"),
            Path.Combine(HostProcessTests.RepoDir, "tools", "compiler-diff.clj"), "diff", Path.Combine(a, "scheme"), Path.Combine(a, "clojure"),
            "--stage", "p0", "--stage", "error");
        Assert.That(d.ExitCode, Is.EqualTo(0), d.ToString());
        Assert.That(d.Out, Does.Contain(" 0 different"), d.ToString());
    }

    [Test]
    public void DiffLocatesTheFirstChangedStage()
    {
        string a = Path.Combine(_dir, "a"), b = Path.Combine(_dir, "b");
        Dump(a);
        Dump(b, ("CLEAT_SCHEME_NO_034", "loops"));
        var d = Diff(Path.Combine(a, "scheme"), Path.Combine(b, "scheme"));
        Assert.That(d.ExitCode, Is.EqualTo(1), d.ToString());
        Assert.That(d.Out, Does.Contain("stage l0"), "lower-loops 之后（l0）开始不同");
        Assert.That(d.Out, Does.Not.Contain("stage p0"), "parse-core 的输出相同");
        Assert.That(d.Out, Does.Contain("source: \"(def f"), "报告附源形式");
    }
}
