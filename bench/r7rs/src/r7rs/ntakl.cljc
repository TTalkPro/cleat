;;; NTAKL -- The TAKeuchi function using lists as counters, with an alternative boolean expression.
(ns r7rs.ntakl
  (:require [r7rs.common :refer [hide read-input run-r7rs-benchmark]]))

(defn listn [n]
  (if (= n 0)
    ()
    (cons n (listn (- n 1)))))

(def l18 (listn 18))
(def l12 (listn 12))
(def l6 (listn 6))

(declare shorterp)

(defn mas [x y z]
  (if (not (shorterp y x))
    z
    (mas (mas (rest x) y z)
         (mas (rest y) z x)
         (mas (rest z) x y))))

;; shorterp 的自尾调用用 recur
(defn shorterp [x y]
  (cond (empty? y) false
        (empty? x) true
        :else (recur (rest x) (rest y))))

(defn -main [& _]
  (let [count (read-input) input1 (read-input) input2 (read-input) input3 (read-input) output (read-input)
        s4 (str count) s3 (str (clojure.core/count input3)) s2 (str (clojure.core/count input2)) s1 (str (clojure.core/count input1))
        name "ntakl"]
    (run-r7rs-benchmark (str name ":" s1 ":" s2 ":" s3 ":" s4) count
                        (fn [] (mas (hide count input1) (hide count input2) (hide count input3)))
                        (fn [result] (= (clojure.core/count result) output)))))
