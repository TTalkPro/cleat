;;; TAIL -- One of the Kernighan and Van Wyk benchmarks.
;;; Modified for R7RS by Will Clinger.
;;; 每次迭代先逐行读完整个输入（cons 成表），再按相反顺序写出。
(ns r7rs.tail
  (:require [r7rs.common :refer [hide read-input run-r7rs-benchmark]]))

;; 端口与文件操作（JVM：java.io；CLR：System.IO）
(defn- open-input-file [p]
  #?(:clj (java.io.BufferedReader. (java.io.FileReader. ^String p)) :cljr (System.IO.StreamReader. ^String p)))
(defn- read-line* [in]
  #?(:clj (.readLine ^java.io.BufferedReader in) :cljr (.ReadLine ^System.IO.TextReader in)))
(defn- close-input-port [in]
  #?(:clj (.close ^java.io.Reader in) :cljr (.Close ^System.IO.TextReader in)))
(defn- file-exists? [p]
  #?(:clj (.exists (java.io.File. ^String p)) :cljr (System.IO.File/Exists ^String p)))
(defn- delete-file [p]
  #?(:clj (.delete (java.io.File. ^String p)) :cljr (System.IO.File/Delete ^String p)))
(defn- open-output-file [p]
  #?(:clj (java.io.BufferedWriter. (java.io.FileWriter. ^String p)) :cljr (System.IO.StreamWriter. ^String p)))
(defn- write-string [s out]
  #?(:clj (.write ^java.io.Writer out ^String s) :cljr (.Write ^System.IO.TextWriter out ^String s)))
(defn- newline* [out]
  #?(:clj (.write ^java.io.Writer out (int \newline)) :cljr (.Write ^System.IO.TextWriter out \newline)))
(defn- close-output-port [out]
  #?(:clj (.close ^java.io.Writer out) :cljr (.Close ^System.IO.TextWriter out)))

(defn tail-r-aux [port file-so-far]
  (let [x (read-line* port)]
    (if (nil? x)
      file-so-far
      (recur port (cons x file-so-far)))))

(defn echo-lines-in-reverse-order [in out]
  (doseq [line (tail-r-aux in ())]
    (write-string line out)
    (newline* out)))

(defn go [input output]
  (let [in (open-input-file input)]
    (try
      (when (file-exists? output)
        (delete-file output))
      (let [out (open-output-file output)]
        (try (echo-lines-in-reverse-order in out)
             (finally (close-output-port out))))
      (finally (close-input-port in)))))

(defn -main [& _]
  (let [count (read-input) input1 (read-input) input2 (read-input) output (read-input)
        s3 (str count) s2 input2 s1 input1 name "tail"]
    (run-r7rs-benchmark (str name ":" s3) count
                        (fn [] (go (hide count input1) (hide count input2)))
                        (fn [result] true))))
