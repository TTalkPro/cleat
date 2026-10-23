(defn show [& xs] (println (apply pr-str xs)))
(defn r [e] (try (e) (catch Exception ex (str (.GetType ex) ": " (.Message ex)))))
;; cleat-155：无捕获闭包提升为模块级单例（同一源码位置的重复求值共享一个对象）。
;; 提升不得改变语义：创建不提前、错误在调用时抛、可变捕获（volatile! / 循环变量 / 运行时形参）
;; 每个环境各自一份、Var 引用按调用时解析、meta 走 withMeta 拷贝。

;; —— 无捕获：顶层创建、多次调用、循环里反复求值同一源码位置 ——
(def make-nil (fn [] :no-capture))
(show (make-nil) (make-nil))
(defn loop-make [n]
  (loop [i 0 acc 0]
    (if (< i n)
      (let [f (fn [] 7)]
        (recur (inc i) (+ acc (f))))
      acc)))
(show (loop-make 100000))

;; —— 静态捕获（编译期常量）——
(show (let [base 7] ((fn [x] (+ base x)) 3)))

;; —— 捕获运行时值：跨返回后调用；同一源码位置在不同环境互不串扰 ——
(defn make-adder [n] (fn [x] (+ x n)))
(def add2 (make-adder 2))
(def add40 (make-adder 40))
(show (add2 0) (add40 0) (+ (add2 1) (add40 1)))

;; —— volatile! 每个环境各自一份（不得被提升成共享单例）——
(defn make-counter []
  (let [v (volatile! 0)]
    {:inc (fn [] (vswap! v inc)) :get (fn [] @v)}))
(def c1 (make-counter))
(def c2 (make-counter))
((:inc c1)) ((:inc c1)) ((:inc c2))
(show [((:get c1)) ((:get c2))])

;; —— 循环变量捕获：每次迭代捕获当次的值 ——
(loop [i 0 acc []]
  (if (< i 3)
    (recur (inc i) (conj acc (((fn [j] (fn [] j)) i))))
    (show acc)))

;; —— Var 引用捕获：重定义后按调用时解析 ——
(def ^:dynamic *lift-dyn* 1)
(def get-dyn (fn [] *lift-dyn*))
(show (get-dyn))
(alter-var-root #'*lift-dyn* inc)
(show (get-dyn))
(show (binding [*lift-dyn* 5] [(set! *lift-dyn* 6) *lift-dyn*]))
(show *lift-dyn*)

;; —— ^:once 形状（lazy-seq / delay 的展开）与惰性序列 ——
(show (mapv inc [1 2 3]))
(def d (delay :delay-val))
(show (force d) (force d))
(def ls (map (fn [x] (* x x)) [1 2 3]))
(show (vec ls))

;; —— 错误抛出时机：创建不求值体 ——
(def boom (fn [] (/ 1 0)))
(show :created-ok)
(show (r boom))

;; —— meta 走 withMeta 拷贝，不污染被共享的对象 ——
(def mf (with-meta (fn [] 1) {:tag :m}))
(show (meta mf) (mf))
