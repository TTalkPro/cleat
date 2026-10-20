(ns allocshape
  "分配形状对照（只跑 cleat）：同一个 2 字段可变序对的几种构造/读取形状，ns/次。"
  (:require [r7rs.util :refer [mcons mcar mcdr set-mcar!]])
  (:import (r7rs.util MPair)))

(def ^:const n 10000000)

(defn start [] (System.Diagnostics.Stopwatch/StartNew))
(defn elapsed-ns [s] (* 1e6 (.TotalMilliseconds (.Elapsed s))))
(defn report [name t0] (printf "SHAPE %-22s %6.1f ns%n" name (/ (elapsed-ns t0) n)) (flush))

(defn -main [& _]
  (let [p (mcons 1 2)
        arr (object-array 65536)]
    ;; 1) defn 包装（mcons）
    (let [t0 (start)]
      (loop [i 0] (if (< i n) (do (aset arr (bit-and i 65535) (mcons i 2)) (recur (inc i)))))
      (report "mcons + array-store" t0))
    ;; 2) 直接构造（deftype 的 . ctor）
    (let [t0 (start)]
      (loop [i 0] (if (< i n) (do (aset arr (bit-and i 65535) (MPair. i 2)) (recur (inc i)))))
      (report "MPair. + array-store" t0))
    ;; 3) 装箱 long 写入数组（无对象壳）
    (let [t0 (start)]
      (loop [i 0] (if (< i n) (do (aset arr (bit-and i 65535) (num i)) (recur (inc i)))))
      (report "box + array-store" t0))
    ;; 4) object-array 分配（2 元素）
    (let [t0 (start)]
      (loop [i 0] (if (< i n) (do (aset arr (bit-and i 65535) (object-array 2)) (recur (inc i)))))
      (report "object-array[2] + store" t0))
    ;; 5) 只读接口方法（缓存的 p，不逃逸）
    (let [t0 (start)]
      (loop [i 0 acc 0] (if (< i n) (recur (inc i) (unchecked-add-int acc (mcar p))) acc))
      (report "mcar read" t0))
    ;; 6) 写 + 读
    (let [t0 (start)]
      (loop [i 0 acc 0] (if (< i n) (do (set-mcar! p i) (recur (inc i) (unchecked-add-int acc (mcar p)))) acc))
      (report "set-mcar! + mcar" t0))))
