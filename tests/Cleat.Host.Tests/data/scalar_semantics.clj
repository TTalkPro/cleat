;; cleat-123: scalar replacement of deftype loop variables and let temps; output must be identical with CLEAT_NO_OPT=scalar
(defn show [& xs] (println (apply pr-str xs)))
(defn r [e] (try (e) (catch Exception ex (str (.GetType ex) ": " (.Message ex)))))
(deftype Cx [^double re ^double im])
(deftype Lp [^long a ^long b])
(definterface IBump (bump []))
(deftype Mx [^:unsynchronized-mutable ^double v ^double w] IBump (bump [_] (set! v (+ v 100.0)) v))
(defrecord Rp [^double x ^double y])
(defn c+ [^Cx a ^Cx b] (Cx. (+ (.-re a) (.-re b)) (+ (.-im a) (.-im b))))
(defn c* [^Cx a ^Cx b] (Cx. (- (* (.-re a) (.-re b)) (* (.-im a) (.-im b))) (+ (* (.-re a) (.-im b)) (* (.-im a) (.-re b)))))
(defn re-of ^double [^Cx z] (.-re z))
(defn cnt [z0 n] (loop [z z0 c 0] (if (or (= c n) (> (+ (* (re-of z) (re-of z)) (* (.-im ^Cx z) (.-im ^Cx z))) 16.0)) [c (.-re ^Cx z) (.-im ^Cx z)] (recur (c+ (c* z z) z0) (inc c)))))
(show (cnt (Cx. -0.5 0.1) 64) (cnt (Cx. 1.0 1.0) 64) (cnt (Cx. 0.0 0.0) 3) (r #(cnt nil 3)) (r #(cnt :k 3)) (r #(cnt (Lp. 1 2) 3)))
;; identity: within one iteration every escape sees the same object; the initial object is the original one
(def seen (atom []))
(defn track [o] (swap! seen conj o) o)
(defn idl [z0 n] (loop [z z0 c 0] (if (= c n) z (do (track z) (track z) (recur (Cx. (+ (.-re ^Cx z) 1.0) (.-im ^Cx z)) (inc c))))))
(let [z0 (Cx. 0.0 5.0) out (idl z0 3) s @seen]
  (show (count s) (identical? (s 0) z0) (identical? (s 0) (s 1)) (identical? (s 2) (s 3)) (identical? (s 1) (s 2)) (mapv #(.-re ^Cx %) s) (.-re ^Cx out)
        (= (hash (s 2)) (hash (s 3)))))
;; escape to a mutating method: later field reads must see the mutation
(defn mut [m0 n] (loop [m m0 c 0 acc []] (if (= c n) acc (let [b (.bump ^IBump m)] (recur (Mx. (.-w ^Mx m) 1.0) (inc c) (conj acc b (.-w ^Mx m)))))))
(show (mut (Mx. 1.0 2.0) 3))
;; constructor argument conversion and exception order (long -> double, string -> ClassCast after both args evaluated)
(def log (atom []))
(defn conv [n] (loop [z (Cx. 0.0 0.0) c 0] (if (= c n) [(.-re ^Cx z) (.-im ^Cx z)] (recur (Cx. (do (swap! log conj :a) (inc c)) (do (swap! log conj :b) 2)) (inc c)))))
(show (conv 3) (r #(loop [z (Cx. 0.0 0.0) c 0] (if (= c 2) (.-re ^Cx z) (recur (Cx. (do (swap! log conj :x) "s") (do (swap! log conj :y) 1.0)) (inc c))))) @log)
;; long fields
(defn fib-pair [n] (loop [p (Lp. 0 1) c 0] (if (= c n) (.-a ^Lp p) (recur (Lp. (.-b ^Lp p) (+ (.-a ^Lp p) (.-b ^Lp p))) (inc c)))))
(show (fib-pair 10) (fib-pair 50) (r #(fib-pair 100)))
;; records are not scalarised but must still work
(defn rsum [n] (loop [p (->Rp 0.0 0.0) c 0] (if (= c n) [(:x p) (.-y p)] (recur (->Rp (+ (.-x p) 1.0) (+ (.-y p) 0.5)) (inc c)))))
(show (rsum 4))
;; let-bound temporaries: field reads, escape, identity
(defn lets [n] (loop [c 0 acc 0.0 keep nil] (if (= c n) [acc (.-re ^Cx keep)] (let [t (Cx. (* 1.5 c) 2.0) u (c* t t)] (recur (inc c) (+ acc (.-re t) (.-im u)) (if (= c 1) t keep))))))
(show (lets 4))
;; redefining an inlined fn mid-loop: the guard fails and the loop continues with the new fn's results
(def calls (atom 0))
(defn step [^Cx a] (Cx. (+ (.-re a) 1.0) (.-im a)))
(defn run-step [n] (loop [z (Cx. 0.0 0.0) c 0] (if (= c n) (.-re ^Cx z) (do (when (= c 2) (alter-var-root #'step (constantly (fn [a] (swap! calls inc) (Cx. (+ (.-re ^Cx a) 10.0) 0.0))))) (recur (step z) (inc c))))))
(show (run-step 5) @calls)
;; primitive-signature fns inlined with the invokePrim conversions
(defn arl ^long [x] x)
(defn sqd ^double [^double x] (* x x))
(defn usep [] [(arl 18446744073709551616N) (arl 7/2) (sqd 3) (r #(sqd nil)) (r #(sqd "x")) (class (sqd 2)) (class (arl 2))])
(show (usep))
