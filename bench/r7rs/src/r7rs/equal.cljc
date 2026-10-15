;;; EQUAL -- tests the R6RS equal? predicate on some fairly large structures of various shapes.
;;; Copyright 2007 William D Clinger (see the original equal.scm for the full notice).
;; 序对 → util 可变序对（含 make-test-tree5 的点对 (tree . a)），向量 → 对象数组，符号照旧（eqv? → =）。
;; Clojure 的 = 对可变序对 / 数组是同一性比较，而且不处理共享（benchmark1 的 DAG 在朴素比较下是 n! 级），
;; 所以按 R6RS 参考实现（Adams & Dybvig 2008：precheck + fast/slow 交替 + union-find）写了私有 equal?；
;; eq-hashtable → 瞬态 Clojure 哈希表（可变序对与对象数组的 hash/equals 都是同一性），box → volatile，random → 私有 LCG。
;; cdr 方向的尾调用改成循环（car 方向保持递归，与原版一样）。
(ns r7rs.equal
  (:require [r7rs.common :refer [hide read-input run-r7rs-benchmark]]
            [r7rs.util :refer [mcons mpair? mcar mcdr make-vector]]))

;;; ---- R6RS equal?（Adams & Dybvig） ----

(def ^:private object-array-class (class (object-array 0)))
(defn- vector?* [x] (instance? object-array-class x))

(def ^:private k0 400)
(def ^:private kb -40)

(def ^:private rand-state (volatile! 12345))
(defn- random* [n]
  (vreset! rand-state (rem (+ (* @rand-state 1103515245) 12345) 2147483648))
  (rem @rand-state n))

(defn- union-find
  "ht 是装着瞬态表的 volatile；x、y 已在同一类时返回真，否则合并并返回假。"
  [ht x y]
  (letfn [(find [b]
            (let [n @b]
              (if (volatile? n)
                (loop [b b n n]
                  (let [nn @n]
                    (if (volatile? nn)
                      (do (vreset! b nn) (recur n nn))
                      n)))
                b)))]
    (let [bx (get @ht x) by (get @ht y)]
      (if (nil? bx)
        (if (nil? by)
          (let [b (volatile! 1)]
            (vswap! ht assoc! x b)
            (vswap! ht assoc! y b)
            false)
          (let [ry (find by)]
            (vswap! ht assoc! x ry)
            false))
        (if (nil? by)
          (let [rx (find bx)]
            (vswap! ht assoc! y rx)
            false)
          (let [rx (find bx) ry (find by)]
            (or (identical? rx ry)
                (let [nx @rx ny @ry]
                  (if (> nx ny)
                    (do (vreset! ry rx) (vreset! rx (+ nx ny)) false)
                    (do (vreset! rx ry) (vreset! ry (+ ny nx)) false))))))))))

(defn- atom-equal [x y]
  (if (string? x) (and (string? y) (= x y)) (= x y)))

(defn- precheck? [x y k]
  (loop [x x y y k k]
    (cond (identical? x y) k
          (mpair? x) (and (mpair? y)
                          (if (<= k 0)
                            k
                            (let [k (precheck? (mcar x) (mcar y) (- k 1))]
                              (and k (recur (mcdr x) (mcdr y) k)))))
          (vector?* x) (and (vector?* y)
                            (let [n (alength ^objects x)]
                              (and (= (alength ^objects y) n)
                                   (loop [i 0 k k]
                                     (if (or (= i n) (<= k 0))
                                       k
                                       (let [k (precheck? (aget ^objects x i) (aget ^objects y i) (- k 1))]
                                         (and k (recur (+ i 1) k))))))))
          :else (and (atom-equal x y) k))))

(defn- e?
  "原版的 e? / fast? / slow?：k > 0 为 fast（先减 1），k = kb 时以随机燃料回到 fast，其余为 slow（union-find）。"
  [ht x y k]
  (loop [x x y y k k]
    (let [fast (or (> k 0) (= k kb))
          k (cond (> k 0) (- k 1)
                  (= k kb) (- (random* (* 2 k0)) 1)
                  :else k)]
      (cond (identical? x y) k
            (mpair? x)
            (and (mpair? y)
                 (if fast
                   (let [k (e? ht (mcar x) (mcar y) k)]
                     (and k (recur (mcdr x) (mcdr y) k)))
                   (if (union-find ht x y)
                     0
                     (let [k (e? ht (mcar x) (mcar y) (- k 1))]
                       (and k (recur (mcdr x) (mcdr y) k))))))
            (vector?* x)
            (and (vector?* y)
                 (let [n (alength ^objects x)]
                   (and (= (alength ^objects y) n)
                        (if (and (not fast) (union-find ht x y))
                          0
                          (loop [i 0 k (if fast k (- k 1))]
                            (if (= i n)
                              k
                              (let [k (e? ht (aget ^objects x i) (aget ^objects y i) k)]
                                (and k (recur (+ i 1) k)))))))))
            :else (and (atom-equal x y) k)))))

(defn- interleave? [x y k]
  (let [ht (volatile! (transient {}))]
    (if (e? ht x y k) true false)))

(defn- equal? [x y]
  (let [k (precheck? x y k0)]
    (and k (or (> k 0) (interleave? x y 0)))))

;;; ---- 原版 ----

(defn- vector->list [^objects v]
  (loop [i (- (alength v) 1) l nil]
    (if (< i 0) l (recur (- i 1) (mcons (aget v i) l)))))

;; Returns a list with n elements, all equal to x.
(defn- make-test-list1 [n x]
  (if (zero? n)
    nil
    (mcons x (make-test-list1 (- n 1) x))))

;; Returns a list of n lists, each consisting of n x's.
;; The n elements of the outer list are actually the same list.
(defn- make-test-tree1 [n]
  (if (zero? n)
    nil
    (make-test-list1 n (make-test-tree1 (- n 1)))))

;; Returns a list of n elements, as returned by the thunk.
(defn- make-test-list2 [n thunk]
  (if (zero? n)
    nil
    (mcons (thunk) (make-test-list2 (- n 1) thunk))))

;; Returns a balanced tree of height n, with the branching factor
;; at each level equal to the height of the tree at that level.
;; The subtrees do not share structure.
(defn- make-test-tree2 [n]
  (if (zero? n)
    nil
    (make-test-list2 n (fn [] (make-test-tree2 (- n 1))))))

;; Returns an extremely unbalanced tree of height n.
(defn- make-test-tree5 [n]
  (if (zero? n)
    nil
    (mcons (make-test-tree5 (- n 1))
           'a)))

;; Calls the thunk n times.
(defn- iterate* [n thunk]
  (cond (= n 1) (thunk)
        (> n 1) (do (thunk)
                    (recur (- n 1) thunk))
        :else false))

;; DAG with much sharing.
;; 10 is a good parameter for n.
(defn- equality-benchmark1 [n]
  (let [x (make-test-tree1 n)
        y (make-test-tree1 n)]
    (iterate* n (hide n (fn [] (equal? x y))))))

;; Tree with no sharing.
;; 8 is a good parameter for n.
(defn- equality-benchmark2 [n]
  (let [x (make-test-tree2 n)
        y (make-test-tree2 n)]
    (iterate* n (hide n (fn [] (equal? x y))))))

;; Flat vectors.
;; 1000 might be a good parameter for n.
(defn- equality-benchmark3 [n]
  (let [x (make-vector n 'a)
        y (make-vector n 'a)]
    (iterate* n (hide n (fn [] (equal? x y))))))

;; Shallow lists.
;; 300 might be a good parameter for n.
(defn- equality-benchmark4 [n]
  (let [x (vector->list (make-vector n (make-test-tree2 3)))
        y (vector->list (make-vector n (make-test-tree2 3)))]
    (iterate* n (hide n (fn [] (equal? x y))))))

;; No sharing, no proper lists,
;; and deep following car chains instead of cdr.
(defn- equality-benchmark5 [n & rest]
  (let [x (make-test-tree5 n)
        y (make-test-tree5 n)
        iterations (if (nil? rest) n (first rest))]
    (iterate* iterations (hide n (fn [] (equal? x y))))))

(defn- equality-benchmarks [n0 n1 n2 n3 n4 n5]
  (and ; (equality-benchmark0 n0) ;; cyclic benchmark does not work on most non-R7RS schemes
       (equality-benchmark1 n1)
       (equality-benchmark2 n2)
       (equality-benchmark3 n3)
       (equality-benchmark4 n4)
       (equality-benchmark5 n5)))

(defn -main [& _]
  (let [input0 (read-input) input1 (read-input) input2 (read-input) input3 (read-input)
        input4 (read-input) input5 (read-input)]
    ;; 第 7 个数据 output 是 #t：Clojure 读取器读不了（且原版不用它，检查是 (eq? result #t)），所以不读。
    (run-r7rs-benchmark (str "equal:" input0 ":" input1 ":" input2 ":" input3 ":" input4 ":" input5)
                        1
                        (fn []
                          (equality-benchmarks (hide input0 input0)
                                               (hide input0 input1)
                                               (hide input0 input2)
                                               (hide input0 input3)
                                               (hide input0 input4)
                                               (hide input0 input5)))
                        (fn [result] (true? result)))))
