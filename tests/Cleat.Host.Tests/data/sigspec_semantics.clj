;; cleat-132: signature-specialized candidates must stay observably identical; output must be identical with CLEAT_NO_OPT=sigspec
(def sig-ll (fn [x n] (if (= n 0) x (sig-ll (inc x) (dec n)))))
(def sig-dd (fn [x n] (if (= n 0) x (sig-dd (* x 2.0) (dec n)))))
(def sig-overflow (fn [x n] (if (= n 0) x (sig-overflow (+ x x) (dec n)))))

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
