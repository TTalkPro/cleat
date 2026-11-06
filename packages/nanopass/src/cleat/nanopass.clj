;; cleat.nanopass：Clojure 写的 nanopass（cleat-083 起，designs/19 D2、07）。
;; 本文件是对外的入口：define-language、define-parser、unparse（cleat-083）；define-pass、trace-define-pass、echo-define-pass、
;; nanopass-case、with-output-language、out、in-context、pass-input-parser、pass-output-unparser（cleat-084 / 095，展开在 cleat.nanopass.pass）。
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
            [cleat.nanopass.language :as lang]
            [cleat.nanopass.pass :as pass]))

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
(defn production-pred-name [desc nt kw] (symbol (str (short-name desc) ":" nt ":" kw "?")))   ; kw：产生式的 :name（关键字或 appN）
(defn constructor-name [desc nt kw] (symbol (str "make-" (short-name desc) ":" nt ":" kw)))

;; —— 运行时对象 ——

(defn make-language
  "语言的运行时对象并登记（define-language 的展开结果在加载时调用）。preds：终结符名 → 谓词；ctors：标签 → 构造函数；
  pretties：终结符名 → => 的美化函数。"
  ([desc preds ctors] (make-language desc preds ctors {}))
  ([desc preds ctors pretties]
   (r/register-runtime!
     (r/->Language (:name desc) desc preds ctors
                   (into {} (for [nt (:nonterminals desc) p (:prods nt) :when (:tag p)] [(:tag p) p]))
                   (into {} (map (juxt :name identity) (:nonterminals desc)))
                   pretties))))

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
        fail `(r/bad-field '~(:name desc) '~(symbol (str nt ":" (:name prod))) '~v ~v)]
    (cond
      (zero? (:depth field)) `(when-not ~(field-test desc field v) ~fail)
      ;; 一层、非 maybe：谓词直接交给 check-all
      (and (= 1 (:depth field)) (not (:maybe field))) `(when-not (r/check-all ~(first (field-test desc field y)) ~v) ~fail)
      :else `(when-not (r/check-seq (fn [~y] ~(field-test desc field y)) ~v ~(:depth field)) ~fail))))

(defn- emit-language [desc]
  (let [qname (:name desc)
        marker (marker-name desc)
        prods (for [nt (:nonterminals desc) p (:prods nt) :when (= :pair (:kind p))] [nt p])
        node 'cleat.nanopass.records.INode]
    `(do
       (lang/register-description! '~desc)
       (definterface ~marker)
       ~@(for [[_ p] prods]
          (let [fs (mapv :name (:fields p))
                mfield (with-meta 'meta {:unsynchronized-mutable true})]
            `(deftype ~(:type p) [~@fs ~mfield]
               ~node
               (~'nanoTag [~'_] ~(:tag p))
               (~'nanoFields [~'_] [~@fs])
               (~'nanoLang [~'_] '~qname)
               clojure.lang.IMeta
               (~'meta [~'_] ~'meta)
               clojure.lang.IObj
               (~'withMeta [~'this m#] (set! ~'meta m#) ~'this)
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
             `(defn ~(constructor-name desc (:name nt) (:name p)) ~fs
                ~@(when *check-constructors* (map #(field-check desc (:name nt) p %) (:fields p)))
               (r/propagate-src-meta! (new ~(:type p) ~@fs nil)))))
       ~@(for [[nt p] prods]
           (let [x 'nanopass-x]
             `(defn ~(production-pred-name desc (:name nt) (:name p)) [~x] (instance? ~(:type p) ~x))))
       (def ~(symbol (short-name desc))
         (make-language (lang/find-description '~qname)
                        ~(into {} (map (fn [t] [(list 'quote (:name t)) (:pred t)]) (:terminals desc)))
                        ~(into {} (for [[nt p] prods] [(:tag p) (constructor-name desc (:name nt) (:name p))]))
                        ~(into {} (for [t (:terminals desc) :when (:pretty t)] [(list 'quote (:name t)) (:pretty t)]))))
       (defn ~(symbol (str "unparse-" (short-name desc)))
         ([~'nanopass-x] (lang/unparse-node ~'nanopass-x false))
         ([~'nanopass-x ~'nanopass-raw] (lang/unparse-node ~'nanopass-x ~'nanopass-raw)))
       (var ~(symbol (short-name desc))))))

(defmacro define-language
  "定义语言（见文件头）。子句：(terminals …)、(entry 非终结符)、(extends 基语言)、(非终结符 (元变量 …) 产生式 …)；
  extends 时终结符与非终结符子句里用 (+ …) / (- …) 增删；产生式或终结符后可跟 => 美化（unparse 用）。"
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
  "节点 → S 表达式（列表；终结符原样）。raw? 为真时不按 => 美化。"
  ([x] (lang/unparse-node x false))
  ([x raw?] (lang/unparse-node x raw?)))

(defn parse
  "S 表达式 → 语言 l 的非终结符 nt（缺省为入口）的节点；不匹配时报错。"
  ([l x] (parse l (:entry (:desc (language l))) x))
  ([l nt x]
   (let [lg (language l)
         v (lang/parse-nt lg nt x)]
     (if (identical? v ::lang/fail)
       (throw (ex-info (str "nanopass: invalid " (:name lg) " " nt ": " (pr-str x)) {:language (:name lg) :nonterminal nt :form x}))
       v))))

(defmacro define-parser
  "(define-parser parse-L L)：定义 S 表达式 → L 的节点的函数（上游 define-parser；(parse-L x) 按入口，(parse-L nt x) 指定非终结符）。"
  [pname lname]
  `(defn ~pname
     ([x#] (parse (var ~lname) x#))
     ([nt# x#] (parse (var ~lname) nt# x#))))

(defn language->s-expression
  "语言 → 等价的 define-language 形式（展开 extends 之后的全量形式）。"
  [l]
  (lang/desc->s-expression (:desc (language l))))

(defmethod print-method cleat.nanopass.records.INode [n ^System.IO.TextWriter w]
  (.Write w (str "#<" (name (.nanoLang ^cleat.nanopass.records.INode n)) " "))
  (print-method (unparse n) w)
  (.Write w ">"))

;; —— pass（cleat-084）——
;;
;; (define-pass simplify-letrec L0 [ir an] -> L1 []
;;   (definitions (def assigned (assigned-of an)) (defn assigned? [x] (contains? assigned x)))
;;   (Expr [ir] -> Expr []
;;     [(call ~e ~[e*] ...) (out (call ~(Expr e) ~e* ...))]
;;     [(set! ~x ~[e]) (guard (assigned? x)) (out (set-box! ~x ~e))])
;;   (LambdaOf LambdaExpr [ir] -> LambdaExpr [])
;;   (Expr ir))
;;
;; 输入 / 输出：语言、[语言 非终结符]，输出还可以是 *（处理器返回任意值）。处理器 (名字 输入非终结符 [ir 额外形参 …] -> 输出非终结符 [] 子句 …)，
;; 名字与输入非终结符相同时可省略后者；输出非终结符为 * 时返回任意值。处理器在 pass 里互相可见（letfn），也可以在体里直接调用。
;; 没有声明、却被 catamorphism 或复制子句用到的处理器自动生成（输入输出同名，形参只有 ir）；处理器缺的产生式按输出语言同样模式的
;; 产生式自动复制（字段经对应的处理器），经非终结符交替到达的产生式交给该非终结符的处理器。
;; definitions 段：(def x e) 与 (defn f [a] …)；def 的值先于所有函数求值（只能引用前面的 def 与 pass 的形参），函数与处理器互相可见。
;; pass 的体缺省为调用入口非终结符的处理器。

(defmacro out
  "模板（只在 define-pass、with-output-language 的输出上下文里有意义）。"
  [& _]
  (throw (ex-info "nanopass: out used outside an output context (define-pass with an output language, or with-output-language)" {})))

(defmacro in-context
  "在模板上下文里把当前非终结符换成 nt。"
  [nt & _]
  (throw (ex-info "nanopass: in-context used outside an output context" {})))

(defmacro define-pass
  "(define-pass name Lin [ir 形参 …] -> Lout [返回值 …] (definitions …)? 处理器 … 体?)：见文件头的说明与 nanopass/README.md。"
  [pname in-lang formals arrow out-lang rets & body]
  (when-not (and (symbol? pname) (vector? formals) (seq formals) (= '-> arrow) (vector? rets))
    (throw (ex-info (str "nanopass: (define-pass name Lin [ir …] -> Lout [] …) expected, got " (pr-str (list* 'define-pass pname in-lang formals arrow out-lang rets))) {})))
  (pass/expand-define-pass pname in-lang formals out-lang rets body nil))

(defmacro trace-define-pass
  "同 define-pass，运行时把输入与输出（unparse 过）打印到 *out*（上游 trace-define-pass）。"
  [pname in-lang formals arrow out-lang rets & body]
  (pass/expand-define-pass pname in-lang formals out-lang rets body :trace))

(defmacro echo-define-pass
  "同 define-pass，展开期把展开结果打印到 *out*（上游 echo-define-pass）。"
  [pname in-lang formals arrow out-lang rets & body]
  (let [form (pass/expand-define-pass pname in-lang formals out-lang rets body nil)]
    (binding [*print-meta* false] (prn form))
    form))

(defmacro pass-input-parser
  "(pass-input-parser p)：pass 输入语言的解析函数（S 表达式 → 节点）；输入为 * 时是 identity。"
  [pname]
  (let [m (meta (resolve pname))
        in (:nanopass/input m)]
    (when-not m (throw (ex-info (str "nanopass: " pname " is not a pass") {})))
    (if in `(fn [x#] (parse (var ~(:lang in)) '~(:nt in) x#)) `identity)))

(defmacro pass-output-unparser
  "(pass-output-unparser p)：pass 输出语言的 unparse 函数；输出为 * 时是 identity。"
  [pname]
  (let [m (meta (resolve pname))]
    (when-not m (throw (ex-info (str "nanopass: " pname " is not a pass") {})))
    (if (:nanopass/output m) `(fn [x# & [raw#]] (unparse x# (boolean raw#))) `identity)))

(defmacro nanopass-case
  "(nanopass-case [L NT] e [模式 body …] … [else …])：按 L 的 NT 分派 e；~[x] 对同一 nanopass-case 递归。展开期生成内联代码，不建闭包。"
  [spec e & clauses]
  (pass/expand-nanopass-case spec e clauses))

(defmacro with-output-language
  "(with-output-language [L NT] body …)：body 里的 (out …) 构造 L 的 NT。"
  [spec & body]
  (pass/expand-with-output-language spec body))
