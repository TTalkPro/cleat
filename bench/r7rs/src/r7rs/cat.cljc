;;; CAT -- One of the Kernighan and Van Wyk benchmarks.
;;; Rewritten by Will Clinger into more idiomatic Scheme.
(ns r7rs.cat
  (:require [r7rs.common :refer [hide read-input run-r7rs-benchmark]]
            [r7rs.util :refer [file-reader]]))

;; 输出端口与文件操作（JVM：java.io；CLR：System.IO）
(defn- file-exists? [p]
  #?(:clj (.exists (java.io.File. ^String p)) :cljr (System.IO.File/Exists ^String p)))
(defn- delete-file [p]
  #?(:clj (.delete (java.io.File. ^String p)) :cljr (System.IO.File/Delete ^String p)))
(defn- open-output-file [p]
  #?(:clj (java.io.BufferedWriter. (java.io.FileWriter. ^String p)) :cljr (System.IO.StreamWriter. ^String p)))
(defn- write-char [c out]
  #?(:clj (.write ^java.io.Writer out (int (char c))) :cljr (.Write ^System.IO.TextWriter out (char c))))
(defn- close-output-port [out]
  #?(:clj (.close ^java.io.Writer out) :cljr (.Close ^System.IO.TextWriter out)))

(defn catport [[read-char _ _] out]
  (loop []
    (let [x (read-char)]
      (when-not (nil? x)
        (write-char x out)
        (recur)))))

(defn go [input-file output-file]
  (when (file-exists? output-file)
    (delete-file output-file))
  (let [in (file-reader input-file)]
    (try
      (let [out (open-output-file output-file)]
        (try (catport in out)
             (finally (close-output-port out))))
      (finally ((nth in 2))))))

(defn -main [& _]
  (let [count (read-input) input1 (read-input) input2 (read-input) output (read-input)
        s3 (str count) s2 input2 s1 input1 name "cat"]
    (run-r7rs-benchmark (str name ":" s3) count
                        (fn [] (go (hide count input1) (hide count input2)))
                        (fn [result] true))))
