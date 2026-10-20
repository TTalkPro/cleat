;; cleat-146：keyword intern 的 identity / 弱引用表一致性，以及 symbol 与整数文本的语义对照。
;; 同一进程在 CLEAT_NO_OPT=intern 开 / 关两种设置下逐行输出必须相同。
(prn (let [a (keyword "a") b (keyword "a")]
       [(identical? a b) (= a b) (= (hash a) (hash b)) (str a) (keyword? a) (symbol? a)]))

(prn (let [a (keyword "ns" "a") b (keyword "ns/a") c (keyword "ns" "a")]
       [(identical? a b) (identical? a c) (= a b c) (str a) (str b)
        (name a) (namespace a)]))

(prn (let [k (keyword (with-meta 'qualified {:x 1}))]
       [(meta k) (= k :qualified) (identical? k (keyword (with-meta 'qualified {:x 2})))]))

(def intern-find-present (keyword "present"))
(def intern-find-qualified (keyword "q" "hit"))

(prn [(find-keyword "present") (find-keyword "q" "hit") (find-keyword "q/hit")
      (find-keyword "not-interned-146") (find-keyword "q" "not-interned-146")
      (find-keyword "q/not-interned-146")])

(prn (let [s (symbol "a") q (symbol "ns" "a")]
       [s (name s) (namespace s) (str s) (= s 'a)
        q (name q) (namespace q) (str q) (= q 'ns/a)]))

(prn [(keyword? :k) (keyword? 'k) (keyword? "k")
      (symbol? 's) (symbol? :s) (symbol? "s")])

(prn [(str 0 7 -7 9223372036854775807N -9223372036854775808N 1.0 2.5E-4 true)
      (str 9223372036854775807) (str -9223372036854775808) (str 2147483647)
      (str -2147483648)])

(prn (let [a (gensym) b (gensym)]
       [(identical? a b) (= a b) (= (name a) (name b))]))

(prn (count (distinct (map name (repeatedly 100 gensym)))))

(prn [(map keyword (range 5))
      (identical? (keyword (str 3)) (nth (map keyword (range 5)) 3))
      (identical? (keyword (str "hot")) (keyword "hot"))])
