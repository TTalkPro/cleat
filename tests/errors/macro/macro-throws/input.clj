(def m (fn* [&form &env] (throw (new System.Exception "boom"))))
(. (var m) (setMacro))
(def g (fn* [] (m)))
