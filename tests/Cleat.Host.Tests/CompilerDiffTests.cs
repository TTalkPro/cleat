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
    /// cleat-086 / 087 的 G1：影子模式下 Clojure 编译器对各类形式给出的 L0（p0）与前中端各阶段（l0 sl cf c1 in l1 l2 l3）
    /// 与 Scheme 编译器规范化后相同，语法错误的文本也相同
    /// （小函数内联关掉：Clojure 编译器的 .inl 在 cleat-090 做）。完整的输入集用 tools/build.cs ir-dump --shadow --corpus 验收。
    /// </summary>
    [Test]
    public void ShadowCompilerMatchesSchemeThroughL3()
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
            "--stage", "p0", "--stage", "l0", "--stage", "sl", "--stage", "cf", "--stage", "c1", "--stage", "in",
            "--stage", "l1", "--stage", "l2", "--stage", "l3", "--stage", "error");
        Assert.That(d.ExitCode, Is.EqualTo(0), d.ToString());
        Assert.That(d.Out, Does.Contain(" 0 different"), d.ToString());
    }

    /// <summary>
    /// cleat-088 / 089 的 G2：影子模式下 Clojure 编译器的 codegen 给出的 Lcil 与 Scheme 编译器规范化后相同：内存模式（lcil 阶段）与
    /// AOT 模式（aot 阶段，常量编码）。过程类与共享持有类、原始签名的函数、拆箱推断、try 的处理器、case、常量
    /// （Int32 / true / false / 非有限浮点数 / 非 BMP 字符串 / Clojure 的各种值）、deftype* / reify* / proxy / gen-interface 的类。
    /// 完整的输入集用 tools/build.cs ir-dump --shadow --corpus 验收。
    /// </summary>
    [Test]
    public void ShadowCodegenMatchesSchemeLcil()
    {
        string a = Path.Combine(_dir, "g");
        var env = new[] { ("CLEAT_DUMP_IR", a), ("CLEAT_NO_SIMPLE_EVAL", "1"), ("CLEAT_COMPILER", "shadow"), ("CLEAT_NO_INLINE", "1") };
        string[] forms =
        {
            "(defn fib ^long [^long n] (if (< n 2) n (+ (fib (dec n)) (fib (- n 2)))))",
            "(defn f ([] 0) ([x] (f x 1)) ([x y & more] (reduce + x (cons y more))))",
            "(defn h [^double d] (let [x (* d 2.5)] (loop [i 0 acc 0.0] (if (< i 10) (recur (inc i) (+ acc x)) acc))))",
            "(letfn [(ev? [n] (if (zero? n) true (od? (dec n)))) (od? [n] (if (zero? n) false (ev? (dec n))))] (ev? 10))",
            "(let [x 1 x (inc x) {:keys [a b] :or {a 2}} {:b 3}] (loop [i 0 acc []] (if (< i 3) (recur (inc i) (conj acc (+ x a b i))) acc)))",
            "(try (/ 1 0) (catch ArithmeticException e (.Message e)) (catch :default e :other) (finally (println \"done\")))",
            "(case (rand-int 3) 0 :zero 1 :one (2 3) :many :default)",
            "(def ^:dynamic *d* 1)",
            "(binding [*d* 2] (set! *d* 3) [*d* #'*d* (var *d*) '(a b) ^{:m 1} [1 2] ^{:m (inc 1)} {:k [*d*]} #{1 :a \"s\"} #\"re\" \\c nil false true])",
            "[(Math/Abs -1) System.Int32/MaxValue (. \"abc\" (Substring 1)) (.-Length \"ab\") (.ToString (System.Text.StringBuilder. \"x\")) (= ##Inf (/ 1.0 0.0)) 2.147483647000001e9 \"\U0001F600\"]",
            "(fn self [x] (if (pos? x) (self (dec x)) (with-meta [x] {:z 1})))",
            "(defn g [m & {:keys [k] :or {k 0}}] (let [s (StringBuilder.)] (.Append s (str (:a m) k)) (str s)))",
            // cleat-089：deftype* 一族的类生成
            "(defrecord P [x y] Object (ToString [_] (str x \",\" y)))",
            "(deftype T [^:volatile-mutable n] clojure.lang.IDeref (deref [_] (set! n (inc n)) n))",
            "(defprotocol Q (q [this] [this x]))",
            "(extend-type String Q (q ([s] (count s)) ([s x] (str s x))))",
            "(let [r (reify clojure.lang.IDeref (deref [_] 42))] [@r (q \"ab\") (str (->P 1 2))])",
            "(proxy [Object] [] (ToString [] \"p\"))",
            "(definterface I1 (^long foo [^long x]))",
            "(deftype U [] I1 (foo [_ x] (inc x)))",
            "(def m ^{:line 3} '(a b ^:k c))",
            "(def v [1 2.5 \"s\" \\c :k 'sym {:a 1} #{1 2} (int 7) 1N 1.5M 2/3 #\"r\" String])",
        };
        var r = HostProcessTests.CleatIn(_dir, null, env, forms.SelectMany(f => new[] { "-e", f }).ToArray());
        Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
        var d = HostProcessTests.CleatIn(HostProcessTests.RepoDir, null, null, "-I", Path.Combine(HostProcessTests.RepoDir, "src", "clojure"),
            Path.Combine(HostProcessTests.RepoDir, "tools", "compiler-diff.clj"), "diff", Path.Combine(a, "scheme"), Path.Combine(a, "clojure"),
            "--stage", "lcil", "--stage", "aot", "--stage", "aot-error", "--stage", "error");
        Assert.That(d.ExitCode, Is.EqualTo(0), d.ToString());
        Assert.That(d.Out, Does.Contain(" 0 different"), d.ToString());
        // 每个顶层形式（defprotocol 等展开成几个）一条 lcil 与一条 aot（cleat-089：AOT 模式的常量编码），Clojure 一侧没有多出的记录
        var m = System.Text.RegularExpressions.Regex.Match(d.Out, @"compared (\d+) records: \d+ same, 0 different; \d+ only in A, 0 only in B");
        Assert.That(m.Success, Is.True, d.ToString());
        Assert.That(int.Parse(m.Groups[1].Value), Is.GreaterThanOrEqualTo(2 * forms.Length), d.ToString());
    }

    /// <summary>cleat-088 / 089：Clojure 编译器编出的程序集（内存模式）能加载运行（cleat.compiler.pipeline/eval-form），含 deftype / reify / proxy 生成的类。</summary>
    [Test]
    public void ClojureCompiledFormsRun()
    {
        string[] forms =
        {
            "(require 'cleat.compiler.pipeline)",
            "(def ev cleat.compiler.pipeline/eval-form)",
            "(ev '(defn fib ^long [^long n] (if (< n 2) n (+ (fib (dec n)) (fib (- n 2))))))",
            "(ev '(defn f ([] 0) ([x] (f x 1)) ([x y & more] (reduce + x (cons y more)))))",
            "(ev '(defmacro twice [e] `(do ~e ~e)))",
            "(ev '(defrecord P [x y] Object (ToString [_] (str x \",\" y))))",
            "(ev '(deftype T [^:volatile-mutable n] clojure.lang.IDeref (deref [_] (set! n (inc n)) n)))",
            "(ev '(definterface I1 (^long foo [^long x])))",
            "(ev '(deftype U [] I1 (foo [_ x] (inc x))))",
            "(prn (ev '[(str (->P 1 2)) (:y (map->P {:x 1 :y 5})) (let [t (->T 0)] @t @t) (.foo (U.) 41)"
                + " @(let [n 7] (reify clojure.lang.IDeref (deref [_] (* n 6)))) (str (proxy [Object] [] (ToString [] \"p\")))]))",
            "(prn (ev '[(fib 20) (f) (f 5) (f 1 2 3 4)"
                + " (let [x 1 {:keys [a b] :or {a 2}} {:b 3}] (loop [i 0 acc []] (if (< i 3) (recur (inc i) (conj acc (+ x a b i))) acc)))"
                + " (try (/ 1 0) (catch ArithmeticException e :div) (finally (println \"fin\")))"
                + " (letfn [(ev? [n] (if (zero? n) true (od? (dec n)))) (od? [n] (if (zero? n) false (ev? (dec n))))] (ev? 10))"
                + " (map (fn [x] (* x 2.5)) [1 2 3]) (case 2 0 :zero 1 :one (2 3) :many :default)"
                + " (let [s (StringBuilder.)] (.Append s \"x\") (.Append s 42) (str s)) (let [a (atom 0)] (twice (swap! a inc)) @a)]))",
        };
        var r = HostProcessTests.CleatIn(HostProcessTests.RepoDir, null, null,
            new[] { "-I", Path.Combine(HostProcessTests.RepoDir, "src", "clojure") }.Concat(forms.SelectMany(f => new[] { "-e", f })).ToArray());
        Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
        Assert.That(r.Out, Does.Contain("fin\n[6765 0 6 10 [6 7 8] :div true (2.5 5.0 7.5) :many \"x42\" 2]"), r.ToString());
        Assert.That(r.Out, Does.Contain("[\"1,2\" 5 2 42 42 \"p\"]"), r.ToString());
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
