(def m (fn* [&form &env] (. clojure.lang.Numbers (divide 1 0))))
(. (var m) (setMacro))
(def g (fn* [] (m)))
