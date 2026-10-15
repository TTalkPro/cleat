;;; SCHEME -- A Scheme interpreter evaluating a sort, written by Marc Feeley.
;;
;; 移植说明：被解释的程序是 Scheme 数据，解释器用 set-cdr! 改写全局环境与全局变量单元，所以 Scheme 序对一律用
;; util 的可变序对（空表 nil，#f 是 false）；car / cdr / cons / list / append / length / assq / memv 等是本文件里作用于
;; 这种序对的私有版本。Scheme 里只有 #f 为假（'() 为真），所以被解释程序的 if / and / or / cond => / when 用 (false? x) 判断；
;; 编译器自身只在原版本来返回 #f 或序对的地方检验真假。运行时环境帧（rte）是对象数组；被解释的过程是 Clojure 函数，
;; 剩余参数与 apply 在 Clojure 参数序列与 Scheme 表之间转换。eq? 对符号用 =（Clojure 符号不驻留）。
;; scheme-global-variables 是被 set! 的全局变量，用 volatile!。
;; 原语表（def-proc）照原版列出；端口 / 文件 / 读写原语（call-with-input-file、read、display 等）以及修改字符串的
;; make-string / string-set! 省略（Clojure 字符串不可变，输入程序也用不到）；原版注释掉的算术原语同样不定义。
(ns r7rs.scheme
  (:refer-clojure :exclude [cons list list? reverse append])
  (:require [r7rs.common :refer [hide read-input run-r7rs-benchmark]]
            [r7rs.util :as u :refer [mcons mcar mcdr set-mcar! set-mcdr! mpair? make-vector]]))

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

(defn- seq->list
  "Clojure 序列 → Scheme 表。"
  [s]
  (loop [xs (clojure.core/reverse s) acc nil]
    (if (empty? xs) acc (recur (rest xs) (cons (first xs) acc)))))

(defn- list->seq
  "Scheme 表 → Clojure 序列（给 apply 用）。"
  [l]
  (loop [l l acc []]
    (if (pair? l) (recur (cdr l) (conj acc (car l))) (seq acc))))

(defn- list
  ([] nil)
  ([a] (cons a nil))
  ([a b] (cons a (cons b nil)))
  ([a b c] (cons a (cons b (cons c nil))))
  ([a b c & more] (cons a (cons b (cons c (seq->list more))))))

(defn- length [l]
  (loop [l l n 0] (if (pair? l) (recur (cdr l) (+ n 1)) n)))

(defn- append2 [a b]
  (if (pair? a) (cons (car a) (append2 (cdr a) b)) b))

(defn- append
  ([] nil)
  ([a] a)
  ([a b] (append2 a b))
  ([a b & more] (append2 a (apply append b more))))

(defn- reverse [l]
  (loop [l l acc nil] (if (pair? l) (recur (cdr l) (cons (car l) acc)) acc)))

(defn- list? [x]
  (loop [x x] (cond (null? x) true (pair? x) (recur (cdr x)) :else false)))

(defn- list-ref [l i]
  (loop [l l i i] (if (zero? i) (car l) (recur (cdr l) (- i 1)))))

(defn- eq?
  "Scheme 的 eq?：同一对象；符号按名字（Clojure 符号不驻留）；定长整数与字符按值。"
  [a b]
  (or (identical? a b)
      (and (symbol? a) (symbol? b) (= a b))
      (and (int? a) (int? b) (== a b))
      (and (char? a) (char? b) (= a b))))

(defn- eqv? [a b]
  (or (eq? a b)
      (and (number? a) (number? b) (= a b))))

(def ^:private objects-type (type (object-array 0)))
(defn- vector?* [x] (identical? (type x) objects-type))

(defn- equal? [a b]
  (cond (pair? a) (and (pair? b) (equal? (car a) (car b)) (equal? (cdr a) (cdr b)))
        (pair? b) false
        (string? a) (and (string? b) (= a b))
        (vector?* a) false
        :else (eqv? a b)))

(defn- mem-by [same? x l]
  (loop [l l] (cond (pair? l) (if (same? x (car l)) l (recur (cdr l))) :else false)))
(defn- ass-by [same? x l]
  (loop [l l] (cond (pair? l) (if (same? x (caar l)) (car l) (recur (cdr l))) :else false)))
(defn- memq [x l] (mem-by eq? x l))
(defn- memv [x l] (mem-by eqv? x l))
(defn- member [x l] (mem-by equal? x l))
(defn- assq [x l] (ass-by eq? x l))
(defn- assv [x l] (ass-by eqv? x l))
(defn- assoc* [x l] (ass-by equal? x l))

;; Clojure 数据（读入的表）与序对之间的转换（计时外）
(defn- data->pairs [x]
  (if (seq? x)
    (loop [xs (clojure.core/reverse x) acc nil]
      (if (empty? xs) acc (recur (rest xs) (cons (data->pairs (first xs)) acc))))
    x))

(defn- pairs->data [x]
  (cond (pair? x) (loop [acc [] p x]
                    (cond (null? p) (apply clojure.core/list acc)
                          (pair? p) (recur (conj acc (pairs->data (car p))) (cdr p))
                          :else (u/fatal "improper list in result")))
        (nil? x) ()
        :else x))

;; 对象数组（Scheme 向量）
(defn- vec2 [a b] (let [^objects v (object-array 2)] (aset v 0 a) (aset v 1 b) v))
(defn- vec3 [a b c] (let [^objects v (object-array 3)] (aset v 0 a) (aset v 1 b) (aset v 2 c) v))
(defn- vec4 [a b c d] (let [^objects v (object-array 4)] (aset v 0 a) (aset v 1 b) (aset v 2 c) (aset v 3 d) v))
(defn- vector-ref [^objects v i] (aget v (int i)))
(defn- vector-set! [^objects v i x] (aset v (int i) x))

;; - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - -

(declare scheme-comp)

(def scheme-global-environment
  (cons nil   ; environment chain
        nil)) ; macros

(defn scheme-eval [expr]
  (let [code (scheme-comp expr scheme-global-environment)]
    (code false)))

(defn scheme-add-macro [name proc]
  (set-cdr! scheme-global-environment
            (cons (cons name proc) (cdr scheme-global-environment)))
  name)

(defn scheme-error [msg & args]
  (u/fatal msg " " (pr-str (map pairs->data args))))

;; - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - -

(defn lst->vector [l]
  (let [n (length l)
        v (make-vector n)]
    (loop [l l i 0]
      (if (pair? l)
        (do (vector-set! v i (car l))
            (recur (cdr l) (+ i 1)))
        v))))

(defn vector->lst [^objects v]
  (loop [l nil i (- (alength v) 1)]
    (if (< i 0)
      l
      (recur (cons (vector-ref v i) l) (- i 1)))))

;; - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - -

(def scheme-syntactic-keywords
  (data->pairs
   '(quote quasiquote unquote unquote-splicing
           lambda if set! cond => else and or
           case let let* letrec begin do define
           define-macro)))

;; - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - -

(defn push-frame [frame env]
  (if (null? frame)
    env
    (cons (cons (car env) frame) (cdr env))))

;; 两个嵌套的命名 let：loop2 的自尾调用用 recur，loop2 尾调用外层 loop1 改为返回标记后由外层 recur
(defn lookup-var [name env]
  (loop [chain (car env) up 0]
    (if (null? chain)
      name
      (let [r (loop [chain chain up up frame (cdr chain) over 1]
                (cond (null? frame) ::up
                      (eq? (car frame) name) (cons up over)
                      :else (recur chain up (cdr frame) (+ over 1))))]
        (if (identical? r ::up)
          (recur (car chain) (+ up 1))
          r)))))

(defn macro? [name env]
  (assq name (cdr env)))

(defn push-macro [name proc env]
  (cons (car env) (cons (cons name proc) (cdr env))))

(defn lookup-macro [name env]
  (cdr (assq name (cdr env))))

;; - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - -

(defn variable [x]
  (when-not (symbol? x)
    (scheme-error "Identifier expected" x))
  (when (memq x scheme-syntactic-keywords)
    (scheme-error "Variable name can not be a syntactic keyword" x)))

(defn shape [form n]
  (loop [form form n n l form]
    (cond (<= n 0) true
          (pair? l) (recur form (- n 1) (cdr l))
          :else (scheme-error "Ill-constructed form" form))))

;; - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - -

(defn macro-expand [expr env]
  (apply (lookup-macro (car expr) env) (list->seq (cdr expr))))

;; - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - -

(declare gen-var-ref gen-cst gen-vector-form gen-append-form gen-cons-form gen-var-set
         gen-lambda-rest gen-lambda gen-if gen-when gen-or gen-and gen-cond-send gen-case
         gen-case-else gen-case-clause gen-letrec gen-sequence gen-combination gen-macro
         comp-sequence comp-letrec-aux comp-body definition-name definition-value
         comp-quasiquotation-list unquote-splicing? parms->frame rest-param?
         bindings->vars bindings->vals bindings->steps comp-vals)

(defn comp-var [expr env]
  (variable expr)
  (gen-var-ref (lookup-var expr env)))

(defn comp-self-eval [expr env]
  (gen-cst expr))

(defn comp-quote [expr env]
  (shape expr 2)
  (gen-cst (cadr expr)))

(defn comp-quasiquotation [form level env]
  (cond (== level 0)
        (scheme-comp form env)
        (pair? form)
        (cond (eq? (car form) 'quasiquote)
              (comp-quasiquotation-list form (+ level 1) env)
              (eq? (car form) 'unquote)
              (if (== level 1)
                (scheme-comp (cadr form) env)
                (comp-quasiquotation-list form (- level 1) env))
              (eq? (car form) 'unquote-splicing)
              (do (when (== level 1)
                    (scheme-error "Ill-placed 'unquote-splicing'" form))
                  (comp-quasiquotation-list form (- level 1) env))
              :else
              (comp-quasiquotation-list form level env))
        (vector?* form)
        (gen-vector-form
         (comp-quasiquotation-list (vector->lst form) level env))
        :else
        (gen-cst form)))

(defn comp-quasiquote [expr env]
  (comp-quasiquotation (cadr expr) 1 env))

(defn comp-quasiquotation-list [l level env]
  (if (pair? l)
    (let [first (car l)]
      (if (== level 1)
        (if (unquote-splicing? first)
          (do (shape first 2)
              (gen-append-form (scheme-comp (cadr first) env)
                               (comp-quasiquotation (cdr l) 1 env)))
          (gen-cons-form (comp-quasiquotation first level env)
                         (comp-quasiquotation (cdr l) level env)))
        (gen-cons-form (comp-quasiquotation first level env)
                       (comp-quasiquotation (cdr l) level env))))
    (comp-quasiquotation l level env)))

(defn unquote-splicing? [x]
  (if (pair? x)
    (if (eq? (car x) 'unquote-splicing) true false)
    false))

(defn comp-unquote [expr env]
  (scheme-error "Ill-placed 'unquote'" expr))

(defn comp-unquote-splicing [expr env]
  (scheme-error "Ill-placed 'unquote-splicing'" expr))

(defn comp-set! [expr env]
  (shape expr 3)
  (variable (cadr expr))
  (gen-var-set (lookup-var (cadr expr) env) (scheme-comp (caddr expr) env)))

(defn comp-lambda [expr env]
  (shape expr 3)
  (let [parms (cadr expr)]
    (let [frame (parms->frame parms)]
      (let [nb-vars (length frame)
            code (comp-body (cddr expr) (push-frame frame env))]
        (if (rest-param? parms)
          (gen-lambda-rest nb-vars code)
          (gen-lambda nb-vars code))))))

(defn parms->frame [parms]
  (cond (null? parms)
        nil
        (pair? parms)
        (let [x (car parms)]
          (variable x)
          (cons x (parms->frame (cdr parms))))
        :else
        (do (variable parms)
            (list parms))))

(defn rest-param? [parms]
  (cond (pair? parms) (recur (cdr parms))
        (null? parms) false
        :else true))

(defn comp-body [body env]
  (letfn [(letrec-defines [vars vals body env]
            (if (pair? body)
              (let [expr (car body)]
                (cond (not (pair? expr))
                      (letrec-defines* vars vals body env)
                      (macro? (car expr) env)
                      (recur vars vals (cons (macro-expand expr env) (cdr body)) env)
                      :else
                      (cond (eq? (car expr) 'begin)
                            (recur vars vals (append (cdr expr) (cdr body)) env)
                            (eq? (car expr) 'define)
                            (let [x (definition-name expr)]
                              (variable x)
                              (recur (cons x vars)
                                     (cons (definition-value expr) vals)
                                     (cdr body)
                                     env))
                            (eq? (car expr) 'define-macro)
                            (let [x (definition-name expr)]
                              (recur vars
                                     vals
                                     (cdr body)
                                     (push-macro x (scheme-eval (definition-value expr)) env)))
                            :else
                            (letrec-defines* vars vals body env))))
              (scheme-error "Body must contain at least one evaluable expression")))
          (letrec-defines* [vars vals body env]
            (if (null? vars)
              (comp-sequence body env)
              (comp-letrec-aux vars vals body env)))]
    (letrec-defines nil nil body env)))

(defn definition-name [expr]
  (shape expr 3)
  (let [pattern (cadr expr)]
    (let [name (if (pair? pattern) (car pattern) pattern)]
      (when-not (symbol? name)
        (scheme-error "Identifier expected" name))
      name)))

(defn definition-value [expr]
  (let [pattern (cadr expr)]
    (if (pair? pattern)
      (cons 'lambda (cons (cdr pattern) (cddr expr)))
      (caddr expr))))

(defn comp-if [expr env]
  (shape expr 3)
  (let [code1 (scheme-comp (cadr expr) env)
        code2 (scheme-comp (caddr expr) env)]
    (if (pair? (cdddr expr))
      (gen-if code1 code2 (scheme-comp (cadddr expr) env))
      (gen-when code1 code2))))

(defn comp-cond-aux [clauses env]
  (if (pair? clauses)
    (let [clause (car clauses)]
      (shape clause 1)
      (cond (eq? (car clause) 'else)
            (do (shape clause 2)
                (comp-sequence (cdr clause) env))
            (not (pair? (cdr clause)))
            (gen-or (scheme-comp (car clause) env)
                    (comp-cond-aux (cdr clauses) env))
            (eq? (cadr clause) '=>)
            (do (shape clause 3)
                (gen-cond-send (scheme-comp (car clause) env)
                               (scheme-comp (caddr clause) env)
                               (comp-cond-aux (cdr clauses) env)))
            :else
            (gen-if (scheme-comp (car clause) env)
                    (comp-sequence (cdr clause) env)
                    (comp-cond-aux (cdr clauses) env))))
    (gen-cst nil)))

(defn comp-cond [expr env]
  (comp-cond-aux (cdr expr) env))

(defn comp-and-aux [l env]
  (let [code (scheme-comp (car l) env)
        rest (cdr l)]
    (if (pair? rest) (gen-and code (comp-and-aux rest env)) code)))

(defn comp-and [expr env]
  (let [rest (cdr expr)]
    (if (pair? rest) (comp-and-aux rest env) (gen-cst true))))

(defn comp-or-aux [l env]
  (let [code (scheme-comp (car l) env)
        rest (cdr l)]
    (if (pair? rest) (gen-or code (comp-or-aux rest env)) code)))

(defn comp-or [expr env]
  (let [rest (cdr expr)]
    (if (pair? rest) (comp-or-aux rest env) (gen-cst false))))

(defn comp-case-aux [clauses env]
  (if (pair? clauses)
    (let [clause (car clauses)]
      (shape clause 2)
      (if (eq? (car clause) 'else)
        (gen-case-else (comp-sequence (cdr clause) env))
        (gen-case-clause (car clause)
                         (comp-sequence (cdr clause) env)
                         (comp-case-aux (cdr clauses) env))))
    (gen-case-else (gen-cst nil))))

(defn comp-case [expr env]
  (shape expr 3)
  (gen-case (scheme-comp (cadr expr) env)
            (comp-case-aux (cddr expr) env)))

(defn comp-let [expr env]
  (shape expr 3)
  (let [x (cadr expr)]
    (cond (symbol? x)
          (do (shape expr 4)
              (let [y (caddr expr)]
                (let [proc (cons 'lambda (cons (bindings->vars y) (cdddr expr)))]
                  (scheme-comp (cons (list 'letrec (list (list x proc)) x)
                                     (bindings->vals y))
                               env))))
          (pair? x)
          (scheme-comp (cons (cons 'lambda (cons (bindings->vars x) (cddr expr)))
                             (bindings->vals x))
                       env)
          :else
          (comp-body (cddr expr) env))))

(defn bindings->vars [bindings]
  (if (pair? bindings)
    (let [binding (car bindings)]
      (shape binding 2)
      (let [x (car binding)]
        (variable x)
        (cons x (bindings->vars (cdr bindings)))))
    nil))

(defn bindings->vals [bindings]
  (if (pair? bindings)
    (let [binding (car bindings)]
      (cons (cadr binding) (bindings->vals (cdr bindings))))
    nil))

(defn comp-let* [expr env]
  (shape expr 3)
  (let [bindings (cadr expr)]
    (if (pair? bindings)
      (scheme-comp (list 'let
                         (list (car bindings))
                         (cons 'let* (cons (cdr bindings) (cddr expr))))
                   env)
      (comp-body (cddr expr) env))))

(defn comp-letrec [expr env]
  (shape expr 3)
  (let [bindings (cadr expr)]
    (comp-letrec-aux (bindings->vars bindings)
                     (bindings->vals bindings)
                     (cddr expr)
                     env)))

(defn comp-letrec-aux [vars vals body env]
  (if (pair? vars)
    (let [new-env (push-frame vars env)]
      (gen-letrec (comp-vals vals new-env)
                  (comp-body body new-env)))
    (comp-body body env)))

(defn comp-vals [l env]
  (if (pair? l)
    (cons (scheme-comp (car l) env) (comp-vals (cdr l) env))
    nil))

(defn comp-sequence-aux [exprs env]
  (let [code (scheme-comp (car exprs) env)
        rest (cdr exprs)]
    (if (pair? rest) (gen-sequence code (comp-sequence-aux rest env)) code)))

(defn comp-sequence [exprs env]
  (if (pair? exprs)
    (comp-sequence-aux exprs env)
    (gen-cst nil)))

(defn comp-begin [expr env]
  (shape expr 2)
  (comp-sequence (cdr expr) env))

(def ^:private frame-false (list false))   ; '(#f)
(def ^:private ref-1-1 (cons 1 1))         ; '(1 . 1)
(def ^:private ref-0-1 (cons 0 1))         ; '(0 . 1)

(defn comp-do [expr env]
  (shape expr 3)
  (let [bindings (cadr expr)
        exit (caddr expr)]
    (shape exit 1)
    (let [vars (bindings->vars bindings)
          new-env1 (push-frame frame-false env)
          new-env2 (push-frame vars new-env1)]
      (gen-letrec
       (list
        (gen-lambda
         (length vars)
         (gen-if
          (scheme-comp (car exit) new-env2)
          (comp-sequence (cdr exit) new-env2)
          (gen-sequence
           (comp-sequence (cdddr expr) new-env2)
           (gen-combination
            (gen-var-ref ref-1-1)
            (comp-vals (bindings->steps bindings) new-env2))))))
       (gen-combination
        (gen-var-ref ref-0-1)
        (comp-vals (bindings->vals bindings) new-env1))))))

(defn bindings->steps [bindings]
  (if (pair? bindings)
    (let [binding (car bindings)]
      (cons (if (pair? (cddr binding)) (caddr binding) (car binding))
            (bindings->steps (cdr bindings))))
    nil))

(defn comp-define [expr env]
  (shape expr 3)
  (let [pattern (cadr expr)]
    (let [x (if (pair? pattern) (car pattern) pattern)]
      (variable x)
      (gen-sequence
       (gen-var-set (lookup-var x env)
                    (scheme-comp (if (pair? pattern)
                                   (cons 'lambda (cons (cdr pattern) (cddr expr)))
                                   (caddr expr))
                                 env))
       (gen-cst x)))))

(defn comp-define-macro [expr env]
  (let [x (definition-name expr)]
    (gen-macro x (scheme-eval (definition-value expr)))))

(defn comp-combination [expr env]
  (gen-combination (scheme-comp (car expr) env) (comp-vals (cdr expr) env)))

;;------------------------------------------------------------------------------

(declare scheme-global-var scheme-global-var-ref scheme-global-var-set!
         gen-slot-ref-0 gen-slot-ref-1 gen-slot-ref-up-2 gen-glo-ref
         gen-slot-set-0 gen-slot-set-1 gen-slot-set-n gen-glo-set)

(defn gen-rte-ref [up over]
  (case up
    0 (gen-slot-ref-0 over)
    1 (gen-slot-ref-1 over)
    (gen-slot-ref-up-2 (gen-rte-ref (- up 2) over))))

(defn gen-var-ref [var]
  (if (pair? var)
    (gen-rte-ref (car var) (cdr var))
    (gen-glo-ref (scheme-global-var var))))

(defn gen-slot-ref-0 [i]
  (case i
    0 (fn [rte] (vector-ref rte 0))
    1 (fn [rte] (vector-ref rte 1))
    2 (fn [rte] (vector-ref rte 2))
    3 (fn [rte] (vector-ref rte 3))
    (fn [rte] (vector-ref rte i))))

(defn gen-slot-ref-1 [i]
  (case i
    0 (fn [rte] (vector-ref (vector-ref rte 0) 0))
    1 (fn [rte] (vector-ref (vector-ref rte 0) 1))
    2 (fn [rte] (vector-ref (vector-ref rte 0) 2))
    3 (fn [rte] (vector-ref (vector-ref rte 0) 3))
    (fn [rte] (vector-ref (vector-ref rte 0) i))))

(defn gen-slot-ref-up-2 [code]
  (fn [rte] (code (vector-ref (vector-ref rte 0) 0))))

(defn gen-glo-ref [i]
  (fn [rte] (scheme-global-var-ref i)))

;; 原版的 case 用 eqv?：() / #f / #t 与几个小整数各有专门的闭包
(defn gen-cst [val]
  (cond (nil? val) (fn [rte] nil)
        (false? val) (fn [rte] false)
        (true? val) (fn [rte] true)
        (int? val) (case val
                     -2 (fn [rte] -2)
                     -1 (fn [rte] -1)
                     0 (fn [rte] 0)
                     1 (fn [rte] 1)
                     2 (fn [rte] 2)
                     (fn [rte] val))
        :else (fn [rte] val)))

(defn gen-append-form [code1 code2]
  (fn [rte] (append (code1 rte) (code2 rte))))

(defn gen-cons-form [code1 code2]
  (fn [rte] (cons (code1 rte) (code2 rte))))

(defn gen-vector-form [code]
  (fn [rte] (lst->vector (code rte))))

(defn gen-rte-set [up over code]
  (case up
    0 (gen-slot-set-0 over code)
    1 (gen-slot-set-1 over code)
    (gen-slot-set-n (gen-rte-ref (- up 2) 0) over code)))

(defn gen-var-set [var code]
  (if (pair? var)
    (gen-rte-set (car var) (cdr var) code)
    (gen-glo-set (scheme-global-var var) code)))

(defn gen-slot-set-0 [i code]
  (case i
    0 (fn [rte] (vector-set! rte 0 (code rte)))
    1 (fn [rte] (vector-set! rte 1 (code rte)))
    2 (fn [rte] (vector-set! rte 2 (code rte)))
    3 (fn [rte] (vector-set! rte 3 (code rte)))
    (fn [rte] (vector-set! rte i (code rte)))))

(defn gen-slot-set-1 [i code]
  (case i
    0 (fn [rte] (vector-set! (vector-ref rte 0) 0 (code rte)))
    1 (fn [rte] (vector-set! (vector-ref rte 0) 1 (code rte)))
    2 (fn [rte] (vector-set! (vector-ref rte 0) 2 (code rte)))
    3 (fn [rte] (vector-set! (vector-ref rte 0) 3 (code rte)))
    (fn [rte] (vector-set! (vector-ref rte 0) i (code rte)))))

(defn gen-slot-set-n [up i code]
  (case i
    0 (fn [rte] (vector-set! (up (vector-ref rte 0)) 0 (code rte)))
    1 (fn [rte] (vector-set! (up (vector-ref rte 0)) 1 (code rte)))
    2 (fn [rte] (vector-set! (up (vector-ref rte 0)) 2 (code rte)))
    3 (fn [rte] (vector-set! (up (vector-ref rte 0)) 3 (code rte)))
    (fn [rte] (vector-set! (up (vector-ref rte 0)) i (code rte)))))

(defn gen-glo-set [i code]
  (fn [rte] (scheme-global-var-set! i (code rte))))

;; 剩余参数：Clojure 的 & 参数序列转成 Scheme 表
(defn gen-lambda-1-rest [body]
  (fn [rte]
    (fn [& a]
      (body (vec2 rte (seq->list a))))))

(defn gen-lambda-2-rest [body]
  (fn [rte]
    (fn [a & b]
      (body (vec3 rte a (seq->list b))))))

(defn gen-lambda-3-rest [body]
  (fn [rte]
    (fn [a b & c]
      (body (vec4 rte a b (seq->list c))))))

(defn gen-lambda-n-rest [nb-vars body]
  (fn [rte]
    (fn [a b c & d]
      (let [x (make-vector (+ nb-vars 1))]
        (vector-set! x 0 rte)
        (vector-set! x 1 a)
        (vector-set! x 2 b)
        (vector-set! x 3 c)
        (loop [n nb-vars x x i 4 l (seq->list d)]
          (if (< i n)
            (do (vector-set! x i (car l)) (recur n x (+ i 1) (cdr l)))
            (vector-set! x i l)))
        (body x)))))

(defn gen-lambda-rest [nb-vars body]
  (case nb-vars
    1 (gen-lambda-1-rest body)
    2 (gen-lambda-2-rest body)
    3 (gen-lambda-3-rest body)
    (gen-lambda-n-rest nb-vars body)))

(defn gen-lambda-0 [body]
  (fn [rte]
    (fn []
      (body rte))))

(defn gen-lambda-1 [body]
  (fn [rte]
    (fn [a]
      (body (vec2 rte a)))))

(defn gen-lambda-2 [body]
  (fn [rte]
    (fn [a b]
      (body (vec3 rte a b)))))

(defn gen-lambda-3 [body]
  (fn [rte]
    (fn [a b c]
      (body (vec4 rte a b c)))))

(defn gen-lambda-n [nb-vars body]
  (fn [rte]
    (fn [a b c & d]
      (let [x (make-vector (+ nb-vars 1))]
        (vector-set! x 0 rte)
        (vector-set! x 1 a)
        (vector-set! x 2 b)
        (vector-set! x 3 c)
        (loop [n nb-vars x x i 4 l d]
          (when (<= i n)
            (vector-set! x i (first l))
            (recur n x (+ i 1) (next l))))
        (body x)))))

(defn gen-lambda [nb-vars body]
  (case nb-vars
    0 (gen-lambda-0 body)
    1 (gen-lambda-1 body)
    2 (gen-lambda-2 body)
    3 (gen-lambda-3 body)
    (gen-lambda-n nb-vars body)))

(defn gen-sequence [code1 code2]
  (fn [rte] (code1 rte) (code2 rte)))

;; 被解释程序的真假：只有 #f 为假
(defn gen-when [code1 code2]
  (fn [rte]
    (if (false? (code1 rte))
      nil
      (code2 rte))))

(defn gen-if [code1 code2 code3]
  (fn [rte]
    (if (false? (code1 rte))
      (code3 rte)
      (code2 rte))))

(defn gen-cond-send [code1 code2 code3]
  (fn [rte]
    (let [temp (code1 rte)]
      (if (false? temp)
        (code3 rte)
        ((code2 rte) temp)))))

(defn gen-and [code1 code2]
  (fn [rte]
    (let [temp (code1 rte)]
      (if (false? temp)
        temp
        (code2 rte)))))

(defn gen-or [code1 code2]
  (fn [rte]
    (let [temp (code1 rte)]
      (if (false? temp)
        (code2 rte)
        temp))))

(defn gen-case [code1 code2]
  (fn [rte] (code2 rte (code1 rte))))

(defn gen-case-clause [datums code1 code2]
  (fn [rte key] (if (memv key datums) (code1 rte) (code2 rte key))))

(defn gen-case-else [code]
  (fn [rte key] (code rte)))

(defn gen-letrec-1 [val1 body]
  (fn [rte]
    (let [x (vec2 rte false)]
      (vector-set! x 1 (val1 x))
      (body x))))

(defn gen-letrec-2 [val1 val2 body]
  (fn [rte]
    (let [x (vec3 rte false false)]
      (vector-set! x 1 (val1 x))
      (vector-set! x 2 (val2 x))
      (body x))))

(defn gen-letrec-3 [val1 val2 val3 body]
  (fn [rte]
    (let [x (vec4 rte false false false)]
      (vector-set! x 1 (val1 x))
      (vector-set! x 2 (val2 x))
      (vector-set! x 3 (val3 x))
      (body x))))

(defn gen-letrec-n [nb-vals vals body]
  (fn [rte]
    (let [x (make-vector (+ nb-vals 1))]
      (vector-set! x 0 rte)
      (loop [x x i 1 l vals]
        (when (pair? l)
          (vector-set! x i ((car l) x))
          (recur x (+ i 1) (cdr l))))
      (body x))))

(defn gen-letrec [vals body]
  (let [nb-vals (length vals)]
    (case nb-vals
      1 (gen-letrec-1 (car vals) body)
      2 (gen-letrec-2 (car vals) (cadr vals) body)
      3 (gen-letrec-3 (car vals) (cadr vals) (caddr vals) body)
      (gen-letrec-n nb-vals vals body))))

(defn gen-macro [name proc]
  (fn [rte] (scheme-add-macro name proc)))

(defn gen-combination-0 [oper]
  (fn [rte] ((oper rte))))

(defn gen-combination-1 [oper arg1]
  (fn [rte] ((oper rte) (arg1 rte))))

(defn gen-combination-2 [oper arg1 arg2]
  (fn [rte] ((oper rte) (arg1 rte) (arg2 rte))))

(defn gen-combination-3 [oper arg1 arg2 arg3]
  (fn [rte] ((oper rte) (arg1 rte) (arg2 rte) (arg3 rte))))

(defn gen-combination-n [oper args]
  (fn [rte]
    (letfn [(evaluate [l rte]
              (if (pair? l)
                (cons ((car l) rte) (evaluate (cdr l) rte))
                nil))]
      (apply (oper rte) (list->seq (evaluate args rte))))))

(defn gen-combination [oper args]
  (case (length args)
    0 (gen-combination-0 oper)
    1 (gen-combination-1 oper (car args))
    2 (gen-combination-2 oper (car args) (cadr args))
    3 (gen-combination-3 oper (car args) (cadr args) (caddr args))
    (gen-combination-n oper args)))

;; - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - -

(defn scheme-comp [expr env]
  (cond (symbol? expr)
        (comp-var expr env)
        (not (pair? expr))
        (comp-self-eval expr env)
        (macro? (car expr) env)
        (recur (macro-expand expr env) env)
        :else
        (cond (eq? (car expr) 'quote)            (comp-quote expr env)
              (eq? (car expr) 'quasiquote)       (comp-quasiquote expr env)
              (eq? (car expr) 'unquote)          (comp-unquote expr env)
              (eq? (car expr) 'unquote-splicing) (comp-unquote-splicing expr env)
              (eq? (car expr) 'set!)             (comp-set! expr env)
              (eq? (car expr) 'lambda)           (comp-lambda expr env)
              (eq? (car expr) 'if)               (comp-if expr env)
              (eq? (car expr) 'cond)             (comp-cond expr env)
              (eq? (car expr) 'and)              (comp-and expr env)
              (eq? (car expr) 'or)               (comp-or expr env)
              (eq? (car expr) 'case)             (comp-case expr env)
              (eq? (car expr) 'let)              (comp-let expr env)
              (eq? (car expr) 'let*)             (comp-let* expr env)
              (eq? (car expr) 'letrec)           (comp-letrec expr env)
              (eq? (car expr) 'begin)            (comp-begin expr env)
              (eq? (car expr) 'do)               (comp-do expr env)
              (eq? (car expr) 'define)           (comp-define expr env)
              (eq? (car expr) 'define-macro)     (comp-define-macro expr env)
              :else                              (comp-combination expr env))))

;; - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - -

(def ^:private scheme-global-variables (volatile! nil))

(defn scheme-global-var [name]
  (let [x (assq name @scheme-global-variables)]
    (if x
      x
      (let [y (cons name nil)]
        (vreset! scheme-global-variables (cons y @scheme-global-variables))
        y))))

(defn scheme-global-var-ref [i]
  (cdr i))

(defn scheme-global-var-set! [i val]
  (set-cdr! i val)
  nil)

(defn def-proc [name value]
  (scheme-global-var-set!
   (scheme-global-var name)
   value))

;; 原语（作用于 Scheme 序对 / 对象数组的版本）
(defn- cxr [path] (fn [x] (reduce (fn [x op] (if (= op \a) (car x) (cdr x))) x (clojure.core/reverse path))))
(defn- s-map [f l & ls]
  (if (empty? ls)
    ((fn m [l] (if (pair? l) (cons (f (car l)) (m (cdr l))) nil)) l)
    ((fn m [ls] (if (every? pair? ls) (cons (apply f (clojure.core/map car ls)) (m (clojure.core/map cdr ls))) nil))
     (clojure.core/cons l ls))))
(defn- s-for-each [f l & ls]
  (if (empty? ls)
    (loop [l l] (when (pair? l) (f (car l)) (recur (cdr l))))
    (loop [ls (clojure.core/cons l ls)]
      (when (every? pair? ls) (apply f (clojure.core/map car ls)) (recur (clojure.core/map cdr ls)))))
  nil)
(defn- s-apply [f & args]
  (apply f (concat (butlast args) (list->seq (last args)))))
(defn- chain-cmp [cmp key]
  (fn ([a b] (cmp (key a) (key b)))
      ([a b & more] (every? (fn [[a b]] (cmp (key a) (key b))) (partition 2 1 (list* a b more))))))
(defn- str-cmp [pred key] (chain-cmp (fn [a b] (pred (compare a b))) key))
(defn- char-cmp [pred key] (chain-cmp (fn [a b] (pred (compare a b))) key))
(defn- ci-str [s] (apply str (clojure.core/map u/char-downcase s)))
(defn- ci-char [c] (u/char-downcase c))

(def-proc 'not                            (fn [x] (false? x)))
(def-proc 'boolean?                       boolean?)
(def-proc 'eqv?                           eqv?)
(def-proc 'eq?                            eq?)
(def-proc 'equal?                         equal?)
(def-proc 'pair?                          (fn [obj] (pair? obj)))
(def-proc 'cons                           (fn [x y] (cons x y)))
(def-proc 'car                            (fn [x] (car x)))
(def-proc 'cdr                            (fn [x] (cdr x)))
(def-proc 'set-car!                       set-car!)
(def-proc 'set-cdr!                       set-cdr!)
(doseq [p ["aa" "ad" "da" "dd"
           "aaa" "aad" "ada" "add" "daa" "dad" "dda" "ddd"
           "aaaa" "aaad" "aada" "aadd" "adaa" "adad" "adda" "addd"
           "daaa" "daad" "dada" "dadd" "ddaa" "ddad" "ddda" "dddd"]]
  (def-proc (symbol (str "c" p "r")) (cxr p)))
(def-proc 'null?                          (fn [x] (null? x)))
(def-proc 'list?                          list?)
(def-proc 'list                           (fn [& xs] (seq->list xs)))
(def-proc 'length                         length)
(def-proc 'append                         append)
(def-proc 'reverse                        reverse)
(def-proc 'list-ref                       list-ref)
(def-proc 'memq                           memq)
(def-proc 'memv                           memv)
(def-proc 'member                         member)
(def-proc 'assq                           assq)
(def-proc 'assv                           assv)
(def-proc 'assoc                          assoc*)
(def-proc 'symbol?                        symbol?)
(def-proc 'symbol->string                 name)
(def-proc 'string->symbol                 symbol)
(def-proc 'number?                        number?)
(def-proc 'complex?                       number?)
(def-proc 'real?                          number?)
(def-proc 'rational?                      rational?)
(def-proc 'integer?                       integer?)
(def-proc 'exact?                         rational?)
(def-proc 'inexact?                       float?)
(def-proc 'max                            max)
(def-proc 'min                            min)
(def-proc '/                              /)
(def-proc 'abs                            abs)
(def-proc 'floor                          (fn [x] (if (integer? x) x (u/floor (double x)))))
(def-proc 'ceiling                        (fn [x] (if (integer? x) x (u/ceiling (double x)))))
(def-proc 'round                          (fn [x] (if (integer? x) x (u/fl-round (double x)))))
(def-proc 'exp                            (fn [x] (u/exp (double x))))
(def-proc 'log                            (fn [x] (u/log (double x))))
(def-proc 'sin                            (fn [x] (u/sin (double x))))
(def-proc 'cos                            (fn [x] (u/cos (double x))))
(def-proc 'atan                           (fn ([x] (u/atan (double x))) ([y x] (u/atan2 (double y) (double x)))))
(def-proc 'sqrt                           (fn [x] (u/sqrt (double x))))
(def-proc 'exact->inexact                 double)
(def-proc 'number->string                 str)
(def-proc 'string->number                 (fn [s] (or (parse-long s) (parse-double s) false)))
(def-proc 'char?                          char?)
(def-proc 'char=?                         (char-cmp zero? identity))
(def-proc 'char<?                         (char-cmp neg? identity))
(def-proc 'char>?                         (char-cmp pos? identity))
(def-proc 'char<=?                        (char-cmp (complement pos?) identity))
(def-proc 'char>=?                        (char-cmp (complement neg?) identity))
(def-proc 'char-ci=?                      (char-cmp zero? ci-char))
(def-proc 'char-ci<?                      (char-cmp neg? ci-char))
(def-proc 'char-ci>?                      (char-cmp pos? ci-char))
(def-proc 'char-ci<=?                     (char-cmp (complement pos?) ci-char))
(def-proc 'char-ci>=?                     (char-cmp (complement neg?) ci-char))
(def-proc 'char-alphabetic?               u/char-alphabetic?)
(def-proc 'char-whitespace?               u/char-whitespace?)
(def-proc 'char->integer                  u/char->integer)
(def-proc 'integer->char                  u/integer->char)
(def-proc 'char-upcase                    u/char-upcase)
(def-proc 'char-downcase                  u/char-downcase)
(def-proc 'string?                        string?)
(def-proc 'string                         (fn [& cs] (apply str cs)))
(def-proc 'string-length                  count)
(def-proc 'string-ref                     (fn [s i] (nth s i)))
(def-proc 'string=?                       (str-cmp zero? identity))
(def-proc 'string<?                       (str-cmp neg? identity))
(def-proc 'string>?                       (str-cmp pos? identity))
(def-proc 'string<=?                      (str-cmp (complement pos?) identity))
(def-proc 'string>=?                      (str-cmp (complement neg?) identity))
(def-proc 'string-ci=?                    (str-cmp zero? ci-str))
(def-proc 'string-ci<?                    (str-cmp neg? ci-str))
(def-proc 'string-ci>?                    (str-cmp pos? ci-str))
(def-proc 'string-ci<=?                   (str-cmp (complement pos?) ci-str))
(def-proc 'string-ci>=?                   (str-cmp (complement neg?) ci-str))
(def-proc 'substring                      subs)
(def-proc 'string-append                  str)
(def-proc 'vector?                        vector?*)
(def-proc 'make-vector                    make-vector)
(def-proc 'vector                         (fn [& xs] (object-array xs)))
(def-proc 'vector-length                  (fn [^objects v] (alength v)))
(def-proc 'vector-ref                     vector-ref)
(def-proc 'vector-set!                    vector-set!)
(def-proc 'procedure?                     fn?)
(def-proc 'apply                          s-apply)
(def-proc 'map                            s-map)
(def-proc 'for-each                       s-for-each)
;; call-with-current-continuation 原版即注释掉；端口与文件原语省略（见文件头）

(defn -main [& _]
  (let [count (read-input)
        input1 (data->pairs (read-input))
        output (read-input)
        s2 (str count) s1 ""
        name "scheme"]
    (run-r7rs-benchmark (str name ":" s2) count
                        (fn [] (scheme-eval (hide count input1)))
                        (fn [result] (= (pairs->data result) output)))))
