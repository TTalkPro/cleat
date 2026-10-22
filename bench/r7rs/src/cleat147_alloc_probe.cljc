(ns cleat147-alloc-probe
  "cleat-147 量化探针：同 n 下测闭包三态、固定调用与 RestFn 参数数组。
  每个相位用宿主的线程分配计数包住 warm-up 后的迭代段，输出 raw、bytes/iter
  和与配对基线的差值；alloc.cs 的进程总分配只用于粗看类型，不再是这里的读数。")

;; JVM 的 HotSpot ThreadMXBean 和 .NET 的 GC.GetAllocatedBytesForCurrentThread
;; 都是“当前线程累计分配”。不支持线程口径的 JVM 不做分配归一，只报 unavailable。
(def allocation-mode
  #?(:cljr :clr-thread
     :clj (let [bean (java.lang.management.ManagementFactory/getThreadMXBean)]
            (if (.isThreadAllocatedMemorySupported bean)
              (do (.setThreadAllocatedMemoryEnabled bean true)
                  :jvm-thread)
              :unavailable))))

(defn fixed3 ^long [^long a ^long b ^long c] (unchecked-add (unchecked-add a b) c))
(defn variadic0 [& xs] (reduce + 0 xs))
(defn variadic2 [a b & xs] (+ a b (reduce + 0 xs)))

(defn thread-allocated-bytes ^long []
  #?(:cljr (System.GC/GetAllocatedBytesForCurrentThread)
     :clj (let [bean (java.lang.management.ManagementFactory/getThreadMXBean)]
            (if (.isThreadAllocatedMemoryEnabled bean)
              (.getThreadAllocatedBytes
               ^com.sun.management.ThreadMXBean bean
               (.getId (Thread/currentThread)))
              -1))))

(defn run ^long [phase n ^long acc]
  (case phase
    :none (loop [i 0 acc acc]
            (if (< i n)
              (recur (unchecked-add i 1) (unchecked-add acc 1))
              acc))
    :closure-none (loop [i 0 acc acc]
                   (if (< i n)
                     (let [f (fn [x] x)]
                       (recur (unchecked-add i 1) (unchecked-add acc (f 1))))
                     acc))
    :closure-static (loop [i 0 acc acc]
                     (if (< i n)
                       (let [base 7 f (fn [x] (unchecked-add ^long base ^long x))]
                         (recur (unchecked-add i 1) (unchecked-add acc (f 1))))
                       acc))
    :closure-mutable (loop [i 0 acc acc]
                      (if (< i n)
                        (let [v (volatile! 0) f (fn [] (vswap! v inc) @v)]
                          (recur (unchecked-add i 1) (unchecked-add acc (f))))
                        acc))
    :fixed (loop [i 0 acc acc]
            (if (< i n)
              (recur (unchecked-add i 1) (unchecked-add acc (fixed3 1 2 3)))
              acc))
    :rest0 (loop [i 0 acc acc]
            (if (< i n)
              (recur (unchecked-add i 1) (unchecked-add acc (variadic0 1 2 3)))
              acc))
    :rest2 (loop [i 0 acc acc]
            (if (< i n)
              (recur (unchecked-add i 1) (unchecked-add acc (variadic2 1 2 3 4)))
              acc))
    0))

(def phases
  [:none :closure-none :closure-static :closure-mutable :fixed :rest0 :rest2])

;; closure-none 隔掉“每循环建函数但无捕获环境”的成本；fixed 是 RestFn 的固定元数基线。
(def baselines
  {:closure-none :none
   :closure-static :closure-none
   :closure-mutable :closure-none
   :rest0 :fixed
   :rest2 :fixed})

;; warm-up 让首相位和后续相位都越过首次 JIT/编译；归一只用包在计数器里的 n 次。
(defn elapsed-ns ^long []
  #?(:cljr (long (* 1e9 (/ (double (System.Diagnostics.Stopwatch/GetTimestamp))
                           (double (System.Diagnostics.Stopwatch/Frequency)))))
     :clj (System/nanoTime)))

(defn measure [phase n]
  (let [sink (long-array 1)
        warmup (min 10000 n)]
    (run phase warmup 0)
    (let [start (thread-allocated-bytes)
          t0 (elapsed-ns)]
      (let [acc (run phase n 0)]
        (aset sink 0 acc)
        (let [end (thread-allocated-bytes)
              t1 (elapsed-ns)]
          {:phase phase :bytes (- end start) :ns (- t1 t0)
           :sink (long (aget sink 0))})))))

(defn- normalize [value n]
  (double (/ value n)))

(defn print-rows [results n]
  (println (str "metric " (case allocation-mode
                            :clr-thread "System.GC.GetAllocatedBytesForCurrentThread"
                            :jvm-thread "ThreadMXBean.getThreadAllocatedBytes"
                            "unavailable")))
  (println (str "n " n))
  (println (format "%-17s %16s %18s %17s %18s %12s %14s"
                   "phase" "raw-bytes" "bytes/iter" "baseline" "delta/iter" "ns/iter" "sink"))
  (doseq [phase phases]
    (let [{:keys [bytes ns sink]} (get results phase)
          baseline-phase (get baselines phase)
          baseline-bytes (if baseline-phase (:bytes (get results baseline-phase)) bytes)
          delta (- bytes baseline-bytes)]
      (println (format "%-17s %16s %18s %17s %18s %12s %14s"
                       (name phase)
                       (str bytes)
                       (str (format "%.3f" (normalize bytes n)))
                       (or (some-> baseline-phase name) "-")
                       (str (format "%.3f" (normalize delta n)))
                       (str (format "%.3f" (normalize ns n)))
                       (str sink))))))

(defn -main [& args]
  (let [n (or (and (first args) (parse-long (first args))) 20000000)
        results (into {} (map (fn [phase] [phase (measure phase n)])) phases)]
    (print-rows results n)))
