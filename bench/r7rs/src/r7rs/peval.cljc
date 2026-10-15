;;; PEVAL -- A simple partial evaluator for Scheme, written by Marc Feeley.
;;
;; 移植说明：被处理的程序是 Scheme 数据，而且 simplify! 用 set-car! / set-cdr! 原地改写，所以程序的所有序对都用
;; util 的可变序对（空表 nil，#f 是 false），Clojure 读到的输入与例子在计时前转换成序对；car / cdr / cons / list / map /
;; append / length / assq / memq / equal? 等是本文件里作用于这种序对的私有版本。eq? 对符号用 =（Clojure 符号不驻留）。
;; Scheme 里 '() 为真：程序只在原版本来就区分 #f 的地方检验真假（assq / memq / binding-frame 等返回 #f 或序对）；
;; 原语 not 折叠用 false?。全局 *current-num* 与 ref-count / simplify! 里被 set! 的局部变量用 volatile!。
(ns r7rs.peval
  (:refer-clojure :exclude [cons list map quot some? test keep])
  (:require [r7rs.common :refer [hide read-input run-r7rs-benchmark]]
            [r7rs.util :as u :refer [mcons mcar mcdr set-mcar! set-mcdr! mpair?]]))

;; Scheme 序对与表的基本操作
(def ^:private car mcar)
(def ^:private cdr mcdr)
(def ^:private cons mcons)
(def ^:private set-car! set-mcar!)
(def ^:private set-cdr! set-mcdr!)
(def ^:private pair? mpair?)
(defn- null? [x] (nil? x))
(defn- caar [x] (car (car x)))
(defn- cadr [x] (car (cdr x)))
(defn- cdar [x] (cdr (car x)))
(defn- cddr [x] (cdr (cdr x)))
(defn- caddr [x] (car (cdr (cdr x))))
(defn- cdddr [x] (cdr (cdr (cdr x))))
(defn- cadddr [x] (car (cdr (cdr (cdr x)))))
(defn- cadar [x] (car (cdr (car x))))
(defn- caddar [x] (car (cdr (cdr (car x)))))

(defn- list
  ([] nil)
  ([a] (cons a nil))
  ([a b] (cons a (cons b nil)))
  ([a b c] (cons a (cons b (cons c nil))))
  ([a b c & more]
   (cons a (cons b (cons c (loop [xs (reverse more) acc nil]
                             (if (empty? xs) acc (recur (rest xs) (cons (first xs) acc)))))))))

(defn- map [f l]
  (if (pair? l) (cons (f (car l)) (map f (cdr l))) nil))

(defn- append [a b]
  (if (null? a) b (cons (car a) (append (cdr a) b))))

(defn- length [l]
  (loop [l l n 0] (if (null? l) n (recur (cdr l) (+ n 1)))))

(defn- list-ref [l i]
  (loop [l l i i] (if (zero? i) (car l) (recur (cdr l) (- i 1)))))

(defn- for-each [f l]
  (loop [l l] (when (pair? l) (f (car l)) (recur (cdr l)))))

(defn- eq?
  "Scheme 的 eq?：同一对象；符号按名字（Clojure 符号不驻留）；定长整数按值。"
  [a b]
  (or (identical? a b)
      (and (symbol? a) (symbol? b) (= a b))
      (and (int? a) (int? b) (== a b))))

(defn- memq [x l]
  (loop [l l] (cond (null? l) false (eq? x (car l)) l :else (recur (cdr l)))))

(defn- assq [x l]
  (loop [l l] (cond (null? l) false (eq? x (caar l)) (car l) :else (recur (cdr l)))))

(defn- equal? [a b]
  (cond (pair? a) (and (pair? b) (equal? (car a) (car b)) (equal? (cdr a) (cdr b)))
        (pair? b) false
        :else (= a b)))

(defn- list?* [x]
  (loop [x x] (cond (null? x) true (pair? x) (recur (cdr x)) :else false)))

;; Clojure 数据（读入的表）与序对之间的转换（计时外）
(defn- data->pairs [x]
  (if (seq? x)
    (loop [xs (reverse x) acc nil]
      (if (empty? xs) acc (recur (rest xs) (cons (data->pairs (first xs)) acc))))
    x))

(defn- pairs->data [x]
  (cond (pair? x) (loop [acc [] p x]
                    (cond (null? p) (apply clojure.core/list acc)
                          (pair? p) (recur (conj acc (pairs->data (car p))) (cdr p))
                          :else (u/fatal "improper list in result")))
        (nil? x) ()
        :else x))

;;------------------------------------------------------------------------------
;; Utilities

(defn xevery? [pred? l]
  (loop [l l]
    (or (null? l) (and (pred? (car l)) (recur (cdr l))))))

(defn some? [pred? l]
  (loop [l l]
    (if (null? l) false (or (pred? (car l)) (recur (cdr l))))))

(defn map2 [f l1 l2]
  ((fn lp [l1 l2]
     (if (pair? l1)
       (cons (f (car l1) (car l2)) (lp (cdr l1) (cdr l2)))
       nil))
   l1 l2))

(defn get-last-pair [l]
  (loop [l l]
    (let [x (cdr l)] (if (pair? x) (recur x) l))))

;;------------------------------------------------------------------------------
;; The partial evaluator.

(declare peval alphatize const-expr? const-value quot new-variables new-variable)

(defn partial-evaluate [proc args]
  (peval (alphatize proc nil) args))

(defn alphatize [exp env]
  ;; return a copy of 'exp' where each bound var has been renamed (to prevent aliasing problems)
  (letfn [(alpha [exp]
            (cond (const-expr? exp)
                  (quot (const-value exp))
                  (symbol? exp)
                  (let [x (assq exp env)] (if x (cdr x) exp))
                  (or (eq? (car exp) 'if) (eq? (car exp) 'begin))
                  (cons (car exp) (map alpha (cdr exp)))
                  (or (eq? (car exp) 'let) (eq? (car exp) 'letrec))
                  (let [new-env (new-variables (map car (cadr exp)) env)]
                    (list (car exp)
                          (map (fn [x]
                                 (list (cdr (assq (car x) new-env))
                                       (if (eq? (car exp) 'let)
                                         (alpha (cadr x))
                                         (alphatize (cadr x) new-env))))
                               (cadr exp))
                          (alphatize (caddr exp) new-env)))
                  (eq? (car exp) 'lambda)
                  (let [new-env (new-variables (cadr exp) env)]
                    (list 'lambda
                          (map (fn [x] (cdr (assq x new-env))) (cadr exp))
                          (alphatize (caddr exp) new-env)))
                  :else
                  (map alpha exp)))]
    (alpha exp)))

(defn const-expr? [expr]
  ;; is 'expr' a constant expression?
  (and (not (symbol? expr))
       (or (not (pair? expr))
           (eq? (car expr) 'quote))))

(defn const-value [expr]
  ;; return the value of a constant expression
  (if (pair? expr) (cadr expr) expr))

(defn quot [val]
  ;; make a quoted constant whose value is 'val'
  (list 'quote val))

(defn new-variables [parms env]
  (append (map (fn [x] (cons x (new-variable x))) parms) env))

(def ^:private current-num (volatile! 0))

(defn new-variable [name]
  (vreset! current-num (+ @current-num 1))
  (symbol (str (clojure.core/name name) "_" @current-num)))

;;------------------------------------------------------------------------------
;; (peval proc args)

(declare simplify! remove-constant beta-subst not-constant?)

(def ^:private empty-binding (data->pairs '(())))

(defn peval [proc args]
  (simplify!
   (let [parms (cadr proc)
         body (caddr proc)]
     (list 'lambda
           (remove-constant parms args)
           (beta-subst body
                       (map2 (fn [x y] (if (not-constant? y) empty-binding (cons x (quot y))))
                             parms
                             args))))))

(def not-constant (list '?))

(defn not-constant? [x] (identical? x not-constant))

(defn remove-constant [l a]
  (cond (null? l) nil
        (not-constant? (car a)) (cons (car l) (remove-constant (cdr l) (cdr a)))
        :else (remove-constant (cdr l) (cdr a))))

(defn extract-constant [l a]
  (cond (null? l) nil
        (not-constant? (car a)) (extract-constant (cdr l) (cdr a))
        :else (cons (car l) (extract-constant (cdr l) (cdr a)))))

(defn beta-subst [exp env]
  (letfn [(bs [exp]
            (cond (const-expr? exp)
                  (quot (const-value exp))
                  (symbol? exp)
                  (let [x (assq exp env)] (if x (cdr x) exp))
                  (or (eq? (car exp) 'if) (eq? (car exp) 'begin))
                  (cons (car exp) (map bs (cdr exp)))
                  (or (eq? (car exp) 'let) (eq? (car exp) 'letrec))
                  (list (car exp)
                        (map (fn [x] (list (car x) (bs (cadr x)))) (cadr exp))
                        (bs (caddr exp)))
                  (eq? (car exp) 'lambda)
                  (list 'lambda (cadr exp) (bs (caddr exp)))
                  :else
                  (map bs exp)))]
    (bs exp)))

;;------------------------------------------------------------------------------
;; The expression simplifier.

(declare ref-count binding-frame bound-expr add-binding for-each! arg-pattern constant-fold-global)

(def ^:private false-values (list false nil))
(def ^:private quoted-empty (data->pairs ''()))

(defn simplify! [exp]
  ;; simplify the expression 'exp' destructively (it is assumed that the code has been alphatized)
  (let [changed? (volatile! false)]
    (letfn [(simp! [where env]
              (letfn [(s! [where]
                        (let [exp (car where)]
                          (cond (const-expr? exp) nil ; leave constants the way they are
                                (symbol? exp) nil   ; leave variable references the way they are
                                (eq? (car exp) 'if) ; dead code removal for conditionals
                                (do (s! (cdr exp))  ; simplify the predicate
                                    (if (const-expr? (cadr exp)) ; is the predicate a constant?
                                      (do (set-car! where
                                                    (if (memq (const-value (cadr exp)) false-values) ; false?
                                                      (if (== (length exp) 3) quoted-empty (cadddr exp))
                                                      (caddr exp)))
                                          (s! where))
                                      (for-each! s! (cddr exp)))) ; simplify consequent and alt.

                                (eq? (car exp) 'begin)
                                (do (for-each! s! (cdr exp))
                                    (loop [exps exp] ; remove all useless expressions
                                      (when-not (null? (cddr exps)) ; not last expression?
                                        (let [x (cadr exps)]
                                          (recur (if (or (const-expr? x)
                                                         (symbol? x)
                                                         (and (pair? x) (eq? (car x) 'lambda)))
                                                   (do (set-cdr! exps (cddr exps)) exps)
                                                   (cdr exps))))))
                                    (when (null? (cddr exp)) ; only one expression in the begin?
                                      (set-car! where (cadr exp))))

                                (or (eq? (car exp) 'let) (eq? (car exp) 'letrec))
                                (let [new-env (cons exp env)]
                                  (letfn [(keep [i]
                                            (if (>= i (length (cadar where)))
                                              nil
                                              (let [var (car (list-ref (cadar where) i))
                                                    val (cadr (assq var (cadar where)))
                                                    refs (ref-count (car where) var)
                                                    self-refs (ref-count val var)
                                                    total-refs (- (car refs) (car self-refs))
                                                    oper-refs (- (cadr refs) (cadr self-refs))]
                                                (cond (== total-refs 0)
                                                      (keep (+ i 1))
                                                      (or (const-expr? val)
                                                          (symbol? val)
                                                          (and (pair? val)
                                                               (eq? (car val) 'lambda)
                                                               (== total-refs 1)
                                                               (== oper-refs 1)
                                                               (== (car self-refs) 0))
                                                          (and (caddr refs)
                                                               (== total-refs 1)))
                                                      (do (set-car! where
                                                                    (beta-subst (car where)
                                                                                (list (cons var val))))
                                                          (keep (+ i 1)))
                                                      :else
                                                      (cons var (keep (+ i 1)))))))]
                                    (simp! (cddr exp) new-env)
                                    (for-each! (fn [x] (simp! (cdar x) new-env)) (cadr exp))
                                    (let [to-keep (keep 0)]
                                      (if (< (length to-keep) (length (cadar where)))
                                        (do (if (null? to-keep)
                                              (set-car! where (caddar where))
                                              (set-car! (cdar where)
                                                        (map (fn [v] (assq v (cadar where))) to-keep)))
                                            (s! where))
                                        (when (null? to-keep)
                                          (set-car! where (caddar where)))))))

                                (eq? (car exp) 'lambda)
                                (simp! (cddr exp) (cons exp env))

                                :else
                                (do (for-each! s! exp)
                                    (cond (symbol? (car exp)) ; is the operator position a var ref?
                                          (let [frame (binding-frame (car exp) env)]
                                            (if frame ; is it a bound variable?
                                              (let [proc (bound-expr (car exp) frame)]
                                                (when (and (pair? proc)
                                                           (eq? (car proc) 'lambda)
                                                           (some? const-expr? (cdr exp)))
                                                  (let [args (arg-pattern (cdr exp))
                                                        new-proc (peval proc args)
                                                        new-args (remove-constant (cdr exp) args)]
                                                    (set-car! where
                                                              (cons (add-binding new-proc frame (car exp))
                                                                    new-args)))))
                                              (set-car! where
                                                        (constant-fold-global (car exp) (cdr exp)))))
                                          (not (pair? (car exp))) nil
                                          (eq? (caar exp) 'lambda)
                                          (do (set-car! where
                                                        (list 'let
                                                              (map2 list (cadar exp) (cdr exp))
                                                              (caddar exp)))
                                              (s! where)))))))]
                (s! where)))

            (remove-empty-calls! [where env]
              (letfn [(rec! [where]
                        (let [exp (car where)]
                          (cond (const-expr? exp) nil
                                (symbol? exp) nil
                                (eq? (car exp) 'if)
                                (do (rec! (cdr exp))
                                    (rec! (cddr exp))
                                    (rec! (cdddr exp)))
                                (eq? (car exp) 'begin)
                                (for-each! rec! (cdr exp))
                                (or (eq? (car exp) 'let) (eq? (car exp) 'letrec))
                                (let [new-env (cons exp env)]
                                  (remove-empty-calls! (cddr exp) new-env)
                                  (for-each! (fn [x] (remove-empty-calls! (cdar x) new-env))
                                             (cadr exp)))
                                (eq? (car exp) 'lambda)
                                (rec! (cddr exp))
                                :else
                                (do (for-each! rec! (cdr exp))
                                    (when (and (null? (cdr exp)) (symbol? (car exp)))
                                      (let [frame (binding-frame (car exp) env)]
                                        (when frame ; is it a bound variable?
                                          (let [proc (bound-expr (car exp) frame)]
                                            (when (and (pair? proc)
                                                       (eq? (car proc) 'lambda))
                                              (vreset! changed? true)
                                              (set-car! where (caddr proc)))))))))))]
                (rec! where)))]
      (let [x (list exp)]
        (loop []
          (vreset! changed? false)
          (simp! x nil)
          (remove-empty-calls! x nil)
          (if @changed? (recur) (car x)))))))

(defn ref-count [exp var]
  ;; compute how many references to variable 'var' are contained in 'exp'
  (let [total (volatile! 0)
        oper (volatile! 0)
        always-evaled (volatile! true)]
    (letfn [(rc [exp ae]
              (cond (const-expr? exp) nil
                    (symbol? exp)
                    (when (eq? exp var)
                      (vreset! total (+ @total 1))
                      (vreset! always-evaled (and ae @always-evaled)))
                    (eq? (car exp) 'if)
                    (do (rc (cadr exp) ae)
                        (for-each (fn [x] (rc x false)) (cddr exp)))
                    (eq? (car exp) 'begin)
                    (for-each (fn [x] (rc x ae)) (cdr exp))
                    (or (eq? (car exp) 'let) (eq? (car exp) 'letrec))
                    (do (for-each (fn [x] (rc (cadr x) ae)) (cadr exp))
                        (rc (caddr exp) ae))
                    (eq? (car exp) 'lambda)
                    (rc (caddr exp) false)
                    :else
                    (do (for-each (fn [x] (rc x ae)) exp)
                        (when (symbol? (car exp))
                          (when (eq? (car exp) var) (vreset! oper (+ @oper 1)))))))]
      (rc exp true)
      (list @total @oper @always-evaled))))

(defn binding-frame [var env]
  (cond (null? env) false
        (or (eq? (caar env) 'let) (eq? (caar env) 'letrec))
        (if (assq var (cadar env)) (car env) (recur var (cdr env)))
        (eq? (caar env) 'lambda)
        (if (memq var (cadar env)) (car env) (recur var (cdr env)))
        :else
        (u/fatal "ill-formed environment")))

(defn bound-expr [var frame]
  (cond (or (eq? (car frame) 'let) (eq? (car frame) 'letrec))
        (cadr (assq var (cadr frame)))
        (eq? (car frame) 'lambda)
        not-constant
        :else
        (u/fatal "ill-formed frame")))

(defn add-binding [val frame name]
  (letfn [(find-val [val bindings]
            (cond (null? bindings) false
                  (equal? val (cadar bindings)) ; *kludge* equal? is not exactly what
                  (caar bindings)               ; we want...
                  :else
                  (recur val (cdr bindings))))]
    (or (find-val val (cadr frame))
        (let [var (new-variable name)]
          (set-cdr! (get-last-pair (cadr frame)) (list (list var val)))
          var))))

(defn for-each! [proc! l]
  ;; call proc! on each CONS CELL in the list 'l'
  (when-not (null? l)
    (proc! l)
    (recur proc! (cdr l))))

(defn arg-pattern [exps]
  ;; return the argument pattern (i.e. the list of constants in 'exps' but with the
  ;; not-constant value wherever the corresponding expression in 'exps' is not a constant)
  (if (null? exps)
    nil
    (cons (if (const-expr? (car exps))
            (const-value (car exps))
            not-constant)
          (arg-pattern (cdr exps)))))

;;------------------------------------------------------------------------------
;; Knowledge about primitive procedures.

(declare sum product)

(def ^:private primitives
  (list
   (cons 'car (fn [args]
                (and (== (length args) 1)
                     (pair? (car args))
                     (quot (car (car args))))))
   (cons 'cdr (fn [args]
                (and (== (length args) 1)
                     (pair? (car args))
                     (quot (cdr (car args))))))
   (cons '+ (fn [args]
              (and (xevery? number? args)
                   (quot (sum args 0)))))
   (cons '* (fn [args]
              (and (xevery? number? args)
                   (quot (product args 1)))))
   (cons '- (fn [args]
              (and (> (length args) 0)
                   (xevery? number? args)
                   (quot (if (null? (cdr args))
                           (- (car args))
                           (- (car args) (sum (cdr args) 0)))))))
   (cons '/ (fn [args]
              (and (> (length args) 1)
                   (xevery? number? args)
                   (quot (if (null? (cdr args))
                           (/ (car args))
                           (/ (car args) (product (cdr args) 1)))))))
   (cons '< (fn [args]
              (and (== (length args) 2)
                   (xevery? number? args)
                   (quot (< (car args) (cadr args))))))
   (cons '= (fn [args]
              (and (== (length args) 2)
                   (xevery? number? args)
                   (quot (== (car args) (cadr args))))))
   (cons '> (fn [args]
              (and (== (length args) 2)
                   (xevery? number? args)
                   (quot (> (car args) (cadr args))))))
   (cons 'eq? (fn [args]
                (and (== (length args) 2)
                     (quot (eq? (car args) (cadr args))))))
   (cons 'not (fn [args]
                (and (== (length args) 1)
                     (quot (false? (car args))))))
   (cons 'null? (fn [args]
                  (and (== (length args) 1)
                       (quot (null? (car args))))))
   (cons 'pair? (fn [args]
                  (and (== (length args) 1)
                       (quot (pair? (car args))))))
   (cons 'symbol? (fn [args]
                    (and (== (length args) 1)
                         (quot (symbol? (car args))))))))

(defn sum [lst n]
  (if (null? lst) n (recur (cdr lst) (+ n (car lst)))))

(defn product [lst n]
  (if (null? lst) n (recur (cdr lst) (* n (car lst)))))

(defn reduce-global [name args]
  (let [x (assq name primitives)]
    (and x ((cdr x) args))))

(defn constant-fold-global [name exprs]
  (letfn [(flatten [args op]
            (cond (null? args)
                  nil
                  (and (pair? (car args)) (eq? (caar args) op))
                  (append (flatten (cdar args) op) (flatten (cdr args) op))
                  :else
                  (cons (car args) (flatten (cdr args) op))))]
    (let [args (if (or (eq? name '+) (eq? name '*)) ; associative ops
                 (flatten exprs name)
                 exprs)]
      (or (and (xevery? const-expr? args)
               (reduce-global name (map const-value args)))
          (let [pattern (arg-pattern args)]
            (let [non-const (remove-constant args pattern)
                  const (map const-value (extract-constant args pattern))]
              (cond (eq? name '+) ; + is commutative
                    (let [x (reduce-global '+ const)]
                      (if x
                        (let [y (const-value x)]
                          (cons '+ (if (== y 0) non-const (cons x non-const))))
                        (cons name args)))
                    (eq? name '*) ; * is commutative
                    (let [x (reduce-global '* const)]
                      (if x
                        (let [y (const-value x)]
                          (cons '* (if (== y 1) non-const (cons x non-const))))
                        (cons name args)))
                    (eq? name 'cons)
                    (cond (and (const-expr? (cadr args))
                               (null? (const-value (cadr args))))
                          (list 'list (car args))
                          (and (pair? (cadr args))
                               (eq? (car (cadr args)) 'list))
                          (cons 'list (cons (car args) (cdr (cadr args))))
                          :else
                          (cons name args))
                    :else
                    (cons name args))))))))

;;------------------------------------------------------------------------------
;; Examples:

(defn try-peval [proc args]
  (partial-evaluate proc args))

(def example1
  (data->pairs
   '(lambda (a b c)
      (if (null? a) b (+ (car a) c)))))

(def example2
  (data->pairs
   '(lambda (x y)
      (let ((q (lambda (a b) (if (< a 0) b (- 10 b)))))
        (if (< x 0) (q (- y) (- x)) (q y x))))))

(def example3
  (data->pairs
   '(lambda (l n)
      (letrec ((add-list
                (lambda (l n)
                  (if (null? l)
                    '()
                    (cons (+ (car l) n) (add-list (cdr l) n))))))
        (add-list l n)))))

(def example4
  (data->pairs
   '(lambda (exp env)
      (letrec ((eval
                (lambda (exp env)
                  (letrec ((eval-list
                            (lambda (l env)
                              (if (null? l)
                                '()
                                (cons (eval (car l) env)
                                      (eval-list (cdr l) env))))))
                    (if (symbol? exp) (lookup exp env)
                        (if (not (pair? exp)) exp
                            (if (eq? (car exp) 'quote) (car (cdr exp))
                                (apply (eval (car exp) env)
                                       (eval-list (cdr exp) env)))))))))
        (eval exp env)))))

(def example5
  (data->pairs
   '(lambda (a b)
      (letrec ((funct
                (lambda (x)
                  (+ x b (if (< x 1) 0 (funct (- x 1)))))))
        (funct a)))))

(def example6
  (data->pairs
   '(lambda ()
      (letrec ((fib
                (lambda (x)
                  (if (< x 2) x (+ (fib (- x 1)) (fib (- x 2)))))))
        (fib 10)))))

(def example7
  (data->pairs
   '(lambda (input)
      (letrec ((copy (lambda (in)
                       (if (pair? in)
                         (cons (copy (car in))
                               (copy (cdr in)))
                         in))))
        (copy input)))))

(def example8
  (data->pairs
   '(lambda (input)
      (letrec ((reverse (lambda (in result)
                          (if (pair? in)
                            (reverse (cdr in) (cons (car in) result))
                            result))))
        (reverse input '())))))

;; test 里的引用常量
(def ^:private c-10-11 (data->pairs '(10 11)))
(def ^:private c-123 (data->pairs '(1 2 3)))
(def ^:private c-f123 (data->pairs '(f 1 2 3)))
(def ^:private c-a-z (data->pairs '(a b c d e f g h i j k l m n o p q r s t u v w x y z)))

(defn test [input1 input2]
  (vreset! current-num 0)
  (list (try-peval example1 (list c-10-11 not-constant 1))
        (try-peval example2 (list not-constant 1))
        (try-peval example3 (list not-constant 1))
        (try-peval example3 (list c-123 not-constant))
        (try-peval example4 (list 'x not-constant))
        (try-peval example4 (list c-f123 not-constant))
        (try-peval example5 (list 5 not-constant))
        (try-peval example6 nil)
        (try-peval example7 (list c-a-z))
        (try-peval input1 input2)))

(defn -main [& _]
  (let [count (read-input)
        input1 (data->pairs (read-input))
        input2 (data->pairs (read-input))
        output (read-input)
        s2 (str count) s1 ""
        name "peval"]
    (run-r7rs-benchmark (str name ":" s2) count
                        (fn [] (test (hide count input1) (hide count input2)))
                        (fn [result]
                          (and (list?* result)
                               (== (length result) 10)
                               (= (pairs->data (list-ref result 9)) output))))))
