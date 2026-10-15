;;; PI -- Compute PI using bignums.
;;; 精确整数可能超过 64 位：用 +' -' *' 与 quot。Clojure 没有 exact-integer-sqrt / expt：
;;; 这里私有地实现牛顿法整数平方根（初值 2^ceil(位数/2)，位数经读者条件取宿主 BigInteger 的位长）与反复平方的 expt。

;; See http://mathworld.wolfram.com/Pi.html for the various algorithms.
(ns r7rs.pi
  (:require [r7rs.common :refer [hide read-input run-r7rs-benchmark]]))

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

(defn- square [x] (*' x x))

(defn square-root [x]
  (exact-integer-sqrt x))

(defn quartic-root [x]
  (square-root (square-root x)))


;;; Compute pi using the 'brent-salamin' method.

(defn pi-brent-salamin [nb-digits]
  (let [one (expt 10 nb-digits)]
    (loop [a one
           b (square-root (quot (square one) 2))
           t (quot one 4)
           x 1]
      (if (= a b)
        (quot (square (+' a b)) (*' 4 t))
        (let [new-a (quot (+' a b) 2)]
          (recur new-a
                 (square-root (*' a b))
                 (-' t
                     (quot
                      (*' x (square (-' new-a a)))
                      one))
                 (*' 2 x)))))))

;;; Compute pi using the quadratically converging 'borwein' method.

(defn pi-borwein2 [nb-digits]
  (let [one (expt 10 nb-digits)
        one-sq (square one)
        one-4th (square one-sq)
        sqrt2 (square-root (*' one-sq 2))
        qurt2 (quartic-root (*' one-4th 2))]
    (loop [x (quot
              (*' one (+' sqrt2 one))
              (*' 2 qurt2))
           y qurt2
           p (+' (*' 2 one) sqrt2)]
      (let [new-p (quot (*' p (+' x one))
                        (+' y one))]
        (if (= x one)
          new-p
          (let [sqrt-x (square-root (*' one x))]
            (recur (quot
                    (*' one (+' x one))
                    (*' 2 sqrt-x))
                   (quot
                    (*' one (+' (*' x y) one-sq))
                    (*' (+' y one) sqrt-x))
                   new-p)))))))

;;; Compute pi using the quartically converging 'borwein' method.

(defn pi-borwein4 [nb-digits]
  (let [one (expt 10 nb-digits)
        one-sq (square one)
        one-4th (square one-sq)
        sqrt2 (square-root (*' one-sq 2))]
    (loop [y (-' sqrt2 one)
           a (-' (*' 6 one) (*' 4 sqrt2))
           x 8]
      (if (= y 0)
        (quot one-sq a)
        (let [t1 (quartic-root (-' one-4th (square (square y))))
              t2 (quot
                  (*' one (-' one t1))
                  (+' one t1))
              t3 (quot
                  (square (quot (square (+' one t2)) one))
                  one)
              t4 (+' one
                     (+' t2
                         (quot (square t2) one)))]
          (recur t2
                 (quot
                  (-' (*' t3 a) (*' x (*' t2 t4)))
                  one)
                 (*' 4 x)))))))

;;; Try it.

(defn pies [n m s]
  (if (< m n)
    '()
    (let [bs (pi-brent-salamin n)
          b2 (pi-borwein2 n)
          b4 (pi-borwein4 n)]
      (cons (list b2 (-' bs b2) (-' b4 b2))
            (pies (+ n s) m s)))))

(defn -main [& _]
  (let [count (read-input) input1 (read-input) input2 (read-input) input3 (read-input) output (read-input)]
    (run-r7rs-benchmark (str "pi" ":" input1 ":" input2 ":" input3 ":" count) count
                        (fn [] (pies (hide count input1)
                                     (hide count input2)
                                     (hide count input3)))
                        (fn [result] (= result output)))))
