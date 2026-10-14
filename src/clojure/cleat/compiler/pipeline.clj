;; Clojure 编译器的流水线（cleat-086 起）：前端 → 前中端 pass → codegen。现在接到前端（L0）；影子模式（cleat-085）经 shadow-stages
;; 对每个形式转储已实现的阶段，tools/compiler-diff.clj 与 Scheme 编译器的转储比较（designs/19 D5 的 G1）。
(ns cleat.compiler.pipeline
  (:require [cleat.compiler.analyzer :as an]
            [cleat.compiler.dump :as dump]
            [cleat.nanopass :refer [with-output-language out]]
            [cleat.compiler.languages :refer [L0]]))

(defn eval-unit-l0
  "eval 的单元：驱动把顶层形式包成 ($clj-call 'HoldResult #f 核心)（值经线程槽交出，cleat-055）。"
  [form]
  (with-output-language [L0 Expr]
    (out (clj-call "HoldResult" false ~(an/analyze-top form)))))

(defn shadow-stages
  "影子模式：形式 form（序号 ix）走已实现的阶段并转储。"
  [form ix]
  (dump/dump! "eval" ix "p0" (eval-unit-l0 form)))
