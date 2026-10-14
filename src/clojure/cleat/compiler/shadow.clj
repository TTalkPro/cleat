;; 影子模式（cleat-085，designs/19 D5）：CLEAT_COMPILER=shadow 且 CLEAT_DUMP_IR 时，Scheme 驱动对每个顶层形式（在 Scheme 展开与求值之前）
;; 调用 shadow-form；Clojure 编译器处理同一个形式并转储各阶段（p0 … l3、lcil、aot），由 tools/compiler-diff.clj 与 Scheme 编译器的转储比较。
;; 过渡期的工具，不进编译器包（cleat-092：它依赖 Cleat.Scheme 的 IrDump），从源码加载；cleat-093 随 Scheme 一起删除。
(ns cleat.compiler.shadow
  (:require [cleat.compiler.dump :as dump]
            [cleat.compiler.pipeline :as pipeline]
            [cleat.compiler.codegen :as codegen]))

(defn- shadow-stages
  "形式 form（序号 ix）走各阶段并转储。eval 的单元每个形式一个，lower-loops 的临时名从头编号。"
  [form ix]
  (let [nm (str "eval$s" (swap! pipeline/eval-counter inc))   ; 与 Scheme 侧的 eval$N（可能被引用）区分
        results (pipeline/front-ends [[ix (pipeline/eval-unit-l0 form)]] (fn [ix stage node] (dump/dump! "eval" ix stage node)))
        gen (fn [live?] (first (codegen/generate-unit (mapv first results) nm "#Module" (mapv second results) live?)))]
    (when dump/enabled?
      (dump/write! "eval" ix "lcil" (codegen/lcil->string (gen true)))
      ;; 同一个单元以 AOT 模式（常量编码）再生成一次（aot 阶段）；编不了的常量记为 aot-error（消息与对象的类型名）
      (let [aot (try (codegen/lcil->string (gen false))
                     (catch clojure.lang.ExceptionInfo e
                       (if (contains? (ex-data e) :object)
                         (do (dump/write! "eval" ix "aot-error"
                                          (str "cannot encode object " (let [o (:object (ex-data e))] (if (nil? o) "()" (.FullName (class o))))))
                             nil)
                         (throw e))))]
        (when aot (dump/write! "eval" ix "aot" aot))))))

(defn shadow-form
  "形式 form（序号 ix）：记下源形式（只在 Clojure 一侧，差分报告用来定位），再走各阶段。"
  [form ix]
  (dump/write! "eval" ix "src" (Cleat.Scheme.Clojure.IrDump/PrintDatum (binding [*print-meta* false *print-length* 40 *print-level* 12] (pr-str form))))
  (shadow-stages form ix))
