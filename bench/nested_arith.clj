;; 无提示参数上的嵌套算术（cleat-100 / designs/20 §3）：每轮三次装箱与 Numbers 分派，Num 与双局部变量的目标。
(ns bench.nested-arith)

(defn poly [n a b] (loop [i 0 s 0.0] (if (< i n) (recur (inc i) (+ s (* (- a b) (+ a b)))) s)))

(defn -main [& _]
  (assert (= (poly 1 1.5 0.5) 2.0))
  (dotimes [_ 5]
    (let [sw (System.Diagnostics.Stopwatch/StartNew)]
      (poly 5000000 1.5 0.5)
      (printf "BENCH nested_arith %d%n" (.ElapsedMilliseconds sw))
      (flush))))
