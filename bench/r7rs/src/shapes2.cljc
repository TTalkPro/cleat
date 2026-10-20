;; 常见热形状的跨宿主探针（cleat-141 的优化建议依据；cleat-149 起改为**带数据依赖**的口径）。
;;
;; 口径（务必读 r7rs/probe.cljc 的说明）：
;;   **探针结果必须让值参与数据依赖，否则 C2 / Graal 会用 LICM + 标量替换把整个调用提出循环，
;;   JVM 侧的数字不是操作的成本**（cleat 侧不消，于是倍数是伪影）。M15 曾据此得出"nth 慢 22x、
;;   char 常量比较慢 11.7x"，在 designs/28 §1 已被推翻。
;;   现在每个形状：实参经 opaque-obj / opaque-long 按循环变量取，结果写进 sink 并在校验和里被读回；
;;   每行报 raw / net（net = raw - 环底骨架）并做伪影自检（net 落在分辨率内标 ARTIFACT?）。
;;   dead-* 两行是**改口径前的旧写法**（结果丢弃、实参与循环变量无关），留着当自检的对照：
;;   JVM 侧它们应当被标成 ARTIFACT?，cleat 侧不会 —— 说明"两个宿主要么都按本口径跑，要么别比倍数"。
;;
;; 跑法：
;;   dotnet <仓库>/src/Cleat.Host/bin/Release/net10.0/cleat.dll -I bench/r7rs/src -m shapes2
;;   java -Xss8m -cp bench/r7rs/src:$(cd bench/r7rs && clojure -Spath) clojure.main -m shapes2
(ns shapes2
  (:require [r7rs.util :refer [mcons mcar]]
            [r7rs.probe :refer [default-n timed dead-value-control measure-floor! floor
                                opaque-obj opaque-long feed-obj! feed-long!]]))

(def ^:const n default-n)

(defn three [a b c] (+ a b c))

(defn -main [& _]
  (let [xs (list 1 2 3) s "abc" kw :abc g (volatile! 7) p (mcons 1 2)]
    (measure-floor! n)
    (feed-long! (long \a))
    (timed "eq-char"            n i (= (char (opaque-long i)) \a))
    (feed-long! 7)
    (timed "eq-int"             n i (= (opaque-long i) (opaque-long i)))
    (timed "call-arity3"        n i (three (opaque-long i) 2 3))
    (feed-obj! xs)
    (timed "first-list"         n i (first (opaque-obj i)))
    (timed "count-list"         n i (count (opaque-obj i)))
    (timed "nth-list-1"         n i (nth (opaque-obj i) 1))
    (feed-obj! kw)
    (timed "keyword-eq"         n i (= (opaque-obj i) :abc))
    (feed-obj! s)
    (timed "nth-string"         n i (nth (opaque-obj i) 0))
    (timed "first-string"       n i (first (opaque-obj i)))
    (timed "seq-string"         n i (seq (opaque-obj i)))
    (feed-long! 7)
    (timed "keyword-of-str"     n i (keyword (str (opaque-long i))))
    (feed-obj! g)
    (timed "volatile-deref"     n i (deref (opaque-obj i)))
    (timed "volatile-set"       n i (vreset! (opaque-obj i) (opaque-long i)))
    (timed "pair-new"           n i (mcons (opaque-long i) 2))
    (feed-obj! p)
    (timed "mcar-pair"          n i (mcar (opaque-obj i)))
    ;; —— 对照：旧口径（死值 + 循环不变实参），自检应在这里抓到伪影 ——
    (dead-value-control "dead-first-list" n (first xs))
    (dead-value-control "dead-nth-list"   n (nth xs 1))
    (dead-value-control "dead-eq-char"    n (= \a \a))
    (printf "NOTE 只有 net 列可用于跨宿主倍数；raw 含环底 %.2f ns，ARTIFACT? 行的 JVM 数字无效%n" (floor))))
