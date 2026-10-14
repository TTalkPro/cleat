;; Clojure 编译器的流水线（cleat-086 起）：前端 → 前中端 pass → codegen（cleat-088）。影子模式（cleat-085）经 shadow-stages
;; 对每个形式转储各阶段与 Lcil，tools/compiler-diff.clj 与 Scheme 编译器的转储比较（designs/19 D5 的 G1 / G2）；eval-form 用
;; Clojure 编译器编译并运行一个顶层形式（驱动在 cleat-090）。
(ns cleat.compiler.pipeline
  (:require [cleat.compiler.analyzer :as an]
            [cleat.compiler.dump :as dump]
            [cleat.compiler.loops :as loops]
            [cleat.compiler.front :as front]
            [cleat.compiler.closures :as closures]
            [cleat.compiler.codegen :as codegen]
            [cleat.nanopass :refer [with-output-language out]]
            [cleat.compiler.languages :refer [L0]]))

(defn eval-unit-l0
  "eval 的单元：驱动把顶层形式包成 ($clj-call 'HoldResult #f 核心)（值经线程槽交出，cleat-055）。"
  [form]
  (with-output-language [L0 Expr]
    (out (clj-call "HoldResult" false ~(an/analyze-top form)))))

(defn front-end
  "L0 → L3，返回 [L3 分析表]（Scheme 版 compiler.sls front-end；cleat-087）。dump：每个阶段的回调 (dump 阶段 节点)。
  check-forward 与 simplify-if 在 Clojure 输入下不改变任何形式（cleat-085 的普查），check-forward 照样运行（前向变量总是空），
  simplify-if 不移植；cp0-inline 同样不改变 Clojure 代码（候选只有非 fn* 的 lambda，cleat-087 用 G1 确认），只做 simplify-lets。
  转储的阶段名同 Scheme 版：p0 l0 sl cf c1 in l1 l2 l3。"
  ([l0] (front-end l0 (fn [_ _])))
  ([p0 dump]
   (dump "p0" p0)
   (let [l0 (loops/lower-loops p0)
         _ (dump "l0" l0)
         an (front/analyze l0)
         l1 (front/simplify-letrec l0 an)
         _ (dump "sl" l1)
         l1 (front/check-forward l1 an)
         _ (dump "cf" l1)
         l1 (front/cp0-lite l1)
         _ (dump "c1" l1)
         l1 (front/cp0-lite (front/simplify-lets l1 an))
         _ (dump "in" l1)
         _ (dump "l1" l1)
         l2 (front/convert-assignments l1 an)
         _ (dump "l2" l2)
         l3 (closures/uncover-free l2)]
     (dump "l3" l3)
     [l3 an])))

(defn- typed-clauses
  "形式里 ^long / ^double 形参的入口转换（($clj-call 'CallStatic 3 'RT '\"longCast\" …)）的个数：Scheme 版 parse-core 的 typed-count，
  编译驱动据此连同 lower-loops 生成的 label 决定对哪些形式做类型推断。"
  [l0]
  (letfn [(walk [x]
            (cond
              (seq? x) (+ (if (and (= 'clj-call (first x)) (= "CallStatic" (second x)) (>= (count x) 5)
                                   (let [op (nth x 4)] (and (seq? op) (#{"longCast" "doubleCast"} (second op)))))
                            1 0)
                          (reduce + 0 (map walk x)))
              :else 0))]
    (walk (cleat.nanopass/unparse l0))))

(def ^:private eval-counter (atom 0))

(defn- front-ends
  "各形式走前中端：[[L3 含循环?] …]。"
  [l0s dump]
  (loops/reset-fresh-names!)
  (mapv (fn [[ix l0]]
          (let [n0 (loops/loops-made)
                typed (typed-clauses l0)
                [l3 _] (front-end l0 (fn [stage node] (dump ix stage node)))]
            [l3 (or (> (loops/loops-made) n0) (pos? typed))]))
        l0s))

(defn compile-unit
  "一个编译单元：L0 形式 → [Lcil 常量]（Scheme 版 compile-forms->lcil*）。dump：(dump 序号 阶段 节点)。"
  [l0s assembly-name module-name live? dump]
  (let [results (front-ends l0s dump)]
    (codegen/generate-unit (mapv first results) assembly-name module-name (mapv second results) live?)))

(defn shadow-stages
  "影子模式：形式 form（序号 ix）走已实现的阶段并转储。eval 的单元每个形式一个，lower-loops 的临时名从头编号。"
  [form ix]
  (let [nm (str "eval$s" (swap! eval-counter inc))   ; 与 Scheme 侧的 eval$N（可能被引用）区分
        results (front-ends [[ix (eval-unit-l0 form)]] (fn [ix stage node] (dump/dump! "eval" ix stage node)))
        gen (fn [live?] (first (codegen/generate-unit (mapv first results) nm "#Module" (mapv second results) live?)))]
    (when dump/enabled?
      (dump/write! "eval" ix "lcil" (codegen/lcil->string (gen true)))
      ;; 同一个单元以 AOT 模式（常量编码）再生成一次，与 Scheme 编译器的 aot 阶段比较（cleat-089）
      ;; 编不了的常量记为 aot-error，文本同 Scheme 版（消息与对象的类型名）
      (let [aot (try (codegen/lcil->string (gen false))
                     (catch clojure.lang.ExceptionInfo e
                       (if (contains? (ex-data e) :object)
                         (do (dump/write! "eval" ix "aot-error"
                                          (str "cannot encode object " (let [o (:object (ex-data e))] (if (nil? o) "()" (.FullName (class o))))))
                             nil)
                         (throw e))))]
        (when aot (dump/write! "eval" ix "aot" aot))))))

(def ^:private run-counter (atom 0))

(declare eval-form-1)

(defn eval-form
  "用 Clojure 编译器编译并运行一个顶层形式（内存模式：常量对象直接交给发射器），返回它的值。程序集名 ceval$N，
  与 Scheme 编译器的 eval$N 分开。顶层的 do 照 Clojure 的 eval 逐个形式编译运行（后面的形式能看到前面定义的类，如 definterface）。
  cleat-090 的驱动之前用于测试。"
  [form]
  (let [f (loop [f form] (let [g (an/macroexpand-1 f)] (if (identical? g f) f (recur g))))]
    (if (and (seq? f) (= 'do (first f)))
      (reduce (fn [_ x] (eval-form x)) nil (rest f))
      (eval-form-1 f))))

(defn- eval-form-1 [form]
  (let [nm (str "ceval$" (swap! run-counter inc))
        [lcil cs] (compile-unit [[0 (eval-unit-l0 form)]] nm "#Module" true (fn [_ _ _]))
        asm (Cleat.Emit.LcilEmitter/EmitInMemoryData lcil nm (object-array cs))]
    (Cleat.Scheme.ModuleLoader/Run asm)
    (Cleat.Core.Runtime.ClojureOps/TakeHeldResult)))
