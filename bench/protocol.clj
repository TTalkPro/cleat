;; 协议分派（defprotocol + defrecord 的实现）：方法查找 / 分派函数的调用密度。
(ns bench.protocol)

(defprotocol Area
  (area [this]))

(defrecord Rect [w h])
(defrecord Circle [r])

(extend-protocol Area
  Rect (area [this] (* (:w this) (:h this)))
  Circle (area [this] (* 3 (:r this) (:r this))))

(defn -main [& _]
  (let [rs (mapv (fn [i] (->Rect i (inc i))) (range 1000))
        cs (mapv (fn [i] (->Circle i)) (range 1000))]
    (assert (number? (area (first rs))))
    (dotimes [_ 5]
      (let [sw (System.Diagnostics.Stopwatch/StartNew)]
        (loop [i 0 acc 0]
          (if (< i 200000)
            (recur (inc i) (+ acc (area (nth rs (mod i 1000))) (area (nth cs (mod i 1000)))))
            (assert (pos? acc))))
        (printf "BENCH protocol %d%n" (.ElapsedMilliseconds sw))
        (flush)))))
