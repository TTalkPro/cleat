;;; PNPOLY - Test if a point is contained in a 2D polygon.
;;; 输入是 Scheme 向量字面量 #(0. 1. -.5 …)：Clojure 读取器把它读成 (fn* [] (…))，其中 .5 / -.5 读成符号；
;;; scheme-vector 把读到的形式还原成向量（只在读输入时做一次，不计入循环）。不变的向量用 Clojure 向量。
;;; 原版对局部 count 的 set! 改成顺序 let 绑定。
(ns r7rs.pnpoly
  (:require [r7rs.common :refer [hide read-input run-r7rs-benchmark]]))

(defn- scheme-number [x]
  (if (symbol? x) (parse-double (str x)) x))

(defn- scheme-vector [form]
  (if (and (seq? form) (= 'fn* (first form)))
    (vec (map scheme-number (nth form 2)))
    (vec (map scheme-number form))))

(defn pt-in-poly2 [xp yp x y]
  (loop [c false i (- (count xp) 1) j 0]
    (if (< i 0)
      c
      (if (or (and (or (> (nth yp i) y)
                       (>= y (nth yp j)))
                   (or (> (nth yp j) y)
                       (>= y (nth yp i))))
              (>= x
                  (+ (nth xp i)
                     (/ (*
                         (- (nth xp j)
                            (nth xp i))
                         (- y (nth yp i)))
                        (- (nth yp j)
                           (nth yp i))))))
        (recur c (- i 1) i)
        (recur (not c) (- i 1) i)))))

(defn run [input1 input2]
  (let [count 0
        xp (vec (seq input1))        ; (list->vector (vector->list input1))
        yp (vec (seq input2))
        count (if (pt-in-poly2 xp yp 0.5 0.5) (+ count 1) count)
        count (if (pt-in-poly2 xp yp 0.5 1.5) (+ count 1) count)
        count (if (pt-in-poly2 xp yp -0.5 1.5) (+ count 1) count)
        count (if (pt-in-poly2 xp yp 0.75 2.25) (+ count 1) count)
        count (if (pt-in-poly2 xp yp 0.0 2.01) (+ count 1) count)
        count (if (pt-in-poly2 xp yp -0.5 2.5) (+ count 1) count)
        count (if (pt-in-poly2 xp yp -1.0 -0.5) (+ count 1) count)
        count (if (pt-in-poly2 xp yp -1.5 0.5) (+ count 1) count)
        count (if (pt-in-poly2 xp yp -2.25 -1.0) (+ count 1) count)
        count (if (pt-in-poly2 xp yp 0.5 -0.25) (+ count 1) count)
        count (if (pt-in-poly2 xp yp 0.5 -1.25) (+ count 1) count)
        count (if (pt-in-poly2 xp yp -0.5 -2.5) (+ count 1) count)]
    count))

(defn -main [& _]
  (let [count (read-input) input1 (scheme-vector (read-input)) input2 (scheme-vector (read-input)) output (read-input)]
    (run-r7rs-benchmark (str "pnpoly" ":" count) count
                        (fn [] (run (hide count input1) (hide count input2)))
                        (fn [result] (and (number? result) (= result output))))))
