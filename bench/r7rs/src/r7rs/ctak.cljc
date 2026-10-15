;;; CTAK -- A version of the TAK procedure that uses continuations.
(ns r7rs.ctak
  (:require [r7rs.common :refer [hide read-input run-r7rs-benchmark]]
            [r7rs.util :as u]))

;; 原版的 call/cc 都只作逃逸用：每个 k 只在捕获它的 call/cc 的动态范围内被调用（ctak-aux 总以 (k z) 结束），
;; 所以用 util/call-with-escape（异常实现的单向逃逸），语义相同。
(declare ctak-aux)

(defn ctak [x y z]
  (u/call-with-escape
   (fn [k] (ctak-aux k x y z))))

(defn ctak-aux [k x y z]
  (if (not (< y x))
    (k z)
    (u/call-with-escape
     (fn [k]
       (ctak-aux
        k
        (u/call-with-escape
         (fn [k] (ctak-aux k (- x 1) y z)))
        (u/call-with-escape
         (fn [k] (ctak-aux k (- y 1) z x)))
        (u/call-with-escape
         (fn [k] (ctak-aux k (- z 1) x y))))))))

(defn -main [& _]
  (let [count (read-input) input1 (read-input) input2 (read-input) input3 (read-input) output (read-input)
        s4 (str count) s3 (str input3) s2 (str input2) s1 (str input1)
        name "ctak"]
    (run-r7rs-benchmark (str name ":" s1 ":" s2 ":" s3 ":" s4) count
                        (fn [] (ctak (hide count input1) (hide count input2) (hide count input3)))
                        (fn [result] (= result output)))))
