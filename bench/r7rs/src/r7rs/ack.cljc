;;; ACK -- One of the Kernighan and Van Wyk benchmarks.
(ns r7rs.ack
  (:require [r7rs.common :refer [hide read-input run-r7rs-benchmark]]))

;; 第二个分支 (ack (- m 1) 1) 是自尾调用，用 recur；第三个分支外层调用也在尾位置，用 recur
(defn ack [m n]
  (cond (= m 0) (+ n 1)
        (= n 0) (recur (- m 1) 1)
        :else (recur (- m 1) (ack m (- n 1)))))

;; ack(3,12) 的非尾递归深度约 3 万层，超出 JVM 主线程默认栈（StackOverflowError）；Scheme 实现没有这个限制。
;; JVM 上把整个基准放到一个 512MB 栈的线程里跑（不影响计时内容）；cleat 的主线程栈够用，直接跑。
(defn- with-big-stack [f]
  #?(:clj (let [result (volatile! nil)
                t (Thread. nil (fn [] (vreset! result (f))) "ack" (* 512 1024 1024))]
            (.start t)
            (.join t)
            @result)
     :cljr (f)))

(defn -main [& _]
  (let [count (read-input) input1 (read-input) input2 (read-input) output (read-input)
        s3 (str count) s2 (str input2) s1 (str input1)
        name "ack"]
    (with-big-stack
      (fn []
        (run-r7rs-benchmark (str name ":" s1 ":" s2 ":" s3) count
                            (fn [] (ack (hide count input1) (hide count input2)))
                            (fn [result] (= result output)))))))
