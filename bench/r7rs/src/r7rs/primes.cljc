;;; PRIMES -- Compute primes less than 100, written by Eric Mohr.
(ns r7rs.primes
  (:require [r7rs.common :refer [hide read-input run-r7rs-benchmark]]))

(defn interval-list [m n]
  (if (> m n)
    ()
    (cons m (interval-list (+ 1 m) n))))

(defn sieve [l]
  (letfn [(remove-multiples [n l]
            (if (empty? l)
              ()
              (if (= (rem (first l) n) 0)
                (remove-multiples n (rest l))
                (cons (first l)
                      (remove-multiples n (rest l))))))]
    (if (empty? l)
      ()
      (cons (first l)
            (sieve (remove-multiples (first l) (rest l)))))))

(defn primes<= [n]
  (sieve (interval-list 2 n)))

(defn -main [& _]
  (let [count (read-input) input1 (read-input) output (read-input)
        s2 (str count) s1 (str input1)
        name "primes"]
    (run-r7rs-benchmark (str name ":" s1 ":" s2) count
                        (fn [] (primes<= (hide count input1)))
                        (fn [result] (= result output)))))
