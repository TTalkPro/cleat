(def sum-to
  (fn* [n]
    (loop* [i 0 acc 0]
      (if (cleat.kernel/lt i n)
        (cleat.kernel/add 1 (recur (cleat.kernel/inc i) (cleat.kernel/add acc i)))
        acc))))
