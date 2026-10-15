;;; FIB -- A classic benchmark, computes fib(n) inefficiently.
(ns r7rs.fib
  (:require [r7rs.common :refer [hide read-input run-r7rs-benchmark]]))

(defn fib [n]
  (if (< n 2)
    n
    (+ (fib (- n 1))
       (fib (- n 2)))))

(defn -main [& _]
  (let [count (read-input) input (read-input) output (read-input)]
    (run-r7rs-benchmark (str "fib:" input ":" count) count
                        (fn [] (fib (hide count input)))
                        (fn [result] (= result output)))))
