;; 代码生成：L3 → Lcil（cleat-088 起，移植原版 codegen.sls（"原版"指 cleat-093 前仓库里的 Scheme 实现 src/scheme，已删除，见 git 历史）；cleat-092 起只产出 Clojure 的运行时契约：
;; 生成的代码只引用 Cleat.Core 与 clojure.lang，常量表是 Clojure 数据，try 的处理器内联进 catch 块，原版的过程类、共享持有类、
;; 原语与数据都不再出现）。
;;
;; 逐函数照原版（各函数注明对应的原版名字）。只有 Clojure 输入用得到的节点（cleat-085 的普查；原版专有的节点 cleat-092 从语言里删去）。
;;
;; Lcil 是 Clojure 的列表数据（符号、字符串、整数、浮点、布尔），直接交给 Cleat.Emit（ClojureData）；lcil->string 写成文本（转储与差分用）。
(ns cleat.compiler.codegen
  (:require [clojure.string :as str]
            [cleat.nanopass :refer [nanopass-case define-pass with-output-language out]]
            [cleat.nanopass.records :as r]
            [cleat.compiler.languages :as l :refer [L3]])
  (:import [System.Collections Hashtable]))

;; —— 小工具 ——

(defn- i "指令 / Lcil 列表" [& xs] (apply list xs))
(defn- ht [] (Hashtable.))
(defn- hget [^Hashtable h k] (.get_Item h k))
(defn- hput! [^Hashtable h k v] (.set_Item h k v))
(defn- hhas? [^Hashtable h k] (.ContainsKey h k))
(defn- hdel! [^Hashtable h k] (.Remove h k))
(defn- hupdate! [^Hashtable h k f dflt] (hput! h k (f (let [v (hget h k)] (if (nil? v) dflt v)))))
(defn- hsize [^Hashtable h] (.Count h))
(defn- hclear! [^Hashtable h] (.Clear h))
(defn- keys-of [^Hashtable h] (vec (.Keys h)))

;; 目标的类型名（targets.sls 的 clr 表项）
(def obj "System.Object")
(def obj-array "System.Object[]")
(def int32-type "System.Int32")
(def int64-type "System.Int64")
(def double-type "System.Double")
(def bool-type "System.Boolean")
(def char-type "System.Char")
(def string-type "System.String")
(def void-type "System.Void")
(def exception-type "System.Exception")
(def box-type "Cleat.Core.Runtime.Box")
(def truth-type "Cleat.Core.Runtime.Truth")
(def ifn-type "clojure.lang.IFn")
(def clj-var-type "clojure.lang.Var")
(def ilookup-type "clojure.lang.ILookup")
(def clj-util-type "clojure.lang.Util")
(def clj-rt-type "clojure.lang.RT")
(def afunction-type "clojure.lang.AFunction")
(def restfn-type "clojure.lang.RestFn")
(def pmap-type "clojure.lang.IPersistentMap")
(def iobj-type "clojure.lang.IObj")
(def clojure-ops-type "Cleat.Core.Runtime.ClojureOps")
(def type-type "System.Type")
(def type-handle-type "System.RuntimeTypeHandle")
(def static-calls-type "Cleat.Core.Runtime.ClojureStaticCalls")
(def core-assembly "Cleat.Core")
(def target-tail-calls? true)
(def target-clojure-tail-calls? false)

(defn objs [n] (repeat n obj))

;; —— 测量开关：CLEAT_NO_OPT=1 关掉全部，或含 known / inline / infer 关掉对应的一项 ——

(defn- env [n] (or (System.Environment/GetEnvironmentVariable n) ""))
(def ^:private no-opt (env "CLEAT_NO_OPT"))
(defn- opt-off? [nm] (or (= no-opt "1") (str/includes? no-opt nm)))
(def opt-034? (not (opt-off? "known")))
(def opt-inline? (not (opt-off? "inline")))
(def opt-infer? (not (opt-off? "infer")))

;; —— 编译单元（原版 unit）——

(defn- make-unit [assembly-name module-name]
  {:name assembly-name :module module-name
   :classes (atom []) :consts (atom []) :nconsts (atom 0) :const-index (ht)
   :namespaces (atom []) :references (atom []) :counter (atom 0) :type-names (ht)})

(defn- add-class! [u c] (swap! (:classes u) conj c))

(defn- next-id! [u] (let [n @(:counter u)] (swap! (:counter u) inc) n))

;; —— 常量表（原版 constant-index!）——
;; Hashtable 按 Equals 比较：数、字符、字符串、关键字、布尔按值去重；其余只有同一个对象才共用下标

(defn- neg-zero? [x] (and (instance? Double x) (zero? (double x)) (neg? (/ 1.0 (double x)))))
(defn- signed-zero-constant? [d] (neg-zero? d))

(defn- equals-dedupable?
  "按值合并的常量：数、字符、字符串、关键字、布尔；符号与集合可能带元数据、identical? 有意义，只有同一个对象才共用下标。"
  [d]
  (or (number? d) (char? d) (string? d) (keyword? d) (boolean? d)))

(defn- constant-index! [u d]
  (let [hit (and (not (signed-zero-constant? d)) (hget (:const-index u) d))]
    (if (and hit (or (Object/ReferenceEquals (second hit) d) (equals-dedupable? d)))
      (first hit)
      (let [k @(:nconsts u)]
        (when-not (or (signed-zero-constant? d) hit) (hput! (:const-index u) d [k d]))
        (swap! (:consts u) conj d)
        (swap! (:nconsts u) inc)
        k))))

(defn- sanitize [s]
  (apply str (map (fn [c]
                    (if (or (Char/IsLetter ^char c) (Char/IsDigit ^char c) (#{\$ \_ \- \? \! \< \> \= \* \%} c))
                      (if (#{\* \< \>} c) \_ c)
                      \_))
                  (str s))))

(defn- pretty [x]
  (let [s (str x)]
    (if (and (> (count s) 2) (str/starts-with? s "g$"))
      (let [j (str/index-of s "$" 2)] (if j (subs s 2 j) s))
      s)))

;; —— 方法上下文（原版 mctx）——

(defn- make-method-ctx [u class static? loop-label loop-arity self-mref]
  {:unit u :class class :static? static? :vars (ht) :locals (atom []) :buffer (atom []) :nlabels (atom 0) :try-depth (atom 0)
   :loop-label loop-label :loop-arity loop-arity :self-mref self-mref :arg-base (if static? 0 1) :labels (ht)
   :big? (atom false) :ret-type (atom nil) :uses (ht) :pinned (ht)})

(defn- emit! [mc & ins] (swap! (:buffer mc) into ins))
(defn- emit-code! [mc code] (swap! (:buffer mc) into code))

(defn- new-label! [mc]
  (let [n @(:nlabels mc)] (swap! (:nlabels mc) inc) (symbol (str "L" n))))

(defn- new-local! [mc type]
  (let [n (count @(:locals mc))] (swap! (:locals mc) conj type) n))

(defn- with-buffer
  "在单独的缓冲区中生成代码，返回指令向量。"
  [mc thunk]
  (let [saved @(:buffer mc)]
    (reset! (:buffer mc) [])
    (thunk)
    (let [code @(:buffer mc)]
      (reset! (:buffer mc) saved)
      code)))

(defn- in-try [mc thunk]
  (swap! (:try-depth mc) inc)
  (let [code (with-buffer mc thunk)]
    (swap! (:try-depth mc) dec)
    code))

(defn- contains-try? [code] (some #(= 'try (first %)) code))

(defn- branches-to? [code l]
  (some (fn [x] (and (seq? x) (or (and (= 'br (first x)) (= l (second x))) (branches-to? x l)))) code))

(defn- reload-cast [code]
  (if (empty? code)
    []
    (let [last-i (last code)]
      (cond
        (and (seq? last-i) (= 'castclass (first last-i))) [last-i]
        :else []))))

(defn- code-result-type [code]
  (if (empty? code)
    obj
    (let [x (last code)]
      (case (first x)
        (call callvirt) (let [rt (last x)] (if (and (string? rt) (#{int32-type double-type} rt)) rt obj))
        unbox.any (let [t (second x)] (if (#{int32-type double-type} t) t obj))
        ldc.i4 int32-type
        ldc.r8 double-type
        obj))))

(declare ldloc-clear! ref-var!)

(defn- operands!
  "依次生成多个操作数；排在后面的操作数含 try 时先全部存入临时变量（lift-try）。"
  [mc gens]
  (let [codes (mapv #(with-buffer mc %) gens)]
    (if (and (seq codes) (some contains-try? (rest codes)))
      (let [temps (mapv (fn [c] (let [t (new-local! mc (code-result-type c))] (emit-code! mc c) (emit! mc (i 'stloc t)) [t c])) codes)]
        (doseq [[t c] temps]
          (emit! mc (i 'ldloc t))
          (when (= (code-result-type c) obj)
            (emit-code! mc (reload-cast c)))))
      (doseq [c codes] (emit-code! mc c)))))

(defn- pack-array! [mc n]
  (let [temps (vec (reverse (for [_ (range n)] (new-local! mc obj))))]
    ;; temps 为逆序：最后压栈的值先出栈
    (doseq [t temps] (emit! mc (i 'stloc t)))
    (emit! mc (i 'ldc.i4 n) (i 'newarr obj))
    (doseq [[j t] (map-indexed vector (reverse temps))]
      (emit! mc (i 'dup) (i 'ldc.i4 j))
      (ldloc-clear! mc t)
      (emit! mc (i 'stelem.ref)))))

;; —— 结果按上下文收尾 ——

(defn- finish-value! [mc ctx]
  (case ctx
    :value nil
    :effect (emit! mc (i 'pop))
    :test (emit! mc (i 'call truth-type "IsTrue" (i obj) bool-type))
    :tail (do (case @(:ret-type mc)
                :l64 (emit! mc (i 'call static-calls-type "PrimRetLong" (i obj) int64-type))
                :fl (emit! mc (i 'call static-calls-type "PrimRetDouble" (i obj) double-type))
                nil)
              (emit! mc (i 'ret)))))

(defn- finish-bool! [mc ctx]
  (case ctx
    :test nil
    :effect (emit! mc (i 'pop))
    :value (emit! mc (i 'call truth-type "Bool" (i bool-type) obj))
    :tail (do (emit! mc (i 'call truth-type "Bool" (i bool-type) obj)) (finish-value! mc :tail))))

(defn- finish-none! [mc ctx]
  (case ctx
    :effect nil
    :test (emit! mc (i 'ldc.i4 1))
    :value (emit! mc (i 'ldnull))
    :tail (do (emit! mc (i 'ldnull)) (finish-value! mc :tail))))

(defn- tail-ok? [mc ctx]
  (and (= ctx :tail) (not @(:ret-type mc)) (zero? @(:try-depth mc)) target-tail-calls?))

;; —— 常量 ——

(defn- bmp-safe-string? [^String s]
  (let [n (count s)]
    (loop [j 0]
      (if (= j n)
        true
        (let [k (int (.get_Chars s j))]
          (cond
            (and (<= 0xD800 k 0xDBFF) (< (inc j) n) (<= 0xDC00 (int (.get_Chars s (inc j))) 0xDFFF)) (recur (+ j 2))
            (<= 0xD800 k 0xDFFF) false
            :else (recur (inc j))))))))

(defn- load-constant! [mc d]
  (cond
    (instance? Int32 d) (emit! mc (i 'ldc.i4 d) (i 'box int32-type))   ; 读入器给元数据的行号等；Clojure 的整数是 long
    (boolean? d) (emit! mc (i 'ldsfld truth-type (if d "True" "False")))
    (nil? d) (emit! mc (i 'ldnull))
    (and (char? d) (< (int d) 0x10000)) (emit! mc (i 'ldc.i4 (int d)) (i 'box char-type))
    (and (string? d) (bmp-safe-string? d)) (emit! mc (i 'ldstr d))
    :else (emit! mc (i 'ldconst (constant-index! (:unit mc) d)))))

;; —— Lcil 文本（lcil.sls write-lcil、constants.sls write-lcil-datum）——

(defn- flonum-digits
  "最短往返的十进制数字与指数 [数字串 k]：|d| = 0.数字 × 10^k（boot/numbers.sls 的 get-digits；.NET 的 R 格式同为最短往返）。"
  [^double d]
  (let [s (.ToString (Math/Abs d) "R" System.Globalization.CultureInfo/InvariantCulture)
        [m e] (str/split s #"E")
        ex (if e (parse-long (str/replace e "+" "")) 0)
        pi (or (str/index-of m ".") (count m))
        ds (str/replace m "." "")
        lead (count (take-while #(= % \0) ds))
        ds (subs ds lead)
        ds (str/replace ds #"0+$" "")]
    [ds (- (+ pi ex) lead)]))

(defn- flonum->string
  "原版的 number->string（flonum）：0 ≤ k ≤ 9 普通十进制，-3 ≤ k ≤ 0 写 0.00ddd，其余写指数 d.ddde±x（一位有效数字写 1e23），
  特殊值 +nan.0 / +inf.0 / -inf.0。"
  [^double d]
  (cond
    (zero? d) (if (neg? (/ 1.0 d)) "-0.0" "0.0")
    (Double/IsNaN d) "+nan.0"
    (Double/IsPositiveInfinity d) "+inf.0"
    (Double/IsNegativeInfinity d) "-inf.0"
    :else
    (let [[ds k] (flonum-digits d)
          n (count ds)
          body (cond
                 (<= 0 k 9) (cond
                              (<= n k) (str ds (apply str (repeat (- k n) "0")) ".0")
                              (zero? k) (str "0." ds)
                              :else (str (subs ds 0 k) "." (subs ds k)))
                 (<= -3 k 0) (str "0." (apply str (repeat (- k) "0")) ds)
                 :else (str (subs ds 0 1) (when (> n 1) (str "." (subs ds 1))) "e" (dec k)))]
      (if (neg? d) (str "-" body) body))))

(defn- write-lcil-string [^System.Text.StringBuilder sb ^String s]
  (.Append sb \")
  (let [n (count s)]
    (loop [j 0]
      (when (< j n)
        (let [c (.get_Chars s j) k (int c)]
          (cond
            (= c \") (do (.Append sb "\\\"") (recur (inc j)))
            (= c \\) (do (.Append sb "\\\\") (recur (inc j)))
            (<= 32 k 126) (do (.Append sb c) (recur (inc j)))
            (and (<= 0xD800 k 0xDBFF) (< (inc j) n) (<= 0xDC00 (int (.get_Chars s (inc j))) 0xDFFF))
            (do (.Append sb (str "\\x" (Convert/ToString (int (+ 0x10000 (* (- k 0xD800) 0x400) (- (int (.get_Chars s (inc j))) 0xDC00))) (int 16)) ";"))
                (recur (+ j 2)))
            :else (do (.Append sb (str "\\x" (Convert/ToString k (int 16)) ";")) (recur (inc j))))))))
  (.Append sb \"))

(defn- write-lcil-datum [^System.Text.StringBuilder sb x]
  (cond
    (or (seq? x) (vector? x)) (if (empty? x)
                                (.Append sb "()")
                                (do (.Append sb \()
                                    (loop [xs (seq x) first? true]
                                      (when xs
                                        (when-not first? (.Append sb \space))
                                        (write-lcil-datum sb (first xs))
                                        (recur (next xs) false)))
                                    (.Append sb \))))
    (string? x) (write-lcil-string sb x)
    (symbol? x) (.Append sb (str x))
    (true? x) (.Append sb "#t")
    (false? x) (.Append sb "#f")
    (instance? Double x) (.Append sb (flonum->string x))
    (integer? x) (.Append sb (str x))
    :else (throw (ex-info (str "codegen: cannot write as Lcil: " (pr-str x)) {:datum x}))))

(def ^:private block-heads #{'lcil 'constants 'type 'method 'body 'try 'catch 'finally})

(defn lcil->string
  "Lcil 的 S 表达式 → 文本（lcil.sls lcil->string：块的子项各占一行，便于 diff）。"
  [x]
  (let [sb (System.Text.StringBuilder.)]
    (letfn [(w [x indent]
              (if (and (seq? x) (block-heads (first x)))
                (let [inner (+ indent 2)]
                  (.Append sb \()
                  (write-lcil-datum sb (first x))
                  (loop [xs (rest x)]
                    (cond
                      (empty? xs) (.Append sb \))
                      (and (not (or (seq? (first xs)) (vector? (first xs)))))
                      (do (.Append sb \space) (write-lcil-datum sb (first xs)) (recur (rest xs)))
                      :else (do (doseq [c xs]
                                  (.Append sb \newline)
                                  (.Append sb (String. \space (int inner)))
                                  (w c inner))
                                (.Append sb \))))))
                (write-lcil-datum sb x)))]
      (w x 0)
      (.Append sb \newline)
      (str sb))))

;; —— 每个编译单元重算的表（原版同名）——
(def ^:private var-types (ht))      ; 变量 → :fx / :fl / :l64（拆箱）
(def ^:private sound-types (ht))    ; 推断收敛后的类型（宿主调用按它选重载）
(def ^:private fixed-types (ht))    ; 原始签名子句的 L / D 形参
(def ^:private var-facts (ht))      ; 变量的已知类型（不改变存储）
(def ^:private assigned-vars (ht))  ; 被 set! 的局部变量

;; —— 类型格（原版 iron-071 的格元素；Clojure 输入只用到其中几种，加上 cleat-042 的 :l64）——

(def ^:private type-parents {:fx :int :int :real :fl :real :real :num})
(defn- type-ancestors [t] (if t (cons t (type-ancestors (type-parents t))) ()))
(defn- type-join [a b]
  (cond (= a :top) b
        (= b :top) a
        (not (and a b)) nil
        :else (let [bs (set (type-ancestors b))] (first (filter bs (type-ancestors a))))))
(defn- meet-type [a b] (type-join a b))

(defn- datum-type
  "常量的类型：Int32 是 fx（原版的 fixnum），Double 是 fl，long / 大整数是 int，其余数是 real。"
  [d]
  (cond (instance? Int32 d) :fx
        (instance? Double d) :fl
        (or (instance? Int64 d) (instance? System.Numerics.BigInteger d) (instance? clojure.lang.BigInt d)) :int
        (number? d) :real
        (boolean? d) :bool
        (char? d) :char
        :else nil))

(def ^:private no-datum (Object.))

(defn- quoted-datum [e]
  (nanopass-case [L3 Expr] e [(quote ~d) d] [else no-datum]))

;; Numbers 的 int64 算术（cleat-042）
(def ^:private numbers-long-ops #{"add" "minus" "multiply" "inc" "dec"})
(def ^:private numbers-bool-ops #{"lt" "lte" "gt" "gte" "equiv"})

(defn- numbers-type? [tp]
  (or (= tp "clojure.lang.Numbers") (and (instance? Type tp) (= (.FullName ^Type tp) "clojure.lang.Numbers"))))

(defn- empty-gen? [g] (or (nil? g) (false? g)))

(defn- numbers-arith-op [d0 d1 e*]
  (when (and (= d0 "CallStatic") (= d1 3) (<= 4 (count e*) 5) (numbers-type? (quoted-datum (first e*))))
    (let [op (quoted-datum (second e*)) gen (quoted-datum (nth e* 2)) argc (- (count e*) 3)]
      (when (and (string? op) (empty-gen? gen)
                 (cond (#{"inc" "dec"} op) (= argc 1)
                       (= op "minus") true
                       (numbers-long-ops op) (= argc 2)
                       (numbers-bool-ops op) (= argc 2)
                       :else false))
        op))))

(declare expr-type host-call-signature)

(defn- numbers-arith-args [e env]
  (nanopass-case [L3 Expr] e
    [(clj-call ~d0 ~d1 ~e* ...)
     (let [op (numbers-arith-op d0 d1 e*)]
       (when (and op (every? #(= :l64 (expr-type % env)) (drop 3 e*)))
         (cons op (drop 3 e*))))]
    [else nil]))

;; 原始签名（cleat-042）
(def primifs-prefix "clojure.lang.primifs.")
(defn- sig-char-type [c] (case c \L int64-type \D double-type obj))
(defn- sig-char-raw [c] (case c \L :l64 \D :fl nil))
(defn- sig-params [^String sig] (seq (subs sig 0 (dec (count sig)))))
(defn- sig-ret [^String sig] (nth sig (dec (count sig))))
(defn- sig-param-types [sig] (map sig-char-type (sig-params sig)))

(declare host-call-type)

(defn expr-type
  "表达式的类型（:fx :fl :int … 与 :l64）或 nil；env：变量 → 类型 | :top。不在 env 里的变量查 var-facts（原版 expr-type 的 Clojure 子集）。"
  [e env]
  (nanopass-case [L3 Expr] e
    [(quote ~d) (if (instance? Int64 d) :l64 (datum-type d))]
    [(clj-call ~d0 ~d1 ~e* ...)
     (if (= d0 "PrimInvoke")
       (sig-char-raw (sig-ret (quoted-datum (first e*))))
       (let [op (numbers-arith-op d0 d1 e*)
             ts (when (and op (numbers-long-ops op)) (map #(expr-type % env) (drop 3 e*)))]
         (cond (and ts (every? #(= :l64 %) ts)) :l64
               (and ts (every? #{:l64 :top} ts)) :top
               :else (host-call-type d0 d1 e* env))))]
    [~x (if (hhas? env x) (hget env x) (hget var-facts x))]
    [(if ~e0 ~e1 ~e2) (type-join (expr-type e1 env) (expr-type e2 env))]
    [(begin ~e* ... ~e) (expr-type e env)]
    [(let ([~x* ~e*] ...) ~body) (expr-type body env)]
    [(jump ~x ~e* ...) :top]
    [(throw ~e) :top]
    [else nil]))

;; —— 宿主调用的静态决议（cleat-042 第 3 项，原版 host-call-*）——

(defn- host-call-layout
  "[static? 实例或nil 实参]，不是 CallStatic / CallInstance / GetStaticField / GetInstanceField 时 nil。"
  [d0 d1 e*]
  (cond (and (= d0 "CallStatic") (= d1 3) (>= (count e*) 3)) [true nil (drop 3 e*)]
        (and (= d0 "CallInstance") (= d1 4) (>= (count e*) 4)) [false (nth e* 2) (drop 4 e*)]
        (and (= d0 "GetStaticField") (false? d1) (= (count e*) 2)) [true nil ()]
        (and (= d0 "GetInstanceField") (false? d1) (= (count e*) 3)) [false (nth e* 2) ()]
        :else nil))

(declare static-arg-type)

(defn- host-call-signature
  "静态决议：[签名 static? 实例 实参] / :pending / nil。签名为 ClojureStaticCalls.Resolve 的结果 object[]。env 缺省用 sound-types。"
  ([d0 d1 e*] (host-call-signature d0 d1 e* nil))
  ([d0 d1 e* env]
   (let [layout (host-call-layout d0 d1 e*)
         env (if (or (nil? env) (identical? env var-types)) sound-types env)]
     (when layout
       (let [t (quoted-datum (first e*))
             nm (quoted-datum (second e*))
             gen (if (#{"CallStatic" "CallInstance"} d0) (quoted-datum (nth e* (if (first layout) 2 3))) nil)]
         (when (and (instance? Type t) (string? nm) (empty-gen? gen))
           (let [ats (mapv #(static-arg-type % env) (nth layout 2))]
             (if (some #{:pending} ats)
               :pending
               (let [^objects sig (if (#{"CallStatic" "CallInstance"} d0)
                                    (Cleat.Core.Runtime.ClojureStaticCalls/Resolve t nm (object-array ats) (first layout))
                                    (Cleat.Core.Runtime.ClojureStaticCalls/ResolveField t nm (first layout)))]
                 (when (pos? (alength sig)) (into [sig] layout)))))))))))

(defn- host-call-type [d0 d1 e* env]
  (let [sl (host-call-signature d0 d1 e* env)]
    (cond (= sl :pending) :top
          (nil? sl) nil
          :else (case (str (aget ^objects (first sl) 3)) "System.Int64" :l64 "System.Double" :fl nil))))

(defn- static-arg-type [e env]
  (let [by-type #(case (expr-type e env) :l64 "System.Int64" :fl "System.Double" :top :pending nil)]
    (nanopass-case [L3 Expr] e
      [(quote ~d) (cond (instance? Int64 d) "System.Int64"
                        (instance? Int32 d) "System.Int32"
                        (instance? Double d) "System.Double"
                        (string? d) "System.String"
                        :else nil)]
      [(clj-call ~d0 ~d1 ~e* ...)
       (if (= d0 "CallStatic")
         (let [sl (host-call-signature d0 d1 e* env)]
           (cond (= sl :pending) :pending
                 (and sl (#{"System.Int32" "System.Boolean" "System.Char" "System.String"} (str (aget ^objects (first sl) 3))))
                 (str (aget ^objects (first sl) 3))
                 :else (by-type)))
         (by-type))]
      [else (by-type)])))

;; —— 推断（iron-034c，原版 infer-walk / infer-var-types-into!，Clojure 子集）——

(defn- inlined-binding? [x] (str/ends-with? (str x) "$inl"))

(declare fix-clause-params!)

(define-pass infer-walk L3 [ir add-def! exclude! labels candidate? use!] -> * []
  (MaybeTyped Expr [ir] -> * []
    [(quote ~d) (or (instance? Int64 d) (boolean (datum-type d)))]
    [~x (candidate? x)]
    [(clj-call ~d0 ~d1 ~e* ...) (or (= d0 "PrimInvoke") (boolean (host-call-layout d0 d1 e*)))]
    [(if ~e0 ~e1 ~e2) (or (MaybeTyped e1) (MaybeTyped e2))]
    [(begin ~e* ... ~e) (MaybeTyped e)]
    [(let ([~x* ~e*] ...) ~body) (MaybeTyped body)]
    [(jump ~x ~e* ...) true]
    [(throw ~e) true]
    [else false])
  (Body Clause [ir] -> * []
    [(clause (~x* ...) ~x ~body) (Expr body)])
  (Raw Expr [ir] -> * []
    [~x (use! x true)]
    [else (Expr ir)])
  (Expr [ir] -> * []
    [~x (use! x false)]
    [(quote ~d) nil]
    [(if ~e0 ~e1 ~e2) (Expr e0) (Expr e1) (Expr e2)]
    [(begin ~e* ... ~e) (run! Expr e*) (Expr e)]
    [(set! ~x ~e) (exclude! x) (Expr e)]
    [(lambda ~nm (~x* ...) ~cl* ...)
     (run! exclude! x*)
     (run! #(fix-clause-params! nm %) cl*)
     (run! Body cl*)]
    [(letrec ([~x* ~le*] ...) ~body) (run! Expr le*) (Expr body)]
    [(let ([~x* ~e*] ...) ~body)
     (doseq [[x e] (map vector x* e*)] (when (MaybeTyped e) (add-def! x e)))
     (run! Raw e*)
     (Expr body)]
    [(call ~e ~e* ...) (Expr e) (run! Expr e*)]
    [(throw ~e) (Expr e)]
    [(try-finally ~e0 ~e1) (Expr e0) (Expr e1)]
    [(try-catch ~e (~d* ~e*) ...) (Expr e) (run! Expr e*)]
    [(label ~x (~x* ...) ~body0 ~body1)
     (hput! labels x x*)
     (run! #(add-def! % nil) x*)
     (Expr body0) (Expr body1)]
    [(jump ~x ~e* ...)
     (when-let [ps (hget labels x)] (doseq [[p e] (map vector ps e*)] (add-def! p e)))
     (run! Raw e*)]
    [(box ~e) (Expr e)]
    [(set-box! ~x ~e) (Expr e)]
    [(clj-var-set! ~d ~e) (Expr e)]
    [(clj-def ~d ~st0 ~st1 ~e0 ~e1) (Expr e0) (Expr e1)]
    [(clj-invoke ~e ~e* ...) (Expr e) (run! Expr e*)]
    [(clj-keyword-invoke ~d ~e ~e* ...) (Expr e) (run! Expr e*)]
    [(clj-call ~d0 ~d1 ~e* ...)
     (cond
       (numbers-arith-op d0 d1 e*) (run! Raw (drop 3 e*))
       (= d0 "PrimInvoke") (do (Expr (second e*))
                               (doseq [[a c] (map vector (drop 2 e*) (sig-params (quoted-datum (first e*))))]
                                 (if (sig-char-raw c) (Raw a) (Expr a))))
       :else (if-let [lay (host-call-layout d0 d1 e*)]
               (do (when (second lay) (Expr (second lay))) (run! Raw (nth lay 2)))
               (run! Expr e*)))]
    [(clj-static-call ~d0 ~d1 ~e* ...) (run! Expr e*)]
    [(check-initialized ~e) (Expr e)]
    [(clj-case ~d ~e0 ~e1 ~e* ...) (Expr e0) (Expr e1) (run! Expr e*)]
    [(clj-deftype ~d (~x* ...) ~le* ...) (run! exclude! x*) (run! Expr le*)]
    [else nil])
  (Expr ir))

(define-pass collect-assigned! L3 [ir] -> * []
  (Body Clause [ir] -> * []
    [(clause (~x* ...) ~x ~body) (Expr body)])
  (Expr [ir] -> * []
    [(set! ~x ~e) (hput! assigned-vars x true) (Expr e)]
    [(if ~e0 ~e1 ~e2) (Expr e0) (Expr e1) (Expr e2)]
    [(begin ~e* ... ~e) (run! Expr e*) (Expr e)]
    [(lambda ~nm (~x* ...) ~cl* ...) (run! Body cl*)]
    [(letrec ([~x* ~le*] ...) ~body) (run! Expr le*) (Expr body)]
    [(let ([~x* ~e*] ...) ~body) (run! Expr e*) (Expr body)]
    [(call ~e ~e* ...) (Expr e) (run! Expr e*)]
    [(throw ~e) (Expr e)]
    [(try-finally ~e0 ~e1) (Expr e0) (Expr e1)]
    [(try-catch ~e (~d* ~e*) ...) (Expr e) (run! Expr e*)]
    [(label ~x (~x* ...) ~body0 ~body1) (Expr body0) (Expr body1)]
    [(jump ~x ~e* ...) (run! Expr e*)]
    [(box ~e) (Expr e)]
    [(set-box! ~x ~e) (hput! assigned-vars x true) (Expr e)]
    [(clj-var-set! ~d ~e) (Expr e)]
    [(clj-def ~d ~st0 ~st1 ~e0 ~e1) (Expr e0) (Expr e1)]
    [(clj-invoke ~e ~e* ...) (Expr e) (run! Expr e*)]
    [(clj-keyword-invoke ~d ~e ~e* ...) (Expr e) (run! Expr e*)]
    [(clj-call ~d0 ~d1 ~e* ...) (run! Expr e*)]
    [(clj-static-call ~d0 ~d1 ~e* ...) (run! Expr e*)]
    [(check-initialized ~e) (Expr e)]
    [(clj-case ~d ~e0 ~e1 ~e* ...) (Expr e0) (Expr e1) (run! Expr e*)]
    [(clj-deftype ~d (~x* ...) ~le* ...) (run! Expr le*)]
    [else nil])
  (Expr ir))

(define-pass count-nodes L3 [ir n] -> * []
  (definitions
    (def cnt (volatile! 0))
    (defn more? [] (vswap! cnt inc) (<= @cnt n)))
  (Expr [ir] -> * []
    [(lambda ~nm (~x* ...) ~cl* ...) nil]
    [(if ~e0 ~e1 ~e2) (when (more?) (Expr e0) (Expr e1) (Expr e2))]
    [(begin ~e* ... ~e) (when (more?) (run! Expr e*) (Expr e))]
    [(set! ~x ~e) (when (more?) (Expr e))]
    [(letrec ([~x* ~le*] ...) ~body) (when (more?) (Expr body))]
    [(let ([~x* ~e*] ...) ~body) (when (more?) (run! Expr e*) (Expr body))]
    [(call ~e ~e* ...) (when (more?) (Expr e) (run! Expr e*))]
    [(throw ~e) (when (more?) (Expr e))]
    [(try-finally ~e0 ~e1) (when (more?) (Expr e0) (Expr e1))]
    [(try-catch ~e (~d* ~e*) ...) (when (more?) (Expr e) (run! Expr e*))]
    [(label ~x (~x* ...) ~body0 ~body1) (when (more?) (Expr body0) (Expr body1))]
    [(jump ~x ~e* ...) (when (more?) (run! Expr e*))]
    [(check-initialized ~e) (when (more?) (Expr e))]
    [else (more?)])
  (Expr ir)
  (> @cnt n))

(def big-body-nodes 2000)
(defn- bigger-than? [body n] (count-nodes body n))

(defn- infer-var-types-into!
  "原版 infer-var-types-into!（seeds 只用于推测循环，Clojure 输入没有，固定为 nil）。"
  [form out]
  (let [defs (ht) excluded (ht) labels (ht) raw-uses (ht) boxed-uses (ht)
        defs-order (atom [])]
    (infer-walk form
                (fn [x e]
                  (when-not (hhas? defs x) (swap! defs-order conj x))
                  (hput! defs x (let [l (or (hget defs x) ())] (if e (cons e l) l))))
                (fn [v] (hput! excluded v true))
                labels
                (fn [x] (hhas? defs x))
                (fn [x raw?] (when (hhas? defs x) (hupdate! (if raw? raw-uses boxed-uses) x inc 0))))
    (let [env (ht) users (ht)
          cands (filterv #(not (hget excluded %)) @defs-order)   ; 不动点与顺序无关（单调，从 top 出发）
          deps (fn deps [e]
                 (nanopass-case [L3 Expr] e
                   [~x [x]]
                   [(clj-call ~d0 ~d1 ~e* ...)
                    (if-let [lay (host-call-layout d0 d1 e*)] (vec (mapcat deps (nth lay 2))) [])]
                   [(if ~e0 ~e1 ~e2) (into (deps e1) (deps e2))]
                   [(begin ~e* ... ~e) (deps e)]
                   [(let ([~x* ~e*] ...) ~body) (deps body)]
                   [else []]))
          meet-defs (fn [x]
                      (let [t (loop [es (or (hget defs x) ()) t :top]
                                (if (or (empty? es) (not t)) t (recur (rest es) (meet-type t (expr-type (first es) env)))))]
                        (if (and (= t :fl) (inlined-binding? x)) nil t)))
          propagate! (fn [work]
                       (loop [work (seq work)]
                         (when work
                           (let [x (first work) old (hget env x) new (when old (meet-defs x))]
                             (if (and old (not= old new))
                               (do (hput! env x new) (recur (seq (concat (hget users x) (rest work)))))
                               (recur (next work)))))))
          decide! (fn [x]
                    (let [t (hget env x)]
                      (when (#{:fx :fl :l64} t) (hput! sound-types x t))
                      (cond (and (#{:fx :fl :l64} t) (<= (or (hget boxed-uses x) 0) (or (hget raw-uses x) 0))) (hput! out x t)
                            (and t (not (#{:top :l64} t))) (hput! var-facts x t))))]
      (doseq [k (keys-of fixed-types)]
        (let [v (hget fixed-types k)] (hput! env k v) (hput! var-types k v) (hput! sound-types k v)))
      (doseq [x cands] (hput! env x :top))
      (doseq [x cands e (or (hget defs x) ())]
        (doseq [v (deps e)] (hput! users v (cons x (or (hget users v) ())))))
      (propagate! cands)
      (run! decide! cands))))

;; —— deftype* 描述的访问（向量，见原版 deftype-info-*）——
(defn- dinfo [d k] (nth d k))
(defn- dinfo-kind [d] (dinfo d 1))   ; true reify*、false deftype*、:interface、:proxy、:genclass
(defn- deftype-reify? [d] (true? (dinfo-kind d)))
(defn- deftype-interface? [d] (= :interface (dinfo-kind d)))
(defn- deftype-proxy? [d] (= :proxy (dinfo-kind d)))
(defn- deftype-genclass? [d] (= :genclass (dinfo-kind d)))
(defn- deftype-caps [x* le*]
  (reduce (fn [acc le]
            (nanopass-case [L3 LambdaExpr] le
              [(lambda ~nm (~x1* ...) ~cl* ...)
               (reduce (fn [a v] (if (or (some #(= v %) a) (some #(= v %) x*)) a (conj a v))) acc x1*)]))
          [] le*))

;; —— 原始值（iron-034c / cleat-042，原版 cg-raw 等）——

(declare cg cg* load-var! cg-raw)

(defn- type-clr [t] (case t :fl double-type :l64 int64-type int32-type))

(defn- box-raw! [mc t]
  (case t
    :fl (emit! mc (i 'box double-type))
    :l64 (emit! mc (i 'box int64-type))
    (emit! mc (i 'box int32-type))))

(defn- unbox-to-sig! [mc c]
  (case c
    \L (emit! mc (i 'call static-calls-type "PrimArgLong" (i obj) int64-type))
    \D (emit! mc (i 'call static-calls-type "PrimArgDouble" (i obj) double-type))
    nil))

(defn- box-from-sig! [mc c]
  (case c
    \L (emit! mc (i 'box int64-type))
    \D (emit! mc (i 'box double-type))
    nil))

(defn- numbers-raw-emit-args! [args op mc]
  (doseq [x args] (cg-raw x mc :l64))
  (emit! mc (i 'call "clojure.lang.Numbers" op (apply i (map (fn [_] int64-type) args))
               (if (numbers-bool-ops op) bool-type int64-type))))

(defn- numbers-raw-emit! [e mc]
  (when-let [oa (numbers-arith-args e var-types)]
    (numbers-raw-emit-args! (rest oa) (first oa) mc)
    (if (numbers-bool-ops (first oa)) :bool :l64)))

(defn- raw-prim-invoke [e t]
  (when (#{:l64 :fl} t)
    (nanopass-case [L3 Expr] e
      [(clj-call ~d0 ~d1 ~e* ...)
       (when (and (= d0 "PrimInvoke") (= (sig-char-raw (sig-ret (quoted-datum (first e*)))) t)) e*)]
      [else nil])))

(defn- raw-host-call [e t]
  (when (#{:l64 :fl} t)
    (nanopass-case [L3 Expr] e
      [(clj-call ~d0 ~d1 ~e* ...)
       (let [sl (host-call-signature d0 d1 e*)]
         (when (and (vector? sl) (= (str (aget ^objects (first sl) 3)) (if (= t :l64) "System.Int64" "System.Double")))
           sl))]
      [else nil])))

(declare cg-prim-invoke! cg-static-host-call! ref-var!)

(defn- cg-raw-default [e mc t]
  (let [fallback #(do (cg e mc :value) (emit! mc (i 'unbox.any (type-clr t))))]
    (nanopass-case [L3 Expr] e
      [(quote ~d)
       (cond (and (= t :fx) (instance? Int32 d)) (emit! mc (i 'ldc.i4 d))
             (and (= t :fl) (instance? Double d) (not (Double/IsNaN d)) (not (Double/IsInfinity d))) (emit! mc (i 'ldc.r8 d))
             (and (= t :l64) (instance? Int64 d)) (emit! mc (i 'ldc.i8 d))
             :else (fallback))]
      [~x (let [loc (hget (:vars mc) x)]
            (if (and loc (#{:tlocal :targ} (first loc)) (= (nth loc 2) t))
              (do (ref-var! mc x false) (emit! mc (if (= :targ (first loc)) (i 'ldarg (second loc)) (i 'ldloc (second loc)))))
              (fallback)))]
      [else (fallback)])))

(defn cg-raw
  "把 e 的值以 t（:fx :fl :l64）的原始形式压栈。"
  [e mc t]
  (let [oa (when (= t :l64) (numbers-arith-args e var-types))]
    (cond
      (and oa (numbers-long-ops (first oa))) (numbers-raw-emit-args! (rest oa) (first oa) mc)
      (raw-prim-invoke e t) (cg-prim-invoke! (raw-prim-invoke e t) mc :raw)
      (raw-host-call e t) (cg-static-host-call! (raw-host-call e t) mc :raw)
      :else (cg-raw-default e mc t))))

(defn- cg-prim-invoke!
  "($clj-call \"PrimInvoke\" #f '签名 f 实参 …)：f 转为 primifs.<签名>、callvirt invokePrim（原版 cg-prim-invoke!）。"
  [e* mc ctx]
  (let [sig (quoted-datum (first e*)) f (second e*) args (drop 2 e*)
        cs (sig-params sig) rc (sig-ret sig)
        iface (str primifs-prefix sig)
        raws (mapv (fn [a c] (let [r (sig-char-raw c)] (when (and r (= (expr-type a var-types) r)) r))) args cs)
        codes (mapv (fn [a r] (with-buffer mc #(if r (cg-raw a mc r) (cg a mc :value)))) (cons f args) (cons nil raws))
        conv! (fn [c r] (when-not r (unbox-to-sig! mc c)))]
    (if (some contains-try? (rest codes))
      (let [temps (mapv (fn [c r] (let [t (new-local! mc (if r (type-clr r) obj))] (emit-code! mc c) (emit! mc (i 'stloc t)) t))
                        codes (cons nil raws))]
        (emit! mc (i 'ldloc (first temps)) (i 'castclass iface))
        (doseq [[t c r] (map vector (rest temps) cs raws)] (emit! mc (i 'ldloc t)) (conv! c r)))
      (do (emit-code! mc (first codes))
          (emit! mc (i 'castclass iface))
          (doseq [[code c r] (map vector (rest codes) cs raws)] (emit-code! mc code) (conv! c r))))
    (emit! mc (i 'callvirt iface "invokePrim" (apply i (sig-param-types sig)) (sig-char-type rc)))
    (when-not (= ctx :raw)
      (box-from-sig! mc rc)
      (finish-value! mc ctx))))

(defn- cg-static-host-call!
  "静态决议的宿主调用（原版 cg-static-host-call!）。sl：[签名 static? 实例 实参]。"
  [sl mc ctx]
  (let [[^objects sig static? inst args] sl
        decl (aget sig 0) nm (aget sig 1) pts (vec (aget sig 2)) ret (str (aget sig 3)) virt? (aget sig 4)
        convert! (fn [pt]
                   (cond (= pt "System.Object") nil
                         ({"System.Int32" "ArgInt32" "System.Int64" "ArgInt64" "System.Double" "ArgDouble"
                           "System.Boolean" "ArgBoolean" "System.Char" "ArgChar"} pt)
                         (emit! mc (i 'call static-calls-type ({"System.Int32" "ArgInt32" "System.Int64" "ArgInt64" "System.Double" "ArgDouble"
                                                                "System.Boolean" "ArgBoolean" "System.Char" "ArgChar"} pt) (i obj) pt))
                         :else (emit! mc (i 'castclass pt))))
        raw-long? (fn [x pt]
                    (let [t (expr-type x var-types)]
                      (cond (and (= t :l64) (#{"System.Int64" "System.Int32"} pt)) :l64
                            (and (= t :fl) (= pt "System.Double")) :fl
                            :else nil)))
        opnds (concat (if static? [] [inst]) args)
        types (concat (if static? [] [:instance]) pts)
        raws (mapv (fn [x pt] (when-not (= pt :instance) (raw-long? x pt))) opnds types)
        codes (mapv (fn [x raw?] (with-buffer mc #(if raw? (cg-raw x mc raw?) (cg x mc :value)))) opnds raws)
        conv! (fn [pt raw?]
                (cond (= pt :instance) (emit! mc (i 'ldstr nm) (i 'call static-calls-type "Instance" (i obj obj) obj) (i 'castclass decl))
                      (and raw? (= pt "System.Int32")) (emit! mc (i 'call "clojure.lang.RT" "intCast" (i int64-type) int32-type))
                      raw? nil
                      :else (convert! pt)))]
    (if (and (seq codes) (some contains-try? (rest codes)))
      (let [temps (mapv (fn [c raw?] (let [t (new-local! mc (if raw? (type-clr raw?) obj))] (emit-code! mc c) (emit! mc (i 'stloc t)) t)) codes raws)]
        (doseq [[t pt raw?] (map vector temps types raws)] (emit! mc (i 'ldloc t)) (conv! pt raw?)))
      (doseq [[c pt raw?] (map vector codes types raws)] (emit-code! mc c) (conv! pt raw?)))
    (let [kind (str (aget sig 5))]
      (if (= kind "method")
        (emit! mc (i (if virt? 'callvirt 'call) decl nm (apply i pts) ret))
        (do (when (= kind "volatile-field") (emit! mc (i 'volatile.)))
            (emit! mc (if static? (i 'ldsfld decl nm) (i 'ldfld decl nm))))))
    (cond
      (= ctx :raw) nil
      (= ret "System.Boolean") (finish-bool! mc ctx)
      :else (do (cond (= ret "System.Void") (emit! mc (i 'ldnull))
                      (= ret "System.Object") (emit! mc (i 'call static-calls-type "RetObject" (i obj) obj))
                      (= ret "System.Int32") (emit! mc (i 'call static-calls-type "RetInt32" (i int32-type) obj))
                      (#{"System.Int64" "System.Double" "System.Char"} ret) (emit! mc (i 'box ret))
                      :else nil)
                (finish-value! mc ctx)))))

;; —— 变量与局部变量清除（cleat-042 第 5 项，原版同名）——

(defn- var-location [mc x]
  (or (hget (:vars mc) x) (throw (ex-info (str "codegen: unbound variable " x) {:var x}))))

(def ^:dynamic *clear-args-by-ref* true)
(defn- clearing? [mc] (pos? (hsize (:uses mc))))
(defn- take-slot! [mc] (emit! mc (i 'call "Cleat.Core.Runtime.Locals" "Take" (i (str obj "&")) obj)))

(defn- clear-var! [mc x]
  (when-let [loc (hget (:vars mc) x)]
    (case (first loc)
      :local (emit! mc (i 'ldnull) (i 'stloc (second loc)))
      :arg (emit! mc (i 'ldnull) (i 'starg (second loc)))
      nil)))

(defn- ref-var!
  "引用变量 x：计数减一；load? 时压栈；是最后一个引用、不在钉住的循环里、是 object 局部变量或参数时清掉槽。"
  [mc x load?]
  (let [uses (:uses mc)
        n (hget uses x)
        last? (and n (do (hput! uses x (dec n)) (= n 1)) (zero? (or (hget (:pinned mc) x) 0)))
        loc (when last? (hget (:vars mc) x))]
    (cond
      (and load? loc (= :local (first loc))) (do (emit! mc (i 'ldloca (second loc))) (take-slot! mc))
      (and load? loc (= :arg (first loc)) *clear-args-by-ref*) (do (emit! mc (i 'ldarga (second loc))) (take-slot! mc))
      :else (do (when load? (load-var! mc x))
                (when last? (clear-var! mc x))))))

(defn- ldloc-clear! [mc t]
  (if (clearing? mc)
    (do (emit! mc (i 'ldloca t)) (take-slot! mc))
    (emit! mc (i 'ldloc t))))

(defn- handler-parts
  "try-catch 的处理器 (lambda nm (fv*) (clause (x) false body))：[异常变量 体]（cleat-092：处理器内联进 catch 块）"
  [h]
  (nanopass-case [L3 LambdaExpr] h
    [(lambda ~nm (~x* ...) ~cl* ...)
     (nanopass-case [L3 Clause] (first cl*) [(clause (~x1* ...) ~x ~body) [(first x1*) body]])]))

(defn- walk-var-refs
  "遍历 L3 表达式的变量引用（on-ref）与约束（on-bind）；不进入嵌套 lambda 的体，闭包创建时读的自由变量算引用；
  try 的处理器体内联，照常遍历。"
  [e on-ref on-bind]
  (letfn [(free-of [le self]
            (nanopass-case [L3 LambdaExpr] le
              [(lambda ~nm (~x* ...) ~cl* ...) (run! on-ref (if self (remove #(= self %) x*) x*))]))
          (walk [e]
            (nanopass-case [L3 Expr] e
              [~x (on-ref x)]
              [(if ~e0 ~e1 ~e2) (walk e0) (walk e1) (walk e2)]
              [(begin ~e* ... ~e) (run! walk e*) (walk e)]
              [(set! ~x ~e) (walk e)]
              [(lambda ~nm (~x* ...) ~cl* ...) (run! on-ref x*)]
              [(letrec ([~x* ~le*] ...) ~body)
               (run! on-bind x*)
               (doseq [[x le] (map vector x* le*)] (free-of le x))
               (walk body)]
              [(let ([~x* ~e*] ...) ~body) (run! walk e*) (run! on-bind x*) (walk body)]
              [(call ~e ~e* ...) (walk e) (run! walk e*)]
              [(throw ~e) (walk e)]
              [(try-finally ~e0 ~e1) (walk e0) (walk e1)]
              [(try-catch ~e (~d* ~e*) ...)
               (walk e)
               (doseq [h e*] (let [[bn hbody] (handler-parts h)] (on-bind bn) (walk hbody)))]
              [(label ~x (~x* ...) ~body0 ~body1) (run! on-bind x*) (walk body0) (walk body1)]
              [(jump ~x ~e* ...) (run! walk e*)]
              [(box ~e) (walk e)]
              [(unbox ~x) (on-ref x)]
              [(set-box! ~x ~e) (on-ref x) (walk e)]
              [(clj-var-set! ~d ~e) (walk e)]
              [(clj-def ~d ~st0 ~st1 ~e0 ~e1) (walk e0) (walk e1)]
              [(clj-invoke ~e ~e* ...) (walk e) (run! walk e*)]
              [(clj-keyword-invoke ~d ~e ~e* ...) (walk e) (run! walk e*)]
              [(clj-call ~d0 ~d1 ~e* ...) (run! walk e*)]
              [(clj-static-call ~d0 ~d1 ~e* ...) (run! walk e*)]
              [(check-initialized ~e) (walk e)]
              [(clj-case ~d ~e0 ~e1 ~e* ...) (walk e0) (walk e1) (run! walk e*)]
              [(clj-deftype ~d (~x* ...) ~le* ...) (when (deftype-reify? d) (run! on-ref (deftype-caps x* le*)))]
              [else nil]))]
    (walk e)))

(defn- init-clearing! [mc body]
  (walk-var-refs body (fn [x] (hupdate! (:uses mc) x inc 0)) (fn [_])))

(defn- branch-ref-counts [mc e]
  (let [uses (:uses mc) t (ht) order (atom [])]
    (if (zero? (hsize uses))
      []
      (do (walk-var-refs e (fn [x] (when (hhas? uses x) (when-not (hhas? t x) (swap! order conj x)) (hupdate! t x inc 0))) (fn [_]))
          (mapv (fn [x] [x (hget t x)]) @order)))))

(defn- adjust-uses! [mc counts op]
  (doseq [[x n] counts] (hupdate! (:uses mc) x #(op % n) 0)))

(defn- loop-outer-refs [body params]
  (let [refs (ht) order (atom []) bound (ht)]
    (doseq [p params] (hput! bound p true))
    (walk-var-refs body (fn [x] (when-not (hhas? refs x) (swap! order conj x)) (hput! refs x true)) (fn [x] (hput! bound x true)))
    (filterv #(not (hget bound %)) @order)))

(defn- load-var! [mc x]
  (let [loc (var-location mc x)]
    (case (first loc)
      :arg (emit! mc (i 'ldarg (second loc)))
      :local (emit! mc (i 'ldloc (second loc)))
      :tlocal (do (emit! mc (i 'ldloc (second loc))) (box-raw! mc (nth loc 2)))
      :targ (do (emit! mc (i 'ldarg (second loc))) (box-raw! mc (nth loc 2)))
      (:field :dfield) (emit! mc (i 'ldarg 0) (i 'ldfld (:class mc) (second loc)))
      :self (emit! mc (i 'ldarg 0)))))

(defn- store-var! [mc x]
  (let [loc (var-location mc x)]
    (case (first loc)
      :arg (emit! mc (i 'starg (second loc)))
      :local (emit! mc (i 'stloc (second loc)))
      :tlocal (emit! mc (i 'stloc (second loc)))
      :dfield (let [t (new-local! mc obj)]
                (emit! mc (i 'stloc t) (i 'ldarg 0) (i 'ldloc t) (i 'stfld (:class mc) (second loc))))
      (throw (ex-info (str "codegen: cannot assign captured variable " x) {:var x})))))

(defn- bind-local-object! [mc x]
  (let [n (new-local! mc obj)] (hput! (:vars mc) x [:local n]) n))

(defn- bind-local! [mc x]
  (if-let [t (and (not @(:big? mc)) (hget var-types x))]
    (let [n (new-local! mc (type-clr t))] (hput! (:vars mc) x [:tlocal n t]) n)
    (bind-local-object! mc x)))

(defn- bind-value! [mc x] (bind-local! mc x) (store-var! mc x))

(defn- var-raw-type [mc x]
  (let [loc (hget (:vars mc) x)] (when (and loc (#{:tlocal :targ} (first loc))) (nth loc 2))))

(defn- binding-code [mc x e]
  (with-buffer mc #(let [t (and (not @(:big? mc)) (hget var-types x))]
                     (if t (cg-raw e mc t) (cg e mc :value)))))

;; —— 表达式（原版 cg / cg*）——

(defn- raw-tail-leaf? [e]
  (nanopass-case [L3 Expr] e
    [(if ~e0 ~e1 ~e2) false]
    [(begin ~e* ... ~e) false]
    [(let ([~x* ~e*] ...) ~body) false]
    [(letrec ([~x* ~le*] ...) ~body) false]
    [(label ~x (~x* ...) ~body0 ~body1) false]
    [(jump ~x ~e* ...) false]
    [(throw ~e) false]
    [(try-finally ~e0 ~e1) false]
    [(try-catch ~e (~d* ~e*) ...) false]
    [else true]))

(defn cg
  "原始签名的方法里尾位置的叶子表达式类型已是返回的原始类型时直接以原始值返回（cleat-042）；其余照常。"
  [e mc ctx]
  (let [rt (and (= ctx :tail) @(:ret-type mc))]
    (if (and rt (raw-tail-leaf? e) (= (expr-type e var-types) rt))
      (do (cg-raw e mc rt) (emit! mc (i 'ret)))
      (cg* e mc ctx))))

(declare lambda-class! make-closure! fill-fields! cg-call deftype-node! source-name)

(defn- unsupported-node [e]
  (throw (ex-info (str "codegen: node not supported by the Clojure compiler: " (r/brief e)) {:node e})))

(defn- cg* [e mc ctx]
  (nanopass-case [L3 Expr] e
    [~x (ref-var! mc x (not= ctx :effect)) (when-not (= ctx :effect) (finish-value! mc ctx))]
    [(label ~x (~x* ...) ~body0 ~body1)
     ;; 局部循环（lower-loops 产生）：形参是 IL 局部变量，jump 赋值后 br 到循环体；body0 没跳到循环体时循环体不生成
     (do (run! #(bind-local! mc %) x*)
         (let [lf (new-label! mc) end-l (new-label! mc)]
           (hput! (:labels mc) x [lf x*])
           (let [code (with-buffer mc #(cg body0 mc ctx))]
             (emit-code! mc code)
             (when (branches-to? code lf)
               (when-not (= ctx :tail) (emit! mc (i 'br end-l)))
               (emit! mc (i 'label lf))
               ;; 体外绑定、体内引用的变量在循环里钉住（不清除）
               (let [outer (loop-outer-refs body1 x*) pinned (:pinned mc)]
                 (doseq [v outer] (hupdate! pinned v inc 0))
                 (cg body1 mc ctx)
                 (doseq [v outer] (hupdate! pinned v dec 0)))
               (when-not (= ctx :tail) (emit! mc (i 'label end-l)))))))]
    [(jump ~x ~e* ...)
     (let [entry (or (hget (:labels mc) x) (throw (ex-info (str "codegen: jump to unknown label " x) {})))
           ;; 实参先全部求值到临时变量再写入形参（并行赋值）；有类型的形参用原始值的临时变量
           temps (mapv (fn [p a]
                         (let [rt (var-raw-type mc p)]
                           (if rt (cg-raw a mc rt) (cg a mc :value))
                           (let [t (new-local! mc (if rt (type-clr rt) obj))] (emit! mc (i 'stloc t)) t)))
                       (second entry) e*)]
       (doseq [[p t] (map vector (second entry) temps)]
         (if (var-raw-type mc p) (emit! mc (i 'ldloc t)) (ldloc-clear! mc t))
         (store-var! mc p))
       (emit! mc (i 'br (first entry)))
       (case ctx :value (emit! mc (i 'ldnull)) :test (emit! mc (i 'ldc.i4 0)) nil))]
    [(quote ~d)
     (case ctx
       :effect nil
       :test (emit! mc (i 'ldc.i4 (if (and (some? d) (not (false? d))) 1 0)))
       (do (load-constant! mc d) (finish-value! mc ctx)))]
    [(if ~e0 ~e1 ~e2)
     (let [else-l (new-label! mc) end-l (new-label! mc)]
       (cg e0 mc :test)
       (emit! mc (i 'brfalse else-l))
       ;; 局部变量清除按分支：生成 then 支时先扣掉 else 支的引用数，生成 else 支前加回
       (let [c2 (branch-ref-counts mc e2)]
         (adjust-uses! mc c2 -)
         (cg e1 mc ctx)
         (adjust-uses! mc c2 +))
       (when-not (= ctx :tail) (emit! mc (i 'br end-l)))
       (emit! mc (i 'label else-l))
       (cg e2 mc ctx)
       (when-not (= ctx :tail) (emit! mc (i 'label end-l))))]
    [(begin ~e* ... ~e)
     (run! #(cg % mc :effect) e*)
     (cg e mc ctx)]
    [(set! ~x ~e)
     (cg e mc :value)
     (store-var! mc x)
     (finish-none! mc ctx)]
    [(lambda ~nm (~x* ...) ~cl* ...)
     (when-not (= ctx :effect)
       (let [c (lambda-class! e mc nil)]
         (make-closure! mc c x*)
         (finish-value! mc ctx)))]
    [(letrec ([~x* ~le*] ...) ~body)
     ;; 命名 fn*（自引用）：先创建全部函数对象，再填自由变量字段（组内互相引用）
     (let [classes (mapv (fn [x le] (lambda-class! le mc x)) x* le*)]
       (run! #(bind-local! mc %) x*)
       (doseq [[x c] (map vector x* classes)]
         (emit! mc (i 'newobj (second c) ()))
         (store-var! mc x))
       (doseq [[x c le] (map vector x* classes le*)]
         (nanopass-case [L3 LambdaExpr] le
           [(lambda ~nm (~x1* ...) ~cl* ...)
            (fill-fields! mc c (remove #(= x %) x1*) #(load-var! mc x))]))
       (cg body mc ctx))]
    [(let ([~x* ~e*] ...) ~body)
     ;; 右边依次求值（看不到同一 let 的变量），再依次存入新变量
     (let [codes (mapv (fn [x e] (binding-code mc x e)) x* e*)]
       (doseq [[x code] (map vector x* codes)] (emit-code! mc code) (bind-value! mc x))
       (cg body mc ctx))]
    [(call ~e0 ~e* ...) (cg-call e0 e* mc ctx)]
    [(throw ~e)
     (cg e mc :value)
     (emit! mc (i 'castclass exception-type) (i 'throw))
     (case ctx :value (emit! mc (i 'ldnull)) :test (emit! mc (i 'ldc.i4 0)) nil)]
    [(try-finally ~e0 ~e1)
     (let [tmp (when-not (= ctx :effect) (new-local! mc obj))
           body (in-try mc #(do (cg e0 mc (if tmp :value :effect)) (when tmp (emit! mc (i 'stloc tmp)))))
           f (with-buffer mc #(cg e1 mc :effect))]
       (emit! mc (i 'try (apply i 'body body) (apply i 'finally f)))
       (when tmp
         (emit! mc (i 'ldloc tmp))
         (finish-value! mc ctx)))]
    [(try-catch ~e (~d* ~e*) ...)
     ;; try { 值 = body } catch (T_i) { 异常 → 处理器的形参；值 = 处理器体 } …：处理器内联进 catch 块（cleat-092，原来是过程闭包）。
     ;; 处理器引用的外层变量在 body 里钉住（异常可能在 body 清掉它们之后抛出）；各处理器互为分支，生成第 i 个时先扣掉后面处理器的引用数
     (let [tmp (when-not (= ctx :effect) (new-local! mc obj))
           hctx (if tmp :value :effect)
           parts (mapv handler-parts e*)
           hrefs (vec (distinct (mapcat (fn [[bn hbody]] (loop-outer-refs hbody [bn])) parts)))
           pinned (:pinned mc)
           _ (doseq [v hrefs] (hupdate! pinned v inc 0))
           body (in-try mc #(do (cg e mc hctx) (when tmp (emit! mc (i 'stloc tmp)))))
           _ (doseq [v hrefs] (hupdate! pinned v dec 0))
           counts (mapv (fn [[_ hbody]] (branch-ref-counts mc hbody)) parts)
           handlers (mapv (fn [j d [bn hbody]]
                            (let [exc (bind-local-object! mc bn)
                                  later (drop (inc j) counts)]
                              (doseq [c later] (adjust-uses! mc c -))
                              (let [code (in-try mc #(do (cg hbody mc hctx) (when tmp (emit! mc (i 'stloc tmp)))))]
                                (doseq [c later] (adjust-uses! mc c +))
                                (apply i 'catch d (cons (i 'stloc exc) code)))))
                          (range) d* parts)]
       (emit! mc (apply i 'try (apply i 'body body) handlers))
       (when tmp
         (emit! mc (i 'ldloc tmp))
         (finish-value! mc ctx)))]
    ;; —— Clojure 节点 ——
    [(clj-var-ref ~d ~st)
     (when-not (= ctx :effect)
       (load-constant! mc d)
       (emit! mc (i 'castclass clj-var-type) (i 'callvirt clj-var-type (if st "get" "getRawRoot") () obj))
       (finish-value! mc ctx))]
    [(clj-var-set! ~d ~e)
     (operands! mc [#(do (load-constant! mc d) (emit! mc (i 'castclass clj-var-type))) #(cg e mc :value)])
     (emit! mc (i 'callvirt clj-var-type "set" (i obj) obj))
     (finish-value! mc ctx)]
    [(clj-def ~d ~st0 ~st1 ~e0 ~e1)
     (let [var! #(do (load-constant! mc d) (emit! mc (i 'castclass clj-var-type)))]
       (when st1
         (var!)
         (emit! mc (i 'callvirt clj-var-type "setDynamic" () clj-var-type) (i 'pop)))
       (when st0
         (operands! mc [var! #(cg e0 mc :value)])
         (emit! mc (i 'callvirt clj-var-type "bindRoot" (i obj) void-type)))
       (operands! mc [var! #(do (cg e1 mc :value) (emit! mc (i 'castclass pmap-type)))])
       (emit! mc (i 'callvirt clj-var-type "setMeta" (i pmap-type) void-type))
       (when-not (= ctx :effect)
         (load-constant! mc d)
         (finish-value! mc ctx)))]
    [(clj-invoke ~e ~e* ...)
     (let [argc (count e*)]
       (operands! mc (cons #(do (cg e mc :value) (emit! mc (i 'castclass ifn-type)))
                           (map (fn [x] #(cg x mc :value)) e*)))
       (when (> argc 20) (pack-array! mc (- argc 20)))
       (when (and (tail-ok? mc ctx) target-clojure-tail-calls?) (emit! mc (i 'tail)))
       (emit! mc (i 'callvirt ifn-type "invoke" (apply i (if (> argc 20) (concat (objs 20) [obj-array]) (objs argc))) obj))
       (finish-value! mc ctx))]
    [(clj-keyword-invoke ~d ~e ~e* ...)
     (let [t (new-local! mc obj)
           defs (mapv (fn [_] (new-local! mc obj)) e*)
           slow (new-label! mc) end (new-label! mc)
           rest! (fn [] (doseq [l defs] (emit! mc (i 'ldloc l))))]
       (cg e mc :value)
       (emit! mc (i 'stloc t))
       (doseq [[x l] (map vector e* defs)] (cg x mc :value) (emit! mc (i 'stloc l)))
       (emit! mc (i 'ldloc t) (i 'isinst ilookup-type) (i 'dup) (i 'brfalse slow))
       (load-constant! mc d)
       (rest!)
       (emit! mc (i 'callvirt ilookup-type "valAt" (apply i (objs (inc (count defs)))) obj) (i 'br end)
              (i 'label slow) (i 'pop) (i 'ldloc t))
       (load-constant! mc d)
       (rest!)
       (emit! mc (i 'call clj-rt-type "get" (apply i (objs (+ 2 (count defs)))) obj) (i 'label end))
       (finish-value! mc ctx))]
    [(clj-call ~d0 ~d1 ~e* ...)
     (cond
       (= d0 "PrimInvoke") (cg-prim-invoke! e* mc ctx)
       (and (= d0 "AwaitAsync") (= 1 (count e*)))
       (let [task "System.Threading.Tasks.Task" support "Cleat.Core.Runtime.AsyncSupport"]
         (cg (first e*) mc :value)
         (emit! mc (i 'call support "ToTask" (i obj) task) (i 'dup)
                (i 'call "System.Runtime.CompilerServices.AsyncHelpers" "Await" (i task) void-type)
                (i 'call support "ResultOf" (i task) obj))
         (finish-value! mc ctx))
       (and (= d0 "IsInstanceOf") (= 2 (count e*))
            (let [t (quoted-datum (first e*))]
              (and (instance? Type t) (pos? (count (Cleat.Core.Runtime.ClojureStaticCalls/IsinstName t))))))
       (do (cg (second e*) mc :value)
           (emit! mc (i 'isinst (Cleat.Core.Runtime.ClojureStaticCalls/IsinstName (quoted-datum (first e*)))) (i 'ldnull) (i 'cgt.un))
           (finish-bool! mc ctx))
       :else
       (case (numbers-raw-emit! e mc)
         :bool (finish-bool! mc ctx)
         :l64 (do (box-raw! mc :l64) (finish-value! mc ctx))
         (let [sig (host-call-signature d0 d1 e*)]
           (if (vector? sig)
             (cg-static-host-call! sig mc ctx)
             (let [argc (count e*)]
               (operands! mc (map (fn [x] #(cg x mc :value)) e*))
               (when d1 (pack-array! mc (- argc d1)))
               (emit! mc (i 'call clojure-ops-type d0 (apply i (if d1 (concat (objs d1) [obj-array]) (objs argc))) obj))
               (finish-value! mc ctx))))))]
    [(clj-static-call ~d0 ~d1 ~e* ...)
     ;; 受保护的直接链接（cleat-033）：根还是函数类 d0 时以根作 self 直接 call d0::invokeStatic，否则普通 IFn.invoke
     (let [n (dec (count e*))
           root (new-local! mc obj)
           defs (mapv (fn [_] (new-local! mc obj)) (rest e*))
           slow (new-label! mc) end (new-label! mc)]
       (cg (first e*) mc :value)
       (emit! mc (i 'stloc root))
       (doseq [[x l] (map vector (rest e*) defs)] (cg x mc :value) (emit! mc (i 'stloc l)))
       (emit! mc (i 'ldloc root) (i 'isinst d0) (i 'brfalse slow))
       (ldloc-clear! mc root)
       (doseq [l defs] (ldloc-clear! mc l))
       (emit! mc (i 'call d0 "invokeStatic" (apply i (objs (inc n))) obj) (i 'br end))
       (emit! mc (i 'label slow))
       (ldloc-clear! mc root)
       (emit! mc (i 'castclass ifn-type))
       (doseq [l defs] (ldloc-clear! mc l))
       (emit! mc (i 'callvirt ifn-type "invoke" (apply i (objs n)) obj))
       (emit! mc (i 'label end))
       (finish-value! mc ctx))]
    [(clj-case ~d ~e0 ~e1 ~e* ...)
     ;; CaseExpr（ClojureCLR 的语义），见 原版的注释
     (let [[shift mask kind entries] (vec d)
           entries (mapv vec entries)
           x (new-local! mc obj) k (new-local! mc int32-type)
           def-l (new-label! mc) end-l (new-label! mc)
           labels (mapv (fn [_] (new-label! mc)) entries)
           hs (mapv first entries)]
       (cg e0 mc :value)
       (emit! mc (i 'stloc x))
       (if (zero? kind)
         (emit! mc (i 'ldloc x) (i 'call clj-util-type "IsNonCharNumeric" (i obj) bool-type) (i 'brfalse def-l)
                (i 'ldloc x) (i 'call clj-util-type "ConvertToInt" (i obj) int32-type))
         (emit! mc (i 'ldloc x) (i 'call clj-util-type "hash" (i obj) int32-type)))
       (when-not (zero? mask) (emit! mc (i 'ldc.i4 shift) (i 'shr) (i 'ldc.i4 mask) (i 'and)))
       (emit! mc (i 'stloc k))
       (when (seq hs)
         (let [lo (apply min hs) hi (apply max hs)]
           (if (<= (- hi lo) (+ 8 (* 2 (count hs))))
             (emit! mc (i 'ldloc k) (i 'ldc.i4 lo) (i 'sub)
                    (i 'switch (apply i (for [h (range lo (inc hi))]
                                         (let [j (first (keep-indexed (fn [j v] (when (= v h) j)) hs))]
                                           (if j (labels j) def-l))))))
             (doseq [[h l] (map vector hs labels)] (emit! mc (i 'ldloc k) (i 'ldc.i4 h) (i 'beq l))))))
       (emit! mc (i 'br def-l))
       (doseq [[en l then] (map vector entries labels e*)]
         (emit! mc (i 'label l))
         (when-not (nth en 2)
           (emit! mc (i 'ldloc x))
           (load-constant! mc (nth en 1))
           (emit! mc (if (= kind 2) (i 'ceq) (i 'call clj-util-type "equiv" (i obj obj) bool-type)) (i 'brfalse def-l)))
         (cg then mc ctx)
         (when-not (= ctx :tail) (emit! mc (i 'br end-l))))
       (emit! mc (i 'label def-l))
       (cg e1 mc ctx)
       (when-not (= ctx :tail) (emit! mc (i 'label end-l))))]
    [(clj-deftype ~d (~x* ...) ~le* ...) (deftype-node! mc d x* le* ctx)]
    [(box ~e)
     (cg e mc :value)
     (emit! mc (i 'newobj box-type (i obj)))
     (finish-value! mc ctx)]
    [(unbox ~x)
     (if (= ctx :effect)
       (ref-var! mc x false)
       (do (ref-var! mc x true)
           (emit! mc (i 'castclass box-type) (i 'ldfld box-type "Value"))
           (finish-value! mc ctx)))]
    [(set-box! ~x ~e)
     (operands! mc [#(do (ref-var! mc x true) (emit! mc (i 'castclass box-type))) #(cg e mc :value)])
     (emit! mc (i 'stfld box-type "Value"))
     (finish-none! mc ctx)]
    ;; simplify-letrec 给前向变量的初值与检查（原版的 $uninitialized / $check-initialized 原语）
    [(uninitialized)
     (when-not (= ctx :effect)
       (emit! mc (i 'ldsfld clojure-ops-type "Uninitialized"))
       (finish-value! mc ctx))]
    [(check-initialized ~e)
     (let [nm (nanopass-case [L3 Expr] e [~x (source-name x)] [(unbox ~x) (source-name x)] [else "?"])]
       (cg e mc :value)
       (emit! mc (i 'ldstr nm) (i 'call clojure-ops-type "CheckInitialized" (i obj obj) obj))
       (finish-value! mc ctx))]
    [else (unsupported-node e)]))

;; —— 调用（原版 cg-call 的 Clojure 子集：自尾调用、letrec 的直接调用、一般调用）——

(defn- cg-call
  "(call e0 e* …)：lower-loops 把立即调用的 lambda 变成 let、命名 let 变成 label，剩下的（不应出现）按 IFn 调用；原版过程的调用路径
  （自尾调用、iron-070 的局部直接调用）cleat-092 删去。"
  [e0 e* mc ctx]
  (cg* (with-output-language [L3 Expr] (out (clj-invoke ~e0 ~e* ...))) mc ctx))

;; —— lambda → 类 ——

(defn- clause-shape [cl]
  (nanopass-case [L3 Clause] cl [(clause (~x* ...) ~x ~body) [(count x*) (boolean x)]]))

(defn- source-name
  "g$名字$序号$后缀 → 名字，其余原样（分析器的临时名形状，报错信息用）。"
  [x]
  (let [s (str x) n (count s)]
    (if (and (> n 2) (= \g (nth s 0)) (= \$ (nth s 1)))
      (loop [j (dec n) dollars 0]
        (cond
          (< j 2) s
          (= \$ (nth s j)) (if (= dollars 1) (subs s 2 j) (recur (dec j) (inc dollars)))
          :else (recur (dec j) dollars)))
      s)))

(defn- finish-method [mc nm attrs params & [ret]]
  (i 'method nm (apply i 'attrs attrs) (apply i 'params params) (i 'ret (or ret obj))
     (apply i 'locals @(:locals mc))
     (i 'clr-namespaces)
     (apply i 'body @(:buffer mc))))

(declare clj-fn-class!)

(defn- lambda-class!
  "fn* 的类：返回 [static? 类名 字段名]。self：letrec 中绑定此 lambda 的变量。到这里的 lambda 只有 Clojure 的 fn*
  （try 的处理器内联、deftype 的方法另行生成、循环降成 label）。"
  [le mc self]
  (nanopass-case [L3 LambdaExpr] le
    [(lambda ~nm (~x1* ...) ~cl* ...)
     (if (l/clj-fn-name? nm)
       (clj-fn-class! mc nm x1* cl* self)
       (throw (ex-info (str "codegen: a non-fn* lambda reached codegen: " (r/brief le)) {:node le})))]))

(defn- fill-fields! [mc c fv* load]
  (doseq [[f j] (map vector fv* (range))]
    (if load (do (load) (emit! mc (i 'castclass (second c)))) (emit! mc (i 'dup)))
    (ref-var! mc f true)
    (emit! mc (i 'stfld (second c) (str "c" j)))))

;; 创建闭包：newobj 后逐个写自由变量字段
(defn- make-closure! [mc c fv*]
  (emit! mc (i 'newobj (second c) ()))
  (fill-fields! mc c fv* nil))

;; —— Clojure 的 fn* → AFunction / RestFn 子类（cleat-051，原版 clj-fn-class! 等）——

(defn- unique-type-name! [u nm]
  (let [names (:type-names u)]
    (loop [k 1]
      (let [n (if (= k 1) nm (str nm "__" k))]
        (if (hget names n) (recur (inc k)) (do (hput! names n true) n))))))

(defn- clause-sig
  "fn 名字记录里固定子句的原始签名（变长子句没有）。"
  [nm cl]
  (when (l/clj-fn-name? nm)
    (nanopass-case [L3 Clause] cl
      [(clause (~x* ...) ~x ~body)
       (when-not x
         (some (fn [[n sig]] (when (= (long n) (count x*)) sig)) (:prims nm)))])))

(defn- fix-clause-params!
  "原始签名子句的 L / D 形参登记为固定类型（cleat-042）。"
  [nm cl]
  (when-let [sig (clause-sig nm cl)]
    (nanopass-case [L3 Clause] cl
      [(clause (~x* ...) ~x ~body)
       (doseq [[p c] (map vector x* (sig-params sig))]
         (when-let [r (sig-char-raw c)] (hput! fixed-types p r)))])))

(defn- bind-clause-params! [mc x* j0 sig]
  (loop [ps (seq x*) j j0 cs (if sig (sig-params sig) ())]
    (when ps
      (let [raw (when (seq cs) (sig-char-raw (first cs)))]
        (hput! (:vars mc) (first ps) (if raw [:targ j raw] [:arg j])))
      (recur (next ps) (inc j) (rest cs))))
  (when sig (reset! (:ret-type mc) (sig-char-raw (sig-ret sig)))))

(defn- clj-clause-method [u class cl fields field-names self mname & [sig]]
  (nanopass-case [L3 Clause] cl
    [(clause (~x* ...) ~x ~body)
     (let [mc (make-method-ctx u class false nil nil nil)]
       (bind-clause-params! mc x* 1 sig)
       (doseq [[f fname] (map vector fields field-names)] (hput! (:vars mc) f [:field fname]))
       (when self (hput! (:vars mc) self [:self]))
       (reset! (:big? mc) (bigger-than? body big-body-nodes))
       (init-clearing! mc body)
       (cg body mc :tail)
       (if sig
         (concat (finish-method mc mname ['optimize] (sig-param-types sig) (sig-char-type (sig-ret sig)))
                 [(i 'implements (str primifs-prefix sig) "invokePrim")])
         (finish-method mc mname ['override 'optimize] (objs (count x*)))))]))

(defn- task-of-object-type [] (Cleat.Core.Runtime.AsyncSupport/TaskOfObjectName))

(defn- clj-async-clause-method [u class cl fields field-names self]
  (nanopass-case [L3 Clause] cl
    [(clause (~x* ...) ~x ~body)
     (let [mc (make-method-ctx u class false nil nil nil)]
       (doseq [[f fname] (map vector fields field-names)] (hput! (:vars mc) f [:field fname]))
       (when self (hput! (:vars mc) self [:self]))
       (reset! (:big? mc) (bigger-than? body big-body-nodes))
       (reset! (:try-depth mc) 1)
       (cg body mc :tail)
       (finish-method mc "invokeAsync" ['async] [] (task-of-object-type)))]))

(defn- clj-static-clause-method [u class cl self sig]
  (nanopass-case [L3 Clause] cl
    [(clause (~x* ...) ~x ~body)
     (let [mc (make-method-ctx u class true nil nil nil)]
       (when self (hput! (:vars mc) self [:arg 0]))
       (bind-clause-params! mc x* 1 sig)
       (reset! (:big? mc) (bigger-than? body big-body-nodes))
       (init-clearing! mc body)
       (cg body mc :tail)
       (if sig
         (finish-method mc "invokeStaticPrim" ['static 'optimize] (cons obj (sig-param-types sig)) (sig-char-type (sig-ret sig)))
         (finish-method mc "invokeStatic" ['static 'optimize] (objs (inc (count x*))))))]))

(defn- clj-prim-bridge [u class cl sig kind]
  (let [mc (make-method-ctx u class (= kind :static) nil nil nil)
        cs (sig-params sig) rc (sig-ret sig)
        n (count cs) ptypes (sig-param-types sig) rtype (sig-char-type rc)]
    (case kind
      :static (do (emit! mc (i 'ldarg 0))
                  (doseq [[c j] (map vector cs (iterate inc 1))] (emit! mc (i 'ldarga j)) (take-slot! mc) (unbox-to-sig! mc c))
                  (emit! mc (i 'call class "invokeStaticPrim" (apply i (cons obj ptypes)) rtype))
                  (box-from-sig! mc rc)
                  (emit! mc (i 'ret))
                  (finish-method mc "invokeStatic" ['static 'optimize] (objs (inc n))))
      :prim (do (emit! mc (i 'ldarg 0))
                (doseq [j (range 1 (inc n))] (emit! mc (i 'ldarg j)))
                (emit! mc (i 'call class "invokeStaticPrim" (apply i (cons obj ptypes)) rtype) (i 'ret))
                (concat (finish-method mc "invokePrim" ['optimize] ptypes rtype)
                        [(i 'implements (str primifs-prefix sig) "invokePrim")]))
      (do (emit! mc (i 'ldarg 0))
          (doseq [[c j] (map vector cs (iterate inc 1))] (emit! mc (i 'ldarga j)) (take-slot! mc) (unbox-to-sig! mc c))
          (emit! mc (i 'call class "invokePrim" (apply i ptypes) rtype))
          (box-from-sig! mc rc)
          (emit! mc (i 'ret))
          (finish-method mc "invoke" ['override 'optimize] (objs n))))))

(defn- clj-forward-method [u class cl]
  (nanopass-case [L3 Clause] cl
    [(clause (~x* ...) ~x ~body)
     (i 'method "invoke" (i 'attrs 'override 'optimize) (apply i 'params (objs (count x*))) (i 'ret obj)
        (apply i 'body (concat [(i 'ldarg 0)]
                               (mapcat (fn [j] [(i 'ldarga j) (i 'call "Cleat.Core.Runtime.Locals" "Take" (i (str obj "&")) obj)]) (range 1 (inc (count x*))))
                               [(i 'call class "invokeStatic" (apply i (objs (inc (count x*)))) obj) (i 'ret)])))]))

(defn- clj-fn-class!
  "fn* → AFunction / RestFn 的子类（原版 clj-fn-class!）。"
  [mc nm x1* cl* self]
  (let [u (:unit mc)
        fields (if self (remove #(= self %) x1*) x1*)
        static? false
        cname (unique-type-name! u (:class nm))
        variadic (let [v (:variadic nm)] (when-not (false? v) v))
        n (count cl*)
        base (if variadic restfn-type afunction-type)
        field-names (map #(str "c" %) (range (count fields)))
        arities (map #(first (clause-shape %)) cl*)
        fixed (if variadic (take (dec n) arities) arities)
        async? (:async? nm)
        methods (atom [])
        add! #(swap! methods conj %)]
    (when (and (empty? fields) (not async?))
      (doseq [cl (if variadic (take (dec n) cl*) cl*)]
        (let [sig (clause-sig nm cl)]
          (add! (clj-static-clause-method u cname cl self sig))
          (when sig
            (add! (clj-prim-bridge u cname cl sig :static))
            (add! (clj-prim-bridge u cname cl sig :prim))))))
    (doseq [[cl j] (map vector cl* (range))]
      (let [last-variadic? (and variadic (= j (dec n)))]
        (cond
          async? (do (add! (clj-async-clause-method u cname cl fields field-names self))
                     (add! (i 'method "invoke" (i 'attrs 'override) (i 'params) (i 'ret obj)
                              (i 'body (i 'ldarg 0) (i 'call cname "invokeAsync" () (task-of-object-type)) (i 'ret)))))
          (and (empty? fields) (not last-variadic?)) (add! (clj-forward-method u cname cl))
          (and (not last-variadic?) (clause-sig nm cl))
          (let [sig (clause-sig nm cl)]
            (add! (clj-clause-method u cname cl fields field-names self "invokePrim" sig))
            (add! (clj-prim-bridge u cname cl sig :invoke)))
          :else (add! (clj-clause-method u cname cl fields field-names self (if last-variadic? "doInvoke" "invoke"))))))
    (when variadic
      (add! (i 'method "getRequiredArity" (i 'attrs 'override) (i 'params) (i 'ret int32-type)
               (i 'body (i 'ldc.i4 variadic) (i 'ret)))))
    (add! (i 'method "HasArity" (i 'attrs 'override) (i 'params int32-type) (i 'ret bool-type)
             (apply i 'body (concat (mapcat (fn [k] [(i 'ldarg 1) (i 'ldc.i4 k) (i 'beq 'YES)]) fixed)
                                    (if variadic [(i 'ldarg 1) (i 'ldc.i4 variadic) (i 'bge 'YES)] [])
                                    [(i 'ldc.i4 0) (i 'ret) (i 'label 'YES) (i 'ldc.i4 1) (i 'ret)]))))
    (add! (i 'method ".ctor" (i 'attrs 'ctor) (i 'params)
             (i 'body (i 'ldarg 0) (i 'call base ".ctor" () void-type) (i 'ret))))
    (let [sigs (distinct (keep #(clause-sig nm %) cl*))]
      (add-class! u (apply i 'type cname (i 'base base) (i 'attrs 'sealed)
                           (concat (if (seq sigs) [(apply i 'interfaces (map #(str primifs-prefix %) sigs))] [])
                                   (map #(i 'field % obj) field-names)
                                   @methods))))
    [static? cname "Instance"]))

;; —— deftype* / reify* / gen-interface / proxy-class* / gen-class* → CLR 类型（cleat-089，原版 cleat-051 / 043 / 053）——
;; 描述 d 是分析器算好的 Clojure 数据（向量；类型写作 ["类型名" 值类型?]），见 原版各 deftype-info-* 的注释。

(defn- sl "描述里的列表 → 向量（false / nil 为空）" [x] (if (or (nil? x) (false? x)) [] (vec x)))
(defn- tname [t] (first t))
(defn- value-type? [t] (true? (second t)))
(defn- spec-iface [sp] (let [x (nth (sl sp) 0)] (when (string? x) x)))
(defn- spec-name [sp] (nth (sl sp) 1))
(defn- spec-ret [sp] (nth (sl sp) 2))
(defn- spec-params [sp] (sl (nth (sl sp) 3)))
(defn- dinfo-class [d] (dinfo d 0))
(defn- dinfo-fields [d] (mapv sl (sl (dinfo d 2))))   ; [名字 可变? volatile?]
(defn- dinfo-ifaces [d] (sl (dinfo d 3)))
(defn- dinfo-methods [d] (sl (dinfo d 4)))
(defn- dinfo-stubs [d] (mapv sl (sl (dinfo d 5))))
(defn- dinfo-basis [d] (dinfo d 6))
(defn- dinfo-record-keys [d] (if (> (count d) 7) (sl (nth d 7)) []))
(defn- dinfo-drops [d] (if (> (count d) 8) (nth d 8) 0))
(defn- truthy? [x] (and (some? x) (not (false? x))))

(defn- ldargs [n] (map #(i 'ldarg %) (range 1 (inc n))))
(def ^:private box-zero [(i 'ldc.i4 0) (i 'box int32-type)])

(defn- record-members
  "defrecord 的另外两个构造函数与静态 create（原版 record-members）。"
  [name d]
  (let [n (count (dinfo-fields d)) drops (dinfo-drops d) ks (dinfo-record-keys d) nb (- n drops)
        alt-ctor (fn [k tail]
                   (i 'method ".ctor" (i 'attrs 'ctor) (apply i 'params (objs k))
                      (apply i 'body (concat [(i 'ldarg 0)] (ldargs k) tail [(i 'call name ".ctor" (apply i (objs n)) void-type) (i 'ret)]))))]
    (if (zero? drops)
      []
      (concat
        (when (= drops 4) [(alt-ctor nb (concat [(i 'ldnull) (i 'ldnull)] box-zero box-zero))])
        (when (and (pos? (- n 2)) (= drops 4)) [(alt-ctor (- n 2) (concat box-zero box-zero))])
        [(i 'method "create" (i 'attrs 'static) (i 'params "clojure.lang.IPersistentMap") (i 'ret name)
            (apply i 'locals "clojure.lang.Keyword" (objs nb))
            (apply i 'body
                   (concat
                     (mapcat (fn [k j]
                               [(i 'ldarg 0) (i 'ldstr k) (i 'call "clojure.lang.Keyword" "intern" (i string-type) "clojure.lang.Keyword")
                                (i 'dup) (i 'stloc 0) (i 'ldnull)
                                (i 'callvirt "clojure.lang.ILookup" "valAt" (i obj obj) obj) (i 'stloc j)
                                (i 'ldarg 0) (i 'ldloc 0) (i 'callvirt "clojure.lang.IPersistentMap" "without" (i obj) "clojure.lang.IPersistentMap")
                                (i 'starg 0)])
                             ks (iterate inc 1))
                     (map #(i 'ldloc %) (range 1 (inc nb)))
                     [(i 'ldnull) (i 'ldarg 0) (i 'call "clojure.lang.RT" "seqOrElse" (i obj) obj)]
                     box-zero box-zero
                     [(i 'newobj name (apply i (objs n))) (i 'ret)])))]))))

(defn- interface-class! [mc d]
  (let [u (:unit mc) name (dinfo-class d)]
    (hput! (:type-names u) name true)
    (add-class! u (apply i 'type name (i 'attrs 'interface 'abstract) (apply i 'interfaces (dinfo-ifaces d))
                         (map (fn [sp] (i 'method (spec-name sp) (i 'attrs 'abstract 'virtual)
                                          (apply i 'params (map tname (spec-params sp))) (i 'ret (tname (spec-ret sp)))))
                              (dinfo-methods d))))
    [name]))

(defn- convert-object-code
  "栈上的 object → 声明类型 t 的代码（void 时丢弃）"
  [t]
  (cond
    (= (tname t) void-type) [(i 'pop)]
    (= (tname t) obj) []
    (value-type? t) [(i 'ldtoken (tname t)) (i 'call type-type "GetTypeFromHandle" (i type-handle-type) type-type)
                     (i 'call clojure-ops-type "ConvertReturn" (i obj type-type) obj) (i 'unbox.any (tname t))]
    :else [(i 'castclass (tname t))]))

(defn- boxed-arg-code
  "第 j 个实参装成 Clojure 的值：值类型装箱并按 Clojure 的规则转换（int → long）"
  [j t]
  (if (value-type? t)
    [(i 'ldarg j) (i 'box (tname t)) (i 'call clojure-ops-type "BoxArg" (i obj) obj)]
    [(i 'ldarg j)]))

(defn- throw-not-implemented [msg]
  [(i 'ldstr msg) (i 'newobj "System.NotImplementedException" (i string-type)) (i 'throw)])

(defn- proxy-class! [mc d]
  (let [u (:unit mc) v (vec d)
        name (nth v 0) super (nth v 2) ifaces (sl (nth v 3)) ctors (mapv sl (sl (nth v 4))) methods (mapv sl (sl (nth v 5)))
        fmap "__clojureFnMap" iproxy "clojure.lang.IProxy"
        method-def
        (fn [[mname ret ptypes access kind target fallback key ok?]]
          (let [ptypes (sl ptypes) target (sl target) n (count ptypes)
                call-base (concat [(i 'ldarg 0)] (ldargs n)
                                  [(i 'call (first target) (second target) (apply i (map tname ptypes)) (tname ret)) (i 'ret)])
                default (if (= fallback "base") call-base
                            (throw-not-implemented (str (second target) " is not implemented by proxy " name)))
                dispatch (if (truthy? ok?)
                           (concat
                             [(i 'ldarg 0) (i 'ldfld name fmap) (i 'ldstr key)
                              (i 'call clojure-ops-type "ProxyFn" (i pmap-type string-type) ifn-type)
                              (i 'dup) (i 'brfalse 'NOFN)]
                             (if (< n 20)
                               (concat [(i 'ldarg 0)]
                                       (mapcat (fn [t j] (boxed-arg-code j t)) ptypes (iterate inc 1))
                                       [(i 'callvirt ifn-type "invoke" (apply i (objs (inc n))) obj)])
                               (concat [(i 'ldc.i4 (inc n)) (i 'newarr obj) (i 'dup) (i 'ldc.i4 0) (i 'ldarg 0) (i 'stelem.ref)]
                                       (mapcat (fn [t j] (concat [(i 'dup) (i 'ldc.i4 j)] (boxed-arg-code j t) [(i 'stelem.ref)]))
                                               ptypes (iterate inc 1))
                                       [(i 'call clojure-ops-type "InvokeIFn" (i ifn-type obj-array) obj)]))
                             (convert-object-code ret)
                             [(i 'ret) (i 'label 'NOFN) (i 'pop)])
                           [])]
            (apply i 'method mname
                   (apply i 'attrs (cond (= kind "override") (cons 'override (when (= access "protected") ['protected]))
                                         (= mname (second target)) []
                                         :else ['private]))
                   (apply i 'params (map tname ptypes)) (i 'ret (tname ret))
                   (concat (when (= kind "implement") [(i 'implements (first target) (second target))])
                           [(apply i 'body (concat dispatch default))]))))
        super-def
        (fn [[_ ret ptypes _ _ target]]
          (let [ptypes (sl ptypes) target (sl target)]
            (i 'method (str "proxy$super$" (second target)) (apply i 'params (map tname ptypes)) (i 'ret (tname ret))
               (apply i 'body (concat [(i 'ldarg 0)] (ldargs (count ptypes))
                                      [(i 'call (first target) (second target) (apply i (map tname ptypes)) (tname ret)) (i 'ret)])))))]
    (when-not (hhas? (:type-names u) name)
      (hput! (:type-names u) name true)
      (add-class! u
        (apply i 'type name (i 'base super) (apply i 'interfaces ifaces)
               (i 'field fmap pmap-type)
               (concat
                 (map (fn [ps]
                        (i 'method ".ctor" (i 'attrs 'ctor) (apply i 'params (map tname ps))
                           (apply i 'body (concat [(i 'ldarg 0)] (ldargs (count ps))
                                                  [(i 'call super ".ctor" (apply i (map tname ps)) void-type) (i 'ret)]))))
                      ctors)
                 [(i 'method "__initClojureFnMappings" (i 'params pmap-type) (i 'ret void-type) (i 'implements iproxy "__initClojureFnMappings")
                     (i 'body (i 'ldarg 0) (i 'ldarg 1) (i 'stfld name fmap) (i 'ret)))
                  (i 'method "__updateClojureFnMappings" (i 'params pmap-type) (i 'ret void-type) (i 'implements iproxy "__updateClojureFnMappings")
                     (i 'body (i 'ldarg 0) (i 'ldarg 0) (i 'ldfld name fmap) (i 'ldarg 1)
                        (i 'call clojure-ops-type "ProxyUpdate" (i pmap-type pmap-type) pmap-type) (i 'stfld name fmap) (i 'ret)))
                  (i 'method "__getClojureFnMappings" (i 'params) (i 'ret pmap-type) (i 'implements iproxy "__getClojureFnMappings")
                     (i 'body (i 'ldarg 0) (i 'ldfld name fmap) (i 'ret)))]
                 (map method-def methods)
                 (map super-def (filter #(= (nth % 6) "base") methods))))))
    [name]))

(defn- genclass-class! [mc d]
  (let [u (:unit mc) v (vec d)
        [name _ super ifaces impl-ns load-impl? var-fields ctors init post-init state methods factory main exposes exposes-methods] v
        ifaces (sl ifaces) var-fields (mapv sl (sl var-fields)) ctors (mapv sl (sl ctors)) methods (mapv sl (sl methods))
        exposes (mapv sl (sl exposes)) exposes-methods (mapv sl (sl exposes-methods))
        init (when (truthy? init) init) post-init (when (truthy? post-init) post-init) state (when (truthy? state) state)
        factory (when (truthy? factory) factory) main (when (truthy? main) main)
        get-fn (fn [f] [(i 'ldsfld name f) (i 'call clojure-ops-type "GenClassFn" (i clj-var-type) ifn-type)])
        not-defined (fn [what] (throw-not-implemented (str what " is not defined for " name)))
        invoke-code (fn [this? first-arg ptypes]
                      (let [n (count ptypes) k (+ n (if this? 1 0)) idx (range first-arg (+ first-arg n))]
                        (if (< k 20)
                          (concat (when this? [(i 'ldarg 0)])
                                  (mapcat (fn [t j] (boxed-arg-code j t)) ptypes idx)
                                  [(i 'callvirt ifn-type "invoke" (apply i (objs k)) obj)])
                          (concat [(i 'ldc.i4 k) (i 'newarr obj)]
                                  (when this? [(i 'dup) (i 'ldc.i4 0) (i 'ldarg 0) (i 'stelem.ref)])
                                  (mapcat (fn [t j s] (concat [(i 'dup) (i 'ldc.i4 s)] (boxed-arg-code j t) [(i 'stelem.ref)]))
                                          ptypes idx (iterate inc (if this? 1 0)))
                                  [(i 'call clojure-ops-type "InvokeIFn" (i ifn-type obj-array) obj)]))))
        method-def
        (fn [[mname ret ptypes access kind target fallback vf ovf ok?]]
          (let [ptypes (sl ptypes) target (sl target) static? (= kind "static") n (count ptypes)
                ovf (when (truthy? ovf) ovf)]
            (apply i 'method mname
                   (apply i 'attrs (cond (= kind "override") (cons 'override (when (= access "protected") ['protected]))
                                         (= kind "implement") (if (= mname (second target)) [] ['private])
                                         (= kind "new") ['virtual]
                                         :else ['static]))
                   (apply i 'params (map tname ptypes)) (i 'ret (tname ret))
                   (concat
                     (when (= kind "implement") [(i 'implements (first target) (second target))])
                     [(apply i 'body
                             (concat
                               (when (truthy? ok?)
                                 (concat (when ovf (concat (get-fn ovf) [(i 'dup) (i 'brtrue 'FOUND) (i 'pop)]))
                                         (get-fn vf) [(i 'dup) (i 'brfalse 'NOFN) (i 'label 'FOUND)]
                                         (invoke-code (not static?) (if static? 0 1) ptypes)
                                         (convert-object-code ret)
                                         [(i 'ret) (i 'label 'NOFN) (i 'pop)]))
                               (if (= fallback "base")
                                 (concat [(i 'ldarg 0)] (ldargs n)
                                         [(i 'call (first target) (second target) (apply i (map tname ptypes)) (tname ret)) (i 'ret)])
                                 (not-defined mname))))]))))
        ctor-def
        (fn [[ps sps]]
          (let [ps (sl ps) sps (sl sps) n (count ps)
                same? (= (map tname ps) (map tname sps))
                pass-through (concat [(i 'ldarg 0)] (ldargs n) [(i 'call super ".ctor" (apply i (map tname sps)) void-type)])
                nth-ins (i 'call "clojure.lang.RT" "nth" (i "System.Object" "System.Int32") "System.Object")]
            (i 'method ".ctor" (i 'attrs 'ctor) (apply i 'params (map tname ps)) (i 'locals obj)
               (apply i 'body
                      (concat
                        (if init
                          (concat (get-fn init) [(i 'dup) (i 'brfalse 'NOINIT)]
                                  (invoke-code false 1 ps) [(i 'stloc 0) (i 'ldarg 0)]
                                  (mapcat (fn [t j] (concat [(i 'ldloc 0) (i 'ldc.i4 0) nth-ins (i 'ldc.i4 j) nth-ins] (convert-object-code t)))
                                          sps (range))
                                  [(i 'call super ".ctor" (apply i (map tname sps)) void-type)]
                                  (when state [(i 'ldarg 0) (i 'ldloc 0) (i 'ldc.i4 1) nth-ins (i 'stfld name state)])
                                  [(i 'br 'AFTERINIT) (i 'label 'NOINIT) (i 'pop)]
                                  (if same? pass-through (not-defined "init"))
                                  [(i 'label 'AFTERINIT)])
                          (if same? pass-through (not-defined "init")))
                        (when post-init
                          (concat (get-fn post-init) [(i 'dup) (i 'brfalse 'NOPOST)]
                                  (invoke-code true 1 ps) [(i 'pop) (i 'br 'ENDPOST) (i 'label 'NOPOST) (i 'pop)]
                                  (not-defined "post-init") [(i 'label 'ENDPOST)]))
                        [(i 'ret)])))))]
    (hput! (:type-names u) name true)
    (add-class! u
      (apply i 'type name (i 'base super) (apply i 'interfaces ifaces)
             (concat
               (map (fn [[f]] (i 'field f clj-var-type 'static)) var-fields)
               (when state [(i 'field state obj 'readonly)])
               [(i 'method ".cctor" (i 'attrs 'cctor) (i 'params)
                   (apply i 'body (concat (mapcat (fn [[f vn]]
                                                    [(i 'ldstr impl-ns) (i 'ldstr vn)
                                                     (i 'call clj-var-type "internPrivate" (i string-type string-type) clj-var-type)
                                                     (i 'stsfld name f)])
                                                  var-fields)
                                          (when (truthy? load-impl?)
                                            [(i 'ldstr impl-ns) (i 'call clojure-ops-type "GenClassLoadImpl" (i string-type) void-type)])
                                          [(i 'ret)])))]
               (map ctor-def ctors)
               (map method-def methods)
               (when factory
                 (map (fn [[ps]]
                        (let [ps (sl ps)]
                          (i 'method factory (i 'attrs 'static) (apply i 'params (map tname ps)) (i 'ret name)
                             (apply i 'body (concat (map #(i 'ldarg %) (range (count ps)))
                                                    [(i 'newobj name (apply i (map tname ps))) (i 'ret)])))))
                      ctors))
               (when main
                 [(i 'method "Main" (i 'attrs 'static) (i 'params "System.String[]") (i 'ret void-type)
                     (i 'body (i 'ldsfld name main) (i 'ldarg 0)
                        (i 'call clojure-ops-type "GenClassMain" (i clj-var-type "System.String[]") void-type) (i 'ret)))])
               (mapcat (fn [[decl f ft static? get set]]
                         (let [static? (truthy? static?)]
                           (concat
                             (when (truthy? get)
                               [(apply i 'method get (concat (when static? [(i 'attrs 'static)])
                                                             [(i 'params) (i 'ret ft)
                                                              (apply i 'body (concat (if static? [(i 'ldsfld decl f)] [(i 'ldarg 0) (i 'ldfld decl f)])
                                                                                     [(i 'ret)]))]))])
                             (when (truthy? set)
                               [(apply i 'method set (concat (when static? [(i 'attrs 'static)])
                                                             [(i 'params ft) (i 'ret void-type)
                                                              (apply i 'body (concat (if static? [(i 'ldarg 0) (i 'stsfld decl f)]
                                                                                         [(i 'ldarg 0) (i 'ldarg 1) (i 'stfld decl f)])
                                                                                     [(i 'ret)]))]))]))))
                       exposes)
               (map (fn [[pub ret ptypes decl sname]]
                      (let [ptypes (sl ptypes)]
                        (i 'method pub (apply i 'params ptypes) (i 'ret ret)
                           (apply i 'body (concat [(i 'ldarg 0)] (ldargs (count ptypes)) [(i 'call decl sname (apply i ptypes) ret) (i 'ret)])))))
                    exposes-methods))))
    [name]))

(defn- convert-return!
  "栈上的 object → 声明类型 t"
  [mc t]
  (cond
    (= (tname t) obj) nil
    (value-type? t) (emit! mc (i 'ldtoken (tname t)) (i 'call type-type "GetTypeFromHandle" (i type-handle-type) type-type)
                           (i 'call clojure-ops-type "ConvertReturn" (i obj type-type) obj) (i 'unbox.any (tname t)))
    :else (emit! mc (i 'castclass (tname t)))))

(defn- deftype-method
  "方法体：this 为 ldarg 0；原始类型的参数在入口装箱进局部变量；字段读写 this 的字段；返回值转换为声明类型"
  [u class le spec mname fvars fnames caps cnames]
  (nanopass-case [L3 LambdaExpr] le
    [(lambda ~nm (~x1* ...) ~cl* ...)
     (nanopass-case [L3 Clause] (first cl*)
       [(clause (~x2* ...) ~x ~body)
        (let [mc (make-method-ctx u class false nil nil nil)
              iface (spec-iface spec) ret (spec-ret spec) ptypes (spec-params spec)
              entry (with-buffer mc
                      (fn []
                        (hput! (:vars mc) (first x2*) [:self])
                        (doseq [[p t j] (map vector (rest x2*) ptypes (iterate inc 1))]
                          (if (value-type? t)
                            (let [l (new-local! mc obj)]
                              (emit! mc (i 'ldarg j) (i 'box (tname t)) (i 'call clojure-ops-type "BoxArg" (i obj) obj) (i 'stloc l))
                              (hput! (:vars mc) p [:local l]))
                            (hput! (:vars mc) p [:arg j])))
                        (doseq [[f n] (map vector fvars fnames)] (hput! (:vars mc) f [:dfield n]))
                        (doseq [[c n] (map vector caps cnames)] (hput! (:vars mc) c [:field n]))))]
          (emit-code! mc entry)
          (reset! (:big? mc) (bigger-than? body big-body-nodes))
          (init-clearing! mc body)
          (binding [*clear-args-by-ref* false]
            (cond
              (= (tname ret) void-type) (do (cg body mc :effect) (emit! mc (i 'ret)))
              (= (tname ret) obj) (cg body mc :tail)
              :else (do (cg body mc :value) (convert-return! mc ret) (emit! mc (i 'ret)))))
          (apply i 'method mname (apply i 'attrs (if iface [] ['override])) (apply i 'params (map tname ptypes)) (i 'ret (tname ret))
                 (apply i 'locals @(:locals mc))
                 (i 'clr-namespaces)
                 (concat (when iface [(i 'implements iface (spec-name spec))])
                         [(apply i 'body @(:buffer mc))])))])]))

(defn- deftype-stub
  "接口中没有直接实现的方法：转调同名同参的方法，或抛 NotImplementedException"
  [class [iface name ret ptypes target] specs mnames]
  (let [ptypes (sl ptypes) target (when (truthy? target) target)]
    (i 'method (str iface "." name) (i 'attrs 'private) (apply i 'params (map tname ptypes)) (i 'ret (tname ret))
       (i 'implements iface name)
       (apply i 'body
              (if target
                (let [sp (nth specs target) tret (spec-ret sp)]
                  (concat [(i 'ldarg 0)] (ldargs (count ptypes))
                          [(i 'call class (nth mnames target) (apply i (map tname (spec-params sp))) (tname tret))]
                          (cond
                            (= (tname ret) void-type) (if (= (tname tret) void-type) [] [(i 'pop)])
                            (= (tname tret) (tname ret)) []
                            :else (concat
                                    (cond (= (tname tret) void-type) [(i 'ldnull)]
                                          (value-type? tret) [(i 'box (tname tret))]
                                          :else [])
                                    (cond (= (tname ret) obj) []
                                          (value-type? ret) [(i 'ldtoken (tname ret)) (i 'call type-type "GetTypeFromHandle" (i type-handle-type) type-type)
                                                             (i 'call clojure-ops-type "ConvertReturn" (i obj type-type) obj) (i 'unbox.any (tname ret))]
                                          :else [(i 'castclass (tname ret))])))
                          [(i 'ret)]))
                (throw-not-implemented (str iface "." name " is not implemented by " class)))))))

(defn- deftype-class!
  "deftype* / reify* 的类（原版 deftype-class!）。返回 [类名 自由变量…]。"
  [mc d x* le*]
  (let [u (:unit mc)
        name (dinfo-class d)
        reify? (deftype-reify? d)
        fields (dinfo-fields d)
        fnames (map first fields)
        caps (deftype-caps x* le*)
        cnames (map #(str "c" %) (range (count caps)))
        specs (dinfo-methods d)
        mnames (loop [ss specs used [] acc []]
                 (if (empty? ss)
                   acc
                   (let [sp (first ss)
                         k (cons (spec-name sp) (map tname (spec-params sp)))
                         n (if (or (nil? (spec-iface sp)) (not (some #(= k %) used))) (spec-name sp) (str (spec-iface sp) "." (spec-name sp)))]
                     (recur (rest ss) (conj used k) (conj acc n)))))
        ctor-fields (if reify? (concat cnames ["__meta"]) fnames)]
    (when (and (not reify?) (seq caps))
      (throw (ex-info (str "deftype*: deftype* methods cannot close over locals " (vec caps)) {:caps caps})))
    (hput! (:type-names u) name true)
    (add-class! u
      (apply i 'type name (i 'attrs 'sealed) (apply i 'interfaces (dinfo-ifaces d))
             (concat
               (if reify?
                 (map #(i 'field % obj) ctor-fields)
                 (map (fn [[f _ vol?]] (apply i 'field f obj (when (truthy? vol?) ['volatile]))) fields))
               [(i 'method ".ctor" (i 'attrs 'ctor) (apply i 'params (objs (count ctor-fields)))
                   (apply i 'body (concat [(i 'ldarg 0) (i 'call obj ".ctor" () void-type)]
                                          (mapcat (fn [f j] [(i 'ldarg 0) (i 'ldarg j) (i 'stfld name f)]) ctor-fields (iterate inc 1))
                                          [(i 'ret)])))]
               (map (fn [le sp mn] (deftype-method u name le sp mn x* fnames caps cnames)) le* specs mnames)
               (map (fn [st] (deftype-stub name st specs mnames)) (dinfo-stubs d))
               (if reify?
                 [(i 'method "meta" (i 'params) (i 'ret pmap-type) (i 'implements "clojure.lang.IMeta" "meta")
                     (i 'body (i 'ldarg 0) (i 'ldfld name "__meta") (i 'castclass pmap-type) (i 'ret)))
                  (i 'method "withMeta" (i 'params pmap-type) (i 'ret iobj-type) (i 'implements "clojure.lang.IObj" "withMeta")
                     (apply i 'body (concat (mapcat (fn [c] [(i 'ldarg 0) (i 'ldfld name c)]) cnames)
                                            [(i 'ldarg 1) (i 'newobj name (apply i (objs (count ctor-fields)))) (i 'ret)])))]
                 (concat
                   [(i 'method "getBasis" (i 'attrs 'static) (i 'params) (i 'ret "clojure.lang.IPersistentVector")
                       (i 'body (i 'ldconst (constant-index! u (dinfo-basis d))) (i 'castclass "clojure.lang.IPersistentVector") (i 'ret)))]
                   (record-members name d))))))
    (cons name caps)))

(defn- deftype-node!
  "deftype*：值为生成的类型（运行时登记，classForName 取最新的）；reify*：值为新实例（自由变量与 nil 元数据作构造参数）"
  [mc d x* le* ctx]
  (let [c (cond (deftype-interface? d) (interface-class! mc d)
                (deftype-proxy? d) (proxy-class! mc d)
                (deftype-genclass? d) (genclass-class! mc d)
                :else (deftype-class! mc d x* le*))]
    (if (deftype-reify? d)
      (do (doseq [v (rest c)] (ref-var! mc v true))
          (emit! mc (i 'ldnull) (i 'newobj (first c) (apply i (concat (objs (count (rest c))) [obj])))))
      (emit! mc (i 'ldtoken (first c)) (i 'call type-type "GetTypeFromHandle" (i type-handle-type) type-type)
             (i 'call clojure-ops-type "DefineType" (i obj) obj)))
    (finish-value! mc ctx)))

;; —— 常量的编码（AOT 单元，cleat-089；原版 (clojure compile) 的 encode-clojure-constant）——
;; 元素经 Cleat.Core.Compiler.RawData 原样取出（宿主调用会把 Int32 换成 long，而 Int32 编码为 (fix n)、long 为 (long n)）。
;; 只有 Clojure 的值（cleat-092）；记录、集合按值重建，不共享（同原版）。

(def ^:private raw-items Cleat.Core.Compiler.RawData/Items)

(defn- map-raw
  "对 x 的每个元素（原样）调 f，结果组成向量。"
  [f x]
  (let [v (raw-items x) n (count v)]
    (loop [k 0 acc []] (if (< k n) (recur (inc k) (conj acc (f (v k)))) acc))))

(defn- double->hex
  "double → IEEE 位模式（大端十六进制 16 位）"
  [x]
  (let [bs (BitConverter/GetBytes (double x))]
    (when BitConverter/IsLittleEndian (Array/Reverse bs))
    (.Replace (BitConverter/ToString bs) "-" "")))

(defn- encoded-text
  "编码的文本（哈希 map / set 的项按它排序：迭代顺序依赖键的哈希，Type、Var 等的哈希按对象标识、每个进程不同；
  加载时按值重建，顺序不影响语义，排序使两次编译的 Lcil 相同——镜像不动点）。"
  [enc]
  (let [sb (System.Text.StringBuilder.)]
    (letfn [(w [x]
              (cond
                (seq? x) (do (.Append sb \() (loop [xs x first? true]
                                                (when (seq xs)
                                                  (when-not first? (.Append sb \space))
                                                  (w (first xs)) (recur (rest xs) false)))
                             (.Append sb \)))
                (string? x) (do (.Append sb \") (.Append sb (-> ^String x (.Replace "\\" "\\\\") (.Replace "\"" "\\\""))) (.Append sb \"))
                (true? x) (.Append sb "#t")
                (false? x) (.Append sb "#f")
                :else (.Append sb (str x))))]
      (w enc)
      (str sb))))

(defn- sorted-items [encs] (sort-by encoded-text compare encs))
(defn- sorted-pairs [kvs]
  (mapcat identity (sort-by #(encoded-text (first %)) compare (partition 2 kvs))))

(defn- encode-constant
  "Clojure 值的编码（designs/05 §3、LCIL.md §8）。带元数据的符号、集合写成 (meta 元数据 对象)。"
  [x]
  (let [encode encode-constant
        with-meta (fn [enc] (let [m (when (instance? clojure.lang.IMeta x) (.meta ^clojure.lang.IMeta x))]
                              (if (nil? m) enc (i 'meta (encode m) enc))))
        named (fn [ns nm] (if (nil? ns) [nm] [ns nm]))]
    (cond
      (nil? x) (i (symbol "nil"))
      (instance? clojure.lang.Var x) (i 'var (str (.Name (.Namespace ^clojure.lang.Var x))) (.Name (.Symbol ^clojure.lang.Var x)))
      (instance? Int64 x) (i 'long x)
      (instance? Int32 x) (i 'fix x)
      (instance? Double x) (i 'double (double->hex x))
      (instance? Boolean x) (if (true? x) (i (symbol "true")) (i (symbol "false")))
      (char? x) (i 'char (int x))
      (string? x) (i 'str x)
      (instance? clojure.lang.Keyword x) (apply i 'kw (named (.Namespace ^clojure.lang.Keyword x) (.Name ^clojure.lang.Keyword x)))
      (instance? clojure.lang.Symbol x) (with-meta (apply i 'csym (named (.Namespace ^clojure.lang.Symbol x) (.Name ^clojure.lang.Symbol x))))
      (instance? clojure.lang.IPersistentVector x) (with-meta (apply i 'cvec (map-raw encode x)))
      (instance? clojure.lang.PersistentTreeMap x) (with-meta (apply i 'sorted-map (map-raw encode x)))
      (instance? clojure.lang.IPersistentMap x) (with-meta (apply i 'map (sorted-pairs (map-raw encode x))))
      (instance? clojure.lang.PersistentTreeSet x) (with-meta (apply i 'sorted-set (map-raw encode x)))
      (instance? clojure.lang.IPersistentSet x) (with-meta (apply i 'set (sorted-items (map-raw encode x))))
      (or (instance? clojure.lang.IPersistentList x) (instance? clojure.lang.ISeq x)) (with-meta (apply i 'clist (map-raw encode x)))
      (instance? clojure.lang.BigInt x) (i 'bigint (str x))
      (instance? System.Numerics.BigInteger x) (i 'bigint (str x))
      (instance? clojure.lang.BigDecimal x) (i 'bigdec (str x))
      (instance? clojure.lang.Ratio x) (i 'ratio (str x))
      (instance? System.Text.RegularExpressions.Regex x) (i 'regex (str x))
      (instance? Type x) (i 'type (.FullName ^Type x))
      :else (throw (ex-info (str "encode-constant: cannot encode object " (pr-str x)) {:object x})))))

(defn- encode-constants
  "常量表 → 编码的列表。cs 的元素原样取（见 RawData）。"
  [cs]
  (map-raw encode-constant cs))

;; —— 编译单元（原版 generate-unit）——

(defn- init-name [k] (str "Init" k))

(defn- top-level-method [u form k]
  (let [mc (make-method-ctx u (:module u) true nil nil nil)]
    (cg form mc :tail)
    (finish-method mc (init-name k) ['static] [])))

(defn generate-unit
  "L3 顶层表达式 → [Lcil 常量列表]（原版 generate-unit）。loop-flags：与 forms 对齐的\"含循环\"标志（只对它们做类型推断）。
  live?：内存模式（eval）——常量不编码，Lcil 里写 (live-constants n)，常量对象交给发射器。"
  [forms assembly-name module-name loop-flags live?]
  (let [u (make-unit assembly-name module-name)]
    (run! hclear! [var-types sound-types fixed-types var-facts assigned-vars])
    (run! collect-assigned! forms)
    (when opt-infer?
      (if (and loop-flags (= (count loop-flags) (count forms)))
        (doseq [[f lp?] (map vector forms loop-flags)] (when lp? (infer-var-types-into! f var-types)))
        (doseq [f forms] (infer-var-types-into! f var-types))))
    (let [inits (mapv (fn [f k] (top-level-method u f k)) forms (range))
          n (count inits)
          entry (i 'method "Initialize" (i 'attrs 'static) (i 'params) (i 'ret obj)
                   (apply i 'body
                          (cond
                            (zero? n) [(i 'ldnull) (i 'ret)]
                            :else (concat (mapcat (fn [k] [(i 'call module-name (init-name k) () obj) (i 'pop)]) (range (dec n)))
                                          [(i 'call module-name (init-name (dec n)) () obj) (i 'ret)]))))
          module (apply i 'type module-name (i 'attrs 'abstract 'sealed) (concat inits [entry]))
          cs @(:consts u)
          lcil (apply i 'lcil 1
                      (i 'assembly assembly-name (apply i 'references core-assembly @(:references u)) (i 'runtime 'clojure)
                         (i 'entry module-name "Initialize"))
                      (concat
                        (cond
                          (empty? cs) []
                          live? [(i 'live-constants (count cs))]
                          ;; 原版在 canonical-gensyms 时（boot 镜像）把编码里的 gensym 改成规范名；Clojure 编译器的临时名本来就确定，不需要
                          :else [(apply i 'constants (map-indexed (fn [k e] (i k e)) (encode-constants cs)))])
                        (cons module @(:classes u))))]
      [lcil cs])))
