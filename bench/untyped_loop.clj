;; 初值来自无提示参数的循环（cleat-100 / designs/20 §4）：编译期不知道 s 是 long 还是 double，每轮装箱。
(ns bench.untyped-loop)

(defn sum-long [n x] (loop [i 0 s x] (if (< i n) (recur (inc i) (+ s i)) s)))
(defn sum-double [n x] (loop [i 0 s x] (if (< i n) (recur (inc i) (+ s 1.5)) s)))

(defn -main [& _]
  (assert (= (sum-long 10 0) 45))
  (assert (= (sum-double 10 0.0) 15.0))
  (dotimes [_ 5]
    (let [sw (System.Diagnostics.Stopwatch/StartNew)]
      (sum-long 5000000 0)
      (sum-double 5000000 0.0)
      (printf "BENCH untyped_loop %d%n" (.ElapsedMilliseconds sw))
      (flush))))
