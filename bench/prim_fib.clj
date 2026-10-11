;; cleat-042 基准：带原始类型提示的递归（^long 形参与返回）——原始签名 / 跨调用不装箱的效果。
(ns bench.prim-fib)

(defn fib ^long [^long n]
  (if (< n 2)
    n
    (+ (fib (- n 1)) (fib (- n 2)))))

(defn -main [& _]
  (assert (= (fib 25) 75025))
  (dotimes [_ 5]
    (let [sw (System.Diagnostics.Stopwatch/StartNew)]
      (fib 27)
      (printf "BENCH prim_fib %d%n" (.ElapsedMilliseconds sw))
      (flush))))
