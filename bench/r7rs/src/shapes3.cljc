;; 分派成本 vs 本体成本：同一 ns 内的直调 / defn 包装 / 通用 RT 调用对照。
;;
;; 口径（cleat-149 修正，读 r7rs/probe.cljc 的说明）：
;;   **探针结果必须让值参与数据依赖，否则 C2 / Graal 会用 LICM + 标量替换把整个调用提出循环，
;;   JVM 侧的数字只是循环开销**。本文件正是这条教训的出处：旧版每一行都是 `(dotimes [_ n] (op 不变实参))`
;;   且结果丢弃，JVM 侧的 nth-list 0.7 ns / first-list 2.3 ns 都是被提出后的残值（designs/28 §1）。
;;   现在实参经 (opaque-obj i) / (opaque-long i) 按循环变量取，结果写进 sink 并读回校验和，
;;   每行报 raw / net（net = raw - 环底）并在 net 落在分辨率内时标 ARTIFACT?。
;;   dead-* 是旧写法本身，留着证明自检能抓到它。
(ns shapes3
  (:require [r7rs.util :refer [mcar mcons]]
            [r7rs.probe :refer [default-n timed dead-value-control measure-floor! floor
                                opaque-obj opaque-long feed-obj! feed-long!]]))

(definterface LP (getCar []) (getCdr []))
(deftype Pair [^:unsynchronized-mutable a ^:unsynchronized-mutable d]
  LP
  (getCar [_] a)
  (getCdr [_] d))

(defn wrap-car [p] (.getCar ^LP p))
(defn wrap-new [x y] (Pair. x y))

(def ^:const n default-n)

(defn -main [& _]
  (let [p (Pair. 1 2) xs (list 1 2 3) s "abc" q (mcar (mcons 5 6))]
    (measure-floor! n)                       ; 旧版的 "noop-local" 由环底取代
    (feed-obj! p)
    (timed "iface-direct"          n i (.getCar ^LP (opaque-obj i)))
    (timed "iface-via-defn"        n i (wrap-car (opaque-obj i)))
    (feed-long! 3)
    (timed "deftype-new"           n i (Pair. (opaque-long i) 2))
    (timed "deftype-new-via-defn"  n i (wrap-new (opaque-long i) 2))
    (feed-obj! xs)
    (timed "nth-list"              n i (nth (opaque-obj i) 1))
    (timed "nth-list-RT-direct"    n i (. clojure.lang.RT (nth (opaque-obj i) 1)))
    (timed "first-list"            n i (first (opaque-obj i)))
    (timed "first-list-RT-direct"  n i (. clojure.lang.RT (first (opaque-obj i))))
    (feed-obj! s)
    (timed "seq-string"            n i (seq (opaque-obj i)))
    (timed "seq-string-RT-direct"  n i (. clojure.lang.RT (seq (opaque-obj i))))
    (timed "first-string"          n i (first (opaque-obj i)))
    ;; —— 对照：改口径前的旧写法（死值 + 循环不变实参），自检在这里抓伪影 ——
    (dead-value-control "dead-iface-direct" n (.getCar ^LP p))
    (dead-value-control "dead-nth-list"     n (nth xs 1))
    (dead-value-control "dead-first-string" n (first s))
    (printf "NOTE net 才是每次调用的成本；raw 含环底 %.2f ns；ARTIFACT? 行的数字不可用于跨宿主倍数%n" (floor))))
