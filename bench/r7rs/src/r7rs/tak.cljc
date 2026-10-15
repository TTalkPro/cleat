;;; TAK -- A vanilla version of the TAKeuchi function.
(ns r7rs.tak
  (:require [r7rs.common :refer [hide read-input run-r7rs-benchmark]]))

(defn tak [x y z]
  (if (not (< y x))
    z
    (tak (tak (- x 1) y z)
         (tak (- y 1) z x)
         (tak (- z 1) x y))))

(defn -main [& _]
  (let [count (read-input) input1 (read-input) input2 (read-input) input3 (read-input) output (read-input)]
    (run-r7rs-benchmark (str "tak:" input1 ":" input2 ":" input3 ":" count) count
                        (fn [] (tak (hide count input1) (hide count input2) (hide count input3)))
                        (fn [result] (= result output)))))
