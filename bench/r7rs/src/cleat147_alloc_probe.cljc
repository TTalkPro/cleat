(ns cleat147-alloc-probe
  "cleat-147 量化探针：按 phase 分开测闭包三态、固定调用与 RestFn 超出固定元数的数组。")

(defn fixed3 [a b c] (unchecked-add (unchecked-add ^long a ^long b) ^long c))
(defn variadic0 [& xs] (reduce + 0 xs))
(defn variadic2 [a b & xs] (+ a b (reduce + 0 xs)))

(defn run [phase n sink]
  (case phase
    :none (dotimes [_ n] (aset sink 0 (unchecked-add ^long (aget sink 0) 1)))
    :closure-none (dotimes [_ n] (let [f (fn [x] x)] (aset sink 0 (unchecked-add ^long (aget sink 0) (f 1)))))
    :closure-static (dotimes [_ n] (let [base 7 f (fn [x] (unchecked-add ^long base ^long x))] (aset sink 0 (unchecked-add ^long (aget sink 0) (f 1)))))
    :closure-mutable (dotimes [_ n] (let [v (volatile! 0) f (fn [] (vswap! v inc) @v)] (aset sink 0 (unchecked-add ^long (aget sink 0) (f)))))
    :fixed (dotimes [_ n] (aset sink 0 (unchecked-add ^long (aget sink 0) (fixed3 1 2 3))))
    :rest0 (dotimes [_ n] (aset sink 0 (unchecked-add ^long (aget sink 0) (variadic0 1 2 3))))
    :rest2 (dotimes [_ n] (aset sink 0 (unchecked-add ^long (aget sink 0) (variadic2 1 2 3 4))))
    nil))

(defn -main [& args]
  (let [phase (keyword (or (first args) "none"))
        n (if (= phase :none) 50000000 50000000)
        sink (long-array 1)]
    (run phase n sink)
    (println (str phase " " (aget sink 0)))))
