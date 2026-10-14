;; cleat.nanopass 的语言描述（cleat-083，designs/19 D2、07 D3）：define-language 的子句 → 纯数据的语言描述，extends 的 + / - 编辑，
;; 按描述数据驱动的 S 表达式解析与 unparse（上游 parser.ss / unparser.ss 生成代码的部分在这里是普通函数，07 D1）。
;;
;; 语言描述：
;;   {:name 限定名 :entry 非终结符
;;    :terminals    [{:name symbol :pred clojure.core/symbol? :metas [x s pr]} …]
;;    :nonterminals [{:name Expr :metas [e body] :prods [产生式 …]} …]}
;; 产生式：
;;   {:kind :terminal    :meta x  :term symbol}            终结符交替
;;   {:kind :nonterminal :meta le :nt LambdaExpr}          非终结符交替
;;   {:kind :pair :keyword if :name if :pattern (if e0 e1 e2) :tag 3 :type L0-Expr-if :pretty nil
;;    :fields [{:name e0 :raw e :sort :nt :of Expr :depth 0 :maybe false} …]}
;;   不以关键字开头的产生式（上游允许，cleat-095 起支持）：(e0 e1 ...) 的 :keyword 为 nil，:name 为 app0、app1 …（按定义顺序），
;;   按形状匹配（元素个数；有省略号时不少于固定元素数）。
;; 模式里 [ ] 与 ( ) 等价（描述里一律是列表）；元变量 = 终结符或非终结符声明的元变量名加后缀（数字、* 、^）。
;; 产生式或终结符后面可以跟 => 美化（cleat-095，上游的 unparse 钩子）：产生式 => 模板（同样的元变量，unparse 时按它输出），
;; 终结符 => 函数（unparse 时作用于终结符的值）；(unparse x true) 不美化。
;; 终结符描述：{:name symbol :pred clojure.core/symbol? :metas [x] :pretty nil|限定符号}
(ns cleat.nanopass.language
  (:require [cleat.nanopass.records :as r]))

;; —— 宏展开期的描述注册表：限定名 → 描述（define-language 展开时与加载时都登记，07 D5）——

(defonce ^:private descriptions (atom {}))

(defn register-description! [desc]
  (swap! descriptions assoc (:name desc) desc)
  desc)

(defn find-description [qname]
  (or (get @descriptions qname)
      (throw (ex-info (str "nanopass: unknown language " qname) {:language qname}))))

;; —— 元变量 ——

(defn raw-meta
  "去掉元变量的后缀（数字、*、^）：e0 → e，x* → x，body1 → body。"
  [sym]
  (let [s (name sym)
        i (loop [i (count s)]
            (if (and (pos? i) (<= 0 (.IndexOf "0123456789*^" (nth s (dec i)))))
              (recur (dec i))
              i))]
    (when (zero? i) (throw (ex-info (str "nanopass: invalid meta-variable " sym) {:meta sym})))
    (symbol (subs s 0 i))))

(defn- normalize
  "模式：向量改为列表（[ ] 与 ( ) 等价）；保留元数据（::pretty）。"
  [p]
  (cond
    (vector? p) (with-meta (apply list (map normalize p)) (meta p))
    (seq? p) (with-meta (apply list (map normalize p)) (meta p))
    :else p))

(defn- ellipsis? [x] (= x '...))

(defn- metas-of [x]
  (let [m (if (or (vector? x) (seq? x)) (vec x)
              (throw (ex-info (str "nanopass: expected a list of meta-variables, got " (pr-str x)) {})))]
    (doseq [s m] (when-not (symbol? s) (throw (ex-info (str "nanopass: invalid meta-variable " (pr-str s)) {}))))
    m))

;; —— define-language 子句 → 未注解的描述 ——

(defn- parse-terminal [resolve-pred t]
  (if (and (seq? t) (= 2 (count t)) (symbol? (first t)))
    {:name (first t) :pred (resolve-pred (symbol (str (name (first t)) "?"))) :metas (metas-of (second t))
     :pretty (when-let [pf (::pretty (meta t))] (resolve-pred pf))}
    (throw (ex-info (str "nanopass: invalid terminal clause " (pr-str t)) {}))))

(defn- attach-pretty
  "项的序列里 x => y 合并为带 ::pretty 元数据 y 的 x（上游的 unparse 钩子）。"
  [items]
  (loop [xs (seq items) out []]
    (cond
      (nil? xs) out
      (and (next xs) (= '=> (second xs)))
      (do (when-not (nnext xs) (throw (ex-info "nanopass: => needs a template or a function after it" {})))
          (recur (nnext (next xs)) (conj out (let [x (first xs)]
                                               (if (instance? clojure.lang.IObj x)
                                                 (vary-meta x assoc ::pretty (nth xs 2))
                                                 (throw (ex-info (str "nanopass: => cannot follow " (pr-str x)) {})))))))
      (= '=> (first xs)) (throw (ex-info "nanopass: misplaced =>" {}))
      :else (recur (next xs) (conj out (first xs))))))

(defn- edit-clause?
  "(+ …) / (- …)"
  [x op]
  (and (seq? x) (= (first x) op)))

(defn- clause-head [c] (when (seq? c) (first c)))

(defn- check-nonterminal-clause [c]
  (when-not (and (seq? c) (symbol? (first c)) (next c) (or (vector? (second c)) (seq? (second c))))
    (throw (ex-info (str "nanopass: invalid nonterminal clause " (pr-str c)) {}))))

(defn- prod-key [p] (normalize p))

(defn- apply-edits
  "base：已有的项；edits：子句体（(+ …) / (- …)，也可以直接写项，等同 +；项后可跟 => 美化）；key：比较用的键。"
  [base edits key what]
  (reduce
    (fn [items e]
      (cond
        (edit-clause? e '-)
        (reduce (fn [items x]
                  (let [k (key x)]
                    (when-not (some #(= k (key %)) items)
                      (throw (ex-info (str "nanopass: cannot remove " what " " (pr-str x) ": not in the base language") {})))
                    (vec (remove #(= k (key %)) items))))
                items (attach-pretty (rest e)))
        (edit-clause? e '+) (into items (attach-pretty (rest e)))
        :else (conj items e)))
    (vec base) (attach-pretty edits)))

(defn- term-key [t] (:name t))

(defn clauses->desc
  "define-language 的子句 → 未注解的描述（:prods 里是模式）。base 为 extends 的基语言描述（或 nil）；
  resolve-pred：谓词符号 → 限定符号（宏展开期按当前命名空间决议）。"
  [qname clauses base resolve-pred]
  (let [by-head (group-by #(let [h (clause-head %)] (if (#{'terminals 'entry 'extends 'nongenerative-id} h) h :nt)) clauses)
        terms (if base
                (let [edits (mapcat rest (get by-head 'terminals))
                      ;; 基语言的终结符保持原样；新增的按当前命名空间决议谓词
                      items (apply-edits (map (fn [t] (with-meta (list (:name t) (apply list (:metas t))) {::desc t})) (:terminals base))
                                         edits (fn [t] (first t)) "terminal")]
                  (mapv #(or (::desc (meta %)) (parse-terminal resolve-pred %)) items))
                (mapv #(parse-terminal resolve-pred %) (attach-pretty (mapcat rest (get by-head 'terminals)))))
        entry (some-> (get by-head 'entry) last second)
        base-nts (when base (mapv (fn [nt] {:name (:name nt) :metas (:metas nt)
                                            :prods (mapv #(if (= :pair (:kind %))
                                                            (if (:pretty %) (vary-meta (:pattern %) assoc ::pretty (:pretty %)) (:pattern %))
                                                            (:meta %))
                                                         (:prods nt))})
                                  (:nonterminals base)))
        nts (reduce
              (fn [nts c]
                (check-nonterminal-clause c)
                (let [[nt-name metas & body] c
                      i (first (keep-indexed (fn [i nt] (when (= (:name nt) nt-name) i)) nts))]
                  (if i
                    (let [nt (nts i)
                          prods (apply-edits (:prods nt) body prod-key "production")]
                      (if (empty? prods)
                        (vec (concat (subvec nts 0 i) (subvec nts (inc i))))
                        (assoc nts i (assoc nt :metas (vec (distinct (concat (:metas nt) (metas-of metas)))) :prods (mapv normalize prods)))))
                    (do (when (some #(edit-clause? % '-) body)
                          (throw (ex-info (str "nanopass: nonterminal " nt-name " is not in the base language") {})))
                        (conj nts {:name nt-name :metas (metas-of metas)
                                   :prods (mapv normalize (attach-pretty (mapcat #(if (edit-clause? % '+) (attach-pretty (rest %)) [%]) body)))})))))
              (or base-nts []) (get by-head :nt))]
    {:name qname
     :entry (or entry (:entry base) (:name (first nts)))
     :terminals terms
     :nonterminals nts}))

;; —— 注解：元变量表、字段、标签、类型名 ——

(defn- meta-table [desc]
  (reduce (fn [m [sort item]]
            (reduce (fn [m mv]
                      (when (contains? m mv)
                        (throw (ex-info (str "nanopass: meta-variable " mv " declared twice in " (:name desc)) {})))
                      (assoc m mv [sort (:name item)]))
                    m (:metas item)))
          {} (concat (map #(vector :term %) (:terminals desc)) (map #(vector :nt %) (:nonterminals desc)))))

(defn- pattern-fields
  "模式（关键字之后的部分）里的元变量，按出现顺序：{:name :raw :sort :of :depth :maybe}。"
  [metas elts depth]
  (loop [xs (seq elts) out []]
    (if-not xs
      out
      (let [x (first xs)
            rep? (and (next xs) (ellipsis? (second xs)))
            d (if rep? (inc depth) depth)
            fs (cond
                 (ellipsis? x) (throw (ex-info "nanopass: misplaced ..." {}))
                 (symbol? x) (let [raw (raw-meta x)
                                   [sort of] (or (metas raw) (throw (ex-info (str "nanopass: unknown meta-variable " x) {:meta x})))]
                               [{:name x :raw raw :sort sort :of of :depth d :maybe false}])
                 (and (seq? x) (= 'maybe (first x)) (= 2 (count x)) (symbol? (second x)))
                 (let [f (first (pattern-fields metas [(second x)] d))] [(assoc f :maybe true)])
                 (seq? x) (do (when (< 1 (count (filter ellipsis? x)))
                                (throw (ex-info (str "nanopass: more than one ... in " (pr-str x)) {})))
                              (pattern-fields metas x d))
                 :else (throw (ex-info (str "nanopass: invalid pattern element " (pr-str x)) {})))]
        (recur (if rep? (nnext xs) (next xs)) (into out fs))))))

(defn type-name
  "产生式的 deftype 名：语言短名-非终结符-关键字。"
  [qname nt kw]
  (symbol (str (name qname) "-" nt "-" kw)))

(defn annotate
  "未注解的描述 → 完整描述（产生式按定义顺序编号 0 起；类型名与标签只取决于定义顺序，07 D6）。"
  [desc]
  (let [metas (meta-table desc)
        meta-sym? (fn [x] (and (symbol? x) (try (contains? metas (raw-meta x)) (catch Exception _ false))))
        tag (volatile! -1)
        acc (volatile! [])
        nts (mapv
              (fn [nt]
                (let [prods (mapv
                              (fn [p]
                                (cond
                                  (symbol? p)
                                  (let [[sort of] (or (metas (raw-meta p))
                                                      (throw (ex-info (str "nanopass: unknown meta-variable " p " in " (:name nt)) {})))]
                                    (if (= sort :term) {:kind :terminal :meta p :term of} {:kind :nonterminal :meta p :nt of}))
                                  (and (seq? p) (symbol? (first p)) (not (meta-sym? (first p))) (not (ellipsis? (first p))))
                                  (let [fields (pattern-fields metas (rest p) 0)
                                        ;; 同一关键字的产生式可以有几个（按形状区分，如 (if e1 e2) 与 (if e1 e2 e3)）：第二个起名字带序号
                                        same (count (filter #(= (first p) (:keyword %)) @acc))
                                        nm (if (zero? same) (first p) (symbol (str (first p) (inc same))))]
                                    (when-not (apply distinct? (map :name (cons {:name (gensym)} fields)))
                                      (throw (ex-info (str "nanopass: duplicate meta-variable in " (pr-str p)) {})))
                                    (let [prod {:kind :pair :keyword (first p) :name nm :pattern p :fields fields :pretty (some-> (::pretty (meta p)) normalize)
                                                :tag (vswap! tag inc) :type (type-name (:name desc) (:name nt) nm)}]
                                      (vswap! acc conj prod)
                                      prod))
                                  ;; 不以关键字开头的产生式（(e0 e1 ...)）：按形状匹配，名字 appN
                                  (and (seq? p) (seq p) (or (not (symbol? (first p))) (meta-sym? (first p))))
                                  (let [fields (pattern-fields metas p 0)
                                        nm (symbol (str "app" (count (filter #(and (= :pair (:kind %)) (nil? (:keyword %))) @acc))))]
                                    (when-not (apply distinct? (map :name (cons {:name (gensym)} fields)))
                                      (throw (ex-info (str "nanopass: duplicate meta-variable in " (pr-str p)) {})))
                                    (let [prod {:kind :pair :keyword nil :name nm :pattern p :fields fields :pretty (some-> (::pretty (meta p)) normalize)
                                                :tag (vswap! tag inc) :type (type-name (:name desc) (:name nt) nm)}]
                                      (vswap! acc conj prod)
                                      prod))
                                  :else (throw (ex-info (str "nanopass: invalid production: " (pr-str p)) {}))))
                              (:prods nt))
                      pairs (filter #(= :pair (:kind %)) prods)
                      pats (map :pattern pairs)
                      ;; 同一关键字、同样形状（元素个数、有无省略号）的两个产生式无法区分
                      shape (fn [p] [(:keyword p) (count (remove ellipsis? (:pattern p))) (boolean (some ellipsis? (:pattern p)))])]
                  (when-not (apply distinct? (cons (gensym) pats))
                    (throw (ex-info (str "nanopass: the nonterminal " (:name nt) " in language " (:name desc) " lists the same production more than once") {})))
                  (when-not (apply distinct? (cons (gensym) (map shape (filter :keyword pairs))))
                    (throw (ex-info (str "nanopass: two productions of " (:name nt) " in language " (:name desc) " share a keyword and a shape") {})))
                  (vreset! acc [])
                  (assoc nt :prods prods)))
              (:nonterminals desc))]
    (when-not (some #(= (:entry desc) (:name %)) nts)
      (throw (ex-info (str "nanopass: entry " (:entry desc) " is not a nonterminal") {})))
    (assoc desc :nonterminals nts)))

(defn nonterminal [desc nt-name]
  (or (first (filter #(= nt-name (:name %)) (:nonterminals desc)))
      (throw (ex-info (str "nanopass: " (:name desc) " has no nonterminal " nt-name) {}))))

(defn nonterminal-tags
  "非终结符（连同经非终结符交替包含的）的产生式标签集合，与它直接或间接接受的终结符名。"
  [desc nt-name]
  (loop [todo [nt-name] seen #{} tags (sorted-set) terms []]
    (if-let [[n & more] (seq todo)]
      (if (seen n)
        (recur more seen tags terms)
        (let [prods (:prods (nonterminal desc n))]
          (recur (concat more (keep :nt prods)) (conj seen n)
                 (into tags (keep :tag prods))
                 (into terms (remove (set terms) (keep :term prods))))))
      {:tags tags :terms (vec (distinct terms))})))

;; —— language->s-expression ——

(defn desc->s-expression
  "描述 → 等价的 define-language 形式（不带 extends，展开后的全量形式）。"
  [desc]
  (concat
    ['define-language (symbol (name (:name desc)))
     (cons 'terminals (map (fn [t] (list (:name t) (apply list (:metas t)))) (:terminals desc)))
     (list 'entry (:entry desc))]
    (map (fn [nt] (list* (:name nt) (apply list (:metas nt))
                         (map #(case (:kind %) :pair (:pattern %) (:meta %)) (:prods nt))))
         (:nonterminals desc))))

;; —— 按描述的 unparse ——
;; ctx：{:env 字段名 → 值 :fields 字段名 → 字段描述 :lang 运行时语言 :raw? 布尔}；raw? 为假时按 => 美化

(declare unparse-node unparse-elt)

(defn- unparse-value [v raw?]
  (if (r/node? v) (unparse-node v raw?) v))

(defn- unparse-term [ctx fname v]
  (let [f (get (:fields ctx) fname)]
    (if (and (not (:raw? ctx)) f (= :term (:sort f)))
      (if-let [pf (get (:pretties (:lang ctx)) (:of f))] (pf v) v)
      (unparse-value v (:raw? ctx)))))

(defn- unparse-elts [pats ctx]
  (let [env (:env ctx)]
    (loop [ps (seq pats) out []]
      (if-not ps
        out
        (let [p (first ps)]
          (if (and (next ps) (ellipsis? (second ps)))
            (let [names (filter #(contains? env %) (map :name (pattern-fields (fn [_] [:any nil]) [p] 0)))
                  n (count (env (first names)))
                  items (for [i (range n)]
                          (unparse-elt p (assoc ctx :env (into env (map (fn [f] [f (nth (env f) i)]) names)))))]
              (recur (nnext ps) (into out items)))
            (recur (next ps) (conj out (unparse-elt p ctx)))))))))

(defn- unparse-elt [p ctx]
  (cond
    (symbol? p) (if (contains? (:env ctx) p) (unparse-term ctx p ((:env ctx) p)) p)   ; 不是字段的符号是美化模板里的字面量
    (and (seq? p) (= 'maybe (first p))) (unparse-term ctx (second p) ((:env ctx) (second p)))
    (seq? p) (apply list (unparse-elts p ctx))
    :else p))

(defn unparse-node
  "节点 → S 表达式（列表；终结符原样）。不是节点的值原样返回。raw? 为假（缺省）时按 => 美化。"
  ([x] (unparse-node x false))
  ([x raw?]
   (if-not (r/node? x)
     x
     (let [^cleat.nanopass.records.INode n x
           lang (r/node-language n)
           prod ((:by-tag lang) (.nanoTag n))
           ctx {:env (zipmap (map :name (:fields prod)) (.nanoFields n))
                :fields (into {} (map (juxt :name identity) (:fields prod)))
                :lang lang :raw? raw?}]
       (cond
         (and (not raw?) (:pretty prod)) (unparse-elt (:pretty prod) ctx)
         (:keyword prod) (apply list (:keyword prod) (unparse-elts (rest (:pattern prod)) ctx))
         :else (apply list (unparse-elts (:pattern prod) ctx)))))))

;; —— 按描述的解析（S 表达式 → 节点）——

(declare parse-nt)

(defn- match-value [lang field v]
  (cond
    (and (:maybe field) (r/absent? v)) [v]
    (= :term (:sort field)) (when ((get (:preds lang) (:of field)) v) [v])
    :else (let [n (parse-nt lang (:of field) v)] (when-not (identical? n ::fail) [n]))))

(defn- match-list
  "pats 与 xs 匹配，返回 {字段名 值}（省略号字段为向量）或 nil。"
  [lang fields-by-name pats xs]
  (let [pats (vec pats) xs (vec xs)
        ei (first (keep-indexed (fn [i p] (when (ellipsis? p) i)) pats))]
    (if ei
      (let [p (pats (dec ei))
            before (subvec pats 0 (dec ei))
            after (subvec pats (inc ei))
            nb (count before) na (count after) n (- (count xs) nb na)]
        (when (>= n 0)
          (let [b1 (match-list lang fields-by-name before (subvec xs 0 nb))
                reps (when b1 (map #(match-list lang fields-by-name [p] [%]) (subvec xs nb (+ nb n))))
                b3 (when (and b1 (every? some? reps)) (match-list lang fields-by-name after (subvec xs (+ nb n))))]
            (when b3
              (let [names (map :name (pattern-fields (fn [_] [:any nil]) [p] 0))]
                (merge b1 b3 (into {} (map (fn [f] [f (mapv #(get % f) reps)]) names))))))))
      (when (= (count pats) (count xs))
        (reduce (fn [env [p x]]
                  (let [b (cond
                            (symbol? p) (when-let [[v] (match-value lang (fields-by-name p) x)] {p v})
                            (and (seq? p) (= 'maybe (first p))) (when-let [[v] (match-value lang (fields-by-name (second p)) x)] {(second p) v})
                            :else (when (or (seq? x) (vector? x)) (match-list lang fields-by-name p x)))]
                    (if b (merge env b) (reduced nil))))
                {} (map vector pats xs))))))

(defn parse-nt
  "S 表达式 → 非终结符 nt-name 的值；不匹配返回 ::fail。"
  [lang nt-name x]
  (let [desc (:desc lang)]
    (loop [ps (:prods (nonterminal desc nt-name))]
      (if-let [[p & more] (seq ps)]
        (let [v (case (:kind p)
                  :terminal (if ((get (:preds lang) (:term p)) x) x ::fail)
                  :nonterminal (parse-nt lang (:nt p) x)
                  :pair (if (and (or (seq? x) (vector? x)) (or (nil? (:keyword p)) (and (seq x) (= (first x) (:keyword p)))))
                          (let [fields-by-name (into {} (map (juxt :name identity) (:fields p)))
                                env (if (:keyword p)
                                      (match-list lang fields-by-name (rest (:pattern p)) (rest x))
                                      (match-list lang fields-by-name (:pattern p) x))]
                            (if env
                              (apply ((:ctors lang) (:tag p)) (map #(get env (:name %)) (:fields p)))
                              ::fail))
                          ::fail))]
          (if (identical? v ::fail) (recur more) v))
        ::fail))))
