;;; FFT - Fast Fourier Transform, translated from "Numerical Recipes in C"
;;; 原版用通用 vector（非 f64vector）与通用算术：这里用对象数组与通用运算。
(ns r7rs.fft
  (:require [r7rs.common :refer [hide read-input run-r7rs-benchmark]]
            [r7rs.util :as u]))

(defn- vector-ref [^objects v i] (aget v i))
(defn- vector-set! [^objects v i x] (aset v i x))

(defn four1 [data]
  (let [n (alength ^objects data)
        pi*2 6.28318530717959]           ; to compute the inverse, negate this value

    ;; bit-reversal section

    (loop [i 0 j 0]
      (when (< i n)
        (when (< i j)
          (let [temp (vector-ref data i)]
            (vector-set! data i (vector-ref data j))
            (vector-set! data j temp))
          (let [temp (vector-ref data (+ i 1))]
            (vector-set! data (+ i 1) (vector-ref data (+ j 1)))
            (vector-set! data (+ j 1) temp)))
        ;; loop2 尾调用 loop1 (+ i 2) (+ j m)：内层 loop 返回新的 j
        (recur (+ i 2)
               (long                    ; 内层 loop 的结果是下标（fixnum）；不加 long 时 JVM 的 j 会被装箱
                (loop [m (quot n 2) j j]
                 (if (and (>= m 2) (>= j m))
                   (recur (quot m 2) (- j m))
                   (+ j m)))))))

    ;; Danielson-Lanczos section

    (loop [mmax 2]
      (when (< mmax n)
        (let [theta (/ pi*2 (double mmax))
              wpr (let [x (u/sin (* 0.5 theta))]
                    (* -2.0 (* x x)))
              wpi (u/sin theta)]
          (loop [wr 1.0 wi 0.0 m 0]
            (when (< m mmax)
              (loop [i m]
                (when (< i n)
                  (let [j (+ i mmax)
                        tempr (- (* wr (vector-ref data j))
                                 (* wi (vector-ref data (+ j 1))))
                        tempi (+ (* wr (vector-ref data (+ j 1)))
                                 (* wi (vector-ref data j)))]
                    (vector-set! data j (- (vector-ref data i) tempr))
                    (vector-set! data (+ j 1) (- (vector-ref data (+ i 1)) tempi))
                    (vector-set! data i (+ (vector-ref data i) tempr))
                    (vector-set! data (+ i 1) (+ (vector-ref data (+ i 1)) tempi))
                    (recur (+ j mmax)))))
              (recur (+ (- (* wr wpr) (* wi wpi)) wr)
                     (+ (+ (* wi wpr) (* wr wpi)) wi)
                     (+ m 2))))
          (recur (* mmax 2)))))))

(def data (u/make-vector 1024 0.0))

(defn run [data]
  (four1 data)
  (vector-ref data 0))

(defn -main [& _]
  (let [count (read-input) input1 (read-input) input2 (read-input) output (read-input)]
    (run-r7rs-benchmark (str "fft" ":" input1 ":" count) count
                        (fn [] (run (hide count (u/make-vector input1 input2))))
                        (fn [result] (= result output)))))
