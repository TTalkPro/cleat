(def vs [1 -1 0 1.5 -0.0 ##NaN 1/2 2N #?(:clj Long/MAX_VALUE :cljr Int64/MaxValue) 3 2.0])
(defn r [f] (try (let [x (f)] [x (cond (double? x) :d (integer? x) :i (ratio? x) :r :else :o)]) (catch #?(:clj Throwable :cljr Exception) e :err)))
(doseq [a vs b vs]
  (println a b (r #(+ a b)) (r #(- a b)) (r #(* a b)) (r #(< a b)) (r #(<= a b)) (r #(> a b)) (r #(>= a b)) (r #(== a b)) (r #(= a b))))
(doseq [a vs] (println a (r #(inc a)) (r #(dec a))))
