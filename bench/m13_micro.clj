;; cleat-130 的 M13 收尾微基准：arrayspec 覆盖无提示 long / double / object 数组循环，
;; typefeedback 在 eval 单元里测量两参候选并确认多态抑制。每个配置独立进程运行：
;;   dotnet …/cleat.dll -I bench -m bench.m13-micro arrayspec
;;   CLEAT_NO_OPT=arrayspec dotnet … -I bench -m bench.m13-micro arrayspec
;;   dotnet …/cleat.dll -I bench -m bench.m13-micro typefeedback
;;   CLEAT_NO_OPT=typefeedback dotnet … -I bench -m bench.m13-micro typefeedback
(ns bench.m13-micro
  (:require [clojure.string :as str]))

(def ^:const n 1000000)
(def ^:const outer 20)

(defn sum-long-unhinted [a limit]
  (loop [i 0 s 0]
    (if (< i limit)
      (recur (+ i 1) (+ s (aget a i)))
      s)))

(defn sum-long-hinted [^longs a limit]
  (loop [i 0 s 0]
    (if (< i limit)
      (recur (+ i 1) (+ s (aget a i)))
      s)))

(defn fill-long-unhinted [a limit]
  (loop [i 0]
    (if (< i limit)
      (do (aset a i (long i)) (recur (+ i 1)))
      (aget a (dec limit)))))

(defn fill-long-hinted [^longs a limit]
  (loop [i 0]
    (if (< i limit)
      (do (aset a i (long i)) (recur (+ i 1)))
      (aget a (dec limit)))))

(defn sum-double-unhinted [a limit]
  (loop [i 0 s 0.0]
    (if (< i limit)
      (recur (+ i 1) (+ s (aget a i)))
      s)))

(defn count-object-unhinted [a limit]
  (loop [i 0 acc 0]
    (if (< i limit)
      (let [x (aget a i)]
        (recur (+ i 1) (if (some? x) (+ acc 1) acc)))
      acc)))

(defn timed [name f]
  (let [sw (System.Diagnostics.Stopwatch/StartNew)
        result (f)
        ms (.TotalMilliseconds (.Elapsed sw))]
    (println (str "+!CSVLINE!+bench," name "," (format "%.3f" ms) "," result))
    result))

(defn warm [f & args] (apply f args))

(defn arrayspec [_]
  (let [la (long-array n)
        da (double-array n)
        oa (object-array n)]
    (dotimes [i n]
      (aset la i (long i))
      (aset da i (double i))
      (aset oa i (num i)))
    (warm sum-long-unhinted la outer)
    (warm sum-long-hinted la outer)
    (warm fill-long-unhinted la outer)
    (warm fill-long-hinted la outer)
    (warm sum-double-unhinted da outer)
    (warm count-object-unhinted oa outer)
    (timed "arrayspec/long-sum-u" #(sum-long-unhinted la n))
    (dotimes [_ (dec outer)] (sum-long-unhinted la n))
    (timed "arrayspec/long-sum-hint" #(sum-long-hinted la n))
    (dotimes [_ (dec outer)] (sum-long-hinted la n))
    (timed "arrayspec/long-fill-u" #(fill-long-unhinted la n))
    (dotimes [_ (dec outer)] (fill-long-unhinted la n))
    (timed "arrayspec/long-fill-hint" #(fill-long-hinted la n))
    (dotimes [_ (dec outer)] (fill-long-hinted la n))
    (timed "arrayspec/double-sum-u" #(sum-double-unhinted da n))
    (dotimes [_ (dec outer)] (sum-double-unhinted da n))
    (timed "arrayspec/object-count-u" #(count-object-unhinted oa n))
    (dotimes [_ (dec outer)] (count-object-unhinted oa n))))

(defn typefeedback [_]
  (eval '(do
    (defn m13-tf-two [x y] (+ x y))
    (defn m13-tf-poly [x] (if (= x 0) x 1))
    (let [sw (System.Diagnostics.Stopwatch/StartNew)]
      (dotimes [_ 50000000] (m13-tf-two 7 8))
      (println (str "+!CSVLINE!+bench,typefeedback/two,"
                    (format "%.3f" (.TotalMilliseconds (.Elapsed sw))) ",0")))
  (dotimes [_ 500] (m13-tf-poly 7))
  (dotimes [_ 501] (m13-tf-poly "x"))))
  (println (. Cleat.Core.Runtime.TypeFeedback Summary)))

(defn -main [& [scenario]]
  (case (or scenario "arrayspec")
    "arrayspec" (arrayspec nil)
    "typefeedback" (typefeedback nil)
    (binding [*out* *err*]
      (println (str "unknown scenario: " (or scenario "")))
      (System.Environment/Exit 2))))
