;; an unclosed list: the reader reaches EOF
(def x 1)
(def y (fn* [] (x 1))
