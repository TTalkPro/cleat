(def m (fn* [&form &env] (throw (new System.ArgumentException "bad arg"))))
(. (var m) (setMacro))
(def g (fn* [] (m)))
