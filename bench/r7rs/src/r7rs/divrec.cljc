;;; DIVREC -- Benchmark which divides by 2 using lists of n ()'s (recursive).
(ns r7rs.divrec
  (:require [r7rs.common :refer [hide read-input run-r7rs-benchmark]]))

(defn create-n [n]
  (loop [n n a ()]
    (if (= n 0) a (recur (- n 1) (cons () a)))))

(defn recursive-div2 [l]
  (if (empty? l)
    ()
    (cons (first l) (recursive-div2 (nnext l)))))

(defn -main [& _]
  (let [count (read-input) input1 (read-input) output (read-input)
        s2 (str count) s1 (str input1)
        ll (create-n (hide count input1))
        name "divrec"]
    (run-r7rs-benchmark (str name ":" s1 ":" s2) count
                        (fn [] (recursive-div2 ll))
                        (fn [result] (= (clojure.core/count result) output)))))
