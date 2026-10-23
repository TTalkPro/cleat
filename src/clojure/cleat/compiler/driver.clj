;; 编译驱动（cleat-090，designs/19 阶段 B）：Clojure 的 eval / load / compile / --compile-boot / --compile-package 走 Clojure 编译器。
;; 照原版 driver.sls / compile.sls / Driver.cs（流程、单元收集的后序、镜像构建的 id 重置、包程序集的改写）（"原版"指 cleat-093 前仓库里的 Scheme 实现 src/scheme，已删除，见 git 历史）。
;; 宿主在加载镜像之后 require 本命名空间并调 install-hooks!。
;;
;;   (eval-toplevel 形式)          Compiler.eval：序列形式先 macroexpand（带上原形式的源位置），顶层 (do …) 拆开逐个求值；
;;                                 其余分析为 L0、编成内存程序集执行（还没有简单形式的解释）
;;   (load-reader 读者 路径 名字)  load-source 钩子：逐个读形式、求值，*ns* 在外层绑定
;;   (compile-reader …)            compile 钩子（*compile-files*）：这个文件与它加载到的文件编成一个包程序集
;;   (compile-package 包名 形式 根目录 dll) / (compile-boot 形式 dll)
(ns cleat.compiler.driver
  (:refer-clojure :exclude [load-reader])
  (:require [clojure.string :as str]
            [clojure.walk]
            [cleat.nanopass :as np :refer [with-output-language out]]
            [cleat.nanopass.records :as r]
            [cleat.compiler.languages :as l :refer [L0]]
            [cleat.compiler.analyzer :as an]
            [cleat.compiler.pipeline :as pl]
            [cleat.compiler.codegen :as cg])
  (:import [Cleat.Core.Compiler AotPaths CompileTimeTypes Diagnostics DriverOps]
           [Cleat.Core.Runtime InterfaceFile InterfaceFile+Entry]
           [clojure.lang RT]))

(def ^:private run-form DriverOps/RunForm)
(def ^:private take-result DriverOps/TakeResult)
(def ^:private run-module DriverOps/RunModule)

;; —— 单元：编译一个文件 / 镜像 / 包时收集各顶层形式的 L0（求值之后记入，后序）——
;; 状态是线程局部的映射（DriverOps.State），不用 Clojure 的动态绑定：驱动的 binding 弹出时会连带弹掉用户代码里不成对的
;; pushThreadBindings（原版的 parameterize 与 Clojure 的绑定无关）。
;;   :unit     atom：L0 的向量
;;   :boot     构建镜像：嵌套加载的文件也记入同一个单元
;;   :package  {:roots 根目录列表或 true, :segments (atom [[加载键 [L0 …]] …])}

(defn- st [] (or (.-Value DriverOps/State) {}))

(defmacro ^:private with-st [m & body]
  `(let [old# (st)]
     (.set_Value DriverOps/State (merge old# ~m))
     (try ~@body (finally (.set_Value DriverOps/State old#)))))

(def ^:private eval-counter (atom 0))

(declare fn-node fn-class fn-arities inline-clauses lcil-types prod-of)

;; eval 单元的小访问器映射：deftype 顶层形式先记下“接口方法 → 唯一实现的字段”，
;; 后续顶层 defn 包装生成 .inl 时把接口调用改成“具体类型守卫 + 字段直读 / 直存”。
(def ^:private eval-accessors (atom {}))

(declare accessors-of merge-accessors)

(defn- register-eval-inlines!
  "eval 的顶层 (def v (fn* …))（cleat-116）：登记已知函数与可内联的小子句，之后的调用点在类型守卫后内联（与接口文件里的同样）。
  只认顶层形式本身（嵌在 let 里的 defn 可能引用外层局部变量）；宏与动态 Var 不登记。"
  [l0 asm lcil]
  (when (r/node? l0)
    (let [[k fs] (prod-of l0)]
      (when (= 'clj-def k)
        (let [[^clojure.lang.Var v _ _ e0] fs]
          (when-let [le (and (instance? clojure.lang.Var v) (not (.isDynamic v)) (not (.IsMacro v)) (fn-node e0))]
            (let [cls (fn-class le)]
              (when ((lcil-types lcil) cls)
                (let [e (InterfaceFile+Entry.)]
                  (set! (.-Var e) (symbol (str (.-Name (.-Namespace v))) (str (.-Symbol v))))
                  (set! (.-ClassName e) (str cls ", " asm))
                  (set! (.-Arities e) (int-array (fn-arities le)))
                  (set! (.-HasStatic e) true)
                  (doseq [[n bytes] (inline-clauses e0 @eval-accessors)] (DriverOps/AddInline e n bytes))
                  (InterfaceFile/RegisterEntry e))))))))))

(defn- run-compiled
  "L0 → 内存程序集 → 执行，值经 HoldResult 取回。"
  [l0]
  (let [nm (str "eval$c" (swap! eval-counter inc))   ; 原版驱动的程序集叫 eval$N，为区分而带 c
        [lcil cs] (pl/compile-unit [[0 (with-output-language [L0 Expr] (out (clj-call "HoldResult" false ~l0)))]]
                                   nm "#Module" true (fn [_ _ _]))
        ;; 诊断（cleat-139）：CLEAT_DUMP_EVAL_LCIL=<文件> 把每个 eval 单元的 Lcil 追加到文件
        _ (when-let [dump (System.Environment/GetEnvironmentVariable "CLEAT_DUMP_EVAL_LCIL")]
            (System.IO.File/AppendAllText dump (str (pr-str lcil) "\n")))
        asm (Cleat.Emit.LcilEmitter/EmitInMemoryData lcil nm (object-array cs))]
    (run-module asm)
    ;; 构建镜像 / 包时不登记：产物里的调用点不能链接到 eval 程序集里的类
    (when-not (or (:boot (st)) (:package (st))) (register-eval-inlines! l0 nm lcil))
    (take-result)))

(defn- register-eval-accessors!
  "扫描 eval 的顶层 L0，更新接口访问器实现表；默认关，CLEAT_OPT_ACCINLINE=1 时才开启。"
  [l0]
  (when an/opt-accinline?
    (let [found (atom {})]
      (letfn [(walk [x]
                (cond
                  (r/node? x)
                  (let [[k fs] (prod-of x)]
                    (when (= 'clj-deftype k)
                      (let [sx (np/unparse x)]
                        (when (and (seq? sx) (false? (nth (second sx) 1)))
                          (swap! found merge-accessors (accessors-of sx)))))
                    (run! walk fs))
                  (sequential? x) (run! walk x)))]
        (walk l0)
        (swap! eval-accessors merge-accessors @found)))))

;; —— 简单形式的解释（原版 Driver.TryEvalSimple，designs/04 §9：Chez 的 compile-interpret-simple）——
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

(declare simple?)

(defn- atomic?
  "调用的实参、let 的初值：常量、局部变量、Var 读、集合字面量（元素也是原子）。嵌套的调用不解释——解释器把实参值放在向量 / 环境里直到调用返回，
  惰性序列的头就被抓住（顶层的 (println (nth (map inc (range 3e7)) n)) 在 512 MB 堆下 OutOfMemory；编译后的代码按局部变量清除，cleat-117）"
  [x scope]
  (cond
    (symbol? x) (contains? scope x)
    (not (r/node? x)) false
    :else
    (let [[k fs] (prod-of x)]
      (case k
        quote true
        clj-var-ref (instance? clojure.lang.Var (first fs))
        ;; 实参本身都是原子的运行时操作（集合字面量、宿主字段读、静态调用……）：值不来自长时间运行的求值，也算原子；用户函数的已知调用不算
        clj-call (let [[d0 _ e*] fs] (and (not= "KnownCall" d0) (simple? x scope) (every? #(atomic? % scope) e*)))
        false))))

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
        clj-invoke (and (atomic? (first fs) scope) (every? #(atomic? % scope) (second fs)))
        clj-keyword-invoke (let [[d e e*] fs] (and (keyword? d) (<= (count e*) 1) (atomic? e scope) (every? #(atomic? % scope) e*)))
        clj-call (let [[d0 d1 e*] fs]
                   (if (= d0 "KnownCall")   ; 用户函数的已知调用（cleat-110）：解释时就是 IFn 调用
                     (every? #(atomic? % scope) (rest e*))
                     (and (some? (ops-method d0 d1 (count e*))) (every? #(atomic? % scope) e*))))
        call (let [[e0 e*] fs]
                (when-let [[x* body] (let-head e0)]
                  (and (= (count x*) (count e*)) (every? #(atomic? % scope) e*) (simple? body (into scope x*)))))
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
        clj-call (if (= (first fs) "KnownCall")
                   (let [[_ _ e*] fs] (apply (seval (second e*) env) (map #(seval % env) (drop 2 e*))))
                   (let [[d0 d1 e*] fs
                        m (ops-method d0 d1 (count e*))
                        vals (mapv #(seval % env) e*)
                        args (if (false? d1)
                               (object-array vals)
                               (let [n (long d1)] (object-array (conj (subvec vals 0 n) (object-array (subvec vals n))))))]
                    (unwrap-invoke m args)))
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
                    _ (register-eval-accessors! l0)
                    v (eval-l0 l0)]
                (when-let [u (:unit (st))] (swap! u conj l0))
                v))
            form))

(defn macroexpand-full [form]
  (loop [f form] (let [g (an/macroexpand-1 f)] (if (identical? g f) f (recur g)))))

(defn eval-toplevel
  "Clojure 的 Compiler.eval（原版 clojure-eval-toplevel）。"
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

;; —— 接口文件（原版 Driver.BuildInterfaceFile，读 L0 与 Lcil）——

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
  "各子句的形参个数；变长子句（与 必需个数 + 1 个形参同形的最后一个，CljFnName 的 :variadic）记为 -(必需 + 1)（原版 FnArities）"
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
  [x fn-names defs cands accessors]
  (cond
    (r/node? x)
    (let [[k fs] (prod-of x)]
      (case k
        lambda (when (l/clj-fn-name? (first fs)) (swap! fn-names update (:class (first fs)) (fnil inc 0)))
        clj-deftype (when (and (false? (nth (first fs) 1)))
                      (swap! accessors merge-accessors (accessors-of (np/unparse x))))
        clj-def (let [[d _ _ e0] fs]
                   (swap! defs update d (fnil inc 0))
                   (when-let [le (fn-node e0)] (swap! cands conj [d (fn-class le) (fn-arities le) e0])))
        nil)
      (when-not (= k 'quote)
        (doseq [f fs] (walk-l0 f fn-names defs cands accessors))))
    (sequential? x) (doseq [y x] (walk-l0 y fn-names defs cands accessors))
    :else nil))

;; 可内联的小函数（原版 Driver.InlineClauses / InlineOk）：顶层 (def v (fn* …)) 的固定子句足够小、只含 let / loop / if / 调用这类
;; 结构时，接口文件记下子句——Clojure 编译器记 L0 子句的 S 表达式（fasl，[:cleat/l0 子句]）

(def ^:private inline-max-pairs 160)
(def ^:private no-inline-heads '#{set! clj-def clj-var-set! clj-deftype try-catch try-finally})

(def ^:private inline-max-pairs-arith 400)
;; GetInstanceFieldDirect 是 cleat-145 读侧的直读节点（具体类型守卫之后），与 GetInstanceField 同等资格
(def ^:private arith-only-calls #{"CallStatic" "GetInstanceField" "GetInstanceFieldDirect" "NewOf" "Tagged"})

(defn- arith-only?
  "子句只有字段读、Numbers / RT 的静态运算、构造与局部结构（没有经 Var 的调用、关键字调用、直接链接）：deftype 的运算函数（cleat-123）"
  [sexpr]
  (letfn [(ok? [x]
            (cond
              (not (seq? x)) true
              (empty? x) true
              :else (let [h (first x)]
                      (cond
                        (= 'quote h) true
                        (#{'clj-invoke 'clj-keyword-invoke 'clj-static-call 'clj-var-ref 'clj-var-set! 'set!} h) false
                        (= 'clj-call h) (and (contains? arith-only-calls (second x)) (every? ok? (drop 3 x)))
                        :else (every? ok? x)))))]
    (ok? sexpr)))

;; —— deftype 字段访问器的识别（cleat-145）——

(defn- interface-name-of [x]
  (let [s (str x)] (if (str/starts-with? s ",") s (first (str/split s #"\s*,\s*" 2)))))

(defn- quoted-of
  "分析器把宿主调用的类型 / 成员名 / 泛型实参编成 (quote x)（sp-dot）：取回里面的原对象，不是这个形状时 nil。"
  [x]
  (when (and (seq? x) (= 'quote (first x)) (= 2 (count x))) (second x)))

(defn- untagged-of
  "剥掉引用类型实参外层的 (clj-call \"Tagged\" #f 'T e)（host-args）：只剥一层，形状不符原样返回。"
  [x]
  (if (and (seq? x) (= 'clj-call (first x)) (= "Tagged" (second x)) (= 5 (count x))) (nth x 4) x))

(defn- accessor-class-type
  "deftype 的 CLR 类名 → System.Type（cleat-145）：先按名决议（eval 路径里 deftype 已求值，取到真正的类型；
  144 的按包索引同样走这里），再取本进程刚编译、还没求值的编译期登记类型（包构建里是桩：
  codegen 决议不出 stfld / isinst 时自动退回反射路径，行为与内联前相同）。取不到就不做守卫内联。"
  [class-name]
  (when (string? class-name)
    (or (try (RT/classForName ^String class-name) (catch Exception _ nil))
        (try (CompileTimeTypes/Get ^String class-name) (catch Exception _ nil)))))

(defn- method-real-body
  "剥掉 deftype 方法体统一生成的 named-let 外壳 (call (letrec ([lp (lambda \"recur\" (clause ys false 体))]) lp) 实参 …)，
  返回 [形参 真正的方法体]；形状不合返回 nil。零实参方法的外壳是 (call (letrec …) )，所以只要求 call 至少两项。"
  [body]
  (when (and (seq? body) (= 'call (first body)) (>= (count body) 2))
    (let [callee (nth body 1)
          inits (drop 2 body)]
      (when (and (seq? callee) (= 'letrec (first callee)) (= 3 (count callee)))
        (let [bs (nth callee 1)
              lp (nth callee 2)
              binding (when (and (sequential? bs) (= 1 (count bs))) (first bs))]
          (when (and (sequential? binding) (= 2 (count binding))
                     (= (nth binding 0) lp)
                     (let [lam (nth binding 1)]
                       (and (seq? lam) (= 'lambda (first lam)) (= 3 (count lam)))))
            (let [cl2 (nth (nth binding 1) 2)]
              (when (and (seq? cl2) (= 'clause (first cl2)) (= 4 (count cl2))
                         (sequential? (nth cl2 1))
                         (= (count (nth cl2 1)) (count inits)))
                [(nth cl2 1) (nth cl2 3)]))))))))

(defn- read-accessor-field
  "方法体就是一个字段引用：返回该字段在绑定里的位置。"
  [body _ field-names]
  (when (symbol? body) (field-names body)))

(defn- set-accessor-field
  "方法体是「把形参 v 经一层临时绑定存进字段 f，并返回同一个 v」：返回字段位置。
  值必须来自本方法的形参（ys），否则调用点传进来的实参对不上，不做特化。"
  [body ys field-names]
  (when (and (seq? body) (= 'begin (first body)) (= 3 (count body)))
    (let [[_ wrapper result] body
          [_ lambda-form value] (when (and (seq? wrapper) (= 'call (first wrapper)) (= 3 (count wrapper))) wrapper)
          [_ _ clause] (when (and (seq? lambda-form) (= 'lambda (first lambda-form)) (= 3 (count lambda-form))) lambda-form)
          [_ temps _ inner] (when (and (seq? clause) (= 'clause (first clause)) (= 4 (count clause))) clause)
          [_ assignment returned] (when (and (seq? inner) (= 'begin (first inner)) (= 3 (count inner))) inner)
          [_ target tmp] (when (and (seq? assignment) (= 'set! (first assignment)) (= 3 (count assignment))) assignment)]
      (when (and (= 1 (count temps))
                 (symbol? value) (symbol? result) (symbol? tmp) (symbol? target)
                 (= result value) (some #(= % value) ys)
                 (= returned tmp) (= tmp (first temps)))
        (field-names target)))))

(defn- accessors-of
  "deftype 的 S 表达式 → {[接口 方法 实参个数] {:class :field :kind}}。只认读一个字段、或“存一个字段并返回新值”的方法。"
  [sx]
  (let [[_ info field-bindings & lambdas] sx
        class-name (nth info 0)
        field-descs (vec (nth info 2))
        fields (vec field-bindings)
        field-names (zipmap fields (range))
        by-index (fn [i] (nth (nth field-descs i) 0))
        read-field (fn [ys body] (some-> (read-accessor-field body ys field-names) by-index))
        ;; 写访问器返回的是字段下标（下面还要按下标查可变性），字段名由 by-index 在决议成立时再取
        set-field (fn [ys body] (set-accessor-field body ys field-names))]
    (loop [ms (vec (nth info 4)) ls (vec lambdas) acc {}]
      (if (or (empty? ms) (empty? ls))
        acc
        (let [[iface method _ ptypes] (first ms)
              lambda-form (first ls)
              clause (when (and (seq? lambda-form) (= 'lambda (first lambda-form)) (= 3 (count lambda-form)))
                       (nth lambda-form 2))
              mb (when (and (seq? clause) (= 'clause (first clause)) (= 4 (count clause)))
                   (method-real-body (nth clause 3)))
              ys (first mb)
              body (second mb)
              n (count ptypes)
              spec (cond
                     (and iface (= n 0) body) (when-let [f (read-field ys body)] {:class class-name :field f :kind :read})
                     (and iface (= n 1) body) (when-let [i (set-field ys body)]
                                                ;; 可变且非 volatile：直存用普通 stfld，volatile 字段需要 volatile. 前缀，不做特化
                                                (when (and (true? (nth (nth field-descs i) 1))
                                                           (not (true? (nth (nth field-descs i) 2))))
                                                  {:class class-name :field (by-index i) :kind :write}))
                     :else nil)]
          (recur (rest ms) (rest ls)
                 (if (and iface spec)
                   (assoc acc [(interface-name-of iface) (str method) n] spec)
                   acc)))))))

(defn- merge-accessors
  "合并访问器表；同一个接口方法有多个不同实现时删掉该项，调用点保留接口调用。相同实现的重新定义覆盖。"
  [m add]
  (reduce (fn [m [k v]]
            (if-let [old (m k)]
              (if (= old v) m (dissoc m k))
              (assoc m k v)))
          m add))

(defn- peel-named-let
  "剥一层「外层是纯转发实参的命名 let」：(call (letrec ([lp (lambda nm (clause (y* ...) false body))]) lp) x* ...)
  → [[[y x] …] body]（内层形参 → 外层表达式的改名表 + 里面的体）；形状不合或实参个数对不上时 nil。"
  [x]
  (when (and (seq? x) (= 'call (first x)) (>= (count x) 2))
    (let [callee (nth x 1)
          inits (vec (drop 2 x))]
      (when (and (seq? callee) (= 'letrec (first callee)) (= 3 (count callee)))
        (let [bs (nth callee 1)
              lp (nth callee 2)]
          (when (and (sequential? bs) (= 1 (count bs)))
            (let [pair (first bs)]
              (when (and (sequential? pair) (= 2 (count pair)) (= (nth pair 0) lp))
                (let [lam (nth pair 1)]
                  (when (and (seq? lam) (= 'lambda (first lam)) (= 3 (count lam)))
                    (let [cl2 (nth lam 2)]
                      (when (and (seq? cl2) (= 'clause (first cl2)) (= 4 (count cl2))
                                 (sequential? (nth cl2 1))
                                 (= (count (nth cl2 1)) (count inits)))
                        [(vec (map vector (vec (nth cl2 1)) inits)) (nth cl2 3)]))))))))))))

(defn- specialize-accessor-clause
  "把纯接口访问的包装子句改成“具体类型守卫 + 字段直读 / 直存，失败走原接口调用”（cleat-145）。
  只认剥掉纯转发的命名 let 之后**整个剩下的体就是那一个 CallInstance**：接收者 / 实参必须一路映射到本
  子句的形参（守卫与直读各用一次，不重复求值，内层改名不泄漏到外层）。形状不合、类型决议不出、
  任何异常都原样返回子句（调用点保持接口调用）。"
  [cl accessors]
  (try
    (let [top (set (second cl))
          [body ren] (loop [x (nth cl 3) r {}]
                       (if-let [[b nb] (peel-named-let x)]
                         (recur nb (reduce (fn [m [y i]] (if (or (top i) (m i)) (assoc m y i) m)) r b))
                         [x r]))
          ;; 逐层改名表可能有多跳：一路走到本子句的形参才算“同一个表达式、求值一次”，
          ;; 停在中间层的内层符号上是自由变量，停在非符号的表达式上会被守卫与直读各求值一次 —— 都不特化
          resolve (fn [s] (loop [x s seen #{}]
                            (cond (top x) x
                                  (or (not (symbol? x)) (seen x)) nil
                                  :else (if-let [r (ren x)] (recur r (conj seen x)) nil))))
          call (when (and (seq? body) (= 'clj-call (first body)) (= "CallInstance" (second body))
                          (= 4 (nth body 2)) (>= (count body) 7))
                 body)
          iface (when call (quoted-of (nth call 3)))
          method (when call (quoted-of (nth call 4)))
          obj (when call (untagged-of (nth call 5)))
          args (when call (mapv untagged-of (drop 7 call)))
          o (when (symbol? obj) (resolve obj))
          vs (when (and (some? o) (every? symbol? args)) (mapv resolve args))
          vs (when (and (some? vs) (every? some? vs)) vs)
          n (count vs)
          spec (when (and (some? iface) (some? method))
                 (get accessors [(interface-name-of iface) (str method) n]))
          ty (when spec (accessor-class-type (:class spec)))]
      (if-not (and o (some? vs) ty)
        cl
        (let [concrete (list 'quote ty)
              field (list 'quote (:field spec))
              direct (case (:kind spec)
                       :read (when (zero? n)
                               ;; 读侧用直读节点（守卫保证 x 已是 concrete）：通用 GetInstanceField 发射器要为实例位置
                               ;; 补 ldstr + Instance(=NotNil) 的空检查、object 结果还要过 RetObject，两条都比原来的
                               ;; 可内联接口调用更贵（实测 browse x1.54 / destruc x1.22 的回退），cleat-145 修正
                               (list 'clj-call "GetInstanceFieldDirect" false concrete field o))
                       :write (when (= 1 n)
                                (list 'clj-call "SetInstanceField" false concrete field o (first vs)))
                       nil)]
          (if direct
            ;; cl 是 np/unparse 出来的普通 list（不是 Associative）：按下标换体后重新拼回 list
            (apply list (assoc (vec cl) 3
                               (list 'if (list 'clj-call "IsInstanceOf" false concrete o)
                                     direct (nth cl 3))))
            cl))))
    (catch Exception _ cl)))

(defn- inline-ok?
  "序对个数不超过上限（同 原版按表的格子数），不含赋值、定义、类型、try 与嵌套的 fn*；只有字段读、算术与构造的子句上限放宽（cleat-123）"
  [sexpr]
  (let [inline-max-pairs (if (arith-only? sexpr) inline-max-pairs-arith inline-max-pairs)
        pairs (volatile! 0)]
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

(defn- prim-sig-wrap
  "有原始签名 sig（如 \"DOD\"：各形参与返回的 L / D / O）的子句 (clause (x …) false body)：L / D 形参经 PrimArgLong / PrimArgDouble 重新绑定、
  结果经 PrimRetLong / PrimRetDouble——与 invoke → invokePrim 的桥相同的转换；没有签名时原样。"
  [cl sig]
  (if-not (string? sig)
    cl
    (let [[_ x* rst body] cl
          cs (butlast sig) rc (last sig)
          conv (fn [c] (case c \L "PrimArgLong" \D "PrimArgDouble" nil))
          prim (keep (fn [[x c]] (when (conv c) [x (symbol (str (name x) "$pa")) (conv c)])) (map vector x* cs))
          m (into {} (map (fn [[x p _]] [x p]) prim))
          body' (if (empty? m) body (clojure.walk/postwalk (fn [y] (if (and (symbol? y) (m y)) (m y) y)) body))
          body'' (case rc
                   \L (list 'clj-call "CallStatic" 3 (list 'quote Cleat.Core.Runtime.ClojureStaticCalls) (list 'quote "PrimRetLong") (list 'quote nil) body')
                   \D (list 'clj-call "CallStatic" 3 (list 'quote Cleat.Core.Runtime.ClojureStaticCalls) (list 'quote "PrimRetDouble") (list 'quote nil) body')
                   body')]
      (list 'clause x* rst
            (if (empty? prim)
              body''
              (apply list 'call (list 'lambda false (list 'clause (apply list (map second prim)) false body''))
                     (map (fn [[x _ f]] (list 'clj-call "CallStatic" 3 (list 'quote Cleat.Core.Runtime.ClojureStaticCalls) (list 'quote f) (list 'quote nil) x)) prim)))))))

(defn- inline-clauses
  "def 的初值（不带自引用的 fn*，或外包 WithMetaOf）的可内联固定子句：[[参数个数 fasl 字节] …]"
  [e0 accessors]
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
                           sig (some (fn [[k sg]] (when (= (long k) (count x*)) sg)) (:prims nm))
                           raw (prim-sig-wrap (np/unparse cl) sig)
                           sp (when (and an/opt-accinline? (seq accessors))
                                (specialize-accessor-clause raw accessors))
                           ;; 特化把守卫与直读多包了一层调用，可能超出内联上限；这时退回未特化的
                           ;; 子句，绝不能因为特化失败把 cleat-144 已有的内联整体丢掉
                           sexpr (let [cand (or sp raw)]
                                   (if (inline-ok? cand) cand raw))]
                       ;; 有原始签名的子句（^long / ^double 形参或返回）：体外包上与 invokePrim 相同的入口 / 返回转换后内联（cleat-123；cleat-116 时不内联）
                       (when (inline-ok? sexpr)
                         (try [(count x*) (Cleat.Core.Runtime.Fasl/Write [:cleat/l0 sexpr])]
                              (catch Exception _ nil)))))   ; 常量里有 fasl 写不了的对象：不内联
                   (take last-fixed cl*)))))))

(defn- build-interface-file [asm l0s lcil image-id]
  (let [types (lcil-types lcil)
        fn-names (atom {}) defs (atom {}) cands (atom []) accessors (atom {})
        file (InterfaceFile.)]
    (run! #(walk-l0 % fn-names defs cands accessors) l0s)
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
          (doseq [[n bytes] (inline-clauses e0 @accessors)] (DriverOps/AddInline e n bytes)))
        (.Add (.-Entries file) e)))
    file))

(defn- write-interface-file [dll l0s lcil]
  (let [file (build-interface-file (System.IO.Path/GetFileNameWithoutExtension ^String dll) l0s lcil (DriverOps/Mvid dll))
        ifc (InterfaceFile/PathFor dll)]
    (when (System.IO.File/Exists ifc) (System.IO.File/Delete ifc))
    (.Save file ifc)))

;; —— AOT 单元（原版 compile.sls）——

(defn- check-boot-image-l0 [image-id]
  (with-output-language [L0 Expr] (out (clj-call "CheckBootImage" false (quote ~image-id)))))

(defn- compile-forms [l0s asm]
  (first (pl/compile-unit (map-indexed vector l0s) asm "#Module" false (fn [_ _ _]))))

(defn- dump-lcil? [] (string? (System.Environment/GetEnvironmentVariable "CLEAT_DUMP_LCIL")))

(defn- save-dll! [lcil dll]
  (when (System.IO.File/Exists dll) (System.IO.File/Delete dll))
  (Cleat.Emit.LcilEmitter/SaveData lcil dll dll false))

(defn- compile-boot-unit
  "Clojure 镜像（原版 clojure-compile-boot-unit）：一个单元编成 dll，旁边写 Lcil 文本与接口文件。"
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

;; 包程序集（原版 clojure-compile-package-unit / package-lcil）：入口 Initialize 只做镜像核对（Init0）与预链接，每个文件一个入口
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
   (let [pkg {:roots roots :segments (atom [])}
         rl @an/restrict-loop-inlines?]
     ;; 包构建不做 eval 专属的循环体内联限制（cleat-120）：与 eval 不同的内联决定会让自举的接口文件跨级反馈，破坏种子不动点
     (reset! an/restrict-loop-inlines? false)
     (try
       (with-st {:package pkg :unit nil}
         (run! eval-toplevel forms))
       (compile-package-unit @(:segments pkg) package dll (or image-id (AotPaths/ClojureImageId)))
       (finally (reset! an/restrict-loop-inlines? rl))))))

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
  (let [unit (atom [])
        runtime-spec? Cleat.Core.Runtime.RuntimeSigSpec/Enabled]
    (reset! form-ordinal 0)
    (let [inl @an/inline-calls? rw @an/reflection-warnings?]
      ;; 构建镜像时不做“新”内联决定（cleat-144）：种子与两级镜像的 .inl 必须逐字一致，内联决定留到包与 eval 单元。
      ;; cleat-145 的具体类型守卫同样留在包 / eval 两条路径，不动镜像构建口径。
      ;; 也不报反射警告（cleat-099）：core_print / gvec / instant 等开着 *warn-on-reflection*，镜像构建的输出不变
      (reset! an/inline-calls? false)
      (reset! an/reflection-warnings? false)
      (set! Cleat.Core.Runtime.RuntimeSigSpec/Enabled false)
      (try
        (with-st {:unit unit :boot true}
          (doseq [f forms] (reset-ids!) (eval-toplevel f)))
        (finally
          (set! Cleat.Core.Runtime.RuntimeSigSpec/Enabled runtime-spec?)
          (reset! an/inline-calls? inl) (reset! an/reflection-warnings? rw))))
    (compile-boot-unit @unit dll)))

;; —— 钩子 ——

(defn install-hooks!
  "登记 eval / load-source / compile / macroexpand-1（Cleat.Core.Runtime.RuntimeHooks）与镜像、包的编译入口（Cleat.Core.Compiler.DriverOps）。"
  []
  (Cleat.Core.Runtime.RuntimeHooks/Set "eval" eval-hook)
  (Cleat.Core.Runtime.RuntimeHooks/Set "load-source" load-hook)
  (Cleat.Core.Runtime.RuntimeHooks/Set "compile" compile-reader)
  (Cleat.Core.Runtime.RuntimeHooks/Set "macroexpand-1" (fn [form _] (an/macroexpand-1 form)))
  (set! Cleat.Core.Runtime.RuntimeSigSpec/SpecCompiler cg/compile-runtime-specialization)
  (set! Cleat.Core.Runtime.RuntimeSigSpec/SpecFilter cg/acceptable-runtime-specialization?)
  (DriverOps/InstallCompileHooks compile-boot compile-package)
  nil)
