(def f (fn* [a] (. clojure.lang.Numbers (divide a 0))))

  (f 1)
