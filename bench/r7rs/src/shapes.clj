(ns shapes)
(def ^:const n 10000000)
(defn t [name f]
  (let [sw (System.Diagnostics.Stopwatch/StartNew)] (f)
    (printf "SHAPE %-20s %7.1f ns/次%n" name (/ (* 1e6 (.TotalMilliseconds (.Elapsed sw))) n)) (flush)))
(defn three [a b c] (+ a b c))
(defn zero [] 42)
(defn -main [& _]
  (let [xs (list 1 2 3) s "abc" kw :abc
        g (volatile! 7)]
    (t "int-lit-eq" #(dotimes [_ n] (= 1 1)))
    (t "long-var-eq" #(dotimes [_ n] (= g 7)))
    (t "count-list" #(dotimes [_ n] (count xs)))
    (t "first-list" #(dotimes [_ n] (first xs)))
    (t "nth-list-1" #(dotimes [_ n] (nth xs 1)))
    (t "keyword-eq" #(dotimes [_ n] (= kw :abc)))
    (t "gensym-keyword" #(dotimes [_ n] (keyword (str @g))))
    (t "call-arity3" #(dotimes [_ n] (three 1 2 3)))
    (t "call-arity0" #(dotimes [_ n] (zero)))
    (t "seq-string-eq" #(dotimes [_ n] (seq s)))
    (flush)))
