;;; DESTRUC -- Destructive operation benchmark.
;; 被 set-car! / set-cdr! 改写的表全部用 util 的可变序对（空表 nil）；正确性检查时把结果转成 Clojure 表再 =。
(ns r7rs.destruc
  (:require [r7rs.common :refer [hide read-input run-r7rs-benchmark]]
            [r7rs.util :refer [mcons mpair? mcar mcdr set-mcar! set-mcdr!]]))

(defn- mlength [x]
  (loop [x x n 0]
    (if (nil? x) n (recur (mcdr x) (+ n 1)))))

(defn- append-to-tail! [x y]
  (if (nil? x)
    y
    (loop [a x b (mcdr x)]
      (if (nil? b)
        (do (set-mcdr! a y)
            x)
        (recur b (mcdr b))))))

(defn destructive [n m]
  (let [l (loop [i 10 a nil]
            (if (= i 0) a (recur (- i 1) (mcons nil a))))]
    (loop [i n]
      (if (= i 0)
        l
        (do
          (if (nil? (mcar l))
            (loop [l l]
              (when-not (nil? l)
                (when (nil? (mcar l))
                  (set-mcar! l (mcons nil nil)))
                (append-to-tail! (mcar l)
                                 (loop [j m a nil]
                                   (if (= j 0) a (recur (- j 1) (mcons nil a)))))
                (recur (mcdr l))))
            (loop [l1 l l2 (mcdr l)]
              (when-not (nil? l2)
                (set-mcdr! (loop [j (quot (mlength (mcar l2)) 2)
                                  a (mcar l2)]
                             (if (zero? j)
                               a
                               (do (set-mcar! a i)
                                   (recur (- j 1) (mcdr a)))))
                           (let [n (quot (mlength (mcar l1)) 2)]
                             (if (= n 0)
                               (do (set-mcar! l1 nil)
                                   (mcar l1))
                               (loop [j n a (mcar l1)]
                                 (if (= j 1)
                                   (let [x (mcdr a)]
                                     (set-mcdr! a nil)
                                     x)
                                   (do (set-mcar! a i)
                                       (recur (- j 1) (mcdr a))))))))
                (recur (mcdr l1) (mcdr l2)))))
          (recur (- i 1)))))))

(defn- m->clj
  "可变序对结构 → Clojure 表（空表 → ()），供 equal? 检查。"
  [x]
  (cond (mpair? x) (loop [x x acc []]
                     (if (mpair? x)
                       (recur (mcdr x) (conj acc (m->clj (mcar x))))
                       (apply list acc)))
        (nil? x) ()
        :else x))

(defn -main [& _]
  (let [count (read-input) input1 (read-input) input2 (read-input) output (read-input)]
    (run-r7rs-benchmark (str "destruc:" input1 ":" input2 ":" count) count
                        (fn [] (destructive (hide count input1) (hide count input2)))
                        (fn [result] (= (m->clj result) output)))))
