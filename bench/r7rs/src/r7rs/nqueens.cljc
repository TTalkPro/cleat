;;; NQUEENS -- Compute number of solutions to 8-queens problem.
(ns r7rs.nqueens
  (:require [r7rs.common :refer [hide read-input run-r7rs-benchmark]]))

(def trace? false)

;; Scheme 的 append（及早复制第一个表）；clojure.core/concat 是惰性的，形状不同
(defn- append [x y]
  (if (empty? x)
    y
    (cons (first x) (append (rest x) y))))

(defn nqueens [n]
  (letfn [(iota1 [n]
            (loop [i n l ()]
              (if (= i 0) l (recur (- i 1) (cons i l)))))
          (my-try [x y z]
            (if (empty? x)
              (if (empty? y)
                (do (when trace? (prn z))
                    1)
                0)
              (+ (if (ok? (first x) 1 z)
                   (my-try (append (rest x) y) () (cons (first x) z))
                   0)
                 (my-try (rest x) (cons (first x) y) z))))
          (ok? [row dist placed]
            (if (empty? placed)
              true
              (and (not (= (first placed) (+ row dist)))
                   (not (= (first placed) (- row dist)))
                   (ok? row (+ dist 1) (rest placed)))))]
    (my-try (iota1 n) () ())))

(defn -main [& _]
  (let [count (read-input) input1 (read-input) output (read-input)
        s2 (str count) s1 (str input1)
        name "nqueens"]
    (run-r7rs-benchmark (str name ":" s1 ":" s2) count
                        (fn [] (nqueens (hide count input1)))
                        (fn [result] (= result output)))))
