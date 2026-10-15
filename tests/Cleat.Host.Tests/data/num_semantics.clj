(defn show [& xs] (println (apply pr-str xs)))
(defn r [e] (try (e) (catch Exception ex (str (.GetType ex) ": " (.Message ex)))))
;; loops whose variables come from untyped params
(defn sum [n x] (loop [i 0 s x] (if (< i n) (recur (inc i) (+ s i)) s)))
(show (sum 10 0) (sum 10 0.5) (sum 3 1/2) (sum 3 10N) (sum 3 1.0M) (r #(sum 3 "a")) (r #(sum 3 nil)) (sum 0 :k))
(defn poly [n a b] (loop [i 0 s 0.0] (if (< i n) (recur (inc i) (+ s (* (- a b) (+ a b)))) s)))
(show (poly 3 1.5 0.5) (poly 3 3 1) (poly 2 1/2 1/3) (poly 1 1N 2) (r #(poly 1 nil 1)) (r #(poly 1 "x" 1)))
;; arithmetic on params
(defn ar [a b] [(+ a b) (- a b) (* a b) (/ a b) (inc a) (dec b) (< a b) (<= a b) (> a b) (>= a b) (== a b) (= a b)])
(show (ar 1 2) (ar 1 2.0) (ar 1.5 2) (ar 1.5 2.5) (ar 1 1.0) (ar 6 3) (ar 1 3) (ar 1/2 3) (ar 2N 3) (ar 1.0M 2) (ar 7 7))
(show (r #(ar Int64/MaxValue 1)) (r #(ar Int64/MinValue 1)) (r #(ar 1 0)) (r #(ar 1.0 0)) (r #(ar -1.0 0)) (r #(ar 0.0 0.0)) (r #(ar 1.0 0.0)) (ar ##NaN 1) (ar ##NaN ##NaN) (ar ##Inf ##-Inf) (ar 1.0 2) (ar 7 2.0))
(show (r #(ar nil 1)) (r #(ar 1 nil)) (r #(ar "a" 1)) (r #(ar :k 2)) (r #(ar [1] 1)))
(defn big [a] [(* a a) (+ a a) (inc a) (dec a) (- a 1) (- a) (*' a a) (+' a a) (inc' a)])
(defn mul [a b] (* a b)) (show (r #(mul Int64/MinValue -1)) (r #(mul 3037000500 3037000500)) (r #(mul -3037000500 3037000500)) (mul 1.5 2) (mul Int64/MinValue 1) (mul Int64/MinValue 0))
(show (r #(* Int64/MinValue -1)) (r #(let [a Int64/MinValue b -1] (* a b))) (r #((fn [a b] (* a b)) Int64/MinValue -1)))
;; let variables and nesting
(defn lets [a b] (let [x (+ a b) y (* x x) z (- y x)] [x y z (/ z 2) (< x y) (str x) (identical? x x) (number? y)]))
(show (lets 1 2) (lets 1.5 2) (lets 1/2 1/2) (lets 10N 1))
(defn mixed [a] (let [x (+ a 1)] (if (> x 10) (str "big" x) (vector x (+ x 0.5)))))
(show (mixed 5) (mixed 20) (mixed 9.5) (mixed 1/2))
;; a num param used both raw and boxed
(defn both [a] [(+ a 1) a (str a) (type a) (vector a) (= a a) (identical? a a)])
(show (both 1) (both 1.5) (both 1/3) (both 10N) (r #(both nil)) (r #(both "s")))
;; unchecked math
(show (binding [*unchecked-math* true] (eval '((fn [a b] [(+ a b) (* a b)]) Int64/MaxValue 2))))
(set! *unchecked-math* true)
(defn unch [a b] [(+ a b) (- a b) (* a b) (inc a)])
(show (unch Int64/MaxValue 1) (unch 2.5 1))
(set! *unchecked-math* false)
;; loop with mixed long/double across iterations
(defn flip [n] (loop [i 0 s 0] (if (< i n) (recur (inc i) (if (even? i) (+ s 1) (+ s 0.5))) s)))
(show (flip 4) (flip 5) (flip 0))
;; comparisons chain and NaN loops
(defn cnt [x lim] (loop [c 0 v x] (if (and (< c lim) (< v 100)) (recur (inc c) (* v 2)) [c v])))
(show (cnt 1 50) (cnt 1.5 50) (cnt ##NaN 5) (cnt 1/3 50) (cnt 200 50))
;; mbrot shape
(defn count-iter [cr ci max-iter]
  (loop [zr 0.0 zi 0.0 c 0]
    (if (and (< c max-iter) (< (+ (* zr zr) (* zi zi)) 4.0))
      (recur (+ (- (* zr zr) (* zi zi)) cr) (+ (* 2.0 zr zi) ci) (inc c))
      c)))
(show (count-iter -0.5 0.5 100) (count-iter 1 1 100) (count-iter 0 0 10) (count-iter -0.75 0.1 1000))
