using System;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace Cleat.Host.Tests;

/// <summary>
/// Clojure 编译器（cleat-088 ~ 090）的运行测试：编出的程序集能加载运行，驱动的 eval / load / compile 走它。
/// （cleat-085 的逐形式转储、影子模式与两个编译器的差分门控随 Scheme 编译器在 cleat-092 / 093 删除。）
/// </summary>
[Category("Host")]
public class ClojureCompilerTests
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

    /// <summary>
    /// cleat-096：原始返回类型提示写在 defn 的名字上（^long fib）与写在形参表上（fib ^long [...]）都让函数类实现 primifs.LL，
    /// 调用点算出同样的签名（JVM 在名字上的写法会 AbstractMethodError：Var 的 :tag 是求过值的函数 clojure.core/long）。
    /// </summary>
    [Test]
    public void PrimitiveReturnHintOnTheDefnName()
    {
        var r = HostProcessTests.CleatIn(_dir, null, null,
            "-e", "(defn ^long fib [^long n] (if (< n 2) n (+ (fib (dec n)) (fib (- n 2))))) (fib 10)",
            "-e", "(defn fib2 ^long [^long n] (if (< n 2) n (+ (fib2 (dec n)) (fib2 (- n 2))))) (fib2 10)",
            "-e", "(defn ^double half [^long n] (/ n 2.0)) (half 3)",
            "-e", "(defn ^long id [x] x) (id 7)",
            "-e", "[(map str (filter #(.StartsWith (str %) \"clojure.lang.primifs\") (supers (class fib)))) (map str (filter #(.StartsWith (str %) \"clojure.lang.primifs\") (supers (class half))))]");
        Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
        Assert.That(r.Out, Is.EqualTo("#'user/fib\n55\n#'user/fib2\n55\n#'user/half\n1.5\n#'user/id\n7\n[(\"clojure.lang.primifs.LL\") (\"clojure.lang.primifs.LD\")]\n"), r.ToString());
    }

    /// <summary>
    /// cleat-090：eval / load / compile 走 Clojure 写的驱动（cleat.compiler.driver）：简单形式解释、其余编译执行（DriverOps 的计数），
    /// 用户代码里不成对的 pushThreadBindings 不被驱动弹掉，*compile-files* 编出的包程序集在新进程里加载运行。
    /// </summary>
    [Test]
    public void ClojureDriverEvalsLoadsAndCompiles()
    {
        (string, string)[] env = null;
        var r = HostProcessTests.CleatIn(_dir, null, env,
            // 计数从零起：开发布局下编译器可能刚从源码加载过（它自己的形式也计数）
            "-e", "(set! (. Cleat.Core.Compiler.DriverOps SimpleCount) 0) (set! (. Cleat.Core.Compiler.DriverOps CompiledCount) 0)",
            "-e", "(def a 1) (def b [a 2]) (. \"x\" Length) (let* [k :k] (k {:k 1}))",
            "-e", "[(. Cleat.Core.Compiler.DriverOps SimpleCount) (. Cleat.Core.Compiler.DriverOps CompiledCount)]",
            "-e", "(defn f [x] (* x 2)) (f 21)",
            "-e", "(. Cleat.Core.Compiler.DriverOps CompiledCount)",
            "-e", "(. clojure.lang.Var (pushThreadBindings {(var *warn-on-reflection*) true})) (set! *warn-on-reflection* false) *warn-on-reflection*");
        Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
        Assert.That(r.Out, Does.Contain("[6 0]\n#'user/f\n42\n1\n"), "simple forms interpreted (the second set! too), the fn compiled: " + r);
        Assert.That(r.Out, Does.EndWith("false\nfalse\n"), r.ToString());

        Directory.CreateDirectory(Path.Combine(_dir, "src", "demo"));
        File.WriteAllText(Path.Combine(_dir, "src", "demo", "util.clj"), "(ns demo.util)\n(defn twice [x] (* 2 x))\n");
        File.WriteAllText(Path.Combine(_dir, "src", "demo", "core.clj"),
            "(ns demo.core (:require [demo.util :as u]))\n(defrecord Pt [x y])\n(defn hello [n] (str \"hi \" (u/twice n) \" \" (:y (->Pt 1 2))))\n");
        string classes = Path.Combine(_dir, "classes");
        Directory.CreateDirectory(classes);
        var c = HostProcessTests.CleatIn(_dir, null, env, "-I", "src", "-e", "(binding [*compile-path* \"classes\"] (compile 'demo.core))");
        Assert.That(c.ExitCode, Is.EqualTo(0), c.ToString());
        Assert.That(File.Exists(Path.Combine(classes, "demo.core.dll")), Is.True, c.ToString());
        var run = HostProcessTests.CleatIn(_dir, null, null, "-I", "classes", "-e", "(require 'demo.core)", "-e", "(demo.core/hello 5)");
        Assert.That(run.ExitCode, Is.EqualTo(0), run.ToString());
        Assert.That(run.Out, Does.EndWith("\"hi 10 2\"\n"), run.ToString());
    }
}
