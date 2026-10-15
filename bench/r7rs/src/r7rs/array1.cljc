;;; ARRAY1 -- One of the Kernighan and Van Wyk benchmarks.
;;; 原版用通用向量（make-vector / vector-ref / vector-set!）：这里是对象数组（^objects 只说明是向量，元素仍是装箱的整数）。
(ns r7rs.array1
  (:require [r7rs.common :refer [hide read-input run-r7rs-benchmark]]
            [r7rs.util :refer [make-vector]]))

(defn create-x [n]
  (let [^objects result (make-vector n)]
    (loop [i 0]
      (if (>= i n)
        result
        (do (aset result i (num i))
            (recur (+ i 1)))))))

(defn create-y [^objects x]
  (let [n (alength x)
        ^objects result (make-vector n)]
    (loop [i (- n 1)]
      (if (< i 0)
        result
        (do (aset result i (aget x i))
            (recur (- i 1)))))))

(defn my-try [n]
  (alength ^objects (create-y (create-x n))))

(defn go [m n]
  (loop [repeat m result ()]
    (if (> repeat 0)
      (recur (- repeat 1) (my-try n))
      result)))

(defn -main [& _]
  (let [count (read-input) input1 (read-input) output (read-input)
        s2 (str count) s1 (str input1) name "array1"]
    (run-r7rs-benchmark (str name ":" s1 ":" s2) 1
                        (fn [] (go (hide count count) (hide count input1)))
                        (fn [result] (= result output)))))
