;;; LATTICE -- Obtained from Andrew Wright.
;; 符号 → 关键字（eq? / case → identical? / case）；点对（格本身 (elems . cmp)、pas 里的 (next . x)）→ util 可变序对；
;; zulu-select / select-map 的结果表被 xreverse! 用 set-cdr! 原地反转 → 可变序对表；其余不可变表用 Clojure 表（cons / first / next）。
;; map / append 用急切的私有实现（Scheme 的 map / append 是急切的）。
(ns r7rs.lattice
  (:require [r7rs.common :refer [hide read-input run-r7rs-benchmark]]
            [r7rs.util :refer [mcons mcar mcdr set-mcdr! fatal]]))

;; Given a comparison routine that returns one of
;;       less
;;       more
;;       equal
;;       uncomparable
;; return a new comparison routine that applies to sequences.
(defn- lexico [base]
  (letfn [(lex-fixed [fixed lhs rhs]
            (loop [lhs lhs rhs rhs]
              (if (nil? lhs)
                fixed
                (let [probe (base (first lhs) (first rhs))]
                  (if (or (identical? probe :equal)
                          (identical? probe fixed))
                    (recur (next lhs) (next rhs))
                    :uncomparable)))))
          (lex-first [lhs rhs]
            (if (nil? lhs)
              :equal
              (let [probe (base (first lhs) (first rhs))]
                (case probe
                  (:less :more) (lex-fixed probe (next lhs) (next rhs))
                  :equal (recur (next lhs) (next rhs))
                  :uncomparable :uncomparable
                  nil))))]
    lex-first))

(defn- make-lattice [elem-list cmp-func]
  (mcons elem-list cmp-func))

(defn- lattice->elements [l] (mcar l))

(defn- lattice->cmp [l] (mcdr l))

(defn- rotate [fo fum]
  (let [nxt (mcdr fo)]
    (set-mcdr! fo fum)
    (if (nil? nxt)
      fo
      (recur nxt fo))))

(defn- xreverse! [lst]
  (if (nil? lst)
    nil
    (rotate lst nil)))

;; Select elements of a list which pass some test.
(defn- zulu-select [test lst]
  (loop [ac nil lst lst]
    (if (nil? lst)
      (xreverse! ac)
      (recur (let [head (first lst)]
               (if (test head)
                 (mcons head ac)
                 ac))
             (next lst)))))

;; Select elements of a list which pass some test and map a function
;; over the result.  Note, only efficiency prevents this from being the
;; composition of select and map.
(defn- select-map [test func lst]
  (loop [ac nil lst lst]
    (if (nil? lst)
      (xreverse! ac)
      (recur (let [head (first lst)]
               (if (test head)
                 (mcons (func head) ac)
                 ac))
             (next lst)))))

;; This version of map-and tail-recurses on the last test.
;; （lst 是 select-map 的结果，可变序对表）
(defn- map-and [proc lst]
  (if (nil? lst)
    true
    (loop [lst lst]
      (let [rest (mcdr lst)]
        (if (nil? rest)
          (proc (mcar lst))
          (and (proc (mcar lst))
               (recur rest)))))))

(defn- memq2
  "(memq x '(a b))：x 是 a 或 b 时为真。"
  [x a b]
  (or (identical? x a) (identical? x b)))

(defn- maps-1 [source target pas new]
  (let [scmp (lattice->cmp source)
        tcmp (lattice->cmp target)]
    (let [less (select-map (fn [p] (identical? :less (scmp (mcar p) new)))
                           mcdr
                           pas)
          more (select-map (fn [p] (identical? :more (scmp (mcar p) new)))
                           mcdr
                           pas)]
      (zulu-select
       (fn [t]
         (and (map-and (fn [t2] (memq2 (tcmp t2 t) :less :equal)) less)
              (map-and (fn [t2] (memq2 (tcmp t2 t) :more :equal)) more)))
       (lattice->elements target)))))

(defn- mmap
  "map 作用于可变序对表，得到 Clojure 表。"
  [f l]
  (if (nil? l) nil (cons (f (mcar l)) (mmap f (mcdr l)))))

(defn- map1 [f l]
  (if (nil? l) nil (cons (f (first l)) (map1 f (next l)))))

(defn- append2 [a b]
  (if (nil? a) b (cons (first a) (append2 (next a) b))))

(defn- append-all
  "(apply append x)"
  [ls]
  (cond (nil? ls) nil
        (nil? (next ls)) (first ls)
        :else (append2 (first ls) (append-all (next ls)))))

(defn- maps-rest [source target pas rest to-1 to-collect]
  (if (nil? rest)
    (to-1 pas)
    (let [nxt (first rest)
          rest (next rest)]
      (to-collect
       (mmap (fn [x]
               (maps-rest source target
                          (cons (mcons nxt x) pas)
                          rest
                          to-1
                          to-collect))
             (maps-1 source target pas nxt))))))

(defn- maps [source target]
  (make-lattice
   (maps-rest source
              target
              nil
              (lattice->elements source)
              (fn [x] (list (map1 mcdr x)))
              (fn [x] (append-all x)))
   (lexico (lattice->cmp target))))

(defn- sum [lst]
  (if (nil? lst)
    0
    (+ (first lst) (sum (next lst)))))

(defn- count-maps [source target]
  (maps-rest source
             target
             nil
             (lattice->elements source)
             (fn [x] 1)
             sum))

(defn- run [k]
  (let [l2 (make-lattice (list :low :high)
                         (fn [lhs rhs]
                           (case lhs
                             :low (case rhs
                                    :low :equal
                                    :high :less
                                    (fatal "make-lattice base " rhs))
                             :high (case rhs
                                     :low :more
                                     :high :equal
                                     (fatal "make-lattice base " rhs))
                             (fatal "make-lattice base " lhs))))
        l3 (maps l2 l2)
        l4 (maps l3 l3)]
    (count-maps l2 l2)
    (count-maps l3 l3)
    (count-maps l2 l3)
    (count-maps l3 l2)
    (case k
      33 (count-maps l3 l3)
      44 (count-maps l4 l4)
      45 (let [l5 (maps l4 l4)] (count-maps l4 l5))
      54 (let [l5 (maps l4 l4)] (count-maps l5 l4))
      55 (let [l5 (maps l4 l4)] (count-maps l5 l5))
      (fatal "run: unanticipated problem size " k))))

(defn -main [& _]
  (let [count (read-input) input1 (read-input) output (read-input)]
    (run-r7rs-benchmark (str "lattice:" input1 ":" count) count
                        (fn [] (run (hide count input1)))
                        (fn [result] (= result output)))))
