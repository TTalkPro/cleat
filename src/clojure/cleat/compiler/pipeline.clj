;; Clojure 编译器的流水线（cleat-086 起）：前端 → 前中端 pass → codegen。现在接到前端（L0）；影子模式（cleat-085）经 shadow-stages
;; 对每个形式转储已实现的阶段，tools/compiler-diff.clj 与 Scheme 编译器的转储比较（designs/19 D5 的 G1）。
(ns cleat.compiler.pipeline
  (:require [cleat.compiler.analyzer :as an]
            [cleat.compiler.dump :as dump]
            [cleat.compiler.loops :as loops]
            [cleat.compiler.front :as front]
            [cleat.compiler.closures :as closures]
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

(defn shadow-stages
  "影子模式：形式 form（序号 ix）走已实现的阶段并转储。eval 的单元每个形式一个，lower-loops 的临时名从头编号。"
  [form ix]
  (loops/reset-fresh-names!)
  (front-end (eval-unit-l0 form) (fn [stage node] (dump/dump! "eval" ix stage node))))
