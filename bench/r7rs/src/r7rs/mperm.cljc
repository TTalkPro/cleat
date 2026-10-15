;;; MPERM -- memory system benchmark using Zaks's permutation generator (perm9.sch, MpermNKL).
;; 不可变表 → Clojure 表（cons / first / next，空表 nil）：permutations 生成共享尾部的置换表（revloop 新分配、list-tail 共享），
;; 保持原版的分配与存活模式；permutations 里被 set! 的局部 x / perms → volatile；队列 → 对象数组。
(ns r7rs.mperm
  (:require [r7rs.common :refer [hide read-input run-r7rs-benchmark]]
            [r7rs.util :refer [make-vector]]))

(defn- permutations [x]
  (let [x (volatile! x)
        perms (volatile! (list @x))]
    (letfn [(P [n]
              (when (> n 1)
                (loop [j (- n 1)]
                  (if (zero? j)
                    (P (- n 1))
                    (do (P (- n 1))
                        (F n)
                        (recur (- j 1)))))))
            (F [n]
              (vreset! x (revloop @x n (list-tail @x n)))
              (vreset! perms (cons @x @perms)))
            (revloop [x n y]
              (if (zero? n)
                y
                (recur (next x)
                       (- n 1)
                       (cons (first x) y))))
            (list-tail [x n]
              (if (zero? n)
                x
                (recur (next x) (- n 1))))]
      (P (count @x))
      @perms)))

;; Given a list of lists of numbers, returns the sum of the sums
;; of those lists.
(defn- sumlists [x]
  (loop [x x sum 0]
    (if (nil? x)
      sum
      (recur (next x)
             (loop [y (first x) sum sum]
               (if (nil? y) sum (recur (next y) (+ sum (first y)))))))))

(defn- one-to-n [n]
  (loop [n n p nil]
    (if (zero? n) p (recur (- n 1) (cons n p)))))

(defn- factorial [n]
  (if (= n 0)
    1
    (* n (factorial (- n 1)))))

(defn- MpermNKL-benchmark [m n k ell]
  (if (and (<= 0 m)
           (pos? n)
           (pos? k)
           (<= 0 ell k))
    (let [id (str "mperm:" m ":" n ":" k ":" ell)
          ^objects queue (make-vector k nil)]
      (letfn [;; Fills queue positions [i, j).
              (fill-queue [i j]
                (when (< i j)
                  (aset queue i (permutations (one-to-n n)))
                  (recur (+ i 1) j)))
              ;; Removes ell elements from queue.
              (flush-queue []
                (loop [i 0]
                  (when (< i k)
                    (aset queue
                          i
                          (let [j (+ i ell)]
                            (if (< j k)
                              (aget queue j)
                              nil)))
                    (recur (+ i 1)))))]
        (fill-queue 0 (- k ell))
        (run-r7rs-benchmark id
                            m
                            (fn []
                              (fill-queue (- k ell) k)
                              (flush-queue)
                              queue)
                            (fn [^objects q]
                              (let [q0 (aget q 0)
                                    qi (aget q (max 0 (- k ell 1)))]
                                (and (or (and (nil? q0) (nil? qi))
                                         (and (seq? q0)
                                              (seq? qi)
                                              (= (first q0) (first qi))))
                                     (= (sumlists q0)
                                        (/ (* n (+ n 1) (factorial n)) 2))))))))
    (do (print "Incorrect arguments to MpermNKL-benchmark")
        (newline))))

(defn -main [& _]
  (let [input1 (read-input) input2 (read-input) input3 (read-input) input4 (read-input) output (read-input)]
    (MpermNKL-benchmark (hide input1 input1)
                        (hide input1 input2)
                        (hide input1 input3)
                        (hide input1 input4))))
