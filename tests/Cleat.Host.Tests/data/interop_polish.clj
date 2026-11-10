;; cleat-192：互操作打磨。ClojureCompilerTests.InteropPolish 核对输出。
(defn warnings [form]
  (let [w (System.IO.StringWriter.)]
    (binding [*err* w *warn-on-reflection* true] (eval form))
    (count (re-seq #"Reflection warning" (str w)))))

;; 1. 静态属性 / 字段的类型、Var 的 :tag（含实参是本进程 deftype 的泛型）、loop 局部按初值定类型
(def ^System.Threading.AutoResetEvent handle (System.Threading.AutoResetEvent. false))
(deftype Ent [x])
(def ^|System.Collections.Generic.SortedSet`1[user.Ent]| ents nil)
(prn :static-types
     (warnings '(defn p1 [] (.Ticks DateTime/UtcNow)))
     (warnings '(defn p2 [] (.Length String/Empty)))
     (warnings '(defn p3 [] (.WaitOne handle 0)))
     (warnings '(defn p4 [] (.Min ents)))
     (warnings '(defn p5 [] (loop [b (System.Text.StringBuilder.) i 0] (if (< i 3) (recur (.Append b i) (inc i)) (.ToString b)))))
     ((resolve (quote p5))))
;; fn 的 Var 的 :tag 是返回类型，不当作值的类型
(defn ^String named [] "x")
(prn :fn-var-tag (fn? named))

;; 2. 带类型实参的泛型短名
(ns polish.generic (:import [System.Collections.Generic |Dictionary`2[System.Object,System.Object]|]))
(prn :generic-short (.Count (doto (|Dictionary`2[System.String,System.Int64]|.) (.Add "a" 1))))
(in-ns 'user)

;; 3. 数组类符号：resolve、definterface 里本命名空间接口的数组、类型名决议不落到编译期桩
(definterface Foo (^String bar []))
(definterface Baz (^Foo/1 baz []))
(prn :arrays (= (resolve 'String/2) (Type/GetType "System.String[][]")) (= (resolve 'long/1) (class (long-array 0)))
     (try (resolve 'NoSuchTypeHere/2) (catch clojure.lang.TypeNotFoundException e :tnf))
     (= (.ReturnType (.GetMethod Baz "baz")) (.MakeArrayType Foo))
     (= (clojure.lang.RT/classForName "user.Foo[]") (.MakeArrayType Foo)))

;; 4. 限定方法作为值与 param-tags
(prn :method-values
     (map String/.ToUpper ["a" "b"])
     (map Math/Abs [-1 -2])
     (map ^[double] Math/Abs [-1.5])
     (map str (map ^[String] System.Text.StringBuilder/new ["q"]))
     (let [f0 ^[] clojure.lang.Tuple/create f1 ^[_] clojure.lang.Tuple/create] [(f0) (f1 1)])
     (map ^[_ _] clojure.lang.Tuple/create [1 2] [3 4])
     (map ^[System.Globalization.CultureInfo] String/.ToUpper ["a"] [System.Globalization.CultureInfo/InvariantCulture])
     ((:parse {:parse ^[String] Guid/Parse}) "00000000-0000-0001-0000-000000000002")
     Int64/MaxValue)
(prn :param-tags-call (^[float] Math/Abs -1.5) (warnings '(defn p6 [x] (^[long] Math/Abs x))))
(prn :bad (mapv #(try (eval %) (catch Exception e (boolean (re-find %2 (.Message e)))))
                ['String/foo 'String/.foo 'Math/new] [#"static method" #"instance method" #"constructor"]))
