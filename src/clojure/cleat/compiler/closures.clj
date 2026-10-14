;; uncover-free：L2 → L3，lambda 带上自由变量表（cleat-084 试点，移植 src/scheme/lib/cleat/scheme/compiler/closures.sls）。
;; 自由变量表的顺序与 Scheme 版相同（set-add 追加在末尾），L3 的 unparse 逐项一致。
(ns cleat.compiler.closures
  (:require [cleat.nanopass :refer [define-pass nanopass-case out]]
            [cleat.compiler.languages :refer [L2 L3]]))

;; —— 小集合（无重复的向量，顺序同 Scheme 版）——
(defn- member? [x s] (loop [s (seq s)] (cond (nil? s) false (= x (first s)) true :else (recur (next s)))))
(defn- set-add [x s] (if (member? x s) s (conj s x)))
(defn- set-union [a b] (if (empty? b) a (reduce (fn [s x] (set-add x s)) a b)))
(defn- set-minus [s xs] (if (empty? xs) s (filterv (fn [x] (not (member? x xs))) s)))
(defn- union* [ss] (reduce set-union [] ss))

(define-pass attach L2 [ir fvt] -> L3 []
  (LambdaExpr [ir] -> LambdaExpr []
    [(lambda ~nm ~[cl*] ...)
     (out (lambda ~nm (~(or (get fvt ir) []) ...) ~cl* ...))]))

(defn uncover-free [ir]
  (let [fvt (System.Collections.Hashtable.)]
    (letfn [(Expr [e]
              (nanopass-case [L2 Expr] e
                [~x [x]]
                [(quote ~d) []]
                [(if ~e0 ~e1 ~e2) (union* [(Expr e0) (Expr e1) (Expr e2)])]
                [(begin ~e* ... ~e) (union* (map Expr (concat e* [e])))]
                [(set! ~x ~e) (set-add x (Expr e))]
                [(gref ~s) []]
                [(gset! ~s ~e) (Expr e)]
                [(mref ~x) []]
                [(mset! ~x ~e) (Expr e)]
                [(prim ~pr) []]
                [(primcall ~pr ~e* ...) (union* (map Expr e*))]
                [(lambda ~nm ~cl* ...)
                 (let [fv (union* (map Clause cl*))]
                   (.set_Item fvt e fv)
                   fv)]
                [(label ~x (~x* ...) ~body0 ~body1) (set-minus (set-union (Expr body0) (Expr body1)) (cons x x*))]
                [(jump ~x ~e* ...) (set-add x (union* (map Expr e*)))]
                [(letrec ([~x* ~le*] ...) ~body) (set-minus (union* (cons (Expr body) (map Expr le*))) x*)]
                [(let ([~x* ~e*] ...) ~body) (set-union (union* (map Expr e*)) (set-minus (Expr body) x*))]
                [(library ~d ([~x* ~s* ~e*] ...) ~body) (union* (cons (Expr body) (map Expr e*)))]
                [(call ~e ~e* ...) (union* (map Expr (cons e e*)))]
                [(and ~e* ...) (union* (map Expr e*))]
                [(or ~e* ...) (union* (map Expr e*))]
                [(throw ~e) (Expr e)]
                [(break) []]
                [(try-finally ~e0 ~e1) (set-union (Expr e0) (Expr e1))]
                [(try-catch ~e (~d* ~e*) ...) (union* (cons (Expr e) (map Expr e*)))]
                [(escape-frame ~x ~body) (set-minus (Expr body) [x])]
                [(try-handle ~x ~body0 ~body1) (set-union (Expr body0) (set-minus (Expr body1) [x]))]
                [(host-call ~d0 ~d1 ~st ~e* ...) (union* (map Expr e*))]
                [(host-field-get ~d0 ~d1 ~st ~e* ...) (union* (map Expr e*))]
                [(host-field-set ~d0 ~d1 ~st ~e* ...) (union* (map Expr e*))]
                [(host-new ~d ~e* ...) (union* (map Expr e*))]
                [(host-new-array ~d ~e) (Expr e)]
                [(host-cast ~d ~e) (Expr e)]
                [(host-is ~d ~e) (Expr e)]
                [(host-typeof ~d) []]
                [(host-using ~d) []]
                [(host-reference ~d) []]
                [(host-namespaces) []]
                [(unsupported ~d ~e* ...) (union* (map Expr e*))]
                [(clj-var-ref ~d ~st) []]
                [(clj-var-set! ~d ~e) (Expr e)]
                [(clj-def ~d ~st0 ~st1 ~e0 ~e1) (set-union (Expr e0) (Expr e1))]
                [(clj-invoke ~e ~e* ...) (union* (map Expr (cons e e*)))]
                [(clj-keyword-invoke ~d ~e ~e* ...) (union* (map Expr (cons e e*)))]
                [(clj-call ~d0 ~d1 ~e* ...) (union* (map Expr e*))]
                [(clj-static-call ~d0 ~d1 ~e* ...) (union* (map Expr e*))]
                [(clj-case ~d ~e0 ~e1 ~e* ...) (union* (map Expr (list* e0 e1 e*)))]
                [(clj-deftype ~d (~x* ...) ~le* ...) (set-minus (union* (map Expr le*)) x*)]
                [(box ~e) (Expr e)]
                [(unbox ~x) [x]]
                [(set-box! ~x ~e) (set-add x (Expr e))]))
            (Clause [cl]
              (nanopass-case [L2 Clause] cl
                [(clause (~x* ...) ~x ~body) (set-minus (Expr body) (if x (cons x x*) x*))]))]
      (Expr ir)
      (attach ir fvt))))
