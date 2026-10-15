;;; MAZEFUN -- Constructs a maze in a purely functional way,
;;; written by Marc Feeley.
;;; 表 → Clojure 列表（cons / first / rest，急切构造）；点对 (cons i j) → 二元向量 [i j]（只读，按值比较）。
;;; Scheme 的 map / append / member 是急切的，这里各写一个急切版本（clojure.core 的 map / concat 是惰性的）。
(ns r7rs.mazefun
  (:refer-clojure :exclude [for shuffle])
  (:require [r7rs.common :refer [hide read-input run-r7rs-benchmark]]))

(defn- map1 [f lst]
  (if (seq lst)
    (cons (f (first lst)) (map1 f (rest lst)))
    ()))

(defn- append2 [a b]
  (if (seq a)
    (cons (first a) (append2 (rest a) b))
    b))

(defn- append
  ([a b] (append2 a b))
  ([a b & more] (append2 a (apply append b more))))

(defn- member [x lst]
  (loop [lst lst]
    (cond (not (seq lst)) false
          (= x (first lst)) lst
          :else (recur (rest lst)))))

(defn foldr [f base lst]
  (letfn [(foldr-aux [lst]
            (if (not (seq lst))
              base
              (f (first lst) (foldr-aux (rest lst)))))]
    (foldr-aux lst)))

(defn foldl [f base lst]
  (letfn [(foldl-aux [base lst]
            (if (not (seq lst))
              base
              (recur (f base (first lst)) (rest lst))))]
    (foldl-aux base lst)))

(defn for [lo hi f]
  (letfn [(for-aux [lo]
            (if (< lo hi)
              (cons (f lo) (for-aux (+ lo 1)))
              ()))]
    (for-aux lo)))

(defn concat* [lists]
  (foldr append () lists))

(defn list-read [lst i]
  (if (= i 0)
    (first lst)
    (recur (rest lst) (- i 1))))

(defn list-write [lst i val]
  (if (= i 0)
    (cons val (rest lst))
    (cons (first lst) (list-write (rest lst) (- i 1) val))))

(defn list-remove-pos [lst i]
  (if (= i 0)
    (rest lst)
    (cons (first lst) (list-remove-pos (rest lst) (- i 1)))))

(defn duplicates? [lst]
  (if (not (seq lst))
    false
    (or (member (first lst) (rest lst))
        (recur (rest lst)))))

(defn make-matrix [n m init]
  (for 0 n (fn [i] (for 0 m (fn [j] (init i j))))))

(defn matrix-read [mat i j]
  (list-read (list-read mat i) j))

(defn matrix-write [mat i j val]
  (list-write mat i (list-write (list-read mat i) j val)))

(defn matrix-size [mat]
  [(count mat) (count (first mat))])

(defn matrix-map [f mat]
  (map1 (fn [lst] (map1 f lst)) mat))

(def initial-random 0)

(defn next-random-number [current-random]
  (rem (+ (* current-random 3581) 12751) 131072))

(declare shuffle-aux)

(defn shuffle [lst]
  (shuffle-aux lst initial-random))

(defn shuffle-aux [lst current-random]
  (if (not (seq lst))
    ()
    (let [new-random (next-random-number current-random)]
      (let [i (rem new-random (count lst))]
        (cons (list-read lst i)
              (shuffle-aux (list-remove-pos lst i)
                           new-random))))))

(declare cave-to-maze pierce-randomly try-to-pierce change-cavity change-cavity-aux neighboring-cavities)

(defn make-maze [n m] ; n and m must be odd
  (if (not (and (odd? n) (odd? m)))
    'error
    (let [cave
          (make-matrix n m (fn [i j]
                             (if (and (even? i) (even? j))
                               [i j]
                               false)))
          possible-holes
          (concat*
           (for 0 n (fn [i]
                      (concat*
                       (for 0 m (fn [j]
                                  (if (= (even? i) (even? j))
                                    ()
                                    (list [i j]))))))))]
      (cave-to-maze (pierce-randomly (shuffle possible-holes) cave)))))

(defn cave-to-maze [cave]
  (matrix-map (fn [x] (if x '_ '*)) cave))

(defn pierce [pos cave]
  (let [i (nth pos 0) j (nth pos 1)]
    (matrix-write cave i j pos)))

(defn pierce-randomly [possible-holes cave]
  (if (not (seq possible-holes))
    cave
    (let [hole (first possible-holes)]
      (recur (rest possible-holes)
             (try-to-pierce hole cave)))))

(defn try-to-pierce [pos cave]
  (let [i (nth pos 0) j (nth pos 1)]
    (let [ncs (neighboring-cavities pos cave)]
      (if (duplicates?
           (map1 (fn [nc] (matrix-read cave (nth nc 0) (nth nc 1))) ncs))
        cave
        (pierce pos
                (foldl (fn [c nc] (change-cavity c nc pos))
                       cave
                       ncs))))))

(defn change-cavity [cave pos new-cavity-id]
  (let [i (nth pos 0) j (nth pos 1)]
    (change-cavity-aux cave pos new-cavity-id (matrix-read cave i j))))

(defn change-cavity-aux [cave pos new-cavity-id old-cavity-id]
  (let [i (nth pos 0) j (nth pos 1)]
    (let [cavity-id (matrix-read cave i j)]
      (if (= cavity-id old-cavity-id)
        (foldl (fn [c nc]
                 (change-cavity-aux c nc new-cavity-id old-cavity-id))
               (matrix-write cave i j new-cavity-id)
               (neighboring-cavities pos cave))
        cave))))

(defn neighboring-cavities [pos cave]
  (let [size (matrix-size cave)]
    (let [n (nth size 0) m (nth size 1)]
      (let [i (nth pos 0) j (nth pos 1)]
        (append (if (and (> i 0) (matrix-read cave (- i 1) j))
                  (list [(- i 1) j])
                  ())
                (if (and (< i (- n 1)) (matrix-read cave (+ i 1) j))
                  (list [(+ i 1) j])
                  ())
                (if (and (> j 0) (matrix-read cave i (- j 1)))
                  (list [i (- j 1)])
                  ())
                (if (and (< j (- m 1)) (matrix-read cave i (+ j 1)))
                  (list [i (+ j 1)])
                  ()))))))

(defn -main [& _]
  (let [count (read-input) input1 (read-input) input2 (read-input) output (read-input)
        s3 (str count) s2 (str input2) s1 (str input1) name "mazefun"]
    (run-r7rs-benchmark (str name ":" s1 ":" s2 ":" s3) count
                        (fn [] (make-maze (hide count input1) (hide count input2)))
                        (fn [result] (= result output)))))
