(ns foo.check-assert)

(defn f
  [x]
  {:pre [(keyword? x)]}
  x)

;; cleat: clojure.main has no -main here; the test runs this one
(defn -main [& args]
  (println (f 100)))
