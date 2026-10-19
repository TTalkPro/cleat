;; cleat-139：O 模式整体特化（designs/27）——任意体按观察到的形参类型重新生成。默认、CLEAT_NO_OPT=sigspec 与
;; 压力模式（CLEAT_RTSPEC_THRESHOLD=1）输出必须逐字相同。每个函数先热身越过阈值，再观察。
(def warm 300)

;; 副作用只执行一次（没有放弃后重算）
(def counter (atom 0))
(def noisy-add (fn [a b] (swap! counter inc) (+ a b)))
(dotimes [_ warm] (noisy-add 1 2))
(reset! counter 0)
(prn (noisy-add 3 4) (noisy-add 1.5 2) @counter)

;; 返回 nil / false 不是未命中
(def maybe-nil (fn [n] (when (> n 10) n)))
(dotimes [_ warm] (maybe-nil 3))
(prn (maybe-nil 3) (maybe-nil 30) (maybe-nil 3.5))
(def falsy (fn [n] (if (pos? n) false n)))
(dotimes [_ warm] (falsy 1))
(prn (falsy 1) (falsy -1) (falsy 0))

;; 调用别的函数、集合、字符串拼接
(def build (fn [n acc] (if (zero? n) acc (build (dec n) (conj acc (* n n))))))
(dotimes [_ warm] (build 5 []))
(prn (build 6 []) (build 3 '()) (build 2.0 []))
(def fmt (fn [n s] (str s ":" (inc n))))
(dotimes [_ warm] (fmt 1 "a"))
(prn (fmt 41 "x") (fmt 1.5 "y") (fmt 1 :kw))

;; 闭包捕获原始形参
(def adders (fn [n] (mapv (fn [i] (+ n i)) (range 3))))
(dotimes [_ warm] (adders 10))
(prn (adders 10) (adders 0.5))
(def delayed (fn [x] (let [f (fn [] (* x 2))] (f))))
(dotimes [_ warm] (delayed 21))
(prn (delayed 21) (delayed 1.25) (delayed 1/2))

;; 闭包里的自调用
(def tree-sum (fn [t] (if (number? t) t (reduce + 0 (map (fn [c] (tree-sum c)) t)))))
(dotimes [_ warm] (tree-sum [1 [2 3] [[4]]]))
(prn (tree-sum [1 [2 3] [[4]]]) (tree-sum 5) (tree-sum [1.5 [2]]))

;; 异常、溢出、try / catch / finally
(def safe-div (fn [a b] (try (quot a b) (catch ArithmeticException e :div0) (finally (swap! counter inc)))))
(dotimes [_ warm] (safe-div 10 3))
(reset! counter 0)
(prn (safe-div 10 3) (safe-div 1 0) (safe-div 10.0 4) @counter)
(def overflow (fn [a n] (if (zero? n) a (overflow (* a a) (dec n)))))
(dotimes [_ warm] (overflow 2 3))
(prn (try (overflow 3 10) (catch Exception e (.Name (.GetType e)))) (overflow 2N 3))

;; 自调用的实参类型变化：long 签名里以 double 递归
(def drift (fn [x n] (if (zero? n) x (drift (/ x 2) (dec n)))))
(dotimes [_ warm] (drift 64 3))
(prn (drift 64 3) (drift 3 2) (drift 1.0 2))
(def mixed-rec (fn [x n] (if (zero? n) x (mixed-rec (if (even? n) (double x) (long x)) (dec n)))))
(dotimes [_ warm] (mixed-rec 7 4))
(prn (mixed-rec 7 4) (mixed-rec 7 3))

;; with-redefs / binding / 重定义之后的自调用
(def countdown (fn [n acc] (if (zero? n) acc (countdown (dec n) (conj acc n)))))
(dotimes [_ warm] (countdown 3 []))
(with-redefs [countdown (fn [n acc] [:redef n acc])] (prn (countdown 2 [])))
(prn (countdown 3 []))
(def ^:dynamic *scale* 1)
(def scaled (fn [n] (* n *scale*)))
(dotimes [_ warm] (scaled 3))
(prn (scaled 3) (binding [*scale* 10] (scaled 3)) (binding [*scale* 0.5] (scaled 3)))
(def selfish (fn [n] (if (zero? n) :done (selfish (dec n)))))
(dotimes [_ warm] (selfish 5))
(def inner selfish)
(def selfish (fn [n] [:new n]))
(prn (inner 3) (selfish 3))

;; 多个签名、兼容条目、key 为 0 的调用
(def poly (fn [a b] (if (number? a) (+ a (count (str b))) [a b])))
(dotimes [_ warm] (poly 1 2))
(dotimes [_ warm] (poly 1.5 2))
(dotimes [_ warm] (poly 1 "abc"))
(prn (poly 1 2) (poly 1.5 2) (poly 1 "abc") (poly :k 2) (poly nil nil) (poly 1 nil))

;; 数组与字符串形参，带写入（不是纯的）
(def fill! (fn [xs v] (dotimes [i (alength xs)] (aset xs i v)) xs))
(dotimes [_ warm] (fill! (long-array 3) 7))
(prn (vec (fill! (long-array 3) 9)) (vec (fill! (double-array 2) 1.5)) (vec (fill! (object-array 2) :x)))
(def sum-arr (fn [xs] (loop [i 0 acc 0] (if (< i (alength xs)) (recur (inc i) (+ acc (aget xs i))) acc))))
(dotimes [_ warm] (sum-arr (int-array [1 2 3])))
(prn (sum-arr (int-array [1 2 3])) (sum-arr (long-array [4 5])) (sum-arr (double-array [0.5 0.25])))
(def shout (fn [s n] (apply str (repeat n (.ToUpper s)))))
(dotimes [_ warm] (shout "ab" 2))
(prn (shout "ab" 2) (shout "x" 0))

;; 惰性序列与 recur 的形参
(def take-evens (fn [n] (filter even? (range n))))
(dotimes [_ warm] (doall (take-evens 10)))
(prn (take-evens 10) (take-evens 3))
(def loop-sum (fn [n] (loop [i 0 acc 0] (if (< i n) (recur (inc i) (+ acc i)) acc))))
(dotimes [_ warm] (loop-sum 10))
(prn (loop-sum 100) (loop-sum 10.5) (loop-sum 0))
