;;; FIBC -- FIB using first-class continuations, written by Kent Dybvig
(ns r7rs.fibc
  (:require [r7rs.common :refer [hide read-input run-r7rs-benchmark]]
            [r7rs.util :as u]))

(defn succ [n] (+ n 1))
(defn pred [n] (- n 1))

;; addc 的自尾调用用 recur
(defn addc [x y k]
  (if (zero? y)
    (k x)
    (recur (succ x) (pred y) k)))

;; 原版的 call/cc 只作逃逸用：c 只在捕获它的 call/cc 的动态范围内被调用（经 addc 或直接 (c 0) / (c 1)），
;; 所以用 util/call-with-escape，语义相同。最外层的 c 是普通函数 (fn [n] n)。
(defn fibc [x c]
  (if (zero? x)
    (c 0)
    (if (zero? (pred x))
      (c 1)
      (addc (u/call-with-escape
             (fn [c] (fibc (pred x) c)))
            (u/call-with-escape
             (fn [c] (fibc (pred (pred x)) c)))
            c))))

(defn -main [& _]
  (let [count (read-input) input (read-input) output (read-input)
        s2 (str count) s1 (str input)
        name "fibc"]
    (run-r7rs-benchmark (str name ":" s1 ":" s2) count
                        (fn [] (fibc (hide count input) (hide count (fn [n] n))))
                        (fn [result] (= result output)))))
