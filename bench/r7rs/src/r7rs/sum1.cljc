;;; SUM1 -- One of the Kernighan and Van Wyk benchmarks.
(ns r7rs.sum1
  (:require [r7rs.common :refer [hide read-input run-r7rs-benchmark]]))

;; 原版的 call-with-input-file + (read port)：打开 Clojure 读取器，逐个 read 到文件末尾，最后关闭
(defn- open-input-file [path]
  #?(:clj (java.io.PushbackReader. (java.io.FileReader. (str path)))
     :cljr (clojure.lang.PushbackTextReader. (System.IO.StreamReader. (str path)))))

(defn- close-port [port]
  #?(:clj (.close ^java.io.PushbackReader port)
     :cljr (.Close ^clojure.lang.PushbackTextReader port)))

(defn- call-with-input-file [path proc]
  (let [port (open-input-file path)]
    (try (proc port) (finally (close-port port)))))

(def ^:private eof (Object.))

(defn sumport [port sum-so-far]
  (let [x (read {:eof eof} port)]
    (if (identical? x eof)
      sum-so-far
      (recur port (+ x sum-so-far)))))

(defn sum [port]
  (sumport port 0.0))

(defn go [input]
  (call-with-input-file input sum))

(defn -main [& _]
  (let [count (read-input) input1 (read-input) output (read-input)
        s2 (str count) s1 input1
        name "sum1"]
    (run-r7rs-benchmark (str name ":" s2) count
                        (fn [] (go (hide count input1)))
                        (fn [result] (<= (abs (- result output)) 1e-9)))))
