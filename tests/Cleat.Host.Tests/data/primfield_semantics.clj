(defn show [& xs] (println (apply pr-str xs)))
(defmacro r [e] `(try ~e (catch Exception ex# :error)))
;; cleat-112: ^long / ^double deftype and defrecord fields are primitive fields
(deftype Complex [^double re ^double im])
(defn c+ [^Complex a ^Complex b] (Complex. (+ (.-re a) (.-re b)) (+ (.-im a) (.-im b))))
(defn parts [^Complex c] [(.-re c) (.-im c)])
(show (parts (c+ (Complex. 1.0 2.0) (Complex. 3 4))) (parts (Complex. 1 2)) (parts (->Complex 5 6.5)) (r (Complex. "a" 1.0)) (r (Complex. nil 1.0)))
(show (map type (parts (Complex. 1 2))) (.-re (Complex. 3/2 0)))
(deftype Cnt [^:unsynchronized-mutable ^long n ^:unsynchronized-mutable ^double total]
  clojure.lang.IDeref (deref [_] [n total])
  clojure.lang.IFn (invoke [this x] (set! n (inc n)) (set! total (+ total x)) this))
(let [c (Cnt. 0 0)] (dotimes [i 5] (c i)) (show @c (map type @c)))
(let [c (Cnt. 0 0.5)] (show (r (c "x")) @c))
(defrecord P [^long x ^double y z])
(let [p (->P 1 2 :z) q (map->P {:x 3 :y 4.5 :z nil :w 9})]
  (show p q (:x p) (:y p) (type (:y p)) (= p (->P 1 2.0 :z)) (assoc p :x 10) (.-x q) (into {} p) (dissoc p :z) (assoc p :y 7) (r (assoc p :x "s"))))
(show (r (->P "x" 1 2)) (r (map->P {:x 1.5 :y 2})))
;; mixed field kinds, metadata, equality and hashing through the record
(defrecord Q [a ^long b])
(show (= (->Q 1 2) (->Q 1 2)) (= (hash (->Q 1 2)) (hash (->Q 1 2))) (meta (with-meta (->Q 1 2) {:m 1})) (:b (with-meta (->Q 1 2) {:m 1})))
