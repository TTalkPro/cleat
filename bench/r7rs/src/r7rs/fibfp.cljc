;;; FIBFP -- Computes fib(35) using floating point
(ns r7rs.fibfp
  (:require [r7rs.common :refer [hide read-input run-r7rs-benchmark]]))

(defn fibfp [n]
  (if (< n 2.0)
    n
    (+ (fibfp (- n 1.0))
       (fibfp (- n 2.0)))))

(defn -main [& _]
  (let [count (read-input) input (read-input) output (read-input)
        s2 (str count) s1 (str input)
        name "fibfp"]
    (run-r7rs-benchmark (str name ":" s1 ":" s2) count
                        (fn [] (fibfp (hide count input)))
                        (fn [result] (= result output)))))
