;;; BROWSE -- Benchmark to create and browse through
;;; an AI-like data base of units.
;; 原版的表结构（属性表、模式、单元表）都会被 set-cdr! 改写或与改写的结构混用 → 全部用 util 可变序对（空表 nil）；
;; 符号 → 关键字（interned，eq? → identical?）：输入模式与字面模式经私有 ->m 转换（相当于 Scheme 读取器产生序对）；
;; generate-symbol 用 (keyword "<n>")；全局 set! → volatile。
;; 注意：my-match 的 ((null? dat) '()) 在 Scheme 里返回真值，这里返回 Clojure 的 ()（真值）保持同样的控制流。
(ns r7rs.browse
  (:require [r7rs.common :refer [hide read-input run-r7rs-benchmark]]
            [r7rs.util :refer [mcons mpair? mcar mcdr set-mcar! set-mcdr!]]))

(defn- ->m
  "Clojure 数据（读入的表 / 符号）→ 可变序对与关键字。"
  [x]
  (cond (seq? x) (if (seq x) (mcons (->m (first x)) (->m (rest x))) nil)
        (symbol? x) (keyword (name x))
        :else x))

(def ^:private kw-pattern :pattern)
(def ^:private kw-? (keyword "?"))
(def ^:private kw-* (keyword "*"))

(defn- lookup [key table]
  (loop [x table]
    (if (nil? x)
      false
      (let [pair (mcar x)]
        (if (identical? (mcar pair) key)
          pair
          (recur (mcdr x)))))))

(def ^:private properties (volatile! nil))

(defn- get-prop [key1 key2]
  (let [x (lookup key1 @properties)]
    (if x
      (let [y (lookup key2 (mcdr x))]
        (if y
          (mcdr y)
          false))
      false)))

(defn- put-prop [key1 key2 val]
  (let [x (lookup key1 @properties)]
    (if x
      (let [y (lookup key2 (mcdr x))]
        (if y
          (set-mcdr! y val)
          (set-mcdr! x (mcons (mcons key2 val) (mcdr x)))))
      (vreset! properties
               (mcons (mcons key1 (mcons (mcons key2 val) nil)) @properties)))))

(def ^:private current-gensym (volatile! 0))

(defn- generate-symbol []
  (vreset! current-gensym (+ @current-gensym 1))
  (keyword (str @current-gensym)))

(defn- append-to-tail! [x y]
  (if (nil? x)
    y
    (loop [a x b (mcdr x)]
      (if (nil? b)
        (do (set-mcdr! a y)
            x)
        (recur b (mcdr b))))))

(defn- tree-copy [x]
  (if (not (mpair? x))
    x
    (mcons (tree-copy (mcar x))
           (tree-copy (mcdr x)))))

;;; n is # of symbols
;;; m is maximum amount of stuff on the plist
;;; npats is the number of basic patterns on the unit
;;; ipats is the instantiated copies of the patterns

(def ^:private rand-state (volatile! 21))

(defn- init [n m npats ipats]
  (let [ipats (tree-copy ipats)]
    (loop [p ipats]
      (if (nil? (mcdr p))
        (set-mcdr! p ipats)
        (recur (mcdr p))))
    (loop [n n
           i m
           name (generate-symbol)
           a nil]
      (if (= n 0)
        a
        (let [a (mcons name a)]
          (loop [i i]
            (when-not (zero? i)
              (put-prop name (generate-symbol) false)
              (recur (- i 1))))
          (put-prop name
                    kw-pattern
                    (loop [i npats ipats ipats a nil]
                      (if (zero? i)
                        a
                        (recur (- i 1) (mcdr ipats) (mcons (mcar ipats) a)))))
          (loop [j (- m i)]
            (when-not (zero? j)
              (put-prop name (generate-symbol) false)
              (recur (- j 1))))
          (recur (- n 1)
                 (if (zero? i) m (- i 1))
                 (generate-symbol)
                 a))))))

(defn- browse-random []
  (vreset! rand-state (rem (* @rand-state 17) 251))
  @rand-state)

(defn- mlength [x]
  (loop [x x n 0]
    (if (nil? x) n (recur (mcdr x) (+ n 1)))))

(defn- randomize [l]
  (loop [l l a nil]
    (if (nil? l)
      a
      (let [n (rem (browse-random) (mlength l))]
        (if (zero? n)
          (recur (mcdr l) (mcons (mcar l) a))
          (let [a (loop [n n x l]
                    (if (= n 1)
                      (let [a (mcons (mcar (mcdr x)) a)]
                        (set-mcdr! x (mcdr (mcdr x)))
                        a)
                      (recur (- n 1) (mcdr x))))]
            (recur l a)))))))

(defn- massq [k alist]
  (loop [x alist]
    (cond (nil? x) false
          (identical? (mcar (mcar x)) k) (mcar x)
          :else (recur (mcdr x)))))

(defn- mappend [x y]
  (if (nil? x) y (mcons (mcar x) (mappend (mcdr x) y))))

(defn- my-match [pat dat alist]
  (cond (nil? pat)
        (nil? dat)
        (nil? dat) ()
        (or (identical? (mcar pat) kw-?)
            (identical? (mcar pat) (mcar dat)))
        (my-match (mcdr pat) (mcdr dat) alist)
        (identical? (mcar pat) kw-*)
        (or (my-match (mcdr pat) dat alist)
            (my-match (mcdr pat) (mcdr dat) alist)
            (my-match pat (mcdr dat) alist))
        :else
        (cond (not (mpair? (mcar pat)))
              (cond (= (nth (name (mcar pat)) 0) \?)
                    (let [val (massq (mcar pat) alist)]
                      (if val
                        (my-match (mcons (mcdr val) (mcdr pat)) dat alist)
                        (my-match (mcdr pat)
                                  (mcdr dat)
                                  (mcons (mcons (mcar pat) (mcar dat)) alist))))
                    (= (nth (name (mcar pat)) 0) \*)
                    (let [val (massq (mcar pat) alist)]
                      (if val
                        (my-match (mappend (mcdr val) (mcdr pat)) dat alist)
                        (loop [l nil
                               e (mcons nil dat)
                               d dat]
                          (if (or (nil? e)
                                  (my-match (mcdr pat) d (mcons (mcons (mcar pat) l) alist)))
                            (if (nil? e) false true)
                            (recur (append-to-tail! l (mcons (if (nil? d) nil (mcar d)) nil))
                                   (mcdr e)
                                   (if (nil? d) nil (mcdr d)))))))

                    ;; fix suggested by Manuel Serrano
                    ;; (cond did not have an else clause);
                    ;; this changes the run time quite a bit

                    :else false)
              :else (and (mpair? (mcar dat))
                         (my-match (mcar pat) (mcar dat) alist)
                         (my-match (mcdr pat) (mcdr dat) alist)))))

(def ^:private database
  (randomize
   (init 100 10 4 (->m '((a a a b b b b a a a a a b b a a a)
                         (a a b b b b a a
                            (a a) (b b))
                         (a a a b (b a) b a b a))))))

(defn- investigate [units pats]
  (loop [units units]
    (when-not (nil? units)
      (loop [pats pats]
        (when-not (nil? pats)
          (loop [p (get-prop (mcar units) kw-pattern)]
            (when-not (nil? p)
              (my-match (mcar pats) (mcar p) nil)
              (recur (mcdr p))))
          (recur (mcdr pats))))
      (recur (mcdr units)))))

(defn- unit-numbers
  "(map string->number (map symbol->string database))：新的 Clojure 表。"
  [units]
  (if (nil? units)
    ()
    (cons (parse-long (name (mcar units))) (unit-numbers (mcdr units)))))

(defn browse [pats]
  (investigate database pats)
  (unit-numbers database))

(defn -main [& _]
  (let [count (read-input) input1 (->m (read-input)) output (read-input)]
    (run-r7rs-benchmark (str "browse:" count) count
                        (fn [] (browse (hide count input1)))
                        (fn [result] (= result output)))))
