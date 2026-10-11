;; 惰性序列链：map + filter 的分块序列（chunked seq）吞吐。
(ns bench.lazy-chain)

(defn -main [& _]
  (dotimes [_ 5]
    (let [sw (System.Diagnostics.Stopwatch/StartNew)]
      (assert (= 150000 (count (into [] (map inc (filter odd? (range 300000)))))))
      (printf "BENCH lazy-chain %d%n" (.ElapsedMilliseconds sw))
      (flush))))
