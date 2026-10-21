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
    /// cleat-114：Numbers 的 object 版本（+ - * inc dec < <= > >= ==）先判 long / double。tests/Cleat.Host.Tests/data/arith_semantics.cljc
    /// （long、负数、零、double、负零、NaN、Ratio、BigInt、MaxValue 两两组合）的输出与 .out 逐字相同——.out 与 JVM Clojure 1.12 的输出逐行一致。
    /// </summary>
    [Test]
    public void ObjectArithmeticMatchesJvm()
    {
        string script = Path.Combine(HostProcessTests.RepoDir, "tests", "Cleat.Host.Tests", "data", "arith_semantics.cljc");
        string expected = File.ReadAllText(Path.ChangeExtension(script, ".out")).Replace("\r\n", "\n");
        var r = HostProcessTests.CleatIn(_dir, null, null, script);
        Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
        Assert.That(r.Out, Is.EqualTo(expected), r.ToString());
    }

    /// <summary>
    /// cleat-114：eval 的代码发射成 PE 字节装进不可回收的上下文（分层编译、动态 PGO）；只调用一次、没有循环的函数仍直接按优化编译，
    /// tier-0 的临时变量不在调用期间抓住惰性序列。tests/Cleat.Host.Tests/data/lazy_head_semantics.clj 在 512 MB 堆下跑完（顶层形式、
    /// 只调用一次的函数把 3000 万元素的惰性序列交给长时间运行的被调用者、-main 一类）；CLEAT_NO_OPT=tiered（原来的 RunAndCollect）输出相同。
    /// </summary>
    [Test]
    public void TieredEvalKeepsLazySeqsCollectable()
    {
        string script = Path.Combine(HostProcessTests.RepoDir, "tests", "Cleat.Host.Tests", "data", "lazy_head_semantics.clj");
        var limit = ("DOTNET_GCHeapHardLimit", "0x20000000");
        var on = HostProcessTests.CleatInWithTimeout(_dir, null, new[] { limit }, 600_000, script);
        var off = HostProcessTests.CleatInWithTimeout(_dir, null, new[] { limit, ("CLEAT_NO_OPT", "tiered") }, 600_000, script);
        Assert.That(on.ExitCode, Is.EqualTo(0), on.ToString());
        Assert.That(on.Out, Is.EqualTo(off.Out), on + "\n" + off);
        Assert.That(on.Out, Does.Contain("450000015000000 450000015000000 450000015000000 450000015000000 15000000"), on.ToString());
    }

    /// <summary>
    /// cleat-117：镜像与包里的函数类也分层编译。tests/Cleat.Host.Tests/data/lazy_head_core.clj 在 512 MB 堆下把 3000 万元素的惰性序列交给只调用一次的
    /// 核心函数（dorun、last、nth、count、reduce、apply、some、every?、run!、filter、take、drop、doseq、多序列 map、partition、transduce、concat），
    /// 输出与 .out 逐字相同（与 JVM Clojure 的结果相同）。
    /// </summary>
    [Test]
    public void CoreFunctionsKeepLazySeqsCollectable()
    {
        string data = Path.Combine(HostProcessTests.RepoDir, "tests", "Cleat.Host.Tests", "data");
        var r = HostProcessTests.CleatInWithTimeout(_dir, null, new[] { ("DOTNET_GCHeapHardLimit", "0x20000000") }, 900_000, Path.Combine(data, "lazy_head_core.clj"));
        Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
        Assert.That(r.Out, Is.EqualTo(File.ReadAllText(Path.Combine(data, "lazy_head_core.out"))), r.ToString());
    }

    /// <summary>
    /// cleat-112：deftype / defrecord 的 ^long / ^double 字段编成 Int64 / Double 字段（构造器形参仍是 object，另有按字段类型的构造器），方法体里原始读写，
    /// 外部 (.-f x) 静态决议到原始字段。tests/Cleat.Host.Tests/data/primfield_semantics.clj 的输出与 .out 逐字相同
    /// （与 JVM Clojure 1.12 对照过：类型名之外只有 map->P 给 ^long 字段 1.5 时 JVM 报错、cleat 按 longCast 截断）。
    /// </summary>
    [Test]
    public void PrimitiveTypeFields()
    {
        string script = Path.Combine(HostProcessTests.RepoDir, "tests", "Cleat.Host.Tests", "data", "primfield_semantics.clj");
        string expected = File.ReadAllText(Path.ChangeExtension(script, ".out")).Replace("\r\n", "\n");
        var r = HostProcessTests.CleatIn(_dir, null, null, script);
        Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
        Assert.That(r.Out, Is.EqualTo(expected), r.ToString());
    }

    /// <summary>
    /// cleat-111 / 112：较窄的数值宿主结果（(byte x) 的 RT.byteCast、byte[] / short[] / float[] 的 aset）静态决议并与运行时路径一样加宽，同类型的值类型实参原生直传；
    /// 符号 / 关键字 / 字符串的 = 走快路径；无提示的 let 局部取初值的宿主类型（(object-array n)、(str x)、(StringBuilder.)）。
    /// tests/Cleat.Host.Tests/data/narrow_semantics.clj 在 CLEAT_NO_OPT=host 与开两种设置下输出相同，开时没有反射警告。
    /// </summary>
    [Test]
    public void NarrowHostResultsAndLetTypes()
    {
        string script = Path.Combine(HostProcessTests.RepoDir, "tests", "Cleat.Host.Tests", "data", "narrow_semantics.clj");
        var on = HostProcessTests.CleatIn(_dir, null, null, script);
        var off = HostProcessTests.CleatIn(_dir, null, new[] { ("CLEAT_NO_OPT", "host") }, script);
        Assert.That(on.ExitCode, Is.EqualTo(0), on.ToString());
        Assert.That(on.Out, Is.EqualTo(off.Out), "host on:\n" + on.Out + "\nhost off:\n" + off.Out);
        Assert.That(on.Out, Does.Contain("[:x :x :x] 5 \"x\""), on.ToString());
        Assert.That(on.Err, Does.Not.Contain("Reflection warning"), on.ToString());
    }

    /// <summary>
    /// cleat-110：经 Var 调用、分析时 Var 的值是有 invokeStatic 的函数类时，isinst 守卫后直接 call（内存模式；编成包时不用，求值期间的类运行时不存在）。
    /// 用例 tests/Cleat.Host.Tests/data/knowncall_semantics.clj（重定义成别的函数 / 变长函数 / 多参数个数 / 关键字 / map、with-redefs、动态 Var、闭包、
    /// 抛异常的被调函数、实参的求值顺序、长惰性序列作实参不被调用方抓住（256 MB 堆）、跨命名空间与其重定义）在 CLEAT_NO_OPT=known 与开两种设置下输出逐字相同。
    /// </summary>
    [Test]
    public void KnownCallsToUserFunctions()
    {
        string script = Path.Combine(HostProcessTests.RepoDir, "tests", "Cleat.Host.Tests", "data", "knowncall_semantics.clj");
        var limit = ("DOTNET_GCHeapHardLimit", "0x10000000");
        var on = HostProcessTests.CleatIn(_dir, null, new[] { limit }, script);
        var off = HostProcessTests.CleatIn(_dir, null, new[] { limit, ("CLEAT_NO_OPT", "known") }, script);
        Assert.That(on.ExitCode, Is.EqualTo(0), on.ToString());
        Assert.That(on.Out, Is.EqualTo(off.Out), "known on:\n" + on.Out + "\nknown off:\n" + off.Out);
        Assert.That(on.Out, Does.Contain("3000000\n42\n63\n"), on.ToString());
        // cleat-116：eval 定义的小函数登记可内联子句，之后的调用点在类型守卫后内联——重定义后同样生效
        var noInline = HostProcessTests.CleatIn(_dir, null, new[] { limit, ("CLEAT_NO_INLINE", "1") }, script);
        Assert.That(on.Out, Is.EqualTo(noInline.Out), "inline on:\n" + on.Out + "\ninline off:\n" + noInline.Out);
    }

    /// <summary>
    /// cleat-109：小整数装箱缓存（LongBox，-128 ~ 1023）与运行时数值的快路径（Util.equiv / Numbers.ops / category / ConvertToLong 先判 long、double，
    /// empty? 经 Colls.IsEmpty 先用类检查）。用例 tests/Cleat.Host.Tests/data/boxcache_semantics.clj 的输出与 .out 逐字相同（与 JVM Clojure 1.12 对照过：
    /// 只有类型名、异常消息与 CLR 按值的 identical? 不同），CLEAT_NO_OPT=boxcache（生成的代码用 box）时也相同。
    /// </summary>
    [Test]
    public void BoxCacheAndNumericFastPaths()
    {
        string script = Path.Combine(HostProcessTests.RepoDir, "tests", "Cleat.Host.Tests", "data", "boxcache_semantics.clj");
        string expected = File.ReadAllText(Path.ChangeExtension(script, ".out")).Replace("\r\n", "\n");
        var on = HostProcessTests.CleatIn(_dir, null, null, script);
        var off = HostProcessTests.CleatIn(_dir, null, new[] { ("CLEAT_NO_OPT", "boxcache") }, script);
        Assert.That(on.ExitCode, Is.EqualTo(0), on.ToString());
        Assert.That(on.Out, Is.EqualTo(expected), on.ToString());
        Assert.That(off.Out, Is.EqualTo(expected), off.ToString());
    }

    /// <summary>
    /// cleat-142：char 常量与 char 装箱走 CharBox（cleat-121 的缓存扩到 0 ~ 255）。常量发射的 (char?) 分支原来每次求值都
    /// `ldc.i4 + box char-type`，browse 的真实分配里 System.Char 占 28%（1.84 GB）；现在读缓存。CLR 的 char 装箱按值比较
    /// （Util.identical 对 char 比值），所以引用同一性不外露：用例 tests/Cleat.Host.Tests/data/charbox_semantics.clj 的
    /// = / hash / pr-str / str / condp / 集合键 / nth / seq / mapv / 越界异常输出与 .out 逐字相同，CLEAT_NO_OPT=charbox 时也相同。
    /// </summary>
    [Test]
    public void CharConstantsAreBoxedThroughTheCache()
    {
        string script = Path.Combine(HostProcessTests.RepoDir, "tests", "Cleat.Host.Tests", "data", "charbox_semantics.clj");
        string expected = File.ReadAllText(Path.ChangeExtension(script, ".out")).Replace("\r\n", "\n");
        var on = HostProcessTests.CleatIn(_dir, null, null, script);
        var off = HostProcessTests.CleatIn(_dir, null, new[] { ("CLEAT_NO_OPT", "charbox") }, script);
        Assert.That(on.ExitCode, Is.EqualTo(0), on.ToString());
        Assert.That(on.Out, Is.EqualTo(expected), on.ToString());
        Assert.That(off.Out, Is.EqualTo(expected), off.ToString());
    }

    /// <summary>
    /// cleat-143：String 的 first / seq / next / more 直读路径与 StringSeq 的 CharBox 返回路径不改变值、类型、identity 边界。
    /// 用例（tests/Cleat.Host.Tests/data/strseq_semantics.clj：空串、单字符、代理对码元、subs / map / reduce / into、
    /// nth 默认值、seq 的 = 与 identical?、打印形态）与 .out 逐字相同（已对照 JVM Clojure），
    /// CLEAT_NO_OPT=strseq 回到原来的 RT.seq → StringSeq 和隐式装箱时也相同。
    /// </summary>
    [Test]
    public void StringSeqDirectReads()
    {
        string script = Path.Combine(HostProcessTests.RepoDir, "tests", "Cleat.Host.Tests", "data", "strseq_semantics.clj");
        string expected = File.ReadAllText(Path.ChangeExtension(script, ".out")).Replace("\r\n", "\n");
        var on = HostProcessTests.CleatIn(_dir, null, null, script);
        var off = HostProcessTests.CleatIn(_dir, null, new[] { ("CLEAT_NO_OPT", "strseq") }, script);
        Assert.That(on.ExitCode, Is.EqualTo(0), on.ToString());
        Assert.That(on.Out, Is.EqualTo(expected), on.ToString());
        Assert.That(off.Out, Is.EqualTo(expected), off.ToString());
    }

    /// <summary>
    /// cleat-147：RT.nth 的 ISeq / LazySeq / StringSeq / counted list 快路径不改变值、默认值与异常边界。
    /// 用例（tests/Cleat.Host.Tests/data/nthfast_semantics.clj：vector / list / string 代理对 / StringSeq /
    /// chunked / lazy / 无穷 lazy / object 与 long array，以及负索引与越界）在 CLEAT_NO_OPT=nthfast 时输出逐字相同。
    /// </summary>
    [Test]
    public void NthFastPaths()
    {
        string script = Path.Combine(HostProcessTests.RepoDir, "tests", "Cleat.Host.Tests", "data", "nthfast_semantics.clj");
        string expected = File.ReadAllText(Path.ChangeExtension(script, ".out")).Replace("\r\n", "\n");
        var on = HostProcessTests.CleatIn(_dir, null, null, script);
        var off = HostProcessTests.CleatIn(_dir, null, new[] { ("CLEAT_NO_OPT", "nthfast") }, script);
        Assert.That(on.ExitCode, Is.EqualTo(0), on.ToString());
        Assert.That(on.Out, Is.EqualTo(expected), on.ToString());
        Assert.That(off.Out, Is.EqualTo(expected), off.ToString());
    }

    /// <summary>
    /// cleat-144：另一个 ns / 编译单元里的 deftype 与 definterface 在编译期可决议。用例
    /// tests/Cleat.Host.Tests/data/crossns_semantics.clj 覆盖 :import、全名与短名类型提示、构造、instance?；
    /// CLEAT_NO_OPT=crossnstype 走旧决议时输出仍与 .out 逐字相同。另编两个包程序集验证消费者单独编译时能从
    /// 依赖包按类名找到生成类型，运行时 :import / 构造 / 接口提示行为不变。
    /// </summary>
    [Test]
    public void CrossNamespaceGeneratedTypesResolve()
    {
        string script = Path.Combine(HostProcessTests.RepoDir, "tests", "Cleat.Host.Tests", "data", "crossns_semantics.clj");
        string expected = File.ReadAllText(Path.ChangeExtension(script, ".out")).Replace("\r\n", "\n");
        var on = HostProcessTests.CleatIn(_dir, null, null, script);
        var off = HostProcessTests.CleatIn(_dir, null, new[] { ("CLEAT_NO_OPT", "crossnstype") }, script);
        Assert.That(on.ExitCode, Is.EqualTo(0), on.ToString());
        Assert.That(on.Out, Is.EqualTo(expected), on.ToString());
        Assert.That(off.Out, Is.EqualTo(expected), off.ToString());

        string src = Path.Combine(_dir, "src"), outDir = Path.Combine(_dir, "out");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(Path.Combine(src, "crossns"));
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(src, "crossns", "gen.clj"),
            "(ns crossns.gen)\n"
            + "(definterface Shape (area []))\n"
            + "(deftype Box [^long side] Shape (area [_] (* side side)))\n");
        File.WriteAllText(Path.Combine(src, "crossns", "use.clj"),
            "(ns crossns.use (:require [crossns.gen]) (:import (crossns.gen Box Shape)))\n"
            + "(defn area ^long [^Shape x] (.area x))\n(defn side ^long [^Box x] (.side x))\n"
            + "(defn -main [& _] (let [b (Box. 7)] (prn [(instance? Box b) (area b) (side b)])))\n");
        var dep = HostProcessTests.CleatIn(_dir, null, null,
            "-I", src, "--compile-package", Path.Combine(outDir, "crossns.gen.dll"),
            "--package", "crossns.gen", "--root", src, "crossns.gen");
        Assert.That(dep.ExitCode, Is.EqualTo(0), dep.ToString());
        var use = HostProcessTests.CleatIn(_dir, null, null,
            "-I", outDir, "-I", src, "--compile-package", Path.Combine(outDir, "crossns.use.dll"),
            "--package", "crossns.use", "--root", src, "crossns.use");
        Assert.That(use.ExitCode, Is.EqualTo(0), use.ToString());
        var run = HostProcessTests.CleatIn(_dir, null, null,
            "-I", outDir, "-e", "(require 'crossns.use)", "-e", "(crossns.use/-main)");
        Assert.That(run.ExitCode, Is.EqualTo(0), run.ToString());
        Assert.That(run.Out, Is.EqualTo("[true 49 7]\n"), run.ToString());
    }

    /// <summary>
    /// cleat-145：deftype 的小访问器（读一个字段、或存一个字段并返回新值）的包装函数在内联时换成
    /// 「具体类型守卫 + 字段直读 / 直存」，守卫不成立时走原来的接口调用。语义用例
    /// tests/Cleat.Host.Tests/data/accinline_semantics.clj（读写与返回值、接收者与实参各只求值一次及其顺序、
    /// nil 与错误类型的异常文本、volatile 字段、^long 原始字段、同接口的两个实现、非访问器方法、
    /// 循环里连续写、中途重定义包装 Var）在 CLEAT_NO_OPT=accinline 时输出逐字相同。
    /// IL 取证两条路径：eval 单元与两个包程序集（消费者单独编译）的开侧都有 isinst 守卫与 ldfld / stfld，
    /// 关侧只剩接口调用。最后退化路径探针（同名重定义 deftype、reify 的另一个实现、错误类型）逐字对照开 / 关。
    /// </summary>
    [Test]
    public void AccessorInliningCrossCompileUnits()
    {
        string script = Path.Combine(HostProcessTests.RepoDir, "tests", "Cleat.Host.Tests", "data", "accinline_semantics.clj");
        string expected = File.ReadAllText(Path.ChangeExtension(script, ".out")).Replace("\r\n", "\n");
        var on = HostProcessTests.CleatIn(_dir, null, null, script);
        var off = HostProcessTests.CleatIn(_dir, null, new[] { ("CLEAT_NO_OPT", "accinline") }, script);
        Assert.That(on.ExitCode, Is.EqualTo(0), on.ToString());
        Assert.That(on.Out, Is.EqualTo(expected), on.ToString());
        Assert.That(off.Out, Is.EqualTo(expected), off.ToString());

        // eval 单元：守卫后的直读 / 直存只在开侧出现（deftype 自身方法的字段访问两侧都有，故比带程序集名的跨单元引用）
        string onLcil = Path.Combine(_dir, "eval-on.lcil"), offLcil = Path.Combine(_dir, "eval-off.lcil");
        var onDump = HostProcessTests.CleatIn(_dir, null, new[] { ("CLEAT_DUMP_EVAL_LCIL", onLcil) }, script);
        var offDump = HostProcessTests.CleatIn(_dir, null,
            new[] { ("CLEAT_NO_OPT", "accinline"), ("CLEAT_DUMP_EVAL_LCIL", offLcil) }, script);
        Assert.That(onDump.Out, Is.EqualTo(expected), onDump.ToString());
        Assert.That(offDump.Out, Is.EqualTo(expected), offDump.ToString());
        string onText = File.ReadAllText(onLcil), offText = File.ReadAllText(offLcil);
        Assert.That(onText, Does.Contain("(isinst \"accinline.gen.Foo"), "开侧应有具体类型守卫");
        Assert.That(onText, Does.Contain("(ldfld \"accinline.gen.Foo, "), "开侧应有跨单元的字段直读");
        Assert.That(onText, Does.Contain("(stfld \"accinline.gen.Foo, "), "开侧应有跨单元的字段直存");
        Assert.That(offText, Does.Not.Contain("(isinst \"accinline.gen.Foo"), offText);
        Assert.That(offText, Does.Not.Contain("(stfld \"accinline.gen.Foo, "), offText);

        // 包路径：gen 与 use 各编一个程序集，use 单独编译时只消费 gen 固化的 .inl（不跑 gen 的入口）
        string src = Path.Combine(_dir, "p"), outOn = Path.Combine(_dir, "p-on"), outOff = Path.Combine(_dir, "p-off");
        Directory.CreateDirectory(Path.Combine(src, "accinlinep"));
        Directory.CreateDirectory(outOn);
        Directory.CreateDirectory(outOff);
        File.WriteAllText(Path.Combine(src, "accinlinep", "gen.clj"),
            "(ns accinlinep.gen)\n(definterface IFoo (getA []) (getB []) (setA [v]))\n"
            + "(deftype Foo [^:unsynchronized-mutable a ^:unsynchronized-mutable b]\n"
            + "  IFoo (getA [_] a) (getB [_] b) (setA [_ v] (set! a v) v))\n"
            + "(defn mk [x y] (Foo. x y))\n"
            + "(defn ga [p] (.getA ^IFoo p))\n(defn gb [p] (.getB ^IFoo p))\n(defn sa! [p v] (.setA ^IFoo p v))\n");
        File.WriteAllText(Path.Combine(src, "accinlinep", "use.clj"),
            "(ns accinlinep.use (:require [accinlinep.gen :as g]))\n"
            + "(defn spin [n] (let [p (g/mk 1 2)] (loop [i 0 acc 0]\n"
            + "  (if (< i n) (do (g/sa! p (+ acc (g/ga p))) (recur (inc i) (g/gb p))) acc))))\n"
            + "(defn -main [& _] (prn (spin 3) :ok))\n");
        void BuildPair(string outDir, (string, string)[] env)
        {
            var gen = HostProcessTests.CleatIn(_dir, null, env,
                "-I", src, "--compile-package", Path.Combine(outDir, "accinlinep.gen.dll"),
                "--package", "accinlinep.gen", "--root", src, "accinlinep.gen");
            Assert.That(gen.ExitCode, Is.EqualTo(0), gen.ToString());
            var use = HostProcessTests.CleatIn(_dir, null, env,
                "-I", outDir, "-I", src, "--compile-package", Path.Combine(outDir, "accinlinep.use.dll"),
                "--package", "accinlinep.use", "--root", src, "accinlinep.use");
            Assert.That(use.ExitCode, Is.EqualTo(0), use.ToString());
        }
        BuildPair(outOn, new[] { ("CLEAT_DUMP_LCIL", "1") });
        BuildPair(outOff, new[] { ("CLEAT_NO_OPT", "accinline"), ("CLEAT_DUMP_LCIL", "1") });
        string useOn = File.ReadAllText(Path.Combine(outOn, "accinlinep.use.dll.lcil"));
        string useOff = File.ReadAllText(Path.Combine(outOff, "accinlinep.use.dll.lcil"));
        Assert.That(useOn, Does.Contain("(isinst \"accinlinep.gen.Foo\""), "消费者的 IL 应有守卫");
        Assert.That(useOn, Does.Contain("(ldfld \"accinlinep.gen.Foo\" \"a\")"), useOff);
        Assert.That(useOn, Does.Contain("(stfld \"accinlinep.gen.Foo\" \"a\")"), useOff);
        Assert.That(useOff, Does.Not.Contain("(isinst \"accinlinep.gen.Foo\""), useOff);
        Assert.That(useOff, Does.Not.Contain("(stfld \"accinlinep.gen.Foo\""), useOff);
        var runOn = HostProcessTests.CleatIn(_dir, null, null,
            "-I", outOn, "-e", "(require 'accinlinep.use)", "-e", "(accinlinep.use/-main)");
        var runOff = HostProcessTests.CleatIn(_dir, null, new[] { ("CLEAT_NO_OPT", "accinline") },
            "-I", outOff, "-e", "(require 'accinlinep.use)", "-e", "(accinlinep.use/-main)");
        Assert.That(runOn.ExitCode, Is.EqualTo(0), runOn.ToString());
        Assert.That(runOn.Out, Is.EqualTo("2 :ok\n"), runOn.ToString());
        Assert.That(runOff.Out, Is.EqualTo("2 :ok\n"), runOff.ToString());

        // 退化路径：同名重定义 deftype（守卫与直读用的是同一个按名决议的类型）、reify 的另一个实现、
        // 错误的接收者类型 —— 开 / 关的输出必须逐字相同
        string probe = Path.Combine(_dir, "probe.clj");
        File.WriteAllText(probe,
            "(definterface IFoo (getA []))\n(deftype Foo [a b] IFoo (getA [_] a))\n(defn ga [p] (.getA ^IFoo p))\n"
            + "(def o1 (Foo. 1 2))\n(deftype Foo [a b] IFoo (getA [_] b))\n(def o2 (Foo. 1 2))\n"
            + "(println 'redef (ga o1) (ga o2) (ga (Foo. 1 2)))\n"
            + "(println 'reify (ga (reify IFoo (getA [_] 42))))\n"
            + "(deftype G [^:unsynchronized-mutable x] IFoo (getA [_] x))\n(println 'foreign (ga (G. 7)))\n"
            + "(println 'wrong (try (ga \"s\") (catch Exception e (.GetType e))))\n");
        var probeOn = HostProcessTests.CleatIn(_dir, null, null, probe);
        var probeOff = HostProcessTests.CleatIn(_dir, null, new[] { ("CLEAT_NO_OPT", "accinline") }, probe);
        Assert.That(probeOn.ExitCode, Is.EqualTo(0), probeOn.ToString());
        Assert.That(probeOn.Out, Is.EqualTo(probeOff.Out), "accinline on:\n" + probeOn.Out + "\noff:\n" + probeOff.Out);
        Assert.That(probeOn.Out.Split('\n').Length, Is.GreaterThanOrEqualTo(5), probeOn.ToString());
    }

    /// <summary>
    /// cleat-146：keyword 的字符串键 intern 命中不新建 Symbol；str 的 long / int 用不变区域性格式化。
    /// 用例 tests/Cleat.Host.Tests/data/intern_semantics.clj 覆盖 identity / 元数据 / find-keyword /
    /// symbol 的文本语义 / gensym 唯一性；CLEAT_NO_OPT=intern 退回旧路径时输出逐字相同。
    /// </summary>
    [Test]
    public void InternAndIntegerStringFastPaths()
    {
        string script = Path.Combine(HostProcessTests.RepoDir, "tests", "Cleat.Host.Tests", "data", "intern_semantics.clj");
        var on = HostProcessTests.CleatIn(_dir, null, null, script);
        var off = HostProcessTests.CleatIn(_dir, null, new[] { ("CLEAT_NO_OPT", "intern") }, script);
        Assert.That(on.ExitCode, Is.EqualTo(0), on.ToString());
        Assert.That(off.ExitCode, Is.EqualTo(0), off.ToString());
        Assert.That(on.Out, Is.EqualTo(off.Out), "intern on:\n" + on.Out + "\nintern off:\n" + off.Out);
        Assert.That(on.Out, Does.StartWith("[true true true \":a\""), on.ToString());
    }

    /// <summary>
    /// cleat-108：回边实参是 if / begin 时，两支里对原始类型循环变量的引用算原始用途，汇合处原始发射（不装箱）。语义用例
    /// （tests/Cleat.Host.Tests/data/joinraw_semantics.clj：计数器、嵌套 if、带副作用的 do、throw 与提前退出的分支、溢出、两支类型不同、Ratio、double、
    /// 无提示初值、循环里另有装箱用途）在 CLEAT_NO_OPT=joinraw 与开两种设置下输出逐字相同；计数器循环里只剩返回值处一次 Int64 装箱。
    /// </summary>
    [Test]
    public void JoinRawUses()
    {
        string script = Path.Combine(HostProcessTests.RepoDir, "tests", "Cleat.Host.Tests", "data", "joinraw_semantics.clj");
        var on = HostProcessTests.CleatIn(_dir, null, null, script);
        var off = HostProcessTests.CleatIn(_dir, null, new[] { ("CLEAT_NO_OPT", "joinraw") }, script);
        Assert.That(on.ExitCode, Is.EqualTo(0), on.ToString());
        Assert.That(off.ExitCode, Is.EqualTo(0), off.ToString());
        Assert.That(on.Out, Is.EqualTo(off.Out), "joinraw on:\n" + on.Out + "\njoinraw off:\n" + off.Out);
        Assert.That(on.Out, Does.Contain("5 0 4 System.Int64"), on.ToString());
        Assert.That(on.Out.Split('\n').Length, Is.GreaterThan(10), on.ToString());

        File.WriteAllText(Path.Combine(_dir, "jr.clj"), "(ns jr)\n(defn e5 [x] (loop [i 0 n 0] (if (< i 10000) (recur (inc i) (if (< i 5000) (inc n) n)) n)))\n");
        var r = HostProcessTests.CleatIn(_dir, null, new[] { ("CLEAT_DUMP_LCIL", "1") },
            "-I", _dir, "--compile-package", Path.Combine(_dir, "jr.dll"), "--package", "jr", "--root", _dir, "jr");
        Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
        string lcil = File.ReadAllText(Path.Combine(_dir, "jr.dll.lcil"));
        int start = lcil.IndexOf("(type \"jr$e5\"", StringComparison.Ordinal);
        int end = lcil.IndexOf("(method \"invoke\"", start, StringComparison.Ordinal);
        string e5 = lcil.Substring(start, end - start);
        Assert.That(System.Text.RegularExpressions.Regex.Matches(e5, "\\(box \"System.Int64\"\\)").Count, Is.LessThanOrEqualTo(1), e5);
        Assert.That(e5, Does.Not.Contain("ArgInt64"), e5);
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
    /// cleat-133 / 137：无提示的候选函数（cleat-137 起不再编译期预生成 LL / DD）标记 RuntimeSigEntry，
    /// 进入运行时观察 / 按需生成框架。语义输出在默认、
    /// CLEAT_NO_OPT=sigspec、CLEAT_NO_OPT=typed（只启用 sigspec）三种设置下逐字相同。
    /// </summary>
    [Test]
    public void SignatureSpecialization()
    {
        string script = Path.Combine(HostProcessTests.RepoDir, "tests", "Cleat.Host.Tests", "data", "sigspec_semantics.clj");
        // 用例按 cleat-135 的 16 次阈值写（dotimes 20）；cleat-139 起缺省阈值是 8，这里显式设成 16，另跑一遍压力模式（阈值 1）
        var t16 = ("CLEAT_RTSPEC_THRESHOLD", "16");
        var sync = ("CLEAT_RTSPEC_SYNC", "1");   // cleat-139 起缺省后台编译；断言生成时机 / 导出文件的运行用同步
        var on = HostProcessTests.CleatIn(_dir, null, new[] { t16 }, script);
        var off = HostProcessTests.CleatIn(_dir, null, new[] { ("CLEAT_NO_OPT", "sigspec") }, script);
        var noTyped = HostProcessTests.CleatIn(_dir, null, new[] { ("CLEAT_NO_OPT", "typed"), t16 }, script);
        var stress = HostProcessTests.CleatIn(_dir, null, new[] { ("CLEAT_RTSPEC_THRESHOLD", "1"), ("CLEAT_RTSPEC_ALL", "1") }, script);
        Assert.That(on.ExitCode, Is.EqualTo(0), on.ToString());
        Assert.That(off.ExitCode, Is.EqualTo(0), off.ToString());
        Assert.That(noTyped.ExitCode, Is.EqualTo(0), noTyped.ToString());
        Assert.That(stress.ExitCode, Is.EqualTo(0), stress.ToString());
        Assert.That(on.Out, Is.EqualTo(off.Out), "sigspec on:\n" + on.Out + "\nsigspec off:\n" + off.Out);
        Assert.That(on.Out, Is.EqualTo(noTyped.Out), "typed off:\n" + on.Out + "\ntyped off sigspec:\n" + noTyped.Out);
        Assert.That(stress.Out, Is.EqualTo(off.Out), "stress:\n" + stress.Out + "\nsigspec off:\n" + off.Out);

        string src = Path.Combine(_dir, "sigspec.clj");
        File.WriteAllText(src, "(ns sigspec)\n(defn sig-dd [x n] (if (= n 0) x (sig-dd (* x 2.0) (dec n))))\n"
            + "(defn noisy [x n] (println x) (if (= n 0) x (noisy (inc x) (dec n))))\n"
            + "(defn rt4 [a b c d] (+ (+ (+ a b) c) d))\n"
            + "(defn rt-sum4 [a b c n] (if (= n 0) a (rt-sum4 (+ a b) (+ b c) (+ c n) (dec n))))\n");
        var r = HostProcessTests.CleatIn(_dir, null, new[] { ("CLEAT_NO_OPT", "typed"), ("CLEAT_DUMP_LCIL", "1") },
            "-I", _dir, "--compile-package", Path.Combine(_dir, "sigspec.dll"), "--package", "sigspec", "--root", _dir, "sigspec");
        Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
        string lcil = File.ReadAllText(Path.Combine(_dir, "sigspec.dll.lcil"));
        string Type(string name)
        {
            int start = lcil.IndexOf("(type \"sigspec$" + name + "\"", StringComparison.Ordinal);
            int end = lcil.IndexOf("\n  (type ", start + 1, StringComparison.Ordinal);
            return lcil.Substring(start, (end < 0 ? lcil.Length : end) - start);
        }
        string sigDd = Type("sig_dd");
        // cleat-137：不再预生成 LL / DD；cleat-139：运行时候选只在 eval 单元里生成（包里没有可用的 L3 登记），
        // 包程序集的函数类没有签名表字段，也不调 Install
        Assert.That(sigDd, Does.Not.Contain("ssKind"), sigDd);
        Assert.That(sigDd, Does.Not.Contain("invokeStaticTyped"), sigDd);
        string noisy = Type("noisy");
        Assert.That(noisy, Does.Not.Contain("invokeStaticTyped"), noisy);
        string rt4 = Type("rt4");
        Assert.That(rt4, Does.Not.Contain("__rtSpec"), rt4);
        Assert.That(rt4, Does.Not.Contain("RuntimeSigSpec"), rt4);

        string runtimeSrc = Path.Combine(_dir, "rtspec134.clj");
        string runtimeLcil = Path.Combine(_dir, "rtspec134.lcil");
        File.WriteAllText(runtimeSrc, "(ns rtspec134)\n(defn rt-array [xs] (aget xs 0))\n"
            + "(dotimes [_ 20] (rt-array (int-array [7])))\n(prn (rt-array (int-array [9])))\n");
        // 单次 aget 不在热区：这里验证机制，不看收益门槛
        var runtime = HostProcessTests.CleatIn(_dir, null, new[] { ("CLEAT_DUMP_RT_LCIL", runtimeLcil), t16, sync, ("CLEAT_RTSPEC_ALL", "1") }, runtimeSrc);
        Assert.That(runtime.ExitCode, Is.EqualTo(0), runtime.ToString());
        Assert.That(runtime.Out, Is.EqualTo("9\n"), runtime.ToString());
        Assert.That(File.Exists(runtimeLcil), runtime.ToString());
        Assert.That(File.ReadAllText(runtimeLcil),
            Does.Contain("(call \"clojure.lang.RT\" \"aget\" (\"System.Int32[]\" \"System.Int32\") \"System.Int32\")"),
            File.ReadAllText(runtimeLcil));

        string wideSrc = Path.Combine(_dir, "rtspec135.clj");
        string wideLcil = Path.Combine(_dir, "rtspec135.lcil");
        File.WriteAllText(wideSrc, "(ns rtspec135)\n"
            + "(defn rt6 [a b c d e n] (if (= n 0) a (rt6 (inc a) b c d e (dec n))))\n"
            + "(dotimes [_ 20] (rt6 1 2 3 4 5 3))\n(prn (rt6 2 3 4 5 6 2))\n");
        var wide = HostProcessTests.CleatIn(_dir, null, new[] { ("CLEAT_DUMP_RT_LCIL", wideLcil), t16, sync }, wideSrc);
        Assert.That(wide.ExitCode, Is.EqualTo(0), wide.ToString());
        Assert.That(wide.Out, Is.EqualTo("4\n"), wide.ToString());
        string wideText = File.ReadAllText(wideLcil);
        Assert.That(wideText, Does.Contain("(call \"rtSpec$"));
        Assert.That(wideText, Does.Contain(
            // cleat-138 形参掩码：b ~ e 只原样传给自调用，不进 key，保持 object
            "(\"System.Object\" \"System.Int64\" \"System.Object\" \"System.Object\" \"System.Object\" \"System.Object\" \"System.Int64\") \"System.Int64\")"));

        string tableSrc = Path.Combine(_dir, "rtspec-table.clj");
        File.WriteAllText(tableSrc, "(defn rt-multi [xs n] (if (= n 0) (aget xs 0) (rt-multi xs (dec n))))\n"
            + "(dotimes [_ 20] (rt-multi (int-array [3]) 2))\n(prn (rt-multi (int-array [7]) 1))\n"
            + "(dotimes [_ 24] (rt-multi (long-array [11]) 2))\n(prn (rt-multi (long-array [13]) 1))\n"
            + "(prn (rt-multi (int-array [17]) 1))\n");
        var table = HostProcessTests.CleatIn(_dir, null, new[] { t16 }, tableSrc);
        Assert.That(table.ExitCode, Is.EqualTo(0), table.ToString());
        Assert.That(table.Out, Is.EqualTo("7\n13\n17\n"), table.ToString());
    }

    /// <summary>
    /// cleat-139：O 模式整体特化——任意函数体（副作用、闭包、异常、集合、写数组）按观察到的形参类型重新生成，
    /// 以 object 通用体兜底。tests/Cleat.Host.Tests/data/rtspec_jit_semantics.clj 在默认、CLEAT_NO_OPT=sigspec、
    /// 压力模式（阈值 1）三种设置下输出逐字相同，默认设置确有运行时生成（CLEAT_RTSPEC_STATS）。
    /// </summary>
    [Test]
    public void WholeFunctionRuntimeSpecialization()
    {
        string script = Path.Combine(HostProcessTests.RepoDir, "tests", "Cleat.Host.Tests", "data", "rtspec_jit_semantics.clj");
        var on = HostProcessTests.CleatIn(_dir, null, new[] { ("CLEAT_RTSPEC_STATS", "1"), ("CLEAT_RTSPEC_SYNC", "1") }, script);
        var off = HostProcessTests.CleatIn(_dir, null, new[] { ("CLEAT_NO_OPT", "sigspec") }, script);
        // 压力模式：阈值 1 且不看收益门槛（CLEAT_RTSPEC_ALL），用例里的直线小函数也重新编译
        var stress = HostProcessTests.CleatIn(_dir, null, new[] { ("CLEAT_RTSPEC_THRESHOLD", "1"), ("CLEAT_RTSPEC_ALL", "1"), ("CLEAT_RTSPEC_STATS", "1"), ("CLEAT_RTSPEC_SYNC", "1") }, script);
        // 后台编译的压力运行：特化在调用进行中装入，只比较输出
        var stressAsync = HostProcessTests.CleatIn(_dir, null, new[] { ("CLEAT_RTSPEC_THRESHOLD", "1"), ("CLEAT_RTSPEC_ALL", "1") }, script);
        Assert.That(stressAsync.ExitCode, Is.EqualTo(0), stressAsync.ToString());
        Assert.That(stressAsync.Out, Is.EqualTo(off.Out), "async stress:\n" + stressAsync.Out + "\nsigspec off:\n" + off.Out);
        Assert.That(off.ExitCode, Is.EqualTo(0), off.ToString());
        Assert.That(on.ExitCode, Is.EqualTo(0), on.ToString());
        Assert.That(stress.ExitCode, Is.EqualTo(0), stress.ToString());
        Assert.That(on.Out, Is.EqualTo(off.Out), "default:\n" + on.Out + "\nsigspec off:\n" + off.Out);
        Assert.That(stress.Out, Is.EqualTo(off.Out), "stress:\n" + stress.Out + "\nsigspec off:\n" + off.Out);
        var m = System.Text.RegularExpressions.Regex.Match(on.Err, @"rtspec: compiled (\d+) failed (\d+)");
        Assert.That(m.Success, on.Err);   // "rtspec: compiled N failed M compile-ms T"
        Assert.That(int.Parse(m.Groups[1].Value), Is.GreaterThanOrEqualTo(8), on.Err);   // 收益门槛：有循环 / 自调用 / 数组的函数
        Assert.That(int.Parse(m.Groups[2].Value), Is.EqualTo(0), on.Err);
        var ms = System.Text.RegularExpressions.Regex.Match(stress.Err, @"rtspec: compiled (\d+) failed (\d+)");
        Assert.That(ms.Success, stress.Err);
        Assert.That(int.Parse(ms.Groups[1].Value), Is.GreaterThanOrEqualTo(25), stress.Err);
        Assert.That(int.Parse(ms.Groups[2].Value), Is.EqualTo(0), stress.Err);
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
    public void DeftypeLoopVariablesAreScalarised()
    {
        // cleat-123：循环变量与 let 临时对象的 deftype 标量替换（逃逸处才物化、一轮内同一个对象）、原始签名函数带转换的内联、
        // 循环中途重定义被内联的函数、可变字段、构造实参的转换与异常顺序、defrecord 不拆：CLEAT_NO_OPT=scalar 开 / 关逐字相同
        string script = Path.Combine(HostProcessTests.RepoDir, "tests", "Cleat.Host.Tests", "data", "scalar_semantics.clj");
        var on = HostProcessTests.CleatIn(_dir, null, null, script);
        var off = HostProcessTests.CleatIn(_dir, null, new[] { ("CLEAT_NO_OPT", "scalar") }, script);
        Assert.That(on.ExitCode, Is.EqualTo(0), on.ToString());
        Assert.That(on.Out, Is.EqualTo(off.Out), "scalar on:\n" + on.Out + "\nscalar off:\n" + off.Out);
        Assert.That(on.Out, Does.Contain("6 true true true false [0.0 0.0 1.0 1.0 2.0 2.0] 3.0 true"), on.ToString());
    }

    [Test]
    public void UnhintedArraysInLoopsAreSpeculated()
    {
        // cleat-127：循环里无提示的数组变量按元素类型推测（快份里的 aget / aset 决议到类型化重载）；推测不中、nil、越界、错误的值类型、
        // 闭包与嵌套循环在 CLEAT_NO_OPT=arrayspec 开 / 关两种设置下输出逐字相同
        string script = Path.Combine(HostProcessTests.RepoDir, "tests", "Cleat.Host.Tests", "data", "arrayspec_semantics.clj");
        var on = HostProcessTests.CleatIn(_dir, null, null, script);
        var off = HostProcessTests.CleatIn(_dir, null, new[] { ("CLEAT_NO_OPT", "arrayspec") }, script);
        Assert.That(on.ExitCode, Is.EqualTo(0), on.ToString());
        Assert.That(on.Out, Is.EqualTo(off.Out), "arrayspec on:\n" + on.Out + "\narrayspec off:\n" + off.Out);
        Assert.That(on.Out, Does.StartWith("6 2.0 9 3 6.5 3 200 1.5\n"), on.ToString());
        Assert.That(on.Out, Does.Contain("\n8\n"), on.ToString());
    }

    [Test]
    public void UserCallsInLoopsAreNotInlined()
    {
        // cleat-120：循环里的用户 wrapper 不再展开成带其他调用的内联体；wrapper 重定义、实参副作用与异常顺序仍逐字相同
        string script = Path.Combine(HostProcessTests.RepoDir, "tests", "Cleat.Host.Tests", "data", "loopinl_semantics.clj");
        var on = HostProcessTests.CleatIn(_dir, null, null, script);
        var off = HostProcessTests.CleatIn(_dir, null, new[] { ("CLEAT_NO_OPT", "loopinl") }, script);
        Assert.That(on.ExitCode, Is.EqualTo(0), on.ToString());
        Assert.That(off.ExitCode, Is.EqualTo(0), off.ToString());
        Assert.That(on.Out, Is.EqualTo(off.Out), "loopinl on:\n" + on.Out + "\nloopinl off:\n" + off.Out);
        Assert.That(on.Out, Is.EqualTo(File.ReadAllText(Path.ChangeExtension(script, ".out")).Replace("\r\n", "\n")), on.ToString());
    }

    [Test]
    public void EscapeReturnsMarkersAndFallsBackConservatively()
    {
        // cleat-125：直接 escape 调用返回 marker；普通值、跨函数传播、非尾逃逸、嵌套 owner、用户异常与保护区回退逐字不变
        string script = Path.Combine(HostProcessTests.RepoDir, "tests", "Cleat.Host.Tests", "data", "escape_semantics.clj");
        var on = HostProcessTests.CleatIn(_dir, null, null, script);
        Assert.That(on.ExitCode, Is.EqualTo(0), on.ToString());
        Assert.That(on.Out, Is.EqualTo(File.ReadAllText(Path.ChangeExtension(script, ".out")).Replace("\r\n", "\n")), on.ToString());
    }

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
