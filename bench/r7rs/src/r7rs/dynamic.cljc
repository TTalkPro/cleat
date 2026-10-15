;;; DYNAMIC -- Obtained from Andrew Wright.
;;
;; Fritz's dynamic type inferencer, set up to run on itself
;; (see the end of this file).
;;
;; 移植说明：
;; - 原版每次迭代都用 Scheme 的 read 读 inputs/dynamic.data（一份 Scheme 源码，含 #t / #f 与点对），Clojure 读取器读不了，
;;   所以本文件带一个小的 Scheme read（scheme-read：表、点对、' ` , ,@、字符串、#t / #f、#\字符、#( 向量、数、符号、; 注释），
;;   在计时内照原版逐个读出数据。dynamic-parse-file 读完后关闭文件（原版不关）。
;; - 并查集元素与类型变量靠 set-car! / set-cdr! 修改，AST 与绑定大量使用点对，所以所有 Scheme 序对一律用 util 的可变序对
;;   （空表 nil，#f 是 false）；car / cdr / cons / list / map / for-each / append / reverse / length / assv / memq / eqv? / equal?
;;   是本文件里作用于这种序对的私有版本，map / append 迭代构造（不随表长消耗栈；global-constraints 可能很长）。
;; - Scheme 符号用 Clojure 符号，eq? / eqv? 对符号按名字比较；被 set! 的全局变量用 volatile!。
;; - 内部自尾递归（pf*、pe*、psb、tail 等）用 loop / recur；其余递归保持普通调用。
(ns r7rs.dynamic
  (:refer-clojure :exclude [cons list map reverse empty boolean symbol for-each list? vector-of])
  (:require [r7rs.common :refer [hide read-input run-r7rs-benchmark]]
            [r7rs.util :as u :refer [mcons mcar mcdr set-mcar! set-mcdr! mpair? char-whitespace? file-reader]]))

;;----------------------------------------------------------------------------
;; Scheme 序对与表（私有）
;;----------------------------------------------------------------------------

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
(defn- caadr [x] (car (car (cdr x))))
(defn- cdadr [x] (cdr (car (cdr x))))

(defn- list
  ([] nil)
  ([a] (cons a nil))
  ([a b] (cons a (cons b nil)))
  ([a b c] (cons a (cons b (cons c nil))))
  ([a b c & more]
   (cons a (cons b (cons c (loop [xs (clojure.core/reverse more) acc nil]
                             (if (empty? xs) acc (recur (rest xs) (cons (first xs) acc)))))))))

(defn- map
  ([f l]
   (if (pair? l)
     (let [head (cons (f (car l)) nil)]
       (loop [l (cdr l) last head]
         (if (pair? l)
           (let [p (cons (f (car l)) nil)]
             (set-cdr! last p)
             (recur (cdr l) p))
           head)))
     nil))
  ([f l1 l2]
   (if (and (pair? l1) (pair? l2))
     (let [head (cons (f (car l1) (car l2)) nil)]
       (loop [l1 (cdr l1) l2 (cdr l2) last head]
         (if (and (pair? l1) (pair? l2))
           (let [p (cons (f (car l1) (car l2)) nil)]
             (set-cdr! last p)
             (recur (cdr l1) (cdr l2) p))
           head)))
     nil)))

(defn- for-each
  ([f l] (loop [l l] (when (pair? l) (f (car l)) (recur (cdr l)))))
  ([f l1 l2] (loop [l1 l1 l2 l2] (when (and (pair? l1) (pair? l2)) (f (car l1) (car l2)) (recur (cdr l1) (cdr l2))))))

(defn- append2 [a b]
  (if (pair? a)
    (let [head (cons (car a) nil)]
      (loop [a (cdr a) last head]
        (if (pair? a)
          (let [p (cons (car a) nil)]
            (set-cdr! last p)
            (recur (cdr a) p))
          (do (set-cdr! last b) head))))
    b))

(defn- append
  ([] nil)
  ([a] a)
  ([a b] (append2 a b))
  ([a b & more] (append2 a (apply append b more))))

(defn- reverse [l]
  (loop [l l acc nil] (if (pair? l) (recur (cdr l) (cons (car l) acc)) acc)))

(defn- length [l]
  (loop [l l n 0] (if (pair? l) (recur (cdr l) (+ n 1)) n)))

(defn- list? [x]
  (loop [x x] (cond (null? x) true (pair? x) (recur (cdr x)) :else false)))

(defn- eqv?
  "Scheme 的 eqv?：符号按名字，数按类别与值，字符按值，其余按同一对象。"
  [a b]
  (cond (identical? a b) true
        (symbol? a) (= a b)
        (number? a) (= a b)
        (char? a) (= a b)
        (boolean? a) (= a b)
        :else false))

(def ^:private eq? eqv?)

(defn- memq [x l]
  (loop [l l] (cond (null? l) false (eq? x (car l)) l :else (recur (cdr l)))))

(defn- assv [x l]
  (loop [l l] (cond (null? l) false (eqv? x (caar l)) (car l) :else (recur (cdr l)))))

(defn- equal? [a b]
  (cond (pair? a) (and (pair? b) (equal? (car a) (car b)) (equal? (cdr a) (cdr b)))
        (pair? b) false
        (vector? a) (and (vector? b) (== (count a) (count b)) (every? identity (clojure.core/map equal? a b)))
        :else (or (eqv? a b) (and (string? a) (= a b)))))

(defn- list->vector [l]
  (loop [v (transient []) l l] (if (pair? l) (recur (conj! v (car l)) (cdr l)) (persistent! v))))

(defn- vector->list [v]
  (loop [i (- (count v) 1) acc nil] (if (neg? i) acc (recur (- i 1) (cons (nth v i) acc)))))

(defn- error [who & xs] (u/fatal who ": " (apply str (interpose " " xs))))

;; Clojure 读取器读出的数据（检查用的期望结果，含 (a . b) 写法）→ 序对
(defn- data->pairs [x]
  (if (seq? x)
    (let [v (vec x) n (count v)
          [elems tail] (if (and (>= n 3) (= (nth v (- n 2)) '.))
                         [(subvec v 0 (- n 2)) (data->pairs (nth v (- n 1)))]
                         [v nil])]
      (loop [i (- (count elems) 1) acc tail]
        (if (neg? i) acc (recur (- i 1) (cons (data->pairs (nth elems i)) acc)))))
    x))

;;----------------------------------------------------------------------------
;; Scheme 的 read（原版用宿主的 read 读输入文件）
;;----------------------------------------------------------------------------

(def ^:private eof-object (Object.))
(defn- eof-object? [x] (identical? x eof-object))

(defn- delimiter? [c]
  (or (nil? c)
      (case c (\( \) \" \;) true (char-whitespace? c))))

(defn- skip-whitespace [rd pk]
  (loop []
    (let [c (pk)]
      (cond (nil? c) nil
            (char-whitespace? c) (do (rd) (recur))
            (= c \;) (do (loop [] (let [c (rd)] (when-not (or (nil? c) (= c \newline)) (recur))))
                         (recur))
            :else nil))))

(defn- read-token [rd pk prefix]
  (let [sb (u/string-builder)]
    (u/sb-append! sb prefix)
    (loop []
      (if (delimiter? (pk))
        (str sb)
        (do (u/sb-append-char! sb (rd)) (recur))))))

(defn- digit? [c] (and c (<= (int \0) (int c) (int \9))))

(defn- parse-atom [s]
  (let [c0 (nth s 0)
        numeric? (or (digit? c0)
                     (and (> (count s) 1) (#{\+ \- \.} c0) (or (digit? (nth s 1))
                                                              (and (= (nth s 1) \.) (> (count s) 2) (digit? (nth s 2))))))]
    (or (and numeric? (or (parse-long s) (parse-double s)))
        (clojure.core/symbol s))))

(declare scheme-read-datum)

(defn- build-list [v tail]
  (loop [i (- (count v) 1) acc tail]
    (if (neg? i) acc (recur (- i 1) (cons (nth v i) acc)))))

(defn- read-list-tail [rd pk]
  (loop [acc []]
    (skip-whitespace rd pk)
    (let [c (pk)]
      (cond (nil? c) (u/fatal "read: unexpected end of file in list")
            (= c \)) (do (rd) (build-list acc nil))
            (= c \.) (do (rd)
                         (if (delimiter? (pk))
                           (let [tail (scheme-read-datum rd pk)]
                             (skip-whitespace rd pk)
                             (if (= (rd) \))
                               (build-list acc tail)
                               (u/fatal "read: bad dotted list")))
                           (recur (conj acc (parse-atom (read-token rd pk "."))))))
            :else (recur (conj acc (scheme-read-datum rd pk)))))))

(defn- read-string-literal [rd]
  (let [sb (u/string-builder)]
    (loop []
      (let [c (rd)]
        (cond (nil? c) (u/fatal "read: unexpected end of file in string")
              (= c \") (str sb)
              (= c \\) (let [e (rd)]
                         (u/sb-append-char! sb (case e \n \newline \t \tab e))
                         (recur))
              :else (do (u/sb-append-char! sb c) (recur)))))))

(defn- read-hash [rd pk]
  (let [c (rd)]
    (case c
      (\t \f) (let [tok (read-token rd pk (str c))]
                (case tok ("t" "true") true ("f" "false") false (u/fatal "read: bad #" tok)))
      \\ (let [c1 (rd)]
           (if (and (u/char-alphabetic? c1) (not (delimiter? (pk))))
             (let [tok (read-token rd pk (str c1))]
               (case tok "space" \space "newline" \newline "tab" \tab
                     (if (== (count tok) 1) (nth tok 0) (u/fatal "read: bad character #\\" tok))))
             c1))
      \( (list->vector (read-list-tail rd pk))
      (u/fatal "read: bad # syntax " c))))

(defn- scheme-read-datum [rd pk]
  (skip-whitespace rd pk)
  (let [c (pk)]
    (if (nil? c)
      eof-object
      (case c
        \( (do (rd) (read-list-tail rd pk))
        \) (do (rd) (u/fatal "read: unexpected )"))
        \' (do (rd) (list 'quote (scheme-read-datum rd pk)))
        \` (do (rd) (list 'quasiquote (scheme-read-datum rd pk)))
        \, (do (rd) (if (= (pk) \@)
                      (do (rd) (list 'unquote-splicing (scheme-read-datum rd pk)))
                      (list 'unquote (scheme-read-datum rd pk))))
        \" (do (rd) (read-string-literal rd))
        \# (do (rd) (read-hash rd pk))
        (parse-atom (read-token rd pk ""))))))

(defn- scheme-read
  "(read port)：port 是 util/file-reader 的 [read-char peek-char close]。"
  [port]
  (scheme-read-datum (nth port 0) (nth port 1)))

;;----------------------------------------------------------------------------
;; Environment management
;;----------------------------------------------------------------------------

;; environments are lists of pairs, the first component being the key

;; bindings

(def gen-binding cons)
;; generates a type binding, binding a symbol to a type variable

(def binding-key car)
;; returns the key of a type binding

(def binding-value cdr)
;; returns the tvariable of a type binding

(defn key-show [key]
  ;; default show procedure for keys
  key)

(defn value-show [value]
  ;; default show procedure for values
  value)

(def ^:private colon (clojure.core/symbol ":"))

(defn binding-show [binding]
  ;; returns a printable representation of a type binding
  (cons (key-show (binding-key binding))
        (cons colon (value-show (binding-value binding)))))

;; environments

(def dynamic-empty-env nil)
;; returns the empty environment

(defn extend-env-with-binding [env binding]
  ;; extends env with a binding, which hides any other binding in env
  ;; for the same key (see dynamic-lookup)
  ;; returns the extended environment
  (cons binding env))

(defn extend-env-with-env [env ext-env]
  ;; extends environment env with environment ext-env
  ;; a binding for a key in ext-env hides any binding in env for
  ;; the same key (see dynamic-lookup)
  ;; returns the extended environment
  (append ext-env env))

(def dynamic-lookup (fn [x l] (assv x l)))
;; returns the first pair in env that matches the key; returns #f
;; if no such pair exists

(defn env->list [e]
  ;; converts an environment to a list of bindings
  e)

(defn env-show [env]
  ;; returns a printable list representation of a type environment
  (map binding-show env))

;;----------------------------------------------------------------------------
;;       Parsing for Scheme
;;----------------------------------------------------------------------------

;; Lexical notions

(def syntactic-keywords
  ;; source: IEEE Scheme, 7.1, <expression keyword>, <syntactic keyword>
  (data->pairs '(lambda if set! begin cond and or case let let* letrec do
                  quasiquote else => define unquote unquote-splicing)))

(declare dynamic-parse-action-null-const dynamic-parse-action-boolean-const dynamic-parse-action-char-const
         dynamic-parse-action-number-const dynamic-parse-action-string-const dynamic-parse-action-symbol-const
         dynamic-parse-action-vector-const dynamic-parse-action-pair-const dynamic-parse-action-var-def
         dynamic-parse-action-null-formal dynamic-parse-action-pair-formal dynamic-parse-action-variable
         dynamic-parse-action-identifier dynamic-parse-action-null-arg dynamic-parse-action-pair-arg
         dynamic-parse-action-procedure-call dynamic-parse-action-lambda-expression dynamic-parse-action-conditional
         dynamic-parse-action-empty dynamic-parse-action-assignment dynamic-parse-action-begin-expression
         dynamic-parse-action-cond-expression dynamic-parse-action-and-expression dynamic-parse-action-or-expression
         dynamic-parse-action-case-expression dynamic-parse-action-let-expression
         dynamic-parse-action-named-let-expression dynamic-parse-action-let*-expression
         dynamic-parse-action-letrec-expression dynamic-parse-action-definition
         dynamic-parse-action-function-definition dynamic-parse-action-commands
         list-of-1? list-of-2? list-of-3? list-of-list-of-2s?)

;; Datum

;; dynamic-parse-datum: parses nonterminal <datum>

(defn dynamic-parse-datum [e]
  ;; Source: IEEE Scheme, sect. 7.2, <datum>
  ;; Note: "'" is parsed as 'quote, "`" as 'quasiquote, "," as
  ;; 'unquote, ",@" as 'unquote-splicing (see sect. 4.2.5, p. 18)
  ;; ***Note***: quasi-quotations are not permitted! (It would be
  ;; necessary to pass the environment to dynamic-parse-datum.)
  (cond
    (null? e)
    (dynamic-parse-action-null-const)
    (boolean? e)
    (dynamic-parse-action-boolean-const e)
    (char? e)
    (dynamic-parse-action-char-const e)
    (number? e)
    (dynamic-parse-action-number-const e)
    (string? e)
    (dynamic-parse-action-string-const e)
    (symbol? e)
    (dynamic-parse-action-symbol-const e)
    (vector? e)
    (dynamic-parse-action-vector-const (map dynamic-parse-datum (vector->list e)))
    (pair? e)
    (dynamic-parse-action-pair-const (dynamic-parse-datum (car e))
                                     (dynamic-parse-datum (cdr e)))
    :else (error 'dynamic-parse-datum "Unknown datum:" e)))

;; VarDef

;; dynamic-parse-formal: parses nonterminal <variable> in defining occurrence position

(defn dynamic-parse-formal [f-env e]
  ;; e is an arbitrary object, f-env is a forbidden environment;
  ;; returns: a variable definition (a binding for the symbol), plus
  ;; the value of the binding as a result
  (if (symbol? e)
    (cond
      (memq e syntactic-keywords)
      (error 'dynamic-parse-formal "Illegal identifier (keyword):" e)
      (dynamic-lookup e f-env)
      (error 'dynamic-parse-formal "Duplicate variable definition:" e)
      :else (let [dynamic-parse-action-result (dynamic-parse-action-var-def e)]
              (cons (gen-binding e dynamic-parse-action-result)
                    dynamic-parse-action-result)))
    (error 'dynamic-parse-formal "Not an identifier:" e)))

;; dynamic-parse-formal*

(defn dynamic-parse-formal* [formals]
  ;; parses a list of formals and returns a pair consisting of generated
  ;; environment and list of parsing action results
  (let [pf* (fn [f-env results formals]
              ;; f-env: "forbidden" environment (to avoid duplicate defs)
              ;; results: the results of the parsing actions
              ;; formals: the unprocessed formals
              ;; Note: generates the results of formals in reverse order!
              (cond
                (null? formals)
                (cons f-env results)
                (pair? formals)
                (let [fst-formal (car formals)
                      binding-result (dynamic-parse-formal f-env fst-formal)
                      binding (car binding-result)
                      var-result (cdr binding-result)]
                  (recur (extend-env-with-binding f-env binding)
                         (cons var-result results)
                         (cdr formals)))
                :else (error 'dynamic-parse-formal* "Illegal formals:" formals)))]
    (let [renv-rres (pf* dynamic-empty-env nil formals)]
      (cons (car renv-rres) (reverse (cdr renv-rres))))))

;; dynamic-parse-formals: parses <formals>

(defn dynamic-parse-formals [formals]
  ;; parses <formals>; see IEEE Scheme, sect. 7.3
  ;; returns a pair: env and result
  (letfn [(pfs [f-env formals]
            (cond
              (null? formals)
              (cons dynamic-empty-env (dynamic-parse-action-null-formal))
              (pair? formals)
              (let [fst-formal (car formals)
                    rem-formals (cdr formals)
                    bind-res (dynamic-parse-formal f-env fst-formal)
                    bind (car bind-res)
                    res (cdr bind-res)
                    nf-env (extend-env-with-binding f-env bind)
                    renv-res* (pfs nf-env rem-formals)
                    renv (car renv-res*)
                    res* (cdr renv-res*)]
                (cons (extend-env-with-binding renv bind)
                      (dynamic-parse-action-pair-formal res res*)))
              :else
              (let [bind-res (dynamic-parse-formal f-env formals)
                    bind (car bind-res)
                    res (cdr bind-res)]
                (cons (extend-env-with-binding dynamic-empty-env bind)
                      res))))]
    (pfs dynamic-empty-env formals)))

;; Expr

(declare dynamic-parse-variable dynamic-parse-quote dynamic-parse-lambda dynamic-parse-if dynamic-parse-set
         dynamic-parse-begin dynamic-parse-cond dynamic-parse-case dynamic-parse-and dynamic-parse-or
         dynamic-parse-let dynamic-parse-let* dynamic-parse-letrec dynamic-parse-do dynamic-parse-quasiquote
         dynamic-parse-procedure-call dynamic-parse-body dynamic-parse-command* dynamic-parse-command)

;; dynamic-parse-expression: parses nonterminal <expression>

(defn dynamic-parse-expression [env e]
  (cond
    (symbol? e)
    (dynamic-parse-variable env e)
    (pair? e)
    (let [op (car e) args (cdr e)]
      (case op
        quote (dynamic-parse-quote env args)
        lambda (dynamic-parse-lambda env args)
        if (dynamic-parse-if env args)
        set! (dynamic-parse-set env args)
        begin (dynamic-parse-begin env args)
        cond (dynamic-parse-cond env args)
        case (dynamic-parse-case env args)
        and (dynamic-parse-and env args)
        or (dynamic-parse-or env args)
        let (dynamic-parse-let env args)
        let* (dynamic-parse-let* env args)
        letrec (dynamic-parse-letrec env args)
        do (dynamic-parse-do env args)
        quasiquote (dynamic-parse-quasiquote env args)
        (dynamic-parse-procedure-call env op args)))
    :else (dynamic-parse-datum e)))

;; dynamic-parse-expression*

(defn dynamic-parse-expression* [env exprs]
  ;; Parses lists of expressions (returns them in the right order!)
  (let [pe* (fn [results es]
              (cond
                (null? es) results
                (pair? es) (recur (cons (dynamic-parse-expression env (car es)) results) (cdr es))
                :else (error 'dynamic-parse-expression* "Not a list of expressions:" es)))]
    (reverse (pe* nil exprs))))

;; dynamic-parse-expressions

(defn dynamic-parse-expressions [env exprs]
  ;; parses lists of arguments of a procedure call
  (cond
    (null? exprs) (dynamic-parse-action-null-arg)
    (pair? exprs) (let [fst-expr (car exprs)
                        rem-exprs (cdr exprs)
                        fst-res (dynamic-parse-expression env fst-expr)
                        rem-res (dynamic-parse-expressions env rem-exprs)]
                    (dynamic-parse-action-pair-arg fst-res rem-res))
    :else (error 'dynamic-parse-expressions "Illegal expression list:" exprs)))

;; dynamic-parse-variable: parses variables (applied occurrences)

(defn dynamic-parse-variable [env e]
  (if (symbol? e)
    (if (memq e syntactic-keywords)
      (error 'dynamic-parse-variable "Illegal identifier (keyword):" e)
      (let [assoc-var-def (dynamic-lookup e env)]
        (if assoc-var-def
          (dynamic-parse-action-variable (binding-value assoc-var-def))
          (dynamic-parse-action-identifier e))))
    (error 'dynamic-parse-variable "Not an identifier:" e)))

;; dynamic-parse-procedure-call

(defn dynamic-parse-procedure-call [env op args]
  (dynamic-parse-action-procedure-call
   (dynamic-parse-expression env op)
   (dynamic-parse-expressions env args)))

;; dynamic-parse-quote

(defn dynamic-parse-quote [env args]
  (if (list-of-1? args)
    (dynamic-parse-datum (car args))
    (error 'dynamic-parse-quote "Not a datum (multiple arguments):" args)))

;; dynamic-parse-lambda

(defn dynamic-parse-lambda [env args]
  (if (pair? args)
    (let [formals (car args)
          body (cdr args)
          nenv-fresults (dynamic-parse-formals formals)
          nenv (car nenv-fresults)
          fresults (cdr nenv-fresults)]
      (dynamic-parse-action-lambda-expression
       fresults
       (dynamic-parse-body (extend-env-with-env env nenv) body)))
    (error 'dynamic-parse-lambda "Illegal formals/body:" args)))

;; dynamic-parse-body

(defn dynamic-parse-body [env body]
  ;; <body> = <definition>* <expression>+
  (letfn [(def-var* [f-env body]
            ;; finds the defined variables in a body and returns an
            ;; environment containing them
            (if (pair? body)
              (let [n-env (def-var f-env (car body))]
                (if n-env
                  (def-var* n-env (cdr body))
                  f-env))
              f-env))
          (def-var [f-env clause]
            ;; finds the defined variables in a single clause and extends
            ;; f-env accordingly; returns false if it's not a definition
            (if (pair? clause)
              (case (car clause)
                define (if (pair? (cdr clause))
                         (let [pattern (cadr clause)]
                           (cond
                             (symbol? pattern)
                             (extend-env-with-binding
                              f-env
                              (gen-binding pattern
                                           (dynamic-parse-action-var-def pattern)))
                             (and (pair? pattern) (symbol? (car pattern)))
                             (extend-env-with-binding
                              f-env
                              (gen-binding (car pattern)
                                           (dynamic-parse-action-var-def
                                            (car pattern))))
                             :else f-env))
                         f-env)
                begin (def-var* f-env (cdr clause))
                false)
              false))]
    (if (pair? body)
      (dynamic-parse-command* (def-var* env body) body)
      (error 'dynamic-parse-body "Illegal body:" body))))

;; dynamic-parse-if

(defn dynamic-parse-if [env args]
  (cond
    (list-of-3? args)
    (dynamic-parse-action-conditional
     (dynamic-parse-expression env (car args))
     (dynamic-parse-expression env (cadr args))
     (dynamic-parse-expression env (caddr args)))
    (list-of-2? args)
    (dynamic-parse-action-conditional
     (dynamic-parse-expression env (car args))
     (dynamic-parse-expression env (cadr args))
     (dynamic-parse-action-empty))
    :else (error 'dynamic-parse-if "Not an if-expression:" args)))

;; dynamic-parse-set

(defn dynamic-parse-set [env args]
  (if (list-of-2? args)
    (dynamic-parse-action-assignment
     (dynamic-parse-variable env (car args))
     (dynamic-parse-expression env (cadr args)))
    (error 'dynamic-parse-set "Not a variable/expression pair:" args)))

;; dynamic-parse-begin

(defn dynamic-parse-begin [env args]
  (dynamic-parse-action-begin-expression
   (dynamic-parse-body env args)))

;; dynamic-parse-cond

(declare dynamic-parse-cond-clause dynamic-parse-case-clause)

(defn dynamic-parse-cond [env args]
  (if (and (pair? args) (list? args))
    (dynamic-parse-action-cond-expression
     (map (fn [e] (dynamic-parse-cond-clause env e))
          args))
    (error 'dynamic-parse-cond "Not a list of cond-clauses:" args)))

;; dynamic-parse-cond-clause

(defn dynamic-parse-cond-clause [env e]
  ;; ***Note***: Only (<test> <sequence>) is permitted!
  (if (pair? e)
    (cons
     (if (eqv? (car e) 'else)
       (dynamic-parse-action-empty)
       (dynamic-parse-expression env (car e)))
     (dynamic-parse-body env (cdr e)))
    (error 'dynamic-parse-cond-clause "Not a cond-clause:" e)))

;; dynamic-parse-and

(defn dynamic-parse-and [env args]
  (if (list? args)
    (dynamic-parse-action-and-expression
     (dynamic-parse-expression* env args))
    (error 'dynamic-parse-and "Not a list of arguments:" args)))

;; dynamic-parse-or

(defn dynamic-parse-or [env args]
  (if (list? args)
    (dynamic-parse-action-or-expression
     (dynamic-parse-expression* env args))
    (error 'dynamic-parse-or "Not a list of arguments:" args)))

;; dynamic-parse-case

(defn dynamic-parse-case [env args]
  (if (and (list? args) (> (length args) 1))
    (dynamic-parse-action-case-expression
     (dynamic-parse-expression env (car args))
     (map (fn [e] (dynamic-parse-case-clause env e))
          (cdr args)))
    (error 'dynamic-parse-case "Not a list of clauses:" args)))

;; dynamic-parse-case-clause

(defn dynamic-parse-case-clause [env e]
  (if (pair? e)
    (cons
     (cond
       (eqv? (car e) 'else)
       (list (dynamic-parse-action-empty))
       (list? (car e))
       (map dynamic-parse-datum (car e))
       :else (error 'dynamic-parse-case-clause "Not a datum list:" (car e)))
     (dynamic-parse-body env (cdr e)))
    (error 'dynamic-parse-case-clause "Not case clause:" e)))

;; dynamic-parse-let

(declare dynamic-parse-named-let dynamic-parse-normal-let dynamic-parse-parallel-bindings
         dynamic-parse-sequential-bindings dynamic-parse-recursive-bindings dynamic-parse-define)

(defn dynamic-parse-let [env args]
  (if (pair? args)
    (if (symbol? (car args))
      (dynamic-parse-named-let env args)
      (dynamic-parse-normal-let env args))
    (error 'dynamic-parse-let "Illegal bindings/body:" args)))

;; dynamic-parse-normal-let

(defn dynamic-parse-normal-let [env args]
  ;; parses "normal" let-expressions
  (let [bindings (car args)
        body (cdr args)
        env-ast (dynamic-parse-parallel-bindings env bindings)
        nenv (car env-ast)
        bresults (cdr env-ast)]
    (dynamic-parse-action-let-expression
     bresults
     (dynamic-parse-body (extend-env-with-env env nenv) body))))

;; dynamic-parse-named-let

(defn dynamic-parse-named-let [env args]
  ;; parses a named let-expression
  (if (pair? (cdr args))
    (let [variable (car args)
          bindings (cadr args)
          body (cddr args)
          vbind-vres (dynamic-parse-formal dynamic-empty-env variable)
          vbind (car vbind-vres)
          vres (cdr vbind-vres)
          env-ast (dynamic-parse-parallel-bindings env bindings)
          nenv (car env-ast)
          bresults (cdr env-ast)]
      (dynamic-parse-action-named-let-expression
       vres bresults
       (dynamic-parse-body (extend-env-with-env
                            (extend-env-with-binding env vbind)
                            nenv) body)))
    (error 'dynamic-parse-named-let "Illegal named let-expression:" args)))

;; dynamic-parse-parallel-bindings

(defn dynamic-parse-parallel-bindings [env bindings]
  ;; returns a pair consisting of an environment
  ;; and a list of pairs (variable . asg)
  ;; ***Note***: the list of pairs is returned in reverse unzipped form!
  (if (list-of-list-of-2s? bindings)
    (let [env-formals-asg (dynamic-parse-formal* (map car bindings))
          nenv (car env-formals-asg)
          bresults (cdr env-formals-asg)
          exprs-asg (dynamic-parse-expression* env (map cadr bindings))]
      (cons nenv (cons bresults exprs-asg)))
    (error 'dynamic-parse-parallel-bindings "Not a list of bindings:" bindings)))

;; dynamic-parse-let*

(defn dynamic-parse-let* [env args]
  (if (pair? args)
    (let [bindings (car args)
          body (cdr args)
          env-ast (dynamic-parse-sequential-bindings env bindings)
          nenv (car env-ast)
          bresults (cdr env-ast)]
      (dynamic-parse-action-let*-expression
       bresults
       (dynamic-parse-body (extend-env-with-env env nenv) body)))
    (error 'dynamic-parse-let* "Illegal bindings/body:" args)))

;; dynamic-parse-sequential-bindings

(defn dynamic-parse-sequential-bindings [env bindings]
  ;; returns a pair consisting of an environment
  ;; and a list of pairs (variable . asg)
  ;; ***Note***: the list of pairs is returned in reverse unzipped form!
  (let [psb (fn [f-env c-env var-defs expr-asgs binds]
              ;; f-env: forbidden environment
              ;; c-env: constructed environment
              ;; var-defs: results of formals
              ;; expr-asgs: results of corresponding expressions
              ;; binds: reminding bindings to process
              (cond
                (null? binds)
                (cons f-env (cons var-defs expr-asgs))
                (pair? binds)
                (let [fst-bind (car binds)]
                  (if (list-of-2? fst-bind)
                    (let [fbinding-bres (dynamic-parse-formal f-env (car fst-bind))
                          fbind (car fbinding-bres)
                          bres (cdr fbinding-bres)
                          new-expr-asg (dynamic-parse-expression c-env (cadr fst-bind))]
                      (recur (extend-env-with-binding f-env fbind)
                             (extend-env-with-binding c-env fbind)
                             (cons bres var-defs)
                             (cons new-expr-asg expr-asgs)
                             (cdr binds)))
                    (error 'dynamic-parse-sequential-bindings "Illegal binding:" fst-bind)))
                :else (error 'dynamic-parse-sequential-bindings "Illegal bindings:" binds)))]
    (let [env-vdefs-easgs (psb dynamic-empty-env env nil nil bindings)]
      (cons (car env-vdefs-easgs)
            (cons (reverse (cadr env-vdefs-easgs))
                  (reverse (cddr env-vdefs-easgs)))))))

;; dynamic-parse-letrec

(defn dynamic-parse-letrec [env args]
  (if (pair? args)
    (let [bindings (car args)
          body (cdr args)
          env-ast (dynamic-parse-recursive-bindings env bindings)
          nenv (car env-ast)
          bresults (cdr env-ast)]
      (dynamic-parse-action-letrec-expression
       bresults
       (dynamic-parse-body (extend-env-with-env env nenv) body)))
    (error 'dynamic-parse-letrec "Illegal bindings/body:" args)))

;; dynamic-parse-recursive-bindings

(defn dynamic-parse-recursive-bindings [env bindings]
  ;; ***Note***: the list of pairs is returned in reverse unzipped form!
  (if (list-of-list-of-2s? bindings)
    (let [env-formals-asg (dynamic-parse-formal* (map car bindings))
          formals-env (car env-formals-asg)
          formals-res (cdr env-formals-asg)
          exprs-asg (dynamic-parse-expression*
                     (extend-env-with-env env formals-env)
                     (map cadr bindings))]
      (cons formals-env
            (cons formals-res exprs-asg)))
    (error 'dynamic-parse-recursive-bindings "Illegal bindings:" bindings)))

;; dynamic-parse-do

(defn dynamic-parse-do [env args]
  ;; parses do-expressions
  ;; ***Note***: Not implemented!
  (error 'dynamic-parse-do "Nothing yet..."))

;; dynamic-parse-quasiquote

(defn dynamic-parse-quasiquote [env args]
  ;; ***Note***: Not implemented!
  (error 'dynamic-parse-quasiquote "Nothing yet..."))

;; Command

;; dynamic-parse-command

(defn dynamic-parse-command [env c]
  (if (pair? c)
    (let [op (car c)
          args (cdr c)]
      (case op
        define (dynamic-parse-define env args)
        begin (dynamic-parse-action-begin-expression (dynamic-parse-command* env args))
        (dynamic-parse-expression env c)))
    (dynamic-parse-expression env c)))

;; dynamic-parse-command*

(defn dynamic-parse-command* [env commands]
  ;; parses a sequence of commands
  (if (list? commands)
    (map (fn [command] (dynamic-parse-command env command)) commands)
    (error 'dynamic-parse-command* "Invalid sequence of commands:" commands)))

;; dynamic-parse-define

(defn dynamic-parse-define [env args]
  ;; three cases -- see IEEE Scheme, sect. 5.2
  ;; ***Note***: the parser admits forms (define (x . y) ...)
  ;; ***Note***: Variables are treated as applied occurrences!
  (if (pair? args)
    (let [pattern (car args)
          exp-or-body (cdr args)]
      (cond
        (symbol? pattern)
        (if (list-of-1? exp-or-body)
          (dynamic-parse-action-definition
           (dynamic-parse-variable env pattern)
           (dynamic-parse-expression env (car exp-or-body)))
          (error 'dynamic-parse-define "Not a single expression:" exp-or-body))
        (pair? pattern)
        (let [function-name (car pattern)
              function-arg-names (cdr pattern)
              env-ast (dynamic-parse-formals function-arg-names)
              formals-env (car env-ast)
              formals-ast (cdr env-ast)]
          (dynamic-parse-action-function-definition
           (dynamic-parse-variable env function-name)
           formals-ast
           (dynamic-parse-body (extend-env-with-env env formals-env) exp-or-body)))
        :else (error 'dynamic-parse-define "Not a valid pattern:" pattern)))
    (error 'dynamic-parse-define "Not a valid definition:" args)))

;; Auxiliary routines

;; forall?

(defn forall? [pred list]
  (if (null? list)
    true
    (and (pred (car list)) (recur pred (cdr list)))))

;; list-of-1?

(defn list-of-1? [l]
  (and (pair? l) (null? (cdr l))))

;; list-of-2?

(defn list-of-2? [l]
  (and (pair? l) (pair? (cdr l)) (null? (cddr l))))

;; list-of-3?

(defn list-of-3? [l]
  (and (pair? l) (pair? (cdr l)) (pair? (cddr l)) (null? (cdddr l))))

;; list-of-list-of-2s?

(defn list-of-list-of-2s? [e]
  (cond
    (null? e)
    true
    (pair? e)
    (and (list-of-2? (car e)) (recur (cdr e)))
    :else false))

;; File processing

;; dynamic-parse-from-port

(defn dynamic-parse-from-port [port]
  (let [next-input (scheme-read port)]
    (if (eof-object? next-input)
      nil
      (dynamic-parse-action-commands
       (dynamic-parse-command dynamic-empty-env next-input)
       (dynamic-parse-from-port port)))))

;; dynamic-parse-file

(defn dynamic-parse-file [file-name]
  (let [input-port (file-reader file-name)]
    (try
      (dynamic-parse-from-port input-port)
      (finally ((nth input-port 2))))))

;;----------------------------------------------------------------------------
;; Implementation of Union/find data structure in Scheme
;;----------------------------------------------------------------------------

;; for union/find the following attributes are necessary: rank, parent
;; (see Tarjan, "Data structures and network algorithms", 1983)
;; In the Scheme realization an element is represented as a single
;; cons cell; its address is the element itself; the car field contains
;; the parent, the cdr field is an address for a cons
;; cell containing the rank (car field) and the information (cdr field)

(defn gen-element [info]
  ;; generates a new element: the parent field is initialized to '(),
  ;; the rank field to 0
  (cons nil (cons 0 info)))

(def info (fn [l] (cddr l)))
;; returns the information stored in an element

(defn set-info! [elem info]
  ;; sets the info-field of elem to info
  (set-cdr! (cdr elem) info))

(defn find! [elem]
  ;; finds the class representative of elem and sets the parent field
  ;; directly to the class representative (a class representative has
  ;; '() as its parent)
  (let [p-elem (car elem)]
    (if (null? p-elem)
      elem
      (let [rep-elem (find! p-elem)]
        (set-car! elem rep-elem)
        rep-elem))))

(defn link! [elem-1 elem-2]
  ;; links class elements by rank
  ;; they must be distinct class representatives
  ;; returns the class representative of the merged equivalence classes
  (let [rank-1 (cadr elem-1)
        rank-2 (cadr elem-2)]
    (cond
      (== rank-1 rank-2)
      (do (set-car! (cdr elem-2) (+ rank-2 1))
          (set-car! elem-1 elem-2)
          elem-2)
      (> rank-1 rank-2)
      (do (set-car! elem-2 elem-1)
          elem-1)
      :else
      (do (set-car! elem-1 elem-2)
          elem-2))))

(def asymm-link! (fn [l x] (set-car! l x)))

;;----------------------------------------------------------------------------
;; Type management
;;----------------------------------------------------------------------------

;; TVar

(def ^:private counter (volatile! 0))
;; counter for generating tvar id's

(defn gen-id []
  ;; generates a new id (for printing purposes)
  (vreset! counter (+ @counter 1))
  @counter)

(defn gen-tvar []
  ;; generates a new type variable from a new symbol
  ;; uses union/find elements with two info fields
  ;; a type variable has exactly four fields:
  ;; car:     TVar (the parent field; initially null)
  ;; cadr:    Number (the rank field; is always nonnegative)
  ;; caddr:   Symbol (the type variable identifier; used only for printing)
  ;; cdddr:   Type (the leq field; initially null)
  (gen-element (cons (gen-id) nil)))

(defn gen-type [tcon targs]
  ;; generates a new type variable with an associated type definition
  (gen-element (cons (gen-id) (cons tcon targs))))

(def dynamic (gen-element (cons 0 nil)))
;; the special type variable dynamic

;; Generic operations

(defn tvar-id [tvar]
  ;; returns the (printable) symbol representing the type variable
  (car (info tvar)))

(defn tvar-def [tvar]
  ;; returns the type definition (if any) of the type variable
  (cdr (info tvar)))

(defn set-def! [tvar tcon targs]
  ;; sets the type definition part of tvar to type
  (set-cdr! (info tvar) (cons tcon targs))
  nil)

(defn reset-def! [tvar]
  ;; resets the type definition part of tvar to nil
  (set-cdr! (info tvar) nil))

(def type-con (fn [l] (car l)))
;; returns the type constructor of a type definition

(def type-args (fn [l] (cdr l)))
;; returns the type variables of a type definition

(defn tvar->string [tvar]
  ;; converts a tvar's id to a string
  (if (eqv? (tvar-id tvar) 0)
    "Dynamic"
    (str "t#" (tvar-id tvar))))

(declare type-show ptype-con)

(defn tvar-show [tv]
  ;; returns a printable list representation of type variable tv
  (let [tv-rep (find! tv)
        tv-def (tvar-def tv-rep)]
    (cons (tvar->string tv-rep)
          (if (null? tv-def)
            nil
            (cons 'is (type-show tv-def))))))

(defn type-show [type]
  ;; returns a printable list representation of type definition type
  (cond
    (eqv? (type-con type) ptype-con)
    (let [new-tvar (gen-tvar)]
      (cons ptype-con
            (cons (tvar-show new-tvar)
                  (tvar-show ((type-args type) new-tvar)))))
    :else
    (cons (type-con type)
          (map (fn [tv] (tvar->string (find! tv)))
               (type-args type)))))

;; Special type operations

;; type constructor literals

(def boolean-con 'boolean)
(def char-con 'char)
(def null-con 'null)
(def number-con 'number)
(def pair-con 'pair)
(def procedure-con 'procedure)
(def string-con 'string)
(def symbol-con 'symbol)
(def vector-con 'vector)

;; type constants and type constructors

(declare pair)

(defn null []
  ;; ***Note***: Temporarily changed to be a pair!
  ;; (gen-type null-con '())
  (pair (gen-tvar) (gen-tvar)))
(defn boolean []
  (gen-type boolean-con nil))
(defn character []
  (gen-type char-con nil))
(defn number []
  (gen-type number-con nil))
(defn charseq []
  (gen-type string-con nil))
(defn symbol []
  (gen-type symbol-con nil))
(defn pair [tvar-1 tvar-2]
  (gen-type pair-con (list tvar-1 tvar-2)))
(defn array [tvar]
  (gen-type vector-con (list tvar)))
(defn procedure [arg-tvar res-tvar]
  (gen-type procedure-con (list arg-tvar res-tvar)))

;; equivalencing of type variables

(declare equiv-with-dynamic!)

(defn equiv! [tv1 tv2]
  (let [tv1-rep (find! tv1)
        tv2-rep (find! tv2)
        tv1-def (tvar-def tv1-rep)
        tv2-def (tvar-def tv2-rep)]
    (cond
      (eqv? tv1-rep tv2-rep)
      nil
      (eqv? tv2-rep dynamic)
      (equiv-with-dynamic! tv1-rep)
      (eqv? tv1-rep dynamic)
      (equiv-with-dynamic! tv2-rep)
      (null? tv1-def)
      (if (null? tv2-def)
        ;; both tv1 and tv2 are distinct type variables
        (link! tv1-rep tv2-rep)
        ;; tv1 is a type variable, tv2 is a (nondynamic) type
        (asymm-link! tv1-rep tv2-rep))
      (null? tv2-def)
      ;; tv1 is a (nondynamic) type, tv2 is a type variable
      (asymm-link! tv2-rep tv1-rep)
      (eqv? (type-con tv1-def) (type-con tv2-def))
      ;; both tv1 and tv2 are (nondynamic) types with equal numbers of
      ;; arguments
      (do (link! tv1-rep tv2-rep)
          (map equiv! (type-args tv1-def) (type-args tv2-def)))
      :else
      ;; tv1 and tv2 are types with distinct type constructors or different
      ;; numbers of arguments
      (do (equiv-with-dynamic! tv1-rep)
          (equiv-with-dynamic! tv2-rep))))
  nil)

(defn equiv-with-dynamic! [tv]
  (let [tv-rep (find! tv)]
    (when (not (eqv? tv-rep dynamic))
      (let [tv-def (tvar-def tv-rep)]
        (asymm-link! tv-rep dynamic)
        (when (not (null? tv-def))
          (map equiv-with-dynamic! (type-args tv-def))))))
  nil)

;;----------------------------------------------------------------------------
;; Polymorphic type management
;;----------------------------------------------------------------------------

;; type constructor literal for polymorphic types

(def ptype-con 'forall)

(defn forall [tv-func]
  (gen-type ptype-con tv-func))

(defn forall2 [tv-func2]
  (forall (fn [tv1]
            (forall (fn [tv2]
                      (tv-func2 tv1 tv2))))))

(defn forall3 [tv-func3]
  (forall (fn [tv1]
            (forall2 (fn [tv2 tv3]
                       (tv-func3 tv1 tv2 tv3))))))

(defn forall4 [tv-func4]
  (forall (fn [tv1]
            (forall3 (fn [tv2 tv3 tv4]
                       (tv-func4 tv1 tv2 tv3 tv4))))))

(defn forall5 [tv-func5]
  (forall (fn [tv1]
            (forall4 (fn [tv2 tv3 tv4 tv5]
                       (tv-func5 tv1 tv2 tv3 tv4 tv5))))))

;; (polymorphic) instantiation

(defn instantiate-type [tv]
  ;; instantiates type tv and returns a generic instance
  (let [tv-rep (find! tv)
        tv-def (tvar-def tv-rep)]
    (cond
      (null? tv-def)
      tv-rep
      (eqv? (type-con tv-def) ptype-con)
      (recur ((type-args tv-def) (gen-tvar)))
      :else
      tv-rep)))

(defn fix [tv-func]
  ;; forms a recursive type: the fixed point of type mapping tv-func
  (let [new-tvar (gen-tvar)
        inst-tvar (tv-func new-tvar)
        inst-def (tvar-def inst-tvar)]
    (if (null? inst-def)
      (error 'fix "Illegal recursive type:"
             (list (tvar-show new-tvar) '= (tvar-show inst-tvar)))
      (do (set-def! new-tvar
                    (type-con inst-def)
                    (type-args inst-def))
          new-tvar))))

;;----------------------------------------------------------------------------
;;       Constraint management
;;----------------------------------------------------------------------------

;; constraints

(def gen-constr (fn [a b] (cons a b)))
;; generates an equality between tvar1 and tvar2

(def constr-lhs (fn [c] (car c)))
;; returns the left-hand side of a constraint

(def constr-rhs (fn [c] (cdr c)))
;; returns the right-hand side of a constraint

(defn constr-show [c]
  (cons (tvar-show (car c))
        (cons '=
              (cons (tvar-show (cdr c)) nil))))

;; constraint set management

(def ^:private global-constraints (volatile! nil))

(defn init-global-constraints! []
  (vreset! global-constraints nil))

(defn add-constr! [lhs rhs]
  (vreset! global-constraints
           (cons (gen-constr lhs rhs) @global-constraints))
  nil)

(defn glob-constr-show []
  ;; returns printable version of global constraints
  (map constr-show @global-constraints))

;; constraint normalization

(declare normalize!)

(defn normalize-global-constraints! []
  (normalize! @global-constraints)
  (init-global-constraints!))

(defn normalize! [constraints]
  (map (fn [c]
         (equiv! (constr-lhs c) (constr-rhs c))) constraints))

;; ----------------------------------------------------------------------------
;; Abstract syntax definition and parse actions
;; ----------------------------------------------------------------------------

;; Abstract syntax operators

;; Datum

(def null-const 0)
(def boolean-const 1)
(def char-const 2)
(def number-const 3)
(def string-const 4)
(def symbol-const 5)
(def vector-const 6)
(def pair-const 7)

;; Bindings

(def var-def 8)
(def null-def 29)
(def pair-def 30)

;; Expr

(def variable 9)
(def identifier 10)
(def procedure-call 11)
(def lambda-expression 12)
(def conditional 13)
(def assignment 14)
(def cond-expression 15)
(def case-expression 16)
(def and-expression 17)
(def or-expression 18)
(def let-expression 19)
(def named-let-expression 20)
(def let*-expression 21)
(def letrec-expression 22)
(def begin-expression 23)
(def do-expression 24)
(def empty 25)
(def null-arg 31)
(def pair-arg 32)

;; Command

(def definition 26)
(def function-definition 27)
(def begin-command 28)

;; Parse actions for abstract syntax construction

(declare ast-gen)

(defn dynamic-parse-action-null-const []
  ;; dynamic-parse-action for '()
  (ast-gen null-const nil))

(defn dynamic-parse-action-boolean-const [e]
  ;; dynamic-parse-action for #f and #t
  (ast-gen boolean-const e))

(defn dynamic-parse-action-char-const [e]
  ;; dynamic-parse-action for character constants
  (ast-gen char-const e))

(defn dynamic-parse-action-number-const [e]
  ;; dynamic-parse-action for number constants
  (ast-gen number-const e))

(defn dynamic-parse-action-string-const [e]
  ;; dynamic-parse-action for string literals
  (ast-gen string-const e))

(defn dynamic-parse-action-symbol-const [e]
  ;; dynamic-parse-action for symbol constants
  (ast-gen symbol-const e))

(defn dynamic-parse-action-vector-const [e]
  ;; dynamic-parse-action for vector literals
  (ast-gen vector-const e))

(defn dynamic-parse-action-pair-const [e1 e2]
  ;; dynamic-parse-action for pairs
  (ast-gen pair-const (cons e1 e2)))

(defn dynamic-parse-action-var-def [e]
  ;; dynamic-parse-action for defining occurrences of variables;
  ;; e is a symbol
  (ast-gen var-def e))

(defn dynamic-parse-action-null-formal []
  ;; dynamic-parse-action for null-list of formals
  (ast-gen null-def nil))

(defn dynamic-parse-action-pair-formal [d1 d2]
  ;; dynamic-parse-action for non-null list of formals;
  ;; d1 is the result of parsing the first formal,
  ;; d2 the result of parsing the remaining formals
  (ast-gen pair-def (cons d1 d2)))

(defn dynamic-parse-action-variable [e]
  ;; dynamic-parse-action for applied occurrences of variables
  ;; ***Note***: e is the result of a dynamic-parse-action on the
  ;; corresponding variable definition!
  (ast-gen variable e))

(defn dynamic-parse-action-identifier [e]
  ;; dynamic-parse-action for undeclared identifiers (free variable
  ;; occurrences)
  ;; ***Note***: e is a symbol (legal identifier)
  (ast-gen identifier e))

(defn dynamic-parse-action-null-arg []
  ;; dynamic-parse-action for a null list of arguments in a procedure call
  (ast-gen null-arg nil))

(defn dynamic-parse-action-pair-arg [a1 a2]
  ;; dynamic-parse-action for a non-null list of arguments in a procedure call
  ;; a1 is the result of parsing the first argument,
  ;; a2 the result of parsing the remaining arguments
  (ast-gen pair-arg (cons a1 a2)))

(defn dynamic-parse-action-procedure-call [op args]
  ;; dynamic-parse-action for procedure calls: op function, args list of arguments
  (ast-gen procedure-call (cons op args)))

(defn dynamic-parse-action-lambda-expression [formals body]
  ;; dynamic-parse-action for lambda-abstractions
  (ast-gen lambda-expression (cons formals body)))

(defn dynamic-parse-action-conditional [test then-branch else-branch]
  ;; dynamic-parse-action for conditionals (if-then-else expressions)
  (ast-gen conditional (cons test (cons then-branch else-branch))))

(defn dynamic-parse-action-empty []
  ;; dynamic-parse-action for missing or empty field
  (ast-gen empty nil))

(defn dynamic-parse-action-assignment [lhs rhs]
  ;; dynamic-parse-action for assignment
  (ast-gen assignment (cons lhs rhs)))

(defn dynamic-parse-action-begin-expression [body]
  ;; dynamic-parse-action for begin-expression
  (ast-gen begin-expression body))

(defn dynamic-parse-action-cond-expression [clauses]
  ;; dynamic-parse-action for cond-expressions
  (ast-gen cond-expression clauses))

(defn dynamic-parse-action-and-expression [args]
  ;; dynamic-parse-action for and-expressions
  (ast-gen and-expression args))

(defn dynamic-parse-action-or-expression [args]
  ;; dynamic-parse-action for or-expressions
  (ast-gen or-expression args))

(defn dynamic-parse-action-case-expression [key clauses]
  ;; dynamic-parse-action for case-expressions
  (ast-gen case-expression (cons key clauses)))

(defn dynamic-parse-action-let-expression [bindings body]
  ;; dynamic-parse-action for let-expressions
  (ast-gen let-expression (cons bindings body)))

(defn dynamic-parse-action-named-let-expression [variable bindings body]
  ;; dynamic-parse-action for named-let expressions
  (ast-gen named-let-expression (cons variable (cons bindings body))))

(defn dynamic-parse-action-let*-expression [bindings body]
  ;; dynamic-parse-action for let-expressions
  (ast-gen let*-expression (cons bindings body)))

(defn dynamic-parse-action-letrec-expression [bindings body]
  ;; dynamic-parse-action for let-expressions
  (ast-gen letrec-expression (cons bindings body)))

(defn dynamic-parse-action-definition [variable expr]
  ;; dynamic-parse-action for simple definitions
  (ast-gen definition (cons variable expr)))

(defn dynamic-parse-action-function-definition [variable formals body]
  ;; dynamic-parse-action for function definitions
  (ast-gen function-definition (cons variable (cons formals body))))

(def dynamic-parse-action-commands (fn [a b] (cons a b)))
;; dynamic-parse-action for processing a command result followed by a the
;; result of processing the remaining commands

;; Pretty-printing abstract syntax trees

(declare ast-con ast-arg ast-tvar datum-show)

(defn ast-show [ast]
  ;; converts abstract syntax tree to list representation (Scheme program)
  ;; ***Note***: check translation of constructors to numbers at the top of the file
  (let [syntax-op (ast-con ast)
        syntax-arg (ast-arg ast)]
    (case syntax-op
      (0 1 2 3 4 8 10) syntax-arg
      (29 31) nil
      (30 32) (cons (ast-show (car syntax-arg)) (ast-show (cdr syntax-arg)))
      5 (list 'quote syntax-arg)
      6 (list->vector (map ast-show syntax-arg))
      7 (list 'cons (ast-show (car syntax-arg)) (ast-show (cdr syntax-arg)))
      9 (ast-arg syntax-arg)
      11 (cons (ast-show (car syntax-arg)) (ast-show (cdr syntax-arg)))
      12 (cons 'lambda (cons (ast-show (car syntax-arg))
                             (map ast-show (cdr syntax-arg))))
      13 (cons 'if (cons (ast-show (car syntax-arg))
                         (cons (ast-show (cadr syntax-arg))
                               (let [alt (cddr syntax-arg)]
                                 (if (eqv? (ast-con alt) empty)
                                   nil
                                   (list (ast-show alt)))))))
      14 (list 'set! (ast-show (car syntax-arg)) (ast-show (cdr syntax-arg)))
      15 (cons 'cond
               (map (fn [cc]
                      (let [guard (car cc)
                            body (cdr cc)]
                        (cons
                         (if (eqv? (ast-con guard) empty)
                           'else
                           (ast-show guard))
                         (map ast-show body))))
                    syntax-arg))
      16 (cons 'case
               (cons (ast-show (car syntax-arg))
                     (map (fn [cc]
                            (let [data (car cc)]
                              (if (and (pair? data)
                                       (eqv? (ast-con (car data)) empty))
                                (cons 'else
                                      (map ast-show (cdr cc)))
                                (cons (map datum-show data)
                                      (map ast-show (cdr cc))))))
                          (cdr syntax-arg))))
      17 (cons 'and (map ast-show syntax-arg))
      18 (cons 'or (map ast-show syntax-arg))
      19 (cons 'let
               (cons (map
                      (fn [vd e]
                        (list (ast-show vd) (ast-show e)))
                      (caar syntax-arg)
                      (cdar syntax-arg))
                     (map ast-show (cdr syntax-arg))))
      20 (cons 'let
               (cons (ast-show (car syntax-arg))
                     (cons (map
                            (fn [vd e]
                              (list (ast-show vd) (ast-show e)))
                            (caadr syntax-arg)
                            (cdadr syntax-arg))
                           (map ast-show (cddr syntax-arg)))))
      21 (cons 'let*
               (cons (map
                      (fn [vd e]
                        (list (ast-show vd) (ast-show e)))
                      (caar syntax-arg)
                      (cdar syntax-arg))
                     (map ast-show (cdr syntax-arg))))
      22 (cons 'letrec
               (cons (map
                      (fn [vd e]
                        (list (ast-show vd) (ast-show e)))
                      (caar syntax-arg)
                      (cdar syntax-arg))
                     (map ast-show (cdr syntax-arg))))
      23 (cons 'begin
               (map ast-show syntax-arg))
      24 (error 'ast-show "Do expressions not handled!" syntax-arg)
      25 (error 'ast-show "This can't happen: empty encountered!")
      26 (list 'define
               (ast-show (car syntax-arg))
               (ast-show (cdr syntax-arg)))
      27 (cons 'define
               (cons
                (cons (ast-show (car syntax-arg))
                      (ast-show (cadr syntax-arg)))
                (map ast-show (cddr syntax-arg))))
      28 (cons 'begin
               (map ast-show syntax-arg))
      (error 'ast-show "Unknown abstract syntax operator:" syntax-op))))

;; ast*-show

(defn ast*-show [p]
  ;; shows a list of abstract syntax trees
  (map ast-show p))

;; datum-show

(defn datum-show [ast]
  ;; prints an abstract syntax tree as a datum
  (case (ast-con ast)
    (0 1 2 3 4 5) (ast-arg ast)
    6 (list->vector (map datum-show (ast-arg ast)))
    7 (cons (datum-show (car (ast-arg ast))) (datum-show (cdr (ast-arg ast))))
    (error 'datum-show "This should not happen!")))

;; write-to-port / write-file：原版把程序写到端口 / 文件，基准不调用，省略。

;; ----------------------------------------------------------------------------
;; Typed abstract syntax tree management: constraint generation, display, etc.
;; ----------------------------------------------------------------------------

(declare tail convert-tvars dynamic-top-level-env)

;; Abstract syntax operations, incl. constraint generation

(defn ast-gen [syntax-op arg]
  ;; generates all attributes and performs semantic side effects
  (let [ntvar
        (case syntax-op
          (0 29 31) (null)
          1 (boolean)
          2 (character)
          3 (number)
          4 (charseq)
          5 (symbol)
          6 (let [aux-tvar (gen-tvar)]
              (for-each (fn [t]
                          (add-constr! t aux-tvar))
                        (map ast-tvar arg))
              (array aux-tvar))
          (7 30 32) (let [t1 (ast-tvar (car arg))
                          t2 (ast-tvar (cdr arg))]
                      (pair t1 t2))
          8 (gen-tvar)
          9 (ast-tvar arg)
          10 (let [in-env (dynamic-lookup arg @dynamic-top-level-env)]
               (if in-env
                 (instantiate-type (binding-value in-env))
                 (let [new-tvar (gen-tvar)]
                   (vreset! dynamic-top-level-env (extend-env-with-binding
                                                   @dynamic-top-level-env
                                                   (gen-binding arg new-tvar)))
                   new-tvar)))
          11 (let [new-tvar (gen-tvar)]
               (add-constr! (procedure (ast-tvar (cdr arg)) new-tvar)
                            (ast-tvar (car arg)))
               new-tvar)
          12 (procedure (ast-tvar (car arg))
                        (ast-tvar (tail (cdr arg))))
          13 (let [t-test (ast-tvar (car arg))
                   t-consequent (ast-tvar (cadr arg))
                   t-alternate (ast-tvar (cddr arg))]
               (add-constr! (boolean) t-test)
               (add-constr! t-consequent t-alternate)
               t-consequent)
          14 (let [var-tvar (ast-tvar (car arg))
                   exp-tvar (ast-tvar (cdr arg))]
               (add-constr! var-tvar exp-tvar)
               var-tvar)
          15 (let [new-tvar (gen-tvar)]
               (for-each (fn [body]
                           (add-constr! (ast-tvar (tail body)) new-tvar))
                         (map cdr arg))
               (for-each (fn [e]
                           (add-constr! (boolean) (ast-tvar e)))
                         (map car arg))
               new-tvar)
          16 (let [new-tvar (gen-tvar)
                   t-key (ast-tvar (car arg))
                   case-clauses (cdr arg)]
               (for-each (fn [exprs]
                           (for-each (fn [e]
                                       (add-constr! (ast-tvar e) t-key))
                                     exprs))
                         (map car case-clauses))
               (for-each (fn [body]
                           (add-constr! (ast-tvar (tail body)) new-tvar))
                         (map cdr case-clauses))
               new-tvar)
          (17 18) (do (for-each (fn [e]
                                  (add-constr! (boolean) (ast-tvar e)))
                                arg)
                      (boolean))
          (19 21 22) (let [var-def-tvars (map ast-tvar (caar arg))
                           def-expr-types (map ast-tvar (cdar arg))
                           body-type (ast-tvar (tail (cdr arg)))]
                       (for-each add-constr! var-def-tvars def-expr-types)
                       body-type)
          20 (let [var-def-tvars (map ast-tvar (caadr arg))
                   def-expr-types (map ast-tvar (cdadr arg))
                   body-type (ast-tvar (tail (cddr arg)))
                   named-var-type (ast-tvar (car arg))]
               (for-each add-constr! var-def-tvars def-expr-types)
               (add-constr! (procedure (convert-tvars var-def-tvars) body-type)
                            named-var-type)
               body-type)
          23 (ast-tvar (tail arg))
          24 (error 'ast-gen "Do-expressions not handled! (Argument: ~s) arg")
          25 (gen-tvar)
          26 (let [t-var (ast-tvar (car arg))
                   t-exp (ast-tvar (cdr arg))]
               (add-constr! t-var t-exp)
               t-var)
          27 (let [t-var (ast-tvar (car arg))
                   t-formals (ast-tvar (cadr arg))
                   t-body (ast-tvar (tail (cddr arg)))]
               (add-constr! (procedure t-formals t-body) t-var)
               t-var)
          28 (gen-tvar)
          (error 'ast-gen "Can't handle syntax operator:" syntax-op))]
    (cons syntax-op (cons ntvar arg))))

(def ast-con car)
;; extracts the ast-constructor from an abstract syntax tree

(def ast-arg cddr)
;; extracts the ast-argument from an abstract syntax tree

(def ast-tvar cadr)
;; extracts the tvar from an abstract syntax tree

;; tail

(defn tail [l]
  ;; returns the tail of a nonempty list
  (if (null? (cdr l))
    (car l)
    (recur (cdr l))))

;; convert-tvars

(defn convert-tvars [tvar-list]
  ;; converts a list of tvars to a single tvar
  (cond
    (null? tvar-list) (null)
    (pair? tvar-list) (pair (car tvar-list)
                            (convert-tvars (cdr tvar-list)))
    :else (error 'convert-tvars "Not a list of tvars:" tvar-list)))

;; Pretty-printing abstract syntax trees

(defn tast-show [ast]
  ;; converts abstract syntax tree to list representation (Scheme program)
  (let [syntax-op (ast-con ast)
        syntax-tvar (tvar-show (ast-tvar ast))
        syntax-arg (ast-arg ast)]
    (cons
     (case syntax-op
       (0 1 2 3 4 8 10) syntax-arg
       (29 31) nil
       (30 32) (cons (tast-show (car syntax-arg))
                     (tast-show (cdr syntax-arg)))
       5 (list 'quote syntax-arg)
       6 (list->vector (map tast-show syntax-arg))
       7 (list 'cons (tast-show (car syntax-arg))
               (tast-show (cdr syntax-arg)))
       9 (ast-arg syntax-arg)
       11 (cons (tast-show (car syntax-arg)) (tast-show (cdr syntax-arg)))
       12 (cons 'lambda (cons (tast-show (car syntax-arg))
                              (map tast-show (cdr syntax-arg))))
       13 (cons 'if (cons (tast-show (car syntax-arg))
                          (cons (tast-show (cadr syntax-arg))
                                (let [alt (cddr syntax-arg)]
                                  (if (eqv? (ast-con alt) empty)
                                    nil
                                    (list (tast-show alt)))))))
       14 (list 'set! (tast-show (car syntax-arg))
                (tast-show (cdr syntax-arg)))
       15 (cons 'cond
                (map (fn [cc]
                       (let [guard (car cc)
                             body (cdr cc)]
                         (cons
                          (if (eqv? (ast-con guard) empty)
                            'else
                            (tast-show guard))
                          (map tast-show body))))
                     syntax-arg))
       16 (cons 'case
                (cons (tast-show (car syntax-arg))
                      (map (fn [cc]
                             (let [data (car cc)]
                               (if (and (pair? data)
                                        (eqv? (ast-con (car data)) empty))
                                 (cons 'else
                                       (map tast-show (cdr cc)))
                                 (cons (map datum-show data)
                                       (map tast-show (cdr cc))))))
                           (cdr syntax-arg))))
       17 (cons 'and (map tast-show syntax-arg))
       18 (cons 'or (map tast-show syntax-arg))
       19 (cons 'let
                (cons (map
                       (fn [vd e]
                         (list (tast-show vd) (tast-show e)))
                       (caar syntax-arg)
                       (cdar syntax-arg))
                      (map tast-show (cdr syntax-arg))))
       20 (cons 'let
                (cons (tast-show (car syntax-arg))
                      (cons (map
                             (fn [vd e]
                               (list (tast-show vd) (tast-show e)))
                             (caadr syntax-arg)
                             (cdadr syntax-arg))
                            (map tast-show (cddr syntax-arg)))))
       21 (cons 'let*
                (cons (map
                       (fn [vd e]
                         (list (tast-show vd) (tast-show e)))
                       (caar syntax-arg)
                       (cdar syntax-arg))
                      (map tast-show (cdr syntax-arg))))
       22 (cons 'letrec
                (cons (map
                       (fn [vd e]
                         (list (tast-show vd) (tast-show e)))
                       (caar syntax-arg)
                       (cdar syntax-arg))
                      (map tast-show (cdr syntax-arg))))
       23 (cons 'begin
                (map tast-show syntax-arg))
       24 (error 'tast-show "Do expressions not handled!" syntax-arg)
       25 (error 'tast-show "This can't happen: empty encountered!")
       26 (list 'define
                (tast-show (car syntax-arg))
                (tast-show (cdr syntax-arg)))
       27 (cons 'define
                (cons
                 (cons (tast-show (car syntax-arg))
                       (tast-show (cadr syntax-arg)))
                 (map tast-show (cddr syntax-arg))))
       28 (cons 'begin
                (map tast-show syntax-arg))
       (error 'tast-show "Unknown abstract syntax operator:" syntax-op))
     syntax-tvar)))

;; tast*-show

(defn tast*-show [p]
  ;; shows a list of abstract syntax trees
  (map tast-show p))

;; counters for tagging/untagging

(def ^:private untag-counter (volatile! 0))
(def ^:private no-untag-counter (volatile! 0))
(def ^:private tag-counter (volatile! 0))
(def ^:private no-tag-counter (volatile! 0))
(def ^:private may-untag-counter (volatile! 0))
(def ^:private no-may-untag-counter (volatile! 0))

(defn reset-counters! []
  (vreset! untag-counter 0)
  (vreset! no-untag-counter 0)
  (vreset! tag-counter 0)
  (vreset! no-tag-counter 0)
  (vreset! may-untag-counter 0)
  (vreset! no-may-untag-counter 0))

(defn counters-show []
  (list
   (cons @tag-counter @no-tag-counter)
   (cons @untag-counter @no-untag-counter)
   (cons @may-untag-counter @no-may-untag-counter)))

;; tag-show

(defn tag-show [tvar-rep prog]
  ;; display prog with tagging operation
  (if (eqv? tvar-rep dynamic)
    (do (vreset! tag-counter (+ @tag-counter 1))
        (list 'tag prog))
    (do (vreset! no-tag-counter (+ @no-tag-counter 1))
        (list 'no-tag prog))))

;; untag-show

(defn untag-show [tvar-rep prog]
  ;; display prog with untagging operation
  (if (eqv? tvar-rep dynamic)
    (do (vreset! untag-counter (+ @untag-counter 1))
        (list 'untag prog))
    (do (vreset! no-untag-counter (+ @no-untag-counter 1))
        (list 'no-untag prog))))

(defn may-untag-show [tvar-rep prog]
  ;; display possible untagging in actual arguments
  (if (eqv? tvar-rep dynamic)
    (do (vreset! may-untag-counter (+ @may-untag-counter 1))
        (list 'may-untag prog))
    (do (vreset! no-may-untag-counter (+ @no-may-untag-counter 1))
        (list 'no-may-untag prog))))

;; tag-ast-show

(defn tag-ast-show [ast]
  ;; converts typed and normalized abstract syntax tree to
  ;; a Scheme program with explicit tagging and untagging operations
  (let [syntax-op (ast-con ast)
        syntax-tvar (find! (ast-tvar ast))
        syntax-arg (ast-arg ast)]
    (case syntax-op
      (0 1 2 3 4)
      (tag-show syntax-tvar syntax-arg)
      (8 10) syntax-arg
      (29 31) nil
      30 (cons (tag-ast-show (car syntax-arg))
               (tag-ast-show (cdr syntax-arg)))
      32 (cons (may-untag-show (find! (ast-tvar (car syntax-arg)))
                               (tag-ast-show (car syntax-arg)))
               (tag-ast-show (cdr syntax-arg)))
      5 (tag-show syntax-tvar (list 'quote syntax-arg))
      6 (tag-show syntax-tvar (list->vector (map tag-ast-show syntax-arg)))
      7 (tag-show syntax-tvar (list 'cons (tag-ast-show (car syntax-arg))
                                   (tag-ast-show (cdr syntax-arg))))
      9 (ast-arg syntax-arg)
      11 (let [proc-tvar (find! (ast-tvar (car syntax-arg)))]
           (cons (untag-show proc-tvar
                             (tag-ast-show (car syntax-arg)))
                 (tag-ast-show (cdr syntax-arg))))
      12 (tag-show syntax-tvar
                   (cons 'lambda (cons (tag-ast-show (car syntax-arg))
                                       (map tag-ast-show (cdr syntax-arg)))))
      13 (let [test-tvar (find! (ast-tvar (car syntax-arg)))]
           (cons 'if (cons (untag-show test-tvar
                                       (tag-ast-show (car syntax-arg)))
                           (cons (tag-ast-show (cadr syntax-arg))
                                 (let [alt (cddr syntax-arg)]
                                   (if (eqv? (ast-con alt) empty)
                                     nil
                                     (list (tag-ast-show alt))))))))
      14 (list 'set! (tag-ast-show (car syntax-arg))
               (tag-ast-show (cdr syntax-arg)))
      15 (cons 'cond
               (map (fn [cc]
                      (let [guard (car cc)
                            body (cdr cc)]
                        (cons
                         (if (eqv? (ast-con guard) empty)
                           'else
                           (untag-show (find! (ast-tvar guard))
                                       (tag-ast-show guard)))
                         (map tag-ast-show body))))
                    syntax-arg))
      16 (cons 'case
               (cons (tag-ast-show (car syntax-arg))
                     (map (fn [cc]
                            (let [data (car cc)]
                              (if (and (pair? data)
                                       (eqv? (ast-con (car data)) empty))
                                (cons 'else
                                      (map tag-ast-show (cdr cc)))
                                (cons (map datum-show data)
                                      (map tag-ast-show (cdr cc))))))
                          (cdr syntax-arg))))
      17 (cons 'and (map
                     (fn [ast]
                       (let [bool-tvar (find! (ast-tvar ast))]
                         (untag-show bool-tvar (tag-ast-show ast))))
                     syntax-arg))
      18 (cons 'or (map
                    (fn [ast]
                      (let [bool-tvar (find! (ast-tvar ast))]
                        (untag-show bool-tvar (tag-ast-show ast))))
                    syntax-arg))
      19 (cons 'let
               (cons (map
                      (fn [vd e]
                        (list (tag-ast-show vd) (tag-ast-show e)))
                      (caar syntax-arg)
                      (cdar syntax-arg))
                     (map tag-ast-show (cdr syntax-arg))))
      20 (cons 'let
               (cons (tag-ast-show (car syntax-arg))
                     (cons (map
                            (fn [vd e]
                              (list (tag-ast-show vd) (tag-ast-show e)))
                            (caadr syntax-arg)
                            (cdadr syntax-arg))
                           (map tag-ast-show (cddr syntax-arg)))))
      21 (cons 'let*
               (cons (map
                      (fn [vd e]
                        (list (tag-ast-show vd) (tag-ast-show e)))
                      (caar syntax-arg)
                      (cdar syntax-arg))
                     (map tag-ast-show (cdr syntax-arg))))
      22 (cons 'letrec
               (cons (map
                      (fn [vd e]
                        (list (tag-ast-show vd) (tag-ast-show e)))
                      (caar syntax-arg)
                      (cdar syntax-arg))
                     (map tag-ast-show (cdr syntax-arg))))
      23 (cons 'begin
               (map tag-ast-show syntax-arg))
      24 (error 'tag-ast-show "Do expressions not handled!" syntax-arg)
      25 (error 'tag-ast-show "This can't happen: empty encountered!")
      26 (list 'define
               (tag-ast-show (car syntax-arg))
               (tag-ast-show (cdr syntax-arg)))
      27 (let [func-tvar (find! (ast-tvar (car syntax-arg)))]
           (list 'define
                 (tag-ast-show (car syntax-arg))
                 (tag-show func-tvar
                           (cons 'lambda
                                 (cons (tag-ast-show (cadr syntax-arg))
                                       (map tag-ast-show (cddr syntax-arg)))))))
      28 (cons 'begin
               (map tag-ast-show syntax-arg))
      (error 'tag-ast-show "Unknown abstract syntax operator:" syntax-op))))

;; tag-ast*-show

(defn tag-ast*-show [p]
  ;; display list of commands/expressions with tagging/untagging
  ;; operations
  (map tag-ast-show p))

;; ----------------------------------------------------------------------------
;; Top level type environment
;; ----------------------------------------------------------------------------

;; type environment for miscellaneous

(def misc-env
  (list
   (cons 'quote (forall (fn [tv] tv)))
   (cons 'eqv? (forall (fn [tv] (procedure (convert-tvars (list tv tv))
                                           (boolean)))))
   (cons 'eq? (forall (fn [tv] (procedure (convert-tvars (list tv tv))
                                          (boolean)))))
   (cons 'equal? (forall (fn [tv] (procedure (convert-tvars (list tv tv))
                                             (boolean)))))))

;; type environment for input/output

(def io-env
  (list
   (cons 'open-input-file (procedure (convert-tvars (list (charseq))) dynamic))
   (cons 'eof-object? (procedure (convert-tvars (list dynamic)) (boolean)))
   (cons 'read (forall (fn [tv]
                         (procedure (convert-tvars (list tv)) dynamic))))
   (cons 'write (forall (fn [tv]
                          (procedure (convert-tvars (list tv)) dynamic))))
   (cons 'display (forall (fn [tv]
                            (procedure (convert-tvars (list tv)) dynamic))))
   (cons 'newline (procedure (null) dynamic))
   (cons 'pretty-print (forall (fn [tv]
                                 (procedure (convert-tvars (list tv)) dynamic))))))

;; type environment for Booleans

(def boolean-env
  (list
   (cons 'boolean? (forall (fn [tv]
                             (procedure (convert-tvars (list tv)) (boolean)))))
   ;;(cons #f (boolean))
   ;; #f doesn't exist in Chez Scheme, but gets mapped to null!
   (cons true (boolean))
   (cons 'not (procedure (convert-tvars (list (boolean))) (boolean)))))

;; type environment for pairs and lists

(defn list-type [tv]
  (fix (fn [tv2] (pair tv tv2))))

(def list-env
  (list
   (cons 'pair? (forall2 (fn [tv1 tv2]
                           (procedure (convert-tvars (list (pair tv1 tv2)))
                                      (boolean)))))
   (cons 'null? (forall2 (fn [tv1 tv2]
                           (procedure (convert-tvars (list (pair tv1 tv2)))
                                      (boolean)))))
   (cons 'list? (forall2 (fn [tv1 tv2]
                           (procedure (convert-tvars (list (pair tv1 tv2)))
                                      (boolean)))))
   (cons 'cons (forall2 (fn [tv1 tv2]
                          (procedure (convert-tvars (list tv1 tv2))
                                     (pair tv1 tv2)))))
   (cons 'car (forall2 (fn [tv1 tv2]
                         (procedure (convert-tvars (list (pair tv1 tv2)))
                                    tv1))))
   (cons 'cdr (forall2 (fn [tv1 tv2]
                         (procedure (convert-tvars (list (pair tv1 tv2)))
                                    tv2))))
   (cons 'set-car! (forall2 (fn [tv1 tv2]
                              (procedure (convert-tvars (list (pair tv1 tv2)
                                                              tv1))
                                         dynamic))))
   (cons 'set-cdr! (forall2 (fn [tv1 tv2]
                              (procedure (convert-tvars (list (pair tv1 tv2)
                                                              tv2))
                                         dynamic))))
   (cons 'caar (forall3 (fn [tv1 tv2 tv3]
                          (procedure (convert-tvars
                                      (list (pair (pair tv1 tv2) tv3)))
                                     tv1))))
   (cons 'cdar (forall3 (fn [tv1 tv2 tv3]
                          (procedure (convert-tvars
                                      (list (pair (pair tv1 tv2) tv3)))
                                     tv2))))
   (cons 'cadr (forall3 (fn [tv1 tv2 tv3]
                          (procedure (convert-tvars
                                      (list (pair tv1 (pair tv2 tv3))))
                                     tv2))))
   (cons 'cddr (forall3 (fn [tv1 tv2 tv3]
                          (procedure (convert-tvars
                                      (list (pair tv1 (pair tv2 tv3))))
                                     tv3))))
   (cons 'caaar (forall4
                 (fn [tv1 tv2 tv3 tv4]
                   (procedure (convert-tvars
                               (list (pair (pair (pair tv1 tv2) tv3) tv4)))
                              tv1))))
   (cons 'cdaar (forall4
                 (fn [tv1 tv2 tv3 tv4]
                   (procedure (convert-tvars
                               (list (pair (pair (pair tv1 tv2) tv3) tv4)))
                              tv2))))
   (cons 'cadar (forall4
                 (fn [tv1 tv2 tv3 tv4]
                   (procedure (convert-tvars
                               (list (pair (pair tv1 (pair tv2 tv3)) tv4)))
                              tv2))))
   (cons 'cddar (forall4
                 (fn [tv1 tv2 tv3 tv4]
                   (procedure (convert-tvars
                               (list (pair (pair tv1 (pair tv2 tv3)) tv4)))
                              tv3))))
   (cons 'caadr (forall4
                 (fn [tv1 tv2 tv3 tv4]
                   (procedure (convert-tvars
                               (list (pair tv1 (pair (pair tv2 tv3) tv4))))
                              tv2))))
   (cons 'cdadr (forall4
                 (fn [tv1 tv2 tv3 tv4]
                   (procedure (convert-tvars
                               (list (pair tv1 (pair (pair tv2 tv3) tv4))))
                              tv3))))
   (cons 'caddr (forall4
                 (fn [tv1 tv2 tv3 tv4]
                   (procedure (convert-tvars
                               (list (pair tv1 (pair tv2 (pair tv3 tv4)))))
                              tv3))))
   (cons 'cdddr (forall4
                 (fn [tv1 tv2 tv3 tv4]
                   (procedure (convert-tvars
                               (list (pair tv1 (pair tv2 (pair tv3 tv4)))))
                              tv4))))
   (cons 'cadddr
         (forall5 (fn [tv1 tv2 tv3 tv4 tv5]
                    (procedure (convert-tvars
                                (list (pair tv1
                                            (pair tv2
                                                  (pair tv3
                                                        (pair tv4 tv5))))))
                               tv4))))
   (cons 'cddddr
         (forall5 (fn [tv1 tv2 tv3 tv4 tv5]
                    (procedure (convert-tvars
                                (list (pair tv1
                                            (pair tv2
                                                  (pair tv3
                                                        (pair tv4 tv5))))))
                               tv5))))
   (cons 'list (forall (fn [tv]
                         (procedure tv tv))))
   (cons 'length (forall (fn [tv]
                           (procedure (convert-tvars (list (list-type tv)))
                                      (number)))))
   (cons 'append (forall (fn [tv]
                           (procedure (convert-tvars (list (list-type tv)
                                                           (list-type tv)))
                                      (list-type tv)))))
   (cons 'reverse (forall (fn [tv]
                            (procedure (convert-tvars (list (list-type tv)))
                                       (list-type tv)))))
   (cons 'list-ref (forall (fn [tv]
                             (procedure (convert-tvars (list (list-type tv)
                                                             (number)))
                                        tv))))
   (cons 'memq (forall (fn [tv]
                         (procedure (convert-tvars (list tv
                                                         (list-type tv)))
                                    (boolean)))))
   (cons 'memv (forall (fn [tv]
                         (procedure (convert-tvars (list tv
                                                         (list-type tv)))
                                    (boolean)))))
   (cons 'member (forall (fn [tv]
                           (procedure (convert-tvars (list tv
                                                           (list-type tv)))
                                      (boolean)))))
   (cons 'assq (forall2 (fn [tv1 tv2]
                          (procedure (convert-tvars
                                      (list tv1
                                            (list-type (pair tv1 tv2))))
                                     (pair tv1 tv2)))))
   (cons 'assv (forall2 (fn [tv1 tv2]
                          (procedure (convert-tvars
                                      (list tv1
                                            (list-type (pair tv1 tv2))))
                                     (pair tv1 tv2)))))
   (cons 'assoc (forall2 (fn [tv1 tv2]
                           (procedure (convert-tvars
                                       (list tv1
                                             (list-type (pair tv1 tv2))))
                                      (pair tv1 tv2)))))))

(def symbol-env
  (list
   (cons 'symbol? (forall (fn [tv]
                            (procedure (convert-tvars (list tv)) (boolean)))))
   (cons 'symbol->string (procedure (convert-tvars (list (symbol))) (charseq)))
   (cons 'string->symbol (procedure (convert-tvars (list (charseq))) (symbol)))))

(def number-env
  (list
   (cons 'number? (forall (fn [tv]
                            (procedure (convert-tvars (list tv)) (boolean)))))
   (cons '+ (procedure (convert-tvars (list (number) (number))) (number)))
   (cons '- (procedure (convert-tvars (list (number) (number))) (number)))
   (cons '* (procedure (convert-tvars (list (number) (number))) (number)))
   (cons '/ (procedure (convert-tvars (list (number) (number))) (number)))
   (cons 'number->string (procedure (convert-tvars (list (number))) (charseq)))
   (cons 'string->number (procedure (convert-tvars (list (charseq))) (number)))))

(def char-env
  (list
   (cons 'char? (forall (fn [tv]
                          (procedure (convert-tvars (list tv)) (boolean)))))
   (cons 'char->integer (procedure (convert-tvars (list (character)))
                                   (number)))
   (cons 'integer->char (procedure (convert-tvars (list (number)))
                                   (character)))))

(def string-env
  (list
   (cons 'string? (forall (fn [tv]
                            (procedure (convert-tvars (list tv)) (boolean)))))))

(def vector-env
  (list
   (cons 'vector? (forall (fn [tv]
                            (procedure (convert-tvars (list tv)) (boolean)))))
   (cons 'make-vector (forall (fn [tv]
                                (procedure (convert-tvars (list (number)))
                                           (array tv)))))
   (cons 'vector-length (forall (fn [tv]
                                  (procedure (convert-tvars (list (array tv)))
                                             (number)))))
   (cons 'vector-ref (forall (fn [tv]
                               (procedure (convert-tvars (list (array tv)
                                                               (number)))
                                          tv))))
   (cons 'vector-set! (forall (fn [tv]
                                (procedure (convert-tvars (list (array tv)
                                                                (number)
                                                                tv))
                                           dynamic))))))

(def procedure-env
  (list
   (cons 'procedure? (forall (fn [tv]
                               (procedure (convert-tvars (list tv)) (boolean)))))
   (cons 'map (forall2 (fn [tv1 tv2]
                         (procedure (convert-tvars
                                     (list (procedure (convert-tvars
                                                       (list tv1)) tv2)
                                           (list-type tv1)))
                                    (list-type tv2)))))
   (cons 'foreach (forall2 (fn [tv1 tv2]
                             (procedure (convert-tvars
                                         (list (procedure (convert-tvars
                                                           (list tv1)) tv2)
                                               (list-type tv1)))
                                        (list-type tv2)))))
   (cons 'call-with-current-continuation
         (forall2 (fn [tv1 tv2]
                    (procedure (convert-tvars
                                (list (procedure
                                       (convert-tvars
                                        (list (procedure (convert-tvars
                                                          (list tv1)) tv2)))
                                       tv2)))
                               tv2))))))

;; global top level environment

(defn global-env []
  (append misc-env
          io-env
          boolean-env
          symbol-env
          number-env
          char-env
          string-env
          vector-env
          procedure-env
          list-env))

(def ^:private dynamic-top-level-env (volatile! (global-env)))

(defn init-dynamic-top-level-env! []
  (vreset! dynamic-top-level-env (global-env))
  nil)

(defn dynamic-top-level-env-show []
  ;; displays the top level environment
  (map (fn [binding]
         (cons (key-show (binding-key binding))
               (cons colon (tvar-show (binding-value binding)))))
       (env->list @dynamic-top-level-env)))

;; ----------------------------------------------------------------------------
;; Dynamic type inference for Scheme
;; ----------------------------------------------------------------------------

(def ^:private tag-ops (volatile! 0))
(def ^:private no-ops (volatile! 0))

(defn ic! [] (init-global-constraints!))
(defn pc [] (glob-constr-show))
(defn lc [] (length @global-constraints))
(defn n! [] (normalize-global-constraints!))
(defn pt [] (dynamic-top-level-env-show))
(defn it! [] (init-dynamic-top-level-env!))
(defn io! [] (vreset! tag-ops 0) (vreset! no-ops 0))
(defn i! [] (ic!) (it!) (io!) nil)

(def doit
  (fn [input-file]
    (i!)
    (let [foo (dynamic-parse-file input-file)]
      (normalize-global-constraints!)
      (reset-counters!)
      (tag-ast*-show foo)
      (counters-show))))

(defn -main [& _]
  (let [count (read-input) input1 (read-input) output (data->pairs (read-input))
        s2 (str count) s1 input1
        name "dynamic"]
    (run-r7rs-benchmark (str name ":" s2) count
                        (fn [] (doit (hide count input1)))
                        (fn [result] (equal? result output)))))
