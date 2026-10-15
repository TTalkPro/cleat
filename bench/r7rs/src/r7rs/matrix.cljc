;;; MATRIX -- Obtained from Andrew Wright.
(ns r7rs.matrix
  (:require [r7rs.common :refer [hide read-input run-r7rs-benchmark]]
            [r7rs.util :refer [mcons mcar mcdr set-mcar! set-mcdr! make-vector]]))

;; Scheme 的 map 是急切的、产生表；这里用递归的急切版本（不用惰性序列）
(defn- map1 [f l]
  (if (empty? l) () (cons (f (first l)) (map1 f (rest l)))))
(defn- map2 [f l1 l2]
  (if (or (empty? l1) (empty? l2)) () (cons (f (first l1) (first l2)) (map2 f (rest l1) (rest l2)))))

;;; We need R6RS div and mod for this benchmark.
(defn div [x y]
  (cond (and (integer? x) (integer? y) (>= x 0))
        (quot x y)
        (< y 0)
        (let [q (quot x y) r (- x (* q y))]
          (if (== r 0) q (+ q 1)))
        :else
        (let [q (quot x y) r (- x (* q y))]
          (if (== r 0) q (- q 1)))))

(defn mod* [x y]
  (cond (and (integer? x) (integer? y) (>= x 0))
        (rem x y)
        (< y 0)
        (let [q (quot x y) r (- x (* q y))]
          (if (== r 0) 0 (- r y)))
        :else
        (let [q (quot x y) r (- x (* q y))]
          (if (== r 0) 0 (+ r y)))))

;; Chez-Scheme compatibility stuff（盒子是可变序对）
(defn chez-box [x] (mcons x nil))
(defn chez-unbox [x] (mcar x))
(defn chez-set-box! [x y] (set-mcar! x y))

(declare gen-perms zunda zebra zulu fold miota)

(defn maximal? [mat]
  (loop [first-row-perm (gen-perms mat)]
    (if first-row-perm
      (and (zunda first-row-perm mat)
           (recur (first-row-perm :brother)))
      true)))

(defn zunda [first-row-perm mat]
  (let [first-row (first-row-perm :now)
        number-of-cols (count first-row)
        make-row->func
        (fn [if-equal if-different]
          (fn [row]
            (let [^objects vec (make-vector number-of-cols)]
              (loop [i 0 first first-row row row]
                (when-not (== i number-of-cols)
                  (aset vec i (if (== (clojure.core/first first) (clojure.core/first row)) if-equal if-different))
                  (recur (+ i 1) (rest first) (rest row))))
              (fn [i] (aget vec i)))))
        mat (rest mat)]
    (zebra (first-row-perm :child)
           (make-row->func 1 -1)
           (make-row->func -1 1)
           mat
           number-of-cols)))

(defn zebra [row-perm row->func+ row->func- mat number-of-cols]
  ((fn _-*- [row-perm mat partitions]
     (or (not row-perm)
         (and (zulu (first mat)
                    (row->func+ (row-perm :now))
                    partitions
                    (fn [new-partitions]
                      (_-*- (row-perm :child) (rest mat) new-partitions)))
              (zulu (first mat)
                    (row->func- (row-perm :now))
                    partitions
                    (fn [new-partitions]
                      (_-*- (row-perm :child) (rest mat) new-partitions)))
              (let [new-row-perm (row-perm :brother)]
                (or (not new-row-perm)
                    (recur new-row-perm mat partitions))))))
   row-perm mat (list (miota number-of-cols))))

(defn- cons-if-not-null [lhs rhs]
  (if (empty? lhs) rhs (cons lhs rhs)))

;; 原版是三个嵌套的命名 let：_-split- 与 _-minus- 的自尾调用用 recur；
;; _-minus- 尾调用外层 _-*- 改成普通调用（深度不超过分区数，即列数）。
(defn zulu [old-row new-row-func partitions equal-cont]
  ((fn _-*- [p-in old-row rev-p-out]
     (loop [partition (first p-in) old-row old-row plus () minus ()]
       (if (empty? partition)
         (loop [old-row old-row m minus]
           (if (empty? m)
             (let [rev-p-out (cons-if-not-null minus (cons-if-not-null plus rev-p-out))
                   p-in (rest p-in)]
               (if (empty? p-in)
                 (equal-cont (reverse rev-p-out))
                 (_-*- p-in old-row rev-p-out)))
             (or (== 1 (first old-row))
                 (recur (rest old-row) (rest m)))))
         (let [next (first partition)]
           (case (new-row-func next)
             1 (and (== 1 (first old-row))
                    (recur (rest partition) (rest old-row) (cons next plus) minus))
             -1 (recur (rest partition) old-row plus (cons next minus)))))))
   partitions old-row ()))

(defn all? [ok? lst]
  (loop [lst lst]
    (or (empty? lst)
        (and (ok? (first lst))
             (recur (rest lst))))))

(defn gen-perms [objects]
  ((fn _-*- [zulu-future past]
     (if (empty? zulu-future)
       false
       (fn [msg]
         (case msg
           :now (first zulu-future)
           :brother (_-*- (rest zulu-future) (cons (first zulu-future) past))
           :child (gen-perms (fold past cons (rest zulu-future)))
           :puke (cons (first zulu-future) (fold past cons (rest zulu-future)))
           (throw (ex-info (str "gen-perms: Bad msg: " msg) {}))))))
   objects ()))

(defn fold [lst folder state]
  (loop [lst lst state state]
    (if (empty? lst)
      state
      (recur (rest lst) (folder (first lst) state)))))

(defn miota [len]
  ((fn _-*- [i]
     (if (== i len) () (cons i (_-*- (+ i 1)))))
   0))

(defn proc->vector [size proc]
  (let [^objects res (make-vector size)]
    (loop [i 0]
      (when-not (== i size)
        (aset res i (proc i))
        (recur (+ i 1))))
    res))

(declare extended-gcd)

;; Given a prime number P, return a procedure which, given a `maker' procedure,
;; calls it on the operations for the field Z/PZ.
(defn make-modular [modulus]
  (let [reduce (fn [x] (mod* x modulus))
        coef-zero? (fn [x] (zero? (reduce x)))
        coef-+ (fn [x y] (reduce (+ x y)))
        coef-negate (fn [x] (reduce (- x)))
        coef-* (fn [x y] (reduce (* x y)))
        coef-recip (let [^objects inverses
                         (proc->vector (- modulus 1)
                                       (fn [i]
                                         (extended-gcd (+ i 1) modulus
                                                       (fn [gcd inverse ignore] inverse))))]
                     (fn [x]
                       (let [x (reduce x)]
                         (aget inverses (- x 1)))))]
    (fn [maker]
      (maker 0 1 coef-zero? coef-+ coef-negate coef-* coef-recip))))

;; Extended Euclidean algorithm.
(def extended-gcd
  (let [n->sgn-abs (fn [x cont]
                     (if (>= x 0)
                       (cont 1 x)
                       (cons -1 (- x))))]
    (fn [a b cont]
      (n->sgn-abs a
                  (fn [p-a p]
                    (n->sgn-abs b
                                (fn [q-b q]
                                  (loop [p p p-a p-a p-b 0 q q q-a 0 q-b q-b]
                                    (if (zero? q)
                                      (cont p p-a p-b)
                                      (let [mult (div p q)]
                                        (recur q q-a q-b
                                               (- p (* mult q))
                                               (- p-a (* mult q-a))
                                               (- p-b (* mult q-b)))))))))))))

(defn make-row-reduce [coef-zero coef-one coef-zero? coef-+ coef-negate coef-* coef-recip]
  (fn [mat]
    ((fn _-*- [mat]
       (if (or (empty? mat) (empty? (first mat)))
         ()
         (loop [in mat out ()]
           (if (empty? in)
             (map1 (fn [x] (cons coef-zero x)) (_-*- out))
             (let [prow (first in)
                   pivot (first prow)
                   prest (rest prow)
                   in (rest in)]
               (if (coef-zero? pivot)
                 (recur in (cons prest out))
                 (let [zap-row (map1 (let [mult (coef-recip pivot)]
                                       (fn [x] (coef-* mult x)))
                                     prest)]
                   (cons (cons coef-one zap-row)
                         (map1 (fn [x] (cons coef-zero x))
                               (_-*- (fold in
                                           (fn [row mat]
                                             (cons (let [first-col (first row)
                                                         rest-row (rest row)]
                                                     (if (coef-zero? first-col)
                                                       rest-row
                                                       (map2 (let [mult (coef-negate first-col)]
                                                               (fn [f z] (coef-+ f (coef-* mult z))))
                                                             rest-row
                                                             zap-row)))
                                                   mat))
                                           out)))))))))))
     mat)))

(defn make-in-row-space? [coef-zero coef-one coef-zero? coef-+ coef-negate coef-* coef-recip]
  (let [row-reduce (make-row-reduce coef-zero coef-one coef-zero? coef-+ coef-negate coef-* coef-recip)]
    (fn [mat]
      (let [mat (row-reduce mat)]
        (fn [row]
          (loop [row row mat mat]
            (if (empty? row)
              true
              (let [r-first (first row)
                    r-rest (rest row)]
                (cond (coef-zero? r-first)
                      (recur r-rest
                             (map1 rest (if (or (empty? mat) (coef-zero? (first (first mat))))
                                          mat
                                          (rest mat))))
                      (empty? mat)
                      false
                      :else
                      (let [zap-row (first mat)
                            z-first (first zap-row)
                            z-rest (rest zap-row)
                            mat (rest mat)]
                        (if (coef-zero? z-first)
                          false
                          (recur (map2 (let [mult (coef-negate r-first)]
                                         (fn [r z] (coef-+ r (coef-* mult z))))
                                       r-rest
                                       z-rest)
                                 (map1 rest mat)))))))))))))

(defn make-modular-row-reduce [modulus]
  ((make-modular modulus) make-row-reduce))

(defn make-modular-in-row-space? [modulus]
  ((make-modular modulus) make-in-row-space?))

;; Given a bound, find a prime greater than the bound.（primes 是边遍历边在尾部追加的可变表）
(defn find-prime [bound]
  (let [primes (mcons 2 nil)
        last (chez-box primes)
        is-next-prime? (fn [trial]
                         (loop [primes primes]
                           (or (nil? primes)
                               (let [p (mcar primes)]
                                 (or (< trial (* p p))
                                     (and (not (zero? (mod* trial p)))
                                          (recur (mcdr primes))))))))]
    (if (> 2 bound)
      2
      (loop [trial 3]
        (if (is-next-prime? trial)
          (let [entry (mcons trial nil)]
            (set-mcdr! (chez-unbox last) entry)
            (chez-set-box! last entry)
            (if (> trial bound)
              trial
              (recur (+ trial 2))))
          (recur (+ trial 2)))))))

(defn- expt [b e]
  (loop [acc 1 e e] (if (zero? e) acc (recur (* acc b) (- e 1)))))

(defn det-upper-bound [size]
  (let [main-part (expt size (div size 2))]
    (if (even? size)
      main-part
      (* main-part
         (loop [i 0]
           (if (>= (* i i) size) i (recur (+ i 1))))))))

(declare fold-over-subs-of-size fold-over-rows remove-in-order)

;; Fold over all maximal matrices.
(defn go [number-of-cols inv-size folder state]
  (let [in-row-space? (make-modular-in-row-space? (find-prime (det-upper-bound inv-size)))
        make-tester (fn [mat]
                      (let [tests (let [old-mat (rest mat)
                                        new-row (first mat)]
                                    (fold-over-subs-of-size old-mat
                                                            (- inv-size 2)
                                                            (fn [sub tests]
                                                              (cons (in-row-space? (cons new-row sub))
                                                                    tests))
                                                            ()))]
                        (fn [row]
                          (loop [tests tests]
                            (and (not (empty? tests))
                                 (or ((first tests) row)
                                     (recur (rest tests))))))))
        all-rows (fold (fold-over-rows (- number-of-cols 1) cons ())
                       (fn [row rows] (cons (cons 1 row) rows))
                       ())]
    ((fn _-*- [number-of-rows rev-mat possible-future state]
       (let [zulu-future (remove-in-order
                          (if (< number-of-rows inv-size)
                            (in-row-space? rev-mat)
                            (make-tester rev-mat))
                          possible-future)]
         (if (empty? zulu-future)
           (folder (reverse rev-mat) state)
           (loop [zulu-future zulu-future state state]
             (if (empty? zulu-future)
               state
               (let [rest-of-future (rest zulu-future)]
                 (recur rest-of-future
                        (let [first (first zulu-future)
                              new-rev-mat (cons first rev-mat)]
                          (if (maximal? (reverse new-rev-mat))
                            (_-*- (+ number-of-rows 1) new-rev-mat rest-of-future state)
                            state)))))))))
     1 (list (first all-rows)) (rest all-rows) state)))

(defn go-folder [mat bsize-blen-blist]
  (let [bsize (first bsize-blen-blist)
        size (count mat)]
    (if (< size bsize)
      bsize-blen-blist
      (let [blen (second bsize-blen-blist)
            blist (rest (rest bsize-blen-blist))]
        (if (== size bsize)
          (let [blen (+ blen 1)]
            (cons bsize
                  (cons blen
                        (cond (< blen 3000) (cons mat blist)
                              (== blen 3000) (cons "..." blist)
                              :else blist))))
          (list size 1 mat))))))

(defn really-go [number-of-cols inv-size]
  (rest (rest (go number-of-cols inv-size go-folder (list -1 -1)))))

(defn remove-in-order [remove? lst]
  (reverse (fold lst (fn [e lst] (if (remove? e) lst (cons e lst))) ())))

(defn fold-over-rows [number-of-cols folder state]
  (if (zero? number-of-cols)
    (folder () state)
    (fold-over-rows (- number-of-cols 1)
                    (fn [tail state] (folder (cons -1 tail) state))
                    (fold-over-rows (- number-of-cols 1)
                                    (fn [tail state] (folder (cons 1 tail) state))
                                    state))))

;; Fold over subsets of a given size.
(defn fold-over-subs-of-size [universe size folder state]
  (let [usize (count universe)]
    (if (< usize size)
      state
      ((fn _-*- [size universe folder csize state]
         (cond (zero? csize) (folder universe state)
               (zero? size) (folder () state)
               :else (let [first-u (first universe)
                           rest-u (rest universe)]
                       (recur size rest-u folder (- csize 1)
                              (_-*- (- size 1) rest-u
                                    (fn [tail state] (folder (cons first-u tail) state))
                                    csize state)))))
       size universe folder (- usize size) state))))

(defn -main [& _]
  (let [count (read-input) input1 (read-input) input2 (read-input) output (read-input)
        s3 (str count) s2 (str input2) s1 (str input1)
        name "matrix"]
    (run-r7rs-benchmark (str name ":" s1 ":" s2 ":" s3) count
                        (fn [] (really-go (hide count input1) (hide count input2)))
                        (fn [result] (= result output)))))
