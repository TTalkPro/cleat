;;; SBOYER -- Logic programming benchmark, originally written by Bob Boyer.
;;; Much less CONS-intensive than NBOYER because it uses Henry Baker's "sharing cons".
;;; Port of r7rs-benchmarks src/sboyer.scm (Will Clinger's version: lemmas live in
;;; symbol-records found through an association list, not on property lists).
;;; Representation: Scheme symbols -> keywords (interned, compared with identical?);
;;; Scheme lists -> Clojure seqs with nil as the empty list (car = first, cdr = next,
;;; pair? = seq?); alist entries -> util mutable pairs (mcons, car/cdr);
;;; symbol-record -> 2-element object array like the original vector;
;;; the module-level set! variables (unify-subst, rewrite-count, ...) -> mutable cells.
;;; The Scheme symbol `nil` in the data is read by Clojure as nil and becomes :nil.
(ns r7rs.sboyer
  (:refer-clojure :exclude [get test])
  (:require [r7rs.common :refer [hide read-input run-r7rs-benchmark]]
            [r7rs.util :refer [mcons mcar mcdr]]))

(definterface ICell (get []) (set [v]))
(deftype Cell [^:unsynchronized-mutable v]
  ICell
  (get [_] v)
  (set [_ x] (set! v x) x))
(defn- cell [v] (Cell. v))
(defn- cget [c] (.get ^r7rs.sboyer.ICell c))
(defn- cset! [c v] (.set ^r7rs.sboyer.ICell c v))

(defn- ->data
  "Quoted Scheme datum (read as Clojure data) -> symbols become keywords, nil-lists end in nil."
  [x]
  (cond (seq? x) (seq (map ->data x))
        (symbol? x) (keyword (name x))
        (nil? x) :nil
        :else x))

(defn- pair? [x] (seq? x))
(defn- car [x] (first x))
(defn- cdr [x] (next x))
(defn- cadr [x] (first (next x)))
(defn- caddr [x] (first (next (next x))))
(defn- cadddr [x] (first (next (next (next x)))))

(defn- assq [k alist]
  (loop [l alist]
    (cond (nil? l) false
          (identical? (mcar (first l)) k) (first l)
          :else (recur (next l)))))

(def ^:private alist
  (->data
   '((x f (plus (plus a b)
                (plus c (zero))))
     (y f (times (times a b)
                 (plus c d)))
     (z f (reverse (append (append a b)
                           (nil))))
     (u equal (plus a b)
        (difference x y))
     (w lessp (remainder a b)
        (member a (length b))))))

(def ^:private term
  (->data
   '(implies (and (implies x y)
                  (and (implies y z)
                       (and (implies z u)
                            (implies u w))))
             (implies x w))))

(def ^:private lemmas
  (->data
  '((equal (compile form)
                    (reverse (codegen (optimize form)
                                      (nil))))
             (equal (eqp x y)
                    (equal (fix x)
                           (fix y)))
             (equal (greaterp x y)
                    (lessp y x))
             (equal (lesseqp x y)
                    (not (lessp y x)))
             (equal (greatereqp x y)
                    (not (lessp x y)))
             (equal (boolean x)
                    (or (equal x (t))
                        (equal x (f))))
             (equal (iff x y)
                    (and (implies x y)
                         (implies y x)))
             (equal (even1 x)
                    (if (zerop x)
                        (t)
                        (odd (_1- x))))
             (equal (countps- l pred)
                    (countps-loop l pred (zero)))
             (equal (fact- i)
                    (fact-loop i 1))
             (equal (reverse- x)
                    (reverse-loop x (nil)))
             (equal (divides x y)
                    (zerop (remainder y x)))
             (equal (assume-true var alist)
                    (cons (cons var (t))
                          alist))
             (equal (assume-false var alist)
                    (cons (cons var (f))
                          alist))
             (equal (tautology-checker x)
                    (tautologyp (normalize x)
                                (nil)))
             (equal (falsify x)
                    (falsify1 (normalize x)
                              (nil)))
             (equal (prime x)
                    (and (not (zerop x))
                         (not (equal x (add1 (zero))))
                         (prime1 x (_1- x))))
             (equal (and p q)
                    (if p (if q (t)
                              (f))
                        (f)))
             (equal (or p q)
                    (if p (t)
                        (if q (t)
                            (f))))
             (equal (not p)
                    (if p (f)
                        (t)))
             (equal (implies p q)
                    (if p (if q (t)
                              (f))
                        (t)))
             (equal (fix x)
                    (if (numberp x)
                        x
                        (zero)))
             (equal (if (if a b c)
                        d e)
                    (if a (if b d e)
                        (if c d e)))
             (equal (zerop x)
                    (or (equal x (zero))
                        (not (numberp x))))
             (equal (plus (plus x y)
                          z)
                    (plus x (plus y z)))
             (equal (equal (plus a b)
                           (zero))
                    (and (zerop a)
                         (zerop b)))
             (equal (difference x x)
                    (zero))
             (equal (equal (plus a b)
                           (plus a c))
                    (equal (fix b)
                           (fix c)))
             (equal (equal (zero)
                           (difference x y))
                    (not (lessp y x)))
             (equal (equal x (difference x y))
                    (and (numberp x)
                         (or (equal x (zero))
                             (zerop y))))
             (equal (meaning (plus-tree (append x y))
                             a)
                    (plus (meaning (plus-tree x)
                                   a)
                          (meaning (plus-tree y)
                                   a)))
             (equal (meaning (plus-tree (plus-fringe x))
                             a)
                    (fix (meaning x a)))
             (equal (append (append x y)
                            z)
                    (append x (append y z)))
             (equal (reverse (append a b))
                    (append (reverse b)
                            (reverse a)))
             (equal (times x (plus y z))
                    (plus (times x y)
                          (times x z)))
             (equal (times (times x y)
                           z)
                    (times x (times y z)))
             (equal (equal (times x y)
                           (zero))
                    (or (zerop x)
                        (zerop y)))
             (equal (exec (append x y)
                          pds envrn)
                    (exec y (exec x pds envrn)
                          envrn))
             (equal (mc-flatten x y)
                    (append (flatten x)
                            y))
             (equal (member x (append a b))
                    (or (member x a)
                        (member x b)))
             (equal (member x (reverse y))
                    (member x y))
             (equal (length (reverse x))
                    (length x))
             (equal (member a (intersect b c))
                    (and (member a b)
                         (member a c)))
             (equal (nth (zero)
                         i)
                    (zero))
             (equal (exp i (plus j k))
                    (times (exp i j)
                           (exp i k)))
             (equal (exp i (times j k))
                    (exp (exp i j)
                         k))
             (equal (reverse-loop x y)
                    (append (reverse x)
                            y))
             (equal (reverse-loop x (nil))
                    (reverse x))
             (equal (count-list z (sort-lp x y))
                    (plus (count-list z x)
                          (count-list z y)))
             (equal (equal (append a b)
                           (append a c))
                    (equal b c))
             (equal (plus (remainder x y)
                          (times y (quotient x y)))
                    (fix x))
             (equal (power-eval (big-plus1 l i base)
                                base)
                    (plus (power-eval l base)
                          i))
             (equal (power-eval (big-plus x y i base)
                                base)
                    (plus i (plus (power-eval x base)
                                  (power-eval y base))))
             (equal (remainder y 1)
                    (zero))
             (equal (lessp (remainder x y)
                           y)
                    (not (zerop y)))
             (equal (remainder x x)
                    (zero))
             (equal (lessp (quotient i j)
                           i)
                    (and (not (zerop i))
                         (or (zerop j)
                             (not (equal j 1)))))
             (equal (lessp (remainder x y)
                           x)
                    (and (not (zerop y))
                         (not (zerop x))
                         (not (lessp x y))))
             (equal (power-eval (power-rep i base)
                                base)
                    (fix i))
             (equal (power-eval (big-plus (power-rep i base)
                                          (power-rep j base)
                                          (zero)
                                          base)
                                base)
                    (plus i j))
             (equal (gcd x y)
                    (gcd y x))
             (equal (nth (append a b)
                         i)
                    (append (nth a i)
                            (nth b (difference i (length a)))))
             (equal (difference (plus x y)
                                x)
                    (fix y))
             (equal (difference (plus y x)
                                x)
                    (fix y))
             (equal (difference (plus x y)
                                (plus x z))
                    (difference y z))
             (equal (times x (difference c w))
                    (difference (times c x)
                                (times w x)))
             (equal (remainder (times x z)
                               z)
                    (zero))
             (equal (difference (plus b (plus a c))
                                a)
                    (plus b c))
             (equal (difference (add1 (plus y z))
                                z)
                    (add1 y))
             (equal (lessp (plus x y)
                           (plus x z))
                    (lessp y z))
             (equal (lessp (times x z)
                           (times y z))
                    (and (not (zerop z))
                         (lessp x y)))
             (equal (lessp y (plus x y))
                    (not (zerop x)))
             (equal (gcd (times x z)
                         (times y z))
                    (times z (gcd x y)))
             (equal (value (normalize x)
                           a)
                    (value x a))
             (equal (equal (flatten x)
                           (cons y (nil)))
                    (and (nlistp x)
                         (equal x y)))
             (equal (listp (gopher x))
                    (listp x))
             (equal (samefringe x y)
                    (equal (flatten x)
                           (flatten y)))
             (equal (equal (greatest-factor x y)
                           (zero))
                    (and (or (zerop y)
                             (equal y 1))
                         (equal x (zero))))
             (equal (equal (greatest-factor x y)
                           1)
                    (equal x 1))
             (equal (numberp (greatest-factor x y))
                    (not (and (or (zerop y)
                                  (equal y 1))
                              (not (numberp x)))))
             (equal (times-list (append x y))
                    (times (times-list x)
                           (times-list y)))
             (equal (prime-list (append x y))
                    (and (prime-list x)
                         (prime-list y)))
             (equal (equal z (times w z))
                    (and (numberp z)
                         (or (equal z (zero))
                             (equal w 1))))
             (equal (greatereqp x y)
                    (not (lessp x y)))
             (equal (equal x (times x y))
                    (or (equal x (zero))
                        (and (numberp x)
                             (equal y 1))))
             (equal (remainder (times y x)
                               y)
                    (zero))
             (equal (equal (times a b)
                           1)
                    (and (not (equal a (zero)))
                         (not (equal b (zero)))
                         (numberp a)
                         (numberp b)
                         (equal (_1- a)
                                (zero))
                         (equal (_1- b)
                                (zero))))
             (equal (lessp (length (delete x l))
                           (length l))
                    (member x l))
             (equal (sort2 (delete x l))
                    (delete x (sort2 l)))
             (equal (dsort x)
                    (sort2 x))
             (equal (length (cons x1
                                  (cons x2
                                        (cons x3 (cons x4
                                                       (cons x5
                                                             (cons x6 x7)))))))
                    (plus 6 (length x7)))
             (equal (difference (add1 (add1 x))
                                2)
                    (fix x))
             (equal (quotient (plus x (plus x y))
                              2)
                    (plus x (quotient y 2)))
             (equal (sigma (zero)
                           i)
                    (quotient (times i (add1 i))
                              2))
             (equal (plus x (add1 y))
                    (if (numberp y)
                        (add1 (plus x y))
                        (add1 x)))
             (equal (equal (difference x y)
                           (difference z y))
                    (if (lessp x y)
                        (not (lessp y z))
                        (if (lessp z y)
                            (not (lessp y x))
                            (equal (fix x)
                                   (fix z)))))
             (equal (meaning (plus-tree (delete x y))
                             a)
                    (if (member x y)
                        (difference (meaning (plus-tree y)
                                             a)
                                    (meaning x a))
                        (meaning (plus-tree y)
                                 a)))
             (equal (times x (add1 y))
                    (if (numberp y)
                        (plus x (times x y))
                        (fix x)))
             (equal (nth (nil)
                         i)
                    (if (zerop i)
                        (nil)
                        (zero)))
             (equal (last (append a b))
                    (if (listp b)
                        (last b)
                        (if (listp a)
                            (cons (car (last a))
                                  b)
                            b)))
             (equal (equal (lessp x y)
                           z)
                    (if (lessp x y)
                        (equal (t) z)
                        (equal (f) z)))
             (equal (assignment x (append a b))
                    (if (assignedp x a)
                        (assignment x a)
                        (assignment x b)))
             (equal (car (gopher x))
                    (if (listp x)
                        (car (flatten x))
                        (zero)))
             (equal (flatten (cdr (gopher x)))
                    (if (listp x)
                        (cdr (flatten x))
                        (cons (zero)
                              (nil))))
             (equal (quotient (times y x)
                              y)
                    (if (zerop y)
                        (zero)
                        (fix x)))
             (equal (get j (set i val mem))
                    (if (eqp j i)
                        val
                        (get j mem))))
))

;;; The first phase.

(def ^:private symbol-records-alist (cell nil))

(defn- make-symbol-record [sym] (object-array [sym nil]))
(defn- put-lemmas! [symbol-record lemmas] (aset ^objects symbol-record 1 lemmas))
(defn- get-lemmas [symbol-record] (aget ^objects symbol-record 1))
(defn- get-name [symbol-record] (aget ^objects symbol-record 0))
(defn- symbol-record-equal? [r1 r2] (identical? r1 r2))

(defn- symbol->symbol-record [sym]
  (let [x (assq sym (cget symbol-records-alist))]
    (if x
      (mcdr x)
      (let [r (make-symbol-record sym)]
        (cset! symbol-records-alist (cons (mcons sym r) (cget symbol-records-alist)))
        r))))

(defn- put [sym property value]
  (put-lemmas! (symbol->symbol-record sym) value))

(defn- get [sym property]
  (get-lemmas (symbol->symbol-record sym)))

(declare translate-args)

(defn- translate-term [term]
  (cond (not (pair? term)) term
        :else (cons (symbol->symbol-record (car term))
                    (translate-args (cdr term)))))

(defn- translate-args [lst]
  (cond (nil? lst) nil
        :else (cons (translate-term (car lst))
                    (translate-args (cdr lst)))))

(defn- add-lemma [term]
  (cond (and (pair? term)
             (identical? (car term) :equal)
             (pair? (cadr term)))
        (put (car (cadr term))
             :lemmas
             (cons (translate-term term)
                   (get (car (cadr term)) :lemmas)))
        :else (throw (ex-info (str "ADD-LEMMA did not like term:  " term) {}))))

(defn- add-lemma-lst [lst]
  (loop [lst lst]
    (cond (nil? lst) true
          :else (do (add-lemma (car lst))
                    (recur (cdr lst))))))

(defn- setup [] (add-lemma-lst lemmas))

;;; The second phase.

(def ^:private if-constructor (cell :*))   ; becomes (symbol->symbol-record :if)
(def ^:private rewrite-count (cell 0))     ; sanity check
(def ^:private unify-subst (cell :*))
(def ^:private false-term (cell :*))       ; becomes (translate-term '(f))
(def ^:private true-term (cell :*))        ; becomes (translate-term '(t))

(declare apply-subst-lst term-args-equal?)

(defn- apply-subst [alist term]
  (cond (not (pair? term))
        (let [temp-temp (assq term alist)]
          (if temp-temp (mcdr temp-temp) term))
        :else (cons (car term) (apply-subst-lst alist (cdr term)))))

(defn- apply-subst-lst [alist lst]
  (cond (nil? lst) nil
        :else (cons (apply-subst alist (car lst))
                    (apply-subst-lst alist (cdr lst)))))

(defn- translate-alist [alist]
  (cond (nil? alist) nil
        :else (cons (mcons (car (car alist)) (translate-term (cdr (car alist))))
                    (translate-alist (cdr alist)))))

;; Translated terms can be circular structures, which can't be compared using
;; Scheme's equal? and member procedures, so we use these instead.
(defn- term-equal? [x y]
  (cond (pair? x)
        (and (pair? y)
             (symbol-record-equal? (car x) (car y))
             (term-args-equal? (cdr x) (cdr y)))
        :else (= x y)))

(defn- term-args-equal? [lst1 lst2]
  (loop [lst1 lst1 lst2 lst2]
    (cond (nil? lst1) (nil? lst2)
          (nil? lst2) false
          (term-equal? (car lst1) (car lst2)) (recur (cdr lst1) (cdr lst2))
          :else false)))

(defn- term-member? [x lst]
  (loop [lst lst]
    (cond (nil? lst) false
          (term-equal? x (car lst)) true
          :else (recur (cdr lst)))))

(defn- falsep [x lst]
  (or (term-equal? x (cget false-term))
      (term-member? x lst)))

(defn- truep [x lst]
  (or (term-equal? x (cget true-term))
      (term-member? x lst)))

(defn- tautologyp [x true-lst false-lst]
  (cond (truep x true-lst) true
        (falsep x false-lst) false
        (not (pair? x)) false
        (identical? (car x) (cget if-constructor))
        (cond (truep (cadr x) true-lst)
              (recur (caddr x) true-lst false-lst)
              (falsep (cadr x) false-lst)
              (recur (cadddr x) true-lst false-lst)
              :else (and (tautologyp (caddr x) (cons (cadr x) true-lst) false-lst)
                         (recur (cadddr x) true-lst (cons (cadr x) false-lst))))
        :else false))

(declare one-way-unify1-lst)

(defn- one-way-unify1 [term1 term2]
  (cond (not (pair? term2))
        (let [temp-temp (assq term2 (cget unify-subst))]
          (cond temp-temp (term-equal? term1 (mcdr temp-temp))
                (number? term2) (= term1 term2)   ; This bug fix makes nboyer 10-25% slower!
                :else (do (cset! unify-subst (cons (mcons term2 term1) (cget unify-subst)))
                          true)))
        (not (pair? term1)) false
        (identical? (car term1) (car term2))
        (one-way-unify1-lst (cdr term1) (cdr term2))
        :else false))

(defn- one-way-unify1-lst [lst1 lst2]
  (loop [lst1 lst1 lst2 lst2]
    (cond (nil? lst1) (nil? lst2)
          (nil? lst2) false
          (one-way-unify1 (car lst1) (car lst2)) (recur (cdr lst1) (cdr lst2))
          :else false)))

(defn- one-way-unify [term1 term2]
  (cset! unify-subst nil)
  (one-way-unify1 term1 term2))

(declare rewrite-args rewrite-with-lemmas)

;; Henry Baker's sharing CONS, which avoids allocation if the result is already in hand.
;; REWRITE and REWRITE-ARGS use SCONS instead of CONS (identity of the original cells is kept:
;; cdr = next returns the same tail object, so eq? on cdrs carries over as identical?).
(defn- scons [x y original]
  (if (and (identical? x (car original))
           (identical? y (cdr original)))
    original
    (cons x y)))

(defn- rewrite [term]
  (cset! rewrite-count (+ (cget rewrite-count) 1))
  (cond (not (pair? term)) term
        :else (rewrite-with-lemmas (scons (car term) (rewrite-args (cdr term)) term)
                                   (get-lemmas (car term)))))

(defn- rewrite-args [lst]
  (cond (nil? lst) nil
        :else (scons (rewrite (car lst)) (rewrite-args (cdr lst)) lst)))

(defn- rewrite-with-lemmas [term lst]
  (loop [lst lst]
    (cond (nil? lst) term
          (one-way-unify term (cadr (car lst)))
          (rewrite (apply-subst (cget unify-subst) (caddr (car lst))))
          :else (recur (cdr lst)))))

(defn- tautp [x]
  (tautologyp (rewrite x) nil nil))

(defn- test [alist term n]
  (let [term (apply-subst (translate-alist alist)
                          (translate-term
                           (loop [term term n n]
                             (if (zero? n) term (recur (list :or term (list :f)) (- n 1))))))]
    (tautp term)))

(defn- setup-boyer []
  (cset! symbol-records-alist nil)
  (cset! if-constructor (symbol->symbol-record :if))
  (cset! false-term (translate-term (list :f)))
  (cset! true-term (translate-term (list :t)))
  (setup))

(defn- test-boyer [alist term n]
  (cset! rewrite-count 0)
  (let [answer (test alist term n)]
    (if answer (cget rewrite-count) false)))

(defn -main [& _]
  (let [count (read-input)
        input (read-input)
        output (read-input)
        s2 (str count)
        s1 (str input)
        name "sboyer"]
    (run-r7rs-benchmark (str name ":" s1 ":" s2)
                        count
                        (fn []
                          (setup-boyer)
                          (test-boyer alist term (hide count input)))
                        (fn [rewrites]
                          (and (number? rewrites) (= rewrites output))))))
