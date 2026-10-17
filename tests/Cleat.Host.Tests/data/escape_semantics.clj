;; cleat-125: language-level escape markers with conservative fallback to the old exception protocol
(def target (fn* [x ^:escape k] (if (= x :escape) (k :escaped) [:normal x])))
(def indirect (fn* [f] (f :fallback)))
(def pass (fn* [x ^:escape k] (target x k)))
(def fallback (fn* [k] (indirect k)))
(def nested (fn* [] (escape* (fn [outer-k]
                              (escape* (fn [inner-k] (outer-k :from-inner)))))))
(def show (fn* []
  [(escape* (fn [k] (target :escape k)))
   (escape* (fn [k] (target :normal k)))
   (escape* (fn [k] (pass :escape k)))
   (escape* (fn [k] (fallback k)))
   (escape* (fn [k] (target :normal k) (k :after) :unreachable))
   (nested)
   (try (escape* (fn [k] (throw (ex-info "user" {:v 1}))))
        (catch Exception e [:user (ex-message e) (:v (ex-data e))]))
   (try (escape* (fn [k] (target :normal k) (k :inside-try) :unreachable))
        (catch Exception e :escape-should-not-be-caught))]))
(println (pr-str (show)))
