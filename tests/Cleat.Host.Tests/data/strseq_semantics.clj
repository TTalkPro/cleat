(require '[clojure.string :as str])
(defn show [& xs] (println (apply pr-str xs)))
;; cleat-143: direct read paths for strings must preserve StringSeq values, types, identity and boundaries
(def surrogate "a😀bc")
(let [s "abc"]
  (show (first s) (first "") (seq "") (seq "a") (next s) (next "a") (rest s) (rest "a"))
  (show (second s) (nth s 1 :nf) (nth s 9 :nf) (count "") (empty? "") (empty? s) (mapv identity s))
  (show (reduce str (seq s)) (into [] s) (first (subs "xabc" 1)) (reduce + (map int s)))
  (let [a (seq s) b (seq s)]
    (show (= a b) (= (next a) (seq "bc")) (identical? a b) (identical? a a)
          (identical? (next a) (next b)) (class a) (class (rest s))))
  (show (str/starts-with? (str (seq s)) "clojure.lang.StringSeq@")
        (subs (str (seq s)) 0 (min 21 (count (str (seq s)))))))
(let [s surrogate]
  ;; 用码元打印代理对：裸代理字符在 JVM / CLR 的不可编码输出替换策略不同，不是本优化要检验的语义
  (show (count s) (mapv int s) (mapv int (seq s)) (reduce + (map int s))
        (mapv int (subs s 1 4)) (int (first (subs s 1 4)))))
