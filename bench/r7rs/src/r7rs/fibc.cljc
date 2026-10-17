;;; FIBC -- FIB using first-class continuations, written by Kent Dybvig
(ns r7rs.fibc
  (:require [r7rs.common :refer [hide read-input run-r7rs-benchmark]]
            [r7rs.util :as u]))

(defn succ [n] (+ n 1))
(defn pred [n] (- n 1))

;; cleat-125：语言层 escape* + ^:escape 续延形参。普通续延仍由 EscapeFallbackOf 回退到异常协议；
;; 直接传 owner 的调用返回 marker，由调用点的 EscapePropagate 沿已证明路径传播。
(def addc
  (fn* [x y ^:escape k]
    (if (zero? y)
      (k x)
      (recur (succ x) (pred y) k))))

(def fibc
  (fn* [x ^:escape c]
    (if (zero? x)
      (c 0)
      (if (zero? (pred x))
        (c 1)
        (addc #?(:cljr (escape* (fn [c] (fibc (pred x) c))) :clj (u/call-with-escape (fn [c] (fibc (pred x) c))))
              #?(:cljr (escape* (fn [c] (fibc (pred (pred x)) c))) :clj (u/call-with-escape (fn [c] (fibc (pred (pred x)) c))))
              c)))))

(defn -main [& _]
  (let [count (read-input) input (read-input) output (read-input)
        s2 (str count) s1 (str input)
        name "fibc"]
    (run-r7rs-benchmark (str name ":" s1 ":" s2) count
                        (fn [] (fibc (hide count input) (hide count (fn [n] n))))
                        (fn [result] (= result output)))))
