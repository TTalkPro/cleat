;;; TRIANGL -- Board game benchmark.
(ns r7rs.triangl
  (:refer-clojure :exclude [test])
  (:require [r7rs.common :refer [hide read-input run-r7rs-benchmark]]))

;; Scheme 向量（有 vector-set!）用对象数组（^objects 提示放在 attempt 的局部绑定上：def 符号上的提示 JVM 会求值）
(def board (object-array '(1 1 1 1 1 0 1 1 1 1 1 1 1 1 1 1)))

(def sequence* (object-array '(0 0 0 0 0 0 0 0 0 0 0 0 0 0)))

(def a (object-array '(1 2 4 3 5 6 1 3 6 2 5 4 11 12
                                13 7 8 4 4 7 11 8 12 13 6 10
                                15 9 14 13 13 14 15 9 10
                                6 6)))

(def b (object-array '(2 4 7 5 8 9 3 6 10 5 9 8
                                12 13 14 8 9 5 2 4 7 5 8
                                9 3 6 10 5 9 8 12 13 14
                                8 9 5 5)))

(def c (object-array '(4 7 11 8 12 13 6 10 15 9 14 13
                                13 14 15 9 10 6 1 2 4 3 5 6 1
                                3 6 2 5 4 11 12 13 7 8 4 4)))

;; 原版的 vector-set!：值参数是 Object，JVM 上 (aset arr i 0) 这种原始 long 字面量会走反射
(defn- vector-set! [^objects v i x] (aset v i x))

;; 原版的全局 *answer*（set!）用 atom
(def answer (atom ()))

(defn attempt [i depth]
  (let [^objects board board ^objects sequence* sequence* ^objects a a ^objects b b ^objects c c]
  (cond (= depth 14)
        (do (reset! answer (cons (rest (apply list sequence*)) @answer))
            true)
        (and (= 1 (aget board (aget a i)))
             (= 1 (aget board (aget b i)))
             (= 0 (aget board (aget c i))))
        (do (vector-set! board (aget a i) 0)
            (vector-set! board (aget b i) 0)
            (vector-set! board (aget c i) 1)
            (vector-set! sequence* depth i)
            (let [depth (+ depth 1)]
              (loop [j 0]
                (if (or (= j 36) (attempt j depth))
                  false
                  (recur (+ j 1)))))
            (vector-set! board (aget a i) 1)
            (vector-set! board (aget b i) 1)
            (vector-set! board (aget c i) 0)
            false)
        :else false)))

(defn test [i depth]
  (reset! answer ())
  (attempt i depth)
  (first @answer))

(defn -main [& _]
  (let [count (read-input) input1 (read-input) input2 (read-input) output (read-input)
        s3 (str count) s2 (str input2) s1 (str input1)
        name "triangl"]
    (run-r7rs-benchmark (str name ":" s1 ":" s2 ":" s3) count
                        (fn [] (test (hide count input1) (hide count input2)))
                        (fn [result] (= result output)))))
