(defn show [& xs] (println (apply pr-str xs)))
(defn r [e] (try (e) (catch Exception ex (str (.GetType ex) ": " (.Message ex)))))
;; cleat-108: typed loop variables whose recur argument is an if / begin
(defn cnt [n] (loop [i 0 c 0] (if (< i n) (recur (inc i) (if (even? i) (inc c) c)) c)))
(show (cnt 10) (cnt 0) (cnt 7) (type (cnt 5)))
(defn nested [n] (loop [i 0 a 0 b 0] (if (< i n) (recur (inc i) (if (< i 3) (if (odd? i) (+ a 10) (dec a)) a) (if (> i 5) (do (+ b i)) b)) [a b])))
(show (nested 10) (nested 2))
(def log (atom []))
(defn side [n] (loop [i 0 s 0] (if (< i n) (recur (inc i) (do (swap! log conj i) (if (zero? (mod i 3)) (+ s i) s))) s)))
(show (side 7) @log)
;; a branch that leaves the loop or throws
(defn early [n lim] (loop [i 0 s 0] (if (< i n) (recur (inc i) (if (> s lim) (throw (ex-info "too big" {:s s})) (+ s i))) s)))
(show (early 5 100) (r #(early 50 100)))
(defn exit [n] (loop [i 0 s 0] (cond (>= i n) s (= i 4) (recur (inc i) (if (odd? s) s (* s 2))) :else (recur (inc i) (+ s i)))))
(show (exit 10) (exit 3))
;; overflow inside a branch still throws
(defn ovf [n] (loop [i 0 s (- Int64/MaxValue 3)] (if (< i n) (recur (inc i) (if (pos? i) (inc s) s)) s)))
(show (ovf 3) (r #(ovf 10)))
;; types that differ between branches: no raw storage, same answers
(defn mixed [n] (loop [i 0 s 0] (if (< i n) (recur (inc i) (if (even? i) (+ s 1) (+ s 0.5))) s)))
(show (mixed 5) (type (mixed 5)) (mixed 0))
(defn ratio [n] (loop [i 0 s 0] (if (< i n) (recur (inc i) (if (even? i) (+ s 1/2) s)) s)))
(show (ratio 5))
;; double accumulators and untyped entries
(defn dsum [n] (loop [i 0 s 0.0] (if (< i n) (recur (inc i) (if (even? i) (+ s 1.5) s)) s)))
(show (dsum 5) (dsum 0))
(defn usum [n x] (loop [i 0 s x] (if (< i n) (recur (inc i) (if (even? i) (+ s i) s)) s)))
(show (usum 6 0) (usum 6 0.5) (usum 6 1/3) (usum 0 :k) (r #(usum 3 "a")))
;; boxed uses of the same variable inside the branches
(defn boxed [n] (loop [i 0 s 0 acc []] (if (< i n) (recur (inc i) (if (even? i) (inc s) s) (conj acc s)) [s acc])))
(show (boxed 5))
