(ns cleat147-probe
  "cleat-147 本地探针：索引随循环变化、nth 结果进入 sink，避免整个调用被外提。"
  (:import [System.Diagnostics Stopwatch]))

(def ^:const n 20000000)
(def xs (list 1 2 3 4 5 6 7 8))
(def vec-xs [1 2 3 4 5 6 7 8])
(def str-xs (seq "abcdefgh"))
(def chunk-xs (map inc (range 8)))
(def lazy-xs (take 8 (iterate inc 1)))
(def arr (object-array (range 1 9)))

(defn report [name t0 acc]
  (printf "NTH %-14s %7.2f ns  sink=%d%n" name
          (/ (* (double (- (Stopwatch/GetTimestamp) t0)) 1e9)
             (* n 3.0) (double Stopwatch/Frequency))
          acc))

(defn overhead [] (loop [i 0 acc 0] (if (< i (* n 3)) (recur (inc i) (unchecked-add acc (bit-and i 7))) acc)))
(defn vector-loop [] (loop [i 0 acc 0] (if (< i (* n 3)) (recur (inc i) (unchecked-add acc (nth vec-xs (bit-and i 7)))) acc)))
(defn list-loop [] (loop [i 0 acc 0] (if (< i (* n 3)) (recur (inc i) (unchecked-add acc (nth xs (bit-and i 7)))) acc)))
(defn string-loop [] (loop [i 0 acc 0] (if (< i (* n 3)) (recur (inc i) (unchecked-add acc (long (nth "abcdefgh" (bit-and i 7))))) acc)))
(defn string-seq-loop [] (loop [i 0 acc 0] (if (< i (* n 3)) (recur (inc i) (unchecked-add acc (long (nth str-xs (bit-and i 7))))) acc)))
(defn chunked-loop [] (loop [i 0 acc 0] (if (< i (* n 3)) (recur (inc i) (unchecked-add acc (nth chunk-xs (bit-and i 7)))) acc)))
(defn lazy-loop [] (loop [i 0 acc 0] (if (< i (* n 3)) (recur (inc i) (unchecked-add acc (nth lazy-xs (bit-and i 7)))) acc)))
(defn array-loop [] (loop [i 0 acc 0] (if (< i (* n 3)) (recur (inc i) (unchecked-add acc (nth arr (bit-and i 7)))) acc)))

(defn -main [& _]
  (report "overhead" (Stopwatch/GetTimestamp) (overhead))
  (report "vector" (Stopwatch/GetTimestamp) (vector-loop))
  (report "list" (Stopwatch/GetTimestamp) (list-loop))
  (report "string" (Stopwatch/GetTimestamp) (string-loop))
  (report "string-seq" (Stopwatch/GetTimestamp) (string-seq-loop))
  (report "chunked" (Stopwatch/GetTimestamp) (chunked-loop))
  (report "lazy" (Stopwatch/GetTimestamp) (lazy-loop))
  (report "array" (Stopwatch/GetTimestamp) (array-loop)))
