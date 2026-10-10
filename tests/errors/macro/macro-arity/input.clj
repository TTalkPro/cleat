(def m (fn* [&form &env x] x))
(. (var m) (setMacro))
(def g (fn* [] (m)))
