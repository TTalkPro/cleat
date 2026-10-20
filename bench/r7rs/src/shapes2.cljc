(ns shapes2
  "常见热形状的跨宿主探针（cleat-141 优化建议依据）。ns/次，各 1×10^7。"
  (:require [r7rs.util :refer [mcons mcar]]))

(def ^:const n 10000000)

#?(:clj  (defn start [] (System/nanoTime))
   :cljr (defn start [] (System.Diagnostics.Stopwatch/StartNew)))
#?(:clj  (defn elapsed-ns [s] (double (- (System/nanoTime) s)))
   :cljr (defn elapsed-ns [s] (* 1e6 (.TotalMilliseconds (.Elapsed s)))))

(defn three [a b c] (+ a b c))
(defn report [name t0]
  (printf "SHAPE %-16s %7.1f ns%n" name (/ (elapsed-ns t0) n))
  (flush))

(defn -main [& _]
  (let [xs (list 1 2 3) s "abc" kw :abc g (volatile! 7)]
    (let [t0 (start)] (dotimes [_ n] (= \a \a))        (report "lit-eq-char" t0))
    (let [t0 (start)] (dotimes [_ n] (= 1 1))          (report "lit-eq-int" t0))
    (let [t0 (start)] (dotimes [_ n] (three 1 2 3))    (report "call-arity3" t0))
    (let [t0 (start)] (dotimes [_ n] (first xs))       (report "first-list" t0))
    (let [t0 (start)] (dotimes [_ n] (count xs))       (report "count-list" t0))
    (let [t0 (start)] (dotimes [_ n] (nth xs 1))       (report "nth-list-1" t0))
    (let [t0 (start)] (dotimes [_ n] (= kw :abc))      (report "keyword-eq" t0))
    (let [t0 (start)] (dotimes [_ n] (nth s 0))        (report "nth-string" t0))
    (let [t0 (start)] (dotimes [_ n] (first s))        (report "first-string" t0))
    (let [t0 (start)] (dotimes [_ n] (seq s))          (report "seq-string" t0))
    (let [t0 (start)] (dotimes [_ n] (keyword (str 7))) (report "keyword-of-str" t0))
    (let [t0 (start)] (dotimes [_ n] @g)               (report "volatile-deref" t0))
    (let [t0 (start)] (dotimes [_ n] (vreset! g 7))    (report "volatile-set" t0))
    (let [t0 (start)] (dotimes [_ n] (mcons 1 2))      (report "pair-new" t0))
    (let [t0 (start)] (dotimes [_ n] (mcar xs))        (report "mcar-on-list" t0))))
