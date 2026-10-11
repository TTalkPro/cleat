;; 持久向量上的 reduce：序列构造 + 归约的集合密集基准。
(ns bench.reduce-vec)

(defn -main [& _]
  (let [v (vec (range 1000000))]
    (assert (= (reduce + v) 499999500000))
    (dotimes [_ 5]
      (let [sw (System.Diagnostics.Stopwatch/StartNew)]
        (reduce + v)
        (printf "BENCH reduce-vec %d%n" (.ElapsedMilliseconds sw))
        (flush)))))
