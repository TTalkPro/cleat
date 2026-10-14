;; 编译驱动（cleat-090，designs/19 阶段 B）：Clojure 的 eval / load / compile / --compile-boot / --compile-package 走 Clojure 编译器。
;; 照 Scheme 版 src/scheme/clojure/driver.sls 与 compile.sls（流程、单元收集的后序、镜像构建的 id 重置、包程序集的改写）与
;; Cleat.Scheme.Clojure.Driver 的接口文件。CLEAT_COMPILER=clojure 时宿主在加载镜像之后调 install-hooks!。
;;
;;   (eval-toplevel 形式)          Compiler.eval：序列形式先 macroexpand（带上原形式的源位置），顶层 (do …) 拆开逐个求值；
;;                                 其余分析为 L0、编成内存程序集执行（还没有简单形式的解释）
;;   (load-reader 读者 路径 名字)  load-source 钩子：逐个读形式、求值，*ns* 在外层绑定
;;   (compile-reader …)            compile 钩子（*compile-files*）：这个文件与它加载到的文件编成一个包程序集
;;   (compile-package 包名 形式 根目录 dll) / (compile-boot 形式 dll)
(ns cleat.compiler.driver
  (:refer-clojure :exclude [load-reader])
  (:require [clojure.string :as str]
            [cleat.nanopass :as np :refer [with-output-language out]]
            [cleat.nanopass.records :as r]
            [cleat.compiler.languages :as l :refer [L0]]
            [cleat.compiler.analyzer :as an]
            [cleat.compiler.pipeline :as pl]
            [cleat.compiler.codegen :as cg])
  (:import [Cleat.Core.Compiler AotPaths Diagnostics DriverOps]
           [Cleat.Core.Runtime InterfaceFile InterfaceFile+Entry]))

(def ^:private run-form DriverOps/RunForm)
(def ^:private take-result DriverOps/TakeResult)
(def ^:private run-module DriverOps/RunModule)

;; —— 单元：编译一个文件 / 镜像 / 包时收集各顶层形式的 L0（求值之后记入，后序）——
;; 状态是线程局部的映射（DriverOps.State），不用 Clojure 的动态绑定：驱动的 binding 弹出时会连带弹掉用户代码里不成对的
;; pushThreadBindings（Scheme 版的 parameterize 与 Clojure 的绑定无关）。
;;   :unit     atom：L0 的向量
;;   :boot     构建镜像：嵌套加载的文件也记入同一个单元
;;   :package  {:roots 根目录列表或 true, :segments (atom [[加载键 [L0 …]] …])}

(defn- st [] (or (.-Value DriverOps/State) {}))

(defmacro ^:private with-st [m & body]
  `(let [old# (st)]
     (.set_Value DriverOps/State (merge old# ~m))
     (try ~@body (finally (.set_Value DriverOps/State old#)))))

(def ^:private eval-counter (atom 0))

(defn- run-compiled
  "L0 → 内存程序集 → 执行，值经 HoldResult 取回。"
  [l0]
  (let [nm (str "eval$c" (swap! eval-counter inc))   ; 与 Scheme 驱动的 eval$N 不重名（同时存在时按程序集名解析类型）
        [lcil cs] (pl/compile-unit [[0 (with-output-language [L0 Expr] (out (clj-call "HoldResult" false ~l0)))]]
                                   nm "#Module" true (fn [_ _ _]))
        asm (Cleat.Emit.LcilEmitter/EmitInMemoryData lcil nm (object-array cs))]
    (run-module asm)
    (take-result)))

;; —— 简单形式的解释（Scheme 版 Driver.TryEvalSimple，designs/04 §9：Chez 的 compile-interpret-simple）——
;; 不创建函数 / 类型的形式（常量、局部变量、Var 的读写与 def、调用、ClojureOps 的 clj-call、关键字调用、let、if、begin）直接解释，
;; 不生成内存程序集。先整体检查（simple?），通过才求值。

(defn- prod-of
  "节点的产生式名（符号 lambda clj-def quote …）与字段向量（顺序同产生式里元变量出现的顺序）。"
  [x]
  (let [^cleat.nanopass.records.INode n x]
    [(:keyword ((:by-tag (r/node-language n)) (.nanoTag n))) (.nanoFields n)]))

(def ^:private ops-methods (atom {}))

(defn- ops-method
  "clj-call 的目标：ClojureOps 的公有静态方法（固定个数时最后一个参数是 object[]）；没有时 nil。"
  [nm fixed argc]
  (when (and (string? nm) (or (false? fixed) (integer? fixed)))
    (let [fixed-n (if (false? fixed) -1 (long fixed))]
      (when (<= fixed-n argc)
        (let [k [nm (if (neg? fixed-n) argc (inc fixed-n)) fixed-n]]
          (if (contains? @ops-methods k)
            (@ops-methods k)
            (let [[_ pc] k
                  m (first (filter (fn [^System.Reflection.MethodInfo m]
                                     (let [ps (.GetParameters m)]
                                       (and (= nm (.Name m)) (= pc (alength ps))
                                            (or (neg? fixed-n) (= |System.Object[]| (.ParameterType ^System.Reflection.ParameterInfo (aget ps (dec pc))))))))
                                   (.GetMethods Cleat.Core.Runtime.ClojureOps
                                                (enum-or System.Reflection.BindingFlags/Public System.Reflection.BindingFlags/Static))))]
              (swap! ops-methods assoc k m)
              m)))))))

(defn- let-head
  "立即调用的单子句、无剩余参数的 lambda（let）：[形参 体]，否则 nil"
  [e]
  (when (r/node? e)
    (let [[k [nm cl*]] (prod-of e)]
      (when (and (= k 'lambda) (= 1 (count cl*)))
        (let [[_ [x* x body]] (prod-of (first cl*))]
          (when (r/absent? x) [x* body]))))))

(defn- simple? [x scope]
  (cond
    (symbol? x) (contains? scope x)
    (not (r/node? x)) false
    :else
    (let [[k fs] (prod-of x)]
      (case k
        quote true
        if (every? #(simple? % scope) fs)
        begin (and (every? #(simple? % scope) (first fs)) (simple? (second fs) scope))
        clj-var-ref (instance? clojure.lang.Var (first fs))
        clj-var-set! (and (instance? clojure.lang.Var (first fs)) (simple? (second fs) scope))
        clj-def (let [[d _ _ e0 e1] fs] (and (instance? clojure.lang.Var d) (simple? e0 scope) (simple? e1 scope)))
        clj-invoke (and (simple? (first fs) scope) (every? #(simple? % scope) (second fs)))
        clj-keyword-invoke (let [[d e e*] fs] (and (keyword? d) (<= (count e*) 1) (simple? e scope) (every? #(simple? % scope) e*)))
        clj-call (let [[d0 d1 e*] fs] (and (some? (ops-method d0 d1 (count e*))) (every? #(simple? % scope) e*)))
        call (let [[e0 e*] fs]
                (when-let [[x* body] (let-head e0)]
                  (and (= (count x*) (count e*)) (every? #(simple? % scope) e*) (simple? body (into scope x*)))))
        false))))

(defn- unwrap-invoke [^System.Reflection.MethodInfo m args]
  (try (.Invoke m nil args)
       (catch System.Reflection.TargetInvocationException e
         (if-let [inner (.InnerException e)]
           (.Throw (System.Runtime.ExceptionServices.ExceptionDispatchInfo/Capture inner))
           (throw e)))))

(defn- seval [x env]
  (if (symbol? x)
    (if (contains? env x) (env x) (throw (InvalidOperationException. (str "simple eval: unbound local " x))))
    (let [[k fs] (prod-of x)]
      (case k
        quote (first fs)
        if (let [[e0 e1 e2] fs] (if (Cleat.Core.Runtime.Truth/IsTrue (seval e0 env)) (seval e1 env) (seval e2 env)))
        begin (do (run! #(seval % env) (first fs)) (seval (second fs) env))
        clj-var-ref (let [[^clojure.lang.Var v st] fs] (if st (.get v) (.getRawRoot v)))
        clj-var-set! (let [[^clojure.lang.Var v e] fs] (.set v (seval e env)))
        ;; DefExpr（与 codegen 的 clj-def 相同的顺序）：setDynamic、bindRoot（有初值时）、setMeta；值为 Var
        clj-def (let [[^clojure.lang.Var v st0 st1 e0 e1] fs]
                   (when st1 (.setDynamic v))
                   (when st0 (.bindRoot v (seval e0 env)))
                   (.setMeta v (seval e1 env))
                   v)
        clj-invoke (let [[e e*] fs] (apply (seval e env) (map #(seval % env) e*)))
        clj-keyword-invoke (let [[kw e e*] fs
                                  target (seval e env)]
                              (if (empty? e*) (get target kw) (get target kw (seval (first e*) env))))
        clj-call (let [[d0 d1 e*] fs
                        m (ops-method d0 d1 (count e*))
                        vals (mapv #(seval % env) e*)
                        args (if (false? d1)
                               (object-array vals)
                               (let [n (long d1)] (object-array (conj (subvec vals 0 n) (object-array (subvec vals n))))))]
                    (unwrap-invoke m args))
        call (let [[e0 e*] fs
                    [x* body] (let-head e0)
                    vals (mapv #(seval % env) e*)]
                (seval body (into env (map vector x* vals))))))))

(defn- eval-l0 [l0]
  (if (and DriverOps/SimpleEvalEnabled (simple? l0 #{}))
    (do (DriverOps/CountSimple) (seval l0 {}))
    (do (DriverOps/CountCompiled) (run-compiled l0))))

(defn- eval-form [form]
  (run-form (fn []
              (let [l0 (an/analyze-top form)
                    v (eval-l0 l0)]
                (when-let [u (:unit (st))] (swap! u conj l0))
                v))
            form))

(defn macroexpand-full [form]
  (loop [f form] (let [g (an/macroexpand-1 f)] (if (identical? g f) f (recur g)))))

(defn eval-toplevel
  "Clojure 的 Compiler.eval（Scheme 版 clojure-eval-toplevel）。"
  [form]
  (let [form (if (seq? form) (an/transfer-source (run-form #(macroexpand-full form) form) form) form)]
    (if (and (seq? form) (= 'do (first form)))
      ;; 顶层 (do …) 的子形式也带上它的源位置
      (reduce (fn [_ x] (eval-toplevel (an/transfer-source x form))) nil (rest form))
      (eval-form form))))

(defn- eval-hook
  "eval 钩子（REPL、-e、clojure.core/eval）：不记入正在编译的单元"
  [form]
  (with-st {:unit nil} (eval-toplevel form)))

;; —— load ——

(def ^:private form-ordinal (atom 0))

(defn- reset-ids!
  "构建镜像时每个顶层形式开始前把 RT 的 id 计数器置为 编号 × 100000（designs/08 §7，gensym 与 fn 类名不随进程历史变化）"
  []
  (clojure.lang.RT/setNextID (* (swap! form-ordinal inc) 100000)))

(defn load-reader [reader path name]
  (let [r (if (instance? clojure.lang.LineNumberingTextReader reader) reader (clojure.lang.LineNumberingTextReader. reader))
        eof (Object.)
        ns0 *ns*
        v (binding [*ns* ns0]
            (loop [last nil]
              (let [form (Diagnostics/ReadForm r eof)]
                (if (identical? form eof)
                  last
                  (do (when (:boot (st)) (reset-ids!))
                      (recur (eval-toplevel form)))))))]
    ;; 构建镜像时：load 结束后 *ns* 恢复，镜像里被加载文件的形式之后也要恢复——记一个 (in-ns '原命名空间)
    (when-let [u (and (:boot (st)) (:unit (st)))]
      (swap! u conj (an/analyze-top (list 'clojure.core/in-ns (list 'quote (.-Name ^clojure.lang.Namespace ns0))))))
    v))

(defn- package-file? [pkg path]
  (let [roots (:roots pkg)]
    (or (true? roots)
        (let [full (AotPaths/PackageFullPath path)]
          (some #(AotPaths/UnderDirectory full %) roots)))))

(defn- load-built
  "编进镜像或包程序集的文件：*file* 绑成加载名（clojure/core.clj，同 JVM 的 :file），产物里不带构建机器上的绝对路径（cleat-091：
  从种子构建的镜像在任何位置都与种子相同）"
  [reader path name]
  (binding [*file* name] (load-reader reader path name)))

(defn- load-hook [reader path name]
  (let [pkg (:package (st))]
    (if (and pkg (not (:boot (st))) (package-file? pkg path))
      (let [unit (atom [])
            v (with-st {:unit unit} (load-built reader path name))
            k (AotPaths/LoadKey path name)]
        (when-not (some #(= k (first %)) @(:segments pkg))
          (swap! (:segments pkg) conj [k @unit]))
        v)
      (with-st {:unit (when (:boot (st)) (:unit (st)))}
        (if (:boot (st)) (load-built reader path name) (load-reader reader path name))))))

;; —— 接口文件（Cleat.Scheme.Clojure.Driver 的 BuildInterfaceFile，读 L0 与 Lcil）——

(defn- lcil-types
  "Lcil 里定义的类型名"
  [lcil]
  (into #{} (keep (fn [x] (when (and (seq? x) (= 'type (first x))) (second x)))) (tree-seq seq? seq lcil)))

(defn- fn-node
  "def 的初值是 fn*：(lambda CljFnName …)；带元数据时外面套 (clj-call \"WithMetaOf\" …)；命名 fn* 是 (letrec ([f (lambda …)]) f)"
  [e]
  (when (r/node? e)
    (let [[k [a b c]] (prod-of e)]
      (case k
        lambda (when (l/clj-fn-name? a) e)                              ; [nm cl*]
        clj-call (when (= "WithMetaOf" a) (fn-node (first c)))          ; [d0 d1 e*]
        letrec (when (and (= 1 (count a)) (= (first a) c)) (fn-node (first b)))   ; [x* e* body]
        nil))))

(defn- fn-class [le] (:class (first (second (prod-of le)))))

(defn- fn-arities
  "各子句的形参个数；变长子句（与 必需个数 + 1 个形参同形的最后一个，CljFnName 的 :variadic）记为 -(必需 + 1)（Scheme 版 FnArities）"
  [le]
  (let [[_ [nm cl*]] (prod-of le)
        r (mapv (fn [cl] (let [[_ [x*]] (prod-of cl)] (count x*))) cl*)
        req (:variadic nm)]
    (if (integer? req)
      (let [n (inc (long req))
            idx (last (keep-indexed (fn [i c] (when (= c n) i)) r))]
        (if idx (assoc r idx (- n)) r))
      r)))

(defn- walk-l0
  "顶层形式里的 clj-def 与 fn*：数 fn 类名与 Var 的定义次数，记下 (def v (fn* …)) 的候选。"
  [x fn-names defs cands]
  (cond
    (r/node? x)
    (let [[k fs] (prod-of x)]
      (case k
        lambda (when (l/clj-fn-name? (first fs)) (swap! fn-names update (:class (first fs)) (fnil inc 0)))
        clj-def (let [[d _ _ e0] fs]
                   (swap! defs update d (fnil inc 0))
                   (when-let [le (fn-node e0)] (swap! cands conj [d (fn-class le) (fn-arities le) e0])))
        nil)
      (when-not (= k 'quote)
        (doseq [f fs] (walk-l0 f fn-names defs cands))))
    (sequential? x) (doseq [y x] (walk-l0 y fn-names defs cands))
    :else nil))

;; 可内联的小函数（Scheme 版 Driver.InlineClauses / InlineOk）：顶层 (def v (fn* …)) 的固定子句足够小、只含 let / loop / if / 调用这类
;; 结构时，接口文件记下子句——Clojure 编译器记 L0 子句的 S 表达式（fasl，[:cleat/l0 子句]，与 Scheme 编译器的 Lcore 子句区分）

(def ^:private inline-max-pairs 160)
(def ^:private no-inline-heads '#{set! clj-def clj-var-set! clj-deftype try-catch try-finally})

(defn- inline-ok?
  "序对个数不超过上限（同 Scheme 版按表的格子数），不含赋值、定义、类型、try 与嵌套的 fn*"
  [sexpr]
  (let [pairs (volatile! 0)]
    (letfn [(ok? [x]
              (loop [xs x]
                (cond
                  (not (seq? xs)) true
                  (empty? xs) true
                  :else
                  (let [h (first xs)]
                    (vswap! pairs inc)
                    (cond
                      (> @pairs inline-max-pairs) false
                      (and (identical? xs x) (= 'quote h)) true
                      (and (identical? xs x) (symbol? h) (no-inline-heads h)) false
                      (and (identical? xs x) (= 'lambda h) (l/clj-fn-name? (second xs))) false
                      (and (seq? h) (not (ok? h))) false
                      :else (recur (rest xs)))))))]
      (ok? sexpr))))

(defn- inline-clauses
  "def 的初值（不带自引用的 fn*，或外包 WithMetaOf）的可内联固定子句：[[参数个数 fasl 字节] …]"
  [e0]
  (let [fn-le? (fn [x] (and (r/node? x) (let [[k [a]] (prod-of x)] (and (= 'lambda k) (l/clj-fn-name? a)))))
        le (cond
             (fn-le? e0) e0
             (r/node? e0) (let [[k [a _ c]] (prod-of e0)]
                            (when (and (= 'clj-call k) (= "WithMetaOf" a) (fn-le? (first c))) (first c)))
             :else nil)]
    (when le
      (let [[_ [nm cl*]] (prod-of le)
            last-fixed (if (integer? (:variadic nm)) (dec (count cl*)) (count cl*))]
        (vec (keep (fn [cl]
                     (let [[_ [x*]] (prod-of cl)
                           sexpr (np/unparse cl)]
                       (when (inline-ok? sexpr)
                         (try [(count x*) (Cleat.Scheme.Fasl/Write [:cleat/l0 sexpr] true)]
                              (catch Exception _ nil)))))   ; 常量里有 fasl 写不了的对象：不内联
                   (take last-fixed cl*)))))))

(defn- build-interface-file [asm l0s lcil image-id]
  (let [types (lcil-types lcil)
        fn-names (atom {}) defs (atom {}) cands (atom [])
        file (InterfaceFile.)]
    (run! #(walk-l0 % fn-names defs cands) l0s)
    (set! (.-AssemblyName file) asm)
    (set! (.-ImageId file) image-id)
    (doseq [[^clojure.lang.Var v cls arities e0] @cands
            :when (and (= 1 (@defs v)) (= 1 (@fn-names cls)) (types cls)
                       (not (.isDynamic v)) (not (:redef (meta v))))]
      (let [e (InterfaceFile+Entry.)]
        (set! (.-Var e) (symbol (str (.-Name (.-Namespace v))) (str (.-Symbol v))))
        (set! (.-ClassName e) (str cls ", " asm))
        (set! (.-Arities e) (int-array arities))
        (set! (.-HasStatic e) true)
        (when-not (.IsMacro v)
          (doseq [[n bytes] (inline-clauses e0)] (DriverOps/AddInline e n bytes)))
        (.Add (.-Entries file) e)))
    file))

(defn- write-interface-file [dll l0s lcil]
  (let [file (build-interface-file (System.IO.Path/GetFileNameWithoutExtension ^String dll) l0s lcil (DriverOps/Mvid dll))
        ifc (InterfaceFile/PathFor dll)]
    (when (System.IO.File/Exists ifc) (System.IO.File/Delete ifc))
    (.Save file ifc)))

;; —— AOT 单元（Scheme 版 compile.sls）——

(defn- check-boot-image-l0 [image-id]
  (with-output-language [L0 Expr] (out (clj-call "CheckBootImage" false (quote ~image-id)))))

(defn- compile-forms [l0s asm]
  (first (pl/compile-unit (map-indexed vector l0s) asm "#Module" false (fn [_ _ _]))))

(defn- dump-lcil? [] (string? (System.Environment/GetEnvironmentVariable "CLEAT_DUMP_LCIL")))

(defn- save-dll! [lcil dll]
  (when (System.IO.File/Exists dll) (System.IO.File/Delete dll))
  (Cleat.Emit.LcilEmitter/SaveData lcil dll dll false))

(defn- compile-boot-unit
  "Clojure 镜像（Scheme 版 clojure-compile-boot-unit）：一个单元编成 dll，旁边写 Lcil 文本与接口文件。"
  [l0s dll]
  (let [asm (System.IO.Path/GetFileNameWithoutExtension ^String dll)
        lcil (compile-forms l0s asm)
        text (cg/lcil->string lcil)
        lcil-path (System.IO.Path/ChangeExtension ^String dll ".lcil")]
    (spit lcil-path text)
    (when (System.IO.File/Exists dll) (System.IO.File/Delete dll))
    (Cleat.Emit.LcilEmitter/Save text lcil-path dll false)
    (write-interface-file dll l0s lcil)
    dll))

;; 包程序集（Scheme 版 clojure-compile-package-unit / package-lcil）：入口 Initialize 只做镜像核对（Init0）与预链接，每个文件一个入口
;; Load<i> 依次调它的 Init<k>；程序集级特性 CleatPackage、CleatNamespace 作索引；(lazy-start)；接口文件作为嵌入资源

(defn- init-call [k] (list 'call "#Module" (str "Init" k) () "System.Object"))
(defn- load-name [i] (str "Load" i))

(defn- load-method [i start n]
  (list 'method (load-name i) '(attrs static) '(params) '(ret "System.Object")
        (apply list 'body (if (zero? n)
                            ['(ldnull) '(ret)]
                            (concat (mapcat (fn [k] [(init-call k) '(pop)]) (range start (+ start n -1)))
                                    [(init-call (+ start n -1)) '(ret)])))))

(defn- init-call? [x] (and (seq? x) (= 'call (first x)) (= "#Module" (second x))))

(defn- rewrite-entry
  "Initialize 的体：保留第一个 Init 调用之前的部分，再调前 nfixed 个 Init，返回 nil"
  [m nfixed]
  (map (fn [c]
         (if (and (seq? c) (= 'body (first c)))
           (apply list 'body (concat (take-while (complement init-call?) (rest c))
                                     (mapcat (fn [k] [(init-call k) '(pop)]) (range nfixed))
                                     ['(ldnull) '(ret)]))
           c))
       m))

(defn- package-lcil [lcil segments package nfixed]
  (let [loads (loop [ss segments i 0 start nfixed acc []]
                (if (empty? ss)
                  acc
                  (let [[k forms] (first ss) n (count forms)]
                    (recur (rest ss) (inc i) (+ start n) (conj acc [k (load-method i start n)])))))
        attrs (concat ['(lazy-start) (list 'attribute "Cleat.Core.CleatPackageAttribute" package)]
                      (map (fn [[k m]] (list 'attribute "Cleat.Core.CleatNamespaceAttribute" k "#Module" (second m))) loads))]
    (apply list
           (map (fn [item]
                  (cond
                    (and (seq? item) (= 'assembly (first item))) (apply list (concat item attrs))
                    (and (seq? item) (= 'type (first item)) (= "#Module" (second item)))
                    (apply list (concat (map (fn [x] (if (and (seq? x) (= 'method (first x)) (= "Initialize" (second x)))
                                                        (apply list (rewrite-entry x nfixed))
                                                        x))
                                             item)
                                        (map second loads)))
                    :else item))
                lcil))))

(defn- with-resources [lcil resources]
  (apply list (map (fn [item]
                     (if (and (seq? item) (= 'assembly (first item)))
                       (apply list (concat item (map #(apply list 'resource %) resources)))
                       item))
                   lcil)))

(defn- compile-package-unit [segments package dll image-id]
  (let [check (if (string? image-id) [(check-boot-image-l0 image-id)] [])
        l0s (vec (concat check (mapcat second segments)))
        asm (System.IO.Path/GetFileNameWithoutExtension ^String dll)
        lcil (package-lcil (compile-forms l0s asm) segments package (count check))
        dir (System.IO.Path/Combine (System.IO.Path/GetTempPath) (str "cleat-ifc-" (.ToString (Guid/NewGuid) "N")))
        _ (System.IO.Directory/CreateDirectory dir)
        ifc (System.IO.Path/Combine dir InterfaceFile/ResourceName)
        _ (.Save (build-interface-file asm l0s lcil Guid/Empty) ifc)
        inl (InterfaceFile/InlinePathFor ifc)
        lcil (with-resources lcil (cons ["cleat.ifc" ifc] (when (System.IO.File/Exists inl) [["cleat.inl" inl]])))]
    (when (dump-lcil?) (spit (str dll ".lcil") (cg/lcil->string lcil)))
    (try (save-dll! lcil dll)
         (finally (try (System.IO.Directory/Delete dir true) (catch System.IO.IOException _ nil))))
    dll))

(defn compile-package
  "在 user 命名空间里依次求值形式（通常是 (require 'ns) …），根目录下被加载到的文件编成一个包程序集（designs/17 §2）。
  image-id：镜像核对用的镜像 id（缺省正在运行的镜像；自举时为下一级镜像编编译器包，cleat-091）"
  ([package forms roots dll] (compile-package package forms roots dll nil))
  ([package forms roots dll image-id]
   (let [pkg {:roots roots :segments (atom [])}]
     (with-st {:package pkg :unit nil}
       (run! eval-toplevel forms))
     (compile-package-unit @(:segments pkg) package dll (or image-id (AotPaths/ClojureImageId))))))

(defn- compile-reader
  "*compile-files* 为真时加载文件：这个文件与它加载到的、还没加载过的文件编成一个包程序集"
  [reader path name]
  (if (:package (st))
    (load-hook reader path name)
    (let [pkg {:roots true :segments (atom [])}
          v (with-st {:package pkg :unit nil} (load-hook reader path name))
          k (AotPaths/LoadKey path name)]
      (compile-package-unit @(:segments pkg) (AotPaths/LoadKeyNamespace k) (AotPaths/CompileOutputPath path name) (AotPaths/ClojureImageId))
      v)))

(defn compile-boot
  "依次求值形式（boot-order 的 load / require），全部加载到的文件的顶层形式按求值顺序（后序）收集成一个单元，编译为 Clojure 镜像 dll。"
  [forms dll]
  (let [unit (atom [])]
    (reset! form-ordinal 0)
    ;; 构建镜像时不内联：镜像里的子句不含内联结果，各级镜像的接口文件相同，不动点成立
    (let [inl @an/inline-calls?]
      (reset! an/inline-calls? false)
      (try
        (with-st {:unit unit :boot true}
          (doseq [f forms] (reset-ids!) (eval-toplevel f)))
        (finally (reset! an/inline-calls? inl))))
    (compile-boot-unit @unit dll)))

;; —— 钩子 ——

(defn install-hooks!
  "登记 eval / load-source / compile / macroexpand-1（Cleat.Core.Runtime.RuntimeHooks）与镜像、包的编译入口（Cleat.Core.Compiler.DriverOps）。"
  []
  (Cleat.Core.Runtime.RuntimeHooks/Set "eval" eval-hook)
  (Cleat.Core.Runtime.RuntimeHooks/Set "load-source" load-hook)
  (Cleat.Core.Runtime.RuntimeHooks/Set "compile" compile-reader)
  (Cleat.Core.Runtime.RuntimeHooks/Set "macroexpand-1" (fn [form _] (an/macroexpand-1 form)))
  (DriverOps/InstallCompileHooks compile-boot compile-package)
  nil)
