;;; MBROT -- Generation of Mandelbrot set fractal.
;;; 原版的 count 改名 mbrot-count（避免遮蔽 clojure.core/count）；矩阵是可变 vector 的 vector → 对象数组。
(ns r7rs.mbrot
  (:refer-clojure :exclude [test])
  (:require [r7rs.common :refer [hide read-input run-r7rs-benchmark]]
            [r7rs.util :as u]))

(defn mbrot-count [r i step x y]
  (let [max-count 64
        radius-sq 16.0]
    (let [cr (+ r (* (double x) step))
          ci (+ i (* (double y) step))]
      (loop [zr cr
             zi ci
             c 0]
        (if (= c max-count)
          c
          (let [zr-sq (* zr zr)
                zi-sq (* zi zi)]
            (if (> (+ zr-sq zi-sq) radius-sq)
              c
              (let [new-zr (+ (- zr-sq zi-sq) cr)
                    new-zi (+ (* 2.0 (* zr zi)) ci)]
                (recur new-zr new-zi (+ c 1))))))))))

(defn mbrot [^objects matrix r i step n]
  (loop [y (- n 1)]
    (when (>= y 0)
      (let [y' (loop [x (- n 1)]
                 (if (>= x 0)
                   (do (aset ^objects (aget matrix x) y (mbrot-count r i step x y))
                       (recur (- x 1)))
                   (- y 1)))]
        (recur y')))))

(defn test [n]
  (let [^objects matrix (u/make-vector n)]
    (loop [i (- n 1)]
      (when (>= i 0)
        (aset matrix i (u/make-vector n))
        (recur (- i 1))))
    (mbrot matrix -1.0 -0.5 0.005 n)
    (aget ^objects (aget matrix 0) 0)))

(defn -main [& _]
  (let [count (read-input) input1 (read-input) output (read-input)]
    (run-r7rs-benchmark (str "mbrot" ":" input1 ":" count) count
                        (fn [] (test (hide count input1)))
                        (fn [result] (= result output)))))
