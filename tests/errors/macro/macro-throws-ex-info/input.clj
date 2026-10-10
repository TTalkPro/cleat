(def m (fn* [&form &env] (throw (new clojure.lang.ExceptionInfo "bad input" {:a 1}))))
(. (var m) (setMacro))
(def g (fn* []
  (m)))
