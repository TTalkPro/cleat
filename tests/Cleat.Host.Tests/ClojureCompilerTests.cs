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
    /// cleat-099：*warn-on-reflection* 为真时，按分析期已知的类型决议不了的宿主调用向 *err* 报一行，措辞照 ClojureCLR（test_clojure rt 的正则）：
    /// 目标类型未知、成员不存在（no such method）、多个重载按实参类型选不出（argument types: …）、构造器。能决议的不报：提示、字面量、
    /// Var 的 :tag（str）、:inline 展开（int）、嵌套宿主调用与属性的返回类型（.Name）、接口继承的方法（valAt）、泛型方法（aclone）；
    /// defonce / defmulti / defprotocol 等核心宏也不报。*warn-on-reflection* 为假时什么都不报。
    /// </summary>
    [Test]
    public void ReflectionWarnings()
    {
        File.WriteAllText(Path.Combine(_dir, "w.clj"), string.Join("\n", new[]
        {
            "(set! *warn-on-reflection* true)",
            "(defn a1 [x] (.blah x))",
            "(defn a2 [^String x] (.blah x))",
            "(defn a3 [^String x] (.zap x 1))",
            "(defn a4 [^String x] (.IndexOf x 12.1 'a))",
            "(defn a5 [^String x y] (.IndexOf x y))",
            "(defn a6 [x] (.zap x 1))",
            "(defn a7 [] (String/Format #\"boom\" 12))",
            "(defn a8 [] (String. 1 2 3))",
            "(println \"--silent\")",
            "(defn b1 [^String x] (.IndexOf x \"a\"))",
            "(defn b2 [^System.IO.TextWriter w x] (.Write w (str x)))",
            "(defn b3 [^Type t] (.IndexOf (.Name t) \".\"))",
            "(defn b4 [^System.Array a ^System.Array b] (System.Array/Copy a b (int 3)))",
            "(defn b5 [^objects a i v] (aset a i v) (aclone a))",
            "(defn b6 [^clojure.lang.IPersistentMap m k] (.valAt m k))",
            "(defn b7 [^String s] [(.ToUpper \"abc\") (.Substring s 1) (System.Text.StringBuilder. \"x\")])",
            "(defonce d1 1) (defmulti d2 identity) (defprotocol D3 (d3 [x]))",
            "(binding [*warn-on-reflection* false] (eval '(defn c1 [x] (.blah x))))",
            "(println :done)",
        }));
        var r = HostProcessTests.CleatIn(_dir, null, null, Path.Combine(_dir, "w.clj"));
        Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
        Assert.That(r.Out, Is.EqualTo("--silent\n:done\n"), r.ToString());
        var lines = r.Err.Split('\n').Where(l => l.StartsWith("Reflection warning")).ToArray();
        Assert.That(lines.Length, Is.EqualTo(8), r.ToString());
        string[] expected =
        {
            @"w\.clj:2:\d+ - reference to field/property blah can't be resolved \(target class is unknown\)\.",
            @"w\.clj:3:\d+ - reference to field/property blah on System\.String can't be resolved\.",
            @"w\.clj:4:\d+ - call to method zap on System\.String can't be resolved \(no such method\)\.",
            @"w\.clj:5:\d+ - call to method IndexOf on System\.String can't be resolved \(argument types: System\.Double, clojure\.lang\.Symbol\)\.",
            @"w\.clj:6:\d+ - call to method IndexOf on System\.String can't be resolved \(argument types: unknown\)\.",
            @"w\.clj:7:\d+ - call to method zap can't be resolved \(target class is unknown\)\.",
            @"w\.clj:8:\d+ - call to static method Format on System\.String can't be resolved \(argument types: System\.Text\.RegularExpressions\.Regex, System\.Int64\)\.",
            @"w\.clj:9:\d+ - call to System\.String ctor can't be resolved\.",
        };
        for (int i = 0; i < expected.Length; i++)
            Assert.That(lines[i], Does.Match("^Reflection warning, .*" + expected[i] + "$"), r.ToString());
    }

    /// <summary>
    /// cleat-102：Num——编译期定不出 long / double 的局部变量、循环变量与形参放进 Cleat.Core.Runtime.Num 做算术，不装箱。
    /// 语义用例（tests/Cleat.Host.Tests/data/num_semantics.clj：无提示形参进入的循环、溢出、+'、混合、Ratio / BigInt / BigDecimal、NaN、
    /// == 与 =、非数与 nil 报错、除零、unchecked-math、形参既原始又装箱使用、NaN 的同一性相等）在 CLEAT_NO_OPT=num 与开两种设置下输出逐字相同；
    /// 嵌套算术的 fn 编成程序集后，循环体里没有 Double 装箱（只有返回时的一次）而有 Num 的运算。
    /// </summary>
    [Test]
    public void NumUnboxedArithmetic()
    {
        string script = Path.Combine(HostProcessTests.RepoDir, "tests", "Cleat.Host.Tests", "data", "num_semantics.clj");
        var on = HostProcessTests.CleatIn(_dir, null, null, script);
        var off = HostProcessTests.CleatIn(_dir, null, new[] { ("CLEAT_NO_OPT", "num") }, script);
        Assert.That(on.ExitCode, Is.EqualTo(0), on.ToString());
        Assert.That(off.ExitCode, Is.EqualTo(0), off.ToString());
        Assert.That(on.Out, Is.EqualTo(off.Out), "Num on:\n" + on.Out + "\nNum off:\n" + off.Out);
        Assert.That(on.Out, Does.Contain("45 45.5 7/2 13N 4.0M"), on.ToString());
        Assert.That(on.Out.Split('\n').Length, Is.GreaterThan(12), on.ToString());

        // 生成的代码：嵌套算术的循环按 Num 算
        string src = Path.Combine(_dir, "numbench.clj");
        File.WriteAllText(src, "(ns numbench)\n(defn poly [n a b] (loop [i 0 s 0.0] (if (< i n) (recur (inc i) (+ s (* (- a b) (+ a b)))) s)))\n");
        var r = HostProcessTests.CleatIn(_dir, null, new[] { ("CLEAT_DUMP_LCIL", "1") },
            "-I", _dir, "--compile-package", Path.Combine(_dir, "numbench.dll"), "--package", "numbench", "--root", _dir, "numbench");
        Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
        string lcil = File.ReadAllText(Path.Combine(_dir, "numbench.dll.lcil"));
        int polyStart = lcil.IndexOf("(type \"numbench$poly\"", StringComparison.Ordinal);
        int polyEnd = lcil.IndexOf("\n  (type ", polyStart + 1, StringComparison.Ordinal);
        string poly = lcil.Substring(polyStart, (polyEnd < 0 ? lcil.Length : polyEnd) - polyStart);
        Assert.That(poly, Does.Contain("\"Cleat.Core.Runtime.Num\" \"Multiply\""), poly);
        Assert.That(poly, Does.Contain("\"Cleat.Core.Runtime.Num\" \"FromObject\""), poly);
        Assert.That(System.Text.RegularExpressions.Regex.Matches(poly, "\\(box \"System.Double\"\\)").Count, Is.LessThanOrEqualTo(1), poly);
    }

    /// <summary>
    /// cleat-104：无提示的纯数值函数有类型化入口（invokeStaticTyped，通用入口实参相符时转调，自调用在 Var 未被重定义时直调）。
    /// 语义用例（tests/Cleat.Host.Tests/data/typed_semantics.clj：long / double / Ratio / BigInt / BigDecimal / Int32 / 非数 / nil 实参、long 进 double 出、
    /// 溢出、= / zero? / recur、unchecked-math、重定义后旧函数的类型化体看到新的 Var 值（结果是 long、double 与非数）、经 map / apply / partial 调用）
    /// 在 CLEAT_NO_OPT=typed 与开两种设置下输出逐字相同；fib 有 invokeStaticTyped(Int64) → Int64，有 ^long 提示的不另生成。
    /// </summary>
    [Test]
    public void TypedEntries()
    {
        string script = Path.Combine(HostProcessTests.RepoDir, "tests", "Cleat.Host.Tests", "data", "typed_semantics.clj");
        var on = HostProcessTests.CleatIn(_dir, null, null, script);
        var off = HostProcessTests.CleatIn(_dir, null, new[] { ("CLEAT_NO_OPT", "typed") }, script);
        Assert.That(on.ExitCode, Is.EqualTo(0), on.ToString());
        Assert.That(off.ExitCode, Is.EqualTo(0), off.ToString());
        Assert.That(on.Out, Is.EqualTo(off.Out), "typed on:\n" + on.Out + "\ntyped off:\n" + off.Out);
        Assert.That(on.Out, Does.Contain("6765 6765.0 1/2 610N 99.5M 144"), on.ToString());
        Assert.That(on.Out, Does.Contain("2.5 2.5 1.5"), on.ToString());
        Assert.That(on.Out.Split('\n').Length, Is.GreaterThan(14), on.ToString());

        string src = Path.Combine(_dir, "typed.clj");
        File.WriteAllText(src, "(ns typed)\n(defn fib [n] (if (< n 2) n (+ (fib (- n 1)) (fib (- n 2)))))\n"
            + "(defn pfib ^long [^long n] (if (< n 2) n (+ (pfib (- n 1)) (pfib (- n 2)))))\n"
            + "(defn noisy [n] (println n) (if (< n 1) 0 (noisy (dec n))))\n");
        var r = HostProcessTests.CleatIn(_dir, null, new[] { ("CLEAT_DUMP_LCIL", "1") },
            "-I", _dir, "--compile-package", Path.Combine(_dir, "typed.dll"), "--package", "typed", "--root", _dir, "typed");
        Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
        string lcil = File.ReadAllText(Path.Combine(_dir, "typed.dll.lcil"));
        string Type(string name)
        {
            int start = lcil.IndexOf("(type \"typed$" + name + "\"", StringComparison.Ordinal);
            int end = lcil.IndexOf("\n  (type ", start + 1, StringComparison.Ordinal);
            return lcil.Substring(start, (end < 0 ? lcil.Length : end) - start);
        }
        string fib = Type("fib");
        Assert.That(fib, Does.Contain("(call \"typed$fib\" \"invokeStaticTyped\" (\"System.Object\" \"System.Int64\") \"System.Int64\")"), fib);
        Assert.That(fib, Does.Contain("(call \"clojure.lang.Numbers\" \"add\" (\"System.Int64\" \"System.Int64\") \"System.Int64\")"), fib);
        Assert.That(Type("pfib"), Does.Not.Contain("invokeStaticTyped"));
        Assert.That(Type("noisy"), Does.Not.Contain("invokeStaticTyped"));   // 有副作用的体不特化
    }

    /// <summary>
    /// cleat-103：初值来自无提示形参的最内层循环生成快慢两份（入口 isinst，快份原始局部）。语义用例（tests/Cleat.Host.Tests/data/loopspec_semantics.clj：
    /// 入口是 long / double / Ratio / BigInt / BigDecimal / Int32 / 非数 / nil、溢出、NaN、两个变量一起推测、会变成 double 的变量、循环里装箱使用、
    /// 初值是调用（求值一次、按顺序）、unchecked-math、嵌套循环）在 CLEAT_NO_OPT=loopspec 与开两种设置下输出逐字相同；
    /// 无提示的 sum-long 有 long 的快份（循环体里是 Numbers.add(long, long)，没有 Object 版的 add），有 ^long 提示的不复制。
    /// </summary>
    [Test]
    public void LoopSpeculation()
    {
        string script = Path.Combine(HostProcessTests.RepoDir, "tests", "Cleat.Host.Tests", "data", "loopspec_semantics.clj");
        var on = HostProcessTests.CleatIn(_dir, null, null, script);
        var off = HostProcessTests.CleatIn(_dir, null, new[] { ("CLEAT_NO_OPT", "loopspec") }, script);
        Assert.That(on.ExitCode, Is.EqualTo(0), on.ToString());
        Assert.That(off.ExitCode, Is.EqualTo(0), off.ToString());
        Assert.That(on.Out, Is.EqualTo(off.Out), "loopspec on:\n" + on.Out + "\nloopspec off:\n" + off.Out);
        Assert.That(on.Out, Does.Contain("45 45.5 7/2 13N 4.0M 7"), on.ToString());
        Assert.That(on.Out.Split('\n').Length, Is.GreaterThan(10), on.ToString());

        string src = Path.Combine(_dir, "spec.clj");
        File.WriteAllText(src, "(ns spec)\n(defn sum-long [n x] (loop [i 0 s x] (if (< i n) (recur (inc i) (+ s i)) s)))\n"
            + "(defn tsum [^long n ^long x] (loop [i 0 s x] (if (< i n) (recur (inc i) (+ s i)) s)))\n");
        var r = HostProcessTests.CleatIn(_dir, null, new[] { ("CLEAT_DUMP_LCIL", "1") },
            "-I", _dir, "--compile-package", Path.Combine(_dir, "spec.dll"), "--package", "spec", "--root", _dir, "spec");
        Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
        string lcil = File.ReadAllText(Path.Combine(_dir, "spec.dll.lcil"));
        string Type(string name)
        {
            int start = lcil.IndexOf("(type \"spec$" + name + "\"", StringComparison.Ordinal);
            int end = lcil.IndexOf("\n  (type ", start + 1, StringComparison.Ordinal);
            return lcil.Substring(start, (end < 0 ? lcil.Length : end) - start);
        }
        string sum = Type("sum_long"), tsum = Type("tsum");
        Assert.That(sum, Does.Contain("(isinst \"System.Int64\")"), sum);
        Assert.That(sum, Does.Contain("(call \"clojure.lang.Numbers\" \"add\" (\"System.Int64\" \"System.Int64\") \"System.Int64\")"), sum);
        Assert.That(sum, Does.Contain("(call \"clojure.lang.Numbers\" \"add\" (\"System.Object\" \"System.Int64\") \"System.Object\")"), sum);   // 慢份
        Assert.That(tsum, Does.Not.Contain("isinst"), tsum);
    }

    /// <summary>
    /// cleat-106：带数组提示的 aget / aset / alength 静态决议到类型化的 RT 重载（分析器给宿主调用实参带上引用类型的标注，决议器接受一维数组；
    /// 元素是 long / double / object / bool / char 时 agetOnArray 改用类型化的 aget）。语义用例（tests/Cleat.Host.Tests/data/array_semantics.clj：
    /// 各种元素类型的读写与结果类型、越界、错误的值类型、无提示的数组、amap）在 CLEAT_NO_OPT=host 与开两种设置下输出逐字相同；
    /// 提示与实际类型不符时 castclass 抛 InvalidCastException（JVM 的 checkcast）；long-array 的复制循环里没有运行时决议。
    /// </summary>
    [Test]
    public void ArrayAccessIsResolvedStatically()
    {
        string script = Path.Combine(HostProcessTests.RepoDir, "tests", "Cleat.Host.Tests", "data", "array_semantics.clj");
        var on = HostProcessTests.CleatIn(_dir, null, null, script);
        var off = HostProcessTests.CleatIn(_dir, null, new[] { ("CLEAT_NO_OPT", "host") }, script);
        Assert.That(on.ExitCode, Is.EqualTo(0), on.ToString());
        Assert.That(on.Out, Is.EqualTo(off.Out), "resolution on:\n" + on.Out + "\nresolution off:\n" + off.Out);
        Assert.That(on.Out, Does.Contain("5 1.5 :k 7 \\x true 3"), on.ToString());

        var lie = HostProcessTests.CleatIn(_dir, null, null,
            "-e", "(defn g [^objects o x] (aset o 0 x))", "-e", "(try (g (long-array 1) 1) (catch InvalidCastException _ :cce))");
        Assert.That(lie.Out, Does.EndWith(":cce\n"), lie.ToString());

        string src = Path.Combine(_dir, "arr.clj");
        File.WriteAllText(src, "(ns arr)\n(defn cy [^longs x] (let [n (alength x) ^longs r (long-array n)] (loop [i (dec n)] (if (neg? i) r (do (aset r i (aget x i)) (recur (dec i)))))))\n");
        var r = HostProcessTests.CleatIn(_dir, null, new[] { ("CLEAT_DUMP_LCIL", "1") },
            "-I", _dir, "--compile-package", Path.Combine(_dir, "arr.dll"), "--package", "arr", "--root", _dir, "arr");
        Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
        string lcil = File.ReadAllText(Path.Combine(_dir, "arr.dll.lcil"));
        Assert.That(lcil, Does.Contain("(call \"clojure.lang.RT\" \"aget\" (\"System.Int64[]\" \"System.Int32\") \"System.Int64\")"), lcil);
        Assert.That(lcil, Does.Contain("(call \"clojure.lang.RT\" \"aset\" (\"System.Int64[]\" \"System.Int32\" \"System.Int64\") \"System.Int64\")"), lcil);
        Assert.That(lcil, Does.Not.Contain("\"CallStatic\""), lcil);
    }

    /// <summary>
    /// cleat-099：镜像里的文件再 RT.load 是空操作（JVM 里 AOT 库已初始化的 __init 类同）：(use 'clojure.core)、(require 'clojure.string)、
    /// (load "/clojure/string") 都不从源码重载，改过的 Var 保留；镜像文件的清单由加载器在镜像初始化时记下。
    /// </summary>
    [Test]
    public void LoadingAnImageFileAgainIsANoOp()
    {
        var r = HostProcessTests.CleatIn(_dir, null, null,
            "-e", "(alter-var-root #'clojure.string/blank? (constantly (fn [_] :patched))) nil",
            "-e", "(use 'clojure.core) (require 'clojure.string) (load \"/clojure/string\") (require 'clojure.string :reload)",
            "-e", "[(Cleat.Core.Runtime.Loader/IsImageFile \"clojure/core\") (Cleat.Core.Runtime.Loader/IsImageFile \"clojure/string\") (Cleat.Core.Runtime.Loader/IsImageFile \"clojure/nope\") (clojure.string/blank? \"\")]");
        Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
        Assert.That(r.Out, Does.EndWith("[true true false :patched]\n"), r.ToString());
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
