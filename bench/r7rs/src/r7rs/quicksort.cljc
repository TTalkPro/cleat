;; This is probably from Lars Hansen's MS thesis.
;; The quick-1 benchmark.  (Figure 35, page 132.)
;; 向量 → 对象数组；随机数生成器（L'Ecuyer）照原版用浮点运算，种子向量是对象数组。
;; 原版的 seed-set! / seed-ref 未被使用，略去（种子因此不会被替换，直接闭包捕获）。
(ns r7rs.quicksort
  (:refer-clojure :exclude [partition])
  (:require [r7rs.common :refer [hide read-input run-r7rs-benchmark]]
            [r7rs.util :refer [make-vector call-with-escape floor ceiling]]))

(declare partition)

(defn quick-1 [^objects v less?]
  (letfn [(helper [left right]
            (if (< left right)
              (let [median (partition v left right less?)]
                (if (< (- median left) (- right median))
                  (do (helper left (- median 1))
                      (helper (+ median 1) right))
                  (do (helper (+ median 1) right)
                      (helper left (- median 1)))))
              v))]
    (helper 0 (- (alength v) 1))))

(defn partition [^objects v left right less?]
  (let [mid (aget v right)
        uploop (fn [i]
                 (let [i (+ i 1)]
                   (if (and (< i right) (less? (aget v i) mid))
                     (recur i)
                     i)))
        downloop (fn [j]
                   (let [j (- j 1)]
                     (if (and (> j left) (less? mid (aget v j)))
                       (recur j)
                       j)))]
    (loop [i (- left 1) j right]
      (let [i (uploop i)
            j (downloop j)]
        (let [tmp (aget v i)]
          (aset v i (aget v j))
          (aset v j tmp)
          (if (< i j)
            (recur i j)
            (do (aset v j (aget v i))
                (aset v i (aget v right))
                (aset v right tmp)
                i)))))))

;;; A uniform [0,1] random number generator; is
;;; Pierre L'Ecuyer's generator from his paper
;;; "Good parameters and implementations for combined multiple
;;; recursive random number generators"

;; Scheme 的 truncate 作用于浮点数
(defn- truncate [x] (if (< x 0.0) (ceiling x) (floor x)))

(def random-flonum
  (let [norm 2.328306549295728e-10
        m1 4294967087.0
        m2 4294944443.0
        a12 1403580.0
        a13n 810728.0
        a21 527612.0
        a23n 1370589.0
        seed (object-array [1.0 0.0 0.0 1.0 0.0 0.0])] ;; will be mutated
    ;; uses no conversions between flonums and fixnums.
    (fn []
      (let [^objects seed seed]
        (let [p1 (- (* a12 (aget seed 1))
                    (* a13n (aget seed 0)))
              p2 (- (* a21 (aget seed 5))
                    (* a23n (aget seed 3)))]
          (let [k1 (truncate (/ p1 m1))
                k2 (truncate (/ p2 m2))
                ignore1 (aset seed 0 (aget seed 1))
                ignore3 (aset seed 3 (aget seed 4))]
            (let [p1 (- p1 (* k1 m1))
                  p2 (- p2 (* k2 m2))
                  ignore2 (aset seed 1 (aget seed 2))
                  ignore4 (aset seed 4 (aget seed 5))]
              (let [p1 (if (< p1 0.0) (+ p1 m1) p1)
                    p2 (if (< p2 0.0) (+ p2 m2) p2)]
                (aset seed 2 (num p1))
                (aset seed 5 (num p2))
                (if (<= p1 p2)
                  (* norm (+ (- p1 p2) m1))
                  (* norm (- p1 p2)))))))))))

(defn random [n]
  (long (truncate (* (double n) (random-flonum)))))

(defn- vector-map [f ^objects v]
  (let [n (alength v) ^objects r (make-vector n)]
    (loop [i 0]
      (if (< i n)
        (do (aset r i (f (aget v i))) (recur (+ i 1)))
        r))))

(defn -main [& _]
  (let [count (read-input) input1 (read-input) input2 (read-input) output (read-input)
        s3 (str count) s2 (str input2) s1 (str input1) name "quicksort"
        n (hide count input1)
        r (hide count input2)
        less? (hide count (fn [x y] (< x y)))
        ^objects v (make-vector n)]
    (loop [i 0]
      (when-not (= i n)
        (aset v i (random r))
        (recur (+ i 1))))
    (run-r7rs-benchmark (str name ":" s1 ":" s3) count
                        (fn [] (quick-1 (vector-map identity v) less?))
                        (fn [^objects v]
                          (call-with-escape
                           (fn [return]
                             (loop [i 1]
                               (if (= i (alength v))
                                 true
                                 (do (when-not (<= (aget v (- i 1)) (aget v i))
                                       (return false))
                                     (recur (+ i 1)))))))))))
