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
            [cleat.compiler.languages :as l :refer [L3]]
            [cleat.compiler.loops :as loops])
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
(def num-type "Cleat.Core.Runtime.Num")   ; 不装箱的未定类型数（cleat-102）
(def long-box-type "Cleat.Core.Runtime.LongBox")   ; 小整数装箱缓存（cleat-109）
(def ^:private narrow-value-types #{"System.Byte" "System.SByte" "System.Int16" "System.UInt16" "System.Single"})   ; 较窄的数值类型（cleat-111）
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
(def opt-host? (not (opt-off? "host")))
(def opt-loopspec? (not (opt-off? "loopspec")))   ; 循环的 long / double 推测：快慢两份循环（cleat-103）
(def opt-typed? (not (opt-off? "typed")))         ; 无提示函数的类型化入口（cleat-104）
(def opt-joinraw? (not (opt-off? "joinraw")))     ; 原始用途穿过 if / begin 的尾位置（cleat-108）
(def ^:private live-unit? (atom false))   ; 当前编译单元是内存模式（eval）：生成的代码与先前求值生成的类在同一进程（cleat-110）；分层编译时 if 的值经局部变量汇合（cleat-114）
(def opt-boxcache? (not (opt-off? "boxcache")))   ; long 装箱走 LongBox（cleat-109）
(def opt-imagetier? (not (opt-off? "imagetier")))   ; 镜像与包里的函数类也按 cleat-114 的规则分层编译（cleat-117）
(def opt-arrayspec? (not (opt-off? "arrayspec")))
(def opt-scalar? (not (opt-off? "scalar")))         ; 循环变量的 deftype 标量替换（cleat-123）
(def opt-collapse? (not (opt-off? "collapse")))     ; 折叠内联子句的 arity 标签（cleat-123）   ; 循环里无提示的数组变量按元素类型推测（cleat-127）
(def opt-typefeedback? (not (opt-off? "typefeedback")))   ; eval 候选的剖面记录（cleat-128；量化后 129 关闭）
(def opt-sigspec? (not (opt-off? "sigspec")))     ; 无提示递归函数的 LL / DD 签名特化与缓存分发（cleat-132）
(defn- box-long [] (if opt-boxcache? (i 'call long-box-type "Get" (i int64-type) obj) (i 'box int64-type)))
;; double 经不内联的 LongBox.Double 装箱：内联的 box 在值存活期间调用分配，SysV 的 xmm 寄存器都由调用方保存，
;; RyuJIT 于是把循环里的 double 变量整个放在栈上（每次迭代一次存取，sumfp 慢 4 倍）
(def opt-boxdouble? (not (opt-off? "boxdouble")))
(defn- box-double [] (if opt-boxdouble? (i 'call long-box-type "Double" (i double-type) obj) (i 'box double-type)))
(def opt-num? (not (opt-off? "num")))     ; Num：未定类型数的局部 / 循环变量 / 形参（cleat-102）   ; 宿主调用（方法 / 字段 / 属性 / 构造器）的编译期静态决议（cleat-042 / 101）

;; —— 编译单元（原版 unit）——

(defn- make-unit [assembly-name module-name]
  {:name assembly-name :module module-name
   :classes (atom []) :consts (atom []) :nconsts (atom 0) :const-index (ht)
   :namespaces (atom []) :references (atom []) :counter (atom 0) :type-names (ht)
   :var-fields (atom [])})   ; eval 单元里 Var 的 static readonly 字段：[Var 字段名] 的有序表（cleat-114）

(defn- add-class! [u c] (swap! (:classes u) conj c))

(def ^:private vars-type "$Vars")

(declare constant-index! emit!)

(defn- load-var-object!
  "Var 对象压栈（类型为 Var）。eval 单元（cleat-114）：$Vars 的 static readonly 字段（JIT 在类型初始化后当常量，没有常量表的数组读与 castclass，
  JVM 的 fn 类同样把 Var 放在 static final 字段里）；镜像与包照旧 ldconst + castclass（字节不变）。"
  [mc d]
  (if @live-unit?
    (let [u (:unit mc)
          f (or (some (fn [[v n]] (when (identical? v d) n)) @(:var-fields u))
                (let [n (str "v" (count @(:var-fields u)))] (swap! (:var-fields u) conj [d n]) n))]
      (emit! mc (i 'ldsfld vars-type f)))
    (emit! mc (i 'ldconst (constant-index! (:unit mc) d)) (i 'castclass clj-var-type))))

(defn- vars-class
  "$Vars：各 Var 的 static readonly 字段与填充它们的类型初始化器（从常量表取）；没有 Var 时 nil。"
  [u]
  (let [vfs @(:var-fields u)]
    (when (seq vfs)
      (apply i 'type vars-type (i 'attrs 'abstract 'sealed)
             (concat (map (fn [[_ n]] (i 'field n clj-var-type 'static 'readonly)) vfs)
                     [(i 'method ".cctor" (i 'attrs 'cctor) (i 'params)
                         (apply i 'body (concat (mapcat (fn [[v n]] [(i 'ldconst (constant-index! u v)) (i 'castclass clj-var-type) (i 'stsfld vars-type n)]) vfs)
                                                [(i 'ret)])))])))))

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
(def ^:private num-aliases (ht))    ; 只是 Num 形参副本的变量 → 形参（cleat-102：共用形参的位置，装箱用途仍读原实参）
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
;; Num 路径的算术（cleat-102）：long-ops 加 divide（两个 long 相除可能是 Ratio，没有 long 的原始路径）
(def ^:private numbers-num-ops #{"add" "minus" "multiply" "divide" "inc" "dec"})
(def ^:private num-methods {"add" "Add" "minus" "Subtract" "multiply" "Multiply" "divide" "Divide" "inc" "Inc" "dec" "Dec"
                            "lt" "LessThan" "lte" "LessOrEqual" "gt" "GreaterThan" "gte" "GreaterOrEqual" "equiv" "Equiv"})

(defn- numbers-type? [tp]
  (or (= tp "clojure.lang.Numbers") (and (instance? Type tp) (= (.FullName ^Type tp) "clojure.lang.Numbers"))))

(defn- empty-gen? [g] (or (nil? g) (false? g)))

(defn- numbers-arith-op [d0 d1 e*]
  (when (and (= d0 "CallStatic") (= d1 3) (<= 4 (count e*) 5) (numbers-type? (quoted-datum (first e*))))
    (let [op (quoted-datum (second e*)) gen (quoted-datum (nth e* 2)) argc (- (count e*) 3)]
      (when (and (string? op) (empty-gen? gen)
                 (cond (#{"inc" "dec"} op) (= argc 1)
                       (= op "minus") true
                       (= op "divide") (= argc 2)
                       (numbers-long-ops op) (= argc 2)
                       (numbers-bool-ops op) (= argc 2)
                       :else false))
        op))))

(declare expr-type host-call-signature)

(defn- numbers-arith-args [e env]
  (nanopass-case [L3 Expr] e
    [(clj-call ~d0 ~d1 ~e* ...)
     (let [op (numbers-arith-op d0 d1 e*)]
       ;; 只有 long 的原始重载存在的运算（divide 两个 long 可能是 Ratio，没有 long → long 的重载；它只走 Num 路径）
       (when (and op (or (numbers-long-ops op) (numbers-bool-ops op)) (every? #(= :l64 (expr-type % env)) (drop 3 e*)))
         (cons op (drop 3 e*))))]
    [else nil]))

;; 原始签名（cleat-042）
(def primifs-prefix "clojure.lang.primifs.")
(defn- sig-char-type [c] (case c \L int64-type \D double-type obj))
(defn- sig-char-raw [c] (case c \L :l64 \D :fl nil))
(defn- sig-params [^String sig] (seq (subs sig 0 (dec (count sig)))))
(defn- sig-ret [^String sig] (nth sig (dec (count sig))))
(defn- sig-param-types [sig] (map sig-char-type (sig-params sig)))

(declare host-call-type l3-some?)

(defn expr-type
  "表达式的类型（:fx :fl :int … 与 :l64）或 nil；env：变量 → 类型 | :top。不在 env 里的变量查 var-facts（原版 expr-type 的 Clojure 子集）。"
  [e env]
  (nanopass-case [L3 Expr] e
    [(quote ~d) (if (instance? Int64 d) :l64 (datum-type d))]
    [(clj-call ~d0 ~d1 ~e* ...)
     (cond
       (= d0 "Tagged") (expr-type (second e*) env)   ; 静态类型标注（cleat-101 的延续）：透明
       (= d0 "Speculated") (quoted-datum (first e*))   ; 推测循环的快份入口：isinst 检查过的实参（cleat-103）
       (= d0 "TypedSelf") (sig-char-raw (sig-ret (first (quoted-datum (first e*)))))   ; 类型化体里的自调用（cleat-104）
       (= d0 "TypedEntry") (expr-type (second e*) env)
       (= d0 "SigEntry") (expr-type (last e*) env)   ; 签名分发对外仍是通用体返回的对象（cleat-132）
       (= d0 "PrimInvoke")
       (sig-char-raw (sig-ret (quoted-datum (first e*))))
       :else
       (let [op (numbers-arith-op d0 d1 e*)
             ts (when (and op (numbers-num-ops op)) (map #(expr-type % env) (drop 3 e*)))]
         (cond (and ts (some #(= :num %) ts)) :num   ; 有 Num 操作数的算术按 Num 算（cleat-102）
               (and ts (numbers-long-ops op) (every? #(= :l64 %) ts)) :l64
               (and ts (numbers-long-ops op) (every? #{:l64 :top} ts)) :top
               :else (host-call-type d0 d1 e* env))))]
    [~x (if (hhas? env x) (hget env x) (hget var-facts x))]
    [(if ~e0 ~e1 ~e2) (type-join (expr-type e1 env) (expr-type e2 env))]
    [(begin ~e* ... ~e) (expr-type e env)]
    [(let ([~x* ~e*] ...) ~body) (expr-type body env)]
    ;; 不回跳的标签（内联子句外面的 arity 标签）的值就是体的值（cleat-123）；真正的循环不推（出口变量在推断早期可能被暂定成别的类型）
    [(label ~x (~x* ...) ~body0 ~body1)
     (when-not (l3-some? (fn [j] (nanopass-case [L3 Expr] j [(jump ~x2 ~e* ...) (= x2 x)] [else false])) body1 true)
       (expr-type body1 env))]
    [(jump ~x ~e* ...) :top]
    [(throw ~e) :top]
    [else nil]))

;; —— 宿主调用的静态决议（cleat-042 第 3 项，原版 host-call-*）——

(defn- host-call-layout
  "[static? 实例或nil 实参]，不是 CallStatic / CallInstance / GetStaticField / GetInstanceField 时 nil。"
  [d0 d1 e*]
  (cond (and (= d0 "CallStatic") (= d1 3) (>= (count e*) 3)) [true nil (drop 3 e*)]
        (and (= d0 "NewOf") (= d1 1) (>= (count e*) 1)) [true nil (rest e*)]   ; 构造器（cleat-101）
        (and (= d0 "CallInstance") (= d1 4) (>= (count e*) 4)) [false (nth e* 2) (drop 4 e*)]
        (and (= d0 "GetStaticField") (false? d1) (= (count e*) 2)) [true nil ()]
        (and (= d0 "GetInstanceField") (false? d1) (= (count e*) 3)) [false (nth e* 2) ()]
        :else nil))

(declare static-arg-type)

(defn- host-call-signature
  "静态决议：[签名 static? 实例 实参] / :pending / nil。签名为 ClojureStaticCalls.Resolve 的结果 object[]。env 缺省用 sound-types。"
  ([d0 d1 e*] (host-call-signature d0 d1 e* nil))
  ([d0 d1 e* env]
   (let [layout (when opt-host? (host-call-layout d0 d1 e*))
         env (if (or (nil? env) (identical? env var-types)) sound-types env)]
     (when layout
       (let [t (quoted-datum (first e*))
             ctor? (= d0 "NewOf")
             nm (if ctor? "" (quoted-datum (second e*)))
             gen (if (#{"CallStatic" "CallInstance"} d0) (quoted-datum (nth e* (if (first layout) 2 3))) nil)]
         (when (and (instance? Type t) (string? nm) (empty-gen? gen))
           (let [ats (mapv #(static-arg-type % env) (nth layout 2))]
             (if (some #{:pending} ats)
               :pending
               (let [^objects sig (cond ctor? (Cleat.Core.Runtime.ClojureStaticCalls/ResolveCtor t (object-array ats))
                                        (#{"CallStatic" "CallInstance"} d0) (Cleat.Core.Runtime.ClojureStaticCalls/Resolve t nm (object-array ats) (first layout))
                                        :else (Cleat.Core.Runtime.ClojureStaticCalls/ResolveField t nm (first layout)))]
                 (when (pos? (alength sig)) (into [sig] layout)))))))))))

(def ^:private host-ret-raws
  ;; 宿主调用结果的原始用途（cleat-101 / 118）：Int32 与较窄的整数加宽成 l64、Single 加宽成 fl——与装箱路径的转换相同
  {"System.Int64" :l64 "System.Int32" :l64 "System.Int16" :l64 "System.SByte" :l64 "System.Byte" :l64 "System.UInt16" :l64
   "System.Double" :fl "System.Single" :fl})
(defn- host-ret-raw [ret] (host-ret-raws ret))
(defn- emit-ret-widen!
  "原始用途的加宽指令（结果已在栈上）"
  [mc ret]
  (case ret
    ("System.Int32" "System.Int16" "System.SByte") (emit! mc (i 'conv.i8))
    ("System.Byte" "System.UInt16") (emit! mc (i 'conv.u8))
    "System.Single" (emit! mc (i 'conv.r8))
    nil))

(defn- host-call-type [d0 d1 e* env]
  (let [sl (host-call-signature d0 d1 e* env)]
    (cond (= sl :pending) :top
          (nil? sl) nil
          :else (host-ret-raw (str (aget ^objects (first sl) 3))))))

(defn- tagged-parts
  "(clj-call \"Tagged\" #f 'T e)（分析器给宿主调用实参的静态类型标注）：[T e]，否则 nil。"
  [e]
  (nanopass-case [L3 Expr] e
    [(clj-call ~d0 ~d1 ~e* ...) (when (and (= d0 "Tagged") (= 2 (count e*))) [(quoted-datum (first e*)) (second e*)])]
    [else nil]))

(defn- static-arg-type [e env]
  (let [by-type #(case (expr-type e env) :l64 "System.Int64" :fl "System.Double" :top :pending nil)]
    (if-let [[t inner] (tagged-parts e)]
      (or (static-arg-type inner env) (when (instance? Type t) (.AssemblyQualifiedName ^Type t)))
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
                 (and sl (or (#{"System.Int32" "System.Boolean" "System.Char" "System.String"} (str (aget ^objects (first sl) 3)))
                             (narrow-value-types (str (aget ^objects (first sl) 3)))))   ; cleat-111
                 (str (aget ^objects (first sl) 3))
                 :else (by-type)))
         (by-type))]
      [else (by-type)]))))

;; —— 推断（iron-034c，原版 infer-walk / infer-var-types-into!，Clojure 子集）——

(defn- inlined-binding? [x] (str/ends-with? (str x) "$inl"))

(declare fix-clause-params! dinfo-kind dinfo-fields field-raw)

(define-pass infer-walk L3 [ir add-def! exclude! labels candidate? use! param!] -> * []
  (MaybeTyped Expr [ir] -> * []
    [(quote ~d) (or (instance? Int64 d) (boolean (datum-type d)))]
    [~x (candidate? x)]
    [(clj-call ~d0 ~d1 ~e* ...) (or (#{"PrimInvoke" "TypedSelf"} d0) (boolean (host-call-layout d0 d1 e*)))]
    [(if ~e0 ~e1 ~e2) (or (MaybeTyped e1) (MaybeTyped e2))]
    [(begin ~e* ... ~e) (MaybeTyped e)]
    [(let ([~x* ~e*] ...) ~body) (MaybeTyped body)]
    [(jump ~x ~e* ...) true]
    [(throw ~e) true]
    [else false])
  (Body Clause [ir] -> * []
    [(clause (~x* ...) ~x ~body) (run! param! x*) (Expr body)])
  (Raw Expr [ir] -> * []
    [~x (use! x true)]
    ;; 分支汇合（cleat-108）：if 两支、begin 最后一项的值就是整个表达式的值，原始发射（cg-raw-join!）直接在各支里原始求值
    [(if ~e0 ~e1 ~e2) (if opt-joinraw? (do (Expr e0) (Raw e1) (Raw e2)) (Expr ir))]
    [(begin ~e* ... ~e) (if opt-joinraw? (do (run! Expr e*) (Raw e)) (Expr ir))]
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
       (= d0 "TypedSelf") (do (Expr (second e*))
                              (doseq [[a c] (map vector (drop 2 e*) (sig-params (first (quoted-datum (first e*)))))]
                                (if (sig-char-raw c) (Raw a) (Expr a))))
       :else (if-let [lay (host-call-layout d0 d1 e*)]
               (do (when (second lay) (Expr (second lay))) (run! Raw (nth lay 2)))
               (run! Expr e*)))]
    [(clj-static-call ~d0 ~d1 ~e* ...) (run! Expr e*)]
    [(check-initialized ~e) (Expr e)]
    [(clj-case ~d ~e0 ~e1 ~e* ...) (Expr e0) (Expr e1) (run! Expr e*)]
    [(clj-deftype ~d (~x* ...) ~le* ...)
     ;; ^long / ^double 字段（cleat-112）：固定类型，方法体里的算术按原始类型算；其余字段不参与推断
     (let [fts (if (false? (dinfo-kind d)) (map #(nth % 3 false) (dinfo-fields d)) [])]   ; 只有 deftype*（reify / proxy / 接口 / gen-class 的描述布局不同）
       (doseq [[x ft] (map vector x* (concat fts (repeat false)))]
         (if-let [r (field-raw ft)] (hput! fixed-types x r) (exclude! x))))
     (run! Expr le*)]
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
  "原版 infer-var-types-into!。seeds：推测循环（cleat-103）的试算用——体外变量的已知类型（第一遍推断的 sound-types），放进环境、不参与不动点。"
  ([form out] (infer-var-types-into! form out nil))
  ([form out seeds]
  (let [defs (ht) excluded (ht) labels (ht) raw-uses (ht) boxed-uses (ht) params (ht)
        defs-order (atom [])]
    (infer-walk form
                (fn [x e]
                  (when-not (hhas? defs x) (swap! defs-order conj x))
                  (hput! defs x (let [l (or (hget defs x) ())] (if e (cons e l) l))))
                (fn [v] (hput! excluded v true))
                labels
                (fn [x] (hhas? defs x))
                (fn [x raw?] (when (or (hhas? defs x) (hhas? params x)) (hupdate! (if raw? raw-uses boxed-uses) x inc 0)))
                (fn [p] (hput! params p true)))
    (let [env (ht) users (ht)
          cands (filterv #(not (hget excluded %)) @defs-order)   ; 不动点与顺序无关（单调，从 top 出发）
          deps (fn deps [e]
                 (nanopass-case [L3 Expr] e
                   [~x [x]]
                   [(clj-call ~d0 ~d1 ~e* ...)
                    (cond (= d0 "Tagged") (deps (second e*))
                          :else (if-let [lay (host-call-layout d0 d1 e*)] (vec (mapcat deps (nth lay 2))) []))]
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
      (when seeds (doseq [k (keys-of seeds)] (hput! env k (hget seeds k))))
      (doseq [x cands] (hput! env x :top))
      (doseq [x cands e (or (hget defs x) ())]
        (doseq [v (deps e)] (hput! users v (cons x (or (hget users v) ())))))
      (propagate! cands)
      (run! decide! cands)
      ;; Num（cleat-102）：没定成 :fx / :fl / :l64 的——
      ;;   形参：有原始用途（Numbers 算术 / 比较的操作数）就放进 Num（入口 FromObject 一次；装箱用途读原来的实参，不另花钱）；
      ;;   let 变量与 label 形参：全部定义都是数值类型的表达式（含 Num 变量、形参）且原始用途不少于装箱用途——只增不减的不动点。
      ;; 这是表示，不是类型事实：不进 var-facts。
      (when opt-num?
        (let [numeric? (fn [t] (contains? #{:fx :fl :int :real :num :l64} t))
              fresh? (fn [x] (and (not (hhas? out x)) (not (hhas? fixed-types x)) (not (hget excluded x))))]
          (doseq [x (keys-of params)]
            (when (and (fresh? x) (pos? (or (hget raw-uses x) 0)))
              (hput! env x :num) (hput! out x :num)))
          ;; 形参的副本（fn 的 arity 循环把形参复制进标签变量；(let [x p] …)）：全部定义都是对同一个 Num 形参（或其别名）的引用时
          ;; 做别名——否则副本的装箱用途每次 Num.Box 新建对象，还丢掉 NaN 的同一性相等
          (let [ref-of (fn [e] (nanopass-case [L3 Expr] e [~x x] [else nil]))
                root (fn root [q] (if-let [r (hget num-aliases q)] (root r) q))]
            (loop []
              (when (reduce (fn [changed x]
                              (let [ds (or (hget defs x) ())
                                    qs (distinct (map ref-of ds))]
                                (if (and (fresh? x) (seq ds) (= 1 (count qs)) (first qs)
                                         (let [q (first qs)] (and (= :num (hget out q)) (or (hget params (root q)) (hhas? num-aliases q)))))
                                  (do (hput! num-aliases x (root (first qs))) (hput! env x :num) (hput! out x :num) true)
                                  changed)))
                            false cands)
                (recur))))
          (loop []
            (when (reduce (fn [changed x]
                            (if (or (not (fresh? x)) (empty? (or (hget defs x) ())))
                              changed
                              (let [ts (map #(expr-type % env) (hget defs x))]
                                (if (and (every? numeric? ts) (<= (or (hget boxed-uses x) 0) (or (hget raw-uses x) 0)))
                                  (do (hput! env x :num) (hput! out x :num) true)
                                  changed))))
                          false cands)
              (recur)))))))))

;; —— 循环推测（cleat-103，skeg iron-071 / 080B）——
;; 最内层 label 循环里初值类型未定的形参：在"它们是 long（double）"的前提下推断能把它们定成原始类型时，生成快慢两份循环——
;; 入口按实参 isinst，成立进快份（形参的初值是 (clj-call "Speculated" #f 'l64 t)，其余由推断完成：原始局部、回边不检查），
;; 否则进原来的循环（object / Num）。Clojure 的 long 溢出抛异常，两份一致，不需要 skeg 的哨兵与重算。

(def ^:private spec-max-nodes 300)
(def ^:dynamic ^:private *spec-changed* (volatile! false))

(defn- spec-simple?
  "可以复制的循环体：没有 lambda / letrec / label / try / 赋值 / box / deftype / def，节点不超过 spec-max-nodes。"
  [e]
  (let [n (volatile! 0)]
    (letfn [(ok? [e]
              (and (<= (vswap! n inc) spec-max-nodes)
                   (nanopass-case [L3 Expr] e
                     [~x true]
                     [(quote ~d) true]
                     [(if ~e0 ~e1 ~e2) (and (ok? e0) (ok? e1) (ok? e2))]
                     [(begin ~e* ... ~e) (and (every? ok? e*) (ok? e))]
                     [(let ([~x* ~e*] ...) ~body) (and (every? ok? e*) (ok? body))]
                     [(call ~e ~e* ...) (and (ok? e) (every? ok? e*))]
                     [(throw ~e) (ok? e)]
                     [(jump ~x ~e* ...) (every? ok? e*)]
                     [(check-initialized ~e) (ok? e)]
                     [(clj-var-ref ~d ~st) true]
                     [(clj-invoke ~e ~e* ...) (and (ok? e) (every? ok? e*))]
                     [(clj-keyword-invoke ~d ~e ~e* ...) (and (ok? e) (every? ok? e*))]
                     [(clj-call ~d0 ~d1 ~e* ...) (every? ok? e*)]
                     [(clj-static-call ~d0 ~d1 ~e* ...) (every? ok? e*)]
                     [(clj-case ~d ~e0 ~e1 ~e* ...) (and (ok? e0) (ok? e1) (every? ok? e*))]
                     [else false])))]
      (ok? e))))

(defn- spec-binders
  "复制体里的绑定名（let 变量、label 名与形参）。"
  [e]
  (let [acc (volatile! [])]
    (letfn [(walk [e]
              (nanopass-case [L3 Expr] e
                [(if ~e0 ~e1 ~e2) (run! walk [e0 e1 e2])]
                [(begin ~e* ... ~e) (run! walk e*) (walk e)]
                [(let ([~x* ~e*] ...) ~body) (vswap! acc into x*) (run! walk e*) (walk body)]
                [(label ~x (~x* ...) ~body0 ~body1) (vswap! acc conj x) (vswap! acc into x*) (walk body0) (walk body1)]
                [(call ~e ~e* ...) (walk e) (run! walk e*)]
                [(throw ~e) (walk e)]
                [(jump ~x ~e* ...) (run! walk e*)]
                [(check-initialized ~e) (walk e)]
                [(clj-invoke ~e ~e* ...) (walk e) (run! walk e*)]
                [(clj-keyword-invoke ~d ~e ~e* ...) (walk e) (run! walk e*)]
                [(clj-call ~d0 ~d1 ~e* ...) (run! walk e*)]
                [(clj-static-call ~d0 ~d1 ~e* ...) (run! walk e*)]
                [(clj-case ~d ~e0 ~e1 ~e* ...) (walk e0) (walk e1) (run! walk e*)]
                [else nil]))]
      (walk e)
      @acc)))

(define-pass spec-rename L3 [ir m] -> L3 []
  (Expr [ir] -> Expr []
    [~x (get m x x)]
    [(let ([~x* ~[e*]] ...) ~[body]) (out (let ([~(mapv #(get m % %) x*) ~e*] ...) ~body))]
    [(label ~x (~x* ...) ~[body0] ~[body1]) (out (label ~(get m x x) (~(mapv #(get m % %) x*) ...) ~body0 ~body1))]
    [(jump ~x ~[e*] ...) (out (jump ~(get m x x) ~e* ...))]))

(defn- spec-final-jump
  "body0 是 let / begin 套着的一个 (jump lbl 实参 …)：用 (f 实参) 替换那个 jump；别的形状 nil。"
  [e lbl f]
  (nanopass-case [L3 Expr] e
    [(let ([~x* ~e*] ...) ~body)
     (when-let [b (spec-final-jump body lbl f)] (with-output-language [L3 Expr] (out (let ([~x* ~e*] ...) ~b))))]
    [(begin ~e* ... ~e)
     (when-let [b (spec-final-jump e lbl f)] (with-output-language [L3 Expr] (out (begin ~e* ... ~b))))]
    [(jump ~x ~e* ...) (when (= x lbl) (f e*))]
    [else nil]))

(defn- spec-trivial? [e] (nanopass-case [L3 Expr] e [~x true] [(quote ~d) true] [else false]))
(defn- spec-const? [e] (nanopass-case [L3 Expr] e [(quote ~d) true] [else false]))

(defn- spec-numeric-use?
  "v 在 e 里是 Numbers 算术 / 比较的操作数。"
  [e v]
  (letfn [(walk [e]
            (nanopass-case [L3 Expr] e
              [(if ~e0 ~e1 ~e2) (or (walk e0) (walk e1) (walk e2))]
              [(begin ~e* ... ~e) (or (some walk e*) (walk e))]
              [(let ([~x* ~e*] ...) ~body) (or (some walk e*) (walk body))]
              [(call ~e ~e* ...) (or (walk e) (some walk e*))]
              [(throw ~e) (walk e)]
              [(jump ~x ~e* ...) (some walk e*)]
              [(check-initialized ~e) (walk e)]
              [(clj-invoke ~e ~e* ...) (or (walk e) (some walk e*))]
              [(clj-keyword-invoke ~d ~e ~e* ...) (or (walk e) (some walk e*))]
              [(clj-call ~d0 ~d1 ~e* ...)
               (or (and (numbers-arith-op d0 d1 e*) (some #(nanopass-case [L3 Expr] % [~x (= x v)] [else false]) (drop 3 e*)))
                   (some walk e*))]
              [(clj-static-call ~d0 ~d1 ~e* ...) (some walk e*)]
              [(clj-case ~d ~e0 ~e1 ~e* ...) (or (walk e0) (walk e1) (some walk e*))]
              [else false]))]
    (boolean (walk e))))

(defn- spec-copy
  "快份：(label x' (p' …) (jump x' 实参' …) body1')，推测的位置包 Speculated。"
  [x x* body1 args guess]
  (let [m (into {} (map (fn [v] [v (loops/fresh (str v))]) (cons x (concat x* (spec-binders body1)))))
        x' (m x)
        args' (map-indexed (fn [k a] (if-let [t (guess k)] (with-output-language [L3 Expr] (out (clj-call "Speculated" false (quote ~t) ~a))) a)) args)]
    [(with-output-language [L3 Expr]
       (out (label ~x' (~(mapv m x*) ...) (jump ~x' ~args' ...) ~(spec-rename body1 m))))
     (mapv m x*)]))

(defn- spec-guess
  "推测：位置 → :l64 / :fl。先都猜 long，试算推断定不成的改猜 double，再不成的放弃；空表示不推测。"
  [x x* body1 args cands]
  (loop [guess (into {} (map (fn [k] [k :l64]) cands)) n 0]
    (if (or (empty? guess) (> n 2))
      {}
      (let [[copy ps'] (spec-copy x x* body1 args guess)
            scratch (ht)
            _ (infer-var-types-into! copy scratch sound-types)
            bad (filter (fn [[k t]] (not= t (hget scratch (nth ps' k)))) guess)]
        (if (empty? bad)
          guess
          (recur (reduce (fn [g [k t]] (if (= t :l64) (assoc g k :fl) (dissoc g k))) guess bad) (inc n)))))))

(defn- spec-label
  "(label x (x* …) body0 body1) 的推测形式，不推测时 nil。"
  [x x* body0 body1]
  (when (spec-simple? body1)
    (spec-final-jump
      body0 x
      (fn [args]
        (let [cands (filter (fn [k] (let [a (nth args k)]
                                      (and (not (spec-const? a)) (not (#{:l64 :fl :fx} (expr-type a sound-types)))
                                           ;; 第一遍已定成原始类型的不推测；Num 变量照样推测（cleat-113：Num 的比较与混合运算仍走 Numbers.ops，
                                           ;; sumfp 每轮 3.7 ns，快份全程原始 double）
                                           (not (#{:l64 :fl} (hget var-types (nth x* k))))
                                           (spec-numeric-use? body1 (nth x* k)))))
                            (range (count args)))
              guess (if (seq cands) (spec-guess x x* body1 args cands) {})]
          (when (seq guess)
            (vreset! *spec-changed* true)
            (let [temps (mapv (fn [a] (if (spec-trivial? a) a (loops/fresh "spec"))) args)
                  binds (keep-indexed (fn [k a] (when-not (spec-trivial? a) [(temps k) a])) args)
                  [fast _] (spec-copy x x* body1 temps guess)
                  check (fn [k] (let [t (temps k)]
                                  (with-output-language [L3 Expr]
                                    (out (clj-call "IsInstanceOf" false (quote ~(if (= :l64 (guess k)) Int64 Double)) ~t)))))
                  ks (sort (keys guess))
                  test (reduce (fn [acc k] (with-output-language [L3 Expr] (out (if ~acc ~(check k) (quote false)))))
                               (check (first ks)) (rest ks))
                  sel (with-output-language [L3 Expr] (out (if ~test ~fast (jump ~x ~temps ...))))]
              (if (empty? binds)
                sel
                (with-output-language [L3 Expr] (out (let ([~(mapv first binds) ~(mapv second binds)] ...) ~sel)))))))))))

(declare l3-some?)

;; —— 数组推测（cleat-127，designs/23 §1）——
;; 循环体里被 aget / aset / alength 当数组用、在循环外绑定且不被赋值的变量 a，类型静态未知时：按用法取元素类型的证据，
;; 生成 (if (isinst T[] a) (let ([a' a]) 循环的副本（a 的用途换成 (Tagged T[] a')）) 原来的循环)。快份里的数组访问静态决议到
;; 类型化的 RT.aget / aset（ldelem / stelem）；数组在进入循环时就确定，循环里 a 不变，不需要 bailout。最多两种元素类型。

(defn- rt-call-name
  "(clj-call \"CallStatic\" 3 'clojure.lang.RT '名字 'nil 实参…)：[名字 实参]，否则 nil。"
  [e]
  (nanopass-case [L3 Expr] e
    [(clj-call ~d0 ~d1 ~e* ...)
     (when (and (= d0 "CallStatic") (= d1 3) (>= (count e*) 4))
       (let [t (quoted-datum (first e*)) nm (quoted-datum (second e*))]
         (when (and (or (= t 'clojure.lang.RT) (= t "clojure.lang.RT") (and (instance? Type t) (= (.FullName ^Type t) "clojure.lang.RT")))
                    (string? nm) (empty-gen? (quoted-datum (nth e* 2))))
           [nm (drop 3 e*)])))]
    [else nil]))

(defn- array-uses
  "body 里以裸变量为数组实参的 RT.aget / agetOnArray / aset / alength：变量 → {:numeric? :aset-types #{…} :generic?}。"
  [body]
  (let [acc (volatile! {})
        note! (fn [v k x] (vswap! acc update v (fnil (fn [m] (case k :aset (update m :aset-types conj x) (assoc m k true))) {:aset-types #{}})))
        arr-var (fn [e] (when-let [[nm args] (rt-call-name e)]
                          (when (#{"agetOnArray" "aget" "aset" "alength"} nm)
                            (nanopass-case [L3 Expr] (first args) [~x [nm x args]] [else nil]))))]
    (letfn [(walk [e numeric?]
              (when-let [[nm v args] (arr-var e)]
                (case nm
                  ("agetOnArray" "aget") (note! v (if numeric? :numeric? :generic?) nil)
                  "aset" (note! v :aset (expr-type (nth args 2) sound-types))
                  nil))
              (nanopass-case [L3 Expr] e
                [(if ~e0 ~e1 ~e2) (walk e0 false) (walk e1 false) (walk e2 false)]
                [(begin ~e* ... ~e) (run! #(walk % false) e*) (walk e false)]
                [(let ([~x* ~e*] ...) ~body) (run! #(walk % false) e*) (walk body false)]
                [(label ~x (~x* ...) ~body0 ~body1) (walk body0 false) (walk body1 false)]
                [(jump ~x ~e* ...) (run! #(walk % false) e*)]
                [(call ~e ~e* ...) (walk e false) (run! #(walk % false) e*)]
                [(clj-invoke ~e ~e* ...) (walk e false) (run! #(walk % false) e*)]
                [(clj-keyword-invoke ~d ~e ~e* ...) (walk e false) (run! #(walk % false) e*)]
                [(clj-call ~d0 ~d1 ~e* ...)
                 (let [num? (boolean (numbers-arith-op d0 d1 e*))] (run! #(walk % num?) e*))]
                [(clj-static-call ~d0 ~d1 ~e* ...) (run! #(walk % false) e*)]
                [(throw ~e) (walk e false)]
                [else nil]))]
      (walk body false)
      @acc)))

(def ^:private array-type-of {:l64 "System.Int64[]" :fl "System.Double[]" :obj "System.Object[]"})

(defn- array-guesses
  "一个数组变量的元素类型推测（Type 的列表，最多两个）：aset 的值类型 > aget 结果进入算术（long[]、double[]）> 通用用途（object[]）。"
  [{:keys [numeric? generic? aset-types]}]
  (let [ks (cond
             (seq aset-types) (distinct (map #(case % :l64 :l64 :fl :fl :obj) aset-types))
             numeric? [:l64 :fl]
             generic? [:obj]
             :else nil)]
    (when (<= 1 (count ks) 2)
      (mapv #(Type/GetType ^String (array-type-of %)) ks))))

(define-pass spec-tag-var L3 [ir v v' t] -> L3 []
  (Expr [ir] -> Expr []
    [~x (if (= x v) (with-output-language [L3 Expr] (out (clj-call "Tagged" false (quote ~t) ~v'))) x)]))

(defn- array-spec-label
  "(label x (x* …) body0 body1) 的数组推测形式；不推测时 nil。fallback 是原来的（可能已做数值推测的）循环。"
  [x x* body0 body1 fallback]
  (when (and opt-arrayspec? (spec-simple? body1)
             (l3-some? (fn [j] (nanopass-case [L3 Expr] j [(jump ~x2 ~e* ...) (= x2 x)] [else false])) body1 true))
    (let [uses (array-uses body1)
          body-binders (set (spec-binders body1))
          cands (keep (fn [[v u]]
                        ;; 守卫要放在循环入口，所以只有循环体外绑定的数组才可推测。
                        ;; 内联结果这类 let 临时在循环体内绑定；若不排除，副本会引用未进入绑定的原名。
                        (when (and (not (some #(= v %) x*)) (not (contains? body-binders v))
                                   (not (hget assigned-vars v)) (not (#{:l64 :fl :fx} (hget var-types v))))
                          (when-let [ts (array-guesses u)] [v ts])))
                      (sort-by (comp str key) uses))]
      ;; 第一版只推测一个变量（多个变量的组合会让副本成倍增长）
      (when-let [[v ts] (first cands)]
        (vreset! *spec-changed* true)
        (reduce (fn [else-e t]
                  (let [v' (loops/fresh (str v))
                        m (into {} (map (fn [y] [y (loops/fresh (str y))]) (cons x (concat x* (spec-binders body0) (spec-binders body1)))))
                        b0 (spec-tag-var (spec-rename body0 m) v v' t)
                        b1 (spec-tag-var (spec-rename body1 m) v v' t)]
                    (with-output-language [L3 Expr]
                      (out (if (clj-call "IsInstanceOf" false (quote ~t) ~v)
                             (let ([~[v'] ~[v]] ...) (label ~(m x) (~(mapv m x*) ...) ~b0 ~b1))
                             ~else-e)))))
                fallback (reverse ts))))))

;; —— 循环变量的标量替换（cleat-123，designs/22 §4 D10）——
;; 最内层循环的循环变量 z：recur 时（某些分支）构造 deftype T 的新对象 (NewOf T a b …)、循环体里读它的字段。T 的字段都是 long / double、
;; 构造器的实参与字段一一对应时，z 拆成每个字段一个原始循环变量与对象槽 zobj：
;;   进入时 (isinst T z0) 不成立 → 原来的循环；成立 → 字段从 z0 读出，zobj = z0（原来的对象）。
;;   字段读：(if zobj (zobj 的字段) 标量)——zobj 不空时以对象为准（被交出去以后可能被改）。
;;   别的用途（交给未知调用、守卫失败的分支……）：zobj 为空才用当前字段构造一次，用 zobj——一轮内是同一个对象，同一性与原程序相同，
;;   只是分配推迟到真正需要时（deftype 的构造器没有副作用；字段按构造器同样的转换语义存）。
;;   recur：新值在尾位置是 (NewOf T a b) 时字段取 a / b（按构造器的转换）、zobj 置空；是 z 本身时不变；是别的（未知对象）时
;;   带着本轮全部实参跳回原来的循环（在 recur 处切换是精确的：本轮的副作用都已发生）。

(def ^:private scalar-max-nodes 600)

(defn- scalar-type-fields
  "deftype T 的字段（[名字 :l64|:fl] 的向量，声明顺序），全是 long / double 且个数为 n 时；否则 nil。"
  [t n]
  (when (and (instance? Type t) (not (.IsValueType ^Type t)) (.IsSealed ^Type t))
    (let [fs (remove #(.IsStatic ^System.Reflection.FieldInfo %) (.GetFields ^Type t))
          ks (mapv (fn [^System.Reflection.FieldInfo f] [(.Name f) (condp = (.FieldType f) Int64 :l64 Double :fl nil)]) fs)]
      (when (and (= n (count ks)) (pos? n) (every? second ks)) ks))))

(defn- new-of-parts [e]
  (nanopass-case [L3 Expr] e
    [(clj-call ~d0 ~d1 ~e* ...) (when (and (= d0 "NewOf") (= d1 1) (seq e*)) [(quoted-datum (first e*)) (vec (rest e*))])]
    [else nil]))

(defn- untag [e]
  (nanopass-case [L3 Expr] e
    [(clj-call ~d0 ~d1 ~e* ...) (if (and (= d0 "Tagged") (= 2 (count e*))) (untag (second e*)) e)]
    [else e]))

(defn- field-read-of
  "(GetInstanceField 'T 'f z)（实例可带 Tagged）：[T f 实例]，否则 nil。"
  [e]
  (nanopass-case [L3 Expr] e
    [(clj-call ~d0 ~d1 ~e* ...)
     (when (and (= d0 "GetInstanceField") (false? d1) (= 3 (count e*)))
       [(quoted-datum (first e*)) (quoted-datum (second e*)) (untag (nth e* 2))])]
    [else nil]))

(define-pass collapse-entry-labels L3 [ir] -> L3 []
  ;; (label L (p …) (jump L a …) body1)，body1 不跳回 L、实参都是变量：换成 body1[p := a]（内联的子句的 arity 标签）
  (Expr [ir] -> Expr []
    [(label ~x (~x* ...) ~[body0] ~[body1])
     (let [args (nanopass-case [L3 Expr] body0 [(jump ~x2 ~e* ...) (when (= x2 x) e*)] [else nil])]
       (if (and args (= (count args) (count x*))
                (every? #(and (symbol? %) (not (hget assigned-vars %))) args)
                (not-any? #(hget assigned-vars %) x*)
                (not (l3-some? (fn [j] (nanopass-case [L3 Expr] j [(jump ~x3 ~e* ...) (= x3 x)] [else false])) body1 true))
                ;; 体里有 lambda 时不折叠：闭包转换后 lambda 带显式的捕获变量表，代换不改它
                (not (l3-some? (fn [j] (nanopass-case [L3 Expr] j
                                         [(lambda ~nm (~x4* ...) ~cl* ...) true]
                                         [(clj-deftype ~d (~x5* ...) ~le* ...) true]   ; reify / deftype 的方法（l3-some? 不进它们）
                                         [else false]))
                               body1)))
         (spec-rename body1 (zipmap x* args))
         (out (label ~x (~x* ...) ~body0 ~body1))))]))

(defn- scalar-nodes-ok?
  "体足够小、没有 lambda / letrec / try（与 spec-simple? 相同，但允许不回跳的 label）"
  [e]
  (let [n (volatile! 0)]
    (letfn [(ok? [e]
              (and (<= (vswap! n inc) scalar-max-nodes)
                   (nanopass-case [L3 Expr] e
                     [~x true]
                     [(quote ~d) true]
                     [(if ~e0 ~e1 ~e2) (and (ok? e0) (ok? e1) (ok? e2))]
                     [(begin ~e* ... ~e) (and (every? ok? e*) (ok? e))]
                     [(let ([~x* ~e*] ...) ~body) (and (every? ok? e*) (ok? body))]
                     [(label ~x (~x* ...) ~body0 ~body1) (and (ok? body0) (ok? body1))]
                     [(call ~e ~e* ...) (and (ok? e) (every? ok? e*))]
                     [(throw ~e) (ok? e)]
                     [(jump ~x ~e* ...) (every? ok? e*)]
                     [(check-initialized ~e) (ok? e)]
                     [(clj-var-ref ~d ~st) true]
                     [(clj-invoke ~e ~e* ...) (and (ok? e) (every? ok? e*))]
                     [(clj-keyword-invoke ~d ~e ~e* ...) (and (ok? e) (every? ok? e*))]
                     [(clj-call ~d0 ~d1 ~e* ...) (every? ok? e*)]
                     [(clj-static-call ~d0 ~d1 ~e* ...) (every? ok? e*)]
                     [(clj-case ~d ~e0 ~e1 ~e* ...) (and (ok? e0) (ok? e1) (every? ok? e*))]
                     [else false])))]
      (ok? e))))

(defn- tails
  "e 的尾位置表达式（穿过 let / begin / if / 不回跳的 label 的体）"
  [e]
  (nanopass-case [L3 Expr] e
    [(let ([~x* ~e*] ...) ~body) (tails body)]
    [(begin ~e* ... ~e) (tails e)]
    [(if ~e0 ~e1 ~e2) (concat (tails e1) (tails e2))]
    [(label ~x (~x* ...) ~body0 ~body1) (tails body1)]
    [else [e]]))

(defn- loop-jumps
  "body 里跳到 x 的 jump 的实参表（不进 lambda）"
  [body x]
  (let [acc (volatile! [])]
    (l3-some? (fn [j] (nanopass-case [L3 Expr] j [(jump ~x2 ~e* ...) (do (when (= x2 x) (vswap! acc conj e*)) false)] [else false])) body true)
    @acc))

(defn- prim-arg-conv
  "构造器存原始字段时的转换（ClojureStaticCalls.PrimArgLong / PrimArgDouble），实参已是同类型的原始值时省掉"
  [e k]
  (if (= k (expr-type e sound-types))
    e
    (with-output-language [L3 Expr]
      (out (clj-call "CallStatic" 3 (quote ~Cleat.Core.Runtime.ClojureStaticCalls) (quote ~(if (= k :l64) "PrimArgLong" "PrimArgDouble")) (quote nil) ~e)))))

;; rw-sub：对 e 的各个子表达式应用 rw 后重建（scalar-nodes-ok? 允许的形式；绑定名经 rw 对变量的处理改名）
(defn- rw-sub [rw e]
  (with-output-language [L3 Expr]
    (nanopass-case [L3 Expr] e
      [~x (rw x)]
      [(quote ~d) e]
      [(if ~e0 ~e1 ~e2) (out (if ~(rw e0) ~(rw e1) ~(rw e2)))]
      [(begin ~e* ... ~e) (out (begin ~@(map rw e*) ~(rw e)))]
      [(let ([~x* ~e*] ...) ~body) (out (let ([~(mapv rw x*) ~(mapv rw e*)] ...) ~(rw body)))]
      [(label ~x (~x* ...) ~body0 ~body1) (out (label ~(rw x) (~(mapv rw x*) ...) ~(rw body0) ~(rw body1)))]
      [(jump ~x ~e* ...) (out (jump ~(rw x) ~@(map rw e*)))]
      [(call ~e ~e* ...) (out (call ~(rw e) ~@(map rw e*)))]
      [(throw ~e) (out (throw ~(rw e)))]
      [(check-initialized ~e) (out (check-initialized ~(rw e)))]
      [(clj-var-ref ~d ~st) e]
      [(clj-invoke ~e ~e* ...) (out (clj-invoke ~(rw e) ~@(map rw e*)))]
      [(clj-keyword-invoke ~d ~e ~e* ...) (out (clj-keyword-invoke ~d ~(rw e) ~@(map rw e*)))]
      [(clj-call ~d0 ~d1 ~e* ...) (out (clj-call ~d0 ~d1 ~@(map rw e*)))]
      [(clj-static-call ~d0 ~d1 ~e* ...) (out (clj-static-call ~d0 ~d1 ~@(map rw e*)))]
      [(clj-case ~d ~e0 ~e1 ~e* ...) (out (clj-case ~d ~(rw e0) ~(rw e1) ~@(map rw e*)))]
      [else e])))

;; let 绑定的构造（cleat-123 第二步）：(let ([t (NewOf T a …)]) body)，T 可拆、t 不被赋值 → 字段局部变量 + 对象槽（同循环变量：
;; 字段读按 (if tobj (tobj 的字段) 标量)，别的用途在 tobj 为空时才构造一次）。初值的尾位置混着构造与别的值（内联的守卫）且 body 小时，
;; 先把 let 推进初值的各个尾位置（body 复制、绑定换新名），构造的那一支再拆。

(def ^:private split-max-nodes 150)

(defn- node-count [e]
  (let [n (volatile! 0)] (l3-some? (fn [_] (vswap! n inc) false) e) @n))

(defn- push-let-into-tails
  "(let ([t E]) body) → E 的每个尾位置 X 换成 (let ([t' X]) body')（body' 是换了新名的副本）"
  [t e body]
  (letfn [(copy-body [x]
            (let [m (into {} (map (fn [y] [y (loops/fresh (str y))]) (cons t (spec-binders body))))]
              (with-output-language [L3 Expr] (out (let ([~[(m t)] ~[x]] ...) ~(spec-rename body m))))))
          (push [x]
            (nanopass-case [L3 Expr] x
              [(let ([~x* ~e*] ...) ~b) (with-output-language [L3 Expr] (out (let ([~x* ~e*] ...) ~(push b))))]
              [(begin ~e* ... ~e) (with-output-language [L3 Expr] (out (begin ~@e* ~(push e))))]
              [(if ~e0 ~e1 ~e2) (with-output-language [L3 Expr] (out (if ~e0 ~(push e1) ~(push e2))))]
              [else (copy-body x)]))]
    (push e)))

(defn- scalarize-let-new
  "(let ([t (NewOf T a …)]) body) 的拆分形式；不拆时 nil"
  [t e body]
  (when-let [[ty args] (new-of-parts e)]
    (when-let [fs (and (not (hget assigned-vars t)) (scalar-type-fields ty (count args)))]
      (when (l3-some? (fn [x] (when-let [[t' _ inst] (field-read-of x)] (and (identical? t' ty) (= inst t)))) body)
        (let [tf (mapv (fn [[nm _]] (loops/fresh (str t "." nm))) fs)
              tobj (loops/fresh (str t ".obj"))
              raw? (every? (fn [[a [_ k]]] (= k (expr-type a sound-types))) (map vector args fs))
              ats (if raw? args (mapv (fn [_] (loops/fresh "sr")) args))
              tagged (fn [v] (with-output-language [L3 Expr] (out (clj-call "Tagged" false (quote ~ty) ~v))))
              materialize (with-output-language [L3 Expr]
                            (out (if ~tobj ~tobj (begin (set! ~tobj (clj-call "NewOf" 1 (quote ~ty) ~@tf)) ~tobj))))
              rw (fn rw [x]
                   (if-let [[t' nm inst] (field-read-of x)]
                     (if (and (identical? t' ty) (= inst t))
                       (let [j (first (keep-indexed (fn [i [n _]] (when (= n nm) i)) fs))]
                         (with-output-language [L3 Expr]
                           (out (if ~tobj (clj-call "GetInstanceField" false (quote ~ty) (quote ~nm) ~(tagged tobj)) ~(tf j)))))
                       (rw-sub rw x))
                     (nanopass-case [L3 Expr] x
                       [~x (if (= x t) materialize x)]
                       [else (rw-sub rw x)])))
              inner (with-output-language [L3 Expr]
                      (out (let ([~tf ~(if raw? (vec ats) (mapv (fn [a [_ k]] (prim-arg-conv a k)) ats fs))] ...)
                             (let ([~[tobj] ~[(with-output-language [L3 Expr] (out (quote nil)))]] ...) ~(rw body)))))]
          (if raw?
            inner
            (with-output-language [L3 Expr] (out (let ([~ats ~(vec args)] ...) ~inner)))))))))

(defn- scalarize-lets
  "e 里（不进 lambda）可拆的 let 绑定的构造；多绑定的 let 先拆成嵌套的单绑定（名字唯一，求值顺序不变）"
  [e]
  (letfn [(walk [x]
            (nanopass-case [L3 Expr] x
              [(let ([~x* ~e*] ...) ~body)
               (if (> (count x*) 1)
                 (walk (reduce (fn [b [y ey]] (with-output-language [L3 Expr] (out (let ([~[y] ~[ey]] ...) ~b))))
                               body (reverse (map vector x* e*))))
                 (let [t (first x*) e0 (walk (first e*))]
                   (or (scalarize-let-new t e0 body)
                       (let [ts (tails e0)
                             news (keep new-of-parts ts)]
                         (when (and (seq news) (< (count news) (count ts)) (not (hget assigned-vars t))
                                    (some (fn [[ty args]] (scalar-type-fields ty (count args))) news)
                                    (<= (node-count body) split-max-nodes)
                                    (l3-some? (fn [y] (when-let [[_ _ inst] (field-read-of y)] (= inst t))) body))
                           (walk (push-let-into-tails t e0 body))))
                       (with-output-language [L3 Expr] (out (let ([~[t] ~[e0]] ...) ~(walk body)))))))]
              [(lambda ~nm (~x* ...) ~cl* ...) x]
              [~x x]
              [else (rw-sub walk x)]))]
    (walk e)))

(defn- scalar-label
  "(label x (x* …) body0 body1) 的标量替换形式；不做时 nil。"
  [x x* body0 body1]
  (when opt-scalar?
    (let [body1 (collapse-entry-labels body1)]
      (when (and (scalar-nodes-ok? body1)
                 (l3-some? (fn [j] (nanopass-case [L3 Expr] j [(jump ~x2 ~e* ...) (= x2 x)] [else false])) body1 true))
        (let [jumps (loop-jumps body1 x)
              cand (some (fn [k]
                           (let [z (nth x* k)
                                 ts (mapcat #(tails (nth % k)) jumps)
                                 news (keep new-of-parts ts)
                                 t (first (first news))
                                 fs (when t (scalar-type-fields t (count (second (first news)))))]
                             (when (and fs (not (hget assigned-vars z))
                                        (every? (fn [[t' args]] (and (identical? t t') (= (count args) (count fs)))) news)
                                        (l3-some? (fn [e] (when-let [[t' _ inst] (field-read-of e)] (and (identical? t' t) (= inst z)))) body1))
                               [k z t fs])))
                         (range (count x*)))]
          (when-let [[k z t fs] cand]
            (vreset! *spec-changed* true)
            (let [fx (loops/fresh "fast")
                  zf (mapv (fn [[nm _]] (loops/fresh (str z "." nm))) fs)
                  zobj (loops/fresh (str z ".obj"))
                  others (vec (keep-indexed (fn [i y] (when (not= i k) y)) x*))
                  m (into {} (map (fn [y] [y (loops/fresh (str y))]) (concat others (spec-binders body1))))
                  fps (vec (concat (map m others) zf [zobj]))   ; 快份循环的形参：其余循环变量、各字段、对象槽
                  tagged (fn [v] (with-output-language [L3 Expr] (out (clj-call "Tagged" false (quote ~t) ~v))))
                  read-field (fn [nm v] (with-output-language [L3 Expr] (out (clj-call "GetInstanceField" false (quote ~t) (quote ~nm) ~(tagged v)))))
                  materialize (with-output-language [L3 Expr]
                                (out (if ~zobj ~zobj (begin (set! ~zobj (clj-call "NewOf" 1 (quote ~t) ~@zf)) ~zobj))))
                  fast-jump (fn [other-vals field-vals obj-val]
                              (with-output-language [L3 Expr] (out (jump ~fx ~@(concat other-vals field-vals [obj-val])))))
                  rw (fn rw [e]
                       (if-let [[t' nm inst] (field-read-of e)]
                         (if (and (identical? t' t) (= inst z))
                           (let [j (first (keep-indexed (fn [i [n _]] (when (= n nm) i)) fs))]
                             (with-output-language [L3 Expr] (out (if ~zobj ~(read-field nm zobj) ~(zf j)))))
                           (rw-sub rw e))
                         (nanopass-case [L3 Expr] e
                           [~x (if (= x z) materialize (get m x x))]
                           [(jump ~x2 ~e* ...)
                            (if (= x2 x)
                              ;; recur：z 之前的实参先求值进临时变量，z 的实参按尾位置分派，之后的实参在每个尾里求值（求值顺序不变）
                              (let [pre (subvec (vec e*) 0 k) post (subvec (vec e*) (inc k))
                                    tmps (mapv (fn [_] (loops/fresh "sr")) pre)
                                    others-vals (fn [post'] (vec (concat tmps post')))
                                    slow (fn [zv] (let [zt (loops/fresh "sr")]
                                                    (with-output-language [L3 Expr]
                                                      (out (let ([~[zt] ~[zv]] ...)
                                                             (jump ~x ~@(concat tmps [zt] (map rw post))))))))
                                    push (fn push [ze]
                                           (or (when-let [[t' args] (new-of-parts ze)]
                                                 (when (and (identical? t' t) (every? (fn [[a [_ kk]]] (= kk (expr-type a sound-types))) (map vector args fs)))
                                                   ;; 实参已是字段的原始类型：构造器的转换是恒等，不经临时变量
                                                   (fast-jump (others-vals (map rw post)) (mapv rw args) (with-output-language [L3 Expr] (out (quote nil))))))
                                               (when-let [[t' args] (new-of-parts ze)]
                                                 (when (identical? t' t)
                                                   (let [ats (mapv (fn [_] (loops/fresh "sr")) args)]
                                                     (with-output-language [L3 Expr]
                                                       (out (let ([~ats ~(mapv rw args)] ...)
                                                              ~(fast-jump (others-vals (map rw post))
                                                                          (map (fn [a [_ kk]] (prim-arg-conv a kk)) ats fs)
                                                                          (with-output-language [L3 Expr] (out (quote nil))))))))))
                                               (when (= (untag ze) z)
                                                 (fast-jump (others-vals (map rw post)) zf zobj))
                                               (nanopass-case [L3 Expr] ze
                                                 [(let ([~x* ~e*] ...) ~body)
                                                  (with-output-language [L3 Expr] (out (let ([~(mapv #(get m % %) x*) ~(mapv rw e*)] ...) ~(push body))))]
                                                 [(begin ~e* ... ~e) (with-output-language [L3 Expr] (out (begin ~@(map rw e*) ~(push e))))]
                                                 [(if ~e0 ~e1 ~e2) (with-output-language [L3 Expr] (out (if ~(rw e0) ~(push e1) ~(push e2))))]
                                                 [else (slow (rw ze))])))
                                    body (push (nth e* k))]
                                (if (empty? pre)
                                  body
                                  (with-output-language [L3 Expr] (out (let ([~tmps ~(mapv rw pre)] ...) ~body)))))
                              (rw-sub rw e))]
                           [else (rw-sub rw e)])))
                  fast-body (scalarize-lets (rw body1))]
              (spec-final-jump
                body0 x
                (fn [args]
                  (let [tmps (mapv (fn [_] (loops/fresh "sr")) args)
                        zt (tmps k)]
                    (with-output-language [L3 Expr]
                      (out (let ([~tmps ~(vec args)] ...)
                             (if (clj-call "IsInstanceOf" false (quote ~t) ~zt)
                               (label ~fx (~fps ...)
                                 (jump ~fx ~@(concat (keep-indexed (fn [i a] (when (not= i k) a)) tmps)
                                                     (map (fn [[nm _]] (read-field nm zt)) fs)
                                                     [zt]))
                                 ~fast-body)
                               (jump ~x ~@tmps)))))))))))))))

(define-pass speculate-loops L3 [ir] -> L3 []
  (Expr [ir] -> Expr []
    [(label ~x (~x* ...) ~[body0] ~[body1])
     (if-let [sb0 (scalar-label x x* body0 body1)]
       (out (label ~x (~x* ...) ~sb0 ~body1))   ; 标量替换（cleat-123）：快份在 body0 里，原来的循环兼作退回
       (let [b0 (spec-label x x* body0 body1)
             plain (out (label ~x (~x* ...) ~(or b0 body0) ~body1))]
         (or (array-spec-label x x* body0 body1 plain) plain)))]))

;; —— 无提示函数的类型化入口（cleat-104，skeg iron-075 / 076）——
;; 顶层 (def f (fn [形参…] 体))：单固定子句、≤ 4 形参、没有闭包字段、没有原始签名提示，体是纯的（只有 Numbers / Util.equiv 的运算、
;; if / let / begin / 循环与经 Var 的自调用，且至少有一次自调用），不超过 spec-entry-max-nodes 个节点。形参（在 Numbers 运算里用到的）与返回值推测
;; long / double：类型化的一份体（绑定换新名，自调用换成 (clj-call "TypedSelf" …)）在这些固定类型下推断出的返回类型就是猜的类型、
;; 自调用的实参也是，就生成 invokeStaticTyped(self, T…) → R；通用入口 invokeStatic 包成 (clj-call "TypedEntry" …)：实参类型相符时
;; 转调它，否则（或类型化体里自调用发现 Var 被重定义、新函数的结果不是 R，抛 TypedBailout）走通用体。long 溢出照常抛异常，不重算。

(def ^:private spec-entry-max-nodes 200)
(def ^:private spec-entries (ht))   ; fn 的类名（名字记录的 :class）→ {:sig 签名 :clause 类型化子句}；每个编译单元清空
(def ^:private spec-profiles (ht))   ; 推测失败 / 缺席的候选：类名 → 固定形参个数（cleat-128）；每个编译单元清空
(def ^:private spec-signature-entries (ht))   ; cleat-104 推测失败候选的 LL / DD 特化：类名 → 特化表；每个编译单元清空

(defn- spec-self-ref? [e v]
  (nanopass-case [L3 Expr] e [(clj-var-ref ~d ~st) (identical? d v)] [else false]))

(defn- spec-pure-call? [d0 d1 e*]
  (or (numbers-arith-op d0 d1 e*)
      (and (= d0 "CallStatic") (= d1 3) (>= (count e*) 3)
           (let [t (quoted-datum (first e*)) nm (quoted-datum (second e*))]
             (or (numbers-type? t)
                 (and (instance? Type t) (= (.FullName ^Type t) "clojure.lang.Util") (#{"equiv" "equals"} nm)))))))

(defn- spec-pure?
  "类型化入口的体：纯运算与自调用。"
  [e v]
  (let [n (volatile! 0)]
    (letfn [(ok? [e]
              (and (<= (vswap! n inc) spec-entry-max-nodes)
                   (nanopass-case [L3 Expr] e
                     [~x true]
                     [(quote ~d) true]
                     [(if ~e0 ~e1 ~e2) (and (ok? e0) (ok? e1) (ok? e2))]
                     [(begin ~e* ... ~e) (and (every? ok? e*) (ok? e))]
                     [(let ([~x* ~e*] ...) ~body) (and (every? ok? e*) (ok? body))]
                     [(label ~x (~x* ...) ~body0 ~body1) (and (ok? body0) (ok? body1))]
                     [(jump ~x ~e* ...) (every? ok? e*)]
                     [(clj-invoke ~e ~e* ...) (and (spec-self-ref? e v) (every? ok? e*))]
                     [(clj-call ~d0 ~d1 ~e* ...) (and (spec-pure-call? d0 d1 e*) (every? ok? e*))]
                     [else false])))]
      (ok? e))))

(define-pass spec-typed-copy L3 [ir m v info] -> L3 []
  (Expr [ir] -> Expr []
    [~x (get m x x)]
    [(let ([~x* ~[e*]] ...) ~[body]) (out (let ([~(mapv #(get m % %) x*) ~e*] ...) ~body))]
    [(label ~x (~x* ...) ~[body0] ~[body1]) (out (label ~(get m x x) (~(mapv #(get m % %) x*) ...) ~body0 ~body1))]
    [(jump ~x ~[e*] ...) (out (jump ~(get m x x) ~e* ...))]
    [(clj-invoke ~e ~[e*] ...)
     (if (spec-self-ref? e v)
       (out (clj-call "TypedSelf" false (quote ~info) ~e ~e* ...))
       (out (clj-invoke ~(Expr e) ~e* ...)))]))

(defn- spec-typed-selves
  "类型化体里的 TypedSelf：[实参 …] 的序列。"
  [e]
  (let [acc (volatile! [])]
    (letfn [(walk [e]
              (nanopass-case [L3 Expr] e
                [(if ~e0 ~e1 ~e2) (run! walk [e0 e1 e2])]
                [(begin ~e* ... ~e) (run! walk e*) (walk e)]
                [(let ([~x* ~e*] ...) ~body) (run! walk e*) (walk body)]
                [(label ~x (~x* ...) ~body0 ~body1) (walk body0) (walk body1)]
                [(jump ~x ~e* ...) (run! walk e*)]
                [(clj-call ~d0 ~d1 ~e* ...) (when (= d0 "TypedSelf") (vswap! acc conj (drop 2 e*))) (run! walk e*)]
                [else nil]))]
      (walk e)
      @acc)))

(defn- spec-body-type
  "返回类型：expr-type 加上 label（体与循环体的类型之并，jump 是 :top）；只用于类型化入口的判定。"
  [e env]
  (nanopass-case [L3 Expr] e
    [(label ~x (~x* ...) ~body0 ~body1) (type-join (spec-body-type body0 env) (spec-body-type body1 env))]
    [(if ~e0 ~e1 ~e2) (type-join (spec-body-type e1 env) (spec-body-type e2 env))]
    [(begin ~e* ... ~e) (spec-body-type e env)]
    [(let ([~x* ~e*] ...) ~body) (spec-body-type body env)]
    [else (expr-type e env)]))

(defn- spec-entry-try
  "猜测 guess（形参 → :l64 / :fl / nil）与返回 r：成立时 {:sig :params :body}，否则 nil。"
  [x* body v guess r]
  (let [sig (str (apply str (map #(case % :l64 "L" :fl "D" "O") guess)) (case r :l64 "L" :fl "D"))
        m (into {} (map (fn [b] [b (loops/fresh (str b))]) (concat x* (spec-binders body))))
        ps (mapv m x*)
        tb (spec-typed-copy body m v [sig])
        scratch (ht)]
    (doseq [[p t] (map vector ps guess)] (when t (hput! fixed-types p t)))
    (infer-var-types-into! tb scratch sound-types)
    (let [env (ht)]
      (doseq [k (keys-of scratch)] (hput! env k (hget scratch k)))
      (doseq [[p t] (map vector ps guess)] (when t (hput! env p t)))
      (when (and (= r (spec-body-type tb env))
                 (seq (spec-typed-selves tb))   ; 只有递归函数：好处来自类型化体里的自调用，非递归的通用入口只多一次检查与 try
                 (every? (fn [args] (every? (fn [[a t]] (or (nil? t) (= t (expr-type a env)))) (map vector args guess)))
                         (spec-typed-selves tb)))
        {:sig sig :params ps :body tb :guess guess}))))

(defn- spec-entry
  "clj-def 的 fn 能否有类型化入口：成立时 {:sig :params :body :guess}。"
  [v nm fv* cl*]
  (when (and (l/clj-fn-name? nm) (empty? fv*) (= 1 (count cl*)) (false? (:variadic nm)) (not (:async? nm))
             (empty? (:prims nm)))
    (nanopass-case [L3 Clause] (first cl*)
      [(clause (~x* ...) ~x ~body)
       (when (and (not x) (<= 1 (count x*) 4) (spec-pure? body v))
         (let [;; fn 的 arity 循环把形参复制进标签变量：(label recur (p' …) (jump recur p …) 体)——看标签变量的用途
               arity (nanopass-case [L3 Expr] body
                       [(label ~x1 (~x1* ...) ~body0 ~body1)
                        (nanopass-case [L3 Expr] body0
                          [(jump ~x2 ~e* ...) (when (and (= x2 x1) (= (count e*) (count x1*))) [e* x1* body1])]
                          [else nil])]
                       [else nil])
               num (mapv (fn [p]
                           (or (spec-numeric-use? body p)
                               (when-let [[a* l* b1] arity]
                                 (some (fn [[a q]] (and (nanopass-case [L3 Expr] a [~x (= x p)] [else false]) (spec-numeric-use? b1 q)))
                                       (map vector a* l*)))))
                         x*)]
           (when (some true? num)
             (some (fn [[t r]] (spec-entry-try x* body v (mapv #(when % t) num) r))
                   [[:l64 :l64] [:l64 :fl] [:fl :fl]]))))])))

(defn- spec-feedback-candidate
  "cleat-104 的通用体条件成立但类型化入口不成立（推测失败，或没有数值证据）时返回形参个数（cleat-128）。"
  [v nm fv* cl*]
  (when (and (instance? clojure.lang.Var v) (l/clj-fn-name? nm) (empty? fv*) (= 1 (count cl*)) (false? (:variadic nm))
             (not (:async? nm)) (empty? (:prims nm)))
    (nanopass-case [L3 Clause] (first cl*)
      [(clause (~x* ...) ~x ~body)
       (when (and (not x) (<= 1 (count x*) 4) (spec-pure? body v)) (count x*))])))

(defn- sigspec-param-vectors
  "给定形参个数 n，返回候选类型向量列表（:l64 / :fl 组合）。1 参 L / D，2 参 LL / LD / DL / DD，3 参 LLL / LLD / DDL / DDD。"
  [n]
  (case n
    1 [[:l64] [:fl]]
    2 [[:l64 :l64] [:l64 :fl] [:fl :l64] [:fl :fl]]
    3 [[:l64 :l64 :l64] [:l64 :l64 :fl] [:fl :fl :l64] [:fl :fl :fl]]
    []))

(defn- spec-signature-entry-tries
  "cleat-132：无编译期类型证据的 1～3 参纯递归候选，预生成运行时常见的 L / D 混合特化。
  返回类型仍由类型化体在猜测形参下证明；证明不了时不投机。"
  [v nm fv* cl*]
  (when (and opt-sigspec? (not (hhas? spec-entries (:class nm)))
             (spec-feedback-candidate v nm fv* cl*))
    (nanopass-case [L3 Clause] (first cl*)
      [(clause (~x* ...) ~x ~body)
       (when (and (not x) (<= 1 (count x*) 3))
         (->> (sigspec-param-vectors (count x*))
              (keep (fn [guess] (some #(spec-entry-try x* body v guess %) [:l64 :fl])))
              (into [])))])))

(define-pass speculate-entries L3 [ir] -> L3 []
  (Expr [ir] -> Expr []
    [(clj-def ~d ~st0 ~st1 ~[e0] ~[e1])
     (let [e0' (nanopass-case [L3 Expr] e0
                 [(lambda ~nm (~x* ...) ~cl* ...)
                 (let [pf (and opt-typefeedback? @live-unit? (spec-feedback-candidate d nm x* cl*))
                       ;; 类型化入口不能依赖剖面开关 / eval 模式（cleat-104 的原有语义）
                       se (and opt-typed? (instance? clojure.lang.Var d) (not (hhas? spec-entries (:class nm)))
                               (spec-entry d nm x* cl*))
                       ;; 编译期推不出类型但形状合适时，把 LL / DD 交给运行时签名缓存（cleat-132）
                       ss (and (not se) (instance? clojure.lang.Var d)
                               (spec-signature-entry-tries d nm x* cl*))]
                   (cond
                     se (do (vreset! *spec-changed* true)
                            (hput! spec-entries (:class nm) se)
                            (let [cl (nanopass-case [L3 Clause] (first cl*)
                                       [(clause (~y* ...) ~y ~body)
                                        (with-output-language [L3 Clause]
                                          (out (clause (~y* ...) ~y
                                                 ~(with-output-language [L3 Expr]
                                                    (out (clj-call "TypedEntry" false (quote ~[(:sig se) y*]) ~body))))))])]
                              (with-output-language [L3 Expr] (out (lambda ~nm (~x* ...) ~cl)))))
                     ss (do (vreset! *spec-changed* true)
                            (hput! spec-signature-entries (:class nm) ss)
                            (let [cl (nanopass-case [L3 Clause] (first cl*)
                                       [(clause (~y* ...) ~y ~body)
                                        (with-output-language [L3 Clause]
                                          (out (clause (~y* ...) ~y
                                                 ~(with-output-language [L3 Expr]
                                                    (out (clj-call "SigEntry" false
                                                                   (quote ~(mapv :sig ss))
                                                                   ~body))))))])]
                              (with-output-language [L3 Expr] (out (lambda ~nm (~x* ...) ~cl)))))
                     ;; 推测失败 / 缺席：类名和形参个数交给函数类生成静态剖面字段（cleat-128）
                     pf (do (hput! spec-profiles (:class nm) pf) e0)
                     :else e0))]
                 [else nil])]
       (out (clj-def ~d ~st0 ~st1 ~(or e0' e0) ~e1)))]))

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

(declare cg cg* load-var! cg-raw l3-some? binding-code bind-value! bind-local! num-alias-loc cg-profile-args!)

(defn- type-clr [t] (case t :fl double-type :l64 int64-type :num num-type int32-type))

(defn- box-raw! [mc t]
  (case t
    :fl (emit! mc (box-double))
    :l64 (emit! mc (box-long))
    :num (emit! mc (i 'call num-type "Box" (i num-type) obj))
    (emit! mc (i 'box int32-type))))

(defn- unbox-to-sig! [mc c]
  (case c
    \L (emit! mc (i 'call static-calls-type "PrimArgLong" (i obj) int64-type))
    \D (emit! mc (i 'call static-calls-type "PrimArgDouble" (i obj) double-type))
    nil))

(defn- box-from-sig! [mc c]
  (case c
    \L (emit! mc (box-long))
    \D (emit! mc (box-double))
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
         (when (and (vector? sl) (= t (host-ret-raw (str (aget ^objects (first sl) 3)))))
           sl))]
      [else nil])))

(declare cg-prim-invoke! cg-static-host-call! ref-var! cg-typed-self! cg-typed-entry!
         cg-signature-entry! cg-known-call! cg-escape-propagate!)

(defn- typed-self-ret
  "(clj-call \"TypedSelf\" …) 的返回类型（:l64 / :fl），别的节点 nil。"
  [e]
  (nanopass-case [L3 Expr] e
    [(clj-call ~d0 ~d1 ~e* ...) (when (= d0 "TypedSelf") (sig-char-raw (sig-ret (first (quoted-datum (first e*))))))]
    [else nil]))

(defn- cg-raw-default [e mc t]
  (let [fallback #(do (cg e mc :value) (emit! mc (i 'unbox.any (type-clr t))))]
    (nanopass-case [L3 Expr] e
      [(quote ~d)
       (cond (and (= t :fx) (instance? Int32 d)) (emit! mc (i 'ldc.i4 d))
             (and (= t :fl) (instance? Double d) (not (Double/IsNaN d)) (not (Double/IsInfinity d))) (emit! mc (i 'ldc.r8 d))
             (and (= t :l64) (instance? Int64 d)) (emit! mc (i 'ldc.i8 d))
             :else (fallback))]
      [~x (let [loc (hget (:vars mc) x)]
            (cond
              (and loc (#{:tlocal :targ} (first loc)) (= (nth loc 2) t))
              (do (ref-var! mc x false) (emit! mc (if (= :targ (first loc)) (i 'ldarg (second loc)) (i 'ldloc (second loc)))))
              (and loc (= :dfield (first loc)) (= (nth loc 2 nil) t))   ; 原始字段直接读（cleat-112）
              (emit! mc (i 'ldarg 0) (i 'ldfld (:class mc) (second loc)))
              :else (fallback)))]
      [else (fallback)])))

(declare var-raw-type cg-raw-join! branch-ref-counts adjust-uses!)

(defn- num-arith
  "有 Num 操作数的 Numbers 算术 / 比较：[方法名 实参 bool?]，否则 nil（cleat-102）。"
  [e]
  (nanopass-case [L3 Expr] e
    [(clj-call ~d0 ~d1 ~e* ...)
     (let [op (numbers-arith-op d0 d1 e*) args (drop 3 e*)]
       (when (and op opt-num? (or (numbers-num-ops op) (numbers-bool-ops op))
                  (not (and (= op "minus") (= 1 (count args))))
                  (some #(= :num (expr-type % var-types)) args))
         [(num-methods op) args (boolean (numbers-bool-ops op))]))]
    [else nil]))

(defn- cg-raw-num
  "把 e 的值以 Num 压栈：常量直接构造；Num 变量 ldloc；l64 / fl 变量转换；有 Num 操作数的算术递归按 Num 算；其余求值后 FromObject。"
  [e mc]
  (let [from-object! #(do (cg e mc :value) (emit! mc (i 'call num-type "FromObject" (i obj) num-type)))
        ;; 推断为 double / long 的子表达式（宿主调用、原始算术）：原始路径算出再包进 Num，不装箱
        from-typed! #(case (expr-type e var-types)
                       :fl (do (cg-raw e mc :fl) (emit! mc (i 'call num-type "FromDouble" (i double-type) num-type)))
                       :l64 (do (cg-raw e mc :l64) (emit! mc (i 'call num-type "FromInt64" (i int64-type) num-type)))
                       (from-object!))]
    (nanopass-case [L3 Expr] e
      [(quote ~d)
       (cond (instance? Int64 d) (emit! mc (i 'ldc.i8 d) (i 'call num-type "FromInt64" (i int64-type) num-type))
             (and (instance? Double d) (not (Double/IsNaN d)) (not (Double/IsInfinity d)))
             (emit! mc (i 'ldc.r8 d) (i 'call num-type "FromDouble" (i double-type) num-type))
             :else (from-object!))]
      [~x (let [loc (hget (:vars mc) x)]
            (case (when loc (first loc))
              :numarg (do (ref-var! mc x false) (emit! mc (i 'ldloc (nth loc 2))))
              (:tlocal :targ) (case (nth loc 2)
                                :num (do (ref-var! mc x false) (emit! mc (i 'ldloc (second loc))))
                                :l64 (do (cg-raw e mc :l64) (emit! mc (i 'call num-type "FromInt64" (i int64-type) num-type)))
                                :fl (do (cg-raw e mc :fl) (emit! mc (i 'call num-type "FromDouble" (i double-type) num-type)))
                                (from-object!))
              (from-object!)))]
      [(clj-call ~d0 ~d1 ~e* ...)
       (if-let [[m args bool?] (num-arith e)]
         (if bool?
           (from-object!)
           (do (doseq [a args] (cg-raw-num a mc))
               (emit! mc (i 'call num-type m (apply i (map (fn [_] num-type) args)) num-type))))
         (from-typed!))]
      [(if ~e0 ~e1 ~e2) (from-typed!)]
      [(let ([~x* ~e*] ...) ~body) (from-typed!)]
      [(begin ~e* ... ~e) (from-typed!)]
      [else (from-object!)])))

(defn- num-emit!
  "有 Num 操作数的 Numbers 算术 / 比较：按 Num 算，返回 :num 或 :bool；不是时 nil。"
  [e mc]
  (when-let [[m args bool?] (num-arith e)]
    (doseq [a args] (cg-raw-num a mc))
    (emit! mc (i 'call num-type m (apply i (map (fn [_] num-type) args)) (if bool? bool-type num-type)))
    (if bool? :bool :num)))

(defn cg-raw
  "把 e 的值以 t（:fx :fl :l64 :num）的原始形式压栈。"
  [e mc t]
  (let [oa (when (= t :l64) (numbers-arith-args e var-types))
        na (when (#{:fl :l64} t) (num-arith e))]
    (cond
      (= t :num) (cg-raw-num e mc)
      (and (#{:l64 :fl} t) (= t (typed-self-ret e))) (cg-typed-self! e mc)
      ;; 含 Num 操作数的算术要 double / long 原始值（推断把 (+ double-var num) 定成 double）：整棵按 Num 算再取值，不装箱
      (and na (not (nth na 2)))
      (do (cg-raw-num e mc) (emit! mc (i 'call num-type (if (= t :fl) "ToDouble" "ToInt64") (i num-type) (type-clr t))))
      (and oa (numbers-long-ops (first oa))) (numbers-raw-emit-args! (rest oa) (first oa) mc)
      (raw-prim-invoke e t) (cg-prim-invoke! (raw-prim-invoke e t) mc :raw)
      (raw-host-call e t) (cg-static-host-call! (raw-host-call e t) mc :raw)
      (and opt-joinraw? (cg-raw-join! e mc t)) nil
      :else (cg-raw-default e mc t))))

(defn- cg-raw-join!
  "if / begin 的原始发射（cleat-108）：各支 / 最后一项原始求值，汇合处不装箱。是这两种节点时生成并返回 true，否则 nil。"
  [e mc t]
  (nanopass-case [L3 Expr] e
    [(if ~e0 ~e1 ~e2)
     (let [else-l (new-label! mc) end-l (new-label! mc)]
       (cg e0 mc :test)
       (emit! mc (i 'brfalse else-l))
       (let [c2 (branch-ref-counts mc e2)]   ; 局部变量清除按分支，同 cg* 的 if
         (adjust-uses! mc c2 -)
         (cg-raw e1 mc t)
         (adjust-uses! mc c2 +))
       (emit! mc (i 'br end-l) (i 'label else-l))
       (cg-raw e2 mc t)
       (emit! mc (i 'label end-l))
       true)]
    [(begin ~e* ... ~e)
     (run! #(cg % mc :effect) e*)
     (cg-raw e mc t)
     true]
    ;; let（cleat-123）：同 cg* 绑定变量，体原始求值——内联的调用是 (let ([r Var 读] …) (if 守卫 快 慢))，两支都是原始值时不再装箱再拆箱
    [(let ([~x* ~e*] ...) ~body)
     ;; 只在体的推断类型正好是 t 时（否则交给 cg-raw-default 的转换）
     (when (= t (expr-type body var-types))
       (let [codes (mapv (fn [x e] (if (num-alias-loc mc x) [] (binding-code mc x e))) x* e*)]
         (doseq [[x code] (map vector x* codes)]
           (if (num-alias-loc mc x) (bind-local! mc x) (do (emit-code! mc code) (bind-value! mc x))))
         (cg-raw body mc t)
         true))]
    [else nil]))

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

(defn- native-host-call
  "x 是静态决议、返回类型为 pt（pt 为 nil 时不限）的宿主调用时返回它的决议结果 sl，否则 nil（cleat-111）。"
  [x pt]
  (nanopass-case [L3 Expr] x
    [(clj-call ~d0 ~d1 ~e* ...)
     (let [sl (host-call-signature d0 d1 e*)]
       (when (and (vector? sl) (= "method" (str (aget ^objects (first sl) 5)))
                  (or (nil? pt) (= pt (str (aget ^objects (first sl) 3)))))
         sl))]
    [else nil]))

(def ^:private value-arg-helpers
  {"System.Int32" "ArgInt32" "System.Int64" "ArgInt64" "System.Double" "ArgDouble" "System.Boolean" "ArgBoolean" "System.Char" "ArgChar"
   ;; cleat-111：较窄的数值类型
   "System.Byte" "ArgByte" "System.SByte" "ArgSByte" "System.Int16" "ArgInt16" "System.UInt16" "ArgUInt16" "System.Single" "ArgSingle"})

(defn- cg-static-host-call!
  "静态决议的宿主调用（原版 cg-static-host-call!）。sl：[签名 static? 实例 实参]。"
  [sl mc ctx]
  (let [[^objects sig static? inst args] sl
        decl (aget sig 0) nm (aget sig 1) pts (vec (aget sig 2)) ret (str (aget sig 3)) virt? (aget sig 4)
        convert! (fn [pt]
                   (cond (= pt "System.Object") nil
                         (value-arg-helpers pt)
                         (emit! mc (i 'call static-calls-type (value-arg-helpers pt) (i obj) pt))
                         :else (emit! mc (i 'castclass pt))))
        raw-long? (fn [x pt]
                    (let [t (expr-type x var-types)]
                      (cond (and (= t :l64) (#{"System.Int64" "System.Int32"} pt)) :l64
                            (and (= t :fl) (= pt "System.Double")) :fl
                            ;; cleat-111：实参本身是返回类型正好为 pt 的静态宿主调用（(aset ^bytes b i (byte x))）：原生值直接留在栈上
                            (and (contains? narrow-value-types pt) (native-host-call x pt)) :native
                            :else nil)))
        opnds (concat (if static? [] [inst]) args)
        types (concat (if static? [] [:instance]) pts)
        raws (mapv (fn [x pt] (when-not (= pt :instance) (raw-long? x pt))) opnds types)
        codes (mapv (fn [x raw?] (with-buffer mc #(cond (= raw? :native) (cg-static-host-call! (native-host-call x nil) mc :native)
                                                         raw? (cg-raw x mc raw?)
                                                         :else (cg x mc :value)))) opnds raws)
        conv! (fn [pt raw?]
                (cond (= pt :instance) (emit! mc (i 'ldstr nm) (i 'call static-calls-type "Instance" (i obj obj) obj) (i 'castclass decl))
                      (and raw? (= pt "System.Int32")) (emit! mc (i 'call "clojure.lang.RT" "intCast" (i int64-type) int32-type))
                      raw? nil
                      :else (convert! pt)))]
    (if (and (seq codes) (some contains-try? (rest codes)))
      (let [temps (mapv (fn [c raw? pt] (let [t (new-local! mc (cond (= raw? :native) pt raw? (type-clr raw?) :else obj))] (emit-code! mc c) (emit! mc (i 'stloc t)) t)) codes raws types)]
        (doseq [[t pt raw?] (map vector temps types raws)] (emit! mc (i 'ldloc t)) (conv! pt raw?)))
      (doseq [[c pt raw?] (map vector codes types raws)] (emit-code! mc c) (conv! pt raw?)))
    (let [kind (str (aget sig 5))]
      (case kind
        "method" (emit! mc (i (if virt? 'callvirt 'call) decl nm (apply i pts) ret))
        ;; 构造器（cleat-101）：newobj；值类型的结果装箱（sig 第 5 项为真时）
        "ctor" (do (emit! mc (i 'newobj decl (apply i pts))) (when virt? (emit! mc (i 'box decl))))
        (do (when (= kind "volatile-field") (emit! mc (i 'volatile.)))
            (emit! mc (if static? (i 'ldsfld decl nm) (i 'ldfld decl nm))))))
    (cond
      (= ctx :raw) (emit-ret-widen! mc ret)   ; Int32 与较窄的结果的原始用途是 l64 / fl（cleat-101 / 118）
      (= ctx :native) nil   ; 原生的返回值留在栈上（cleat-111，传给同类型的值类型形参）
      ;; 丢弃的结果不装箱（(aset arr i v) 在循环里每轮 box 一个 long 再 pop）
      (= ctx :effect) (when-not (= ret "System.Void") (emit! mc (i 'pop)))
      (= (str (aget sig 5)) "ctor") (finish-value! mc ctx)
      (= ret "System.Boolean") (finish-bool! mc ctx)
      :else (do (cond (= ret "System.Void") (emit! mc (i 'ldnull))
                      (= ret "System.Object") (when-not (and (> (alength sig) 6) (true? (aget sig 6)))   ; Clojure 定义的类型的方法：已是 Clojure 表示（cleat-113）
                                                (emit! mc (i 'call static-calls-type "RetObject" (i obj) obj)))
                      (= ret "System.Int32") (emit! mc (i 'call static-calls-type "RetInt32" (i int32-type) obj))
                      (= ret "System.Int64") (emit! mc (box-long))
                      (= ret "System.Double") (emit! mc (box-double))
                      (= ret "System.Char") (emit! mc (i 'box ret))
                      ;; cleat-111：较窄的数值结果与运行时决议（Reflector.ConvertFrom）一样加宽成 long / double
                      (#{"System.Byte" "System.UInt16"} ret) (emit! mc (i 'conv.u8) (box-long))
                      (#{"System.SByte" "System.Int16"} ret) (emit! mc (i 'conv.i8) (box-long))
                      (= ret "System.Single") (emit! mc (i 'conv.r8) (box-double))
                      :else nil)
                (finish-value! mc ctx)))))

;; —— 变量与局部变量清除（cleat-042 第 5 项，原版同名）——

(defn- var-location [mc x]
  (or (hget (:vars mc) x) (throw (ex-info (str "codegen: unbound variable " x) {:var x}))))

(def ^:dynamic *clear-args-by-ref* true)
(defn- clearing? [mc] (pos? (hsize (:uses mc))))
(defn- take-slot! [mc] (emit! mc (i 'call "Cleat.Core.Runtime.Locals" "Take" (i (str obj "&")) obj)))

;; 正在生成分层编译（tier-0 起步）的方法（cleat-114 的函数类）：最后一次读局部变量经 ldloca + Locals.Take，值汇合经局部变量——
;; tier-0 把 JIT 临时变量报告为整个方法内活着。直接按优化编译的方法（镜像、包、eval 的其余方法）里 JIT 按活跃区间报告，
;; 照常 ldloc + 置 null：Take 让局部变量地址暴露（不能放进寄存器），大方法里 JIT 的内联预算用尽时它还是一次真正的调用（cleat-116）
(def ^:dynamic *tiered-method* false)
(defn- load-slot! [mc kind n]
  (if *tiered-method*
    (do (emit! mc (i (if (= kind :arg) 'ldarga 'ldloca) n)) (take-slot! mc))
    (emit! mc (i (if (= kind :arg) 'ldarg 'ldloc) n) (i 'ldnull) (i (if (= kind :arg) 'starg 'stloc) n))))
(defn- arg-take!
  "转发方法把形参交给真正的实现：分层方法经 Take（tier-0 里形参槽在调用期间还报告为活），其余直接 ldarg。"
  [mc j]
  (if *tiered-method* (do (emit! mc (i 'ldarga j)) (take-slot! mc)) (emit! mc (i 'ldarg j))))

(def ^:dynamic *once-fn* false)   ; 正在生成 (fn* ^:once …) 的方法：闭包字段也按最后一次使用清除（cleat-117）

(defn- clear-var! [mc x]
  (when-let [loc (hget (:vars mc) x)]
    (case (first loc)
      :local (emit! mc (i 'ldnull) (i 'stloc (second loc)))
      :arg (emit! mc (i 'ldnull) (i 'starg (second loc)))
      :field (when *once-fn* (emit! mc (i 'ldarg 0) (i 'ldnull) (i 'stfld (:class mc) (second loc))))
      nil)))

(defn- ref-var!
  "引用变量 x：计数减一；load? 时压栈；是最后一个引用、不在钉住的循环里、是 object 局部变量或参数时清掉槽。"
  [mc x load?]
  (let [uses (:uses mc)
        n (hget uses x)
        last? (and n (do (hput! uses x (dec n)) (= n 1)) (zero? (or (hget (:pinned mc) x) 0)))
        loc (when last? (hget (:vars mc) x))]
    (cond
      (and load? loc (= :local (first loc))) (load-slot! mc :local (second loc))
      (and load? loc (= :arg (first loc)) *clear-args-by-ref*) (load-slot! mc :arg (second loc))
      :else (do (when load? (load-var! mc x))
                (when last? (clear-var! mc x))))))

(defn- ldloc-clear! [mc t]
  (if (clearing? mc)
    (load-slot! mc :local t)
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
      :numarg (emit! mc (i 'ldarg (second loc)))   ; Num 形参的装箱用途读原来的实参（cleat-102）
      :field (emit! mc (i 'ldarg 0) (i 'ldfld (:class mc) (second loc)))
      :dfield (do (emit! mc (i 'ldarg 0) (i 'ldfld (:class mc) (second loc)))
                  (when-let [r (nth loc 2 nil)] (box-raw! mc r)))   ; 原始字段按值用时装箱（cleat-112）
      :self (emit! mc (i 'ldarg 0)))))

(defn- store-var! [mc x]
  (let [loc (var-location mc x)]
    (case (first loc)
      :arg (emit! mc (i 'starg (second loc)))
      :local (emit! mc (i 'stloc (second loc)))
      :tlocal (emit! mc (i 'stloc (second loc)))
      :numarg (emit! mc (i 'stloc (nth loc 2)))   ; 别名的 jump 写回（值就是形参本身）
      :dfield (let [t (new-local! mc obj)]
                (emit! mc (i 'stloc t) (i 'ldarg 0) (i 'ldloc t))
                (case (nth loc 2 nil)   ; 原始字段：同构造器的转换（cleat-112）
                  :l64 (emit! mc (i 'call static-calls-type "PrimArgLong" (i obj) int64-type))
                  :fl (emit! mc (i 'call static-calls-type "PrimArgDouble" (i obj) double-type))
                  nil)
                (emit! mc (i 'stfld (:class mc) (second loc))))
      (throw (ex-info (str "codegen: cannot assign captured variable " x) {:var x})))))

(defn- bind-local-object! [mc x]
  (let [n (new-local! mc obj)] (hput! (:vars mc) x [:local n]) n))

(defn- num-alias-loc
  "x 是 Num 形参的别名且形参已绑定为 :numarg 时，形参的位置；否则 nil。"
  [mc x]
  (when-let [p (hget num-aliases x)]
    (let [loc (hget (:vars mc) p)] (when (and loc (= :numarg (first loc))) loc))))

(defn- bind-local! [mc x]
  (if-let [loc (num-alias-loc mc x)]
    (do (hput! (:vars mc) x loc) nil)
    (if-let [t (and (not @(:big? mc)) (hget var-types x))]
      (let [n (new-local! mc (type-clr t))] (hput! (:vars mc) x [:tlocal n t]) n)
      (bind-local-object! mc x))))

(defn- bind-value! [mc x] (bind-local! mc x) (store-var! mc x))

(defn- var-raw-type [mc x]
  (let [loc (hget (:vars mc) x)]
    (cond (nil? loc) nil
          (#{:tlocal :targ} (first loc)) (nth loc 2)
          (= :dfield (first loc)) (nth loc 2 nil)   ; 原始字段（cleat-112）
          (= :numarg (first loc)) :num
          :else nil)))

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
         (let [lf (new-label! mc) end-l (new-label! mc)
               outer (loop-outer-refs body1 x*)]
           ;; 分层方法里尾位置的循环：jump 读完实参后，循环体不再引用的 object 形参置空——fn 级 recur 的入口把形参复制进循环变量，
           ;; tier-0 帧里形参整个方法内都报告为活，抓着最初传进来的序列（核心库的 internal-reduce 一类，cleat-117）
           (hput! (:labels mc) x [lf x* (when (and *tiered-method* (= ctx :tail)) (set outer))])
           (let [code (with-buffer mc #(cg body0 mc ctx))]
             (emit-code! mc code)
             (when (branches-to? code lf)
               (when-not (= ctx :tail) (emit! mc (i 'br end-l)))
               (emit! mc (i 'label lf))
               ;; 体外绑定、体内引用的变量在循环里钉住（不清除）；循环体不跳回自己时（每个子句都有的 arity 标签，没有 recur）不是循环，不钉（cleat-117）
               (let [pinned (:pinned mc)
                     outer (if (l3-some? (fn [j] (nanopass-case [L3 Expr] j [(jump ~x2 ~e* ...) (= x2 x)] [else false])) body1 true) outer [])]
                 (doseq [v outer] (hupdate! pinned v inc 0))
                 (cg body1 mc ctx)
                 (doseq [v outer] (hupdate! pinned v dec 0)))
               (when-not (= ctx :tail) (emit! mc (i 'label end-l)))))))]
    [(jump ~x ~e* ...)
     (let [entry (or (hget (:labels mc) x) (throw (ex-info (str "codegen: jump to unknown label " x) {})))
           ;; 实参先全部求值到临时变量再写入形参（并行赋值）；有类型的形参用原始值的临时变量
           temps (mapv (fn [p a]
                         (if (num-alias-loc mc p)
                           nil   ; Num 形参的别名：实参就是形参本身，不用算也不用写
                           (let [rt (var-raw-type mc p)]
                             (if rt (cg-raw a mc rt) (cg a mc :value))
                             (let [t (new-local! mc (if rt (type-clr rt) obj))] (emit! mc (i 'stloc t)) t))))
                       (second entry) e*)]
       (when-let [keep (nth entry 2 nil)]
         ;; 按存储位置判断（cleat-123 发现）：let 把变量绑定到形参时可能直接复用形参的槽（别名），名字不在循环体里不代表槽不再被用
         (let [keep-locs (set (clojure.core/keep #(hget (:vars mc) %) keep))]
         ;; Num 形参的别名（循环变量就用形参的存储，cleat-102）不清
         (doseq [a (distinct (map (fn under [p a]
                                    (cond
                                      (num-alias-loc mc p) nil
                                      (symbol? a) a
                                      :else (nanopass-case [L3 Expr] a
                                              [(clj-call ~d0 ~d1 ~e* ...) (when (#{"Tagged" "Speculated"} d0) (under p (second e*)))]
                                              [else nil])))
                                  (second entry) e*))]
           (when (and (symbol? a) (not (contains? keep a)) (not (contains? keep-locs (hget (:vars mc) a))))
             (let [loc (hget (:vars mc) a)]
               (case (first loc)
                 :arg (emit! mc (i 'ldnull) (i 'starg (second loc)))
                 ;; Num 形参（cleat-102）：Num 副本的 Tag 字段对非数也持有对象本身，一并清掉
                 :numarg (emit! mc (i 'ldnull) (i 'starg (second loc))
                                (i 'ldnull) (i 'call num-type "FromObject" (i obj) num-type) (i 'stloc (nth loc 2)))
                 nil))))))
       (doseq [[p t] (map vector (second entry) temps)]
         (when t
           (if (var-raw-type mc p) (emit! mc (i 'ldloc t)) (ldloc-clear! mc t))
           (store-var! mc p)))
       (emit! mc (i 'br (first entry)))
       (case ctx :value (emit! mc (i 'ldnull)) :test (emit! mc (i 'ldc.i4 0)) nil))]
    [(quote ~d)
     (case ctx
       :effect nil
       :test (emit! mc (i 'ldc.i4 (if (and (some? d) (not (false? d))) 1 0)))
       (do (load-constant! mc d) (finish-value! mc ctx)))]
    [(if ~e0 ~e1 ~e2)
     (let [else-l (new-label! mc) end-l (new-label! mc)
           ;; 分层编译的 eval 单元（cleat-114）：值不留在求值栈上跨过汇合点——JIT 把跨块的栈值溢出到临时变量，tier-0 把它报告为整个方法内活着，
           ;; 只调用一次的方法里惰性序列的头被抓住；两支各存进同一个局部变量，汇合后取出并清空
           merge (when (and *tiered-method* (= ctx :value)) (new-local! mc obj))]
       (cg e0 mc :test)
       (emit! mc (i 'brfalse else-l))
       ;; 局部变量清除按分支：生成 then 支时先扣掉 else 支的引用数，生成 else 支前加回
       (let [c2 (branch-ref-counts mc e2)]
         (adjust-uses! mc c2 -)
         (cg e1 mc ctx)
         (adjust-uses! mc c2 +))
       (when merge (emit! mc (i 'stloc merge)))
       (when-not (= ctx :tail) (emit! mc (i 'br end-l)))
       (emit! mc (i 'label else-l))
       (cg e2 mc ctx)
       (when merge (emit! mc (i 'stloc merge)))
       (when-not (= ctx :tail) (emit! mc (i 'label end-l)))
       (when merge (emit! mc (i 'ldloca merge)) (take-slot! mc)))]
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
     (let [codes (mapv (fn [x e] (if (num-alias-loc mc x) [] (binding-code mc x e))) x* e*)]
       (doseq [[x code] (map vector x* codes)]
         (if (num-alias-loc mc x) (bind-local! mc x) (do (emit-code! mc code) (bind-value! mc x))))
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
       (load-var-object! mc d)
       (emit! mc (i 'callvirt clj-var-type (if st "get" "getRawRoot") () obj))
       (finish-value! mc ctx))]
    [(clj-var-set! ~d ~e)
     (operands! mc [#(load-var-object! mc d) #(cg e mc :value)])
     (emit! mc (i 'callvirt clj-var-type "set" (i obj) obj))
     (finish-value! mc ctx)]
    [(clj-def ~d ~st0 ~st1 ~e0 ~e1)
     (let [var! #(load-var-object! mc d)]
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
       (= d0 "Tagged") (cg (second e*) mc ctx)   ; 静态类型标注：生成代码时透明
       (= d0 "Speculated") (cg (second e*) mc ctx)   ; 推测的实参：值就是检查过的对象；原始用途由 cg-raw-default 的 unbox.any 取（cleat-103）
       (= d0 "TypedSelf") (do (cg-typed-self! e mc) (box-raw! mc (typed-self-ret e)) (finish-value! mc ctx))   ; cleat-104
       (= d0 "TypedEntry") (cg-typed-entry! e* mc ctx)
       (= d0 "SigEntry") (cg-signature-entry! e* mc ctx)   ; cleat-132
       (= d0 "KnownCall") (cg-known-call! e* mc ctx)   ; cleat-110
       (and (= d0 "EscapePropagate") (= 1 (count e*))) (cg-escape-propagate! (first e*) mc ctx)   ; cleat-125
       (and (= d0 "IsInstanceOf") (= 2 (count e*))
            (let [t (quoted-datum (first e*))]
              (and (instance? Type t) (pos? (count (Cleat.Core.Runtime.ClojureStaticCalls/IsinstName t))))))
       (do (cg (second e*) mc :value)
           (emit! mc (i 'isinst (Cleat.Core.Runtime.ClojureStaticCalls/IsinstName (quoted-datum (first e*)))) (i 'ldnull) (i 'cgt.un))
           (finish-bool! mc ctx))
       :else
       (case (or (num-emit! e mc) (numbers-raw-emit! e mc))
         :bool (finish-bool! mc ctx)
         :l64 (do (box-raw! mc :l64) (finish-value! mc ctx))
         :num (do (box-raw! mc :num) (finish-value! mc ctx))
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
       ;; eval 单元（分层编译）：结果不留在栈上跨过汇合点（同 if，cleat-114）
       (let [merge (when *tiered-method* (new-local! mc obj))]
         (emit! mc (i 'call d0 "invokeStatic" (apply i (objs (inc n))) obj))
         (when merge (emit! mc (i 'stloc merge)))
         (emit! mc (i 'br end))
         (emit! mc (i 'label slow))
         (ldloc-clear! mc root)
         (emit! mc (i 'castclass ifn-type))
         (doseq [l defs] (ldloc-clear! mc l))
         (emit! mc (i 'callvirt ifn-type "invoke" (apply i (objs n)) obj))
         (when merge (emit! mc (i 'stloc merge)))
         (emit! mc (i 'label end))
         (when merge (emit! mc (i 'ldloca merge)) (take-slot! mc)))
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

(defn- bind-num-params!
  "推断为 Num 的 object 形参（cleat-102）：方法入口 FromObject 一次存进 Num 局部变量，原始用途读它，装箱用途仍读实参。在 big? 决定之后调用。"
  [mc x*]
  (when-not @(:big? mc)
    (doseq [p x*]
      (let [loc (hget (:vars mc) p)]
        (when (and (= :num (hget var-types p)) (= :arg (first loc)))
          (let [t (new-local! mc num-type)]
            (emit! mc (i 'ldarg (second loc)) (i 'call num-type "FromObject" (i obj) num-type) (i 'stloc t))
            (hput! (:vars mc) p [:numarg (second loc) t])))))))

(defn- clj-clause-method [u class cl fields field-names self mname & [sig]]
  (nanopass-case [L3 Clause] cl
    [(clause (~x* ...) ~x ~body)
     (let [mc (make-method-ctx u class false nil nil nil)]
       (bind-clause-params! mc x* 1 sig)
       (doseq [[f fname] (map vector fields field-names)] (hput! (:vars mc) f [:field fname]))
       (when self (hput! (:vars mc) self [:self]))
       (reset! (:big? mc) (bigger-than? body big-body-nodes))
       (bind-num-params! mc x*)
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
       (when (and opt-typefeedback? @live-unit? (hget spec-profiles class))
         (cg-profile-args! mc class (count x*)))
       (when self (hput! (:vars mc) self [:arg 0]))
       (bind-clause-params! mc x* 1 sig)
       (reset! (:big? mc) (bigger-than? body big-body-nodes))
       (bind-num-params! mc x*)
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
                  (doseq [[c j] (map vector cs (iterate inc 1))] (arg-take! mc j) (unbox-to-sig! mc c))
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
          (doseq [[c j] (map vector cs (iterate inc 1))] (arg-take! mc j) (unbox-to-sig! mc c))
          (emit! mc (i 'call class "invokePrim" (apply i ptypes) rtype))
          (box-from-sig! mc rc)
          (emit! mc (i 'ret))
          (finish-method mc "invoke" ['override 'optimize] (objs n))))))

(defn- clj-forward-method [u class cl]
  (nanopass-case [L3 Clause] cl
    [(clause (~x* ...) ~x ~body)
     (i 'method "invoke" (i 'attrs 'override 'optimize) (apply i 'params (objs (count x*))) (i 'ret obj)
        (apply i 'body (concat [(i 'ldarg 0)]
                               (mapcat (fn [j] (if *tiered-method* [(i 'ldarga j) (i 'call "Cleat.Core.Runtime.Locals" "Take" (i (str obj "&")) obj)] [(i 'ldarg j)])) (range 1 (inc (count x*))))
                               [(i 'call class "invokeStatic" (apply i (objs (inc (count x*)))) obj) (i 'ret)])))]))

(def ^:private tier-classes (ht))   ; 分层编译的函数类（:class）：有循环或自递归的顶层 defn（cleat-114；镜像与包见 cleat-117）

(defn- l3-some?
  "e（含嵌套 lambda 的子句体；own? 为真时不进嵌套的 lambda）里有满足 pred 的节点。"
  [pred e & [own?]]
  (let [top e]
  (letfn [(walk [e]
            (or (pred e)
                (nanopass-case [L3 Expr] e
                  [(if ~e0 ~e1 ~e2) (or (walk e0) (walk e1) (walk e2))]
                  [(begin ~e* ... ~e) (or (some walk e*) (walk e))]
                  [(let ([~x* ~e*] ...) ~body) (or (some walk e*) (walk body))]
                  [(letrec ([~x* ~le*] ...) ~body) (or (some walk le*) (walk body))]
                  [(label ~x (~x* ...) ~body0 ~body1) (or (walk body0) (walk body1))]
                  [(jump ~x ~e* ...) (some walk e*)]
                  [(lambda ~nm (~x* ...) ~cl* ...)
                   (when-not (and own? (not (identical? e top)))
                     (some (fn [cl] (nanopass-case [L3 Clause] cl [(clause (~y* ...) ~y ~body) (walk body)])) cl*))]
                  [(call ~e ~e* ...) (or (walk e) (some walk e*))]
                  [(throw ~e) (walk e)]
                  [(try-finally ~e0 ~e1) (or (walk e0) (walk e1))]
                  [(try-catch ~e (~d* ~e*) ...) (or (walk e) (some walk e*))]
                  [(clj-invoke ~e ~e* ...) (or (walk e) (some walk e*))]
                  [(clj-keyword-invoke ~d ~e ~e* ...) (or (walk e) (some walk e*))]
                  [(clj-call ~d0 ~d1 ~e* ...) (some walk e*)]
                  [(clj-static-call ~d0 ~d1 ~e* ...) (some walk e*)]
                  [(clj-case ~d ~e0 ~e1 ~e* ...) (or (walk e0) (walk e1) (some walk e*))]
                  [(clj-def ~d ~st0 ~st1 ~e0 ~e1) (or (walk e0) (walk e1))]
                  [(box ~e) (walk e)]
                  [(set! ~x ~e) (walk e)]
                  [(set-box! ~x ~e) (walk e)]
                  [(check-initialized ~e) (walk e)]
                  [else false])))]
    (boolean (walk e)))))

(defn- real-loop?
  "节点是真正的循环：label 的循环体里有跳回自己的 jump（每个子句都有的 arity 标签，没有 recur 时不算）。"
  [n]
  (nanopass-case [L3 Expr] n
    [(label ~x (~x* ...) ~body0 ~body1)
     (l3-some? (fn [j] (nanopass-case [L3 Expr] j [(jump ~x2 ~e* ...) (= x2 x)] [else false])) body1)]
    [else false]))

(defn- mark-tier-classes!
  "eval 单元里会被反复调用或长时间循环的函数类分层编译（cleat-114）：体里有循环的任何 fn、引用自己 Var 的 defn（自递归）、
  letfn 里引用同组绑定的 fn（局部递归 / 互递归）。只调用一次、没有循环的 fn（-main 一类）照旧直接按优化编译。形式外面可能套着 HoldResult。"
  [form]
  (let [mark! (fn [le] (nanopass-case [L3 LambdaExpr] le
                         [(lambda ~nm (~x* ...) ~cl* ...) (when (l/clj-fn-name? nm) (hput! tier-classes (:class nm) true))]))]
    (l3-some?
      (fn [e]
        (nanopass-case [L3 Expr] e
          ;; 只看 lambda 自己的体（不进嵌套的 lambda）：-main 里嵌套一个带循环的闭包时，-main 本身只运行一次，不能停在 tier-0
          [(lambda ~nm (~x* ...) ~cl* ...) (when (l3-some? real-loop? e true) (mark! e)) false]
          ;; 自递归：某个子句经自己的 Var 以同样的参数个数调用自己（可以在嵌套的 lambda 里，如 map 的 lazy-seq）。只是引用 Var 不算
          ;; （defprotocol 的分派函数读自己 Var 上的缓存），不同 arity 之间的委托也不算（transduce 的 3 参版本调 4 参版本）——
          ;; 只调用一次时它们停在 tier-0，把实参抓到被调用的归约结束（cleat-117）
          [(clj-def ~d ~st0 ~st1 ~e0 ~e1)
           (let [own? (fn [f] (nanopass-case [L3 Expr] f [(clj-var-ref ~d2 ~st) (identical? d2 d)] [else false]))
                 self-argc (fn [n]
                             (nanopass-case [L3 Expr] n
                               [(clj-invoke ~f ~e* ...) (when (own? f) (count e*))]
                               [(clj-static-call ~d0 ~d1 ~e* ...) (when (own? (first e*)) (count (rest e*)))]
                               [(clj-call ~d0 ~d1 ~e* ...) (cond (= "TypedSelf" d0) :any
                                                                 (and (#{"KnownCall" "PrimInvoke"} d0) (own? (second e*))) (count (drop 2 e*)))]
                               [else nil]))]
             (nanopass-case [L3 Expr] e0
               [(lambda ~nm (~x* ...) ~cl* ...)
                (when (some (fn [cl]
                              (nanopass-case [L3 Clause] cl
                                [(clause (~y* ...) ~y ~body)
                                 (let [k (count y*) rest? (and y (not (r/absent? y)))]
                                   (l3-some? (fn [n] (let [c (self-argc n)]
                                                       (and c (or (= c :any) (if rest? (>= (long c) k) (= (long c) k))))))
                                             body))]))
                            cl*)
                  (mark! e0))]
               [else nil]))
           false]
          [(letrec ([~x* ~le*] ...) ~body)
           (let [names (set x*)]
             (doseq [le le*]
               ;; 调用同组的绑定才算（defprotocol 的分派函数 G__ 把自己传给缓存函数，只是引用）
               (when (l3-some? (fn [n] (nanopass-case [L3 Expr] n
                                         [(clj-invoke ~f ~e* ...) (and (symbol? f) (contains? names f))]
                                         [(call ~f ~e* ...) (and (symbol? f) (contains? names f))]
                                         [else false]))
                               le)
                 (mark! le))))
           false]
          [else false]))
      form))
  nil)

(defn- add-tiered-attr
  "方法形式 (method 名 (attrs …) …) 的 attrs 里加 tiered。"
  [m]
  (if (and (seq? m) (= 'method (first m)))
    (apply i (map (fn [x] (if (and (seq? x) (= 'attrs (first x))) (apply i (concat x ['tiered])) x)) m))
    m))

(def ^:private typed-bailout-type "Cleat.Core.Runtime.TypedBailout")
(def ^:private type-feedback-type "Cleat.Core.Runtime.TypeFeedback")
(def ^:private obj-array-type "System.Object[]")
(def ^:private long-array-type "System.Int64[]")
(def ^:private string-array-type "System.String[]")

(defn- cg-profile-args!
  "通用入口的活动剖面记录（cleat-128）：跨一个独立小方法调用运行时，避免入口主体里调用与后续分支组合成 RyuJIT 拒绝的形状。"
  [mc class n]
  (let [done (new-label! mc)]
    (emit! mc (i 'ldsfld class "tfOn") (i 'brfalse done))
    (doseq [j (range 1 (inc n))] (emit! mc (i 'ldarg j)))
    (emit! mc (i 'call class "ProfileArgs" (apply i (objs n)) void-type)
              (i 'label done))))

(defn- profile-fields
  "候选函数类的静态剖面状态（cleat-128）：每形参一个计数 / 类型槽；达到阈值时 Profile 返回 false 并写入 tfOn。"
  [class n]
  [(i 'field "tfName" string-type 'static 'readonly)
   (i 'field "tfOn" bool-type 'static)
   (i 'field "tfC" long-array-type 'static)
   (i 'field "tfT" obj-array-type 'static)
   (i 'field "tfN" string-array-type 'static)
   (i 'method "ProfileArgs" (i 'attrs 'static) (apply i 'params (objs n)) (i 'ret void-type) (i 'locals)
      (apply i 'body
             (concat
               [(i 'ldsfld class "tfOn") (i 'brfalse 'LprofileDone)
                (i 'ldsfld class "tfName") (i 'ldsfld class "tfC") (i 'ldsfld class "tfT") (i 'ldsfld class "tfN")]
               (mapcat (fn [j] [(i 'ldarg j)]) (range n))
               [(i 'call type-feedback-type "Profile"
                    (apply i string-type long-array-type obj-array-type string-array-type (objs n))
                    bool-type)
                (i 'stsfld class "tfOn") (i 'label 'LprofileDone) (i 'ret)])))
   (i 'method ".cctor" (i 'attrs 'cctor) (i 'params)
      (i 'body (i 'ldstr class) (i 'stsfld class "tfName")
               (i 'ldsfld class "tfName") (i 'ldc.i4 n)
               (i 'call type-feedback-type "Register" (i string-type int32-type) void-type)
               (i 'ldc.i4 n) (i 'newarr int64-type) (i 'stsfld class "tfC")
               (i 'ldc.i4 n) (i 'newarr obj) (i 'stsfld class "tfT")
               (i 'ldc.i4 n) (i 'newarr string-type) (i 'stsfld class "tfN")
               (i 'ldc.i4 1) (i 'stsfld class "tfOn") (i 'ret)))])

(defn- cg-typed-self!
  "类型化体里的自调用（cleat-104）：Var 的值还是本函数的类时直接 call invokeStaticTyped，否则经 IFn.invoke 调用、结果按签名取回
  （不是推测的类型时 TypedBailout）。原始值留在栈上。"
  [e mc]
  (nanopass-case [L3 Expr] e
    [(clj-call ~d0 ~d1 ~e* ...)
     (let [sig (first (quoted-datum (first e*))) f (second e*) args (drop 2 e*)
           cs (sig-params sig) rc (sig-ret sig) cname (:class mc)
           root (new-local! mc obj)
           _ (do (cg f mc :value) (emit! mc (i 'stloc root)))
           temps (mapv (fn [a c]
                         (let [r (sig-char-raw c)]
                           (if r (cg-raw a mc r) (cg a mc :value))
                           (let [t (new-local! mc (if r (type-clr r) obj))] (emit! mc (i 'stloc t)) t)))
                       args cs)
           slow (new-label! mc) end (new-label! mc)]
       (emit! mc (i 'ldloc root) (i 'isinst cname) (i 'brfalse slow) (i 'ldloc root))
       (doseq [t temps] (emit! mc (i 'ldloc t)))
       (emit! mc (i 'call cname "invokeStaticTyped" (apply i (cons obj (sig-param-types sig))) (sig-char-type rc)) (i 'br end))
       (emit! mc (i 'label slow) (i 'ldloc root) (i 'castclass ifn-type))
       (doseq [[t c] (map vector temps cs)] (emit! mc (i 'ldloc t)) (box-from-sig! mc c))
       (emit! mc (i 'callvirt ifn-type "invoke" (apply i (objs (count args))) obj)
              (i 'call typed-bailout-type (if (= rc \L) "Long" "Double") (i obj) (sig-char-type rc))
              (i 'label end)))]))

(defn- cg-typed-entry!
  "通用入口（cleat-104）：实参的类型与签名相符时在 try 里调 invokeStaticTyped（TypedBailout 时落回），否则生成通用体。"
  [e* mc ctx]
  (let [[sig ps] (quoted-datum (first e*)) body (second e*)
        cs (sig-params sig) rc (sig-ret sig) cname (:class mc)
        args (mapv (fn [p] (second (hget (:vars mc) p))) ps)
        gen (new-label! mc) end (new-label! mc)
        tmp (new-local! mc obj)]
    (doseq [[j c] (map vector args cs)]
      (when (sig-char-raw c)
        (emit! mc (i 'ldarg j) (i 'isinst (if (= c \L) int64-type double-type)) (i 'brfalse gen))))
    (emit! mc (i 'try
                 (apply i 'body (concat [(i 'ldarg 0)]
                                        (mapcat (fn [j c] (if (sig-char-raw c) [(i 'ldarg j) (i 'unbox.any (sig-char-type c))] [(i 'ldarg j)])) args cs)
                                        [(i 'call cname "invokeStaticTyped" (apply i (cons obj (sig-param-types sig))) (sig-char-type rc))
                                         (if (= rc \L) (box-long) (box-double))
                                         (i 'stloc tmp)]))
                 (i 'catch typed-bailout-type (i 'pop))))
    (emit! mc (i 'ldloc tmp) (i 'brfalse gen) (i 'ldloc tmp))
    (finish-value! mc ctx)
    (when-not (= ctx :tail) (emit! mc (i 'br end)))
    (emit! mc (i 'label gen))
    (cg body mc ctx)
    (when-not (= ctx :tail) (emit! mc (i 'label end)))))

(defn- cg-signature-call!
  "先检查 invokeStatic 原始实参，再调用类型化重载并装箱；TypedBailout 时去 fail。cache 非空表示慢路径命中后写入签名缓存。"
  [cname {:keys [sig]} result fail cache mc]
  ;; Num 优化会把形参槽改绑成 Num 局部；签名入口必须检查 / 拆箱的仍是 invokeStatic 的原始 object 实参。
  ;; 签名形参个数不能从通用体反推：collapse 可能移除外层 arity label；固定签名字符串本身包含返回类型。
  (let [args (mapv inc (range (dec (count sig))))
        body (concat [(i 'ldarg 0)]
                     (mapcat (fn [j c] [(i 'ldarg j) (i 'unbox.any (sig-char-type c))]) args (sig-params sig))
                     [(i 'call cname "invokeStaticTyped" (apply i (cons obj (sig-param-types sig))) (sig-char-type (sig-ret sig)))
                      (if (= (sig-ret sig) \L) (box-long) (box-double))
                      (i 'stloc result)])]
    (doseq [[j c] (map vector args (sig-params sig))]
      (emit! mc (i 'ldarg j) (i 'isinst (sig-char-type c)) (i 'brfalse fail)))
    ;; 特化体只返回原始 long / double，装箱后不会是 null；方法入口为 null，TypedBailout 后保持 null。
    (emit! mc (i 'try (apply i 'body body) (i 'catch typed-bailout-type (i 'pop)))
           (i 'ldloc result) (i 'brfalse fail))
    (when cache (emit! mc (i 'ldc.i4 cache) (i 'stsfld cname "ssKind")))
    ;; SigEntry 只包函数子句体，始终在 invokeStatic 的尾位置；直接返回可避免跨过后续 EH 块的汇合分支。
    (emit! mc (i 'ldloc result) (i 'ret))))

(defn- cg-signature-entry!
  "(clj-call \"SigEntry\" #f '[sig …] 通用体)（cleat-132）：静态签名缓存命中时直调特化；缓存不命中时
  依次尝试 LL / DD 并记住命中项。类型不符或 TypedBailout 回到通用体；其他异常照常传播。"
  [e* mc ctx]
  (let [body (second e*)
        entries (mapv #(array-map :sig %) (quoted-datum (first e*)))
        cname (:class mc)
        result (new-local! mc obj)
        hot (mapv (fn [_] (new-label! mc)) entries)
        between (mapv (fn [_] (new-label! mc)) (rest entries))
        slow (new-label! mc) generic (new-label! mc)
        fails (vec (concat between [generic]))]
    ;; 缓存只减少 isinst；错缓存 / Var 重定义都安全落入慢路径或通用体。
    (doseq [[idx _entry] (map-indexed vector entries)]
      (emit! mc (i 'ldsfld cname "ssKind") (i 'ldc.i4 (inc idx)) (i 'beq (hot idx))))
    (emit! mc (i 'br slow))
    (doseq [[idx sig] (map-indexed vector (map :sig entries))]
      (emit! mc (i 'label (hot idx)))
      (cg-signature-call! cname (entries idx) result slow nil mc))
    (emit! mc (i 'label slow))
    (doseq [[idx entry] (map-indexed vector entries)]
      ;; 上一个签名失败时分支到当前签名；最后一个签名失败则由 fails 指向通用体。
      (when (pos? idx) (emit! mc (i 'label (fails (dec idx)))))
      (cg-signature-call! cname entry result (fails idx) (inc idx) mc))
    (emit! mc (i 'label generic))
    (cg body mc ctx)))

(defn- known-call-target-ok?
  "KnownCall 的目标类能被这个单元引用：内存模式，或类在从文件加载的程序集里（编成包时，求值期间生成的类在运行时不存在）。"
  [^Type t]
  (or @live-unit?
      (let [a (.Assembly t)] (and (not (.IsDynamic a)) (not (str/blank? (.Location a)))))))

(defn- cg-known-call!
  "(clj-call \"KnownCall\" #f '类 Var读 实参 …)（cleat-110）：先求函数值再求实参（同 IFn 调用的顺序），isinst 类时 call 类.invokeStatic(f, 实参 …)，
  否则 IFn.invoke。临时变量按局部变量清除的规则读（不让实参在调用期间被临时变量抓住）。目标类不能引用时就是普通的 IFn 调用。"
  [e* mc ctx]
  (let [t (quoted-datum (first e*)) f (second e*) args (drop 2 e*) n (count args)]
    (if-not (and (instance? Type t) (known-call-target-ok? t) (<= n 20)
                 (pos? (count (Cleat.Core.Runtime.ClojureStaticCalls/IsinstName t))))
      (cg* (with-output-language [L3 Expr] (out (clj-invoke ~f ~args ...))) mc ctx)
      (let [cname (Cleat.Core.Runtime.ClojureStaticCalls/IsinstName t)
            root (new-local! mc obj)
            _ (do (cg f mc :value) (emit! mc (i 'stloc root)))
            temps (mapv (fn [a] (cg a mc :value) (let [l (new-local! mc obj)] (emit! mc (i 'stloc l)) l)) args)
            slow (new-label! mc) end (new-label! mc)]
        (emit! mc (i 'ldloc root) (i 'isinst cname) (i 'brfalse slow))
        (ldloc-clear! mc root)
        (doseq [l temps] (ldloc-clear! mc l))
        (let [merge (when *tiered-method* (new-local! mc obj))]   ; 结果不留在栈上跨过汇合点（同 if，cleat-114）
          (emit! mc (i 'call cname "invokeStatic" (apply i (objs (inc n))) obj))
          (when merge (emit! mc (i 'stloc merge)))
          (emit! mc (i 'br end) (i 'label slow))
          (ldloc-clear! mc root)
          (emit! mc (i 'castclass ifn-type))
          (doseq [l temps] (ldloc-clear! mc l))
          (emit! mc (i 'callvirt ifn-type "invoke" (apply i (objs n)) obj))
          (when merge (emit! mc (i 'stloc merge)))
          (emit! mc (i 'label end))
          (when merge (emit! mc (i 'ldloca merge)) (take-slot! mc)))
        (finish-value! mc ctx)))))

(defn- cg-escape-propagate!
  "(clj-call \"EscapePropagate\" #f 子表达式)（cleat-125）：marker 是跨调用返回的信号。不在保护区内时，
  尾位置直接 ret；其他位置 throw 同一个 marker（立刻离开当前方法，owner 会在外层拆包）。保护区里 CIL 不允许
  ret / 覆盖用户的 catch 语义，因此退回运行时异常协议。"
  [e mc ctx]
  (if (pos? @(:try-depth mc))
    ;; 用另一个运行时入口，避免 clj-call 分派再次进入本特殊节点
    (cg* (with-output-language [L3 Expr] (out (clj-call "EscapePropagateProtected" false ~e))) mc ctx)
    (let [r (new-local! mc obj)
          normal (new-label! mc)]
      (cg e mc :value)
      (emit! mc (i 'stloc r) (i 'ldloc r) (i 'call clojure-ops-type "IsEscapeMarker" (i obj) bool-type) (i 'brfalse normal))
      (emit! mc (i 'ldloc r))
      (if (= ctx :tail)
        (emit! mc (i 'ret))
        (emit! mc (i 'throw)))
      (emit! mc (i 'label normal) (i 'ldloc r))
      (finish-value! mc ctx))))

(defn- clj-typed-entry-method
  "invokeStaticTyped(self, T…) → R：类型化子句的体（cleat-104）。"
  [u class {:keys [sig params body]}]
  (let [mc (make-method-ctx u class true nil nil nil)]
    (bind-clause-params! mc params 1 sig)
    (reset! (:big? mc) (bigger-than? body big-body-nodes))
    (bind-num-params! mc params)
    (init-clearing! mc body)
    (cg body mc :tail)
    (finish-method mc "invokeStaticTyped" ['static 'optimize] (cons obj (sig-param-types sig)) (sig-char-type (sig-ret sig)))))

(defn- clj-fn-class!
  "fn* → AFunction / RestFn 的子类（原版 clj-fn-class!）。"
  [mc nm x1* cl* self]
  (binding [*tiered-method* (boolean (hget tier-classes (:class nm)))
            *once-fn* (boolean (:once nm))]
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
        add! #(swap! methods conj %)
        profile-n (when (and opt-typefeedback? @live-unit?) (hget spec-profiles (:class nm)))
        ;; 转发 / 桥方法不分层：tier-0 把转发实参的 JIT 临时变量报告为整个方法内活着，被调用的实现运行期间抓着实参（惰性序列的头，cleat-117）
        fwd-methods (atom #{})
        add-fixed! #(do (swap! fwd-methods conj %) (add! %))]
    (when (and (empty? fields) (not async?))
      (doseq [cl (if variadic (take (dec n) cl*) cl*)]
        (let [sig (clause-sig nm cl)]
          (add! (clj-static-clause-method u cname cl self sig))
          (when sig
            (add-fixed! (clj-prim-bridge u cname cl sig :static))
            (add-fixed! (clj-prim-bridge u cname cl sig :prim)))))
      (when-let [se (hget spec-entries (:class nm))]
        (add! (clj-typed-entry-method u cname se))))
    (when-let [ss (hget spec-signature-entries (:class nm))]
      (doseq [se ss] (add! (clj-typed-entry-method u cname se))))   ; 重载按 LL / DD 的实参类型区分（cleat-132）
    (doseq [[cl j] (map vector cl* (range))]
      (let [last-variadic? (and variadic (= j (dec n)))]
        (cond
          async? (do (add! (clj-async-clause-method u cname cl fields field-names self))
                     (add! (i 'method "invoke" (i 'attrs 'override) (i 'params) (i 'ret obj)
                              (i 'body (i 'ldarg 0) (i 'call cname "invokeAsync" () (task-of-object-type)) (i 'ret)))))
          (and (empty? fields) (not last-variadic?)) (add-fixed! (clj-forward-method u cname cl))
          (and (not last-variadic?) (clause-sig nm cl))
          (let [sig (clause-sig nm cl)]
            (add! (clj-clause-method u cname cl fields field-names self "invokePrim" sig))
            (add-fixed! (clj-prim-bridge u cname cl sig :invoke)))
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
                                   (when profile-n (profile-fields cname profile-n))
                                   (when (hget spec-signature-entries (:class nm))
                                     [(i 'field "ssKind" int32-type 'static)])
                                   (if *tiered-method* (map #(if (contains? @fwd-methods %) % (add-tiered-attr %)) @methods) @methods)))))
    [static? cname "Instance"])))

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
(defn- dinfo-fields [d] (mapv sl (sl (dinfo d 2))))   ; [名字 可变? volatile? 原始类型（"System.Int64" / "System.Double" / #f，cleat-112）]
(defn- field-raw "字段的原始类型 → :l64 / :fl / nil（cleat-112）" [ft] (case ft "System.Int64" :l64 "System.Double" :fl nil))
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
  [u class le spec mname fvars fnames ftypes caps cnames]
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
                        (doseq [[f n ft] (map vector fvars fnames ftypes)] (hput! (:vars mc) f (if-let [r (field-raw ft)] [:dfield n r] [:dfield n])))
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
                 (map (fn [[f _ vol? ft]] (apply i 'field f (if (string? ft) ft obj) (when (truthy? vol?) ['volatile]))) fields))
               ;; 构造器的形参都是 object（工厂与反射照旧）；^long / ^double 字段按原始签名的转换语义存（cleat-112）
               [(i 'method ".ctor" (i 'attrs 'ctor) (apply i 'params (objs (count ctor-fields)))
                   (apply i 'body (concat [(i 'ldarg 0) (i 'call obj ".ctor" () void-type)]
                                          (mapcat (fn [f j ft] (concat [(i 'ldarg 0) (i 'ldarg j)]
                                                                       (case (field-raw ft)
                                                                         :l64 [(i 'call static-calls-type "PrimArgLong" (i obj) int64-type)]
                                                                         :fl [(i 'call static-calls-type "PrimArgDouble" (i obj) double-type)]
                                                                         [])
                                                                       [(i 'stfld name f)]))
                                                  ctor-fields (iterate inc 1) (if reify? (repeat false) (map #(nth % 3 false) fields)))
                                          [(i 'ret)])))]
               ;; 有原始字段时另有一个按字段类型的构造器：(Complex. x y) 的实参已知是 double 时静态决议选它，不装箱（cleat-112）
               (let [fts (map #(nth % 3 false) fields)]
                 (when (and (not reify?) (some string? fts))
                   [(i 'method ".ctor" (i 'attrs 'ctor) (apply i 'params (map #(if (string? %) % obj) fts))
                       (apply i 'body (concat [(i 'ldarg 0) (i 'call obj ".ctor" () void-type)]
                                              (mapcat (fn [f j] [(i 'ldarg 0) (i 'ldarg j) (i 'stfld name f)]) ctor-fields (iterate inc 1))
                                              [(i 'ret)])))]))
               (map (fn [le sp mn] (deftype-method u name le sp mn x* fnames (if reify? (repeat false) (map #(nth % 3 false) fields)) caps cnames)) le* specs mnames)
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
    (reset! live-unit? (boolean live?))
    (run! hclear! [var-types sound-types fixed-types var-facts assigned-vars num-aliases
                   spec-entries spec-profiles spec-signature-entries tier-classes])
    (when (or live? opt-imagetier?) (run! mark-tier-classes! forms))
    (let [infer! (fn [forms]
                   (run! collect-assigned! forms)
                   (when opt-infer?
                     (if (and loop-flags (= (count loop-flags) (count forms)))
                       (doseq [[f lp?] (map vector forms loop-flags)] (when lp? (infer-var-types-into! f var-types)))
                       (doseq [f forms] (infer-var-types-into! f var-types)))))
          _ (infer! forms)
          ;; 不回跳、入口实参都是变量的标签（内联子句外面的 arity 标签）折叠成代换：原始值能穿过内联的体（cleat-123）
          forms (if opt-collapse?
                  (let [fs' (mapv collapse-entry-labels forms)]
                    (if (= fs' forms) forms (do (run! hclear! [var-types sound-types fixed-types var-facts assigned-vars num-aliases]) (infer! fs') fs')))
                  forms)
          ;; 循环推测（cleat-103）按第一遍推断的结果挑候选；改写了循环就清表重推（快份的形参由推断定成原始类型）
          forms (if (and opt-infer? (or opt-loopspec? opt-typed? opt-sigspec?))
                  (let [changed (volatile! false)
                        forms' (binding [*spec-changed* changed]
                                 (cond->> forms
                                   (or opt-typed? opt-sigspec?) (mapv speculate-entries)
                                   opt-loopspec? ((fn [fs] (if (and loop-flags (= (count loop-flags) (count fs)))
                                                             (mapv (fn [f lp?] (if lp? (speculate-loops f) f)) fs loop-flags)
                                                             (mapv speculate-loops fs))))))]
                    (if @changed
                      (do (run! hclear! [var-types sound-types fixed-types var-facts assigned-vars num-aliases])
                          (infer! forms')
                          ;; 类型化入口的体（不在形式里）：形参按签名固定类型后推断
                          (doseq [k (keys-of spec-entries)]
                            (let [{:keys [params guess body]} (hget spec-entries k)]
                              (doseq [[p t] (map vector params guess)] (when t (hput! fixed-types p t) (hput! var-types p t) (hput! sound-types p t)))
                              (infer-var-types-into! body var-types)))
                          (doseq [k (keys-of spec-signature-entries)]
                            (doseq [{:keys [params guess body]} (hget spec-signature-entries k)]
                              (doseq [[p t] (map vector params guess)] (when t (hput! fixed-types p t) (hput! var-types p t) (hput! sound-types p t)))
                              (infer-var-types-into! body var-types)))
                          forms')
                      forms))
                  forms)]
      (let [inits (mapv (fn [f k] (top-level-method u f k)) forms (range))
            n (count inits)
            entry (i 'method "Initialize" (i 'attrs 'static) (i 'params) (i 'ret obj)
                     (apply i 'body
                            (cond
                              (zero? n) [(i 'ldnull) (i 'ret)]
                              :else (concat (mapcat (fn [k] [(i 'call module-name (init-name k) () obj) (i 'pop)]) (range (dec n)))
                                            [(i 'call module-name (init-name (dec n)) () obj) (i 'ret)]))))
            module (apply i 'type module-name (i 'attrs 'abstract 'sealed) (concat inits [entry]))
            vars (vars-class u)   ; 在取常量表之前：它把各 Var 登记进常量表
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
                          (cons module (if vars (conj @(:classes u) vars) @(:classes u)))))]
        [lcil cs]))))
