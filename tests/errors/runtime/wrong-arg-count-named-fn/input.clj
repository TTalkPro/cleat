(def f (fn* add [a b] (cleat.kernel/add a b)))
(def g (fn* [] (f 1)))
(g)
