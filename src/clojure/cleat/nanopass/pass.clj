;; cleat.nanopass 的 pass 层（cleat-084 / 095，designs/19 D2、07 D2 / D4）：define-pass、nanopass-case、with-output-language 的展开。
;; 宏在展开期按语言描述直接生成代码：处理器按标签 case 分派，字段经类型提示的 .- 访问，模板 (out …) 编成构造函数调用。
;;
;; 模式（输入一侧）：
;;   (kw p …)          产生式；关键字后的元素与产生式的元素按位置对齐（省略号的对齐规则同上游）；不以关键字开头的产生式按形状找
;;   ~x                绑定字段值（省略号下为向量）；顶层的 ~x 按 x 的元变量匹配终结符 / 非终结符交替
;;   ~[y]              catamorphism：字段值交给该字段非终结符的处理器，结果绑定到 y
;;   ~[y z …]          同上，处理器有多个返回值（向量）时依次绑定
;;   ~[x 实参 … -> y …]           显式写出实参（x 只是输入的名字）：处理器的额外形参按位置传
;;   ~[f :> x 实参 … -> y …]      指定处理器 f（上游的 ,[f : x … -> y …]）
;;   ~x ... / ~@x      省略号区段里的剩余元素（向量）；~[y] ... 对每个元素做 catamorphism；区段里也可以写子模式（含嵌套的产生式）
;;   符号、数等        字面量，按 = 比较
;;   else              其余
;; 模板（输出一侧，(out …)）：
;;   (kw t …)          产生式的构造；~e 为字段的值，~e ... 或 ~@e 把序列接进省略号区段，裸符号为引用的符号；省略号区段里可以写
;;                     带 ~ 的子模板（含嵌套的产生式：按各 ~ 表达式逐元素构造）
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
  (symbol (lang-ns desc) (str "make-" (short-name desc) ":" nt ":" (:name prod))))

(defn- nt-pred [desc nt] (symbol (lang-ns desc) (str (short-name desc) ":" nt "?")))

(defn- nonterminal? [desc nt] (some #(= nt (:name %)) (:nonterminals desc)))
(defn- terminal? [desc t] (some #(= t (:name %)) (:terminals desc)))

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

(declare shape-fits?)

(defn- find-prod
  "关键字 kw 的产生式；给了元素 xs（关键字后的部分）时在同关键字的几个里按形状选。"
  ([desc nt kw] (when kw (first (filter #(= kw (:keyword (second %))) (reachable-prods desc nt)))))
  ([desc nt kw xs]
   (when kw
     (let [cands (filter #(= kw (:keyword (second %))) (reachable-prods desc nt))
           splice? (some #(or (ellipsis? %) (unquote-splicing-form? %)) xs)
           n (count (remove ellipsis? xs))]
       (or (first (filter (fn [[_ p]] (shape-fits? (rest (:pattern p)) n splice?)) cands))
           (first cands))))))

(defn- shape-fits?
  "元素个数 n（含省略号区段的接入时 splice? 为真）能否套进模式 pat。"
  [pat n splice?]
  (let [k (count (remove ellipsis? pat))
        rep? (some ellipsis? pat)]
    (cond
      rep? (or splice? (>= n (dec k)))
      splice? false
      :else (= n k))))

(defn- find-shape-prod
  "不以关键字开头的产生式里第一个形状相符的：元素 xs（模板 / 模式的元素）。"
  [desc nt xs]
  (let [splice? (some #(or (ellipsis? %) (unquote-splicing-form? %)) xs)
        n (count (remove ellipsis? xs))]
    (first (filter (fn [[_ p]] (and (nil? (:keyword p)) (shape-fits? (:pattern p) n splice?)))
                   (reachable-prods desc nt)))))

(defn- prod-for-list
  "模板 / 模式列表 t 对应的产生式：以关键字开头的按关键字，否则按形状。返回 [nt prod 元素们 产生式元素们]。"
  [desc nt t what]
  (if-let [[pnt prod] (and (symbol? (first t)) (find-prod desc nt (first t) (rest t)))]
    [pnt prod (rest t) (rest (:pattern prod))]
    (if-let [[pnt prod] (find-shape-prod desc nt t)]
      [pnt prod t (:pattern prod)]
      (fail "no production " (if (symbol? (first t)) (first t) "of that shape") " in " (short-name desc) " " nt " for " what " " (pr-str t)))))

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

(defn- map-depth
  "n 层嵌套序列 v 的每个元素换成 (gen 元素符号) 生成的表达式的值（mapv，立即实现）。"
  [n gen v]
  (if (zero? n) (gen v)
      (let [y (fresh "y")] `(mapv (fn [~y] ~(map-depth (dec n) gen y)) ~v))))

;; —— 处理器的调用 ——
;; proc：{:name :in-nt :formals :extras :defaults :out-nt :rets :nvals}；额外形参按名字从作用域取，没有时用缺省值

(defn- proc-nvals
  "处理器返回几个值：输出是非终结符时节点 + 返回值；输出 * 时返回值（至少 1）。"
  [proc]
  (if (= '* (:out-nt proc)) (max 1 (count (:rets proc))) (inc (count (:rets proc)))))

(defn- extra-arg
  "处理器 proc 的额外形参 x 在上下文里的实参表达式：作用域里的同名变量，否则缺省值，否则报错。"
  [ctx proc x]
  (let [scope (:scope ctx)]
    (cond
      (or (nil? scope) (scope x)) x
      (contains? (:defaults proc) x) (get (:defaults proc) x)
      :else (fail (:who ctx) ": calling processor " (:name proc) " needs " x ", which is not in scope (declare the processor with it)"))))

(defn- satisfiable?
  "处理器的额外形参在作用域里都有（或有缺省值）。"
  [ctx proc]
  (let [scope (:scope ctx)]
    (or (nil? scope) (every? #(or (scope %) (contains? (:defaults proc) %)) (:extras proc)))))

(defn- proc-call
  ([ctx proc v] (proc-call ctx proc v nil))
  ([ctx proc v args]
   (when (and args (not= (count args) (count (:extras proc))))
     (fail (:who ctx) ": processor " (:name proc) " takes " (count (:extras proc)) " extra argument(s), got " (count args)))
   `(~(:name proc) ~v ~@(or args (map #(extra-arg ctx proc %) (:extras proc))))))

;; —— 模板 ——

(declare walk-outs compile-template template-elt-fields)

(defn- leaf-ref
  "模板里的 ~表达式：在逐元素构造的子模板里（ctx 有 :leaves）记成一个临时名（外层按序列逐元素传入），否则原样。"
  [ctx code]
  (if-let [leaves (:leaves ctx)]
    (let [s (fresh "t")] (swap! leaves conj [s code]) s)
    code))

(defn- lifted-template
  "省略号区段（lift 层）里的产生式子模板：其中每个 ~表达式在本层都是序列，按序列逐元素（mapv）构造。"
  [ctx t lift]
  (let [leaves (atom [])
        code (compile-template (assoc ctx :leaves leaves) t)
        syms (mapv first @leaves)
        seqs (mapv #(leaf-ref ctx (second %)) @leaves)]
    (when (empty? syms) (fail "template " (pr-str t) " inside a ... part needs at least one ~expression"))
    (let [one (fn [c args] `(mapv (fn [~@syms] ~c) ~@args))]
      (loop [k lift c code]
        (if (= k 1) (one c seqs) (recur (dec k) (one c syms)))))))

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
          (fail "template " (pr-str (apply list ts)) ": single items inside a spliced list are not supported (write the ... part as one ~sequence)"))
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
         (unquote-form? t) (leaf-ref ctx (if (= sort :nt) (walk-outs sub (second t)) (second t)))
         (symbol? t) (if (pos? lift) (fail "template element " (pr-str t) " inside a ... part must be ~expr") (list 'quote t))
         (literal? t) (if (pos? lift) (fail "template element " (pr-str t) " inside a ... part must be ~expr") t)
         (listy? t) (cond
                      (not= sort :nt) (if (some unquote-form? (tree-seq listy? seq t))
                                        (fail "unquote inside a terminal datum is not supported: " (pr-str t))
                                        (if (pos? lift) (fail "template element " (pr-str t) " inside a ... part must be ~expr") (list 'quote t)))
                      (pos? lift) (lifted-template sub t lift)
                      :else (compile-template sub t))
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
    (unquote-form? t) (leaf-ref ctx (walk-outs ctx (second t)))
    (symbol? t) (list 'quote t)
    (literal? t) t
    (listy? t)
    (let [desc (:desc ctx)
          [nt prod elts pelts] (prod-for-list desc (:nt ctx) t "template")
          fields (template-list-fields (assoc ctx :prod prod) pelts elts 0)]
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
                             (compile-template (dissoc ctx :leaves) (second form)))
    (head-is? form 'in-context) `(do ~@(map #(walk-outs (assoc ctx :nt (second form)) %) (nnext form)))
    (and (seq? form) (#{'quote 'with-output-language 'cleat.nanopass/with-output-language} (first form))) form
    (seq? form) (with-meta (apply list (map #(walk-outs ctx %) form)) (meta form))
    (vector? form) (with-meta (mapv #(walk-outs ctx %) form) (meta form))
    (map? form) (into (empty form) (map (fn [[k v]] [(walk-outs ctx k) (walk-outs ctx v)]) form))
    (set? form) (into (empty form) (map #(walk-outs ctx %) form))
    :else form))

;; —— 模式 ——
;; 编译结果为步骤序列：
;;   [:test 表达式]              失败则本子句不匹配
;;   [:bind 符号 表达式]
;;   [:cata {:outs [符号 …] :sort :nt|:term :of 非终结符|终结符 :v 表达式 :maybe 布尔 :depth n :proc 符号|nil :args [表达式 …]|nil}]
;;                               catamorphism（guard 之后求值）；depth 层省略号时逐元素映射
;; 省略号区段里的子模式按元素编译（字段的值在本层是向量）：生成 (mapv (fn [元素 …] …) 向量 …)，
;; 元素级的绑定变成列向量，元素级的 cata 提到外层（depth + 1）。

(defn- hinted [sym cls] (with-meta sym {:tag cls}))

(declare match-elt match-list match-production)

(defn- field-by-name [prod n] (first (filter #(= n (:name %)) (:fields prod))))

(defn- parse-cata
  "~[…] 的内容 → {:outs :proc :args}。"
  [v p]
  (let [v (vec v)
        ai (first (keep-indexed (fn [i x] (when (= '-> x) i)) v))]
    (when (some #(= :> %) (if ai (subvec v 0 ai) v))
      (when-not (= :> (second v)) (fail "invalid catamorphism " (pr-str p) ": write ~[proc :> input args … -> outs …]")))
    (let [proc (when (= :> (second v)) (first v))
          left (if ai (subvec v 0 ai) [])
          left (if proc (subvec left 2) left)
          outs (if ai (subvec v (inc ai)) v)]
      (when-not (and (seq outs) (every? symbol? outs)) (fail "invalid catamorphism " (pr-str p) ": the results must be symbols"))
      (when (and ai (empty? left)) (fail "invalid catamorphism " (pr-str p) ": name the input before ->"))
      (when (and (not ai) proc) (fail "invalid catamorphism " (pr-str p) ": ~[proc :> input … -> outs …]"))
      {:outs (vec outs) :proc proc :args (when ai (vec (rest left)))})))

(defn- cata-step [ctx f v depth p]
  (let [{:keys [outs proc args]} (parse-cata (second p) p)]
    [[:cata {:outs outs :sort (:sort f) :of (:of f) :v v :maybe (boolean (:maybe f)) :depth depth :proc proc :args args}]]))

(defn- lift-steps
  "元素级的步骤 → 本层的步骤：按 el-syms 逐元素（mapv）执行测试与绑定，绑定变成列向量，cata 的输入收集成列向量后提到本层。"
  [steps el-syms seqs]
  (let [binds (vec (for [[k a] steps :when (= k :bind)] a))
        catas (vec (for [[k c] steps :when (= k :cata)] c))
        n (count binds)
        res (fresh "res")
        el (fresh "el")
        body-steps (vec (remove #(= :cata (first %)) steps))
        tuple `[~@binds ~@(map :v catas)]
        body (reduce (fn [code [k a b]]
                       (case k
                         :test `(if ~a ~code r/no-match)
                         :bind `(let [~a ~b] ~code)))
                     tuple (reverse body-steps))]
    (vec (concat
           [[:bind res `(mapv (fn [~@el-syms] ~body) ~@seqs)]
            [:test `(every? (fn [~el] (not (identical? ~el r/no-match))) ~res)]]
           (map-indexed (fn [i b] [:bind (vary-meta b dissoc :tag) `(mapv (fn [~el] (nth ~el ~i)) ~res)]) binds)
           (map-indexed (fn [j c] [:cata (assoc c :v `(mapv (fn [~el] (nth ~el ~(+ n j))) ~res) :depth (inc (:depth c)))]) catas)))))

(defn- match-list
  "模式元素 pats 对齐产生式元素 ps；env：字段名 → 本层的值表达式。"
  [ctx prod pats ps env]
  (let [ps (vec ps) pats (vec pats)
        ei (first (keep-indexed (fn [i p] (when (ellipsis? p) i)) ps))]
    (if-not ei
      (do (when (some #(or (ellipsis? %) (unquote-splicing-form? %)) pats)
            (fail "pattern " (pr-str (apply list pats)) " uses ... where " (pr-str (apply list ps)) " has none"))
          (when (not= (count ps) (count pats))
            (fail "pattern " (pr-str (apply list pats)) " does not fit " (pr-str (apply list ps))))
          (vec (mapcat #(match-elt ctx prod %1 %2 env) pats ps)))
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
            rep-names (rep-fields rep)
            len-sym (fresh "n")
            f0 (first rep-names)
            m (count (filter #(= :item (first %)) mid))
            si (first (keep-indexed (fn [i [k]] (when (= k :splice) i)) mid))
            idx (fn [i] (if (and si (> i si)) `(- ~len-sym ~(- (count mid) i)) i))
            slice (fn [f i] (if (and (zero? i) (= (count mid) 1)) (env f) `(subvec (r/->vec ~(env f)) ~i (- ~len-sym ~(- (count mid) i 1)))))
            steps-mid (vec (mapcat
                             (fn [i [k p]]
                               (if (= k :item)
                                 (match-elt ctx prod p rep (into env (map (fn [f] [f `(nth ~(env f) ~(idx i))]) rep-names)))
                                 (cond
                                   ;; ~x ...：整段绑定
                                   (and (unquote-form? p) (symbol? (second p)))
                                   (cond
                                     (symbol? rep) [[:bind (second p) (slice rep i)]]
                                     (and (seq? rep) (= 'maybe (first rep))) [[:bind (second p) (slice (second rep) i)]]
                                     :else (fail "pattern " (pr-str p) " ... : a whole-part variable needs a single-field ... part, not " (pr-str rep)))
                                   ;; ~[y] ...：整段 catamorphism（depth 1）
                                   (cata-form? p)
                                   (let [f (field-by-name prod (if (symbol? rep) rep (second rep)))]
                                     (when-not (or (symbol? rep) (and (seq? rep) (= 'maybe (first rep))))
                                       (fail "pattern " (pr-str p) " ... needs a single-field ... part, not " (pr-str rep)))
                                     (when (not= :nt (:sort f)) (fail "catamorphism " (pr-str p) " on terminal field " (:name f)))
                                     (cata-step ctx f (slice (:name f) i) 1 p))
                                   ;; 子模式 ...：逐元素
                                   (listy? p)
                                   (let [els (mapv (fn [f] (fresh (str f))) rep-names)
                                         env' (zipmap rep-names els)
                                         inner (match-elt ctx prod p rep env')]
                                     (lift-steps inner els (mapv #(slice % i) rep-names)))
                                   :else (fail "invalid pattern element " (pr-str p) " before ..."))))
                             (range) mid))]
        (vec (concat
               (mapcat #(match-elt ctx prod %1 %2 env) (map second pb) before)
               (mapcat #(match-elt ctx prod %1 %2 env) (map second pa) after)
               ;; 长度：没有整段接入时必须相等；有接入时不少于单项数（只有接入、没有单项时不用测）
               (when (or (not si) (pos? m))
                 [[:bind len-sym `(count ~(env f0))]
                  [:test (if si `(>= ~len-sym ~m) `(== ~len-sym ~m))]])
               steps-mid))))))

(defn- match-production
  "值 v（静态知道是非终结符 nt 的值）匹配产生式模式 pat（列表）。top?：处理器已按标签分派，不用再测类型。"
  [ctx pat nt v & [top?]]
  (let [desc (:desc ctx)
        [_ prod elts pelts] (prod-for-list desc nt pat "pattern")
        node (hinted (fresh "node") (prod-class desc prod))
        env (into {} (map (fn [f] [(:name f) `(. ~node ~(symbol (str "-" (:name f))))]) (:fields prod)))]
    (into (if top? [[:bind node v]] [[:test `(instance? ~(prod-class desc prod) ~v)] [:bind node v]])
          (match-list ctx prod elts pelts env))))

(defn- match-elt [ctx prod p pe env]
  (cond
    (or (symbol? pe) (and (seq? pe) (= 'maybe (first pe))))
    (let [fname (if (symbol? pe) pe (second pe))
          f (field-by-name prod fname)
          v (env fname)]
      (cond
        (cata-form? p) (do (when (not= :nt (:sort f)) (fail "catamorphism " (pr-str p) " on terminal field " fname))
                           (cata-step ctx f v 0 p))
        (unquote-form? p) (if (symbol? (second p)) [[:bind (second p) v]] (fail "invalid pattern variable " (pr-str p)))
        (or (symbol? p) (literal? p)) [[:test `(= ~v '~p)]]
        (and (listy? p) (= :nt (:sort f))) (let [t (fresh "v")] (into [[:bind t v]] (match-production ctx (apply list p) (:of f) t)))
        :else (fail "invalid pattern element " (pr-str p) " for field " fname)))
    (seq? pe)
    (if (listy? p)
      (match-list ctx prod p pe env)
      (fail "pattern element " (pr-str p) " must be a list to fit " (pr-str pe)))
    :else (fail "invalid production element " (pr-str pe))))

;; —— cata 的代码 ——

(defn- cata-code
  "cata 描述 → 绑定（[符号 表达式] …）。"
  [ctx {:keys [outs sort of v maybe depth proc args expected]}]
  (let [;; 结果的元变量说明期望的输出非终结符（上游的规则：,[p1] 找输出 Predicate 的处理器）
        expected (or expected
                     (when (:out-desc ctx)   ; 第一个结果是节点
                       (let [[k o] (meta-of (:out-desc ctx) (first outs))] (when (= k :nt) o))))
        p (if proc
            (or ((:proc-by-name ctx) proc) (fail (:who ctx) ": unknown processor " proc " in a catamorphism"))
            (if (= sort :nt)
              ((:find-proc ctx) of expected (boolean args) (count outs))   ; 显式给了实参时不看作用域
              (or ((:try-find-proc ctx) of nil (boolean args)) (fail (:who ctx) ": no processor for terminal " of))))
        nvals (or (:nvals p) 1)
        n (count outs)
        _ (when (> n nvals) (fail (:who ctx) ": catamorphism binds " n " values but processor " (:name p) " returns " nvals))
        call (fn [x] (let [c (proc-call ctx p x args)]
                       (cond
                         (= n nvals) c
                         (= n 1) `(nth ~c 0)
                         :else `(subvec ~c 0 ~n))))
        one (fn [x] (if maybe
                      (let [t (fresh "m")] `(let [~t ~x] (if (r/absent? ~t) ~(if (> n 1) `(vec (cons ~t (repeat ~(dec n) nil))) t) ~(call t))))
                      (call x)))
        whole (map-depth depth one v)]
    (if (= n 1)
      [[(first outs) whole]]
      (let [t (fresh "vs")]
        (into [[t whole]] (map-indexed (fn [i o] [o (map-depth depth (fn [x] `(nth ~x ~i)) t)]) outs))))))

(defn- build-steps
  "步骤 → 代码：测试失败返回 r/no-match；guard 在 cata 之前、所有绑定之后求值。"
  [ctx steps guard body]
  (let [plain (remove #(= :cata (first %)) steps)
        catas (filter #(= :cata (first %)) steps)
        inner `(let [~@(mapcat (fn [[_ c]] (mapcat identity (cata-code ctx c))) catas)] ~@body)
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

(defn- with-rets
  "处理器有额外返回值时自动子句的值：[节点 返回值的缺省 …]。"
  [ctx node-code]
  (let [rets (:rets ctx)]
    (if (seq rets) `[~node-code ~@rets] node-code)))

(defn- auto-clause
  "输入产生式 prod 的复制子句（输出语言同一非终结符可达同样模式的产生式时），否则 nil。"
  [ctx node prod]
  (let [out (:out-desc ctx) out-nt (:out-nt ctx)]
    (when (and out out-nt)
      (when-let [[ont oprod] (first (filter (fn [[_ p]] (= (:pattern p) (:pattern prod))) (reachable-prods out out-nt)))]
        (when (= (:pattern oprod) (:pattern prod))
          (let [hn (hinted (fresh "node") (prod-class (:in-desc ctx) prod))
                bindings (mapcat (fn [f]
                                   (let [v `(. ~hn ~(symbol (str "-" (:name f))))]
                                     (cond
                                       (= :nt (:sort f))
                                       (cata-code ctx {:outs [(:name f)] :sort :nt :of (:of f) :v v :maybe (:maybe f) :depth (:depth f) :expected (:of f)})
                                       ((:try-find-proc ctx) (:of f) nil)
                                       (cata-code ctx {:outs [(:name f)] :sort :term :of (:of f) :v v :maybe (:maybe f) :depth (:depth f)})
                                       :else [[(:name f) v]])))
                                 (:fields prod))]
            `(let [~hn ~node ~@(mapcat identity bindings)]
               ~(with-rets ctx `(~(ctor out ont oprod) ~@(map :name (:fields prod)))))))))))

(defn- dispatch-code
  "处理器体：ir 按标签（终结符按谓词）选子句。ctx：:in-desc :nt :out-desc :out-nt :find-proc :try-find-proc …；
  auto?：缺的产生式自动生成复制子句。"
  [ctx ir clauses auto? who]
  (let [desc (:in-desc ctx)
        nt (:nt ctx)
        mctx (assoc ctx :desc desc)
        prods (reachable-prods desc nt)
        {:keys [terms]} (lang/nonterminal-tags desc nt)
        clauses (map parse-clause clauses)
        no-clause (fn [what] `(throw (ex-info (str ~(str "nanopass: " who ": no clause matches ") (r/brief ~ir)) {:node ~ir})))
        ;; 某个产生式（或终结符）适用的子句链
        chain (fn [applicable fallback]
                (reduce (fn [code {:keys [steps guard body]}]
                          (if (can-fail? steps guard)
                            (let [rs (fresh "r")]
                              `(let [~rs ~(build-steps ctx steps guard body)] (if (identical? ~rs r/no-match) ~code ~rs)))
                            (build-steps ctx steps guard body)))
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
                            (and (listy? (:pattern c)) (not (unquote-form? (:pattern c))))
                            (let [pat (apply list (:pattern c))
                                  [_ p] (prod-for-list desc nt pat "pattern")]
                              (when (= (:tag p) (:tag prod))
                                {:steps (match-production mctx pat nt ir true) :guard (:guard c) :body (:body c)}))
                            :else nil))
        cases (for [[pnt prod :as pp] prods]
                (let [app (upto (keep #(clause-for-prod pp %) clauses))
                      total? (some #(not (can-fail? (:steps %) (:guard %))) app)
                      ;; 经非终结符交替到达的产生式：交给该非终结符的处理器（上游的规则）；否则复制子句
                      delegate (when (and (not= pnt nt) (not total?)) (when-let [p ((:try-find-proc ctx) pnt (:out-nt ctx))] (proc-call ctx p ir)))
                      auto (when (and auto? (not total?) (not delegate)) (auto-clause ctx ir prod))]
                  ;; 没有子句、输出语言又没有同样的产生式：运行时报错（上游也是运行时；输出非终结符不同的处理器常常只处理一部分产生式）
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
        ;; 终结符：经非终结符交替到达的交给该非终结符的处理器，有该终结符的处理器就交给它（上游的规则），否则（自动模式）原样
        nt-alts (when auto?
                  (keep (fn [p] (when (and (= :nonterminal (:kind p)) (seq (:terms (lang/nonterminal-tags desc (:nt p)))))
                                  (when-let [proc ((:try-find-proc ctx) (:nt p) (:out-nt ctx))] [(:nt p) proc])))
                        (:prods (lang/nonterminal desc nt))))
        term-procs (when auto? (keep (fn [t] (when-let [p ((:try-find-proc ctx) t nil)] [t p])) terms))
        term-fallback (let [base (if (and auto? (seq terms) (:out-desc ctx)) (with-rets ctx ir) (no-clause nil))
                            via-terms (reduce (fn [code [t p]] `(if (~(term-pred desc t) ~ir) ~(with-rets ctx (proc-call ctx p ir)) ~code))
                                              base (reverse term-procs))]
                        (reduce (fn [code [pnt p]] `(if (~(nt-pred desc pnt) ~ir) ~(proc-call ctx p ir) ~code))
                                via-terms (reverse nt-alts)))]
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

(defn- parse-formals
  "[ir x [y 缺省] …] → {:formals [ir x y] :extras [x y] :defaults {y 缺省}}"
  [fs who]
  (when-not (and (vector? fs) (seq fs)) (fail who ": formals must be a non-empty vector, got " (pr-str fs)))
  (let [items (map (fn [f] (cond (symbol? f) [f nil]
                                 (and (vector? f) (= 2 (count f)) (symbol? (first f))) [(first f) [(second f)]]
                                 :else (fail who ": invalid formal " (pr-str f))))
                   fs)]
    {:formals (mapv first items)
     :extras (mapv first (rest items))
     :defaults (into {} (for [[x d] (rest items) :when d] [x (first d)]))}))

(defn- parse-processor
  "(Name InNT [formals] -> OutNT [rets] 子句…) 或 (Name [formals] -> OutNT [rets] 子句…)。输入是终结符或 * 时后面是普通的体。"
  [form in-desc]
  (let [[pname & more] form
        [in-nt more] (if (vector? (first more)) [pname more] [(first more) (rest more)])
        [formals arrow out-nt rets & clauses] more]
    (when-not (and (symbol? pname) (vector? formals) (seq formals) (= '-> arrow) (symbol? out-nt) (vector? rets))
      (fail "invalid processor " (pr-str (take 6 form))))
    (let [kind (cond (= '* in-nt) :any
                     (nil? in-desc) (fail "processor " pname ": the pass has no input language, so the processor's input must be *")
                     (nonterminal? in-desc in-nt) :nt
                     (terminal? in-desc in-nt) :term
                     :else (fail "processor " pname ": " in-nt " is neither a nonterminal nor a terminal of " (short-name in-desc)))
          fs (parse-formals formals (str "processor " pname))]
      (when (and (= kind :nt) (some #(not (vector? %)) clauses))
        (fail "processor " pname ": clauses must be vectors [pattern body …]"))
      (let [p (merge fs {:name pname :in-nt in-nt :kind kind :out-nt out-nt :rets rets :clauses clauses})]
        (assoc p :nvals (proc-nvals p))))))

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

(defn expand-define-pass [pname in-spec formals out-spec rets body mode]
  (binding [*fresh* (fresh-fn)]
    (let [in (parse-lang-spec in-spec)
          out (parse-lang-spec out-spec)
          pformals (parse-formals formals (str "define-pass " pname))
          defs (definitions->bindings (mapcat rest (filter #(and (seq? %) (= 'definitions (first %))) body)))
          procs (mapv #(parse-processor % (:desc in)) (filter processor? body))
          tail (remove #(or (processor? %) (and (seq? %) (= 'definitions (first %)))) body)
          out-desc (:desc out)
          ;; 自动生成的处理器（cata 或复制子句要用、却没声明的）：输入与输出同名的非终结符，形参只有 ir
          generated (atom [])
          proc-by-name (fn [n] (first (filter #(= n (:name %)) procs)))
          ;; 非终结符 nt 的处理器：声明的里面输入为 nt、额外形参在当前作用域里能凑齐的（优先输出也是 nt 的），否则自动生成
          ;; 候选：输入为 nt 的处理器里额外形参凑得齐的（any? 时不看）；优先返回值个数与结果个数相符、输出为期望的非终结符
          choose (fn [ctx nt expected any? nouts]
                   (let [cs (filter #(= nt (:in-nt %)) procs)
                         ok (if any? cs (filter #(satisfiable? ctx %) cs))
                         score (fn [p] (+ (if (and nouts (= nouts (:nvals p))) 2 0)
                                          (if (and expected (= expected (:out-nt p))) 1 0)
                                          (if (and (not expected) (= nt (:out-nt p))) 1 0)))
                         pick (fn [xs] (when (seq xs) (apply max-key score (reverse xs))))]
                     (cond
                       (seq ok) (pick ok)
                       (seq cs) (fail (:who ctx) ": calling processor " (:name (pick cs)) " needs "
                                      (first (remove #(or ((:scope ctx) %) (contains? (:defaults (pick cs)) %)) (:extras (pick cs))))
                                      ", which is not in scope (declare the processor with it)")
                       :else nil)))
          find-proc (fn [ctx nt expected & [any? nouts]]
                      (or (choose ctx nt expected any? nouts)
                          (first (filter #(= nt (:in-nt %)) @generated))
                          (if (and out-desc (nonterminal? out-desc nt))
                            (let [p {:name (symbol (str "auto-" nt)) :in-nt nt :kind :nt :formals '[ir] :extras [] :defaults {} :out-nt nt :rets [] :nvals 1 :clauses [] :auto true}]
                              (swap! generated conj p)
                              p)
                            (fail "define-pass " pname ": no processor for " nt " and the output has no nonterminal " nt))))
          try-find-proc (fn [ctx nt expected & [any? nouts]]
                          (or (choose ctx nt expected any? nouts)
                              (first (filter #(= nt (:in-nt %)) @generated))
                              (when (and out-desc (nonterminal? out-desc nt)) (find-proc ctx nt expected any? nouts))))
          scope-base (set (concat (:formals pformals) (map first (:values defs)) (map first (:fns defs))))
          ctx-for (fn [p]
                    (let [scope (into scope-base (:formals p))
                          ctx {:in-desc (:desc in) :nt (:in-nt p)
                               :out-desc (when (and out-desc (not= '* (:out-nt p))) out-desc)
                               :out-nt (when (not= '* (:out-nt p)) (:out-nt p))
                               :rets (:rets p)
                               :proc-by-name proc-by-name
                               :scope scope
                               :who (str pname " " (:name p))}]
                      (assoc ctx :find-proc (fn [nt expected & [any? nouts]] (find-proc ctx nt expected any? nouts))
                                 :try-find-proc (fn [nt expected & [any? nouts]] (try-find-proc ctx nt expected any? nouts)))))
          out-ctx (fn [p] (when (and out-desc (not= '* (:out-nt p))) {:desc out-desc :nt (:out-nt p)}))
          proc-fn (fn [p]
                    (let [ir (first (:formals p))
                          ctx (ctx-for p)]
                      ;; 立即实现：生成代码时才登记的自动处理器（find-proc）要在下面的循环之前出现
                      (realize
                        (if (= :nt (:kind p))
                          (let [clauses (map (fn [c]
                                               (if (vector? c)
                                                 (let [[pat & b] c] (into [pat] (map #(walk-outs (out-ctx p) %) b)))
                                                 c))
                                             (:clauses p))]
                            `(~(:name p) ~(:formals p)
                              ~(dispatch-code ctx ir clauses true (str pname " " (:name p)))))
                          ;; 终结符或 * 的处理器：普通的体
                          `(~(:name p) ~(:formals p)
                            ~@(map #(walk-outs (out-ctx p) %) (:clauses p)))))))
          ;; 先编显式的处理器（期间可能登记自动处理器），再编自动处理器（它们又可能引出新的）
          explicit (mapv proc-fn procs)
          entry-ctx (when out-desc {:desc out-desc :nt (:nt out)})
          pass-ctx {:scope scope-base :who (str pname) :defaults {}}
          ir (first (:formals pformals))
          entry-proc (when (and in (empty? tail)) (find-proc pass-ctx (:nt in) (:nt out)))
          _ (when (and (nil? in) (empty? tail)) (fail "define-pass " pname ": a pass whose input is * needs a body"))
          autos (loop [done 0 out []]
                  (if (< done (count @generated))
                    (recur (inc done) (conj out (proc-fn (nth @generated done))))
                    out))
          ;; 缺省的体：调入口处理器；它的返回值比 pass 多（处理器有额外返回值）时只取 pass 的那些
          pass-nvals (if out (inc (count rets)) (max 1 (count rets)))
          tail (if (seq tail)
                 tail
                 (let [c (proc-call pass-ctx entry-proc ir) n (:nvals entry-proc)]
                   [(cond (<= n pass-nvals) c
                          (= 1 pass-nvals) `(nth ~c 0)
                          :else `(subvec ~c 0 ~pass-nvals))]))
          main `(let [~@(mapcat (fn [[x e]] [x (walk-outs entry-ctx e)]) (:values defs))]
                  (letfn [~@(map (fn [f] (map #(walk-outs entry-ctx %) f)) (:fns defs))
                          ~@explicit
                          ~@autos]
                    ~@(when in
                        [`(when-not (~(nt-pred (:desc in) (:nt in)) ~ir)
                            (throw (ex-info (str ~(str "nanopass: " pname ": expected " (short-name (:desc in)) " " (:nt in) ", got ") (r/brief ~ir)) {:node ~ir})))])
                    ~@(map #(walk-outs entry-ctx %) tail)))
          ;; defn 的属性表会被求值：语言与非终结符的名字要引起来
          meta-map {:nanopass/input (when in (list 'quote {:lang (symbol (lang-ns (:desc in)) (short-name (:desc in))) :nt (:nt in)}))
                    :nanopass/output (when out-desc (list 'quote {:lang (symbol (lang-ns out-desc) (short-name out-desc)) :nt (:nt out)}))}
          fs (:formals pformals)]
      (realize
        (if (= mode :trace)
          (let [r (fresh "r")]
            `(defn ~pname ~meta-map ~fs
               (println ~(str "|" pname " input:"))
               (prn (lang/unparse-node ~ir))
               (let [~r ~main]
                 (println ~(str "|" pname " output:"))
                 (prn ~(if (and out-desc (empty? rets)) `(lang/unparse-node ~r) r))
                 ~r)))
          `(defn ~pname ~meta-map ~fs ~main))))))

;; —— nanopass-case / with-output-language ——

(defn expand-nanopass-case [spec e clauses]
  (binding [*fresh* (fresh-fn)]
    (let [{:keys [desc nt]} (or (parse-lang-spec spec) (fail "nanopass-case needs [L NT]"))
          self 'nanopass-self
          uses-cata (some cata-form? (tree-seq listy? seq clauses))
          selfp {:name self :extras [] :defaults {} :nvals 1}
          ctx {:in-desc desc :nt nt :who "nanopass-case"
               :proc-by-name (fn [n] nil)
               :try-find-proc (fn [n _ & _] nil)
               :find-proc (fn [n _ & _]
                            (if (= n nt)
                              selfp
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
