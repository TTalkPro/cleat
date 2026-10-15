;; Mandelbrot 计数（cleat-100；skeg r7rs-benchmarks 的 mbrot 形状）：无提示参数进入的 double 循环与嵌套算术，
;; 所有数值都从参数传入，编译期类型未知。
(ns bench.mbrot)

(defn count-iter [cr ci max-iter]
  (loop [zr 0.0 zi 0.0 c 0]
    (if (and (< c max-iter) (< (+ (* zr zr) (* zi zi)) 4.0))
      (recur (+ (- (* zr zr) (* zi zi)) cr) (+ (* 2.0 zr zi) ci) (inc c))
      c)))

(defn mbrot [n max-iter]
  (let [step (/ 3.0 n)]
    (loop [y 0 total 0]
      (if (< y n)
        (recur (inc y)
               (loop [x 0 t total]
                 (if (< x n)
                   (recur (inc x) (+ t (count-iter (- (* x step) 2.0) (- (* y step) 1.5) max-iter)))
                   t)))
        total))))

(defn -main [& _]
  (assert (pos? (mbrot 10 20)))
  (dotimes [_ 5]
    (let [sw (System.Diagnostics.Stopwatch/StartNew)]
      (mbrot 500 100)
      (printf "BENCH mbrot %d%n" (.ElapsedMilliseconds sw))
      (flush))))
