;; cleat-132: signature-specialized candidates must stay observably identical; output must be identical with CLEAT_NO_OPT=sigspec
(def sig-ll (fn [x n] (if (= n 0) x (sig-ll (inc x) (dec n)))))
(def sig-dd (fn [x n] (if (= n 0) x (sig-dd (* x 2.0) (dec n)))))
(def sig-overflow (fn [x n] (if (= n 0) x (sig-overflow (+ x x) (dec n)))))
(def rt4 (fn [a b c d] (+ (+ (+ a b) c) d)))
(def rt-sum4 (fn [a b c n] (if (= n 0) a (rt-sum4 (+ a b) (+ b c) (+ c n) (dec n)))))
(def rt-array (fn [xs] (aget xs 0)))
(def rt-string (fn [s] (count s)))
(def rt-mixed (fn [xs s] (+ (aget xs 0) (count s))))

(prn (sig-ll 1 3))
(prn (sig-dd 1.25 2))
(prn (sig-ll 1.5 2))                     ; LL 特化不适用：按通用体提升为 double
(prn (sig-dd 1 2))                       ; DD 特化不适用：按通用体保持整数算术
(prn (sig-ll "x" 0))
(prn (sig-ll nil 0))
(prn (sig-ll 1/3 0))
(prn (sig-ll 1N 0))
(prn (sig-ll 1.25M 0))
(prn (sig-dd ##NaN 0))
(prn (sig-dd 0.0 0))
(prn (sig-dd -0.0 0))
(try (sig-overflow 4611686018427387904 1) (catch Exception e (prn :overflow (.Name (.GetType e)))))
(try (sig-ll :bad 0) (catch Exception e (prn :bad-arg (.Name (.GetType e)))))
(with-redefs [sig-ll (fn [x n] [:redefined x n])] (prn (sig-ll 1 1)))
(prn (sig-ll 1 1))
(dotimes [_ 20] (rt4 1 2 3 4))
(prn (rt4 5 6 7 8))
(dotimes [_ 20] (rt-sum4 (long 1) (long 2) (long 3) (long 4)))
(prn (rt-sum4 (long 5) (long 6) (long 7) (long 8)))
(prn (rt-sum4 1 2 3 4))                  ; 首签名 Int64；Int32 回到通用函数
(dotimes [_ 20] (rt-array (int-array [7])))
(prn (rt-array (int-array [9])))
(prn (rt-array (long-array [11])))               ; 首签名 Int32[]；其他数组回退通用函数
(dotimes [_ 20] (rt-string "abc"))
(prn (rt-string "de"))
(dotimes [_ 20] (rt-mixed (int-array [3]) "abcd"))
(prn (rt-mixed (int-array [4]) "de"))
(prn (rt-mixed (double-array [1.5]) "x"))        ; 混合精确签名不命中，回退通用体
