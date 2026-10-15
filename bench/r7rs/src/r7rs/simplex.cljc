;;; SIMPLEX -- Simplex algorithm.
;;; 矩阵与 l1 / l2 / l3 / iposv / izrov 是可变 vector → 对象数组；被内部函数 set! 的外层变量（ip kp bmax nl1 one? pass2?）用 volatile。
;;; simp2 里只在本函数内 set! 的 q1 / flag? 与内层循环的 qp / q0 改为循环变量（同样的控制流与运算）。
;;; 输出 (#(4 1 3 2) #(0 5 7 6)) 被 Clojure 读成 fn* 形式，读入时还原成向量的表；比较时把结果里的数组转成向量。
(ns r7rs.simplex
  (:refer-clojure :exclude [test])
  (:require [r7rs.common :refer [hide read-input run-r7rs-benchmark]]
            [r7rs.util :as u]))

(defn- vector-ref [^objects v i] (aget v i))
(defn- vector-set! [^objects v i x] (aset v i x))

(defn matrix-rows [^objects a] (alength a))
(defn matrix-columns [^objects a] (alength ^objects (aget a 0)))
(defn matrix-ref [^objects a i j] (aget ^objects (aget a i) j))
(defn matrix-set! [^objects a i j x] (aset ^objects (aget a i) j x))

(defn complain []
  (u/fatal "This shouldn't happen"))

(defn simplex [a m1 m2 m3]
  (let [*epsilon* 0.000001]
    (when-not (and (>= m1 0)
                   (>= m2 0)
                   (>= m3 0)
                   (= (matrix-rows a) (+ m1 m2 m3 2)))
      (complain))
    (let [m12 (+ m1 m2 1)
          m (- (matrix-rows a) 2)
          n (- (matrix-columns a) 1)
          ^objects l1 (u/make-vector n)
          ^objects l2 (u/make-vector m)
          ^objects l3 (u/make-vector m2)
          nl1 (volatile! n)
          ^objects iposv (u/make-vector m)
          ^objects izrov (u/make-vector n)
          ip (volatile! 0)
          kp (volatile! 0)
          bmax (volatile! 0.0)
          one? (volatile! false)
          pass2? (volatile! true)]
      (letfn [(simp1 [mm abs?]
                (vreset! kp (vector-ref l1 0))
                (vreset! bmax (matrix-ref a mm @kp))
                (loop [k 1]
                  (when-not (>= k @nl1)
                    (when (pos?
                           (if abs?
                             (- (abs (matrix-ref a mm (vector-ref l1 k)))
                                (abs @bmax))
                             (- (matrix-ref a mm (vector-ref l1 k)) @bmax)))
                      (vreset! kp (vector-ref l1 k))
                      (vreset! bmax (matrix-ref a mm (vector-ref l1 k))))
                    (recur (+ k 1)))))
              (simp2 []
                (vreset! ip 0)
                (loop [i 0 q1 0.0 flag? false]
                  (when-not (= i m)
                    (if flag?
                      (if (< (matrix-ref a (vector-ref l2 i) @kp) (- *epsilon*))
                        (let [q (/ (- (matrix-ref a (vector-ref l2 i) 0))
                                   (matrix-ref a (vector-ref l2 i) @kp))]
                          (cond
                            (< q q1)
                            (do (vreset! ip (vector-ref l2 i))
                                (recur (+ i 1) q flag?))
                            (= q q1)
                            (do (when (loop [k 1 qp 0.0 q0 0.0]
                                        (if (<= k n)
                                          (let [qp (/ (- (matrix-ref a @ip k))
                                                      (matrix-ref a @ip @kp))
                                                q0 (/ (- (matrix-ref a (vector-ref l2 i) k))
                                                      (matrix-ref a (vector-ref l2 i) @kp))]
                                            (if (= q0 qp)
                                              (recur (+ k 1) qp q0)
                                              (< q0 qp)))
                                          (< q0 qp)))
                                  (vreset! ip (vector-ref l2 i)))
                                (recur (+ i 1) q1 flag?))
                            :else (recur (+ i 1) q1 flag?)))
                        (recur (+ i 1) q1 flag?))
                      (if (< (matrix-ref a (vector-ref l2 i) @kp) (- *epsilon*))
                        (let [q1 (/ (- (matrix-ref a (vector-ref l2 i) 0))
                                    (matrix-ref a (vector-ref l2 i) @kp))]
                          (vreset! ip (vector-ref l2 i))
                          (recur (+ i 1) q1 true))
                        (recur (+ i 1) q1 flag?))))))
              (simp3 [one?]
                (let [ip @ip kp @kp
                      piv (/ 1 (matrix-ref a ip kp))]
                  (loop [ii 0]
                    (when-not (= ii (+ m (if one? 2 1)))
                      (when-not (= ii ip)
                        (matrix-set! a ii kp (* piv (matrix-ref a ii kp)))
                        (loop [kk 0]
                          (when-not (= kk (+ n 1))
                            (when-not (= kk kp)
                              (matrix-set!
                               a ii kk (- (matrix-ref a ii kk)
                                          (* (matrix-ref a ip kk)
                                             (matrix-ref a ii kp)))))
                            (recur (+ kk 1)))))
                      (recur (+ ii 1))))
                  (loop [kk 0]
                    (when-not (= kk (+ n 1))
                      (when-not (= kk kp)
                        (matrix-set! a ip kk (* (- piv) (matrix-ref a ip kk))))
                      (recur (+ kk 1))))
                  (matrix-set! a ip kp piv)))]
        (loop [k 0]
          (when-not (= k n)
            (vector-set! l1 k (+ k 1))
            (vector-set! izrov k k)
            (recur (+ k 1))))
        (loop [i 0]
          (when-not (= i m)
            (when (neg? (matrix-ref a (+ i 1) 0))
              (complain))
            (vector-set! l2 i (+ i 1))
            (vector-set! iposv i (+ n i))
            (recur (+ i 1))))
        (loop [i 0] (when-not (= i m2) (vector-set! l3 i true) (recur (+ i 1))))
        (when (pos? (+ m2 m3))
          (loop [k 0]
            (when-not (= k (+ n 1))
              (loop [i (+ m1 1) sum 0.0]
                (if (> i m)
                  (matrix-set! a (+ m 1) k (- sum))
                  (recur (+ i 1) (+ sum (matrix-ref a i k)))))
              (recur (+ k 1))))
          (loop []
            (simp1 (+ m 1) false)
            (cond
              (<= @bmax *epsilon*)
              (cond (< (matrix-ref a (+ m 1) 0) (- *epsilon*))
                    (vreset! pass2? false)
                    (<= (matrix-ref a (+ m 1) 0) *epsilon*)
                    (loop [ip1 m12]
                      (if (<= ip1 m)
                        (cond (= (vector-ref iposv (- ip1 1)) (+ @ip n -1))
                              (do (simp1 ip1 true)
                                  (cond (pos? @bmax)
                                        (do (vreset! ip ip1)
                                            (vreset! one? true))
                                        :else
                                        (recur (+ ip1 1))))
                              :else
                              (recur (+ ip1 1)))
                        (loop [i (+ m1 1)]
                          (when-not (>= i m12)
                            (when (vector-ref l3 (- i (+ m1 1)))
                              (loop [k 0]
                                (when-not (= k (+ n 1))
                                  (matrix-set!
                                   a i k (- (matrix-ref a i k)))
                                  (recur (+ k 1)))))
                            (recur (+ i 1))))))
                    :else
                    (do (simp2)
                        (if (zero? @ip) (vreset! pass2? false) (vreset! one? true))))
              :else (do (simp2) (if (zero? @ip) (vreset! pass2? false) (vreset! one? true))))
            (when @one?
              (vreset! one? false)
              (simp3 true)
              (cond
                (>= (vector-ref iposv (- @ip 1)) (+ n m12 -1))
                (loop [k 0]
                  (cond
                    (and (< k @nl1) (not (= @kp (vector-ref l1 k))))
                    (recur (+ k 1))
                    :else
                    (do (vreset! nl1 (- @nl1 1))
                        (loop [is k]
                          (when-not (>= is @nl1)
                            (vector-set! l1 is (vector-ref l1 (+ is 1)))
                            (recur (+ is 1))))
                        (matrix-set!
                         a (+ m 1) @kp (+ (matrix-ref a (+ m 1) @kp) 1.0))
                        (loop [i 0]
                          (when-not (= i (+ m 2))
                            (matrix-set! a i @kp (- (matrix-ref a i @kp)))
                            (recur (+ i 1)))))))
                (and (>= (vector-ref iposv (- @ip 1)) (+ n m1))
                     (vector-ref l3
                           (- (vector-ref iposv (- @ip 1)) (+ m1 n))))
                (do (vector-set! l3 (- (vector-ref iposv (- @ip 1)) (+ m1 n)) false)
                    (matrix-set!
                     a (+ m 1) @kp (+ (matrix-ref a (+ m 1) @kp) 1.0))
                    (loop [i 0]
                      (when-not (= i (+ m 2))
                        (matrix-set! a i @kp (- (matrix-ref a i @kp)))
                        (recur (+ i 1))))))
              (let [t (vector-ref izrov (- @kp 1))]
                (vector-set! izrov (- @kp 1) (vector-ref iposv (- @ip 1)))
                (vector-set! iposv (- @ip 1) t))
              (recur))))
        (and @pass2?
             (loop []
               (simp1 0 false)
               (cond
                 (pos? @bmax)
                 (do (simp2)
                     (cond (zero? @ip) true
                           :else (do (simp3 false)
                                     (let [t (vector-ref izrov (- @kp 1))]
                                       (vector-set! izrov
                                             (- @kp 1) (vector-ref iposv (- @ip 1)))
                                       (vector-set! iposv (- @ip 1) t))
                                     (recur))))
                 :else (list iposv izrov))))))))

(defn- svector [& xs] (object-array xs))   ; Scheme 的 (vector …)，结果会被修改

(defn test [input]
  (simplex (svector (svector 0.0 1.0 1.0 3.0 -0.5)
                      (svector 740.0 -1.0 0.0 -2.0 0.0)
                      (svector 0.0 0.0 -2.0 0.0 7.0)
                      (svector 0.5 0.0 -1.0 1.0 -2.0)
                      (svector 9.0 -1.0 -1.0 -1.0 -1.0)
                      (svector 0.0 0.0 0.0 0.0 0.0))
           2 1 1))

(defn- scheme-vector [form]
  (if (and (seq? form) (= 'fn* (first form))) (vec (nth form 2)) form))

(defn -main [& _]
  (let [count (read-input) input1 (read-input) output (map scheme-vector (read-input))]
    (run-r7rs-benchmark (str "simplex" ":" count) count
                        (fn [] (test (hide count input1)))
                        (fn [result] (and (seq? result) (= (map vec result) output))))))
