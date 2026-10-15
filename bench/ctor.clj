;; 对象构造（cleat-100 / designs/20 §2）：deftype、defrecord 工厂、BCL 构造器——现在都经 Reflector.New。
(ns bench.ctor)

(deftype P [a b])
(defrecord R [a b])

(defn run [n]
  (loop [i 0 x nil] (if (< i n) (recur (inc i) (P. i i)) x))
  (loop [i 0 x nil] (if (< i n) (recur (inc i) (->R i i)) x))
  (loop [i 0 x nil] (if (< i n) (recur (inc i) (System.Text.StringBuilder. 16)) x)))

(defn -main [& _]
  (assert (= (.-a ^P (P. 1 2)) 1))
  (dotimes [_ 5]
    (let [sw (System.Diagnostics.Stopwatch/StartNew)]
      (run 1000000)
      (printf "BENCH ctor %d%n" (.ElapsedMilliseconds sw))
      (flush))))
