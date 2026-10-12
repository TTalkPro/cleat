using System;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace Cleat.Host.Tests;

/// <summary>
/// cleat-057：clojure.syntax 的用户级卫生宏（defsyntax / syntax-rules / syntax-case / syntax / with-syntax）：
/// 模板引入的名字不捕获、字面量按 free-identifier=? 比较、与 defmacro 互调、macroexpand 只给 Clojure 数据、
/// 定义卫生宏的命名空间 AOT 后在新进程里被另一个命名空间使用。每个测试在自己的临时目录里以子进程运行宿主。
/// </summary>
[Category("Host")]
public class HygienicMacroTests
{
    private string _dir;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cleat-057-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_dir, true); } catch (Exception) { }
    }

    private void Write(string rel, string text)
    {
        string path = Path.Combine(_dir, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, text);
    }

    private HostProcessTests.Result Run(params string[] args) => HostProcessTests.CleatIn(_dir, null, null, args);

    private static string[] Lines(HostProcessTests.Result r) => r.Out.TrimEnd('\n').Split('\n');

    private const string Lib = @"(ns t057.lib (:require [clojure.syntax :refer [defsyntax syntax-rules syntax-case syntax with-syntax syntax->datum datum->syntax]]))

(defsyntax my-or
  ""(my-or e ...): or, hygienically""
  (syntax-rules ()
    [(_) false]
    [(_ e) e]
    [(_ e1 e2 ...) (let [t e1] (if t t (my-or e2 ...)))]))

(defmacro bad-or [& es]
  (if es `(let [~'t ~(first es)] (if ~'t ~'t (bad-or ~@(next es)))) false))

(defsyntax my-cond
  (syntax-rules (else)
    [(_) nil]
    [(_ [else e]) e]
    [(_ [c e] r ...) (if c e (my-cond r ...))]))

(defsyntax my-let
  (syntax-rules ()
    [(_ [[n v] ...] body ...) ((fn [n ...] body ...) v ...)]))

(defsyntax aif
  ""anaphoric if: deliberately captures it (a procedural transformer; templates cannot call functions)""
  (fn [stx]
    (syntax-case stx ()
      [(_ k c th el)
       (with-syntax [[it (datum->syntax k 'it)]]
         (syntax (let [it c] (if it th el))))])))

(defsyntax swap!!
  (syntax-rules ()
    [(_ a b) (let [tmp a] (set! a b) (set! b tmp))]))

(defsyntax count-args
  (fn [stx]
    (syntax-case stx ()
      [(_ x ...) (datum->syntax stx (count (syntax->datum (syntax (x ...)))))])))

(defn helper [x] (* x 100))

(defsyntax scaled
  ""expands to a call of a private-ish fn of this namespace: free names resolve here""
  (syntax-rules ()
    [(_ e) (helper e)]))
";

    private const string Use = @"(ns t057.use (:require [t057.lib :refer [my-or bad-or my-cond my-let aif count-args scaled]]))

(defn helper [x] (+ x 1))

(defn check []
  [(let [t 5] (my-or false t))
   (let [t 5] (bad-or false t))
   (my-or)
   (my-or nil false 3)
   (let [x 7] (my-or false x))
   (my-cond [false 1] [else 2])
   (let [else false] (my-cond [else 1] [else 2]))
   (my-let [[a 1] [b 2]] (+ a b))
   (aif aif 5 (inc it) 0)
   (count-args a b c)
   (scaled 2)
   (let [helper (fn [x] :captured)] (scaled 3))
   (macroexpand-1 '(t057.lib/my-or a b))
   (:hygienic (meta #'my-or))
   (:macro (meta #'my-or))
   (:doc (meta #'my-or))])
";

    private const string Expected =
        "[5 false false 3 7 2 nil 3 6 3 200 300 (let [t a] (if t t (my-or b))) true true \"(my-or e ...): or, hygienically\"]";

    [Test]
    public void HygieneFromSource()
    {
        Write("src/t057/lib.clj", Lib);
        Write("src/t057/use.clj", Use);
        var r = Run("-I", "src", "-e", "(require 't057.use)", "-e", "(t057.use/check)");
        Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
        Assert.That(Lines(r).Last(), Is.EqualTo(Expected), r.ToString());
    }

    [Test]
    public void TopLevelUseAndMacroexpandAreData()
    {
        Write("src/t057/lib.clj", Lib);
        var r = Run("-I", "src", "-e", "(require 't057.lib)", "-e", "(in-ns 't057.lib)",
                    "-e", "(def t 5)", "-e", "(my-or false t)",
                    "-e", "(macroexpand '(my-or a b))",
                    "-e", "(class (macroexpand-1 '(my-or a b)))",
                    "-e", "(my-cond [false 1] [else :yes])",
                    "-e", "(try (eval '(my-let [[a 1]] (+ a b))) (catch Exception e (.Message e)))");
        Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
        // -e 打印每个非 nil 的结果（require 为 nil、in-ns 与 def 有值）：从末尾数
        var lines = Lines(r).TakeLast(5).ToArray();
        Assert.That(lines[0], Is.EqualTo("5"), "top-level use of a hygienic macro keeps hygiene (no data-level pre-expansion)");
        Assert.That(lines[1], Is.EqualTo("(let* [t a] (if t t (my-or b)))"), "macroexpand keeps going through let at the data level");
        Assert.That(lines[2], Is.EqualTo("clojure.lang.PersistentList"));
        Assert.That(lines[3], Is.EqualTo(":yes"));
        Assert.That(lines[4], Does.Contain("Unable to resolve symbol: b"), "errors inside hygienic expansions are ordinary Clojure syntax errors");
    }

    [Test]
    public void AotThenFreshProcess()
    {
        Write("src/t057/lib.clj", Lib);
        Write("src/t057/use.clj", Use);
        var c = Run("-I", "src", "-e", "(binding [*compile-path* \"src\"] (compile 't057.lib) (compile 't057.use))");
        Assert.That(c.ExitCode, Is.EqualTo(0), c.ToString());
        Assert.That(File.Exists(Path.Combine(_dir, "src", "t057.lib.clj.dll")), Is.True, c.ToString());
        Assert.That(File.Exists(Path.Combine(_dir, "src", "t057.use.clj.dll")), Is.True, c.ToString());

        // 新进程：两个 dll 都从磁盘加载（源码删掉），再在 REPL 里用 lib 的卫生宏
        File.Delete(Path.Combine(_dir, "src", "t057", "lib.clj"));
        File.Delete(Path.Combine(_dir, "src", "t057", "use.clj"));
        var r = Run("-I", "src", "-e", "(require 't057.use)", "-e", "(t057.use/check)",
                    "-e", "(let [t 5] (t057.lib/my-or false t))",
                    "-e", "(.FullName (.Assembly (class t057.use/check)))");
        Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
        var lines = Lines(r).TakeLast(3).ToArray();
        Assert.That(lines[0], Is.EqualTo(Expected), r.ToString());
        Assert.That(lines[1], Is.EqualTo("5"), r.ToString());
        Assert.That(lines[2], Does.StartWith("\"t057.use.clj,"), r.ToString());
    }

    [Test]
    public void ErrorsAreClojureSyntaxErrors()
    {
        Write("src/t057/lib.clj", Lib);
        var r = Run("-I", "src", "-e", "(require 't057.lib)", "-e", "(in-ns 't057.lib)",
                    "-e", "(try (eval '(my-cond 1 2)) (catch Exception e (.Message e)))",
                    "-e", "(try (eval '(defsyntax bad (syntax-rules () [(_ a a) a]))) (catch Exception e (.Message e)))",
                    "-e", "(try (eval '(defsyntax bad2 (syntax-rules () [(_ a ...) a]))) (catch Exception e (.Message e)))",
                    "-e", "(try (eval '(bad2 1 2)) (catch Exception e (.Message e)))");
        Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
        var lines = Lines(r).TakeLast(4).ToArray();
        Assert.That(lines[0], Does.Contain("no syntax-case clause matches: (my-cond 1 2)"), r.ToString());
        Assert.That(lines[0], Does.StartWith("\"Syntax error"), r.ToString());
        Assert.That(lines[1], Does.Contain("duplicate pattern variable: a"), r.ToString());
        Assert.That(lines[2], Does.StartWith("#'t057.lib/bad2"), "a bad template is only detected when used");
        Assert.That(lines[3], Does.Contain("without enough ellipses"), r.ToString());
    }
}
