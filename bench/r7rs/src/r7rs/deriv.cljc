;;; DERIV -- Symbolic derivation.
;; 不可变表：Clojure 表（cons / first / next）；符号比较用 =（读入的符号不保证同一对象）；map 用急切的私有 map1（Scheme 的 map 是急切的）。
(ns r7rs.deriv
  (:require [r7rs.common :refer [hide read-input run-r7rs-benchmark]]))

(defn- map1 [f xs]
  (if (seq xs)
    (cons (f (first xs)) (map1 f (next xs)))
    ()))

;;; Returns the wrong answer for quotients.
;;; Fortunately these aren't used in the benchmark.

(defn deriv [a]
  (cond (not (seq? a))
        (if (= a 'x) 1 0)
        (= (first a) '+)
        (cons '+ (map1 deriv (next a)))
        (= (first a) '-)
        (cons '- (map1 deriv (next a)))
        (= (first a) '*)
        (list '*
              a
              (cons '+ (map1 (fn [a] (list '/ (deriv a) a)) (next a))))
        (= (first a) '/)
        (list '-
              (list '/
                    (deriv (second a))
                    (nth a 2))
              (list '/
                    (second a)
                    (list '*
                          (nth a 2)
                          (nth a 2)
                          (deriv (nth a 2)))))
        :else
        (throw (ex-info "No derivation method available" {}))))

(defn -main [& _]
  (let [count (read-input) input1 (read-input) output (read-input)]
    (run-r7rs-benchmark (str "deriv:" count) count
                        (fn [] (deriv (hide count input1)))
                        (fn [result] (= result output)))))
