using System;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace Cleat.Host.Tests;

/// <summary>
/// cleat-043：proxy、gen-class 生成真正的 CLR 子类（覆写基类的 public / protected 虚方法、按原签名转发构造函数），
/// ^:async fn / await*（运行时 async 与同步退化两种实现）。每个测试在自己的临时目录里以子进程运行宿主。
/// </summary>
[Category("Host")]
public class ClassGenerationTests
{
    private string _dir;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cleat-043-" + Guid.NewGuid().ToString("N").Substring(0, 8));
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

    private HostProcessTests.Result RunEnv((string, string)[] env, params string[] args) => HostProcessTests.CleatIn(_dir, null, env, args);

    private static string[] Lines(HostProcessTests.Result r) => r.Out.TrimEnd('\n').Split('\n');

    private const string Proxy = @"(ns t043.px (:require [clojure.pprint]))
(defn check []
  [(-> (get-proxy-class Object) construct-proxy (init-proxy {}) (update-proxy {""ToString"" (fn [_] ""chain"")}) str)
   (let [d (proxy [System.Collections.ArrayList] [[1 2 3]]
             (IndexOf [v s] (try (proxy-super IndexOf v s) (catch ArgumentOutOfRangeException e -2))))]
     [(.IndexOf d 2 0) (.IndexOf d 1 -1)])
   (try (.Write (proxy [System.IO.Stream] []) nil 1 1) (catch NotImplementedException e :abstract))
   (let [sb (StringBuilder.)
         w (proxy [System.IO.TextWriter] [] (get_Encoding [] System.Text.Encoding/UTF8) (Write ([x] (.Append sb (str ""<"" x "">"")))))]
     (.Write w ""a"") (.Write w \b) (.Write w 42) (str sb))
   (let [log (atom [])
         s (proxy [System.IO.MemoryStream] [] (Dispose [disposing] (swap! log conj disposing) (proxy-super Dispose disposing)))]
     (.Dispose s) @log)
   @(proxy [Object clojure.lang.IDeref] [] (deref [] 42))
   (.IsSubclassOf (class (proxy [System.IO.TextWriter] [])) System.IO.TextWriter)
   (keys (proxy-mappings (proxy [Object] [] (ToString [] ""x""))))
   (with-out-str (clojure.pprint/pprint {:a [1 2 3]}))])
";

    [Test]
    public void ProxyIsARealSubclass()
    {
        Write("src/t043/px.clj", Proxy);
        var r = Run("-I", "src", "-e", "(require 't043.px)", "-e", "(t043.px/check)");
        Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
        Assert.That(Lines(r).Last(),
            Is.EqualTo("[\"chain\" [1 -2] :abstract \"<a><b><42>\" [true] 42 true (\"ToString\") \"{:a [1 2 3]}\\n\"]"), r.ToString());
    }

    private const string GenClass = @"(ns t043.gc)
(definterface Greeter (greet [who]) (greet [a b]) (greet [a ^int b]))
(gen-class :name t043.gc.Example
           :implements [t043.gc.Greeter]
           :state state :init init :post-init post
           :constructors {[String] [] [String int] []}
           :methods [[hello [String] String] ^{:static true} [twice [int] int]]
           :factory create
           :exposes-methods {ToString baseToString})
(defn -init ([s] [[] (atom {:name s})]) ([s n] [[] (atom {:name s :n n})]))
(defn -post [this & args] (swap! (.state this) assoc :post (vec args)))
(defn -greet-Object [this who] (str ""hi "" who "" from "" (:name @(.state this))))
(defn -greet-Object-Object [this a b] ""o,o"")
(defn -greet-Object-Int32 [this a b] (str ""o,i "" b))
(defn -hello [this s] (str ""hello "" s))
(defn -twice [n] (* 2 n))
(defn -ToString [this] (str ""Example<"" (:name @(.state this)) "">""))
(gen-class :name t043.gc.Stream :extends System.IO.MemoryStream :prefix ""s-"" :main true
           :exposes-methods {Dispose baseDispose})
(defn s-Dispose [this disposing] (println ""dispose"" disposing) (.baseDispose this disposing))
(defn s-main [& args] (println ""main"" (vec args)))
(defn check []
  (let [e (t043.gc.Example. ""ann"") o (Object.)]
    [(.greet e 1) (.greet e o o) (.greet e o (int 3)) (.hello e ""x"") (str e) (t043.gc.Example/twice 21)
     (str (t043.gc.Example/create ""z"")) (:post @(.state (t043.gc.Example. ""b"" (int 2)))) (instance? t043.gc.Greeter e)
     (.Name (.FieldType (.GetField t043.gc.Example ""greet_Object_Int32__var"")))]))
";

    private const string GenClassResult =
        "[\"hi 1 from ann\" \"o,o\" \"o,i 3\" \"hello x\" \"Example<ann>\" 42 \"Example<z>\" [\"b\" 2] true \"Var\"]";

    [Test]
    public void GenClassFromSource()
    {
        Write("src/t043/gc.clj", GenClass);
        var r = Run("-I", "src", "-e", "(require 't043.gc)", "-e", "(t043.gc/check)",
                    "-e", "(.Dispose (t043.gc.Stream.))", "-e", "(t043.gc.Stream/Main (into-array String [\"a\" \"b\"]))");
        Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
        Assert.That(Lines(r), Is.EqualTo(new[] { GenClassResult, "dispose true", "main [a b]" }), r.ToString());
    }

    [Test]
    public void ProxyAndGenClassAotThenFreshProcess()
    {
        Write("src/t043/px.clj", Proxy);
        Write("src/t043/gc.clj", GenClass);
        var c = Run("-I", "src", "-e", "(binding [*compile-path* \"src\"] (compile 't043.px) (compile 't043.gc))");
        Assert.That(c.ExitCode, Is.EqualTo(0), c.ToString());
        Assert.That(File.Exists(Path.Combine(_dir, "src", "t043.gc.clj.dll")), Is.True, c.ToString());

        // 新进程：类型在 .clj.dll 里（不是动态程序集）
        var r = Run("-I", "src", "-e", "(require 't043.px 't043.gc)", "-e", "(t043.gc/check)",
                    "-e", "(.FullName (.Assembly t043.gc.Example))",
                    "-e", "(.FullName (.Assembly (class (proxy [System.IO.TextWriter] []))))");
        Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
        var lines = Lines(r);
        Assert.That(lines.Length, Is.EqualTo(3), r.ToString());
        Assert.That(lines[0], Is.EqualTo(GenClassResult), r.ToString());
        Assert.That(lines[1], Is.EqualTo("\"t043.gc.clj, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null\""), r.ToString());
        Assert.That(lines[2], Does.StartWith("\"eval$"), "a proxy at the REPL is generated in that evaluation's unit");

        // 先用类型、后有命名空间：类型初始化器加载实现命名空间（:load-impl-ns 缺省为真）
        string dll = Path.Combine(_dir, "src", "t043.gc.clj.dll").Replace("\\", "/");
        var l = Run("-I", "src", "-e",
            $"(let [t (.GetType (System.Reflection.Assembly/LoadFrom \"{dll}\") \"t043.gc.Example\")] [(str (Activator/CreateInstance t (object-array [\"q\"]))) (contains? @@#'clojure.core/*loaded-libs* 't043.gc)])");
        Assert.That(l.ExitCode, Is.EqualTo(0), l.ToString());
        Assert.That(Lines(l).Last(), Is.EqualTo("[\"Example<q>\" true]"), l.ToString());
    }

    private const string Async = @"(ns t043.as (:require [clojure.clr.async.task.alpha :as t]))
(defn ^:async fetch [n] (t/await (t/delay-task 20)) (* n 10))
(defn ^:async chain [] (+ (t/await (fetch 1)) (t/await (fetch 2))))
(defn ^:async boom [] (t/await (t/delay-task 5)) (throw (InvalidOperationException. ""boom"")))
(defn ^:async tryit [] (try (t/await (boom)) (catch InvalidOperationException e (str ""caught "" (.Message e)))))
(defn check []
  [(t/result (chain)) (t/result (tryit)) (try (t/result (boom)) (catch InvalidOperationException e :rethrown))
   (t/task? (fetch 1)) (t/result (t/async (t/await (t/->task 5))))])
(defn concurrent-ms []
  (let [sw (System.Diagnostics.Stopwatch/StartNew)]
    (doall (t/wait-all-results (doall (repeatedly 100 #(fetch 1)))))
    (.ElapsedMilliseconds sw)))
";

    [Test]
    public void AsyncFallbackAwaitsSynchronously()
    {
        Write("src/t043/as.clj", Async);
        var r = RunEnv(new[] { ("CLEAT_RUNTIME_ASYNC", "0") }, "-I", "src", "-e", "(require 't043.as)",
                       "-e", "(Cleat.Scheme.Clojure.AsyncSupport/RuntimeAsync)", "-e", "(t043.as/check)");
        Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
        Assert.That(Lines(r), Is.EqualTo(new[] { "false", "[30 \"caught boom\" :rethrown true 5]" }), r.ToString());
    }

    [Test]
    public void AsyncOnRuntimeAsyncSuspends()
    {
        Write("src/t043/as.clj", Async);
        var r = RunEnv(new[] { ("DOTNET_RuntimeAsync", "1") }, "-I", "src", "-e", "(require 't043.as)",
                       "-e", "(Cleat.Scheme.Clojure.AsyncSupport/RuntimeAsync)", "-e", "(t043.as/check)", "-e", "(t043.as/concurrent-ms)");
        Assert.That(r.ExitCode, Is.EqualTo(0), r.ToString());
        var lines = Lines(r);
        if (lines[0] != "true") Assert.Ignore("runtime async is not available on this runtime");
        Assert.That(lines[1], Is.EqualTo("[30 \"caught boom\" :rethrown true 5]"), r.ToString());
        // 100 个各等 20 ms 的 fetch 并发挂起：远少于顺序等待的 2 秒
        Assert.That(long.Parse(lines[2]), Is.LessThan(1000), r.ToString());
    }

    [Test]
    public void AwaitOutsideAsyncIsACompileError()
    {
        var r = Run("-e", "(fn [] (await* (System.Threading.Tasks.Task/Delay 1)))");
        Assert.That(r.ExitCode, Is.Not.EqualTo(0), r.ToString());
        Assert.That(r.Err, Does.Contain("(await* ...) can only be used inside a ^:async function or (async ...) block"), r.ToString());
        var c = Run("-e", "(^:async fn* [] (try 1 (catch Exception e (await* (System.Threading.Tasks.Task/Delay 1)))))");
        Assert.That(c.Err, Does.Contain("(await* ...) cannot appear inside a catch or finally clause"), c.ToString());
    }
}
