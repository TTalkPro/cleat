;;; CPSTAK -- A continuation-passing version of the TAK benchmark.
(ns r7rs.cpstak
  (:require [r7rs.common :refer [hide read-input run-r7rs-benchmark]]))

;; 原版全是尾调用（Scheme 有 TCO）。Clojure 没有一般尾调用：tak 的自尾调用用 recur；
;; 调用续延 (k z) 改为返回 thunk #(k z)，外层用 trampoline 驱动，否则续延链会让栈无限增长。
;; 最终续延 (fn [a] a) 返回数字（不是函数），trampoline 就此结束。
(defn cpstak [x y z]
  (letfn [(tak [x y z k]
            (if (not (< y x))
              (fn [] (k z))
              (recur (- x 1)
                     y
                     z
                     (fn [v1]
                       (tak (- y 1)
                            z
                            x
                            (fn [v2]
                              (tak (- z 1)
                                   x
                                   y
                                   (fn [v3]
                                     (tak v1 v2 v3 k)))))))))]
    (trampoline (tak x y z (fn [a] a)))))

(defn -main [& _]
  (let [count (read-input) input1 (read-input) input2 (read-input) input3 (read-input) output (read-input)
        s4 (str count) s3 (str input3) s2 (str input2) s1 (str input1)
        name "cpstak"]
    (run-r7rs-benchmark (str name ":" s1 ":" s2 ":" s3 ":" s4) count
                        (fn [] (cpstak (hide count input1) (hide count input2) (hide count input3)))
                        (fn [result] (= result output)))))
