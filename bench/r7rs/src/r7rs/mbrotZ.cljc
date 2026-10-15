;;; MBROT -- Generation of Mandelbrot set fractal
;;; using Scheme's complex numbers.
;;; Clojure 没有复数：这里私有地用一对 double 的 deftype（Complex）表示，c+ / c* / c*real 对应原版的通用 + / *（每步分配新复数，与 Scheme 的装箱复数一致）。
;;; 原版的 count 改名 mbrot-count。
(ns r7rs.mbrotZ
  (:refer-clojure :exclude [test])
  (:require [r7rs.common :refer [hide read-input run-r7rs-benchmark]]
            [r7rs.util :as u]))

(deftype Complex [^double re ^double im])

(defn make-rectangular [re im] (Complex. (double re) (double im)))
(defn real-part ^double [^Complex z] (.-re z))
(defn imag-part ^double [^Complex z] (.-im z))
(defn c+ [^Complex a ^Complex b] (Complex. (+ (.-re a) (.-re b)) (+ (.-im a) (.-im b))))
(defn c* [^Complex a ^Complex b]
  (Complex. (- (* (.-re a) (.-re b)) (* (.-im a) (.-im b)))
            (+ (* (.-re a) (.-im b)) (* (.-im a) (.-re b)))))
(defn c*real [^Complex a r] (Complex. (* (.-re a) r) (* (.-im a) r)))

(defn mbrot-count [z0 step z]
  (let [max-count 64
        radius 4.0
        radius-sq (* radius radius)]
    (let [z0 (c+ z0 (c*real z step))]
      (loop [z z0
             c 0]
        (if (= c max-count)
          c
          (let [zr (real-part z)
                zi (imag-part z)
                zr-sq (* zr zr)
                zi-sq (* zi zi)]
            (if (> (+ zr-sq zi-sq) radius-sq)
              c
              (recur (c+ (c* z z) z0) (+ c 1)))))))))

(defn mbrot [^objects matrix z0 step n]
  (loop [y (- n 1)]
    (when (>= y 0)
      (let [y' (loop [x (- n 1)]
                 (if (>= x 0)
                   (do (aset ^objects (aget matrix x)
                             y
                             (mbrot-count z0
                                          step
                                          (make-rectangular (double x)
                                                            (double y))))
                       (recur (- x 1)))
                   (- y 1)))]
        (recur y')))))

(defn test [n]
  (let [^objects matrix (u/make-vector n)]
    (loop [i (- n 1)]
      (when (>= i 0)
        (aset matrix i (u/make-vector n))
        (recur (- i 1))))
    (mbrot matrix (make-rectangular -1.0 -0.5) 0.005 n)
    (aget ^objects (aget matrix 0) 0)))

(defn -main [& _]
  (let [count (read-input) input1 (read-input) output (read-input)]
    (run-r7rs-benchmark (str "mbrotZ" ":" input1 ":" count) count
                        (fn [] (test (hide count input1)))
                        (fn [result] (= result output)))))
