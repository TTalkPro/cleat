;; cleat-042 基准集：算术 / 集合密集的合成用例。每个基准打印若干次 "BENCH <名字> <毫秒>"，
;; 由 tools/build.cs bench 聚合（最小 / 中位），--write 记录基线到 bench/baseline.edn。
(ns bench.sum-loop)

(defn run []
  (loop [i 0 acc 0]
    (if (< i 5000000)
      (recur (inc i) (+ acc i))
      acc)))

(defn -main [& _]
  (assert (= (run) 12499997500000))
  (dotimes [_ 5]
    (let [sw (System.Diagnostics.Stopwatch/StartNew)]
      (run)
      (printf "BENCH sum_loop %d%n" (.ElapsedMilliseconds sw))
      (flush))))
