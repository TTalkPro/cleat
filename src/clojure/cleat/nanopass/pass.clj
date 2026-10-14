;; cleat.nanopass 的 pass 层（cleat-084，designs/19 D2、07 D2 / D4）：define-pass、nanopass-case、with-output-language 的展开。
;; 宏在展开期按语言描述直接生成代码：处理器按标签 case 分派，字段经类型提示的 .- 访问，模板 (out …) 编成构造函数调用。
;;
;; 模式（输入一侧）：
;;   (kw p …)          产生式；关键字后的元素与产生式的元素按位置对齐（省略号的对齐规则同上游）
;;   ~x                绑定字段值（省略号下为向量）；顶层的 ~x 按 x 的元变量匹配终结符 / 非终结符交替
;;   ~[x]              catamorphism：字段值交给该字段非终结符的处理器，结果绑定到 x
;;   ~x ... / ~@x      省略号区段里的剩余元素（向量）；~[x] ... 对每个元素做 catamorphism
;;   符号、数等        字面量，按 = 比较
;;   else              其余
;; 模板（输出一侧，(out …)）：
;;   (kw t …)          产生式的构造；~e 为字段的值，~e ... 或 ~@e 把序列接进省略号区段，裸符号为引用的符号
;;   非终结符字段位置的 ~e 里的 (out …) 以该字段的非终结符为上下文（上游按字段切换 quasiquote 的规则）
;;
;; 生成的代码不含 gensym（临时变量名按展开内的计数器取，07 D6 的确定性）；省略号字段的值一律是实现了的向量
;; （惰性序列会改变副作用的顺序：处理器里常有登记表的副作用）。
(ns cleat.nanopass.pass
  (:require [clojure.walk]
            [cleat.nanopass.language :as lang]
            [cleat.nanopass.records :as r]))

;; —— 小工具 ——

(defn- unquote-form? [x] (and (seq? x) (= 'clojure.core/unquote (first x)) (= 2 (count x))))
(defn- unquote-splicing-form? [x] (and (seq? x) (= 'clojure.core/unquote-splicing (first x)) (= 2 (count x))))
(defn- cata-form? [x] (and (unquote-form? x) (vector? (second x))))
(defn- ellipsis? [x] (= x '...))
(defn- listy? [x] (or (seq? x) (vector? x)))
(defn- literal? [x] (or (nil? x) (true? x) (false? x) (number? x) (string? x) (char? x) (keyword? x)))

(defn- fail [& msg] (throw (ex-info (apply str "nanopass: " msg) {})))

(defn- fresh-fn
  "展开内的确定性临时名：base__npN。"
  []
  (let [n (atom 0)] (fn [base] (symbol (str base "__np" (swap! n inc))))))

(def ^:dynamic *fresh* nil)
(defn- fresh [base] (*fresh* base))

(defn- realize
  "展开结果里的惰性序列在 *fresh* 的绑定之内全部实现（生成代码时到处是 for / map）。"
  [form]
  (clojure.walk/postwalk identity form))

;; —— 语言 ——

(defn- qualified [v]
  (let [m (meta v)] (symbol (str (ns-name (:ns m))) (str (:name m)))))

(defn resolve-language
  "符号 → 语言描述（按当前命名空间决议语言的 Var）。"
  [sym]
  (let [v (resolve sym)]
    (if (var? v)
      (lang/find-description (qualified v))
      (fail "unknown language " sym))))

(defn- lang-ns [desc] (namespace (:name desc)))
(defn- short-name [desc] (name (:name desc)))

(defn- prod-class
  "产生式 deftype 的类名（符号，供类型提示）。"
  [desc prod]
  (let [c (ns-resolve (the-ns (symbol (lang-ns desc))) (:type prod))]
    (if (class? c) (symbol (.FullName ^Type c)) (fail "record type " (:type prod) " of " (:name desc) " is not loaded"))))

(defn- ctor [desc nt prod]
  (symbol (lang-ns desc) (str "make-" (short-name desc) ":" nt ":" (:keyword prod))))

(defn- nt-pred [desc nt] (symbol (lang-ns desc) (str (short-name desc) ":" nt "?")))

(defn- reachable-prods
  "非终结符 nt 直接或经非终结符交替可达的产生式：[[nt prod] …]（按定义顺序）。"
  [desc nt]
  (loop [todo [nt] seen #{} out []]
    (if-let [[n & more] (seq todo)]
      (if (seen n)
        (recur more seen out)
        (let [prods (:prods (lang/nonterminal desc n))]
          (recur (concat more (keep :nt prods)) (conj seen n)
                 (into out (for [p prods :when (= :pair (:kind p))] [n p])))))
      out)))

(defn- find-prod [desc nt kw]
  (first (filter #(= kw (:keyword (second %))) (reachable-prods desc nt))))

(defn- meta-of
  "元变量 v → [:term 终结符名] / [:nt 非终结符名] 或 nil。"
  [desc v]
  (let [raw (try (lang/raw-meta v) (catch Exception _ nil))]
    (or (some #(when (some #{raw} (:metas %)) [:term (:name %)]) (:terminals desc))
        (some #(when (some #{raw} (:metas %)) [:nt (:name %)]) (:nonterminals desc)))))

(defn- term-pred [desc term] (:pred (first (filter #(= term (:name %)) (:terminals desc)))))

(defn- field-type [f] [(:sort f) (:of f)])

;; 产生式的子模式里出现的字段名（按出现顺序）
(defn- rep-fields [rep]
  (cond
    (ellipsis? rep) []
    (symbol? rep) [rep]
    (and (seq? rep) (= 'maybe (first rep))) [(second rep)]
    (seq? rep) (vec (mapcat rep-fields rep))
    :else []))

;; —— 模板 ——

(declare walk-outs compile-template template-elt-fields)

(defn- template-list-fields
  "模板元素 ts 对齐产生式元素 ps，返回 {字段名 表达式}。lift > 0 时每个表达式给出的是该层的序列。"
  [ctx ps ts lift]
  (let [ps (vec ps) ts (vec ts)
        ei (first (keep-indexed (fn [i p] (when (ellipsis? p) i)) ps))]
    (if-not ei
      (do (when (some #(or (ellipsis? %) (unquote-splicing-form? %)) ts)
            (fail "template " (pr-str (apply list ts)) " splices where the production " (pr-str (apply list ps)) " has no ..."))
          (when (not= (count ps) (count ts))
            (fail "template " (pr-str (apply list ts)) " does not fit " (pr-str (apply list ps))))
          (apply merge {} (map #(template-elt-fields ctx %1 %2 lift) ps ts)))
      (let [rep (ps (dec ei))
            before (subvec ps 0 (dec ei)) after (subvec ps (inc ei))
            nb (count before) na (count after)
            ;; 模板元素分组：单项 / 接入（~e ... 、~@e、子模板 ...）
            items (loop [xs (seq ts) out []]
                    (if-not xs out
                      (let [x (first xs)]
                        (cond
                          (and (next xs) (ellipsis? (second xs))) (recur (nnext xs) (conj out [:splice x]))
                          (unquote-splicing-form? x) (recur (next xs) (conj out [:splice (list 'clojure.core/unquote (second x))]))
                          (ellipsis? x) (fail "misplaced ... in template " (pr-str (apply list ts)))
                          :else (recur (next xs) (conj out [:item x]))))))
            _ (when (< (count items) (+ nb na))
                (fail "template " (pr-str (apply list ts)) " does not fit " (pr-str (apply list ps))))
            tb (subvec items 0 nb)
            ta (subvec items (- (count items) na))
            mid (subvec items nb (- (count items) na))
            _ (when (some #(= :splice (first %)) (concat tb ta))
                (fail "template " (pr-str (apply list ts)) ": splice outside the ... part of " (pr-str (apply list ps))))
            rep-names (rep-fields rep)
            contribs (map (fn [[k t]]
                            (if (= k :item)
                              [:item (template-elt-fields ctx rep t lift)]
                              [:splice (template-elt-fields ctx rep t (inc lift))]))
                          mid)
            combine (fn [f]
                      (let [parts (map (fn [[k m]] [k (get m f)]) contribs)]
                        (cond
                          (every? #(= :item (first %)) parts) (mapv second parts)
                          (= 1 (count parts)) `(r/->vec ~(second (first parts)))
                          :else `(vec (concat ~@(map (fn [[k e]] (if (= k :item) [e] e)) parts))))))]
        (when (and (pos? lift) (seq (filter #(= :item (first %)) mid)))
          (fail "template " (pr-str (apply list ts)) ": single items inside a spliced sub-template are not supported"))
        (merge (apply merge {} (map #(template-elt-fields ctx %1 (second %2) lift) before tb))
               (apply merge {} (map #(template-elt-fields ctx %1 (second %2) lift) after ta))
               (into {} (map (fn [f] [f (combine f)]) rep-names)))))))

(defn- template-elt-fields [ctx p t lift]
  (cond
    (or (symbol? p) (and (seq? p) (= 'maybe (first p))))
    (let [fname (if (symbol? p) p (second p))
          f (first (filter #(= fname (:name %)) (:fields (:prod ctx))))
          [sort of] (field-type f)
          sub (assoc ctx :nt of)]
      {fname
       (cond
         (unquote-form? t) (if (= sort :nt) (walk-outs sub (second t)) (second t))
         (pos? lift) (fail "template element " (pr-str t) " must be ~expr inside a spliced sub-template")
         (symbol? t) (list 'quote t)
         (literal? t) t
         (listy? t) (if (= sort :nt)
                      (compile-template sub t)
                      (if (some unquote-form? (tree-seq listy? seq t))
                        (fail "unquote inside a terminal datum is not supported: " (pr-str t))
                        (list 'quote t)))
         :else (fail "invalid template element " (pr-str t)))})
    (seq? p)
    (cond
      (listy? t) (template-list-fields ctx p t lift)
      :else (fail "template element " (pr-str t) " must be a list to fit " (pr-str p)))
    :else (fail "invalid production element " (pr-str p))))

(defn compile-template
  "模板 t 在上下文 ctx（:desc 输出语言、:nt 非终结符）里编成表达式。"
  [ctx t]
  (cond
    (unquote-form? t) (walk-outs ctx (second t))
    (symbol? t) (list 'quote t)
    (literal? t) t
    (and (listy? t) (symbol? (first t)))
    (let [desc (:desc ctx)
          [nt prod] (or (find-prod desc (:nt ctx) (first t))
                        (fail "no production " (first t) " in " (short-name desc) " " (:nt ctx) " for template " (pr-str t)))
          fields (template-list-fields (assoc ctx :prod prod) (rest (:pattern prod)) (rest t) 0)]
      `(~(ctor desc nt prod) ~@(map #(get fields (:name %)) (:fields prod))))
    :else (fail "invalid template " (pr-str t))))

(defn- head-is? [form sym]
  (and (seq? form) (symbol? (first form))
       (let [h (first form)]
         (or (= h sym) (= h (symbol "cleat.nanopass" (name sym)))))))

(defn walk-outs
  "form 里的 (out …) 在上下文 ctx 里编成构造函数调用；(in-context NT …) 换上下文；跳过 quote 与嵌套的 with-output-language。
  ctx 为 nil 时 out 原样保留（展开时报错）。"
  [ctx form]
  (cond
    (nil? ctx) form
    (head-is? form 'out) (do (when (not= 2 (count form)) (fail "out takes one template: " (pr-str form)))
                             (compile-template ctx (second form)))
    (head-is? form 'in-context) `(do ~@(map #(walk-outs (assoc ctx :nt (second form)) %) (nnext form)))
    (and (seq? form) (#{'quote 'with-output-language 'cleat.nanopass/with-output-language} (first form))) form
    (seq? form) (with-meta (apply list (map #(walk-outs ctx %) form)) (meta form))
    (vector? form) (with-meta (mapv #(walk-outs ctx %) form) (meta form))
    (map? form) (into (empty form) (map (fn [[k v]] [(walk-outs ctx k) (walk-outs ctx v)]) form))
    (set? form) (into (empty form) (map #(walk-outs ctx %) form))
    :else form))

;; —— 模式 ——
;; 编译结果为步骤序列：[:test 表达式] / [:bind 符号 表达式] / [:cata 符号 处理器调用]（cata 在 guard 之后求值）。

(defn- hinted [sym cls] (with-meta sym {:tag cls}))

(declare match-elt match-list)

(defn- field-by-name [prod n] (first (filter #(= n (:name %)) (:fields prod))))

(defn- map-depth
  "n 层嵌套序列 v 的每个元素换成 (gen 元素符号) 生成的表达式的值（mapv，立即实现）。"
  [n gen v]
  (if (zero? n) (gen v)
      (let [y (fresh "y")] `(mapv (fn [~y] ~(map-depth (dec n) gen y)) ~v))))

(defn- match-list
  "模式元素 pats 对齐产生式元素 ps；env：字段名 → 当前层的值表达式。lift > 0 时只允许整段接入。"
  [ctx prod pats ps env lift]
  (let [ps (vec ps) pats (vec pats)
        ei (first (keep-indexed (fn [i p] (when (ellipsis? p) i)) ps))]
    (if-not ei
      (do (when (some #(or (ellipsis? %) (unquote-splicing-form? %)) pats)
            (fail "pattern " (pr-str (apply list pats)) " uses ... where " (pr-str (apply list ps)) " has none"))
          (when (not= (count ps) (count pats))
            (fail "pattern " (pr-str (apply list pats)) " does not fit " (pr-str (apply list ps))))
          (vec (mapcat #(match-elt ctx prod %1 %2 env lift) pats ps)))
      (let [rep (ps (dec ei))
            before (subvec ps 0 (dec ei)) after (subvec ps (inc ei))
            nb (count before) na (count after)
            items (loop [xs (seq pats) out []]
                    (if-not xs out
                      (let [x (first xs)]
                        (cond
                          (and (next xs) (ellipsis? (second xs))) (recur (nnext xs) (conj out [:splice x]))
                          (unquote-splicing-form? x) (recur (next xs) (conj out [:splice (list 'clojure.core/unquote (second x))]))
                          (ellipsis? x) (fail "misplaced ... in pattern " (pr-str (apply list pats)))
                          :else (recur (next xs) (conj out [:item x]))))))
            _ (when (< (count items) (+ nb na)) (fail "pattern " (pr-str (apply list pats)) " does not fit " (pr-str (apply list ps))))
            pb (subvec items 0 nb) pa (subvec items (- (count items) na))
            mid (subvec items nb (- (count items) na))
            nsplice (count (filter #(= :splice (first %)) mid))
            _ (when (or (> nsplice 1) (some #(= :splice (first %)) (concat pb pa)))
                (fail "pattern " (pr-str (apply list pats)) ": at most one ... part, inside the production's ... part"))
            _ (when (and (pos? lift) (or (seq pb) (seq pa) (some #(= :item (first %)) mid)))
                (fail "pattern " (pr-str (apply list pats)) ": only whole ... parts are supported inside a spliced sub-pattern"))
            rep-names (rep-fields rep)
            len-sym (fresh "n")
            f0 (first rep-names)
            m (count (filter #(= :item (first %)) mid))
            si (first (keep-indexed (fn [i [k]] (when (= k :splice) i)) mid))
            idx (fn [i] (if (and si (> i si)) `(- ~len-sym ~(- (count mid) i)) i))
            steps-mid (vec (mapcat
                             (fn [i [k p]]
                               (if (= k :item)
                                 (match-elt ctx prod p rep (into env (map (fn [f] [f `(nth ~(env f) ~(idx i))]) rep-names)) lift)
                                 (let [end `(- ~len-sym ~(- (count mid) i 1))]
                                   (match-elt ctx prod p rep
                                              (into env (map (fn [f] [f (if (and (zero? i) (= (count mid) 1)) (env f) `(subvec (r/->vec ~(env f)) ~i ~end))]) rep-names))
                                              (inc lift)))))
                             (range) mid))]
        (vec (concat
               (mapcat #(match-elt ctx prod %1 %2 env lift) (map second pb) before)
               (mapcat #(match-elt ctx prod %1 %2 env lift) (map second pa) after)
               ;; 长度：没有整段接入时必须相等；有接入时不少于单项数（只有接入、没有单项时不用测）
               (when (and (zero? lift) (or (not si) (pos? m)))
                 [[:bind len-sym `(count ~(env f0))]
                  [:test (if si `(>= ~len-sym ~m) `(== ~len-sym ~m))]])
               steps-mid))))))

(defn- proc-call [ctx proc v]
  (when-let [scope (:scope ctx)]
    (doseq [x (:extras proc)]
      (when-not (scope x)
        (fail (:who ctx) ": calling processor " (:name proc) " needs " x ", which is not in scope (declare the processor with it)"))))
  `(~(:name proc) ~v ~@(:extras proc)))

(defn- cata-call
  "catamorphism：字段（非终结符 nt）的值 v 交给处理器；lift 层省略号时逐个映射；maybe? 时无值（nil / false）原样。"
  ([ctx nt v lift] (cata-call ctx nt v lift false))
  ([ctx nt v lift maybe?]
   (let [proc ((:find-proc ctx) nt)
         one (fn [x] (if maybe? `(if (r/absent? ~x) ~x ~(proc-call ctx proc x)) (proc-call ctx proc x)))]
     (if (zero? lift)
       (if maybe? (let [t (fresh "m")] `(let [~t ~v] ~(one t))) (one v))
       (map-depth lift one v)))))

(defn- match-production
  "值 v（静态知道是非终结符 nt 的值）匹配产生式模式 pat（列表）。top?：处理器已按标签分派，不用再测类型。"
  [ctx pat nt v & [top?]]
  (let [desc (:desc ctx)
        [_ prod] (or (find-prod desc nt (first pat))
                     (fail "no production " (first pat) " in " (short-name desc) " " nt " for pattern " (pr-str pat)))
        node (hinted (fresh "node") (prod-class desc prod))
        env (into {} (map (fn [f] [(:name f) `(. ~node ~(symbol (str "-" (:name f))))]) (:fields prod)))]
    (into (if top? [[:bind node v]] [[:test `(instance? ~(prod-class desc prod) ~v)] [:bind node v]])
          (match-list ctx prod (rest pat) (rest (:pattern prod)) env 0))))

(defn- match-elt [ctx prod p pe env lift]
  (cond
    (or (symbol? pe) (and (seq? pe) (= 'maybe (first pe))))
    (let [fname (if (symbol? pe) pe (second pe))
          f (field-by-name prod fname)
          v (env fname)]
      (cond
        (cata-form? p) (let [[x & more] (second p)]
                         (when (or more (not (symbol? x))) (fail "only ~[x] catamorphisms are supported, got " (pr-str p)))
                         (when (not= :nt (:sort f)) (fail "catamorphism " (pr-str p) " on terminal field " fname))
                         [[:cata x (cata-call ctx (:of f) v lift (:maybe f))]])
        (unquote-form? p) (if (symbol? (second p)) [[:bind (second p) v]] (fail "invalid pattern variable " (pr-str p)))
        (pos? lift) (fail "pattern element " (pr-str p) " must be ~x or ~[x] inside a spliced sub-pattern")
        (or (symbol? p) (literal? p)) [[:test `(= ~v '~p)]]
        (and (seq? p) (symbol? (first p)) (= :nt (:sort f))) (let [t (fresh "v")] (into [[:bind t v]] (match-production ctx p (:of f) t)))
        :else (fail "invalid pattern element " (pr-str p) " for field " fname)))
    (seq? pe)
    (if (listy? p)
      (match-list ctx prod p pe env lift)
      (fail "pattern element " (pr-str p) " must be a list to fit " (pr-str pe)))
    :else (fail "invalid production element " (pr-str pe))))

(defn- build-steps
  "步骤 → 代码：测试失败返回 r/no-match；guard 在 cata 之前、所有绑定之后求值。"
  [steps guard body]
  (let [plain (remove #(= :cata (first %)) steps)
        catas (filter #(= :cata (first %)) steps)
        inner `(let [~@(mapcat (fn [[_ x e]] [x e]) catas)] ~@body)
        inner (if guard `(if ~guard ~inner r/no-match) inner)]
    (reduce (fn [code [k a b]]
              (case k
                :test `(if ~a ~code r/no-match)
                :bind `(let [~a ~b] ~code)))
            inner (reverse plain))))

(defn- can-fail? [steps guard] (or guard (some #(= :test (first %)) steps)))

;; —— 处理器的分派 ——

(defn- parse-clause [c]
  (when-not (and (vector? c) (seq c)) (fail "clause must be a vector [pattern body …], got " (pr-str c)))
  (let [[pat & body] c
        [guard body] (if (and (seq? (first body)) (= 'guard (ffirst body)))
                       [(if (= 2 (count (first body))) (second (first body)) `(and ~@(rest (first body)))) (rest body)]
                       [nil body])]
    {:pattern pat :guard guard :body body}))

(defn- auto-clause
  "输入产生式 prod 的复制子句（输出语言同一非终结符可达同样模式的产生式时），否则 nil。"
  [ctx node prod]
  (let [out (:out-desc ctx) out-nt (:out-nt ctx)]
    (when (and out out-nt)
      (when-let [[ont oprod] (find-prod out out-nt (:keyword prod))]
        (when (= (:pattern oprod) (:pattern prod))
          (let [hn (hinted (fresh "node") (prod-class (:in-desc ctx) prod))]
            `(let [~hn ~node]
               (~(ctor out ont oprod)
                ~@(for [f (:fields prod)]
                    (let [v `(. ~hn ~(symbol (str "-" (:name f))))]
                      (if (= :nt (:sort f))
                        (cata-call ctx (:of f) v (:depth f) (:maybe f))
                        v)))))))))))

(defn- dispatch-code
  "处理器体：ir 按标签（终结符按谓词）选子句。ctx：:in-desc :nt :out-desc :out-nt :find-proc；auto?：缺的产生式自动生成复制子句。"
  [ctx ir clauses auto? who]
  (let [desc (:in-desc ctx)
        nt (:nt ctx)
        mctx (assoc ctx :desc desc)
        prods (reachable-prods desc nt)
        {:keys [terms]} (lang/nonterminal-tags desc nt)
        clauses (map parse-clause clauses)
        else-c (first (filter #(= 'else (:pattern %)) clauses))
        no-clause (fn [what] `(throw (ex-info (str ~(str "nanopass: " who ": no clause matches ") (r/brief ~ir)) {:node ~ir})))
        ;; 某个产生式（或终结符）适用的子句链
        chain (fn [applicable fallback]
                (reduce (fn [code {:keys [steps guard body]}]
                          (if (can-fail? steps guard)
                            (let [rs (fresh "r")]
                              `(let [~rs ~(build-steps steps guard body)] (if (identical? ~rs r/no-match) ~code ~rs)))
                            (build-steps steps guard body)))
                        fallback (reverse applicable)))
        ;; 截断：第一个不会失败的子句之后的子句用不到
        upto (fn [cs] (let [[a b] (split-with #(can-fail? (:steps %) (:guard %)) cs)] (concat a (take 1 b))))
        top-var? (fn [c] (and (unquote-form? (:pattern c)) (symbol? (second (:pattern c)))))
        clause-for-prod (fn [[pnt prod] c]
                          (cond
                            (= 'else (:pattern c)) {:steps [] :guard (:guard c) :body (:body c)}
                            (top-var? c)
                            (let [[sort of] (or (meta-of desc (second (:pattern c))) (fail who ": unknown meta-variable " (second (:pattern c))))]
                              (when (and (= sort :nt) (some #(= (:tag prod) (:tag (second %))) (reachable-prods desc of)))
                                {:steps [[:bind (second (:pattern c)) ir]] :guard (:guard c) :body (:body c)}))
                            (and (listy? (:pattern c)) (not (unquote-form? (:pattern c))) (= (first (:pattern c)) (:keyword prod)))
                            {:steps (match-production mctx (apply list (:pattern c)) nt ir true) :guard (:guard c) :body (:body c)}
                            :else nil))
        _ (doseq [c clauses]
            (let [p (:pattern c)]
              (when (and (listy? p) (symbol? (first p)) (not (unquote-form? p)) (not (find-prod desc nt (first p))))
                (fail who ": no production " (first p) " in " (short-name desc) " " nt))))
        cases (for [[pnt prod :as pp] prods]
                (let [app (upto (keep #(clause-for-prod pp %) clauses))
                      total? (some #(not (can-fail? (:steps %) (:guard %))) app)
                      ;; 经非终结符交替到达的产生式：交给该非终结符的处理器（上游的规则）；否则复制子句
                      delegate (when (and (not= pnt nt) (not total?)) (when-let [p ((:try-find-proc ctx) pnt)] (proc-call ctx p ir)))
                      auto (when (and auto? (not total?) (not delegate)) (auto-clause ctx ir prod))]
                  (when (and auto? (:out-desc ctx) (empty? app) (not delegate) (not auto))
                    (fail who ": no clause for production " (pr-str (:pattern prod)) " and no counterpart in " (short-name (:out-desc ctx)) " " (:out-nt ctx)))
                  [(:tag prod) (chain app (or delegate auto (no-clause prod)))]))
        term-clauses (upto (keep (fn [c]
                                   (cond
                                     (= 'else (:pattern c)) {:steps [] :guard (:guard c) :body (:body c)}
                                     (top-var? c)
                                     (let [[sort of] (meta-of desc (second (:pattern c)))]
                                       (when (= sort :term)
                                         {:steps [[:test `(~(term-pred desc of) ~ir)] [:bind (second (:pattern c)) ir]]
                                          :guard (:guard c) :body (:body c)}))
                                     :else nil))
                                 clauses))
        term-fallback (if (and auto? (seq terms) (:out-desc ctx)) ir (no-clause nil))]
    `(if (instance? cleat.nanopass.records.INode ~ir)
       (case (.nanoTag ~(hinted ir 'cleat.nanopass.records.INode))
         ~@(mapcat identity cases)
         ~(no-clause nil))
       ~(chain term-clauses term-fallback))))

;; —— define-pass ——

(defn- parse-lang-spec
  "L / [L NT] / * → {:desc :nt} 或 nil（*）。"
  [spec]
  (cond
    (= '* spec) nil
    (symbol? spec) (let [d (resolve-language spec)] {:desc d :nt (:entry d)})
    (and (vector? spec) (= 2 (count spec))) (let [d (resolve-language (first spec))] {:desc d :nt (second spec)})
    :else (fail "invalid language spec " (pr-str spec))))

(defn- parse-processor
  "(Name InNT [formals] -> OutNT [rets] clause …) 或 (Name [formals] -> OutNT [rets] clause …)。"
  [form]
  (let [[pname & more] form
        [in-nt more] (if (vector? (first more)) [pname more] [(first more) (rest more)])
        [formals arrow out-nt rets & clauses] more]
    (when-not (and (symbol? pname) (vector? formals) (seq formals) (= '-> arrow) (symbol? out-nt) (vector? rets))
      (fail "invalid processor " (pr-str (take 6 form))))
    {:name pname :in-nt in-nt :formals formals :extras (vec (rest formals)) :out-nt out-nt :rets rets :clauses clauses}))

(defn- processor? [form]
  (and (seq? form) (symbol? (first form))
       (let [m (rest form)]
         (or (and (vector? (first m)) (= '-> (second m)))
             (and (symbol? (first m)) (vector? (second m)) (= '-> (nth m 2 nil)))))))

(defn- definitions->bindings
  "definitions 段：(def x e) → let 的绑定；(defn f [a] …) → letfn 的函数。"
  [forms]
  (reduce (fn [acc f]
            (cond
              (and (seq? f) (= 'def (first f))) (update acc :values conj [(second f) (nth f 2)])
              (and (seq? f) (#{'defn 'defn-} (first f))) (update acc :fns conj (rest f))
              :else (fail "definitions: only (def …) and (defn …) are supported, got " (pr-str f))))
          {:values [] :fns []} forms))

(defn expand-define-pass [pname in-spec formals out-spec rets body]
  (binding [*fresh* (fresh-fn)]
    (let [in (or (parse-lang-spec in-spec) (fail "define-pass " pname ": the input must be a language"))
          out (parse-lang-spec out-spec)
          defs (definitions->bindings (mapcat rest (filter #(and (seq? %) (= 'definitions (first %))) body)))
          procs (mapv parse-processor (filter processor? body))
          tail (remove #(or (processor? %) (and (seq? %) (= 'definitions (first %)))) body)
          out-desc (:desc out)
          ;; 自动生成的处理器（cata 或复制子句要用、却没声明的）：输入与输出同名的非终结符，形参只有 ir
          generated (atom [])
          find-proc (fn find-proc [nt]
                      (or (let [cs (filter #(= nt (:in-nt %)) procs)]
                            (or (first (filter #(= nt (:out-nt %)) cs)) (first cs)))
                          (first (filter #(= nt (:in-nt %)) @generated))
                          (if (and out-desc (some #(= nt (:name %)) (:nonterminals out-desc)))
                            (let [p {:name (symbol (str "auto-" nt)) :in-nt nt :formals '[ir] :extras [] :out-nt nt :clauses [] :auto true}]
                              (swap! generated conj p)
                              p)
                            (fail "define-pass " pname ": no processor for " nt " and the output has no nonterminal " nt))))
          try-find-proc (fn [nt]
                          (or (first (filter #(= nt (:in-nt %)) procs))
                              (first (filter #(= nt (:in-nt %)) @generated))
                              (when (and out-desc (some #(= nt (:name %)) (:nonterminals out-desc))) (find-proc nt))))
          scope-base (set (concat formals (map first (:values defs)) (map first (:fns defs))))
          ctx-for (fn [p] {:in-desc (:desc in) :nt (:in-nt p)
                           :out-desc (when (and out-desc (not= '* (:out-nt p))) out-desc)
                           :out-nt (when (not= '* (:out-nt p)) (:out-nt p))
                           :find-proc find-proc :try-find-proc try-find-proc
                           :scope (into scope-base (:formals p))
                           :who (str pname " " (:name p))})
          out-ctx (fn [p] (when (and out-desc (not= '* (:out-nt p))) {:desc out-desc :nt (:out-nt p)}))
          proc-fn (fn [p]
                    (let [ir (first (:formals p))
                          ctx (ctx-for p)
                          clauses (map (fn [c]
                                         (if (vector? c)
                                           (let [[pat & b] c] (into [pat] (map #(walk-outs (out-ctx p) %) b)))
                                           c))
                                       (:clauses p))]
                      ;; 立即实现：生成代码时才登记的自动处理器（find-proc）要在下面的循环之前出现
                      (realize
                        `(~(:name p) ~(:formals p)
                          ~(dispatch-code ctx ir clauses true (str pname " " (:name p)))))))
          ;; 先编显式的处理器（期间可能登记自动处理器），再编自动处理器（它们又可能引出新的）
          explicit (mapv proc-fn procs)
          entry-proc (find-proc (:nt in))
          autos (loop [done 0 out []]
                  (if (< done (count @generated))
                    (recur (inc done) (conj out (proc-fn (nth @generated done))))
                    out))
          tail (if (seq tail) tail [`(~(:name entry-proc) ~(first formals) ~@(:extras entry-proc))])
          entry-ctx (when out-desc {:desc out-desc :nt (:nt out)})
          ir (first formals)]
      (realize
       `(defn ~pname ~formals
         (let [~@(mapcat (fn [[x e]] [x (walk-outs entry-ctx e)]) (:values defs))]
           (letfn [~@(map (fn [f] (map #(walk-outs entry-ctx %) f)) (:fns defs))
                   ~@explicit
                   ~@autos]
             (when-not (~(nt-pred (:desc in) (:nt in)) ~ir)
               (throw (ex-info (str ~(str "nanopass: " pname ": expected " (short-name (:desc in)) " " (:nt in) ", got ") (r/brief ~ir)) {:node ~ir})))
             ~@(map #(walk-outs entry-ctx %) tail))))))))

;; —— nanopass-case / with-output-language ——

(defn expand-nanopass-case [spec e clauses]
  (binding [*fresh* (fresh-fn)]
    (let [{:keys [desc nt]} (or (parse-lang-spec spec) (fail "nanopass-case needs [L NT]"))
          self 'nanopass-self
          uses-cata (some cata-form? (tree-seq listy? seq clauses))
          ctx {:in-desc desc :nt nt
               :try-find-proc (fn [n] nil)
               :find-proc (fn [n]
                            (if (= n nt)
                              {:name self :extras []}
                              (fail "nanopass-case: catamorphism on " n " inside a case on " nt)))}
          ir 'nanopass-ir
          code (dispatch-code ctx ir clauses false "nanopass-case")]
      (realize
        (if uses-cata
          `(letfn [(~self [~ir] ~code)] (~self ~e))
          `(let [~ir ~e] ~code))))))

(defn expand-with-output-language [spec body]
  (binding [*fresh* (fresh-fn)]
    (let [{:keys [desc nt]} (or (parse-lang-spec spec) (fail "with-output-language needs L or [L NT]"))]
      (realize `(do ~@(map #(walk-outs {:desc desc :nt nt} %) body))))))
