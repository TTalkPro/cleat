(in-ns (quote other))
(def ^:private secret 1)
(in-ns (quote user))
(def g (fn* [] other/secret))
