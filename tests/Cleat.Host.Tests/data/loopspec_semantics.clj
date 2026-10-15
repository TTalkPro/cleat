(defn show [& xs] (println (apply pr-str xs)))
(defn r [e] (try (e) (catch Exception ex (str (.GetType ex) ": " (.Message ex)))))
;; cleat-103: loops whose variables come from untyped params, run with every kind of entry value
(defn sum [n x] (loop [i 0 s x] (if (< i n) (recur (inc i) (+ s i)) s)))
(show (sum 10 0) (sum 10 0.5) (sum 3 1/2) (sum 3 10N) (sum 3 1.0M) (sum 3 (int 4)) (r #(sum 3 "a")) (r #(sum 3 nil)) (sum 0 :k) (sum 0 nil))
(show (type (sum 3 0)) (type (sum 3 0.0)) (type (sum 3 (int 1))) (r #(sum 2 Int64/MaxValue)) (sum 1 Int64/MinValue))
;; two speculated variables together (one entry long, the other not)
(defn two [n a b] (loop [i 0 x a y b] (if (< i n) (recur (inc i) (+ x i) (* y 2)) [x y])))
(show (two 5 0 1) (two 5 0 1.5) (two 5 0.5 1) (two 3 1/2 1N) (r #(two 70 0 1)) (two 0 "a" "b") (r #(two 1 nil 1)))
;; a variable that is long at entry but would become double: no long copy (inference refuses), same answers
(defn drift [n x] (loop [i 0 s x] (if (< i n) (recur (inc i) (if (> i 2) (+ s 1.5) (+ s i))) s)))
(show (drift 5 0) (drift 5 0.0) (drift 2 1/2) (r #(drift 5 nil)))
;; NaN / infinities through a double loop
(defn dsum [n x] (loop [i 0 s x] (if (< i n) (recur (inc i) (* s 2.0)) s)))
(show (dsum 3 ##NaN) (dsum 3 ##Inf) (dsum 3 1) (dsum 3 1.5) (dsum 3 1/2) (dsum 0 :k))
;; the speculated variable used boxed inside the loop, and as a collection element
(defn collect [n x] (loop [i 0 s x acc []] (if (< i n) (recur (inc i) (+ s i) (conj acc [s (type s) (str s)])) acc)))
(show (collect 3 0) (collect 3 0.5) (collect 2 1/2))
;; init expressions that are calls (evaluated once, in order)
(def calls (atom []))
(defn tr [v] (swap! calls conj v) v)
(defn ord [n] (loop [i (tr 0) s (tr n) t (tr 1)] (if (< i 3) (recur (inc i) (+ s i) (* t 3)) [s t])))
(show (ord 10) (ord 1.5) @calls)
;; comparison chain and early exit with a non-number result
(defn find-first [n x] (loop [i x] (cond (> i n) :none (zero? (mod i 7)) i :else (recur (inc i)))))
(show (find-first 100 1) (find-first 100 50) (find-first 3 1) (find-first 100 1.0) (r #(find-first 10 "a")))
;; unchecked math: wrap in both copies
(set! *unchecked-math* true)
(defn usum [n x] (loop [i 0 s x] (if (< i n) (recur (inc i) (+ s Int64/MaxValue)) s)))
(show (usum 3 1) (usum 3 1.0))
(set! *unchecked-math* false)
;; nested loops: only the inner one is copied
(defn grid [n x] (loop [i 0 acc x] (if (< i n) (recur (inc i) (loop [j 0 a acc] (if (< j n) (recur (inc j) (+ a j)) a))) acc)))
(show (grid 4 0) (grid 4 0.5) (grid 3 1/3))
;; cleat-113: Num variables are speculated too (entry double, double steps; entry long turning double)
(defn sumfp [n] (loop [i n sum 0.0] (if (< i 0.0) sum (recur (- i 1.0) (+ i sum)))))
(show (sumfp 10.0) (sumfp 10) (sumfp 5/2) (sumfp -1.0) (r #(sumfp nil)))
(defn halve [x] (loop [v x k 0] (if (< v 1.0) [v k] (recur (/ v 2.0) (inc k)))))
(show (halve 100.0) (halve 100) (halve 1/3))
