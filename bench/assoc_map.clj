;; 哈希 map 的 assoc 吞吐：键在 1000 个关键字里循环（树路径复用，分支 churn）。
(ns bench.assoc-map)

(defn -main [& _]
  (let [ks (mapv #(keyword (str "k" %)) (range 1000))]
    (dotimes [_ 3]
      (let [sw (System.Diagnostics.Stopwatch/StartNew)]
        (loop [i 0 m {}]
          (if (< i 200000)
            (recur (inc i) (assoc m (nth ks (mod i 1000)) i))
            (assert (= (count m) 1000))))
        (printf "BENCH assoc-map %d%n" (.ElapsedMilliseconds sw))
        (flush)))))
