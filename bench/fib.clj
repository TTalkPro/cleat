;; 朴素递归 fib：函数调用 + 算术 + 分支的密度基准（JVM/CLR 上常用于比较调用开销）。
(ns bench.fib)

(defn fib [n] (if (< n 2) n (+ (fib (- n 1)) (fib (- n 2)))))

(defn -main [& _]
  (assert (= (fib 25) 75025))
  (dotimes [_ 5]
    (let [sw (System.Diagnostics.Stopwatch/StartNew)]
      (fib 25)
      (printf "BENCH fib25 %d%n" (.ElapsedMilliseconds sw))
      (flush))))
