;; Clojure 前端（cleat-086，designs/19 D3）：Clojure 形式 → L0，取代 psyntax + (clojure syntax) / (clojure namespaces) + parse-core。
;;
;; 照 Clojure 语义的分析器，没有语法对象：宏 = Var 上的 :macro，以 (&form &env 实参…) 调用；决议直接用 clojure.lang.Namespace；
;; 特殊形式、宿主语法糖、类型提示（Cleat.Core.Compiler.HostSyntax）、:inline 与错误的 phase 照原版 syntax.sls（"原版"指 cleat-093 前仓库里的 Scheme 实现 src/scheme，已删除，见 git 历史） 与
;; namespaces.sls 逐条移植（各函数注明出处）。
;;
;; 产出的 L0 原与"psyntax 展开 + parse-core"的输出相同（designs/19 D5 的 G1）；cleat-092 起去掉了原版的痕迹：
;;   let* → 每个绑定一层 (call (lambda #f (clause (x) #f 体)) 初值)；命名 let（loop*、fn* 子句的 recur 目标）→
;;   (call (letrec ((lp (lambda "loop" / "recur" …))) lp) 初值…)；recur → (call lp 实参…)；try 的处理器是 lambda；
;;   宿主调用的"无泛型实参"是 nil；case* / deftype* 的描述、fn* 的原始签名表是向量（codegen 读它们）。
;;   这些在 cleat-092 清理。局部变量名 g$名字$N$c（与 psyntax 的改名同形，差分工具按 g$ 前缀规范化）。
;; 体（do、let* / fn* 的体、try 的各段）都是表达式的序列（原版的子形式都包在 %ctx 里，经表达式展开器）：
;; 空为 (quote nil)，一项为它本身，多项为 begin；嵌套的 do 是嵌套的 begin，不拼接。
;;
;; 小函数内联（.inl，cleat-042 第 4 项）：cleat-090 起接口文件旁的 .inl 存 L0 子句（[:cleat/l0 子句]）；
;; 影子模式的 G1 在 CLEAT_NO_INLINE=1 下比较。
(ns cleat.compiler.analyzer
  (:refer-clojure :exclude [macroexpand-1])
  (:require [clojure.string :as str]
            [cleat.nanopass :as np :refer [with-output-language out in-context]]
            [cleat.compiler.languages :as l :refer [L0]])
  (:import [Cleat.Core.Compiler HostSyntax AotPaths]
           [Cleat.Core.Expand ExpanderException LocalBinding]
           [clojure.lang Symbol Keyword Var Namespace RT IObj IMeta IPersistentMap IPersistentVector IPersistentSet ISeq IPersistentList]))


;; —— 上下文（syntax.sls 的 ctx / local）——
;; :tail? 尾位置；:loop nil 或 [循环标签 参数个数]；:locals 局部（新的在前）；:fn-name 外层 fn 的类名；:hint def / letfn 给 fn* 的名字；
;; :pos 当前源位置 [行 列 文件或nil]（psyntax 的注解：形式自己的 :line，没有时沿用外层的）

(defrecord Local [sym name tag mutable? kind])   ; sym：Clojure 符号；name：L0 里的变量名；kind：:local :arg :field :this :catch :async :no-await

;; 序号在进程内唯一（不按形式重置）：一个编译单元里有多个形式，codegen 的类型表等按变量名记（原版的 gensym 同样全局唯一，cleat-090）
(def ^:private counter (atom 0))

(defn- fresh-name
  "L0 的局部变量名：g$名字$N$c（psyntax 的改名形状 g$名字$N$作用域；名字是符号的完整写法，限定符号带命名空间）。
  codegen 的 clause-form 按这个形状还原源码里的名字（procedure-form 的常量）。"
  [base]
  (symbol nil (str "g$" base "$" (swap! counter inc) "$c")))

(defn- marker? [l] (#{:async :no-await} (:kind l)))
(defn- without-markers [ls] (remove marker? ls))

(def root-ctx {:tail? false :loop nil :locals () :fn-name nil :hint nil :pos nil})

(defn- ctx-nontail [c] (assoc c :tail? false :loop nil :hint nil))
(defn- ctx-clear [c] (if (:hint c) (assoc c :hint nil) c))
(defn- ctx-no-recur [c tail?] (assoc c :tail? tail? :loop nil :hint nil))
(defn- ctx-with-locals [c locals] (assoc c :locals (concat (reverse locals) (:locals c)) :hint nil))
(defn- ctx-hinted [c nm] (assoc c :tail? false :loop nil :hint nm))
(defn- ctx-no-await [c] (update c :locals conj (->Local (symbol "\u0000no-await") nil nil false :no-await)))

(defn- find-local [ctx csym]
  (let [n (str csym)]
    (first (filter #(and (not (marker? %)) (= n (str (:sym %)))) (:locals ctx)))))

;; —— 小工具 ——

(defn- current-ns [] (.deref RT/CurrentNSVar))
(defn- sym-is? [x n] (and (symbol? x) (nil? (namespace x)) (= n (name x))))
(defn- clj-list? [x] (and (some? x) (or (instance? IPersistentList x) (instance? ISeq x))))
(defn- munge* [s] (HostSyntax/Munge s))
(defn- tag-of [x] (HostSyntax/TagOf x))
(defn- tag-type [t] (when t (HostSyntax/TagType t)))
(defn- type-name [t] (HostSyntax/TypeName t))
(defn- class-for-name [n] (try (RT/classForName ^String n) (catch Exception _ nil)))

(def ^:private kw-line :line)
(def ^:private source-keys [:line :column :file :source-span :end-line :end-column])

(defn- user-meta
  "用户元数据：去掉 reader 的源位置（syntax.sls user-meta）。"
  [x]
  (let [m (meta x)]
    (when m
      (let [m (apply dissoc m source-keys)]
        (when (pos? (count m)) m)))))

(defn source-of
  "形式自己的源位置 [行 列 文件或nil]（syntax-kinds.sls clojure-source-of + syntax-position）；没有 :line 时 nil。"
  [x]
  (when (instance? IMeta x)
    (when-let [m (meta x)]
      (when-let [line (:line m)]
        [(parse-long (str line)) (parse-long (str (or (:column m) 1))) (let [f (:file m)] (when (and f (pos? (count (str f)))) (str f)))]))))

(defn- source-path [] (str (.deref RT/SourcePathVar)))

;; —— 错误（namespaces.sls expander-exception / clojure-error-hook）——

(def ^:private special-names
  #{"def" "if" "do" "let*" "loop*" "recur" "fn*" "quote" "var" "throw" "try" "monitor-enter" "monitor-exit" "." "new"
    "set!" "letfn*" "case*" "deftype*" "reify*" "proxy-class*" "gen-class*" "await*" "import*" "catch" "finally" "&"})

(defn- special-head
  "形式头部的特殊形式名（未限定；import* 也可以是 clojure.core/import*），否则 nil。"
  [x]
  (when (symbol? x)
    (cond
      (and (nil? (namespace x)) (special-names (name x))) (name x)
      (and (= "clojure.core" (namespace x)) (= "import*" (name x))) "import*"
      :else nil)))

(defn- position [form ctx] (or (source-of form) (:pos ctx)))

(defn expander-exception
  ([pos phase msg cause] (expander-exception pos phase msg cause nil))
  ([pos phase msg cause sym]
   (ExpanderException. (or (nth pos 2 nil) (source-path)) (int (or (first pos) 0)) (int (or (second pos) 0))
                       (case phase
                         :macro-syntax-check ExpanderException/PhaseSyntax
                         :macroexpand ExpanderException/PhaseMacroexpand
                         ExpanderException/PhaseCompile)
                       msg cause sym)))

(defn- err
  "语法错误（compile-syntax-check）：位置取形式自己的或上下文的；出错形式是特殊形式时带它的名字（Clojure 只对特殊形式报 compiling recur at）。"
  [form ctx msg]
  (throw (expander-exception (position form ctx) :compile-syntax-check msg nil
                             (when (clj-list? form) (special-head (first form))))))

;; —— 决议（namespaces.sls resolve-in）——
;; → [对象 nil nil] 或 [nil 错误文本 private?]

(defn- namespace-for [^Namespace ns nsname]
  (let [s (symbol nsname)]
    (or (.LookupAlias ns s) (Namespace/find s))))

(defn resolve-in [^Namespace ns ^Symbol sym]
  (let [nsname (namespace sym) nm (name sym)]
    (if nsname
      (let [target (namespace-for ns nsname)]
        (if-not target
          [nil (str "No such namespace: " nsname) false]
          (let [v (.FindInternedVar ^Namespace target (symbol nm))]
            (cond
              (nil? v) [nil (str "No such var: " sym) false]
              (and (not (identical? (.Namespace ^Var v) ns)) (not (.isPublic ^Var v))) [nil (str "var: " sym " is not public") true]
              :else [v nil nil]))))
      (let [dot (str/index-of nm ".")]
        (cond
          (or (and dot (pos? dot)) (str/ends-with? nm "]"))
          (if-let [t (class-for-name nm)] [t nil nil] [nil (str "Unable to resolve classname: " nm) false])
          (= nm "ns") [RT/NSVar nil nil]
          (= nm "in-ns") [RT/InNSVar nil nil]
          :else (if-let [o (.GetMapping ns (symbol nm))]
                  [o nil nil]
                  [nil (str "Unable to resolve symbol: " sym " in this context") false]))))))

;; —— 宿主语法糖（syntax.sls rewrite-to-dot / static-member-of / unresolved-symbol）——

(defn- maybe-type [ctx form]
  (HostSyntax/MaybeType form (boolean (and (symbol? form) (nil? (namespace form)) (find-local ctx form)))))

(defn- static-member-of
  "Type/member：[类型 成员名] 或 nil。"
  [ctx sym]
  (let [nsname (namespace sym)]
    (when (and nsname (not (namespace-for (current-ns) nsname)) (not (HostSyntax/IsPosDigit (name sym))))
      (when-let [t (maybe-type ctx (symbol nsname))] [t (name sym)]))))

(defn transfer-source
  "把形式 old 的 :line :column :source-span :tag 带到 new 上（自己有的不动）。"
  [new old]
  (if-not (instance? IObj new)
    new
    (let [om (meta old)
          nm (reduce (fn [m k] (let [v (get om k)] (if (and v (not (get m k))) (assoc m k v) m))) (or (meta new) {}) [:line :column :source-span :tag])]
      (with-meta new nm))))

(defn- rewrite-to-dot [ctx form where]
  (let [items (vec form) op (first items)]
    (cond
      (sym-is? op ".") form
      (not (symbol? op)) nil
      :else
      (let [nm (name op)]
        (cond
          (and (nil? (namespace op)) (> (count nm) 1) (= \. (nth nm 0)))
          (do (when (< (count items) 2) (err where ctx "Malformed member expression, expecting (.member target ...)"))
              (let [target (nth items 1)
                    target (if (maybe-type ctx target)
                             (with-meta (list 'clojure.core/identity target) {:tag 'System.Type})
                             target)]
                (transfer-source (apply list '. target (symbol (subs nm 1)) (drop 2 items)) form)))
          (and (nil? (namespace op)) (str/last-index-of nm ".") (pos? (str/last-index-of nm ".")) (= (str/last-index-of nm ".") (dec (count nm))))
          (transfer-source (apply list 'new (symbol (subs nm 0 (dec (count nm)))) (rest items)) form)
          (and (namespace op) (static-member-of ctx op))
          (let [[t _] (static-member-of ctx op)
                tsym (symbol (namespace op))]
            (transfer-source
              (cond
                (= nm "new") (apply list 'new tsym (rest items))
                (and (> (count nm) 1) (= \. (nth nm 0)))
                (do (when (< (count items) 2) (err where ctx (str "Malformed method expression, expecting (" op " target ...)")))   ; 措辞同 ClojureCLR
                    (let [tgt (nth items 1)
                          tgt (if (instance? IObj tgt) (with-meta tgt (assoc (meta tgt) :tag (symbol (.FullName ^Type t)))) tgt)]
                      (apply list '. tgt (symbol (subs nm 1)) (drop 2 items))))
                :else (apply list '. tsym (symbol nm) (rest items)))
              form))
          :else nil)))))

(defn- host-sugar? [sym]
  (let [nm (name sym)]
    (and (nil? (namespace sym))
         (or (and (> (count nm) 1) (= \. (nth nm 0)))
             (let [i (str/last-index-of nm ".")] (and i (= i (dec (count nm)))))))))

;; —— 节点的构造 ——

(defmacro ^:private L0-out [tpl] `(with-output-language [L0 ~'Expr] (out ~tpl)))

(defn- q [d] (L0-out (quote ~d)))
(def ^:private q-nil (delay (q nil)))

(defn- mk-clause [params rest body]
  (with-output-language [L0 Clause] (out (clause (~@params) ~rest ~body))))

(defn- mk-lambda [nm clauses] (L0-out (lambda ~nm ~@clauses)))

(defn- mk-let1
  "psyntax 的 let：((lambda (x …) 体) 初值 …)。"
  [names inits body]
  (L0-out (call ~(mk-lambda false [(mk-clause names false body)]) ~@inits)))

(defn- mk-seq
  "build-sequence：一项为它本身，多项为 begin。"
  [es]
  (if (= 1 (count es)) (first es) (L0-out (begin ~@(butlast es) ~(last es)))))

(defn- clj-call [method fixed & args]
  (L0-out (clj-call ~method ~fixed ~@args)))

(defn- var-reference
  "Var 的读（namespaces.sls var-reference）：:const 为常量，否则 clj-var-ref（动态 Var 为 Var.get）。"
  [^Var v]
  (if (get (meta v) :const)
    (q (.get v))
    (L0-out (clj-var-ref ~v ~(boolean (.isDynamic v))))))

(declare analyze analyze-body)

(defn- analyze-nontail [form ctx] (analyze form (ctx-nontail (ctx-clear ctx))))

;; —— 宏调用（namespaces.sls call-macro）——

(def ^:dynamic *env-locals*
  "&env 的来源：当前上下文的局部（clojure-locals-env）。"
  nil)

(defn- locals-env [ctx]
  (let [ls (reverse (without-markers (:locals ctx)))]
    (when (seq ls)
      (reduce (fn [m l]
                (let [s (:sym l) k (:kind l)]
                  (assoc m s (LocalBinding. s s (:tag l) (= k :arg) (= k :field) (= k :this) (boolean (:mutable? l)) (int 0)))))
              {} ls))))

(defn- remove-fn-suffix [^String nm]
  (let [i (.IndexOf nm "__")] (if (neg? i) nm (subs nm 0 i))))

(defn- macro-arity-fix [^Var v ex]
  (if (instance? clojure.lang.ArityException ex)
    (let [^clojure.lang.ArityException ae ex
          nm (.Name ae)]
      (if (= (remove-fn-suffix nm) (str (munge* (str (.Name (.Namespace v)))) "$" (munge* (name (.Symbol v)))))
        (clojure.lang.ArityException. (- (.Actual ae) 2) nm)
        ex))
    ex))

(defn call-macro
  "宏 Var v 展开形式 form（Clojure 数据）；异常按 Clojure 1.12 的 macroexpand1 分阶段。"
  [^Var v form pos ctx]
  (try
    (.applyTo ^clojure.lang.IFn v (RT/cons form (RT/cons (locals-env ctx) (next form))))
    (catch ExpanderException e (throw e))
    (catch Exception ex
      (let [fixed (macro-arity-fix v ex)
            arity? (not (identical? fixed ex))
            ^Exception ex fixed
            op (first form)
            sym (when (and (not arity?) (symbol? op)) (str op))]
        (throw (expander-exception pos
                                   (cond
                                     arity? :compile-syntax-check
                                     (or (instance? ArgumentException ex) (instance? InvalidOperationException ex)
                                         (instance? clojure.lang.IExceptionInfo ex) (= (.GetType ex) Exception))
                                     :macro-syntax-check
                                     :else :macroexpand)
                                   (.Message ex) ex sym))))))

(defn- macro-var-head
  "以宏 Var 对象开头的形式：返回该 Var（别的命名空间的私有宏报错），否则 nil。"
  [form ctx]
  (when (instance? ISeq form)
    (let [op (first form)]
      (when (and (instance? Var op) (.IsMacro ^Var op))
        (when (and (not (identical? (.Namespace ^Var op) (current-ns))) (not (.isPublic ^Var op)))
          (err form ctx (str "Var: " op " is not public")))
        op))))

;; —— 字面量与集合（syntax.sls datum-expression / const-value）——

(def ^:private no-const (Object.))

(defn- const-value [d]
  (cond
    (symbol? d) no-const
    (nil? d) d
    (or (instance? clojure.lang.IRecord d) (instance? clojure.lang.IType d)) d
    (clj-list? d) (cond
                    (empty? d) d
                    (and (sym-is? (first d) "quote") (= 2 (count d))) (second d)
                    :else no-const)
    (instance? IPersistentMap d) (if (empty? d) d
                                     (let [kvs (mapcat (fn [[k v]] [(const-value k) (const-value v)]) d)]
                                       (if (some #(identical? no-const %) kvs) no-const
                                           (with-meta (let [a (object-array kvs)]
                                                        (if (<= (alength a) 16) (clojure.lang.PersistentArrayMap/createAsIfByAssoc a) (clojure.lang.PersistentHashMap/create a)))
                                                      (meta d)))))
    (instance? IPersistentVector d) (if (empty? d) d
                                        (let [vs (mapv const-value d)]
                                          (if (some #(identical? no-const %) vs) no-const (with-meta vs (meta d)))))
    (instance? IPersistentSet d) (if (empty? d) d
                                     (let [vs (map const-value d)]
                                       (if (some #(identical? no-const %) vs) no-const
                                           (with-meta (clojure.lang.PersistentHashSet/create (object-array vs)) (meta d)))))
    :else d))

(defn- coll-kind [d]
  (cond (instance? IPersistentVector d) "VectorOf" (instance? IPersistentMap d) "MapOf" (instance? IPersistentSet d) "SetOf" :else nil))

(defn- coll-items [d]
  (if (instance? IPersistentMap d) (mapcat (fn [e] [(key e) (val e)]) d) (seq d)))

(defn- analyze-datum [d ctx]
  (cond
    (nil? d) (q nil)
    (instance? Namespace d) (clj-call "FindNamespaceOf" false (q (.Name ^Namespace d)))
    (or (coll-kind d) (and (clj-list? d) (empty? d)))
    (let [v (const-value d)
          m (user-meta d)
          pos (position d ctx)
          sub (assoc (ctx-nontail ctx) :pos pos)
          node (if (identical? v no-const)
                 (apply clj-call (coll-kind d) 0 (mapv #(analyze % sub) (coll-items d)))
                 (q (if (instance? IObj v) (with-meta v nil) v)))]
      ;; 元数据：常量就连同集合一起作常量；否则（cleat-065，JVM 的 MetaExpr）在字面量的词法上下文里求值再 withMeta
      (cond
        (nil? m) node
        :else (let [mv (const-value m)]
                (cond
                  (and (not (identical? v no-const)) (not (identical? mv no-const))) (q (with-meta (if (instance? IObj v) (with-meta v nil) v) mv))
                  (identical? mv no-const) (clj-call "WithMetaOf" false node (analyze m sub))
                  :else (clj-call "WithMetaOf" false node (q mv))))))
    :else (q d)))

;; —— 体（syntax.sls body-stx）——

(defn analyze-body
  "体：空为 (quote nil)，一项为它在 ctx 里的分析，多项为 (begin 非尾… 最后一项)。"
  [forms ctx]
  (cond
    (empty? forms) (q nil)
    (empty? (rest forms)) (analyze (first forms) ctx)
    ;; 从左到右立即分析（宏展开与 deftype* 的桩类型等副作用按源码顺序发生）
    :else (let [init (mapv #(analyze % (ctx-nontail ctx)) (butlast forms))
                lst (analyze (last forms) ctx)]
            (mk-seq (conj init lst)))))

;; —— 符号（namespaces.sls 的 ns-lookup + 绑定种类，syntax.sls unresolved-symbol）——

(defn- type-reference [t]
  (if (HostSyntax/IsStub t)
    (clj-call "RealType" false (q t))
    (q t)))

(defn- analyze-symbol [sym ctx]
  (if-let [l (and (nil? (namespace sym)) (find-local ctx sym))]
    (:name l)
    (let [[o msg private?] (resolve-in (current-ns) sym)]
      (cond
        (instance? Var o) (if (.IsMacro ^Var o)
                            (err sym ctx (str "Can't take the value of a macro: " o))
                            (var-reference o))
        (instance? Type o) (type-reference o)
        (some? o) (err sym ctx (str "Unable to resolve symbol: " sym " in this context"))
        private? (err sym ctx msg)
        (and (namespace sym) (HostSyntax/MaybeArrayType sym)) (type-reference (HostSyntax/MaybeArrayType sym))
        (and (namespace sym) (static-member-of ctx sym))
        (let [[t m] (static-member-of ctx sym)]
          (clj-call "CallStatic" 3 (q t) (q (munge* m)) (q nil)))
        :else (err sym ctx msg)))))

;; —— 调用（syntax.sls application / var-call，namespaces.sls default-var-call）——

(defn- invoke-node [f args] (L0-out (clj-invoke ~f ~@args)))

(defn- prim-kind [t]
  (when (and (symbol? t) (nil? (namespace t)))
    (case (name t) "long" "L" "double" "D" nil)))

(defn- prim-sig
  "原始签名（cleat-042）：形参与返回的 ^long / ^double 各一个字母，其余 O；至少一个原始类型、不超过 4 个形参、
  clojure.lang.primifs 里有这个接口时返回签名，否则 nil。"
  [vec params rettag]
  (let [sig (apply str (concat (map #(or (prim-kind (tag-of %)) "O") params)
                               [(or (prim-kind (tag-of vec)) (prim-kind rettag) "O")]))]
    (when (and (<= (count params) 4) (some #(not= \O %) sig) (class-for-name (str "clojure.lang.primifs." sig)))
      sig)))

(defn- var-rettag
  "Var 元数据里的返回类型提示：defn 把 (defn ^long f …) 的 :tag 当表达式求值了，存的是函数 clojure.core/long 本身（JVM 也如此），
  这里认回符号 long / double——函数类按名字上的提示实现 primifs.LL（cleat-096）；JVM 在这里会 AbstractMethodError。"
  [vm]
  (let [t (:tag vm)]
    (cond
      (symbol? t) t
      (identical? t @#'clojure.core/long) 'long
      (identical? t @#'clojure.core/double) 'double
      :else nil)))

(defn- var-prim-sig [vm n]
  (let [al (:arglists vm)]
    (when (instance? ISeq (seq al))
      (some (fn [v]
              (when (and (instance? IPersistentVector v) (= (count v) n)
                         (not (some #(sym-is? % "&") v)))
                (prim-sig v (seq v) (var-rettag vm))))
            al))))

(def ^:private opt-known-calls? (not (.Contains (or (System.Environment/GetEnvironmentVariable "CLEAT_NO_OPT") "") "known")))   ; cleat-110

(defn- known-call-class
  "用户函数的已知调用（cleat-110）：非动态的 Var、当前值是 AFunction 子类、该类有参数个数为 n 的公开静态 invokeStatic（self 加 n 个 object，
  即没有闭包字段的固定子句）、类型能按名字引用时返回该类，否则 nil。调用时由 isinst 守卫，重定义照常生效。"
  [^Var v n]
  (when (and opt-known-calls? (not (.isDynamic v)) (.hasRoot v))
    (let [r (.getRawRoot v)]
      (when (instance? clojure.lang.AFunction r)
        (let [^Type t (class r)
              m (.GetMethod t "invokeStatic" ^|System.Type[]| (into-array Type (repeat (inc n) Object)))]
          (when (and m (.IsStatic m) (.IsPublic t) (not (.IsNested t)) (not (.IsGenericType t)))
            t))))))

(defn- direct-link-target [v n]
  (let [r (AotPaths/DirectLinkTarget v n)] (when (string? r) r)))

;; —— 调用点内联接口文件里的小函数（cleat-042 第 4 项，原版 syntax.sls inline-target / inline-call；Clojure 编译器的 .inl 是
;; L0 子句，cleat-090）：((lambda (r t …) (if (IsInstanceOf 函数类 r) ((lambda 子句) t …) (r t …))) Var读 实参 …)。
;; 守卫保证 with-redefs / 重定义照常生效；实参只求值一次。子句的变量逐一改名（同一个体内联多次时名字不重复）；形参与实参临时变量带
;; $inl 后缀（编译器不把它们推断为 double）。构建 Clojure 镜像时关闭（各级镜像的接口文件相同，不动点成立）；CLEAT_NO_INLINE 时不内联。

(def inline-calls? (atom (nil? (System.Environment/GetEnvironmentVariable "CLEAT_NO_INLINE"))))

(def ^:private inline-cache (atom {}))

(defn- find-type-named [^String cls]
  (let [comma (.IndexOf cls ",")
        tn (if (neg? comma) cls (.Trim (subs cls 0 comma)))
        an (when-not (neg? comma) (.Trim (subs cls (inc comma))))]
    (some (fn [^System.Reflection.Assembly a]
            (when (or (nil? an) (= an (.Name (.GetName a)))) (.GetType a tn false)))
          (.GetAssemblies System.AppDomain/CurrentDomain))))

(defn- inline-target
  "Var 是接口文件里的已知函数、有参数个数为 n 的可内联子句（Clojure 编译器写的 L0 子句）时返回 [函数类 子句的 S 表达式]，否则 nil。"
  [^Var v n]
  (let [e (Cleat.Core.Runtime.InterfaceFile/FindKnown (symbol (str (.Name (.Namespace v))) (str (.Symbol v))))
        bytes (when e (Cleat.Core.Runtime.InterfaceFile/FindInline e (int n)))]
    (when bytes
      (let [k [e n]]
        (if (contains? @inline-cache k)
          (@inline-cache k)
          (let [r (try
                    (let [data (Cleat.Core.Runtime.Fasl/Read ^bytes bytes)]
                      ;; 别的格式的 .inl 不认
                      (when (and (vector? data) (= :cleat/l0 (first data)))
                        (when-let [t (find-type-named (.-ClassName e))] [t (second data)])))
                    (catch Exception _ nil))]
            (swap! inline-cache assoc k r)
            r))))))

(defn- inl-name [^String old suffix]
  ;; g$名字$N$c → g$名字$M$后缀
  (let [parts (str/split old #"\$")]
    (symbol nil (str "g$" (nth parts 1 "x") "$" (swap! counter inc) "$" suffix))))

(defn- alpha-rename-clause
  "子句 S 表达式 (clause (x …) x body) 里的局部变量（分析器的临时名 g$…$c）逐一改名；子句的形参带 $inl 后缀。quote 里的不动。"
  [cl]
  (let [env (volatile! {})
        params (set (concat (second cl) (when (symbol? (nth cl 2)) [(nth cl 2)])))
        temp? (fn [x] (and (symbol? x) (nil? (namespace x)) (str/starts-with? (name x) "g$")))
        rn (fn [x] (or (@env x) (let [n (inl-name (name x) (if (params x) "inl" "c"))] (vswap! env assoc x n) n)))
        walk (fn walk [x]
               (cond
                 (temp? x) (rn x)
                 (and (seq? x) (= 'quote (first x))) x
                 (seq? x) (apply list (map walk x))
                 :else x))]
    (walk cl)))

(defn- inline-call [[type cl] v args & [prim-sig]]
  (let [lam (np/parse L0 (list 'lambda false (alpha-rename-clause cl)))
        r (fresh-name "r")
        ts (mapv (fn [_] (inl-name "g$t$0$c" "inl")) args)
        ;; 有原始签名的函数（cleat-123）：守卫不成立时与不内联一样走 PrimInvoke（两支都是原始值，语义相同）
        slow (if prim-sig
               (apply clj-call "PrimInvoke" false (q prim-sig) r ts)
               (L0-out (clj-invoke ~r ~@ts)))]
    (L0-out (call (lambda false (clause (~r ~@ts) false
                                  (if (clj-call "IsInstanceOf" false (quote ~type) ~r)
                                    (call ~lam ~@ts)
                                    ~slow)))
                  ~(var-reference v) ~@args))))

(defn- var-call [form ^Var v ctx]
  (let [args (rest form)
        n (count args)
        vm (meta v)
        inline (:inline vm)
        arities (:inline-arities vm)
        nt (ctx-nontail ctx)
        pos (position form ctx)]
    (cond
      (and inline (instance? clojure.lang.IFn inline) (or (not arities) (arities n)))
      (do (when (and (not (identical? (.Namespace v) (current-ns))) (not (.isPublic v)))
            (err form ctx (str "var: " v " is not public")))
          (analyze (transfer-source (.applyTo ^clojure.lang.IFn inline (next form)) form) (assoc ctx :pos pos)))
      (and (= n 2) (= (str v) "#'clojure.core/instance?") (maybe-type ctx (first args)))
      (clj-call "IsInstanceOf" false (q (maybe-type ctx (first args))) (analyze (second args) (assoc nt :pos pos)))
      ;; 只在 fn 体里内联（顶层形式只运行一次：留给简单解释，调用帧也照旧）；实参是 NaN 字面量时不内联——形参本是对象，
      ;; 同一个常量对象 (= a b) 为真，内联后常量传播成原始 double 的比较就为假（cleat-116）。
      ;; 有原始签名的函数也先内联（子句带着与 invokePrim 相同的转换，cleat-123），否则走 PrimInvoke
      (and @inline-calls? (:fn-name ctx) (not (some #(and (instance? Double %) (Double/IsNaN %)) args)) (inline-target v n))
      (inline-call (inline-target v n) v (mapv #(analyze % (assoc nt :pos pos)) args) (when (<= 1 n 4) (var-prim-sig vm n)))
      (and (<= 1 n 4) (var-prim-sig vm n))
      (apply clj-call "PrimInvoke" false (q (var-prim-sig vm n)) (var-reference v) (mapv #(analyze % (assoc nt :pos pos)) args))
      :else
      (if-let [cls (direct-link-target v n)]
        (L0-out (clj-static-call ~cls ~n ~(var-reference v) ~@(mapv #(analyze % (assoc nt :pos pos)) args)))
        (if-let [kc (known-call-class v n)]
          ;; cleat-110：Var 的当前值是有 invokeStatic 的函数类——codegen 在可以引用该类时生成 isinst 守卫后的直接调用
          (apply clj-call "KnownCall" false (q kc) (var-reference v) (mapv #(analyze % (assoc nt :pos pos)) args))
          (invoke-node (var-reference v) (mapv #(analyze % (assoc nt :pos pos)) args)))))))

(declare analyze-special)

(defn- analyze-seq [form ctx]
  (let [pos (position form ctx)
        ctx (assoc ctx :pos pos)
        op (first form)]
    (cond
      (special-head op) (analyze-special (special-head op) form ctx)
      (symbol? op)
      (if-let [l (and (nil? (namespace op)) (find-local ctx op))]
        (invoke-node (:name l) (mapv #(analyze % (ctx-nontail ctx)) (rest form)))
        (let [[o msg private?] (resolve-in (current-ns) op)]
          (cond
            (and (instance? Var o) (.IsMacro ^Var o))
            (analyze (let [x (call-macro o form pos ctx)] x) (assoc ctx :pos pos))
            (instance? Var o) (var-call form o ctx)
            (instance? Type o) (invoke-node (type-reference o) (mapv #(analyze % (ctx-nontail ctx)) (rest form)))
            (some? o) (err form ctx (str "Unable to resolve symbol: " op " in this context"))
            private? (err op ctx msg)
            (and (namespace op) (HostSyntax/MaybeArrayType op))
            (invoke-node (type-reference (HostSyntax/MaybeArrayType op)) (mapv #(analyze % (ctx-nontail ctx)) (rest form)))
            (or (and (namespace op) (static-member-of ctx op)) (host-sugar? op))
            (analyze (or (rewrite-to-dot ctx form form) form) ctx)
            :else (err op ctx msg))))
      :else
      (let [nt (ctx-nontail ctx)]
        (when (nil? op) (err form ctx (str "Can't call nil, form: " (RT/printString form))))
        (cond
          (macro-var-head form ctx) (analyze (call-macro (macro-var-head form ctx) form pos ctx) ctx)
          (and (keyword? op) (<= 1 (count (rest form)) 2))
          (let [as (mapv #(analyze % nt) (rest form))]
            (L0-out (clj-keyword-invoke ~op ~(first as) ~@(rest as))))
          :else (invoke-node (analyze op nt) (mapv #(analyze % nt) (rest form))))))))

(defn analyze
  "Clojure 形式 → L0 的 Expr。"
  [form ctx]
  (cond
    (symbol? form) (analyze-symbol form ctx)
    (and (clj-list? form) (seq form)) (analyze-seq form ctx)
    :else (analyze-datum form ctx)))

;; —— 特殊形式（syntax.sls，逐个对照）——

(defn- source-file [pos] (or (nth pos 2 nil) (source-path)))

(defn- def-var [form ctx ^Namespace ns sym]
  (let [nsname (namespace sym) nm (symbol (name sym))]
    (if nsname
      (let [target (namespace-for ns nsname)]
        (cond
          (nil? target) (err form ctx "Can't refer to qualified var that doesn't exist")
          (identical? target ns) (.intern ns nm)
          (.FindInternedVar ^Namespace target nm) (err form ctx "Can't create defs outside of current ns")
          :else (err form ctx "Can't refer to qualified var that doesn't exist")))
      (let [o (.GetMapping ns nm)]
        (cond
          (nil? o) (.intern ns nm)
          (and (instance? Var o) (identical? (.Namespace ^Var o) ns)) o
          (instance? Var o) (.intern ns nm)
          :else (err form ctx (str "Expecting var, but " sym " is mapped to " o)))))))

(defn- sp-def [form ctx]
  (let [items (vec (rest form))
        build (fn [nsym doc init?]
                (let [sym nsym]
                  (when-not (symbol? sym) (err form ctx "First argument to def must be a Symbol"))
                  (let [ns (current-ns)
                        ^Var v (def-var form ctx ns sym)
                        sm (meta sym)
                        dynamic? (boolean (:dynamic sm))
                        pos (position form ctx)
                        n (name sym)]
                    (when dynamic? (.setDynamic v))
                    (when (and (not dynamic?) (> (count n) 2) (str/starts-with? n "*") (str/ends-with? n "*"))
                      (.WriteLine ^System.IO.TextWriter (RT/errPrintWriter)
                                  (str "Warning: " sym " not declared dynamic and thus is not dynamically rebindable, "
                                       "but its name suggests otherwise. Please either indicate ^:dynamic " sym
                                       " or change the name. (" (source-file pos) ":" (if pos (first pos) 0) ")")))
                    ;; 分析期先把 :arglists（与名字上的原始返回类型提示 :tag，cleat-096）放上 Var：体里的自调用据此选 primifs 签名，
                    ;; 要与函数类实现的一致（函数类用 :rettag，就是这个 :tag）
                    (when-let [al (:arglists sm)]
                      (.setMeta v (cond-> (assoc (meta v) :arglists (second al))
                                    (prim-kind (:tag sm)) (assoc :tag (:tag sm)))))
                    (let [m (cond-> (assoc sm :line (long (if pos (first pos) 0)) :column (long (if pos (second pos) 0)) :file (source-file pos))
                              doc (assoc :doc doc))
                          init-node (if init? (analyze (nth items (if doc 2 1)) (ctx-hinted ctx (name sym))) (q nil))
                          meta-node (analyze m (assoc (ctx-nontail ctx) :pos nil))]
                      (L0-out (clj-def ~v ~(boolean init?) ~dynamic? ~init-node ~meta-node))))))]
    (case (count items)
      1 (build (items 0) nil false)
      2 (build (items 0) nil true)
      3 (if (string? (items 1)) (build (items 0) (items 1) true) (err form ctx "Too many arguments to def"))
      0 (err form ctx "Too few arguments to def")
      (err form ctx "Too many arguments to def"))))

(defn- sp-if [form ctx]
  (let [items (vec (rest form))
        c (ctx-clear ctx)
        nt (ctx-nontail c)]
    (case (count items)
      2 (L0-out (if ~(analyze (items 0) nt) ~(analyze (items 1) c) ~(q nil)))
      3 (L0-out (if ~(analyze (items 0) nt) ~(analyze (items 1) c) ~(analyze (items 2) c)))
      (0 1) (err form ctx "Too few arguments to if")
      (err form ctx "Too many arguments to if"))))

(defn- sp-do [form ctx] (analyze-body (rest form) (ctx-clear ctx)))

(defn- sp-quote [form ctx]
  (if (= 2 (count form))
    (q (second form))
    (err form ctx (str "Wrong number of args (" (dec (count form)) ") passed to quote"))))

(defn- lookup-var [form ctx sym]
  (let [ns (current-ns)]
    (cond
      (namespace sym) (when-let [target (namespace-for ns (namespace sym))] (.FindInternedVar ^Namespace target (symbol (name sym))))
      (sym-is? sym "ns") RT/NSVar
      (sym-is? sym "in-ns") RT/InNSVar
      :else (let [o (.GetMapping ^Namespace ns sym)]
              (cond
                (nil? o) nil
                (instance? Var o) o
                :else (err form ctx (str "Expecting var, but " sym " is mapped to " o)))))))

(defn- sp-var [form ctx]
  (let [sym (second form)]
    (when-not (and (= 2 (count form)) (symbol? sym)) (err form ctx "var requires a symbol"))
    (let [v (lookup-var form ctx sym)]
      (when-not v (err form ctx (str "Unable to resolve var: " sym " in this context")))
      (q v))))

(defn- sp-throw [form ctx]
  (case (count form)
    2 (L0-out (throw ~(analyze (second form) (ctx-nontail ctx))))
    1 (err form ctx "Too few arguments to throw, throw expects a single Exception instance")
    (err form ctx "Too many arguments to throw, throw expects a single Exception instance")))

(defn- sp-monitor [nm method]
  (fn [form ctx]
    (if (= 2 (count form))
      (clj-call method false (analyze (second form) (ctx-nontail ctx)))
      (err form ctx (str nm " requires one argument")))))

(defn- sp-import* [form ctx]
  (let [nm (second form)]
    (when-not (and (= 2 (count form)) (string? nm)) (err form ctx "import* requires a string"))
    (let [t (class-for-name nm)]
      (when-not t (err form ctx (str "Unable to resolve classname: " nm)))
      (clj-call "ImportOf" false (q t)))))

;; let* / loop*

(defn- binding-pairs [form ctx bindings pairs-msg]
  (when-not (vector? bindings) (err form ctx "Bad binding form, expected vector"))
  (when-not (even? (count bindings)) (err form ctx pairs-msg))
  (for [[sym init] (partition 2 bindings)]
    (do (when-not (symbol? sym) (err form ctx (str "Bad binding form, expected symbol, got: " sym)))
        (when (namespace sym) (err form ctx (str "Can't let qualified name: " sym)))
        (when (sym-is? sym "&") (err form ctx "Can't use & as a local binding"))
        [sym init])))

(declare host-form-type)

(defn- prim-field-type
  "deftype / defrecord 字段的原始类型（cleat-112）：^long / ^double 提示的字段编成 Int64 / Double 字段（JVM 同样），其余 nil（object）。"
  [f]
  (when (#{"System.Int64" "System.Double"} (:type f)) (:type f)))

(defn- init-tag
  "没有提示的 let 局部取初值的类（(new T …)、(T. …)、带 ^T 提示的初值，以及 host-form-type 看得出的调用形式；只取引用类型）。"
  [ctx d]
  (let [t (cond
            (tag-of d) (tag-type (tag-of d))
            (and (clj-list? d) (seq d))
            (let [h (first d)]
              (cond
                (sym-is? h "new") (when (> (count d) 1) (maybe-type ctx (second d)))
                (and (symbol? h) (nil? (namespace h)))
                (let [n (name h) k (count n)]
                  (if (and (> k 1) (= \. (nth n (dec k))) (not= \. (nth n 0)) (not (find-local ctx h)))
                    (maybe-type ctx (symbol (subs n 0 (dec k))))
                    ;; cleat-112：其余调用形式取 host-form-type（宿主调用的返回类型、Var 的 :tag、:inline 展开后的形式——JVM 的 LocalBinding 同样取初值的类），
                    ;; 例如 (let [a (object-array n)] (aset a i v)) 的 a 是 Object[]，其后的 aset 静态决议
                    (host-form-type ctx d)))
                :else (host-form-type ctx d)))
            :else nil)]
    (when (and t (not (.IsValueType ^Type t)) (not (.ContainsGenericParameters ^Type t)))
      (symbol (.FullName ^Type t)))))

(defn- make-local* [csym kind name] (->Local csym name (tag-of csym) false kind))

(defn- dedupe-names
  "绑定名对应的 L0 名字；重复的绑定名（Clojure 取最后一个）前面的同名者换成临时名（syntax.sls dedupe-ids：
  generate-temporaries，基名 t）。"
  [syms]
  (let [v (vec syms)]
    (vec (map-indexed (fn [i s] (fresh-name (if (some #(= (str s) (str %)) (subvec v (inc i))) "t" (str s)))) v))))

(defn- named-let
  "psyntax 的命名 let：((letrec ((lp (lambda (y …) 体))) lp) 初值 …)，lambda 的名字为标签的原名（\"loop\" / \"recur\"）。"
  [lp lp-base ys inits body]
  (L0-out (call (letrec ([~[lp] ~[(mk-lambda lp-base [(mk-clause ys false body)])]] ...) ~lp) ~@inits)))

(defn- sp-let [loop?]
  (fn [form ctx]
    (let [bindings (second form)]
      (when-not (>= (count form) 2) (err form ctx "Bad binding form, expected vector"))
      (let [pairs (vec (binding-pairs form ctx bindings "Bad binding form, expected matched symbol expression pairs"))
            c (ctx-clear ctx)
            ;; 每个初值在非尾位置、能看到之前的绑定。分析顺序照 psyntax：let 是 ((lambda (x) 体) 初值)，应用先展开运算符——
            ;; 先分析体，再从最后一个绑定往前分析初值（报哪个错误、宏的副作用都按这个顺序）
            [locals names]
            (reduce (fn [[locals names] [s init]]
                      (let [nm (fresh-name (str s))
                            it (and (not loop?) (not (tag-of s)) (init-tag (ctx-with-locals c locals) init))
                            lc (if it (->Local s nm it false :local) (make-local* s :local nm))]
                        [(conj locals lc) (conj names nm)]))
                    [[] []] pairs)
            analyze-inits (fn []
                            (vec (reverse (for [i (reverse (range (count pairs)))]
                                            (analyze (second (pairs i)) (ctx-nontail (ctx-with-locals c (subvec locals 0 i))))))))
            forms (drop 2 form)]
        (if loop?
          (let [lp (fresh-name "loop")
                ys (dedupe-names (map first pairs))
                inner (mapv (fn [l y] (assoc l :name y)) locals ys)
                bc (-> (ctx-with-locals c inner) (assoc :tail? true :loop [lp (count pairs)] :hint nil))
                body (analyze-body forms bc)
                lbody (named-let lp "loop" ys names body)
                inits (analyze-inits)]
            (if (empty? pairs)
              (mk-let1 [] [] lbody)
              (reduce (fn [b [nm i]] (mk-let1 [nm] [i] b)) lbody (reverse (map vector names inits)))))
          (let [bc (ctx-with-locals c locals)
                body (analyze-body forms bc)
                inits (analyze-inits)]
            (if (empty? pairs)
              (mk-let1 [] [] body)
              (reduce (fn [b [nm i]] (mk-let1 [nm] [i] b)) body (reverse (map vector names inits))))))))))

(defn- sp-recur [form ctx]
  (let [args (rest form)]
    (cond
      (not (:tail? ctx)) (err form ctx "Can only recur from tail position")
      (not (:loop ctx)) (err form ctx "Cannot recur across try")
      (not= (count args) (second (:loop ctx)))
      (err form ctx (str "Mismatched argument count to recur, expected: " (second (:loop ctx)) " args, got: " (count args)))
      :else (L0-out (call ~(first (:loop ctx)) ~@(mapv #(analyze % (ctx-nontail ctx)) args))))))

;; fn*

(defn- fn-params
  "参数向量 → [必需参数 剩余参数或nil]（检查与 C# ParseFnClause 相同）。"
  [form ctx params]
  (when-not (vector? params) (err form ctx "Parameter declaration should be a vector"))
  (loop [ps (seq params) req [] rest nil amp false]
    (if-not ps
      (do (when (and amp (not rest)) (err form ctx "Invalid parameter list"))
          (when (> (count req) 20) (err form ctx "Can't specify more than 20 params"))
          [req rest])
      (let [p (first ps)]
        (when-not (symbol? p) (err form ctx "fn params must be Symbols"))
        (when (namespace p) (err form ctx (str "Can't use qualified name as parameter: " p)))
        (cond
          (sym-is? p "&") (do (when amp (err form ctx "Invalid parameter list")) (recur (next ps) req rest true))
          amp (do (when rest (err form ctx "Unexpected parameter"))
                  (when (tag-of p) (err form ctx "& arg cannot have type hint"))
                  (recur (next ps) req p amp))
          :else (recur (next ps) (conj req p) rest amp))))))

(def ^:private rt-type (delay (class-for-name "clojure.lang.RT")))

(defn- prim-param-init
  "^long / ^double 形参：循环变量的初值经 RT.longCast / doubleCast（cleat-042）；其余原样。"
  [p outer-name]
  (let [t (tag-of p)
        op (when (and (symbol? t) (nil? (namespace t))) (case (name t) "long" "longCast" "double" "doubleCast" nil))]
    (if op
      (clj-call "CallStatic" 3 (q @rt-type) (q op) (q nil) outer-name)
      outer-name)))

(defn- fn-class-name [ctx self]
  (let [base (or (:fn-name ctx) (munge* (str (.Name ^Namespace (current-ns)))))
        simple (cond
                 self (munge* (name self))
                 (:hint ctx) (munge* (:hint ctx))
                 :else (str "fn__" (RT/nextID)))]
    (str base "$" simple)))

(defn- runtime-async? [] (Cleat.Core.Runtime.AsyncSupport/RuntimeAsync))

(declare expand-fn*)

(defn- async-body [form bc forms]
  (let [thunk (with-meta (apply list 'fn* [] forms) nil)
        node (expand-fn* thunk true bc)]
    (clj-call (if (runtime-async?) "AsyncStart" "AsyncRunSync") false node)))

(defn- expand-fn* [form thunk? ctx]
  (let [c ctx
        async? (and (not thunk?)
                    (boolean (or (:async (meta form)) (and (clj-list? form) (:async (meta (first form)))))))
        rest-items (rest form)
        self (when (symbol? (first rest-items)) (first rest-items))
        rest-items (if self (rest rest-items) rest-items)
        single? (vector? (first rest-items))
        clauses (if single? [rest-items] rest-items)
        clause-of (fn [cl]
                    (if single?
                      cl
                      (do (when-not (and (clj-list? cl) (seq cl)) (err form ctx (str "Bad fn clause: " cl)))
                          cl)))
        nm (fn-class-name c self)
        self-name (when self (fresh-name (str self)))
        outer (if self [(->Local self self-name nil false :this)] [])
        base-locals (concat (reverse outer) (when thunk? [(->Local (symbol "\u0000async") nil nil false :async)]) (without-markers (:locals c)))
        base-ctx {:tail? true :loop nil :locals base-locals :fn-name nm :hint nil :pos (:pos c)}
        prims (volatile! [])
        built (loop [cls (seq clauses) fixed [] variadic nil arities []]
                (if-not cls
                  (do (when (and variadic (seq arities) (>= (apply max arities) (inc (first variadic))))
                        (err form ctx "Can't have fixed arity function with more params than variadic function"))
                      (concat fixed (when variadic [(second variadic)])))
                  (let [cl (clause-of (first cls))
                        [req rst] (fn-params form ctx (first cl))
                        n (count req)
                        params (if rst (conj req rst) req)]
                    (cond
                      (and rst variadic) (err form ctx "Can't have more than 1 variadic overload")
                      (and (not rst) (some #{n} arities)) (err form ctx "Can't have 2 overloads with same arity"))
                    (when-not (or rst async? thunk?)
                      (when-let [sig (prim-sig (first cl) req (:rettag (meta form)))]
                        (vswap! prims conj [n sig])))
                    (let [outer-names (dedupe-names params)
                          lp (fresh-name "recur")
                          inner-names (dedupe-names params)
                          locals (mapv (fn [p y] (make-local* p :arg y)) params inner-names)
                          bc (-> base-ctx (ctx-with-locals locals) (assoc :tail? true :loop [lp (count params)] :fn-name nm :hint nil))
                          body (if async? (async-body form (assoc bc :loop nil) (rest cl)) (analyze-body (rest cl) bc))
                          inits (mapv prim-param-init params outer-names)
                          clause (mk-clause outer-names false (named-let lp "recur" inner-names inits body))]
                      (if rst
                        (recur (next cls) fixed [n clause] arities)
                        (recur (next cls) (conj fixed clause) variadic (conj arities n)))))))
        variadic-n (let [cls (seq clauses)]
                     ;; 变长子句的必需参数个数
                     (some (fn [cl] (let [[req rst] (fn-params form ctx (first (clause-of cl)))] (when rst (count req)))) cls))
        fname (cond-> (l/->CljFnName nm (or variadic-n false) (vec @prims)
                                     (boolean (and thunk? (runtime-async?))))
                ;; (fn* ^:once [] …)（lazy-seq、delay 的展开；JVM 的 FnExpr.onceOnly）：闭包字段在最后一次使用处清空（cleat-117）
                (:once (meta (first form))) (assoc :once true))
        fnode (mk-lambda fname built)
        m (let [m (user-meta form)] (when m (let [m (dissoc m :rettag)] (when (pos? (count m)) m))))
        fnode (if m
                (clj-call "WithMetaOf" false fnode (analyze m (assoc (ctx-nontail c) :pos nil)))
                fnode)]
    (if self
      (L0-out (letrec ([~[self-name] ~[fnode]] ...) ~self-name))
      fnode)))

(defn- sp-fn* [form ctx] (expand-fn* form false ctx))

(defn- sp-await* [form ctx]
  (if (= 2 (count form))
    (let [m (first (filter marker? (:locals ctx)))]
      (cond (nil? m) (err form ctx "(await* ...) can only be used inside a ^:async function or (async ...) block")
            (= :no-await (:kind m)) (err form ctx "(await* ...) cannot appear inside a catch or finally clause"))
      (clj-call (if (runtime-async?) "AwaitAsync" "AwaitBlocking") false (analyze (second form) (ctx-nontail (ctx-clear ctx)))))
    (err form ctx "Wrong number of arguments to await*, expected: (await* expr)")))

;; letfn*

(defn- fn*-form? [d] (and (clj-list? d) (seq d) (sym-is? (first d) "fn*")))

(defn- sp-letfn* [form ctx]
  (let [bindings (second form)
        pairs (vec (binding-pairs form ctx bindings "Bad binding form, expected matched symbol expression pairs"))
        c (ctx-clear ctx)
        names (dedupe-names (map first pairs))
        locals (mapv (fn [[s _] nm] (make-local* s :local nm)) pairs names)
        bc (ctx-with-locals c locals)
        inits (mapv (fn [[s i]] (analyze i (ctx-hinted bc (name s)))) pairs)
        body (analyze-body (drop 2 form) bc)]
    (if (every? (fn [[_ i]] (fn*-form? i)) pairs)
      (L0-out (letrec ([~names ~inits] ...) ~body))
      (mk-let1 names (map (fn [_] (q nil)) names)
               (mk-seq (concat (map (fn [nm i] (L0-out (set! ~nm ~i))) names inits) [body]))))))

;; try

(defn- clause-head [f]
  (when (and (clj-list? f) (seq f))
    (cond (sym-is? (first f) "catch") :catch (sym-is? (first f) "finally") :finally :else nil)))

(defn- sp-try
  "try：先把子句分组（检查顺序同 原版），再按原版的展开顺序分析——体、各 catch 的处理器、finally。"
  [form ctx]
  (let [c ctx tail? (:tail? c)
        [body catches fin]
        (loop [fs (seq (rest form)) body [] catches [] fin nil]
          (if fs
            (let [f (first fs)]
              (case (clause-head f)
                :catch
                (let [[_ td b & hforms] f]
                  (when (< (count f) 3) (err form ctx "Unable to resolve classname: nil"))
                  (let [t (if (and (keyword? td) (nil? (namespace td)) (= "default" (name td)))
                            (class-for-name "System.Exception")
                            (maybe-type ctx td))]
                    (when-not t (err form ctx (str "Unable to resolve classname: " td)))
                    (when-not (symbol? b) (err form ctx (str "Bad binding form, expected symbol, got: " b)))
                    (when (namespace b) (err form ctx (str "Can't bind qualified name:" b)))
                    (recur (next fs) body (conj catches [t b hforms]) fin)))
                :finally
                (do (when (next fs) (err form ctx "finally clause must be last in try expression"))
                    (recur (next fs) body catches (rest f)))
                (do (when (seq catches) (err form ctx "Only catch or finally clause can follow catch in try expression"))
                    (recur (next fs) (conj body f) catches fin))))
            [body catches fin]))
        b (analyze-body body (ctx-no-recur c tail?))
        hs (mapv (fn [[t b hforms]]
                   (let [bn (fresh-name (str b))
                         hc (ctx-with-locals (ctx-no-await (ctx-no-recur c tail?)) [(->Local b bn (symbol (.FullName ^Type t)) false :catch)])]
                     [(type-name t) (mk-lambda false [(mk-clause [bn] false (analyze-body hforms hc))])]))
                 catches)
        fin (when fin (analyze-body fin (ctx-no-await (ctx-no-recur c false))))]
    (if (and (empty? catches) (not fin))
      b
      (let [x (if (empty? catches) b (L0-out (try-catch ~b (~(map first hs) ~(map second hs)) ...)))]
        (if fin (L0-out (try-finally ~x ~fin)) x)))))

;; —— 反射警告（cleat-099）：*warn-on-reflection* 为真且宿主成员按分析期已知的类型决议不了时向 *err* 报一行，措辞照 ClojureCLR
;; （test_clojure 的 rt.clj 以正则核对）。"决议不了"照 ClojureCLR / JVM 的编译期语义：目标类型未知；成员名 / 实参个数对不上
;; （no such method）；有多个候选而按实参的静态类型选不出一个（argument types: …）。实参类型来自提示、局部的 :tag、字面量、
;; Var 的 :tag 与 :inline 展开、嵌套宿主调用的返回类型（host-arg-type）。
;; 这不等于 codegen 会不会发静态调用（ClojureStaticCalls.Resolve 另有发射器的限制：非 core 的引用类型形参、泛型、属性、值类型目标
;; 都交给运行时的 Reflector）——那是 codegen 的差距，加提示解决不了，不作为反射警告报给用户。
;; 不报的：deftype 桩类型（this）、无实参的静态成员、proxy-super 的内部成员；只有泛型 / params / by-ref 候选时也不报。

(declare expr-type parse-dot host-form-type)

(def reflection-warnings? (atom true))   ; 镜像构建时关掉（driver/compile-boot）

(defn- warn-reflection [form ctx msg]
  (let [pos (position form ctx)
        file (or (nth pos 2 nil) (not-empty (source-path)) "NO_SOURCE_PATH")]
    (.WriteLine ^System.IO.TextWriter (RT/errPrintWriter)
                (str "Reflection warning, " file ":" (if pos (first pos) 0) ":" (if pos (second pos) 0) " - " msg))))

(defn- warn-reflection? [] (and @reflection-warnings? (boolean *warn-on-reflection*)))

(defn- host-arg-type
  "宿主调用实参 / 目标的静态类型（Type）或 nil（未知）：带提示的符号与形式、局部的 :tag、字面量、(quote x)，
  以及 host-form-type 能看出来的调用形式。"
  [ctx a]
  (cond
    (nil? a) nil
    (symbol? a) (expr-type ctx a)
    (clj-list? a) (or (expr-type ctx a) (host-form-type ctx a))
    (instance? IPersistentVector a) IPersistentVector
    (instance? IPersistentMap a) IPersistentMap
    (instance? IPersistentSet a) IPersistentSet
    :else (class a)))

(def ^:private numeric-widening
  "C# 的隐式数值转换（规范 §10.2.3）：源类型 → 能隐式转到的类型。"
  {System.SByte #{System.Int16 System.Int32 System.Int64 System.Single System.Double System.Decimal}
   System.Byte #{System.Int16 System.UInt16 System.Int32 System.UInt32 System.Int64 System.UInt64 System.Single System.Double System.Decimal}
   System.Int16 #{System.Int32 System.Int64 System.Single System.Double System.Decimal}
   System.UInt16 #{System.Int32 System.UInt32 System.Int64 System.UInt64 System.Single System.Double System.Decimal}
   System.Int32 #{System.Int64 System.Single System.Double System.Decimal}
   System.UInt32 #{System.Int64 System.UInt64 System.Single System.Double System.Decimal}
   System.Int64 #{System.Single System.Double System.Decimal}
   System.UInt64 #{System.Single System.Double System.Decimal}
   System.Char #{System.UInt16 System.Int32 System.UInt32 System.Int64 System.UInt64 System.Single System.Double System.Decimal}
   System.Single #{System.Double}
   System.Double #{}
   System.Decimal #{}})

(defn- implicit-conversion?
  "静态类型 a 的实参能否隐式转成形参类型 p：同型、引用拓宽 / 装箱、数值拓宽。"
  [^Type p ^Type a]
  (or (= p a) (.IsAssignableFrom p a) (contains? (numeric-widening a) p)))

(defn- accepts-arg?
  "形参 p 接受静态类型 a（nil 未知）的实参：未知什么都接受（DLR NarrowingLevel.All），已知要能隐式转换或数值间显式转换。"
  [^Type p ^Type a]
  (or (nil? a) (implicit-conversion? p a) (and (contains? numeric-widening p) (contains? numeric-widening a))))

(defn- better-param?
  "对静态类型 a 的实参，形参 p1 是否严格优于 p2（C# 的更优转换：同型最优；都隐式时能单向隐式转到对方者优；
  隐式优于显式）。未知实参按 object 算：object 形参优于其他，其余平手——ClojureCLR 的 DLR 决议大致如此。"
  [^Type p1 ^Type p2 ^Type a]
  (cond
    (= p1 p2) false
    (nil? a) (= p1 System.Object)
    (= p1 a) true
    (= p2 a) false
    :else (let [i1 (implicit-conversion? p1 a) i2 (implicit-conversion? p2 a)]
            (cond (and i1 (not i2)) true
                  (and i2 (not i1)) false
                  (and i1 i2) (and (implicit-conversion? p2 p1) (not (implicit-conversion? p1 p2)))
                  :else false))))

(defn- param-types [^System.Reflection.MethodBase m]
  (mapv (fn [^System.Reflection.ParameterInfo p] (.ParameterType p)) (.GetParameters m)))

(defn- select-overload
  "候选成员（同名、同实参个数的方法或构造器）里按实参静态类型选出的那个，选不出 nil：
  一个候选直接用（JVM 的 InstanceMethodExpr 同）；多个时取各形参都接受实参、且每一位都不劣于其余候选并至少一位严格更优的那个。"
  [members types]
  (let [members (vec members)]
    (cond
      (empty? members) nil
      (= 1 (count members)) (first members)
      :else
      (let [applicable (filterv (fn [m] (every? identity (map accepts-arg? (param-types m) types))) members)
            better? (fn [m o]
                      (let [ps (param-types m) qs (param-types o)]
                        (and (not-any? identity (map (fn [p q a] (better-param? q p a)) ps qs types))
                             (boolean (some identity (map (fn [p q a] (better-param? p q a)) ps qs types))))))
            best (filter (fn [m] (every? (fn [o] (or (identical? m o) (better? m o))) applicable)) applicable)]
        (cond (= 1 (count applicable)) (first applicable)
              (= 1 (count best)) (first best)
              :else nil)))))

(defn- plain-method?
  "编译期重载决议会考虑的方法：非泛型、形参没有 by-ref / 指针 / params（其余的只能运行时决议，提示帮不上）。"
  [^System.Reflection.MethodInfo m]
  (and (not (.ContainsGenericParameters m)) (not (.IsGenericMethodDefinition m))
       (not-any? (fn [^System.Reflection.ParameterInfo p]
                   (or (.IsByRef (.ParameterType p)) (.IsPointer (.ParameterType p))
                       (.IsDefined p System.ParamArrayAttribute false)))
                 (.GetParameters m))))

(defn- type-and-interfaces
  "t 自己，接口时再加上它继承的接口（GetMethods / GetProperty 不含父接口的成员）。"
  [^Type t]
  (if (.IsInterface t) (cons t (seq (.GetInterfaces t))) [t]))

(defn- host-methods
  "t 的公共方法里名为 nm、n 个形参、静态性为 static? 的（含泛型、params、by-ref 的）。"
  [^Type t ^String nm n static?]
  (filter (fn [^System.Reflection.MethodInfo m]
            (and (= nm (.Name m)) (= (boolean static?) (.IsStatic m)) (= n (alength (.GetParameters m)))))
          (mapcat (fn [^Type x] (.GetMethods x)) (type-and-interfaces t))))

(defn- host-field-or-property? [^Type t ^String nm]
  (boolean (some (fn [^Type x] (or (.GetField x nm) (.GetProperty x nm))) (type-and-interfaces t))))

(defn- host-call-return-type
  "(. target member args…) 能决议时的返回类型（方法的返回类型、属性 / 字段的类型），否则 nil。"
  [ctx dot]
  (let [{:keys [static member args type-args ^Type type]} (parse-dot dot ctx)]
    (when (and type (not type-args) (not (HostSyntax/IsStub type)))
      (let [types (mapv #(host-arg-type ctx %) args)
            ^System.Reflection.MethodInfo m (select-overload (filter plain-method? (host-methods type member (count args) (boolean static))) types)]
        (if m
          (let [rt (.ReturnType m)] (when (not= rt System.Void) rt))
          (when (empty? args)
            (some (fn [^Type x] (or (some-> (.GetProperty x ^String member) .PropertyType)
                                    (some-> (.GetField x ^String member) .FieldType)))
                  (type-and-interfaces type))))))))

(defn- host-form-type
  "(f …) 形式的静态类型：(quote x) 的 x 的类、(new T …) / (T. …) 的 T、(var x) / (def …) 的 Var、宿主调用 (. x m …) / (.m x …) / (T/m …) 的返回类型、
  其余调用按 Var 的 :tag（JVM 的 InvokeExpr 也据此）或 :inline 展开后的形式；看不出来 nil。"
  [ctx a]
  (let [op (first a)]
    (try
      (cond
        (not (symbol? op)) nil
        (and (nil? (namespace op)) (find-local ctx op)) nil
        (sym-is? op "quote") (let [d (second a)] (when (some? d) (class d)))
        (sym-is? op "new") (maybe-type ctx (second a))
        (sym-is? op ".") (host-call-return-type ctx a)
        (or (sym-is? op "var") (sym-is? op "def")) Var
        (special-head op) nil
        (or (host-sugar? op) (and (namespace op) (static-member-of ctx op)))
        (when-let [dot (rewrite-to-dot ctx a a)]
          (if (sym-is? (first dot) "new") (maybe-type ctx (second dot)) (host-call-return-type ctx dot)))
        :else
        (let [[o _ _] (resolve-in (current-ns) op)]
          (when (and (instance? Var o) (not (.IsMacro ^Var o)))
            (let [vm (meta o) inline (:inline vm) arities (:inline-arities vm) n (count (rest a))]
              (or (tag-type (:tag vm))
                  ;; :inline 的调用在 var-call 里展开成宿主调用（(int x) → (. RT (intCast x))），类型看展开后的形式
                  (when (and inline (instance? clojure.lang.IFn inline) (or (not arities) (arities n)))
                    (host-arg-type ctx (.applyTo ^clojure.lang.IFn inline (next a)))))))))
      (catch Exception _ nil))))

(defn- arg-types-string [types]
  (str/join ", " (map #(if % (.FullName ^Type %) "unknown") types)))

(defn- check-host-call
  "(. target member args…) 的反射警告（parse-dot 的结果）。"
  [form ctx {:keys [static member args method? ^Type type]}]
  (when (warn-reflection?)
    (let [n (count args) nm (when type (.FullName type))]
      (cond
        (nil? type)
        (warn-reflection form ctx (if (and (zero? n) (not method?))
                                    (str "reference to field/property " member " can't be resolved (target class is unknown).")
                                    (str "call to method " member " can't be resolved (target class is unknown).")))
        (HostSyntax/IsStub type) nil
        (and static (zero? n)) nil
        (str/starts-with? member "proxy$super$") nil   ; proxy-super（core_proxy）：生成的代理类上的方法，父类上自然没有
        :else
        (let [types (mapv #(host-arg-type ctx %) args)
              methods (host-methods type member n (boolean static))
              plain (filter plain-method? methods)]
          (cond
            (and (seq methods) (empty? plain)) nil
            (select-overload plain types) nil
            static
            (warn-reflection form ctx (str "call to static method " member " on " nm " can't be resolved "
                                           (if (empty? methods) "(no such method)." (str "(argument types: " (arg-types-string types) ")."))))
            (and (zero? n) (not method?))
            (when-not (host-field-or-property? type member)
              (warn-reflection form ctx (str "reference to field/property " member " on " nm " can't be resolved.")))
            (empty? methods)
            (warn-reflection form ctx (str "call to method " member " on " nm " can't be resolved (no such method)."))
            :else
            (warn-reflection form ctx (str "call to method " member " on " nm " can't be resolved (argument types: " (arg-types-string types) ")."))))))))

(defn- check-host-ctor
  "(new T args…) 的反射警告：没有该实参个数的公共构造器，或按实参类型选不出一个。"
  [form ctx ^Type t args]
  (when (and (warn-reflection?) (not (HostSyntax/IsStub t)))
    (let [n (count args)
          ctors (filter (fn [^System.Reflection.ConstructorInfo c] (= n (alength (.GetParameters c)))) (.GetConstructors t))]
      (when-not (select-overload ctors (mapv #(host-arg-type ctx %) args))
        (warn-reflection form ctx (str "call to " (.FullName t) " ctor can't be resolved."))))))

;; 宿主互操作：. / new / set!

(defn- host-args
  "宿主调用的实参。静态类型是引用类型（提示、局部的 :tag、宿主调用的返回类型……）时包一层 (clj-call \"Tagged\" #f 'T e)：
  codegen 的静态决议据此选重载（RT.aset 的 long[] / object[] … 重载要知道数组的类型，cleat-101 的延续），生成代码时透明。"
  [form ctx args]
  (mapv (fn [a]
          (when (and (clj-list? a) (sym-is? (first a) "by-ref")) (err form ctx "by-ref arguments are not supported yet"))
          (let [node (analyze a (ctx-nontail (ctx-clear ctx)))
                t (when-not (or (string? a) (number? a) (keyword? a) (nil? a) (boolean? a) (char? a)) (host-arg-type ctx a))]
            (if (and (instance? Type t) (not (.IsValueType ^Type t)) (not= t Object) (not (HostSyntax/IsStub t)))
              (clj-call "Tagged" false (q t) node)
              node)))
        args))

(defn- expr-type [ctx d]
  (tag-type (or (tag-of d)
                (and (symbol? d) (nil? (namespace d)) (some-> (find-local ctx d) :tag)))))

(defn- parse-dot
  "(. target member args…)：{:static :target :member :prop? :args :type-args :method? :type}。"
  [form ctx]
  (when (< (count form) 3) (err form ctx "Malformed member expression, expecting (. target member ...)"))
  (let [[_ td md & more] form
        [msym args method?] (cond
                              (symbol? md) [md more false]
                              (and (empty? more) (clj-list? md) (symbol? (first md))) [(first md) (rest md) true]
                              :else (err form ctx "Malformed member expression"))
        static (maybe-type ctx td)
        fa (first args)
        type-args (when (and (clj-list? fa) (sym-is? (first fa) "type-args"))
                    (mapv (fn [t] (or (maybe-type ctx t) (tag-type t) (err form ctx (str "Unable to resolve classname: " t)))) (rest fa)))
        args (if type-args (rest args) args)
        nm (name msym)
        prop? (and (not method?) (> (count nm) 1) (= \- (nth nm 0)))
        member (munge* (if prop? (subs nm 1) nm))]
    {:static static :target (when-not static td) :member member :prop? prop? :args args :type-args type-args
     :method? method? :type (or static (host-arg-type ctx td))}))

(defn- sp-dot [form ctx]
  (let [{:keys [static target member prop? args type-args method? type] :as parsed} (parse-dot form ctx)
        nt (ctx-nontail (ctx-clear ctx))
        t type
        gen (when type-args (object-array type-args))]
    (when-not type-args (check-host-call form ctx parsed))
    (cond
      (and static (empty? args) (not method?))
      (if prop? (clj-call "GetStaticField" false (q t) (q member)) (clj-call "CallStatic" 3 (q t) (q member) (q gen)))
      static (apply clj-call "CallStatic" 3 (q t) (q member) (q gen) (host-args form ctx args))
      :else
      (let [x (analyze target nt)]
        (if (and prop? (empty? args) (not method?))
          (clj-call "GetInstanceField" false (q t) (q member) x)
          (apply clj-call "CallInstance" 4 (q t) (q member) x (q gen) (host-args form ctx args)))))))

(defn- sp-new [form ctx]
  (when (< (count form) 2) (err form ctx "wrong number of arguments, expecting: (new Classname args...)"))
  (let [td (second form) t (maybe-type ctx td)]
    (when-not t (err form ctx (str "Unable to resolve classname: " td)))
    (check-host-ctor form ctx t (drop 2 form))
    (apply clj-call "NewOf" 1 (q t) (host-args form ctx (drop 2 form)))))

(defn- sp-set! [form ctx]
  (when-not (= 3 (count form)) (err form ctx "Malformed assignment, expecting (set! target val)"))
  (let [[_ td val] form
        field-set (fn [static target member]
                    (let [x (if target (analyze target (ctx-nontail ctx)) (q nil))
                          v (analyze val (ctx-nontail (ctx-clear ctx)))]
                      (clj-call "SetFieldOf" false (q static) (q member) x v)))]
    (cond
      (symbol? td)
      (let [l (and (nil? (namespace td)) (find-local ctx td))]
        (cond
          l (do (when-not (:mutable? l) (err form ctx (str "Cannot assign to non-mutable: " td)))
                (let [v (analyze val (ctx-nontail (ctx-clear ctx)))
                      tmp (fresh-name "tmp")]
                  (mk-let1 [tmp] [v] (mk-seq [(L0-out (set! ~(:name l) ~tmp)) tmp]))))
          (static-member-of ctx td) (let [[t m] (static-member-of ctx td)] (field-set t nil m))
          :else (let [[o msg _] (resolve-in (current-ns) td)]
                  (cond
                    (instance? Var o) (L0-out (clj-var-set! ~o ~(analyze val (ctx-nontail (ctx-clear ctx)))))
                    msg (err form ctx msg)
                    :else (err form ctx "Invalid assignment target")))))
      (clj-list? td)
      (if-let [dot (rewrite-to-dot ctx td form)]
        (let [{:keys [static target member args type]} (parse-dot dot ctx)]
          (when (seq args) (err form ctx "Invalid assignment target"))
          (field-set (or static type) target member))
        (err form ctx "Invalid assignment target"))
      :else (err form ctx "Invalid assignment target"))))

;; case*

(defn- sp-case* [form ctx]
  (when (< (count form) 8) (err form ctx "Malformed case*"))
  (let [[_ x shift mask default imap switch-type test-type & skip] form
        c (ctx-clear ctx)]
    (when-not (and (symbol? x) (nil? (namespace x)) (find-local ctx x)) (err form ctx "case* expects a local as its expression"))
    (let [tt (if (keyword? test-type) (name test-type) (str test-type))
          kind (case tt "int" 0 "hash-equiv" 1 2)
          skip-set (first skip)
          entries (for [[h [test then]] imap]
                    [h test then (boolean (and skip-set (contains? skip-set h)))])
          int (fn [n] (parse-long (str n)))
          data [(int shift) (int mask) kind (mapv (fn [[h test _ skip?]] [(int h) test skip?]) entries)]
          xn (analyze x (ctx-nontail c))
          dn (analyze default c)
          thens (mapv (fn [[_ _ then]] (analyze then c)) entries)]
      (L0-out (clj-case ~data ~xn ~dn ~@thens)))))

;; deftype* / reify* / gen-interface / proxy-class* / gen-class*

(defn- host-check [form ctx r] (if (string? r) (err form ctx r) r))
(defn- type-desc [nm] [nm (boolean (HostSyntax/IsValueType nm))])

(defn- build-type [form ctx tagname class-name field-syms ifaces method-forms reify?]
  (let [ifaces (host-check form ctx (HostSyntax/ResolveInterfaces ifaces))
        fields (mapv (fn [s]
                       (let [m (meta s)
                             vol (boolean (:volatile-mutable m))
                             mut (or vol (boolean (:unsynchronized-mutable m)))
                             ft (tag-type (tag-of s))]
                         {:sym s :mutable mut :volatile vol :type (if ft (type-name ft) "System.Object") :name (fresh-name (str s))}))
                     field-syms)
        field-locals (mapv (fn [f] (->Local (:sym f) (:name f) (tag-of (:sym f)) (:mutable f) :field)) fields)
        c ctx]
    (loop [fs fields]
      (when (seq fs)
        (when (some #(= (name (:sym %)) (name (:sym (first fs)))) (rest fs))
          (err form ctx (str "Duplicate field name \"" (name (:sym (first fs))) "\" in " class-name)))
        (recur (rest fs))))
    (when-not reify?
      (HostSyntax/DefineStub ^String class-name (object-array (map #(munge* (name (:sym %))) fields))
                             (object-array (map prim-field-type fields)) ^String tagname ifaces))
    (let [ms (mapv (fn [m]
                     (when-not (and (clj-list? m) (symbol? (first m))) (err form ctx (str "Method name must be a symbol: " (first m))))
                     (let [[mname params & mbody] m
                           r (host-check form ctx (HostSyntax/TypeMethod ifaces mname params))
                           iface (nth r 0) mn (nth r 1) ret (nth r 2) ptypes (vec (nth r 3))]
                       {:params params :body mbody :r r
                        :spec [(if (nil? iface) false iface) mn (type-desc ret) (mapv type-desc ptypes)]
                        :ptypes ptypes}))
                   method-forms)
          lambdas (mapv (fn [{:keys [params body ptypes]}]
                          (let [pitems (vec params)
                                this (first pitems) args (rest pitems)
                                [this-name & arg-names] (dedupe-names pitems)
                                lp (fresh-name "recur")
                                inner (dedupe-names args)
                                locals (concat field-locals
                                               [(->Local this this-name (symbol class-name) false :this)]
                                               (map (fn [a t y] (->Local (symbol (name a)) y (symbol t) false :arg)) args ptypes inner))
                                bc {:tail? true :loop [lp (count args)] :locals (concat (reverse locals) (without-markers (:locals c)))
                                    :fn-name (:fn-name c) :hint nil :pos (:pos c)}
                                b (analyze-body body bc)]
                            (mk-lambda false [(mk-clause (cons this-name arg-names) false (named-let lp "recur" inner (vec arg-names) b))])))
                        ms)
          layout (HostSyntax/InterfaceLayout ifaces (object-array (map :r ms)) reify?)
          stubs (mapv (fn [st]
                        [(nth st 0) (nth st 1) (type-desc (nth st 2)) (mapv type-desc (nth st 3))
                         (let [t (nth st 4)] (if (nil? t) false t))])
                      (nth layout 1))
          drops (count (take-while #(#{"__meta" "__extmap" "__hash" "__hasheq"} (name (:sym %))) (reverse fields)))
          basis (take (- (count fields) drops) fields)
          ;; 描述（Clojure 向量，cleat-092）：[类名 reify? 字段 接口 方法 桩 basis 记录键 drops]，见 codegen 的 deftype-info-*
          info [class-name reify?
                (mapv (fn [f] [(munge* (name (:sym f))) (:mutable f) (:volatile f) (or (prim-field-type f) false)]) fields)
                (vec (nth layout 0))
                (mapv :spec ms)
                stubs
                (if reify? false (apply vector (map :sym basis)))
                (if (and (not reify?) (pos? drops)) (mapv #(name (:sym %)) basis) [])
                drops]]
      (L0-out (clj-deftype ~info (~@(map :name fields)) ~@lambdas)))))

(defn- build-interface [form ctx class-name extends sigs]
  (let [resolve-type (fn [x]
                       (let [t (if (instance? Type x) x (maybe-type ctx x))]
                         (when-not t (err form ctx (str "Unable to resolve classname: " x)))
                         (type-desc (type-name t))))
        ifaces (host-check form ctx (HostSyntax/ResolveInterfaces extends))
        specs (map (fn [sig]
                     (let [items (vec sig)]
                       (when (< (count items) 3) (err form ctx "Malformed gen-interface method signature"))
                       [false (if (keyword? (items 0)) (name (items 0)) (str (items 0))) (resolve-type (items 2))
                        (mapv (fn [p]
                                (when (clj-list? p) (err form ctx "by-ref parameters are not supported in gen-interface"))
                                (resolve-type p))
                              (items 1))]))
                   sigs)
        info [class-name :interface [] (mapv type-name ifaces) (vec specs) [] false]]
    (L0-out (clj-deftype ~info ()))))

(defn- sp-deftype* [form ctx]
  (when (< (count form) 4) (err form ctx "Malformed deftype*"))
  (let [[_ tagname class-name fields & more] form
        [opts rest-forms] (loop [r more opts {}]
                            (if (and (keyword? (first r)) (next r))
                              (recur (nnext r) (assoc opts (first r) (second r)))
                              [opts r]))]
    (if (:cleat/interface opts)
      (build-interface form ctx (name class-name) (or (:implements opts) []) (:cleat/interface opts))
      (build-type form ctx (name tagname) (name class-name) (vec fields) (or (:implements opts) []) rest-forms false))))

(defn- sp-reify* [form ctx]
  (let [[_ ifaces & methods] form
        is (conj ifaces 'clojure.lang.IObj)
        class-name (str (munge* (str (.Name ^Namespace (current-ns)))) "$reify__" (RT/nextID))
        node (build-type form ctx class-name class-name [] is methods true)
        m (user-meta form)]
    (if m
      (clj-call "WithMetaOf" false node (analyze m (assoc (ctx-nontail ctx) :pos nil)))
      node)))

(defn- v->l [x] (if (instance? System.Array x) (seq x) ()))

(defn- sp-proxy-class* [form ctx]
  (let [bases (vec (rest form))
        layout (host-check form ctx (HostSyntax/ProxyLayout (str (.Name ^Namespace (current-ns))) bases))
        info [(nth layout 0) :proxy (nth layout 1)
              (vec (v->l (nth layout 2)))
              (mapv (fn [ps] (mapv type-desc ps)) (v->l (nth layout 3)))
              (mapv (fn [m]
                      [(nth m 0) (type-desc (nth m 1)) (mapv type-desc (nth m 2))
                       (nth m 3) (nth m 4) (vec (v->l (nth m 5))) (nth m 6) (nth m 7) (nth m 8)])
                    (v->l (nth layout 4)))]]
    (L0-out (clj-deftype ~info ()))))

(defn- nil->f [x] (if (nil? x) false x))

(defn- sp-gen-class* [form ctx]
  (let [layout (host-check form ctx (HostSyntax/GenClassLayout (str (.Name ^Namespace (current-ns))) (second form)))
        descs (fn [x] (mapv type-desc (v->l x)))
        info [(nth layout 0) :genclass (nth layout 1) (vec (v->l (nth layout 2)))
              (nth layout 3) (nth layout 4)
              (mapv #(vec (v->l %)) (v->l (nth layout 5)))
              (mapv (fn [c] [(descs (nth c 0)) (descs (nth c 1))]) (v->l (nth layout 6)))
              (nil->f (nth layout 7)) (nil->f (nth layout 8)) (nil->f (nth layout 9))
              (mapv (fn [m]
                      [(nth m 0) (type-desc (nth m 1)) (descs (nth m 2)) (nth m 3) (nth m 4)
                       (let [t (nth m 5)] (if (nil? t) false (vec (seq t))))
                       (nth m 6) (nth m 7) (nil->f (nth m 8)) (nth m 9)])
                    (v->l (nth layout 10)))
              (nil->f (nth layout 11)) (nil->f (nth layout 12))
              (mapv (fn [x] [(nth x 0) (nth x 1) (nth x 2) (nth x 3) (nil->f (nth x 4)) (nil->f (nth x 5))])
                    (v->l (nth layout 13)))
              (mapv (fn [x] [(nth x 0) (nth x 1) (vec (v->l (nth x 2))) (nth x 3) (nth x 4)])
                    (v->l (nth layout 14)))]]
    (L0-out (clj-deftype ~info ()))))

(defn- analyze-special [nm form ctx]
  (case nm
    "def" (sp-def form ctx)
    "if" (sp-if form ctx)
    "do" (sp-do form ctx)
    "let*" ((sp-let false) form ctx)
    "loop*" ((sp-let true) form ctx)
    "recur" (sp-recur form ctx)
    "fn*" (sp-fn* form ctx)
    "quote" (sp-quote form ctx)
    "var" (sp-var form ctx)
    "throw" (sp-throw form ctx)
    "try" (sp-try form ctx)
    "monitor-enter" ((sp-monitor "monitor-enter" "MonitorEnterOf") form ctx)
    "monitor-exit" ((sp-monitor "monitor-exit" "MonitorExitOf") form ctx)
    "." (sp-dot form ctx)
    "new" (sp-new form ctx)
    "set!" (sp-set! form ctx)
    "letfn*" (sp-letfn* form ctx)
    "case*" (sp-case* form ctx)
    "deftype*" (sp-deftype* form ctx)
    "reify*" (sp-reify* form ctx)
    "proxy-class*" (sp-proxy-class* form ctx)
    "gen-class*" (sp-gen-class* form ctx)
    "await*" (sp-await* form ctx)
    "import*" (sp-import* form ctx)
    "catch" (err form ctx "catch must be inside try")
    "finally" (err form ctx "finally must be inside try")
    "&" (err form ctx "Unable to resolve symbol: & in this context")))

;; —— 入口 ——

(defn analyze-top
  "顶层形式 → L0（在 *ns* 里；上下文为非尾、没有局部）。"
  [form]
  (analyze form (assoc root-ctx :pos (source-of form))))

(defn macroexpand-1
  "Clojure 的 macroexpand-1（namespaces.sls macroexpand-1-in-ns）：宏调用（含以宏 Var 对象开头的形式）与宿主语法糖展开一层；
  特殊形式与其余形式原样返回。"
  [form]
  (let [ctx root-ctx]
    (cond
      (macro-var-head form ctx) (call-macro (macro-var-head form ctx) form (source-of form) ctx)
      (and (clj-list? form) (seq form) (symbol? (first form)) (not (special-head (first form))))
      (let [op (first form)
            [o _ _] (resolve-in (current-ns) op)]
        (cond
          (and (instance? Var o) (.IsMacro ^Var o)) (call-macro o form (source-of form) ctx)
          (some? o) form
          (or (and (namespace op) (static-member-of ctx op)) (host-sugar? op)) (or (rewrite-to-dot ctx form form) form)
          :else form))
      :else form)))
