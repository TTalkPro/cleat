(ns cleat.compiler.optflags
  (:require [clojure.string :as str]))

;; CLEAT_NO_OPT 的唯一读取点（cleat-164）。"1" 关掉全部；否则按逗号 / 空白分词后精确匹配记号。
;; 旧实现是子串匹配（str/includes? / .Contains），CLEAT_NO_OPT=accinline 会顺带关掉 inline；
;; 记号表与每项的用途见 designs/README 的开关附表。这里只在命名空间加载时读一次环境变量，
;; 与原先各 ns 顶层 def 的求值时机相同。

(def ^:private all? (= "1" (System.Environment/GetEnvironmentVariable "CLEAT_NO_OPT")))

(def ^:private tokens
  (let [v (System.Environment/GetEnvironmentVariable "CLEAT_NO_OPT")]
    (if (and (string? v) (not= v "1"))
      (set (remove str/blank? (.Split ^String v (char-array [\, \space \tab \newline \return]))))
      #{})))

(defn opt-off? [nm] (or all? (contains? tokens nm)))
