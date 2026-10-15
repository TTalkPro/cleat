;;; STRING -- One of the Kernighan and Van Wyk benchmarks.
(ns r7rs.string
  (:require [r7rs.common :refer [hide read-input run-r7rs-benchmark]]))

;; 原版的全局变量 s（set! 改写）
(def s (volatile! "abcdef"))

(defn grow []
  (vreset! s (str "123" @s "456" @s "789"))
  (vreset! s (let [s @s]
               (str (subs s (quot (count s) 2) (count s))
                    (subs s 0 (+ 1 (quot (count s) 2))))))
  @s)

(defn trial [n]
  (loop [i 0]
    (if (> (count @s) n)
      (count @s)
      (do (grow) (recur (+ i 1))))))

(defn my-try [n]
  (loop [i 0]
    (if (>= i 10)
      (count @s)
      (do (vreset! s "abcdef")
          (trial n)
          (recur (+ i 1))))))

(defn -main [& _]
  (let [count (read-input) input1 (read-input) output (read-input)
        s2 (str count) s1 (str input1) name "string"]
    (run-r7rs-benchmark (str name ":" s1 ":" s2) count
                        (fn [] (my-try (hide count input1)))
                        (fn [result] (= result output)))))
