;; cleat-145：小访问器的跨编译单元内联（具体类型守卫 + 字段直读 / 直存）不改变语义；
;; 开关 accinline 是 opt-in（默认关，见 analyzer.clj 的 opt-accinline? 与 notes M16 §8）：
;; 开（CLEAT_OPT_ACCINLINE=1）与关（默认，或 CLEAT_NO_OPT=accinline）两种设置下输出必须逐字相同。
(ns accinline.gen)

(definterface IFoo
  (getA [])
  (getB [])
  (setA [v]))

(deftype Foo [^:unsynchronized-mutable a ^:unsynchronized-mutable b]
  IFoo
  (getA [_] a)
  (getB [_] b)
  (setA [_ v] (set! a v) v))

;; volatile 字段：不做直存特化，读仍走守卫后的字段读（volatile 前缀由决议给出）
(definterface IVol (getA []) (getB []) (setA [v]))
(deftype Vol [^:volatile-mutable v]
  IVol
  (getA [_] v)
  (getB [_] nil)
  (setA [_ x] (set! v x) x))

;; 原始字段（^long）：直存前要有与 (set! 字段) 相同的入口转换，读出来是装箱的 long
(definterface IJ (getN []) (getB []) (setN [v]))
(deftype J [^:unsynchronized-mutable ^long n]
  IJ
  (getN [_] n)
  (getB [_] nil)
  (setN [_ x] (set! n x) x))

;; 同一个接口方法有两个不同实现：冲突的 key 被删掉，调用点保持接口调用
(definterface IM (getV []) (getB []) (setV [v]))
(deftype M1 [^:unsynchronized-mutable x] IM (getV [_] x) (getB [_] nil) (setV [_ v] (set! x v) v))
(deftype M2 [^:unsynchronized-mutable y] IM (getV [_] y) (getB [_] nil) (setV [_ v] (set! y v) v))

;; 方法体不是「一个字段」或「存一个字段并返回新值」：不识别
(definterface IK (sum []) (getB []) (setA [v]))
(deftype K [^:unsynchronized-mutable a ^:unsynchronized-mutable b]
  IK
  (sum [_] (+ a b))
  (getB [_] b)
  (setA [_ v] (set! a v) (inc a)))

(def log (atom []))
(defn note [k x] (swap! log conj k) x)
(defn mk-foo [x y] (note :mk (+ x y)) (Foo. x y))

(ns accinline.use
  (:require [accinline.gen :as g])
  (:import (accinline.gen IFoo Foo IVol Vol IJ J IM M1 M2 IK K)))

(defn show [label f]
  (println label (try (pr-str (f)) (catch Exception e (str "<" (.GetType e) ":" (.Message e) ">")))))

(defn ga [p] (.getA ^IFoo p))
(defn gb [p] (.getB ^IFoo p))
(defn sa! [p v] (.setA ^IFoo p v))
(defn va [p] (.getA ^IVol p))
(defn sv! [p v] (.setA ^IVol p v))
(defn jn [p] (.getN ^IJ p))
(defn sj! [p v] (.setN ^IJ p v))
(defn mv [p] (.getV ^IM p))
(defn sm! [p v] (.setV ^IM p v))
(defn ks [p] (.sum ^IK p))
(defn call-twice [p] [(ga p) (ga p)])

(show "read-write" #(let [o (Foo. 1 2)] [(ga o) (gb o) (sa! o 9) (ga o) (gb o) (.-a ^Foo o)]))
(show "two-reads-same-object" #(let [o (Foo. 3 4)] (call-twice o)))
(show "receiver-once" #(do (reset! g/log []) [(count @g/log) (sa! (g/mk-foo 1 2) 5) (count @g/log) @g/log]))
(show "value-once" #(do (reset! g/log [])
                        (let [o (Foo. 1 2)] [(sa! o (g/note :v 7)) (ga o) @g/log])))
(show "arg-order" #(do (reset! g/log [])
                       (sa! (g/note :recv (Foo. 1 2)) (g/note :val 3))
                       @g/log))
(show "nil-receiver" #(ga nil))
(show "nil-setter" #(sa! nil 1))
(show "volatile" #(let [v (Vol. 5)] [(va v) (sv! v 8) (va v) (sv! (Vol. 1) nil)]))
(show "prim-field" #(let [j (J. 5)] [(jn j) (sj! j 7) (jn j) (instance? System.Int64 (jn j))]))
(show "prim-nil" #(jn nil))
(show "prim-coerce-error" #(sj! (J. 1) :kw))
(show "two-impls" #(let [a (M1. 1) b (M2. 2)] [(mv a) (mv b) (sm! a 3) (sm! b 4) (mv a) (mv b)]))
(show "not-accessor" #(let [k (K. 1 2)] [(ks k) (.setA ^IK k 9) (ks k)]))
(show "same-interface-other-type" #(let [v (Vol. 6)] (va v)))
(show "concrete-hint-unaffected" #(let [o (Foo. 1 2)] [(.getA ^IFoo o) (.-a ^Foo o)]))
(show "loop-mutation" #(let [o (Foo. 0 0)]
                         (loop [i 0 acc []]
                           (if (= i 4) acc
                             (recur (inc i) (conj acc (sa! o (+ (ga o) i)) (ga o)))))))
(defn before [p] (ga p))
(show "redefine-between-rounds" #(let [o (Foo. 1 2)]
                                   (alter-var-root #'ga (constantly (fn [p] (inc (.getA ^IFoo p)))))
                                   [(before o) (ga o)]))
