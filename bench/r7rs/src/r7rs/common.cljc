;; r7rs-benchmarks 的 Clojure 移植共用部分（cleat-107）：对应原版 common.scm 的 hide 与 run-r7rs-benchmark。
;; 同一份源码在 cleat（:cljr）与 JVM Clojure（:clj）上运行；只计程序内部的执行时间（不含启动与编译），输出与原版相同的 +!CSVLINE!+ 行。
(ns r7rs.common)

(def impl-name #?(:clj "clojure" :cljr "cleat"))

(defn current-seconds
  "单调时钟（秒，double）。"
  []
  #?(:clj (/ (double (System/nanoTime)) 1e9)
     :cljr (/ (double (System.Diagnostics.Stopwatch/GetTimestamp)) (double System.Diagnostics.Stopwatch/Frequency))))

(defn hide
  "原版的 hide：返回 x，但让编译器看不出来（经向量里的函数间接返回）。"
  [r x]
  ((nth [identity (fn [y] y)] (if (< r 100) 0 1)) x))

(defn read-input
  "从标准输入读下一个数据（原版的 (read)）。"
  []
  (read))

(defn run-r7rs-benchmark
  "原版 run-r7rs-benchmark：thunk 跑 count 次，最后一次的结果交给 ok?；打印 Elapsed time 与 +!CSVLINE!+impl,name,secs（或 INCORRECT）。"
  [name count thunk ok?]
  (println (str "Running " name))
  (flush)
  (let [t0 (current-seconds)]
    (loop [i 0 result nil]
      (if (< i count)
        (recur (inc i) (thunk))
        (if (ok? result)
          (let [secs (- (current-seconds) t0)]
            (println (str "Elapsed time: " secs " seconds for " name))
            (println (str "+!CSVLINE!+" impl-name "," name "," secs)))
          (do (println (str "ERROR: returned incorrect result: " (pr-str result)))
              (println (str "+!CSVLINE!+" impl-name "," name ",INCORRECT"))))))
    (flush)
    0))
