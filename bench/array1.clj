;; ARRAY1（Kernighan & Van Wyk；r7rs-benchmarks 的 array1，skeg iron-079 量化过它）：建 0..n-1 的数组，再倒序复制一份。
;; 两种写法：long-array（原始元素）与 object-array（等同 Scheme 的 make-vector，元素装箱）；都带数组提示。
(ns bench.array1)

(defn create-x-long [n] (let [^longs r (long-array n)] (loop [i 0] (if (< i n) (do (aset r i i) (recur (inc i))) r))))
(defn create-y-long [^longs x]
  (let [n (alength x) ^longs r (long-array n)]
    (loop [i (dec n)] (if (neg? i) r (do (aset r i (aget x i)) (recur (dec i)))))))

(defn create-x-obj [n] (let [^objects r (object-array n)] (loop [i 0] (if (< i n) (do (aset r i i) (recur (inc i))) r))))
(defn create-y-obj [^objects x]
  (let [n (alength x) ^objects r (object-array n)]
    (loop [i (dec n)] (if (neg? i) r (do (aset r i (aget x i)) (recur (dec i)))))))

(defn run [m n]
  (dotimes [_ m] (create-y-long (create-x-long n)))
  (dotimes [_ m] (create-y-obj (create-x-obj n))))

(defn -main [& _]
  (assert (= 7 (aget ^longs (create-y-long (create-x-long 10)) 7)))
  (assert (= 7 (aget ^objects (create-y-obj (create-x-obj 10)) 7)))
  (dotimes [_ 5]
    (let [sw (System.Diagnostics.Stopwatch/StartNew)]
      (run 10 1000000)
      (printf "BENCH array1 %d%n" (.ElapsedMilliseconds sw))
      (flush))))
