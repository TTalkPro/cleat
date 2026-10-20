(ns shapes3
  "分派成本 vs 本体成本：同一 ns 内的直调 / defn 包装 / 通用 RT 调用对照。"
  (:require [r7rs.util :refer [mcar mcons]]))

(definterface LP (getCar []) (getCdr []))
(deftype Pair [^:unsynchronized-mutable a ^:unsynchronized-mutable d]
  LP
  (getCar [_] a)
  (getCdr [_] d))

(defn wrap-car [p] (.getCar ^LP p))
(defn wrap-new [x y] (Pair. x y))

(def ^:const n 10000000)

#?(:clj  (defn start [] (System/nanoTime))
   :cljr (defn start [] (System.Diagnostics.Stopwatch/StartNew)))
#?(:clj  (defn elapsed-ns [s] (double (- (System/nanoTime) s)))
   :cljr (defn elapsed-ns [s] (* 1e6 (.TotalMilliseconds (.Elapsed s)))))
(defn report [name t0] (printf "DISP %-22s %7.1f ns%n" name (/ (elapsed-ns t0) n)) (flush))

(defn -main [& _]
  (let [p (Pair. 1 2) xs (list 1 2 3) s "abc" q (mcar (mcons 5 6))]
    (let [t0 (start)] (dotimes [_ n] (.getCar ^LP p)) (report "iface-direct" t0))
    (let [t0 (start)] (dotimes [_ n] (wrap-car p)) (report "iface-via-defn" t0))
    (let [t0 (start)] (dotimes [_ n] (Pair. 1 2)) (report "deftype-new" t0))
    (let [t0 (start)] (dotimes [_ n] (wrap-new 1 2)) (report "deftype-new-via-defn" t0))
    (let [t0 (start)] (dotimes [_ n] (nth xs 1)) (report "nth-list" t0))
    (let [t0 (start)] (dotimes [_ n] (. clojure.lang.RT (nth xs 1))) (report "nth-list-RT-direct" t0))
    (let [t0 (start)] (dotimes [_ n] (first xs)) (report "first-list" t0))
    (let [t0 (start)] (dotimes [_ n] (. clojure.lang.RT (first xs))) (report "first-list-RT-direct" t0))
    (let [t0 (start)] (dotimes [_ n] (seq s)) (report "seq-string" t0))
    (let [t0 (start)] (dotimes [_ n] (. clojure.lang.RT (seq s))) (report "seq-string-RT-direct" t0))
    (let [t0 (start)] (dotimes [_ n] (first s)) (report "first-string" t0))
    (let [t0 (start)] (dotimes [_ n] q) (report "noop-local" t0))))
