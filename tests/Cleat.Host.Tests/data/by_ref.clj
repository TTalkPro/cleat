;; cleat-183：宿主调用的 by-ref 实参（ref / out 形参）。ClojureCompilerTests.ByRefArguments 在缺省与 CLEAT_NO_OPT=host
;; （全部走运行时的 ByRefCalls）两种设置下运行，输出必须逐字相同。
(set! *warn-on-reflection* false)

;; out 形参：编译期唯一决议（^String）与无提示两种
(defn parse-hinted [^String s] (let [n 0] (if (Int64/TryParse s (by-ref n)) n :bad)))
(defn parse-any [s] (let [n 0] (if (Int64/TryParse s (by-ref n)) n :bad)))
(prn :out (parse-hinted "42") (parse-hinted "x") (parse-any "43") (parse-any "y"))

;; 结果的表示：Int32 / Double / Boolean 的 out 值按 Clojure 的数值模型（Int64 / Double）
(prn :repr (let [i 0 d 0.0 b false]
             [(Int32/TryParse "7" (by-ref i)) (class i) i
              (Double/TryParse "2.5" (by-ref d)) (class d) d
              (Boolean/TryParse "True" (by-ref b)) b]))

;; 泛型 out（Dictionary.TryGetValue），有提示与无提示
(let [d (doto (|System.Collections.Generic.Dictionary`2[System.Object,System.Object]|.) (.Add "a" 1)) v nil]
  (prn :generic (.TryGetValue d "a" (by-ref v)) v (.TryGetValue d "zz" (by-ref v)) v))
(let [^|System.Collections.Generic.Dictionary`2[System.String,System.Int32]| d
      (doto (|System.Collections.Generic.Dictionary`2[System.String,System.Int32]|.) (.Add "k" 5)) v 0]
  (prn :generic-hinted (.TryGetValue d "k" (by-ref v)) v (class v)))

;; ref 形参：传进去的是局部当前的值，调用后是新值
(prn :ref (let [a 41 b 1] [(System.Threading.Interlocked/Increment (by-ref a)) a
                           (System.Threading.Interlocked/Exchange (by-ref b) 10) b]))
(prn :ref-object (let [o :old] [(System.Threading.Interlocked/Exchange (by-ref o) :new) o]))

;; 写回之后的局部不再是常量（常量传播 / 类型推断都按被写处理）
(defn after [] (let [n 0] (Int64/TryParse "5" (by-ref n)) (+ n 1)))
(defn after-hinted [] (let [^long n 0] (Int64/TryParse "7" (by-ref n)) (inc n)))
(defn after-double [] (let [d 0.0] (Double/TryParse "1.5" (by-ref d)) (* d 2.0)))
(prn :after (after) (after-hinted) (after-double))

;; 循环里反复 by-ref、loop 变量本身作 by-ref、fn 形参作 by-ref
(prn :loop (loop [i 0 acc []] (if (< i 4) (let [n 0] (Int32/TryParse (str (* i 7)) (by-ref n)) (recur (inc i) (conj acc n))) acc)))
(defn loopvar [s] (loop [i 0 n 0] (if (< i 3) (do (Int64/TryParse s (by-ref n)) (recur (inc i) (+ n i))) n)))
(defn param [x] (Double/TryParse "2.25" (by-ref x)) x)
(prn :loopvar (loopvar "10") :param (param nil))

;; 被闭包捕获的局部：闭包看到写回后的值；闭包里 by-ref 外层局部
(prn :captured (let [n 0 f (fn [] n)] (Int64/TryParse "99" (by-ref n)) [n (f)]))
(prn :in-closure (let [n 0 g (fn [] (Int64/TryParse "8" (by-ref n)) n)] [(g) n]))

;; deftype 的可变字段、catch 绑定
(deftype Cell [^:volatile-mutable v] clojure.lang.IDeref (deref [_] (Int64/TryParse "123" (by-ref v)) v))
(prn :field @(Cell. 0))
(prn :catch (try (throw (Exception. "boom"))
                 (catch Exception e (let [r (System.Threading.Interlocked/Exchange (by-ref e) nil)] [(.Message ^Exception r) e]))))

;; 被调方法抛出的异常原样传出（不包 TargetInvocationException）
(prn :throws (try (let [taken false] (System.Threading.Monitor/Enter nil (by-ref taken)) taken) (catch Exception e (class e))))
;; 重载有歧义（ref 位置是 nil，四个 Increment 都相容）时报清楚的错误
(prn :ambiguous (try (let [a nil] (System.Threading.Interlocked/Increment (by-ref a))) (catch Exception e (.StartsWith (.Message e) "More than one matching method Increment"))))
;; 没有 by-ref 位置相符的重载
(prn :no-shape (try (let [n 0] (Int64/Parse "1" (by-ref n))) (catch Exception e (class e))))
