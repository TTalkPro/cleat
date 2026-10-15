;;; WC -- One of the Kernighan and Van Wyk benchmarks.
;;; Rewritten by Will Clinger into more idiomatic (and correct!) Scheme.
(ns r7rs.wc
  (:require [r7rs.common :refer [hide read-input run-r7rs-benchmark]]
            [r7rs.util :refer [file-reader]]))

(defn wcport [[read-char _ _]]
  (loop [nl 0 nw 0 nc 0 inword? false]
    (let [x (read-char)]
      (cond (nil? x) (list nl nw nc)
            (= x \space) (recur nl nw (+ nc 1) false)
            (= x \newline) (recur (+ nl 1) nw (+ nc 1) false)
            :else (recur nl (if inword? nw (+ nw 1)) (+ nc 1) true)))))

;; call-with-input-file：调用后关闭端口
(defn go [x]
  (let [port (file-reader x)]
    (try (wcport port)
         (finally ((nth port 2))))))

(defn -main [& _]
  (let [count (read-input) input (read-input) output (read-input)
        s2 (str count) s1 input name "wc"]
    (run-r7rs-benchmark (str name ":" s1 ":" s2) count
                        (fn [] (go (hide count input)))
                        (fn [result] (= result output)))))
