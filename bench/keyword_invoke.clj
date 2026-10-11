;; 关键字调用 (:k m) 的快速路径密度（ILookup.valAt 分派）。
(ns bench.keyword-invoke)

(defn -main [& _]
  (let [ks (mapv #(keyword (str "k" %)) (range 1000))
        ms (mapv (fn [i] {(nth ks i) i}) (range 1000))]
    (assert (= 0 (:k0 (first ms))))
    (dotimes [_ 5]
      (let [sw (System.Diagnostics.Stopwatch/StartNew)]
        (loop [i 0 acc 0]
          (if (< i 500000)
            (recur (inc i) (+ acc (long (or ((nth ms (mod i 1000)) (nth ks (mod i 1000)) 0)))))
            (assert (pos? acc))))
        (printf "BENCH keyword-invoke %d%n" (.ElapsedMilliseconds sw))
        (flush)))))
