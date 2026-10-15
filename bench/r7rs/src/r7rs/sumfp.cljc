;;; SUMFP -- Compute sum of integers from 0 to 1e6 using floating point
(ns r7rs.sumfp
  (:require [r7rs.common :refer [hide read-input run-r7rs-benchmark]]))

(defn run [n]
  (loop [i n sum 0.0]
    (if (< i 0.0)
      sum
      (recur (- i 1.0) (+ i sum)))))

(defn -main [& _]
  (let [count (read-input) input1 (read-input) output (read-input)
        s2 (str count) s1 (str input1)
        name "sumfp"]
    (run-r7rs-benchmark (str name ":" s1 ":" s2) count
                        (fn [] (run (hide count input1)))
                        (fn [result] (= result output)))))
