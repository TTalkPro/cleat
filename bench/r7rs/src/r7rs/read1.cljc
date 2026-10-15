;;; READ1 -- Test of R6RS get-datum, comparable to the parsing benchmark.
;;; 计时部分：用宿主的 read（Clojure 读取器）从文件逐个读出数据，返回最后一个。
;;; 输入 inputs/parsing.data 是 Scheme 源码，Clojure 读取器读不了其中的 #t / #f（会被当成带标签字面量）；
;;; 计时之前把它转成 outputs/read1.data（#t → true，#f → false，其余原样），计时部分读这份拷贝。
(ns r7rs.read1
  (:require [r7rs.common :refer [hide read-input run-r7rs-benchmark]]
            [r7rs.util :refer [string-builder sb-append! sb-append-char!]]))

(defn- delimiter? [c]
  (or (nil? c) (= c \space) (= c \newline) (= c \tab) (= c \return) (= c \() (= c \))))

;; #t / #f → true / false（不计时）
(defn- clojure-readable-copy [input output]
  (let [text (slurp input)
        n (count text)
        sb (string-builder)]
    (loop [i 0]
      (when (< i n)
        (let [c (nth text i)]
          (if (and (= c \#) (< (+ i 1) n)
                   (#{\t \f} (nth text (+ i 1)))
                   (delimiter? (when (< (+ i 2) n) (nth text (+ i 2)))))
            (do (sb-append! sb (if (= (nth text (+ i 1)) \t) "true" "false"))
                (recur (+ i 2)))
            (do (sb-append-char! sb c)
                (recur (+ i 1)))))))
    (spit output (str sb))
    output))

(defn- open-input-file [path]
  #?(:clj (java.io.PushbackReader. (java.io.BufferedReader. (java.io.FileReader. ^String path)))
     :cljr (clojure.lang.PushbackTextReader. (System.IO.StreamReader. ^String path))))
(defn- close-input-port [in]
  #?(:clj (.close ^java.io.Reader in) :cljr (.Close ^System.IO.TextReader in)))

(def ^:private eof (Object.))

(defn read-from-file-benchmark [input]
  (let [in (open-input-file input)]
    (try
      (loop [x (read {:eof eof} in)
             y false
             i 0]
        (if (identical? x eof)
          y
          (recur (read {:eof eof} in) x (+ i 1))))
      (finally (close-input-port in)))))

(defn -main [& _]
  (let [count (read-input) input1 (read-input) output (read-input)
        s2 (str count) s1 input1 name "read1"
        input1 (clojure-readable-copy input1 "outputs/read1.data")]
    (run-r7rs-benchmark (str name ":" s2) count
                        (fn [] (read-from-file-benchmark (hide count input1)))
                        (fn [result] (= result output)))))
