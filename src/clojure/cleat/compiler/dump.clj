;; 逐形式转储（cleat-085，designs/19 D5）：Clojure 编译器的 IR 按语言描述打印成与 Scheme 编译器相同的规范文本
;; （Cleat.Scheme.Clojure.IrDump：symbol 终结符是裸标识符，datum / name / flag 是 PrintDatum），写到 CLEAT_DUMP_IR/clojure/<单元>.ir。
;; 过渡期的工具：阶段 E（cleat-093）随 Scheme 一起删除。
(ns cleat.compiler.dump
  (:require [clojure.string]
            [cleat.nanopass.records :as r]
            [cleat.compiler.languages :as l]))

(def enabled? (Cleat.Scheme.Clojure.IrDump/Enabled))

(defn- datum [x] (Cleat.Scheme.Clojure.IrDump/PrintDatum x))
(defn- ident [x] (Cleat.Scheme.Clojure.IrDump/IdentifierText (str x)))

(defn- name-text
  "过程名（name 终结符）：与 Scheme 版的值打印相同——无名为 false，符号是 Scheme 符号，CljFnName 是 clj-fn-name 记录。"
  [x]
  (cond
    (or (nil? x) (false? x)) "false"
    (symbol? x) (str "#cleat/ssym " (datum (name x)))
    (l/clj-fn-name? x) (str "#cleat/record [\"clj-fn-name\" " (datum (:class x)) " " (datum (or (:variadic x) false)) " "
                            (datum (:prims x)) " " (datum (boolean (:async? x))) "]")
    :else (datum x)))

(declare ir-text elt-text)

(defn- value-text [field v]
  (cond
    (:quoted field) (Cleat.Scheme.Clojure.IrDump/PrintQuotedDatum v)
    (and (:maybe field) (r/absent? v)) "false"
    (= :nt (:sort field)) (ir-text v)
    (= 'symbol (:of field)) (if (symbol? v) (ident v) (datum v))
    (= 'name (:of field)) (name-text v)
    :else (datum v)))

(defn- elts-text [pats env fields]
  (loop [ps (seq pats) out []]
    (if-not ps
      out
      (let [p (first ps)]
        (if (and (next ps) (= '... (second ps)))
          (let [names (filter symbol? (tree-seq seq? seq (if (seq? p) p (list p))))
                names (remove #{'... 'maybe} names)
                n (count (env (first names)))]
            (recur (nnext ps) (into out (for [i (range n)] (elt-text p (into env (map (fn [f] [f (nth (env f) i)]) names)) fields)))))
          (recur (next ps) (conj out (elt-text p env fields))))))))

(defn- elt-text [p env fields]
  (cond
    (symbol? p) (value-text (fields p) (env p))
    (and (seq? p) (= 'maybe (first p))) (value-text (fields (second p)) (env (second p)))
    :else (str "(" (clojure.string/join " " (elts-text p env fields)) ")")))

(defn ir-text
  "节点 → 规范文本。不是节点的值：符号为标识符（非终结符位置的变量），其余为数据。"
  [x]
  (cond
    (r/node? x)
    (let [^cleat.nanopass.records.INode n x
          lang (r/node-language n)
          prod ((:by-tag lang) (.nanoTag n))
          env (zipmap (map :name (:fields prod)) (.nanoFields n))
          fields (into {} (map (juxt :name identity) (:fields prod)))
          ;; quote 的数据是常量：long 以外的整数带类型名（IrDump.PrintQuotedDatum）
          fields (if (= :quote (:keyword prod)) (update-vals fields #(assoc % :quoted true)) fields)]
      (str "(" (clojure.string/join " " (cons (ident (:keyword prod)) (elts-text (rest (:pattern prod)) env fields))) ")"))
    (symbol? x) (ident x)
    :else (datum x)))

(defn write!
  "写一条记录（单元、序号、阶段、文本）。"
  [unit ix stage text]
  (Cleat.Scheme.Clojure.IrDump/Write "clojure" unit (int ix) stage text))

(defn dump!
  "节点的规范文本写成一条记录。"
  [unit ix stage node]
  (when enabled? (write! unit ix stage (ir-text node))))
