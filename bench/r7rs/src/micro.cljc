(ns micro
  "调用 / 分发 / 分配路径的微基准（cleat-141 差异归因）。同一份 cljc 跑两个宿主，报 ns/次。"
  (:require [r7rs.util :refer [mcons mcar mcdr set-mcar!]]))

(def ^:const n 20000000)

#?(:clj  (defn start [] (System/nanoTime))
   :cljr (defn start [] (System.Diagnostics.Stopwatch/StartNew)))
#?(:clj  (defn elapsed-ns [s] (double (- (System/nanoTime) s)))
   :cljr (defn elapsed-ns [s] (* 1e6 (.TotalMilliseconds (.Elapsed s)))))

(defn add2 [x y] (+ x y))

(defn report [name t0]
  (printf "MICRO %-18s %6.1f ns%n" name (/ (elapsed-ns t0) n))
  (flush))

(defn -main [& _]
  (let [p (mcons 1 2)]
    ;; 1) 可变序对读（defn 包装 + 接口调用）
    (let [t0 (start)]
      (loop [i 0 acc 0] (if (< i n) (recur (inc i) (unchecked-add-int acc (mcar p))) acc))
      (report "mcar-read" t0))
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
    ;; 8) 分配并逃逸到数组（真正的堆分配成本：EA 无法消除）
    (let [arr (object-array 65536) t0 (start)]
      (loop [i 0] (if (< i n) (do (aset arr (bit-and i 65535) (mcons i 2)) (recur (inc i)))))
      (report "mcons-escape" t0))
    ;; 9) 装箱整数写进对象数组（clojure 数据的常见形态）
    (let [arr (object-array 65536) t0 (start)]
      (loop [i 0] (if (< i n) (do (aset arr (bit-and i 65535) (num i)) (recur (inc i)))))
      (report "box-store" t0))))
