;; cleat-120: loop bodies do not inline user wrappers whose clauses call other functions; output must be identical with CLEAT_NO_OPT=loopinl
(defn show [& xs] (println (apply pr-str xs)))
(defn r [e] (try (e) (catch Exception ex (str (.GetType ex) ": " (.Message ex)))))
(deftype Cell [^:unsynchronized-mutable v])
(defn touch [^Cell c x] (set! (.-v ^Cell c) x) [x (.-v ^Cell c)])
;; OUTER has a tiny clause but it calls another user fn; cleat-120 leaves it as a call in loops.
(defn outer [c x] (touch c x))
(defn outer-loop [n] (let [c (Cell. nil)]
                       (loop [i 0 acc []]
                         (if (= i n) [acc (.-v ^Cell c)] (recur (inc i) (conj acc (outer c i)))))))
;; redefining the wrapper between rounds: direct calls see the new root immediately; an inline guard falls back too
(def calls (atom []))
(defn step [x] (swap! calls conj :old) (inc x))
(defn step-loop [n] (loop [x 0 i 0]
                      (if (= i n) [x @calls]
                        (do (when (= i 2) (alter-var-root #'step (constantly (fn [x] (swap! calls conj :new) (+ x 10)))))
                            (recur (step x) (inc i))))))
;; argument effects and exception order are the same whether the clause is inlined
(def log (atom []))
(defn note [x] (swap! log conj x) x)
(defn add-notes [a b] (+ (note a) (note b)))
(defn add-loop [] (loop [i 0 acc []]
                    (if (= i 3) [acc @log]
                      (recur (inc i)
                             (conj acc
                                   (try (add-notes (note i) (if (= i 1) "s" 2))
                                        (catch Exception ex (str "bad:" (.-v ^Cell (Cell. :arg))))))))))
(show (outer-loop 4) (step-loop 5) (add-loop))
