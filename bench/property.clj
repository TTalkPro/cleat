;; 属性读取（cleat-100 / designs/20 §2）：(.Length ^String s) 与 (.Count ^clojure.lang.IPersistentVector v) 现在经 Reflector。
(ns bench.property)

(defn run [n ^String s v]
  (loop [i 0 acc 0]
    (if (< i n)
      (recur (inc i) (+ acc (.Length s) (.Length ^String (.ToString ^Object v))))
      acc)))

(defn -main [& _]
  (assert (= (run 1 "abc" 12) 5))
  (dotimes [_ 5]
    (let [sw (System.Diagnostics.Stopwatch/StartNew)]
      (run 1000000 "abc" 12)
      (printf "BENCH property %d%n" (.ElapsedMilliseconds sw))
      (flush))))
