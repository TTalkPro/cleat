;;; Compute digits of PI using a straightforward implementation of
;;; the Chudnovsky brothers algorithm; see
;;; http://www.craig-wood.com/nick/articles/pi-chudnovsky/
;;; 精确整数用 +' -' *' 与 quot；exact-integer-sqrt / expt 私有实现（同 r7rs.pi）。ch-C^3 改名 ch-C-cubed（^ 是 Clojure 的元数据读者宏）。
(ns r7rs.chudnovsky
  (:refer-clojure :exclude [pi])
  (:require [r7rs.common :refer [hide read-input run-r7rs-benchmark]]
            [r7rs.util :as u]))

(defn- bit-length
  "非负整数 n 的二进制位数。"
  [n]
  #?(:clj (.bitLength (biginteger n))
     :cljr (let [^uints m (.GetMagnitude (biginteger n)) len (alength m)]
             (if (zero? len)
               0
               (loop [top (long (aget m 0)) bits (* 32 (dec len))]
                 (if (zero? top) bits (recur (bit-shift-right top 1) (inc bits))))))))

(defn- expt [base e]
  (loop [r 1 b base e e]
    (cond (zero? e) r
          (odd? e) (recur (*' r b) (*' b b) (quot e 2))
          :else (recur r (*' b b) (quot e 2)))))

(defn- exact-integer-sqrt
  "原版 (exact-integer-sqrt x) 的商 q：最大的 q 使 q*q <= x。"
  [x]
  (if (< x 2)
    x
    (loop [q (expt 2 (quot (inc (bit-length x)) 2))]
      (let [y (quot (+' q (quot x q)) 2)]
        (if (< y q) (recur y) q)))))

(def ch-A 13591409)
(def ch-B 545140134)
(def ch-C 640320)
(def ch-C-cubed (expt 640320 3))
(def ch-D 12)

(defn ch-split [a b]
  (if (= 1 (-' b a))
    (let [g (*' (-' (*' 6 b) 5) (-' (*' 2 b) 1) (-' (*' 6 b) 1))]
      (list g
            (quot (*' ch-C-cubed (expt b 3)) 24)
            (*' (expt -1 b) g (+' (*' b ch-B) ch-A))))
    (let [mid (quot (+' a b) 2)
          gpq1 (ch-split a mid)
          gpq2 (ch-split mid b)
          g1 (first gpq1) p1 (second gpq1) q1 (first (nnext gpq1))
          g2 (first gpq2) p2 (second gpq2) q2 (first (nnext gpq2))]
      (list (*' g1 g2)
            (*' p1 p2)
            (+' (*' q1 p2) (*' q2 g1))))))

(defn integer-sqrt [x]
  (exact-integer-sqrt x))

(defn pi [digits]
  (let [num-terms (long (u/floor (+ 2 (/ digits 14.181647462))))
        sqrt-C (integer-sqrt (*' ch-C (expt 100 digits)))]
    (let [gpq (ch-split 0 num-terms)
          g (first gpq) p (second gpq) q (first (nnext gpq))]
      (quot (*' p ch-C sqrt-C) (*' ch-D (+' q (*' p ch-A)))))))

(defn pies [n m s]
  (if (< m n)
    '()
    (cons (pi n)
          (pies (+ n s) m s))))

(defn -main [& _]
  (let [count (read-input) input1 (read-input) input2 (read-input) input3 (read-input) output (read-input)]
    (run-r7rs-benchmark (str "chudnovsky" ":" input1 ":" input2 ":" input3 ":" count) count
                        (fn [] (pies (hide count input1)
                                     (hide count input2)
                                     (hide count input3)))
                        (fn [result] (= result output)))))
