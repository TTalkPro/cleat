;;; PARAFFINS -- Compute how many paraffins exist with N carbon atoms.
;; 符号 → 关键字。
;; 内层命名 let 尾调用外层（loop2 → (loop1 lst (- nc1 1))）写成内层 loop 返回 lst、外层 recur。
;; 不可变表 → Clojure 表（cons / first / next，空表 nil）；只读的小向量（分割、radical）→ Clojure 向量；
;; 被 vector-set! 的 radicals → 对象数组。原版命名 let 在初值位置非尾递归调用自己（(loop2 (cdr rads1) lst)），
;; 所以每个命名 let 写成具名局部 fn（与原版一样每次进入都建闭包），只有真正的尾调用才是 loop/recur。
(ns r7rs.paraffins
  (:require [r7rs.common :refer [hide read-input run-r7rs-benchmark]]
            [r7rs.util :refer [make-vector]]))

(defn- three-partitions [m]
  (loop [lst nil
         nc1 (quot m 3)]
    (if (< nc1 0)
      lst
      (let [lst (loop [lst lst
                       nc2 (quot (- m nc1) 2)]
                  (if (< nc2 nc1)
                    lst
                    (recur (cons [nc1 nc2 (- m (+ nc1 nc2))] lst)
                           (- nc2 1))))]
        (recur lst (- nc1 1))))))

(defn- four-partitions [m]
  (loop [lst nil
         nc1 (quot m 4)]
    (if (< nc1 0)
      lst
      (let [lst (loop [lst lst
                       nc2 (quot (- m nc1) 3)]
                  (if (< nc2 nc1)
                    lst
                    (let [start (max nc2 (- (quot (+ m 1) 2) (+ nc1 nc2)))
                          lst (loop [lst lst
                                     nc3 (quot (- m (+ nc1 nc2)) 2)]
                                (if (< nc3 start)
                                  lst
                                  (recur (cons [nc1 nc2 nc3 (- m (+ nc1 (+ nc2 nc3)))] lst)
                                         (- nc3 1))))]
                      (recur lst (- nc2 1)))))]
        (recur lst (- nc1 1))))))

(defn- gen [n]
  (let [n-2 (quot n 2)
        ^objects radicals (make-vector (+ n-2 1) '(:H))]
    (letfn [(rads-of-size [n]
              ((fn loop1 [ps lst]
                 (if (nil? ps)
                   lst
                   (let [p (first ps)
                         nc1 (nth p 0)
                         nc2 (nth p 1)
                         nc3 (nth p 2)]
                     ((fn loop2 [rads1 lst]
                        (if (nil? rads1)
                          lst
                          ((fn loop3 [rads2 lst]
                             (if (nil? rads2)
                               lst
                               ((fn loop4 [rads3 lst]
                                  (if (nil? rads3)
                                    lst
                                    (cons [:C (first rads1) (first rads2) (first rads3)]
                                          (loop4 (next rads3) lst))))
                                (if (= nc2 nc3) rads2 (aget radicals nc3))
                                (loop3 (next rads2) lst))))
                           (if (= nc1 nc2) rads1 (aget radicals nc2))
                           (loop2 (next rads1) lst))))
                      (aget radicals nc1)
                      (loop1 (next ps) lst)))))
               (three-partitions (- n 1))
               nil))
            (bcp-generator [j]
              (if (odd? j)
                nil
                ((fn loop1 [rads1 lst]
                   (if (nil? rads1)
                     lst
                     ((fn loop2 [rads2 lst]
                        (if (nil? rads2)
                          lst
                          (cons [:BCP (first rads1) (first rads2)]
                                (loop2 (next rads2) lst))))
                      rads1
                      (loop1 (next rads1) lst))))
                 (aget radicals (quot j 2))
                 nil)))
            (ccp-generator [j]
              ((fn loop1 [ps lst]
                 (if (nil? ps)
                   lst
                   (let [p (first ps)
                         nc1 (nth p 0)
                         nc2 (nth p 1)
                         nc3 (nth p 2)
                         nc4 (nth p 3)]
                     ((fn loop2 [rads1 lst]
                        (if (nil? rads1)
                          lst
                          ((fn loop3 [rads2 lst]
                             (if (nil? rads2)
                               lst
                               ((fn loop4 [rads3 lst]
                                  (if (nil? rads3)
                                    lst
                                    ((fn loop5 [rads4 lst]
                                       (if (nil? rads4)
                                         lst
                                         (cons [:CCP (first rads1) (first rads2) (first rads3) (first rads4)]
                                               (loop5 (next rads4) lst))))
                                     (if (= nc3 nc4) rads3 (aget radicals nc4))
                                     (loop4 (next rads3) lst))))
                                (if (= nc2 nc3) rads2 (aget radicals nc3))
                                (loop3 (next rads2) lst))))
                           (if (= nc1 nc2) rads1 (aget radicals nc2))
                           (loop2 (next rads1) lst))))
                      (aget radicals nc1)
                      (loop1 (next ps) lst)))))
               (four-partitions (- j 1))
               nil))]
      (loop [i 1]
        (if (> i n-2)
          [(bcp-generator n)
           (ccp-generator n)]
          (do (aset radicals i (rads-of-size i))
              (recur (+ i 1))))))))

(defn- nb [n]
  (let [x (gen n)]
    (+ (count (nth x 0))
       (count (nth x 1)))))

(defn -main [& _]
  (let [count (read-input) input1 (read-input) output (read-input)]
    (run-r7rs-benchmark (str "paraffins:" input1 ":" count) count
                        (fn [] (nb (hide count input1)))
                        (fn [result] (= result output)))))
