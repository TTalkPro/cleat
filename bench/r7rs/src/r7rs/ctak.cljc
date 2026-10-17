;;; CTAK -- A version of the TAK procedure that uses continuations.
(ns r7rs.ctak
  (:require [r7rs.common :refer [hide read-input run-r7rs-benchmark]]
            [r7rs.util :as u]))

;; 原版的 call/cc 都只作逃逸用；cleat-125 用语言层 escape* 表达同一动态范围协议。
(declare ctak-aux)

(def ctak
  (fn* [x y z]
    #?(:cljr (escape* (fn [k] (ctak-aux k x y z)))
       :clj (u/call-with-escape (fn [k] (ctak-aux k x y z))))))

(def ctak-aux
  (fn* [^:escape k x y z]
    (if (not (< y x))
      (k z)
      #?(:cljr (escape* (fn [k']
                 (ctak-aux
                  k'
                  (escape* (fn [k] (ctak-aux k (- x 1) y z)))
                  (escape* (fn [k] (ctak-aux k (- y 1) z x)))
                  (escape* (fn [k] (ctak-aux k (- z 1) x y))))))
         :clj (u/call-with-escape (fn [k']
                 (ctak-aux
                  k'
                  (u/call-with-escape (fn [k] (ctak-aux k (- x 1) y z)))
                  (u/call-with-escape (fn [k] (ctak-aux k (- y 1) z x)))
                 (u/call-with-escape (fn [k] (ctak-aux k (- z 1) x y))))))))))

(defn -main [& _]
  (let [count (read-input) input1 (read-input) input2 (read-input) input3 (read-input) output (read-input)
        s4 (str count) s3 (str input3) s2 (str input2) s1 (str input1)
        name "ctak"]
    (run-r7rs-benchmark (str name ":" s1 ":" s2 ":" s3 ":" s4) count
                        (fn [] (ctak (hide count input1) (hide count input2) (hide count input3)))
                        (fn [result] (= result output)))))
