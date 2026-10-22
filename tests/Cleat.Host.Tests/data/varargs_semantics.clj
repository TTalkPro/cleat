;; cleat-154：已知函数类且实参个数恰为变长子句必需元数时直达 doInvoke；其他元数仍走 RestFn 协议。
;; 用例覆盖必需元数、越过必需元数、apply、闭包、重定义、副作用顺序与 arity 错误。
(defn show [& xs] (println (apply pr-str xs)))
(defn err [f] (try [(f)] (catch Exception e [(.FullName (.GetType e)) (.Message e)])))

(defn r0 [& xs] [(count xs) xs])
(defn r2 [a b & xs] [a b (count xs) xs])
(defn c0 [] (r0))
(defn c2 [] (r2 1 2))
(defn c3 [] (r2 1 2 3))
(defn closure [k]
  (fn [& xs] [k (count xs) (reduce + 0 xs)]))
(def called (atom []))
(defn side [x] (swap! called conj x) x)
(defn order [] (r2 (side :a) (side :b) (side :c)))

(show (c0) (c2) (c3))
(show (r0) (apply r2 [4 5]) (apply r2 4 5 [6 7]))
(let [f (closure 7)] (show (f) (f 1) (f 1 2)))
(show (order) @called)
(show (err #(r2 1)) (r2 1 2 3 4))
(defn r2 [a b] :redefined)
(show (c2) (r2 1 2))

