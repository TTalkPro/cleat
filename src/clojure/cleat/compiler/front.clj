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
;; 前向变量在赋值之前可能被读到：对它们的引用包上 $check-initialized
(define-pass check-forward L1 [ir an] -> L1 []
  (definitions
    (defn forward? [x] (marked? (:forward an) x)))
  (Expr [ir] -> Expr []
    [~x (if (forward? x) (out (primcall $check-initialized ~x (quote ~x))) x)]))

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
