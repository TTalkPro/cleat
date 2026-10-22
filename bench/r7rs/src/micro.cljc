;; 调用 / 分发 / 分配路径的微基准（cleat-141 差异归因）。同一份 cljc 跑两个宿主，报 ns/次。
;;
;; 口径（cleat-149）：**结果必须参与数据依赖，否则 C2 / Graal 会用 LICM + 标量替换把整个调用提出循环，
;; JVM 侧的数字不是操作的成本**（见 r7rs/probe.cljc 与 designs/28 §1）。本文件的每一项都把结果累加进 acc
;; （累加是数据依赖），所以**死值消除**不会发生；仍存在的两件事由下面的自检说出口：
;;  1. **循环不变实参的调用会被提出循环**：`(mcar p)` 里 p 不变 → 提出后每轮只剩 acc 相加。
;;     因此每个读探针同时跑一份 *-opaque 变体（实参经不透明数组按循环变量取，提出不了），
;;     raw ≤ 环底（F-load：同结构的"不透明读 + 累加"骨架）时标 `HOISTED?`。
;;  2. **不逃逸的分配会被标量替换整个消掉**（EA）：这是宿主语义差别不是探针伪影，数字照用，
;;     但不能写成".NET 分配慢 9 ~ 12 倍"（designs/28 §1.2）。
;; 跑法：dotnet <仓库>/src/Cleat.Host/bin/Release/net10.0/cleat.dll -I bench/r7rs/src -m micro
;;       java -Xss8m -cp bench/r7rs/src:$(cd bench/r7rs && clojure -Spath) clojure.main -m micro
(ns micro
  (:require [r7rs.util :refer [mcons mcar mcdr set-mcar!]]
            [r7rs.probe :refer [opaque-long opaque-obj]]))

(def ^:const n 20000000)

;; cleat-153 的调用点形状：同一 deftype 上的读访问器。单态格用 r7rs.util 的 MPair/IMPair：
;; 145 的访问器内联只在包装函数跨 ns 被内联时生效（同 ns 的小 defn 不走 .inl）。另一个实现
;; 只在最后一格的单调用点双态交替里出现。
(deftype CallsiteAltPair [v]
  r7rs.util.IMPair
  (getCar [_] v))

#?(:clj  (defn start [] (System/nanoTime))
   :cljr (defn start [] (System.Diagnostics.Stopwatch/StartNew)))
#?(:clj  (defn elapsed-ns [s] (double (- (System/nanoTime) s)))
   :cljr (defn elapsed-ns [s] (* 1e6 (.TotalMilliseconds (.Elapsed s)))))

(defn add2 [x y] (+ x y))

(def ^:private floors (double-array 2))                ; [F-empty 纯循环+累加, F-load 不透明读+累加]

(defn- measure-floors! []
  (let [t0 (start)]
    (loop [i 0 acc 0] (if (< i n) (recur (inc i) (unchecked-add-int acc 1)) acc))
    (aset ^doubles floors 0 (/ (elapsed-ns t0) n)))
  (let [t0 (start)]
    (loop [i 0 acc 0]
      (if (< i n) (recur (inc i) (unchecked-add-int acc (int (opaque-long i)))) acc))
    (aset ^doubles floors 1 (/ (elapsed-ns t0) n)))
  (printf "MICRO-FLOOR %-18s %6.2f / %6.2f ns%n" "empty/load" (aget ^doubles floors 0) (aget ^doubles floors 1))
  (flush))

(defn- line [name t0 dead]
  "自检：F-empty（纯循环 + 累加）是每一项的下界，raw 落在它以内说明被测操作在这一侧根本不存在
   （被提出循环或被折叠）；F-load（同结构再叠一次不透明读）是「带实参读取」形状的下界，只作参考。
   DEAD-VALUE 是静态的一档：结果丢弃的形状按定义不可比（JVM 侧会被 LICM / EA 消掉）。"
  (let [raw (/ (elapsed-ns t0) n)
        fe (aget ^doubles floors 0)
        fl (aget ^doubles floors 1)
        vis (> raw (+ fe 0.1))
        tag (cond (and dead (not vis)) "DEAD-VALUE+HOISTED?"
                  (not vis)            "SUSPECT(<= 纯循环环底：操作看不见)"
                  dead                 "DEAD-VALUE(不可比)"
                  :else                "")]
    (printf "MICRO %-18s %6.1f ns  net-empty=%6.1f net-load=%6.1f %s%n"
            name raw (- raw fe) (- raw fl) tag)
    (flush)))
(defn report [name t0] (line name t0 false))
(defn report-opaque [name t0] (line name t0 false))   ; *-opaque：实参按循环变量取，提出不了
(defn report-dead [name t0] (line name t0 true))

(defn -main [& _]
  (let [p (mcons 1 2)
        ps (let [a (object-array 8)] (dotimes [k 8] (aset ^objects a k p)) a)
        cp (mcons 1 2)
        cps (let [a (object-array 8)] (dotimes [k 8] (aset ^objects a k cp)) a)
        alt-cps (let [a (object-array 8)]
                  (dotimes [k 8] (aset ^objects a k (if (odd? k) (CallsiteAltPair. 2) (mcons 1 2))))
                  a)]
    (measure-floors!)
    ;; 1) 可变序对读（defn 包装 + 接口调用）
    (let [t0 (start)]
      (loop [i 0 acc 0] (if (< i n) (recur (inc i) (unchecked-add-int acc (mcar p))) acc))
      (report "mcar-read" t0))
    ;; 1b) 同一操作，实参按循环变量取（不透明）：与 1) 的差 = 被提出循环的量
    (let [t0 (start)]
      (loop [i 0 acc 0]
        (if (< i n) (recur (inc i) (unchecked-add-int acc (mcar (aget ^objects ps (bit-and i 7))))) acc))
      (report-opaque "mcar-read-opaque" t0))
    ;; 2) 可变字段的写 + 读（原版 set-car! / set-cdr! 的密集用法）
    (let [t0 (start)]
      (loop [i 0 acc 0]
        (if (< i n) (do (set-mcar! p i) (recur (inc i) (unchecked-add-int acc (mcar p)))) acc))
      (report "set+get-mutable" t0))
    ;; 3) 两参普通函数调用（未提示的装箱算术）
    (let [t0 (start)]
      (loop [i 0 acc 0] (if (< i n) (recur (inc i) (add2 acc 1)) acc))
      (report "add2-call" t0))
    ;; 4) 每轮分配可变序对并读取（分配 + 逃逸）
    (let [t0 (start)]
      (loop [i 0 acc 0]
        (if (< i n) (let [q (mcons i 2)] (recur (inc i) (unchecked-add-int acc (mcar q)))) acc))
      (report "mcons-alloc" t0))
    ;; 5) 每轮分配不可变 cons 并 first
    (let [t0 (start)]
      (loop [i 0 acc 0]
        (if (< i n) (let [q (cons i '(2))] (recur (inc i) (unchecked-add-int acc (first q)))) acc))
      (report "cons-first" t0))
    ;; 6) 无提示循环里的减法（装箱整数算术）
    (let [t0 (start)]
      (loop [i 0 acc 0] (if (< i n) (recur (inc i) (- acc i)) acc))
      (report "loop-sub" t0))
    ;; 7) 只读另一个字段（mcdr）
    (let [t0 (start)]
      (loop [i 0 acc 0] (if (< i n) (recur (inc i) (unchecked-add-int acc (mcdr p))) acc))
      (report "mcdr-read" t0))
    (let [t0 (start)]
      (loop [i 0 acc 0]
        (if (< i n) (recur (inc i) (unchecked-add-int acc (mcdr (aget ^objects ps (bit-and i 7))))) acc))
      (report-opaque "mcdr-read-opaque" t0))
    ;; 7c) 对照：旧口径的死值形状（结果丢弃 + 实参不变）——自检应标 HOISTED?
    (let [t0 (start)]
      (dotimes [_ n] (mcar p))
      (report-dead "dead-mcar" t0))
    ;; 8) 分配并逃逸到数组（真正的堆分配成本：EA 无法消除）
    (let [arr (object-array 65536) t0 (start)]
      (loop [i 0] (if (< i n) (do (aset arr (bit-and i 65535) (mcons i 2)) (recur (inc i)))))
      (report "mcons-escape" t0))
    ;; 9) 装箱整数写进对象数组（clojure 数据的常见形态）
    (let [arr (object-array 65536) t0 (start)]
      (loop [i 0] (if (< i n) (do (aset arr (bit-and i 65535) (num i)) (recur (inc i)))))
      (report "box-store" t0))
    ;; 10) cleat-153：同一 deftype 的接口直调（默认 CallInstance 的收益基线）
    (let [t0 (start)]
      (loop [i 0 acc 0]
        (if (< i n)
          (recur (inc i)
                 (unchecked-add-int acc (.getCar ^r7rs.util.IMPair (aget ^objects cps (bit-and i 7)))))
          acc))
      (report "callsite-iface" t0))
    ;; 10b) 同一调用点，接收器提示为具体类型：单态直调上限（不含运行时反馈 / 守卫）
    (let [t0 (start)]
      (loop [i 0 acc 0]
        (if (< i n)
          (recur (inc i)
                 (unchecked-add-int acc (.getCar ^r7rs.util.MPair (aget ^objects cps (bit-and i 7)))))
          acc))
      (report "callsite-concrete" t0))
    ;; 10c) 经 145 的小访问器包装：只有 CLEAT_OPT_ACCINLINE=1 时才特化成守卫 + 字段直读；
    ;;      默认进程里这格退回接口调用，只证明探针 / runner 能跑，不参与收益对比。
    (let [t0 (start)]
      (loop [i 0 acc 0]
        (if (< i n)
          (recur (inc i) (unchecked-add-int acc (mcar (aget ^objects cps (bit-and i 7)))))
          acc))
      (report "callsite-accinline" t0))
    ;; 10d) 同一接口调用点按 A/B 交替：多态（这里是双态）调用点的代价
    (let [t0 (start)]
      (loop [i 0 acc 0]
        (if (< i n)
          (recur (inc i)
                 (unchecked-add-int acc (.getCar ^r7rs.util.IMPair (aget ^objects alt-cps (bit-and i 7)))))
          acc))
      (report "callsite-bimorphic" t0))))
