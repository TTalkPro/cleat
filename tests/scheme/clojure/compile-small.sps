;;; cleat-051：Clojure 版的 compile-small——几十个小程序经 (clojure syntax) 展开 → Scheme 编译器（Clojure 节点）→ CIL → 运行，
;;; 比较结果的 Clojure 打印形式。覆盖：闭包、多 arity、变长、命名 fn 递归、loop / recur 一百万次不增栈、letfn 互递归、
;;; Var 重定义对已编译调用者可见、动态 Var 的线程绑定、关键字调用、互操作、deftype* / reify*（含 C# 调用生成的方法）、
;;; try / catch / finally 的顺序；以及编译结果的形状（Lcore 节点、Lcil 里的 IFn.invoke、没有 tail.、没有过程常量）。
;;; clojure.core 未加载：用到的函数在下面用 def + fn* + 互操作定义（这本身也是被测的编译路径）。
(import (rnrs) (rnrs eval) (ironscheme) (ironscheme clr) (ironscheme compiler)
        (clojure syntax-kinds) (clojure namespaces) (clojure syntax) (clojure compile))

(clr-static-call clojure.lang.RT Init)

(define failures 0)
(define checks 0)
(define (fail! name expected got)
  (set! failures (+ failures 1))
  (display "FAIL: ") (display name) (newline)
  (display "  expected: ") (write expected) (newline)
  (display "  got:      ") (write got) (newline))
(define (clr-message e)
  (if (clr-is System.Exception e) (clr-call System.Exception get_Message e) e))
(define (run thunk)
  (with-clr-exception-handler
    (lambda (e) (list 'exception (clr-message e)))
    (lambda ()
      (guard (e (#t (list 'condition (if (message-condition? e) (condition-message e) e))))
        (thunk)))))
(define (check name thunk expected)
  (set! checks (+ checks 1))
  (let ((got (run thunk)))
    (unless (equal? got expected) (fail! name expected got))))
(define (check-pred name thunk pred)
  (set! checks (+ checks 1))
  (let ((got (run thunk)))
    (unless (pred got) (fail! name "(predicate)" got))))

(define (read-all s)
  (let ((r (clr-new clojure.lang.LineNumberingTextReader (clr-new System.IO.StringReader s)))
        (eof (clr-new System.Object)))
    (let loop ((acc '()))
      (let ((x (clr-static-call clojure.lang.LispReader
                 (read clojure.lang.PushbackTextReader System.Boolean System.Object System.Boolean System.Object)
                 r #f eof #f (clj-map))))
        (if (eq? x eof) (reverse acc) (loop (cons x acc)))))))
(define (read1 s) (car (read-all s)))
(define (str x) (clr-static-call clojure.lang.RT (printString System.Object) x))
(define (ev s) (let loop ((fs (read-all s)) (last #f)) (if (null? fs) last (loop (cdr fs) (clojure-eval (car fs))))))
(define (string-after s sub)
  (let ((n (string-length s)) (k (string-length sub)))
    (let loop ((i 0)) (cond ((> (+ i k) n) #f) ((string=? (substring s i (+ i k)) sub) (substring s i n)) (else (loop (+ i 1)))))))
(define (string-contains? s sub)
  (let ((n (string-length s)) (k (string-length sub)))
    (let loop ((i 0)) (cond ((> (+ i k) n) #f) ((string=? (substring s i (+ i k)) sub) #t) (else (loop (+ i 1)))))))

;;; (t 名字 源码 期望的打印形式)：求值全部形式，比较最后一个值的 Clojure 打印形式
(define-syntax t
  (syntax-rules () ((_ name src expected) (check name (lambda () (str (ev src))) expected))))
(define-syntax t-err-has
  (syntax-rules ()
    ((_ name src sub)
     (check-pred name (lambda () (ev src))
       (lambda (got) (and (pair? got) (eq? (car got) 'exception) (string-contains? (cadr got) sub)))))))

;;; 编译结果的形状。Lcil 文本经常量编码：引用 (clojure compile) 的变量，库体才执行、Clojure 常量的编码器才登记
(define clojure-encoder encode-clojure-constant)
(define (lcore src) (clojure-expand (read1 src)))
(define (lcil-text src) (compile->lcil-text (list (lcore src)) "shape" "#Module"))
(define (tree-exists? pred x)
  (let walk ((x x))
    (or (pred x)
        (and (pair? x) (or (walk (car x)) (walk (cdr x))))
        (and (vector? x) (exists walk (vector->list x))))))

(define source-path-var (clr-static-field-get clojure.lang.RT SourcePathVar))

(with-ns-binding (find-ns 'user)
 (lambda ()
  (clr-static-call clojure.lang.Var pushThreadBindings (clj-map source-path-var "small.clj"))
  (in-ns! 'cleat.small.core)
  (ev "(def identity (fn* identity [x] x))
       (def instance? (fn* instance? [t x] (. t (IsInstanceOfType x))))
       (def = (fn* = [a b] (. clojure.lang.Util (equiv a b))))
       (def identical? (fn* identical? [a b] (. Object (ReferenceEquals a b))))
       (def inc (fn* inc [x] (. clojure.lang.Numbers (inc x))))
       (def dec (fn* dec [x] (. clojure.lang.Numbers (dec x))))
       (def < (fn* < [a b] (. clojure.lang.Numbers (lt a b))))
       (def + (fn* + [a b] (. clojure.lang.Numbers (add a b))))
       (def - (fn* - [a b] (. clojure.lang.Numbers (minus a b))))
       (def * (fn* * [a b] (. clojure.lang.Numbers (multiply a b))))
       (def first (fn* first [x] (. clojure.lang.RT (first x))))
       (def next (fn* next [x] (. clojure.lang.RT (next x))))
       (def count (fn* count [x] (. clojure.lang.RT (count x))))
       (def nth (fn* nth [x i] (. clojure.lang.RT (nth x i))))
       (def conj (fn* conj [c x] (. clojure.lang.RT (conj c x))))
       (def cons (fn* cons [x s] (. clojure.lang.RT (cons x s))))
       (def meta (fn* meta [x] (. clojure.lang.RT (meta x))))
       (def with-meta (fn* with-meta [x m] (. x (withMeta m))))
       (def class (fn* class [x] (if x (. x (GetType)) nil)))
       (def vec (fn* vec [a] (. clojure.lang.LazilyPersistentVector (create a))))
       (def deref (fn* deref [r] (. r deref)))
       (def push! (fn* push! [a x] (. a (swap (fn* [v] (conj v x))))))")
  (in-ns! 'cleat.small)
  (apply refer! 'cleat.small.core
         '(identity instance? = identical? inc dec < + - * first next count nth conj cons meta with-meta class vec deref push!))

  ;; ———————— 字面量与集合 ————————
  (t "integer literals are longs" "(. (class 1) FullName)" "\"System.Int64\"")
  (t "non-constant collections are built at run time" "(let* [a 1 b 2] [a {:k b} #{a} (identity [a b])])" "[1 {:k 2} #{1} [1 2]]")
  (t "metadata on a non-constant vector" "(let* [a 1] (meta ^{:m a} [a]))" "{:m 1}")

  ;; ———————— 闭包 ————————
  (t "closure captures locals" "(let* [a 1 b 2] ((fn* [c] [a b c]) 3))" "[1 2 3]")
  (t "nested closures" "(let* [a 1] (((fn* [b] (fn* [c] [a b c])) 2) 3))" "[1 2 3]")
  (t "closures capture each loop iteration"
     "(let* [fs (loop* [i 0 acc []] (if (< i 3) (recur (inc i) (conj acc (fn* [] i))) acc))] [((nth fs 0)) ((nth fs 2)) (count fs)])"
     "[0 2 3]")
  (t "fn called back from C# (Atom.swap)" "(let* [a (new clojure.lang.Atom 0) bump (fn* [] (. a (swap inc)))] (bump) (bump) (deref a))" "2")
  (t "closure returned from a def'd fn" "(def adder (fn* [n] (fn* [x] (+ x n)))) ((adder 10) 5)" "15")

  ;; ———————— arity ————————
  (ev "(def ma (fn* ([] :zero) ([a] [:one a]) ([a b] [:two a b]) ([a b c & r] [:many a b c r])))")
  (t "multi-arity dispatch" "[(ma) (ma 1) (ma 1 2) (ma 1 2 3) (ma 1 2 3 4 5)]" "[:zero [:one 1] [:two 1 2] [:many 1 2 3 nil] [:many 1 2 3 (4 5)]]")
  (t "variadic only" "[((fn* [& r] r)) ((fn* [& r] r) 1 2)]" "[nil (1 2)]")
  (t "applyTo passes an infinite tail to the variadic clause"
     "(first (. (fn* [a & r] r) (applyTo (cons 1 (. clojure.lang.Repeat (create 5))))))" "5")
  (t "more than 20 arguments (IFn params overload)"
     "((fn* [& r] (count r)) 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15 16 17 18 19 20 21 22 23 24 25)" "25")
  (t "20 fixed parameters"
     "((fn* [a1 a2 a3 a4 a5 a6 a7 a8 a9 a10 a11 a12 a13 a14 a15 a16 a17 a18 a19 a20] [a1 a20]) 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15 16 17 18 19 20)"
     "[1 20]")
  (t "variadic with 20 required parameters"
     "((fn* [a1 a2 a3 a4 a5 a6 a7 a8 a9 a10 a11 a12 a13 a14 a15 a16 a17 a18 a19 a20 & r] [a20 r]) 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15 16 17 18 19 20 21 22)"
     "[20 (21 22)]")
  (t-err-has "arity error names the fn" "(def ma2 (fn* ([] 0) ([a] a))) (ma2 1 2 3)" "Wrong number of args (3) passed to: cleat.small/ma2")
  (t-err-has "arity error of a variadic fn below the required count" "(ma 1 2 3 4) ((fn* vv [a b & r] r) 1)" "Wrong number of args (1) passed to: cleat.small/vv")
  (t "HasArity" "[(. ma (HasArity 2)) (. ma (HasArity 7)) (. ma (HasArity -1)) (. (fn* [a] a) (HasArity 2))]" "[true true false false]")
  (t "fn classes derive from AFunction / RestFn"
     "[(instance? clojure.lang.RestFn ma) (instance? clojure.lang.AFunction (fn* [] 1)) (instance? clojure.lang.RestFn (fn* [] 1)) (instance? clojure.lang.Fn ma)]"
     "[true true false true]")
  (t "fn metadata" "[(meta (with-meta (fn* [] 1) {:a 1})) (meta ^{:b 2} (fn* [] 1)) ((with-meta (fn* [x] x) {:a 1}) 7)]" "[{:a 1} {:b 2} 7]")
  (t "fn class name follows Clojure" "(. (class ma) FullName)" "\"cleat.small$ma\"")

  ;; ———————— 递归 ————————
  (t "named fn recursion" "((fn* fact [n] (if (< n 2) 1 (* n (fact (dec n))))) 20)" "2432902008176640000")
  (t "recursion through a Var" "(def fib (fn* [n] (if (< n 2) n (+ (fib (- n 1)) (fib (- n 2)))))) (fib 20)" "6765")
  (t "the self name is the fn itself" "(let* [f (fn* me [] me)] (identical? f (f)))" "true")

  ;; ———————— loop / recur ————————
  (t "loop* one million iterations" "(loop* [i 0] (if (< i 1000000) (recur (inc i)) i))" "1000000")
  (t "recur to the fn head one million times" "((fn* [i acc] (if (< i 1000000) (recur (inc i) (+ acc 1)) acc)) 0 0)" "1000000")
  (t "recur in a variadic clause passes the rest seq" "((fn* [n & r] (if (< n 3) (recur (inc n) (cons n r)) r)) 0)" "(2 1 0)")
  (t "nested loops" "(loop* [i 0 acc 0] (if (< i 100) (recur (inc i) (loop* [j 0 a acc] (if (< j 100) (recur (inc j) (inc a)) a))) acc))" "10000")

  ;; ———————— letfn* ————————
  (t "letfn* mutual recursion"
     "(letfn* [ev? (fn* ev? [n] (if (= n 0) true (od? (dec n)))) od? (fn* od? [n] (if (= n 0) false (ev? (dec n))))] [(ev? 100) (od? 7) (ev? 7)])"
     "[true true false]")

  ;; ———————— Var ————————
  (t "Var redefinition is visible to compiled callers" "(def v1 1) (def get-v1 (fn* [] v1)) (def v1 2) (get-v1)" "2")
  (t "fn redefinition is visible to compiled callers" "(def g (fn* [] 1)) (def call-g (fn* [] (g))) (def g (fn* [] 2)) (call-g)" "2")
  (ev "(def ^:dynamic *d* 1) (def read-d (fn* [] *d*))")
  (t "dynamic Var thread binding"
     "(do (. clojure.lang.Var (pushThreadBindings {(var *d*) 5})) (try [(read-d) *d*] (finally (. clojure.lang.Var (popThreadBindings)))))" "[5 5]")
  (t "dynamic Var root after the binding" "(read-d)" "1")
  (t "set! of a bound dynamic Var"
     "(do (. clojure.lang.Var (pushThreadBindings {(var *d*) 5})) (try (set! *d* 6) (read-d) (finally (. clojure.lang.Var (popThreadBindings)))))" "6")
  (t "def without init keeps the root" "(def w 3) (def w) w" "3")
  (t "def sets dynamic at run time" "(. (var *d*) isDynamic)" "true")
  (t "def of a fn names it" "(def nm (fn* [] 1)) (. (class nm) FullName)" "\"cleat.small$nm\"")

  ;; ———————— 关键字调用与其他 IFn ————————
  (t "keyword invoke" "[(:a {:a 1}) (:a nil) (:a {:b 1} :dflt) (:a 5) (:a #{:a}) (let* [k {:a 2}] (:a k))]" "[1 nil :dflt nil :a 2]")
  (t "keyword invoke on an ILookup deftype"
     "(deftype* L cleat.small.L [] :implements [clojure.lang.ILookup] (valAt [_ k] [:got k]) (valAt [_ k nf] nf))
      [(:x (new cleat.small.L)) (:x (new cleat.small.L) 9)]" "[[:got :x] 9]")
  (t "collections, Vars and keywords as functions" "[([10 20] 1) ({:a 1} :a) (#{:x} :x) ((var inc) 1) (:k {:k 3})]" "[20 1 :x 2 3]")
  ;; Scheme 过程不会作为 Clojure 函数值出现；万一出现（这里由 Scheme 侧硬塞进 Var），调用与调用非函数一样抛 InvalidCastException
  (clr-call clojure.lang.Var bindRoot (ev "(def sp nil)") car)
  (t-err-has "a Scheme procedure is not a Clojure fn" "(sp 1)" "to type 'clojure.lang.IFn'")

  ;; ———————— 互操作 ————————
  (t "static, instance, property, new" "[(. Math (Max 1 5)) (. \"abc\" ToUpper) (. \"abc\" Length) (. (. (new System.Text.StringBuilder \"a\") (Append \"b\")) ToString)]" "[5 \"ABC\" 3 \"ab\"]")
  (t "instance? with a literal type" "[(instance? String \"s\") (instance? String 1)]" "[true false]")
  (t "int results are longs" "(. (class (. \"abc\" Length)) FullName)" "\"System.Int64\"")

  ;; ———————— deftype* / reify* ————————
  (t "deftype* volatile-mutable field"
     "(deftype* V cleat.small.V [^:volatile-mutable n] :implements [clojure.lang.IDeref] (deref [_] (set! n (inc n)) n))
      (let* [v (new cleat.small.V 0)] [(. v deref) (. v deref) (. (. (. (class v) (GetField \"n\")) (GetRequiredCustomModifiers)) Length)])"
     "[1 2 1]")
  (t "getBasis keeps field metadata" "[(. cleat.small.V getBasis) (meta (first (. cleat.small.V getBasis)))]" "[[n] {:volatile-mutable true}]")
  (t "deftype methods called from C#: Counted / Indexed with int parameters and results"
     "(deftype* IX cleat.small.IX [k] :implements [clojure.lang.Indexed] (nth [_ i] (* i k)) (nth [_ i nf] nf) (count [_] 3))
      [(. clojure.lang.RT (nth (new cleat.small.IX 2) 5)) (. clojure.lang.RT (count (new cleat.small.IX 2))) (. clojure.lang.RT (nth (new cleat.small.IX 2) 9 :nf))]"
     "[10 3 :nf]")
  (t "reify* IComparer used by Array.Sort"
     "(let* [arr (. clojure.lang.RT (toArray [3 1 2]))
             cmp (reify* [System.Collections.IComparer] (Compare [_ a b] (. clojure.lang.Numbers (compare b a))))]
        (. Array (Sort arr cmp))
        (vec arr))"
     "[3 2 1]")
  (t "deftype* Equals / GetHashCode overrides"
     "(deftype* E cleat.small.E [k] :implements [] (Equals [_ o] (= k (. o -k))) (GetHashCode [_] 42))
      [(= (new cleat.small.E 1) (new cleat.small.E 1)) (= (new cleat.small.E 1) (new cleat.small.E 2)) (. (new cleat.small.E 1) GetHashCode)]"
     "[true false 42]")
  (t "deftype* methods can refer to the type being defined"
     "(deftype* SR cleat.small.SR [x] :implements [clojure.lang.IDeref] (deref [this] (if (< x 3) (deref (new cleat.small.SR (inc x))) [x (instance? cleat.small.SR this)])))
      (deref (new cleat.small.SR 0))"
     "[3 true]")
  (t-err-has "an interface method without an implementation throws"
     "(deftype* S cleat.small.S [] :implements [clojure.lang.Counted]) (count (new cleat.small.S))"
     "clojure.lang.Counted.count is not implemented by cleat.small.S")
  (t "a method bridges the same-named method of a super-interface"
     "(deftype* Q cleat.small.Q [] :implements [clojure.lang.ISeq] (^clojure.lang.ISeq cons [_ o] (. clojure.lang.RT (list o))))
      (conj (new cleat.small.Q) 1)"
     "(1)")
  (t "recur to a method head"
     "(deftype* RC cleat.small.RC [] :implements [clojure.lang.Indexed] (nth [this i] (if (< i 10) (recur (inc i)) i)) (nth [_ i nf] nf) (count [_] 0))
      (. clojure.lang.RT (nth (new cleat.small.RC) 3))"
     "10")
  (t "reify* captures locals; withMeta keeps them"
     "(let* [a 1 b 2 r (reify* [clojure.lang.IDeref] (deref [_] [a b]))] (let* [r2 (with-meta r {:m 1})] [(deref r2) (meta r2) (meta r)]))"
     "[[1 2] {:m 1} nil]")
  (t "reify* in a loop captures each iteration"
     "(let* [rs (loop* [i 0 acc []] (if (< i 3) (recur (inc i) (conj acc (reify* [clojure.lang.IDeref] (deref [_] i)))) acc))] [(deref (nth rs 0)) (deref (nth rs 2))])"
     "[0 2]")
  (t "deftype* redefinition: the newest type wins"
     "(deftype* R cleat.small.R [] :implements [clojure.lang.IDeref] (deref [_] 1)) (deref (new cleat.small.R))" "1")
  (t "deftype* redefinition: the newest type wins (2)"
     "(deftype* R cleat.small.R [] :implements [clojure.lang.IDeref] (deref [_] 2)) (deref (new cleat.small.R))" "2")
  (t "deftype* method reads fields of another instance through interop"
     "(deftype* PT cleat.small.PT [x y] :implements [clojure.lang.IDeref] (deref [this] (+ x (. this -y)))) (deref (new cleat.small.PT 1 2))" "3")

  ;; ———————— case*（编译为 IL switch）————————
  (ev "(def sel (fn* [x] (case* x 0 0 :d {1 [1 :one] 2 [2 :two] 3 [3 :three]} :compact :int)))")
  (t "case* :int compact" "[(sel 1) (sel 3) (sel 4) (sel 2.0) (sel \"x\") (sel nil)]" "[:one :three :d :d :d :d]")
  (t "case* :int sparse keys" "(let* [f (fn* [x] (case* x 0 0 :d {5 [5 :five] 1000 [1000 :k] -7 [-7 :neg]} :sparse :int))] [(f 5) (f 1000) (f -7) (f 6)])" "[:five :k :neg :d]")
  (let ((h (lambda (src) (number->string (clr-static-call clojure.lang.Util (hash System.Object) (read1 src))))))
    (t "case* :hash-equiv on keywords and strings"
       (string-append "(let* [f (fn* [x] (case* x 0 0 :none {" (h ":a") " [:a 1] " (h ":b") " [:b 2] " (h "\"s\"") " [\"s\" 3]} :sparse :hash-equiv))] [(f :a) (f :b) (f \"s\") (f :c) (f 1)])")
       "[1 2 3 :none :none]")
    (t "case* skip-check entry (hash collision handled by the branch)"
       (string-append "(let* [f (fn* [x] (case* x 0 0 :none {" (h ":a") " [:a :collision]} :sparse :hash-equiv #{" (h ":a") "}))] [(f :a)])")
       "[:collision]")
    (t "case* :hash-identity"
       (string-append "(let* [f (fn* [x] (case* x 0 0 :none {" (h ":a") " [:a 1]} :sparse :hash-identity))] [(f :a) (f :b)])")
       "[1 :none]"))
  (t "case* branches are in tail position (recur inside a branch)"
     "(loop* [i 0 acc 0] (case* i 0 0 (recur (inc i) (+ acc 1)) {100000 [100000 acc]} :compact :int))" "100000")

  ;; ———————— try / catch / finally ————————
  (t "typed catches pick the matching clause"
     "(try (throw (new System.ArgumentException \"a\")) (catch System.InvalidOperationException e :ioe) (catch System.ArgumentException e (. e Message)) (catch Exception e :ex))"
     "\"a\"")
  (t "catch falls back to a more general type" "(try (throw (new System.InvalidOperationException \"x\")) (catch System.ArgumentException e :arg) (catch Exception e :ex))" ":ex")
  (t "body, catch, finally run in order"
     "(let* [log (new clojure.lang.Atom [])]
        (try (push! log :body) (throw (new Exception \"x\")) (catch Exception e (push! log :catch)) (finally (push! log :finally)))
        (deref log))"
     "[:body :catch :finally]")
  (t "finally runs on normal exit; the value is the body's"
     "(let* [log (new clojure.lang.Atom [])] [(try 1 (finally (push! log :f))) (deref log)])" "[1 [:f]]")
  (t "an uncaught exception passes through the inner finally"
     "(let* [log (new clojure.lang.Atom [])]
        [(try (try (throw (new Exception \"in\")) (finally (push! log :inner))) (catch Exception e (. e Message))) (deref log)])"
     "[\"in\" [:inner]]")
  (t "try in operand position (lifted)" "(+ 1 (try 2 (finally nil)))" "3")
  (t "CLR exceptions from interop are caught" "(try (. \"abc\" (Substring 10)) (catch ArgumentOutOfRangeException e :oob))" ":oob")
  (t "catch inside a fn body, rethrow to the caller"
     "(def rethrow (fn* [] (try (throw (new Exception \"r\")) (catch Exception e (throw e))))) (try (rethrow) (catch Exception e (. e Message)))" "\"r\"")

  ;; ———————— 编译结果的形状 ————————
  (check "Var reference and call are compiler nodes"
    (lambda () (lcore "(get-v1 v1)"))
    (list '$clj-invoke (list '$clj-var-ref (list 'quote (ev "(var get-v1)")) ''#f) (list '$clj-var-ref (list 'quote (ev "(var v1)")) ''#f)))
  (check "dynamic Var reference uses get" (lambda () (lcore "*d*")) (list '$clj-var-ref (list 'quote (ev "(var *d*)")) ''#t))
  (check "keyword invoke node" (lambda () (car (lcore "(:a v1)"))) '$clj-keyword-invoke)
  (check "no procedure constants in Clojure Lcore"
    (lambda ()
      (filter (lambda (src) (tree-exists? procedure? (lcore src)))
              '("(def z 1)" "(fn* [a & r] [a r])" "(let* [x 1] {:a x})" "(. \"a\" (Substring 0))" "(new Object)"
                "(try 1 (catch Exception e 2) (finally 3))" "(deftype* T2 cleat.small.T2 [a] :implements [clojure.lang.IDeref] (deref [_] a))"
                "(reify* [clojure.lang.IDeref] (deref [_] 1))" "(monitor-enter v1)" "(set! *d* 2)" "(instance? String 1)")))
    '())
  (check-pred "IFn.invoke(k) in the Lcil, without tail."
    (lambda () (lcil-text "(fn* [f] (f 1 2))"))
    (lambda (s) (and (string? s) (string-contains? s "(callvirt \"clojure.lang.IFn\" \"invoke\" (\"System.Object\" \"System.Object\") \"System.Object\")")
                     (not (string-contains? s "(tail)")))))
  (check-pred "fn* is an AFunction subclass in the Lcil"
    (lambda () (lcil-text "(fn* named [a] a)"))
    (lambda (s) (and (string? s) (string-contains? s "(base \"clojure.lang.AFunction\")") (string-contains? s "(method \"invoke\""))))
  ;; catch 的处理器是 Scheme 过程类（它的 Invoke1 转发可以有 tail.，不在 try 区域内）；fn 类（含 try 区域）里没有 tail.
  (check-pred "case* compiles to an IL switch"
    (lambda () (lcil-text "(fn* [x] (case* x 0 0 :d {1 [1 :one] 2 [2 :two]} :compact :int))"))
    (lambda (s) (and (string? s) (string-contains? s "(switch (") (string-contains? s "\"IsNonCharNumeric\""))))
  (check-pred "no tail. inside a Clojure try"
    (lambda () (lcil-text "(fn* [f] (try (f 1) (catch Exception e (f 2)) (finally (f 3))))"))
    (lambda (s)
      (let ((fn-class (and (string? s) (string-after s "(type \"cleat.small$fn__"))))
        (and fn-class (string-contains? fn-class "(try") (string-contains? fn-class "(finally")
             (not (string-contains? fn-class "(tail)"))))))

  (clr-static-call clojure.lang.Var popThreadBindings)))

(display checks) (display " checks, ") (display failures) (display " failures") (newline)
(unless (= failures 0) (exit 1))
