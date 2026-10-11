;; cleat-042 基准集：算术 / 集合密集的合成用例。每个基准打印若干次 "BENCH <名字> <毫秒>"，
;; 由 tools/build.cs bench 聚合（最小 / 中位），--write 记录基线到 bench/baseline.edn。
(ns bench.sum-loop)

(defn -main [& _]
  (dotimes [_ 5]
    (let [sw (System.Diagnostics.Stopwatch/StartNew)]
      (loop [i 0 acc 0]
        (if (< i 5000000)
          (recur (inc i) (+ acc i))
          (assert (= acc 12499997500000))))
      (printf "BENCH sum-loop %d%n" (.ElapsedMilliseconds sw))
      (flush))))
