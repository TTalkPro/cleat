;; cleat-147：RT.nth 快路径的语义矩阵；CLEAT_NO_OPT=nthfast 开 / 关输出必须逐字相同。
(defn show [label f]
  (println label (try (pr-str (f)) (catch Exception e (str "<" (.GetType e) ":" (.Message e) ">")))))

(def xs (list 1 2 3))
(def v [1 2 3])
(def s "a\ud83d\ude00b")
(def ss (seq s))
(def chunked (map inc (range 5)))
(def lazy5 (take 5 (iterate inc 10)))
(def objs (object-array [:a :b :c]))
(def longs (long-array [7 8 9]))
(def nf :missing)

;; Indexed / vector：命中、末端、默认、越界默认、负索引异常。
(show "vector-hit" #(nth v 0))
(show "vector-last" #(nth v 2))
(show "vector-default-hit" #(nth v 2 nf))
(show "vector-default-miss" #(nth v 3 nf))
(show "vector-negative-default" #(nth v -1 nf))
(show "vector-negative" #(nth v -1))

;; 持久 list / ASeq：正序、末端、默认与异常路径。
(show "list-hit" #(nth xs 0))
(show "list-last" #(nth xs 2))
(show "list-default-hit" #(nth xs 1 nf))
(show "list-default-miss" #(nth xs 3 nf))
(show "list-negative" #(nth xs -1))
(show "list-oob" #(nth xs 3))

;; 空 list：PersistentList+EmptyList 实现 ISeq 但 seq() 为 nil，快路径必须先过 RT.seq 归一，
;; 否则「越界」会被读成「元素是 nil」（cleat-150 在 CTS clojure.core-test.nth 抓到的回归）。
(show "empty-list-oob" #(nth (list) 0))
(show "empty-list-default" #(nth (list) 0 nf))
(show "empty-vector-oob" #(nth [] 0))
(show "empty-vector-default" #(nth [] 10 nf))
(show "empty-seq-oob" #(nth (seq (list)) 0))

;; string 的 UTF-16 码元语义：代理对占两个 nth 下标，末端与越界异常。
(show "string-hit" #(nth s 0))
(show "string-surrogate-low" #(nth s 1))
(show "string-surrogate-high" #(nth s 2))
(show "string-last" #(nth s 3))
(show "string-default-hit" #(nth s 3 nf))
(show "string-default-miss" #(nth s 4 nf))
(show "string-negative" #(nth s -1))
(show "string-oob" #(nth s 4))

;; StringSeq 与 count 路径。
(show "string-seq-hit" #(nth ss 0))
(show "string-seq-surrogate-high" #(nth ss 2))
(show "string-seq-last" #(nth ss 3))
(show "string-seq-default-miss" #(nth ss 4 nf))
(show "string-seq-oob" #(nth ss 4))

;; chunked seq：chunk 内、跨 chunk、默认与异常。
(show "chunk-hit" #(nth chunked 0))
(show "chunk-cross" #(nth chunked 4))
(show "chunk-default-miss" #(nth chunked 5 nf))
(show "chunk-oob" #(nth chunked 5))
(show "chunk-negative" #(nth chunked -1))

;; LazySeq：realize 后逐节点、默认 miss、小下标不 realize 无穷尾。
(def lazy-infinite (iterate inc 0))
(show "lazy-hit" #(nth lazy5 0))
(show "lazy-last" #(nth lazy5 4))
(show "lazy-default-miss" #(nth lazy5 5 nf))
(show "lazy-oob" #(nth lazy5 5))
(show "lazy-infinite-hit" #(nth lazy-infinite 3))
(show "lazy-negative" #(nth lazy-infinite -1))

;; 一维数组：引用元素、原始 long、默认 miss 与异常。
(show "object-array-hit" #(nth objs 0))
(show "object-array-last" #(nth objs 2))
(show "object-array-default-miss" #(nth objs 3 nf))
(show "object-array-oob" #(nth objs 3))
(show "long-array-hit" #(nth longs 1))
(show "long-array-default-miss" #(nth longs -1 nf))
