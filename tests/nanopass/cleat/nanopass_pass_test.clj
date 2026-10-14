;; cleat.nanopass pass 层的测试（cleat-084）：define-pass、nanopass-case、with-output-language、模板、catamorphism、自动补全。
(ns cleat.nanopass-pass-test
  (:require [clojure.test :refer [deftest is testing]]
            [cleat.nanopass :as np :refer [define-language define-pass nanopass-case with-output-language out in-context]]))

(defn datum? [x] true)

(define-language Lp
  (terminals (symbol (x)) (datum (d)))
  (entry Expr)
  (Expr (e body)
    x
    (quote d)
    (if e0 e1 e2)
    (begin e* ... e)
    (set! x e)
    le
    (letrec ([x* le*] ...) body)
    (call e e* ...)
    (opt (maybe e)))
  (LambdaExpr (le)
    (lambda (x* ...) (maybe x) body)))

(define-language Lq (extends Lp)
  (Expr (e body)
    (- (set! x e))
    (+ (set-box! x e) (unbox x))))

(defn P [x] (np/parse Lp x))
(defn U [x] (np/unparse x))

;; —— 复制与自动处理器 ——

(define-pass identity-pass Lp [ir] -> Lp [])

(deftest auto-generated-processors-copy
  (let [src '(letrec ((f (lambda (a b) r (begin (set! a (quote 1)) (call f a b r)))))
               (if (opt nil) (opt x) (call f (quote 2) y)))
        n (P src)
        m (identity-pass n)]
    (is (= src (U m)))
    (is (not (identical? n m)) "复制出新节点")))

(define-pass boxify Lp [ir boxed] -> Lq []
  (Expr [ir] -> Expr []
    [~x (if (contains? boxed x) (out (unbox ~x)) x)]
    [(set! ~x ~[e]) (out (set-box! ~x ~e))]))

(deftest explicit-clauses-and-copies
  (is (= '(letrec ((f (lambda (a) nil (begin (set-box! a (quote 1)) (unbox a))))) (call f b))
         (U (boxify (P '(letrec ((f (lambda (a) nil (begin (set! a (quote 1)) a)))) (call f b))) #{'a}))))
  (is (thrown-with-msg? Exception #"expected Lp Expr" (boxify (np/parse Lq '(unbox a)) #{})) "入口检查输入语言"))

;; 经非终结符交替到达的产生式交给该非终结符的处理器（attach 的形状）
(define-pass rename-lambdas Lp [ir] -> Lp []
  (LambdaExpr [ir] -> LambdaExpr []
    [(lambda (~x* ...) ~x ~[body]) (out (lambda (~(map #(symbol (str % "'")) x*) ...) ~x ~body))]))

(deftest nonterminal-alternative-delegates
  (is (= '(call (lambda (a' b') nil (call (lambda (c') nil c) a)) (quote 1))
         (U (rename-lambdas (P '(call (lambda (a b) nil (call (lambda (c) nil c) a)) (quote 1))))))))

;; 额外形参：catamorphism 与复制子句按名字传
(define-pass depth-tag Lp [ir] -> Lp []
  (Expr [ir depth] -> Expr []
    [(quote ~d) (out (quote ~[d depth]))])
  (LambdaExpr [ir depth] -> LambdaExpr []
    [(lambda (~x* ...) ~x ~body) (out (lambda (~x* ...) ~x ~(Expr body (inc depth))))])
  (Expr ir 0))

(deftest extra-formals-are-passed-along
  (is (= '(begin (quote [a 0]) (lambda () nil (if (quote [b 1]) (lambda () nil (quote [c 2])) x)))
         (U (depth-tag (P '(begin (quote a) (lambda () nil (if (quote b) (lambda () nil (quote c)) x)))))))))

(deftest missing-extra-formal-is-reported
  (let [msg (try (macroexpand-1 '(cleat.nanopass/define-pass bad cleat.nanopass-pass-test/Lp [ir] -> cleat.nanopass-pass-test/Lp []
                                   (Expr [ir k] -> Expr [])
                                   (LambdaExpr [ir] -> LambdaExpr [])))
                 nil (catch Exception e (.Message e)))]
    (is (re-find #"needs k" (str msg)))))

;; —— 模式 ——

(defn classify [e]
  (nanopass-case [Lp Expr] e
    [(quote 0) :zero]                                     ; 字面量
    [(quote ~d) (guard (string? d)) :string]              ; guard 失败时试下一个子句
    [(quote ~d) :datum]
    [(call (lambda (~x* ...) ~r ~body) ~e* ...) [:let x* e*]]   ; 嵌套的产生式
    [(call ~f) [:thunk-call f]]                           ; 省略号区段里 0 个单项
    [(call ~f ~a ~b) [:call2 f a b]]                      ; 2 个单项（长度必须相等）
    [(call ~f ~a ~rest ...) [:call+ f a rest]]            ; 单项 + 整段
    [(begin ~e* ... ~last) [:begin (count e*) last]]
    [(letrec ([~f ~le]) ~body) [:letrec1 f]]
    [(letrec ([~f* ~le*] ...) ~body) [:letrec f*]]
    [~x [:var x]]
    [~le :lambda]
    [else :other]))

(deftest pattern-matching
  (is (= :zero (classify (P '(quote 0)))))
  (is (= :string (classify (P '(quote "s")))))
  (is (= :datum (classify (P '(quote :k)))))
  (is (= '[:let [a] [(quote 1)]] (update (update (classify (P '(call (lambda (a) nil a) (quote 1)))) 1 vec) 2 #(mapv U %))))
  (is (= '[:thunk-call g] (classify (P '(call g)))))
  (is (= '[:call2 g a b] (classify (P '(call g a b)))))
  (is (= '[:call+ g a [b c]] (update (classify (P '(call g a b c))) 3 vec)))
  (is (= '[:begin 2 c] (classify (P '(begin a b c)))))
  (is (= '[:letrec1 f] (classify (P '(letrec ((f (lambda () nil x))) f)))))
  (is (= '[:letrec [f g]] (update (classify (P '(letrec ((f (lambda () nil x)) (g (lambda () nil y))) f))) 1 vec)))
  (is (= '[:var v] (classify 'v)))
  (is (= :lambda (classify (P '(lambda () nil x)))))
  (is (= :other (classify (P '(opt nil))))))

(deftest nanopass-case-without-match
  (is (thrown-with-msg? Exception #"no clause matches"
        (nanopass-case [Lp Expr] (P '(quote 1)) [(if ~a ~b ~c) 1]))))

(defn count-vars [e]
  (nanopass-case [Lp Expr] e
    [~x 1]
    [(if ~[a] ~[b] ~[c]) (+ a b c)]
    [(begin ~[e*] ... ~[e]) (apply + e e*)]
    [(call ~[e] ~[e*] ...) (apply + e e*)]
    [(opt ~[e]) (or e 0)]                                 ; maybe 字段的 catamorphism：无值原样
    [else 0]))

(deftest nanopass-case-catamorphism
  (is (= 5 (count-vars (P '(if a (begin b c) (call d e (opt nil) (quote 1))))))))

;; 子模式的整段接入（letrec 的 [x* le*]）与多层 catamorphism
(define-pass lambda-arity Lp [ir] -> * []
  (Expr [ir] -> * []
    [(letrec ([~x* ~[le*]] ...) ~[body]) (into {} (map vector x* le*))]
    [else nil])
  (LambdaExpr [ir] -> * []
    [(lambda (~x* ...) ~x ~body) (count x*)]))

(deftest value-pass-and-spliced-subpatterns
  (is (= '{f 2 g 0} (lambda-arity (P '(letrec ((f (lambda (a b) nil a)) (g (lambda () nil b))) f))))))

;; —— 模板 ——

(deftest templates
  (with-output-language [Lp Expr]
    (let [xs '[a b] es [(out (quote 1)) 'c]]
      (is (= '(begin a b c) (U (out (begin ~@xs c)))) "~@ 接入")
      (is (= '(begin a b c) (U (out (begin ~xs ... c)))) "~e ... 接入")
      (is (= '(call f a b (quote 1) c x) (U (out (call f ~@xs ~@es x)))) "多段接入与单项混合")
      (is (= '(letrec ((f (lambda () nil x)) (g (lambda () nil y))) f)
             (U (out (letrec ([~'[f g] ~[(out (lambda () nil x)) (in-context LambdaExpr (out (lambda () nil y)))]] ...) f))))
          "子模板整段接入、in-context")
      (is (= '(opt nil) (U (out (opt ~nil)))) "maybe 字段为 nil")
      (is (= '(quote (1 2)) (U (out (quote (1 2))))) "终结符位置的列表是引用的数据")
      (is (= '(lambda (p q) r (set! p (quote 0)))
             (U (out (lambda (p q) r (set! p (quote 0)))))) "非终结符交替：Expr 上下文里构造 LambdaExpr")))
  (is (thrown-with-msg? Exception #"expected e0"
        (with-output-language [Lp Expr] (out (if ~1 a b)))) "构造检查")
  (is (thrown? Exception (macroexpand '(cleat.nanopass/with-output-language [cleat.nanopass-pass-test/Lp Expr] (out (nosuch a)))))))

;; 模板里的求值顺序：从左到右，序列立即实现（处理器的副作用顺序与 Scheme 版相同）
(define-pass order Lp [ir log] -> Lp []
  (Expr [ir] -> Expr []
    [~x (swap! log conj x) x]))

(deftest evaluation-order
  (let [log (atom [])]
    (order (P '(call f a (begin b c) (if d e g))) log)
    (is (= '[f a b c d e g] @log))))

(deftest deterministic-expansion
  (let [form '(cleat.nanopass/define-pass p cleat.nanopass-pass-test/Lp [ir] -> cleat.nanopass-pass-test/Lq []
                (Expr [ir] -> Expr [] [(set! ~x ~[e]) (out (set-box! ~x ~e))]))]
    (is (= (pr-str (macroexpand-1 form)) (pr-str (macroexpand-1 form))))
    (is (not (re-find #"G__|__auto__" (pr-str (macroexpand-1 form)))) "不含 gensym")))
