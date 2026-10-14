;; 前端的分析与 pass（cleat-084 试点，移植 src/scheme/lib/cleat/scheme/compiler/front.sls 的一部分：analyze、check-forward、
;; convert-assignments；simplify-letrec、cp0-lite、simplify-if 在 cleat-087 移植）。注释见 Scheme 版。
(ns cleat.compiler.front
  (:require [cleat.nanopass :refer [define-pass nanopass-case out]]
            [cleat.compiler.languages :refer [L0 L1 L2]]))

;; —— 小集合（无重复的列表，顺序同 Scheme 版：set-add 加在前面）——
(defn- member? [x s] (loop [s (seq s)] (cond (nil? s) false (= x (first s)) true :else (recur (next s)))))
(defn- set-add [x s] (if (member? x s) s (cons x s)))
(defn- set-union [a b]
  (cond
    (empty? a) b
    (empty? b) a
    :else (reduce (fn [s x] (set-add x s)) b a)))
(defn- set-minus [s xs] (if (empty? xs) s (filter (fn [x] (not (member? x xs))) s)))
(defn- union* [ss] (reduce set-union () ss))

;; 分析表（Hashtable：变量为键时按名字相等，节点为键时按引用相等）
(defrecord Analysis [assigned      ; 被 set! 的词法变量
                     captured      ; 出现在某个 lambda 自由变量中的词法变量
                     letrec-refs   ; letrec 右边（L0 节点）→ 它引用的同组变量
                     massigned     ; 被 mset! 的库变量
                     forward       ; simplify-letrec 引入的前向变量
                     fields])      ; deftype* 的字段变量（被 set! 也不装箱）

(defn- mark! [^System.Collections.Hashtable h k] (.set_Item h k true))
(defn marked? [^System.Collections.Hashtable h k] (boolean (.get_Item h k)))

(defn analyze [ir]
  (let [assigned (System.Collections.Hashtable.)
        captured (System.Collections.Hashtable.)
        letrec-refs (System.Collections.Hashtable.)
        massigned (System.Collections.Hashtable.)
        fields (System.Collections.Hashtable.)]
    (letfn [(Expr [e]
              (nanopass-case [L0 Expr] e
                [~x (list x)]
                [(quote ~d) ()]
                [(if ~e0 ~e1 ~e2) (union* [(Expr e0) (Expr e1) (Expr e2)])]
                [(begin ~e* ... ~e) (union* (map Expr (cons e e*)))]
                [(set! ~x ~e) (mark! assigned x) (set-add x (Expr e))]
                [(gref ~s) ()]
                [(gset! ~s ~e) (Expr e)]
                [(mref ~x) ()]
                [(mset! ~x ~e) (mark! massigned x) (Expr e)]
                [(prim ~pr) ()]
                [(primcall ~pr ~e* ...) (union* (map Expr e*))]
                [(lambda ~nm ~cl* ...)
                 (let [fv (union* (map Clause cl*))]
                   (doseq [x fv] (mark! captured x))
                   fv)]
                [(letrec ([~x* ~e*] ...) ~body)
                 (let [fvs (mapv Expr e*)]
                   (doseq [[rhs fv] (map vector e* fvs)]
                     (.set_Item letrec-refs rhs (filter (fn [x] (some #(= x %) x*)) fv)))
                   (set-minus (union* (cons (Expr body) fvs)) x*))]
                [(let ([~x* ~e*] ...) ~body) (set-union (union* (map Expr e*)) (set-minus (Expr body) x*))]
                [(label ~x (~x* ...) ~body0 ~body1) (set-minus (set-union (Expr body0) (Expr body1)) (cons x x*))]
                [(jump ~x ~e* ...) (set-add x (union* (map Expr e*)))]
                [(library ~d ([~x* ~s* ~e*] ...) ~body) (union* (cons (Expr body) (map Expr e*)))]
                [(call ~e ~e* ...) (union* (map Expr (cons e e*)))]
                [(and ~e* ...) (union* (map Expr e*))]
                [(or ~e* ...) (union* (map Expr e*))]
                [(throw ~e) (Expr e)]
                [(break) ()]
                [(try-finally ~e0 ~e1) (set-union (Expr e0) (Expr e1))]
                [(try-catch ~e (~d* ~e*) ...) (union* (cons (Expr e) (map Expr e*)))]
                [(escape-frame ~x ~body) (set-minus (Expr body) (list x))]
                [(try-handle ~x ~body0 ~body1) (set-union (Expr body0) (set-minus (Expr body1) (list x)))]
                [(host-call ~d0 ~d1 ~st ~e* ...) (union* (map Expr e*))]
                [(host-field-get ~d0 ~d1 ~st ~e* ...) (union* (map Expr e*))]
                [(host-field-set ~d0 ~d1 ~st ~e* ...) (union* (map Expr e*))]
                [(host-new ~d ~e* ...) (union* (map Expr e*))]
                [(host-new-array ~d ~e) (Expr e)]
                [(host-cast ~d ~e) (Expr e)]
                [(host-is ~d ~e) (Expr e)]
                [(host-typeof ~d) ()]
                [(host-using ~d) ()]
                [(host-reference ~d) ()]
                [(host-namespaces) ()]
                [(unsupported ~d ~e* ...) (union* (map Expr e*))]
                [(clj-var-ref ~d ~st) ()]
                [(clj-var-set! ~d ~e) (Expr e)]
                [(clj-def ~d ~st0 ~st1 ~e0 ~e1) (set-union (Expr e0) (Expr e1))]
                [(clj-invoke ~e ~e* ...) (union* (map Expr (cons e e*)))]
                [(clj-keyword-invoke ~d ~e ~e* ...) (union* (map Expr (cons e e*)))]
                [(clj-call ~d0 ~d1 ~e* ...) (union* (map Expr e*))]
                [(clj-static-call ~d0 ~d1 ~e* ...) (union* (map Expr e*))]
                [(clj-case ~d ~e0 ~e1 ~e* ...) (union* (map Expr (list* e0 e1 e*)))]
                [(clj-deftype ~d (~x* ...) ~le* ...)
                 (doseq [x x*] (mark! fields x))
                 (set-minus (union* (map Expr le*)) x*)]))
            (Clause [cl]
              (nanopass-case [L0 Clause] cl
                [(clause (~x* ...) ~x ~body) (set-minus (Expr body) (if x (cons x x*) x*))]))]
      ;; union* 等返回惰性序列；分析的副作用（登记表）必须在返回前发生
      (dorun (Expr ir))
      (->Analysis assigned captured letrec-refs massigned (System.Collections.Hashtable.) fields))))

;; —— check-forward：L1 → L1 ——
;; 前向变量在赋值之前可能被读到：对它们的引用包上 $check-initialized（名字照 Scheme 版是 Scheme 符号，报错时用）
(define-pass check-forward L1 [ir an] -> L1 []
  (definitions
    (defn forward? [x] (marked? (:forward an) x)))
  (Expr [ir] -> Expr []
    [~x (if (forward? x) (out (primcall $check-initialized ~x (quote ~(Cleat.Scheme.Symbol/Intern (str x))))) x)]))

;; —— convert-assignments：L1 → L2 ——
(define-pass convert-assignments L1 [ir an] -> L2 []
  (definitions
    ;; label 的形参表：jump 给被装箱的形参传新 Box
    (def label-params (System.Collections.Hashtable.))
    (defn boxed? [x]
      (and (marked? (:assigned an) x)
           (marked? (:captured an) x)
           (not (marked? (:fields an) x)))))
  (Expr [ir] -> Expr []
    [~x (if (boxed? x) (out (unbox ~x)) x)]
    [(set! ~x ~[e]) (if (boxed? x) (out (set-box! ~x ~e)) (out (set! ~x ~e)))]
    [(let ([~x* ~[e*]] ...) ~[body])
     (out (let ([~x* ~(map (fn [x e] (if (boxed? x) (out (box ~e)) e)) x* e*)] ...) ~body))]
    [(label ~x (~x* ...) ~body0 ~body1)
     (.set_Item label-params x x*)
     (let [b0 (Expr body0) b1 (Expr body1)]
       (out (label ~x (~x* ...) ~b0 ~b1)))]
    [(jump ~x ~[e*] ...)
     (let [p* (or (.get_Item label-params x) [])]
       (out (jump ~x ~(map (fn [p e] (if (boxed? p) (out (box ~e)) e)) p* e*) ...)))]
    [(try-handle ~x ~[body0] ~[body1])
     (if (boxed? x)
       (out (try-handle ~x ~body0 (begin (set! ~x (box ~x)) ~body1)))
       (out (try-handle ~x ~body0 ~body1)))])
  (Clause [ir] -> Clause []
    [(clause (~x* ...) ~x ~[body])
     ;; 被装箱的形参：进入时把形参换成 Box
     (let [bx (filter boxed? (if x (cons x x*) x*))]
       (out (clause (~x* ...) ~x
              ~(if (empty? bx)
                 body
                 (out (begin ~(map (fn [v] (out (set! ~v (box ~v)))) bx) ... ~body))))))]))

;; —— simplify-letrec（含 recognize-let）：L0 → L1（front.sls simplify-letrec，逐条移植）——
;; letrec 的绑定 i 的右边引用了变量 v（v 在 i 处或之后绑定）时：若从 i 到 v 的绑定都是 lambda 且变量都未被赋值，
;; 它们会落在同一组 letrec 中，引用合法；否则 v 是"前向变量"：在整个结构外先绑定为未指定值，到它的位置再 set!。

(defn lambda-form? [e]
  (nanopass-case [L0 Expr] e [(lambda ~nm ~cl* ...) true] [else false]))

(def ^:private const-in-run?
  (let [v (System.Environment/GetEnvironmentVariable "CLEAT_NO_070")]
    (not (and (string? v) (or (= v "1") (.Contains ^String v "letrec"))))))

(define-pass simplify-letrec L0 [ir an] -> L1 []
  (definitions
    (def assigned (:assigned an))
    (def refs (:letrec-refs an))
    (def fwd (System.Collections.Hashtable.))
    (defn assigned? [x] (marked? assigned x))
    (defn groupable? [x e] (and (lambda-form? e) (not (assigned? x))))
    (defn const-binding? [x e]
      (and const-in-run? (not (marked? fwd x))
           (nanopass-case [L0 Expr] e [(quote ~d) true] [else false])))
    (defn run-member? [x e] (or (groupable? x e) (const-binding? x e)))
    ;; 从 x 到 v（含两端）的绑定都可并入同一组
    (defn same-run? [x v x* e*]
      (loop [xs (seq x*) es (seq e*) started false]
        (cond
          (nil? xs) false
          (and (not started) (not= (first xs) x)) (recur (next xs) (next es) false)
          (not (run-member? (first xs) (first es))) false
          (= (first xs) v) true
          :else (recur (next xs) (next es) true))))
    (defn forward-vars [x* e*]
      (loop [xs (seq x*) es (seq e*) acc ()]
        (if-not xs
          acc
          (let [r (or (.get_Item ^System.Collections.Hashtable refs (first es)) ())
                bad (filter (fn [v] (and (some #(= v %) xs) (not (same-run? (first xs) v x* e*)))) r)]
            (recur (next xs) (next es) (reduce (fn [s v] (set-add v s)) acc bad)))))))
  (Expr [ir] -> Expr []
    [(call ~e ~[e*] ...)
     ;; recognize-let：((lambda (x …) body) e …) → (let ((x e) …) body)
     (or (nanopass-case [L0 Expr] e
           [(lambda ~nm ~cl)
            (nanopass-case [L0 Clause] cl
              [(clause (~x* ...) ~x ~body)
               (when (and (not x) (= (count x*) (count e*)))
                 (out (let ([~x* ~e*] ...) ~(Expr body))))])]
           [else nil])
         (out (call ~(Expr e) ~e* ...)))]
    [(letrec ([~x* ~e*] ...) ~[body])
     ;; 前向变量会被标为已赋值而不能并组：反复计算到不再变化
     (let [forward (loop [acc []]
                     (let [new (remove #(some (fn [a] (= a %)) acc) (forward-vars x* e*))]
                       (if (empty? new)
                         acc
                         (do (doseq [v new] (mark! assigned v) (mark! fwd v))
                             (recur (into acc new))))))]
       (doseq [v forward] (mark! assigned v) (mark! (:forward an) v))
       (let [inner
             (letfn [(lp [xs es]
                       (cond
                         (empty? xs) body
                         (run-member? (first xs) (first es))
                         ;; 一组：常量提到前面用 let 绑定，lambda 进 letrec
                         (loop [xs xs es es gx () ge () cx () ce ()]
                           (cond
                             (and (seq xs) (groupable? (first xs) (first es)))
                             (recur (rest xs) (rest es) (cons (first xs) gx) (cons (LambdaOf (first es)) ge) cx ce)
                             (and (seq xs) (const-binding? (first xs) (first es)))
                             (recur (rest xs) (rest es) gx ge (cons (first xs) cx) (cons (Expr (first es)) ce))
                             :else
                             (let [rst (if (empty? gx)
                                         (lp xs es)
                                         (out (letrec ([~(reverse gx) ~(reverse ge)] ...) ~(lp xs es))))]
                               (if (empty? cx) rst (out (let ([~(reverse cx) ~(reverse ce)] ...) ~rst))))))
                         (some #(= (first xs) %) forward)
                         (out (begin (set! ~(first xs) ~(Expr (first es))) ~(lp (rest xs) (rest es))))
                         :else
                         (out (let ([~[(first xs)] ~[(Expr (first es))]] ...) ~(lp (rest xs) (rest es))))))]
               (lp (seq x*) (seq e*)))]
         (if (empty? forward)
           inner
           (out (let ([~forward ~(map (fn [_] (out (primcall $uninitialized))) forward)] ...) ~inner)))))])
  (LambdaOf LambdaExpr [ir] -> LambdaExpr [])
  (Expr ir))

;; —— cp0-lite：L1 → L1（front.sls cp0-lite 中 Clojure 输入用得到的部分）——
;; 常量测试的 if 折叠；begin 里删掉常量语句。查表折叠、discard、memv / eqv? / list 的改写都只作用于 primcall 与 boot 的 gref
;; （Clojure 输入里没有，cleat-085 的普查），不移植。

(defn- const? [e] (nanopass-case [L1 Expr] e [(quote ~d) true] [else false]))
(defn- const-value [e] (nanopass-case [L1 Expr] e [(quote ~d) d] [else nil]))

(define-pass cp0-lite L1 [ir] -> L1 []
  (Expr [ir] -> Expr []
    [(if ~[e0] ~[e1] ~[e2])
     (if (const? e0)
       (if (const-value e0) e1 e2)
       (out (if ~e0 ~e1 ~e2)))]
    [(begin ~[e*] ... ~[e])
     (let [e* (remove const? e*)]
       (if (empty? e*) e (out (begin ~e* ... ~e))))]))

;; —— 变量引用计数与 simplify-lets（inline.sls count-refs! / simplify-lets）——

(define-pass count-refs! L1 [ir counts] -> * []
  (definitions
    (defn bump! [x] (.set_Item ^System.Collections.Hashtable counts x (inc (or (.get_Item ^System.Collections.Hashtable counts x) 0)))))
  (Expr [ir] -> * []
    [~x (bump! x)]
    [(mref ~x) (bump! x)]
    [(lambda ~nm ~cl* ...) (doseq [cl cl*] (Clause cl))]
    [(set! ~x ~e) (Expr e)]
    [(mset! ~x ~e) (Expr e)]
    [(if ~e0 ~e1 ~e2) (Expr e0) (Expr e1) (Expr e2)]
    [(begin ~e* ... ~e) (doseq [x e*] (Expr x)) (Expr e)]
    [(gset! ~s ~e) (Expr e)]
    [(primcall ~pr ~e* ...) (doseq [x e*] (Expr x))]
    [(letrec ([~x* ~le*] ...) ~body) (doseq [x le*] (Expr x)) (Expr body)]
    [(let ([~x* ~e*] ...) ~body) (doseq [x e*] (Expr x)) (Expr body)]
    [(library ~d ([~x* ~s* ~e*] ...) ~body) (doseq [x e*] (Expr x)) (Expr body)]
    [(call ~e ~e* ...) (Expr e) (doseq [x e*] (Expr x))]
    [(and ~e* ...) (doseq [x e*] (Expr x))]
    [(or ~e* ...) (doseq [x e*] (Expr x))]
    [(throw ~e) (Expr e)]
    [(try-finally ~e0 ~e1) (Expr e0) (Expr e1)]
    [(label ~x (~x* ...) ~body0 ~body1) (Expr body0) (Expr body1)]
    [(jump ~x ~e* ...) (doseq [x e*] (Expr x))]
    [(escape-frame ~x ~body) (Expr body)]
    [(try-handle ~x ~body0 ~body1) (Expr body0) (Expr body1)]
    [(try-catch ~e (~d* ~e*) ...) (Expr e) (doseq [x e*] (Expr x))]
    [(clj-var-set! ~d ~e) (Expr e)]
    [(clj-def ~d ~st0 ~st1 ~e0 ~e1) (Expr e0) (Expr e1)]
    [(clj-invoke ~e ~e* ...) (Expr e) (doseq [x e*] (Expr x))]
    [(clj-keyword-invoke ~d ~e ~e* ...) (Expr e) (doseq [x e*] (Expr x))]
    [(clj-call ~d0 ~d1 ~e* ...) (doseq [x e*] (Expr x))]
    [(clj-static-call ~d0 ~d1 ~e* ...) (doseq [x e*] (Expr x))]
    [(clj-case ~d ~e0 ~e1 ~e* ...) (Expr e0) (Expr e1) (doseq [x e*] (Expr x))]
    [(clj-deftype ~d (~x* ...) ~le* ...) (doseq [x le*] (Expr x))]
    [(host-call ~d0 ~d1 ~st ~e* ...) (doseq [x e*] (Expr x))]
    [(host-field-get ~d0 ~d1 ~st ~e* ...) (doseq [x e*] (Expr x))]
    [(host-field-set ~d0 ~d1 ~st ~e* ...) (doseq [x e*] (Expr x))]
    [(host-new ~d ~e* ...) (doseq [x e*] (Expr x))]
    [(host-new-array ~d ~e) (Expr e)]
    [(host-cast ~d ~e) (Expr e)]
    [(host-is ~d ~e) (Expr e)]
    [(unsupported ~d ~e* ...) (doseq [x e*] (Expr x))]
    [else nil])
  (Clause [ir] -> * []
    [(clause (~x* ...) ~x ~body) (Expr body)])
  (Expr ir))

(defn- simple-datum?
  "Scheme 版的 simple-datum?：fixnum（Int32）、flonum、布尔、字符、Scheme 符号、空表。Clojure 的 long（Int64）不是 fixnum，不传播。"
  [d]
  (or (instance? Int32 d) (instance? Double d) (boolean? d) (char? d)
      (instance? Cleat.Scheme.Symbol d) (instance? Cleat.Scheme.EmptyList d)))

(define-pass simplify-lets L1 [ir an] -> L1 []
  (definitions
    (def assigned (:assigned an))
    (def counts (System.Collections.Hashtable.))
    (def subst (System.Collections.Hashtable.))
    (defn refs [x] (or (.get_Item ^System.Collections.Hashtable counts x) 0))
    (defn propagatable? [x e]
      (and (not (marked? assigned x))
           (nanopass-case [L1 Expr] e
             [(quote ~d) (simple-datum? d)]
             [~x (not (marked? assigned x))]
             [else false])))
    (defn droppable-init? [e]
      (nanopass-case [L1 Expr] e
        [(quote ~d) true]
        [~x true]
        [(lambda ~nm ~cl* ...) true]
        [else false])))
  (Expr [ir] -> Expr []
    [~x (or (.get_Item ^System.Collections.Hashtable subst x) x)]
    [(let ([~x* ~[e*]] ...) ~body)
     (loop [xs (seq x*) es (seq e*) kx () ke ()]
       (if-not xs
         (let [body (Expr body)]
           (if (empty? kx) body (out (let ([~(reverse kx) ~(reverse ke)] ...) ~body))))
         (let [x (first xs) e (first es)]
           (cond
             (propagatable? x e) (do (.set_Item ^System.Collections.Hashtable subst x e) (recur (next xs) (next es) kx ke))
             (and (zero? (refs x)) (not (marked? assigned x)) (droppable-init? e)) (recur (next xs) (next es) kx ke)
             :else (recur (next xs) (next es) (cons x kx) (cons e ke))))))]
    [(letrec ([~x* ~le*] ...) ~body)
     (loop [xs (seq x*) ls (seq le*) kx () kl ()]
       (if-not xs
         (let [body (Expr body)]
           (if (empty? kx) body (out (letrec ([~(reverse kx) ~(map LambdaExpr (reverse kl))] ...) ~body))))
         (if (zero? (refs (first xs)))
           (recur (next xs) (next ls) kx kl)
           (recur (next xs) (next ls) (cons (first xs) kx) (cons (first ls) kl)))))])
  (LambdaExpr [ir] -> LambdaExpr [])
  (Clause [ir] -> Clause [])
  (count-refs! ir counts)
  (Expr ir))
