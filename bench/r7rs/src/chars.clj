(ns chars)
(def ^:const n 10000000)
(defn t [name f]
  (let [sw (System.Diagnostics.Stopwatch/StartNew)]
    (f)
    (printf "CHARS %-14s %8.1f ns/次%n" name (/ (* 1e6 (.TotalMilliseconds (.Elapsed sw))) n))))
(defn -main [& _]
  (let [s "abc"]
    (t "lit-eq" #(dotimes [_ n] (= \a \a)))
    (t "nth" #(dotimes [_ n] (nth s 0)))
    (t "nth-eq-lit" #(dotimes [_ n] (= (nth s 0) \a)))
    (t "first-seq" #(dotimes [_ n] (first "abc")))
    (flush)))
