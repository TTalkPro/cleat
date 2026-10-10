(def f
  (fn* [x]
    (try
      (recur x)
      (catch System.Exception e nil))))
