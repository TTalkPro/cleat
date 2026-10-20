;; 微探针的口径工具（cleat-149）。同一份 cljc 跑两个宿主：
;;   dotnet <仓库>/src/Cleat.Host/bin/Release/net10.0/cleat.dll -I bench/r7rs/src -m <探针名>
;;   java -Xss8m -cp bench/r7rs/src:$(clojure -Spath) clojure.main -m <探针名>
;;
;; ── 为什么需要这个文件（shapes3 的教训，见 designs/28 §1）────────────────
;; **探针结果必须让值参与数据依赖，否则 C2 / Graal 会用 LICM + 标量替换把整个调用提出循环，
;; JVM 侧的数字就只是循环开销，不是操作的成本**（cleat 侧没有这种消除，于是"JVM 0.7 ns vs cleat 15 ns"
;; 这类倍数全是伪影）。这里给出两条口径并把它做成结构性的：
;;   1. 被测结果每轮写进 sink 数组的一个槽位（下标随循环变量变化），循环后读回 sink 算校验和；
;;      实参一律经 opaque-long / opaque-obj 从数组里按循环变量取，JIT 无法证明它是常量。
;;   2. 每个探针与"环底"（同样的取参与写回、不做被测操作）对照，报 net = raw - floor；
;;      net 落在分辨率以内（<= ARTIFACT-NS 或 <= floor 的比例）时打 `ARTIFACT?` ——
;;      这一侧看不见这次操作，数字不能用于跨宿主倍数。
;; 自检演示：dead-value-control 就是改口径前的旧写法（结果丢弃、实参与循环变量无关），
;; 在 JVM 侧会被标成 ARTIFACT?，在 cleat 侧不会（cleat 不做这个消除）——两边不可比这件事由标记说出口。
(ns r7rs.probe
  (:refer-clojure :exclude [floor]))

(def ^:const default-n 10000000)          ; 每形状迭代次数
(def ^:const slots 8)                      ; sink / 不透明实参的槽位数（2 的幂，用 bit-and 取模）
(def ^:const artifact-ns 0.3)              ; 净成本低于此值（ns/次）判为"这一侧看不见这次操作"
(def ^:const artifact-frac 0.15)           ; 或净成本低于本次 raw 的这个比例（操作只占循环的一小部分）

#?(:clj  (defn start [] (System/nanoTime))
   :cljr (defn start [] (System.Diagnostics.Stopwatch/StartNew)))
#?(:clj  (defn elapsed-ns [s] (double (- (System/nanoTime) s)))
   :cljr (defn elapsed-ns [s] (* 1e6 (.TotalMilliseconds (.Elapsed s)))))

;; —— 不透明实参与 sink ——
;; 类型提示放在符号上而不是 Var 元数据上（`def` 的 ^objects / ^longs 元数据在两个宿主都不解析类名，
;; 会退化成通用数组访问 + 装箱，探针就测不到真实成本）
(def sink (object-array slots))                ; 结果写这里，循环后读回算校验和
(def opaque-objs (object-array slots))         ; 不透明对象实参（全部装同一个值）
(def opaque-longs (long-array slots))          ; 不透明长整型实参

(defn opaque-long ^long [i] (aget ^longs opaque-longs (bit-and i (dec slots))))
(defn opaque-obj [i] (aget ^objects opaque-objs (bit-and i (dec slots))))
(defn sink! [i v] (aset ^objects sink (bit-and i (dec slots)) v))
(defn feed-obj! [v] (dotimes [i slots] (aset ^objects opaque-objs i v)))
(defn feed-long! [v] (dotimes [i slots] (aset ^longs opaque-longs i (long v))))

(def ^:private floor-ns (double-array 1))
(defn floor [] (aget ^doubles floor-ns 0))
(defn warmup [] (dotimes [_ 200000] (sink! 7 (opaque-obj 7))))

(defn measure-floor!
  "环底：只跑「按循环变量取不透明实参 + 写 sink」的骨架，即探针自身的开销与分辨率。"
  [n]
  (warmup)
  (let [t0 (start)]
    (dotimes [i n] (sink! i (opaque-obj i)))
    (aset ^doubles floor-ns 0 (/ (elapsed-ns t0) n))
    (printf "PROBE-FLOOR %-12s %7.2f ns%n" "skeleton" (floor))
    (flush)))

(defn sink-checksum []
  (areduce ^objects sink i acc 0 (+ acc (hash (aget ^objects sink i)))))

(defn report!
  "统一出图：raw = 每次迭代的总时间，net = raw - 环底（探针骨架自身的开销）。
   ARTIFACT? = 这一侧看不见被测操作（net 落在分辨率内）→ 动态发现的一档；
   DEAD-VALUE = 形状本身不参与数据依赖（结果丢弃、实参与循环变量无关）→ 静态发现的一档，按定义不可比。"
  [name raw chk dead]
  (let [net (- raw (floor))
        art (or (<= net artifact-ns) (<= net (* artifact-frac raw)))
        tag (cond (and dead art) "DEAD-VALUE+ARTIFACT?"
                  dead           "DEAD-VALUE(不可比)"
                  art            "ARTIFACT?"
                  :else          "ok")]
    (printf "PROBE %-18s raw=%7.2f net=%7.2f %s chk=%d%n" name raw net tag chk)
    (flush)))

(defmacro timed
  "跑 n 次 body 并把结果写进 sink（循环后按校验和读回）。第 3 个参数是循环变量名，
   body 的实参必须由它经 (opaque-long i) / (opaque-obj i) 取，否则会被提出循环（LICM）；
   写完后按环底报净成本并做伪影自检。"
  [name n i body]
  `(let [t0# (r7rs.probe/start)]
     (dotimes [~i ~n] (r7rs.probe/sink! ~i ~body))
     (r7rs.probe/report! ~name (/ (r7rs.probe/elapsed-ns t0#) ~n) (r7rs.probe/sink-checksum) false)))

(defmacro dead-value-control
  "改口径前的旧写法（对照用，故意保留）：结果丢弃、实参与循环变量无关。
   在 JVM 侧这个形状会被静态消掉 → 自检应当标 ARTIFACT?；探针文件用它证明自检有效。"
  [name n body]
  `(let [t0# (r7rs.probe/start)]
     (dotimes [i# ~n] (do ~body nil))
     (r7rs.probe/report! ~name (/ (r7rs.probe/elapsed-ns t0#) ~n) (r7rs.probe/sink-checksum) true)))
