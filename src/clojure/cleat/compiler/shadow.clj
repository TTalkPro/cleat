;; 影子模式（cleat-085，designs/19 D5）：CLEAT_COMPILER=shadow 且 CLEAT_DUMP_IR 时，Scheme 驱动对每个顶层形式（在 Scheme 展开与求值之前）
;; 调用 shadow-form；Clojure 编译器按已经实现的阶段处理同一个形式并转储，由 tools/compiler-diff.clj 与 Scheme 编译器的转储比较。
;; 已实现的阶段见 cleat.compiler.pipeline/shadow-stages（cleat-086：前端 → p0）。过渡期的工具，阶段 E 删除。
(ns cleat.compiler.shadow
  (:require [cleat.compiler.dump :as dump]
            [cleat.compiler.pipeline :as pipeline]))

(defn shadow-form
  "形式 form（序号 ix）：记下源形式（只在 Clojure 一侧，差分报告用来定位），再走已实现的阶段。"
  [form ix]
  (dump/write! "eval" ix "src" (Cleat.Scheme.Clojure.IrDump/PrintDatum (binding [*print-meta* false *print-length* 40 *print-level* 12] (pr-str form))))
  (pipeline/shadow-stages form ix))
