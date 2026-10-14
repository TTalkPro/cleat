;; cleat.nanopass：Clojure 写的 nanopass（cleat-083 起，designs/19 D2、07）。
;; 本文件是对外的入口：define-language（cleat-083）；define-pass / nanopass-case / with-output-language 在 cleat-084 加入。
;;
;; (define-language L0
;;   (terminals (symbol (x)) (datum (d)))     ; 终结符：名字 + 元变量，谓词为 名字?（在当前命名空间决议）
;;   (entry Expr)
;;   (Expr (e body)
;;     x
;;     (quote d)
;;     (if e0 e1 e2)
;;     (begin e* ... e))
;;   …)
;; (define-language L1 (extends L0) (Expr (e body) (- (if e0 e1 e2)) (+ (when e0 e1))))
;;
;; 展开为：每个产生式一个 deftype（L0-Expr-if，字段 e0 e1 e2，实现 cleat.nanopass.records.INode 与语言的标记接口 L0$Node）、
;; 带检查的构造函数 make-L0:Expr:if、谓词 L0:Expr:if? 与 L0:Expr?、语言的运行时对象 L0（cleat.nanopass.records.Language）、unparse-L0。
(ns cleat.nanopass
  (:require [cleat.nanopass.records :as r]
            [cleat.nanopass.language :as lang]))

(def ^:dynamic *check-constructors*
  "为真时（缺省）define-language 生成的构造函数检查字段（宏展开期读取）。"
  true)

;; —— 名字 ——

(defn- qualified [v]
  (let [m (meta v)] (symbol (str (ns-name (:ns m))) (str (:name m)))))

(defn- resolve-language-name
  "符号 → 语言的限定名（按当前命名空间决议语言的 Var）。"
  [sym]
  (let [v (resolve sym)]
    (if (var? v)
      (qualified v)
      (throw (ex-info (str "nanopass: unknown language " sym) {:language sym})))))

(defn- resolve-pred [sym]
  (let [v (resolve sym)]
    (if (var? v)
      (qualified v)
      (throw (ex-info (str "nanopass: terminal predicate " sym " is not defined") {:predicate sym})))))

(defn- short-name [desc] (name (:name desc)))
(defn- marker-name [desc] (symbol (str (short-name desc) "$Node")))
(defn nonterminal-pred-name [desc nt] (symbol (str (short-name desc) ":" nt "?")))
(defn production-pred-name [desc nt kw] (symbol (str (short-name desc) ":" nt ":" kw "?")))
(defn constructor-name [desc nt kw] (symbol (str "make-" (short-name desc) ":" nt ":" kw)))

;; —— 运行时对象 ——

(defn make-language
  "语言的运行时对象并登记（define-language 的展开结果在加载时调用）。preds：终结符名 → 谓词；ctors：标签 → 构造函数。"
  [desc preds ctors]
  (r/register-runtime!
    (r/->Language (:name desc) desc preds ctors
                  (into {} (for [nt (:nonterminals desc) p (:prods nt) :when (:tag p)] [(:tag p) p]))
                  (into {} (map (juxt :name identity) (:nonterminals desc))))))

;; —— define-language ——
;; 展开结果不含 gensym（同一源码两次展开相同，自举不动点需要，07 D6）：生成函数的形参用固定名字 nanopass-x / nanopass-elem。

(defn- field-test
  "字段值 v 的检查表达式（单个元素）。"
  [desc field v]
  (let [ok (if (= :term (:sort field))
             (:pred (first (filter #(= (:of field) (:name %)) (:terminals desc))))
             (nonterminal-pred-name desc (:of field)))]
    (if (:maybe field)
      `(or (r/absent? ~v) (~ok ~v))
      `(~ok ~v))))

(defn- field-check [desc nt prod field]
  (let [v (:name field)
        y 'nanopass-elem
        fail `(r/bad-field '~(:name desc) '~(symbol (str nt ":" (:keyword prod))) '~v ~v)]
    (if (zero? (:depth field))
      `(when-not ~(field-test desc field v) ~fail)
      `(when-not (r/check-seq (fn [~y] ~(field-test desc field y)) ~v ~(:depth field)) ~fail))))

(defn- emit-language [desc]
  (let [qname (:name desc)
        marker (marker-name desc)
        prods (for [nt (:nonterminals desc) p (:prods nt) :when (= :pair (:kind p))] [nt p])
        node 'cleat.nanopass.records.INode]
    `(do
       (lang/register-description! '~desc)
       (definterface ~marker)
       ~@(for [[_ p] prods]
           (let [fs (mapv :name (:fields p))]
             `(deftype ~(:type p) ~fs
                ~node
                (~'nanoTag [~'_] ~(:tag p))
                (~'nanoFields [~'_] ~fs)
                (~'nanoLang [~'_] '~qname)
                ~marker)))
       (declare ~@(map #(nonterminal-pred-name desc (:name %)) (:nonterminals desc)))
       ~@(for [nt (:nonterminals desc)]
           (let [{:keys [tags terms]} (lang/nonterminal-tags desc (:name nt))
                 x 'nanopass-x]
             `(defn ~(nonterminal-pred-name desc (:name nt)) [~x]
                (if (instance? ~marker ~x)
                  ~(if (seq tags) `(case (.nanoTag ~(with-meta x {:tag node})) ~(apply list tags) true false) false)
                  (or ~@(for [t terms] `(~(:pred (first (filter #(= t (:name %)) (:terminals desc)))) ~x)) false)))))
       ~@(for [[nt p] prods]
           (let [fs (mapv :name (:fields p))]
             `(defn ~(constructor-name desc (:name nt) (:keyword p)) ~fs
                ~@(when *check-constructors* (map #(field-check desc (:name nt) p %) (:fields p)))
                (new ~(:type p) ~@fs))))
       ~@(for [[nt p] prods]
           (let [x 'nanopass-x]
             `(defn ~(production-pred-name desc (:name nt) (:keyword p)) [~x] (instance? ~(:type p) ~x))))
       (def ~(symbol (short-name desc))
         (make-language (lang/find-description '~qname)
                        ~(into {} (map (fn [t] [(list 'quote (:name t)) (:pred t)]) (:terminals desc)))
                        ~(into {} (for [[nt p] prods] [(:tag p) (constructor-name desc (:name nt) (:keyword p))]))))
       (defn ~(symbol (str "unparse-" (short-name desc))) [~'nanopass-x] (lang/unparse-node ~'nanopass-x))
       (var ~(symbol (short-name desc))))))

(defmacro define-language
  "定义语言（见文件头）。子句：(terminals …)、(entry 非终结符)、(extends 基语言)、(非终结符 (元变量 …) 产生式 …)；
  extends 时终结符与非终结符子句里用 (+ …) / (- …) 增删。"
  [lname & clauses]
  (let [qname (symbol (str (ns-name *ns*)) (name lname))
        base-sym (some #(when (and (seq? %) (= 'extends (first %))) (second %)) clauses)
        base (when base-sym (lang/find-description (resolve-language-name base-sym)))
        desc (lang/annotate (lang/clauses->desc qname clauses base resolve-pred))]
    (lang/register-description! desc)
    (emit-language desc)))

;; —— 语言的访问 ——

(defn language
  "语言的运行时对象：参数为 Language、限定名，或语言的 Var。"
  [x]
  (r/runtime-language (if (var? x) (qualified x) x)))

(defn unparse
  "节点 → S 表达式（列表；终结符原样）。"
  [x]
  (lang/unparse-node x))

(defn parse
  "S 表达式 → 语言 l 的非终结符 nt（缺省为入口）的节点；不匹配时报错。"
  ([l x] (parse l (:entry (:desc (language l))) x))
  ([l nt x]
   (let [lg (language l)
         v (lang/parse-nt lg nt x)]
     (if (identical? v ::lang/fail)
       (throw (ex-info (str "nanopass: invalid " (:name lg) " " nt ": " (pr-str x)) {:language (:name lg) :nonterminal nt :form x}))
       v))))

(defn language->s-expression
  "语言 → 等价的 define-language 形式（展开 extends 之后的全量形式）。"
  [l]
  (lang/desc->s-expression (:desc (language l))))

(defmethod print-method cleat.nanopass.records.INode [n ^System.IO.TextWriter w]
  (.Write w (str "#<" (name (.nanoLang ^cleat.nanopass.records.INode n)) " "))
  (print-method (unparse n) w)
  (.Write w ">"))
