;; 上游 nanopass-framework-scheme 的 tests/unit-tests.ss 翻译到 cleat.nanopass（cleat-095）。
;; 写法对照见 src/clojure/cleat/nanopass/README.md §3。不翻译的：带点对的产生式（(e0 . e1)、(e0 ... . e1)：Clojure 没有非正规表）、
;; 错误信息的原文比较（只检查会报错）、以 ? 结尾的元变量（e? → e2，07 D3）。
(ns cleat.upstream.unit-tests
  (:require [clojure.test :refer [deftest is testing]]
            [cleat.nanopass :as np :refer [define-language define-parser define-pass nanopass-case with-output-language out in-context
                                           pass-input-parser pass-output-unparser]]))

(def primitives '#{car cdr cons + - =})
(defn primitive? [x] (contains? primitives x))
(defn variable? [x] (and (symbol? x) (not (primitive? x))))
(defn constant? [x] (or (number? x) (boolean? x) (string? x) (and (sequential? x) (every? constant? x))))

(define-language L0
  (terminals (variable (x)) (constant (c)) (primitive (pr)))
  (Expr (e)
    (var x)
    (quote c)
    (begin e0 ... e1)
    (if e0 e1 e2)
    (lambda (x ...) e0 ... e1)
    (let ([x e] ...) e0 ... e1)
    (letrec ([x e] ...) e0 ... e1)
    (primapp pr e1 ...)
    (app e0 e1 ...)))

;; 上游的 var 记录（var-sym 等字段）：这里只要 sym；终结符叫 vref（var? 是 clojure.core 的）
(defrecord Vref [sym])
(defn vref? [x] (instance? Vref x))
(defn vref-sym [^Vref v] (:sym v))
(defn make-var [sym] (->Vref sym))

(define-language LUNPARSE
  (terminals
    (vref (x)) => vref-sym
    (constant (c))
    (primitive (pr)))
  (Expr (e body)
    (var x)                                      => x
    (quoted c)                                   => (quote c)
    (seq e0 e1)                                  => (begin e0 e1)
    (if e0 e1 e2)
    (lambda (x ...) e0 ... e1)
    (binding (x ...) (e ...) body0 ... body1)    => (let ([x e] ...) body0 ... body1)
    (recbinding (x ...) (e ...) body0 ... body1) => (letrec ([x e] ...) body0 ... body1)
    (primapp pr e1 ...)                          => (pr e1 ...)
    (app e0 e1 ...)                              => (e0 e1 ...)))

(define-parser parse-LUNPARSE LUNPARSE)

(define-language LBool
  (terminals (boolean (b)))
  (Expr (e) b))

(define-language LBoolLambda
  (terminals (boolean (b)) (symbol (x)))
  (Expr (e)
    v
    x
    (lambda (x) e)
    (and e0 e1)
    (or e0 e1)
    (not e)
    (e0 e1))
  (Value (v) b))

(deftest with-output-language-tests
  (is (= '(var a) (unparse-L0 (with-output-language L0 (in-context Expr (out (var a)))))))
  (is (= '(let ((x (quote 1)) (y (quote 2))) (primapp + (var x) (var y)))
         (unparse-L0 (with-output-language L0 (in-context Expr (out (let ([x (quote 1)] [y (quote 2)]) (primapp + (var x) (var y)))))))))
  (is (= '(var a) (unparse-L0 (with-output-language [L0 Expr] (out (var a))))))
  (is (= '(let ((x (quote 1)) (y (quote 2))) (primapp + (var x) (var y)))
         (unparse-L0 (with-output-language [L0 Expr] (out (let ([x (quote 1)] [y (quote 2)]) (primapp + (var x) (var y)))))))))

(def rec-example
  (let [x.1 (make-var 'x.1) x.2 (make-var 'x.2) y.3 (make-var 'y.3) x.4 (make-var 'x.4)
        zero?.5 (make-var 'zero?.5) *.6 (make-var '*.6) f.7 (make-var 'f.7)]
    {:node (with-output-language [LUNPARSE Expr]
             (out (recbinding
                    (~zero?.5 ~*.6 ~f.7)
                    ((lambda (~x.1) (primapp = (var ~x.1) (quoted 0)))
                     (lambda (~x.2 ~y.3)
                       (if (app (var ~zero?.5) (var ~x.2))
                           (quoted 0)
                           (if (primapp = (var ~x.2) (quoted 1))
                               (var ~y.3)
                               (primapp + (var ~y.3) (app (var ~*.6) (primapp - (var ~x.2) (quoted 1)) (var ~y.3))))))
                     (lambda (~x.4)
                       (if (app (var ~zero?.5) (var ~x.4))
                           (quoted 1)
                           (app (var ~*.6) (var ~x.4) (app (var ~f.7) (primapp - (var ~x.4) (quoted 1)))))))
                    (app (var ~f.7) (quoted 10)))))
     :raw (list 'recbinding (list zero?.5 *.6 f.7)
                (list (list 'lambda (list x.1) (list 'primapp '= (list 'var x.1) '(quoted 0)))
                      (list 'lambda (list x.2 y.3)
                            (list 'if (list 'app (list 'var zero?.5) (list 'var x.2))
                                  '(quoted 0)
                                  (list 'if (list 'primapp '= (list 'var x.2) '(quoted 1))
                                        (list 'var y.3)
                                        (list 'primapp '+ (list 'var y.3)
                                              (list 'app (list 'var *.6) (list 'primapp '- (list 'var x.2) '(quoted 1)) (list 'var y.3))))))
                      (list 'lambda (list x.4)
                            (list 'if (list 'app (list 'var zero?.5) (list 'var x.4))
                                  '(quoted 1)
                                  (list 'app (list 'var *.6) (list 'var x.4) (list 'app (list 'var f.7) (list 'primapp '- (list 'var x.4) '(quoted 1)))))))
                (list 'app (list 'var f.7) '(quoted 10)))
     :pretty '(letrec ((zero?.5 (lambda (x.1) (= x.1 (quote 0))))
                       (*.6 (lambda (x.2 y.3) (if (zero?.5 x.2) (quote 0) (if (= x.2 (quote 1)) y.3 (+ y.3 (*.6 (- x.2 (quote 1)) y.3))))))
                       (f.7 (lambda (x.4) (if (zero?.5 x.4) (quote 1) (*.6 x.4 (f.7 (- x.4 (quote 1))))))))
                (f.7 (quote 10)))}))

(deftest unparse-language
  (testing "raw（不美化）"
    (is (= '(quoted 5) (unparse-LUNPARSE (with-output-language [LUNPARSE Expr] (out (quoted 5))) true)))
    (is (= '(seq (quoted 7) (quoted 8)) (unparse-LUNPARSE (with-output-language [LUNPARSE Expr] (out (seq (quoted 7) (quoted 8)))) true)))
    (let [x.0 (make-var 'x.0)]
      (is (= (list 'var x.0) (unparse-LUNPARSE (with-output-language [LUNPARSE Expr] (out (var ~x.0))) true))))
    (is (= (:raw rec-example) (unparse-LUNPARSE (:node rec-example) true))))
  (testing "美化（=>）"
    (is (= '(quote 5) (unparse-LUNPARSE (with-output-language [LUNPARSE Expr] (out (quoted 5))) false)))
    (is (= '(begin (quote 7) (quote 8)) (unparse-LUNPARSE (with-output-language [LUNPARSE Expr] (out (seq (quoted 7) (quoted 8)))))))
    (let [x.0 (make-var 'x.0)]
      (is (= 'x.0 (unparse-LUNPARSE (with-output-language [LUNPARSE Expr] (out (var ~x.0)))))))
    (is (= (:pretty rec-example) (unparse-LUNPARSE (:node rec-example))))))

(deftest parse-language
  (is (= '(quote 7) (unparse-LUNPARSE (parse-LUNPARSE '(quoted 7)))))
  (let [x.1 (make-var 'x.1)]
    (is (= 'x.1 (unparse-LUNPARSE (parse-LUNPARSE (list 'var x.1))))))
  (is (= '(begin (quote 10) (quote 17)) (unparse-LUNPARSE (parse-LUNPARSE '(seq (quoted 10) (quoted 17))))))
  (is (= (:pretty rec-example) (unparse-LUNPARSE (parse-LUNPARSE (:raw rec-example))))))

(deftest boolean-terminals
  (let [parse-LBool (fn [x] (np/parse LBool x))]
    (is (= true (parse-LBool true)))
    (is (= false (parse-LBool false)))
    (is (thrown? Exception (parse-LBool 'a))))
  (let [parse-LBoolLambda (fn [x] (np/parse LBoolLambda x))]
    (is (= true (parse-LBoolLambda true)))
    (is (= false (parse-LBoolLambda false)))
    (is (= '(lambda (x) false) (unparse-LBoolLambda (parse-LBoolLambda '(lambda (x) false)))))
    (is (= '(lambda (f) (f false)) (unparse-LBoolLambda (parse-LBoolLambda '(lambda (f) (f false))))))
    (is (= '(lambda (f) (not (f false))) (unparse-LBoolLambda (parse-LBoolLambda '(lambda (f) (not (f false)))))))))

;; —— ensure-correct-identifiers：definitions 里与终结符谓词同名的定义不影响语言 ——

(defn datum? [x]
  (or (number? x) (string? x) (symbol? x) (and (sequential? x) (every? datum? x))))

(define-language LVAR
  (terminals (vref (x)) (primitive (pr)) (datum (d)))
  (Expr (e)
    (var x)
    (quote d)
    (if e0 e1 e2)
    (begin e0 ... e1)
    (let ([x e] ...) e1)
    (letrec ([x e] ...) e1)
    (app e0 e1 ...)
    (primapp pr e ...)))

(define-pass break-variable LVAR [ir] -> LVAR []
  (definitions (def vref? symbol?))
  (Expr [ir] -> Expr []
    [(var ~x) (println (str "found var: " (vref-sym x))) (out (var ~x))]))

(deftest accidental-variable-capture
  (is (= "found var: x\n"
         (with-out-str (break-variable (with-output-language [LVAR Expr] (out (var ~(make-var 'x)))))))))

;; —— maybe ——

(define-language Lmaybe
  (terminals (boolean (b)) (integer (i)))
  (Exp (e)
    (Int i)
    (Bool b)
    (Bar (maybe i) e)
    (Foo i (maybe e))))

(define-parser parse-Lmaybe Lmaybe)

(deftest maybe-parse-unparse
  (doseq [f '[(Int 72) (Bool true) (Bar 5 (Bool true)) (Bar false (Bool true)) (Foo 5 false) (Foo 5 (Foo 4 (Foo 3 false)))
              (Foo 5 (Bar 3 (Foo 1 false))) (Foo 5 (Int 3))]]
    (is (= f (unparse-Lmaybe (parse-Lmaybe f))) (pr-str f))))

(deftest maybe-with-output-language
  (is (= '(Int 72) (unparse-Lmaybe (with-output-language [Lmaybe Exp] (out (Int 72))))))
  (is (= '(Bar 5 (Bool true)) (unparse-Lmaybe (with-output-language [Lmaybe Exp] (out (Bar 5 (Bool true)))))))
  (is (= '(Bar false (Bool true)) (unparse-Lmaybe (with-output-language [Lmaybe Exp] (out (Bar false (Bool true)))))))
  (is (= '(Foo 5 false) (unparse-Lmaybe (with-output-language [Lmaybe Exp] (out (Foo 5 false))))))
  (is (= '(Foo 5 (Foo 4 (Foo 3 false))) (unparse-Lmaybe (with-output-language [Lmaybe Exp] (out (Foo 5 (Foo 4 (Foo 3 false))))))))
  (is (= '(Foo 5 (Bar 3 (Foo 1 false))) (unparse-Lmaybe (with-output-language [Lmaybe Exp] (out (Foo 5 (Bar 3 (Foo 1 false))))))))
  (is (= '(Foo 5 (Int 3)) (unparse-Lmaybe (with-output-language [Lmaybe Exp] (out (Foo 5 (Int 3))))))))

(define-pass add-one-int Lmaybe [ir] -> Lmaybe []
  (Exp [ir] -> Exp []
    [(Int ~i) (out (Int ~(inc i)))]))

(define-pass add-one Lmaybe [ir] -> Lmaybe []
  (Exp [ir] -> Exp []
    [(Foo ~i ~[e2]) (out (Foo ~(inc i) ~e2))]
    [(Bar ~i2 ~[e]) (out (Bar ~(and i2 (inc i2)) ~e))]
    [(Int ~i) (out (Int ~(inc i)))]))

(deftest maybe-pass
  (let [P (fn [f] (np/parse Lmaybe f))]
    (is (= '(Int 4) (unparse-Lmaybe (add-one-int (P '(Int 3))))))
    (is (= '(Foo 4 (Int 4)) (unparse-Lmaybe (add-one-int (P '(Foo 4 (Int 3)))))))
    (is (= '(Foo 4 (Foo 5 (Int 3))) (unparse-Lmaybe (add-one-int (P '(Foo 4 (Foo 5 (Int 2))))))))
    (is (= '(Foo 3 false) (unparse-Lmaybe (add-one-int (P '(Foo 3 false))))))
    (is (= '(Bar false (Int 5)) (unparse-Lmaybe (add-one-int (P '(Bar false (Int 4)))))))
    (is (= '(Int 4) (unparse-Lmaybe (add-one (P '(Int 3))))))
    (is (= '(Foo 5 (Int 4)) (unparse-Lmaybe (add-one (P '(Foo 4 (Int 3)))))))
    (is (= '(Foo 5 (Foo 6 (Int 3))) (unparse-Lmaybe (add-one (P '(Foo 4 (Foo 5 (Int 2))))))))
    (is (= '(Foo 4 (Bar 6 (Foo 7 false))) (unparse-Lmaybe (add-one (P '(Foo 3 (Bar 5 (Foo 6 false))))))))
    (is (= '(Foo 4 (Bar false (Foo 7 false))) (unparse-Lmaybe (add-one (P '(Foo 3 (Bar false (Foo 6 false))))))))))

(define-language Lmaybe2
  (terminals (boolean (b)) (integer (i)))
  (Exp (e)
    (Int i)
    (Bool b)
    (Bar (maybe i) ... e)
    (Foo i (maybe e) ...)))

(define-parser parse-Lmaybe2 Lmaybe2)

(deftest maybe-dots-parse-unparse
  (doseq [f '[(Foo 3) (Bar (Int 72)) (Int 72) (Bool true) (Bar 5 (Bool true)) (Bar false (Bool true))
              (Bar false 1 false 2 false 3 (Bool true)) (Bar 1 false 2 false 3 false (Bool true)) (Foo 5 false)
              (Foo 5 false false (Bar 3 (Foo 2 false)) (Bool true) false false (Int 2) false)
              (Foo 5 (Foo 4 (Foo 3 false (Bool true) (Int 3)))) (Foo 5 (Bar 3 (Foo 1 (Bar 2 (Bool true)) false false))) (Foo 5 (Int 3) (Bool false))]]
    (is (= f (unparse-Lmaybe2 (parse-Lmaybe2 f))) (pr-str f))))

(deftest maybe-dots-with-output-language
  (is (= '(Foo 3) (unparse-Lmaybe2 (with-output-language [Lmaybe2 Exp] (out (Foo 3))))))
  (is (= '(Bar (Int 72)) (unparse-Lmaybe2 (with-output-language [Lmaybe2 Exp] (out (Bar (Int 72)))))))
  (is (= '(Bar false 1 false 2 false 3 (Bool true)) (unparse-Lmaybe2 (with-output-language [Lmaybe2 Exp] (out (Bar false 1 false 2 false 3 (Bool true)))))))
  (is (= '(Foo 5 false false (Bar 3 (Foo 2 false)) (Bool true) false false (Int 2) false)
         (unparse-Lmaybe2 (with-output-language [Lmaybe2 Exp] (out (Foo 5 false false (Bar 3 (Foo 2 false)) (Bool true) false false (Int 2) false))))))
  (is (= '(Foo 5 (Bar 3 (Foo 1 (Bar 2 (Bool true)) false false)))
         (unparse-Lmaybe2 (with-output-language [Lmaybe2 Exp] (out (Foo 5 (Bar 3 (Foo 1 (Bar 2 (Bool true)) false false)))))))))

(define-pass add-one-int2 Lmaybe2 [ir] -> Lmaybe2 []
  (Exp [ir] -> Exp []
    [(Int ~i) (out (Int ~(inc i)))]))

(define-pass add-one2 Lmaybe2 [ir] -> Lmaybe2 []
  (Exp [ir] -> Exp []
    [(Foo ~i ~[e2*] ...) (out (Foo ~(inc i) ~e2* ...))]
    [(Bar ~i2* ... ~[e]) (out (Bar ~(map #(and % (inc %)) i2*) ... ~e))]
    [(Int ~i) (out (Int ~(inc i)))]))

(deftest maybe-dots-pass
  (let [P (fn [f] (np/parse Lmaybe2 f))]
    (is (= '(Int 4) (unparse-Lmaybe2 (add-one-int2 (P '(Int 3))))))
    (is (= '(Foo 4 (Int 4) (Int 5) (Int 7) false false (Int 8))
           (unparse-Lmaybe2 (add-one-int2 (P '(Foo 4 (Int 3) (Int 4) (Int 6) false false (Int 7)))))))
    (is (= '(Foo 4 (Foo 5 (Int 3) false (Int 4) (Int 5))) (unparse-Lmaybe2 (add-one-int2 (P '(Foo 4 (Foo 5 (Int 2) false (Int 3) (Int 4))))))))
    (is (= '(Foo 3 false (Int 4)) (unparse-Lmaybe2 (add-one-int2 (P '(Foo 3 false (Int 3)))))))
    (is (= '(Bar 3 false 4 false (Int 4)) (unparse-Lmaybe2 (add-one-int2 (P '(Bar 3 false 4 false (Int 3)))))))
    (is (= '(Int 4) (unparse-Lmaybe2 (add-one2 (P '(Int 3))))))
    (is (= '(Foo 5 (Int 4) (Int 5) (Int 6) false (Int 8)) (unparse-Lmaybe2 (add-one2 (P '(Foo 4 (Int 3) (Int 4) (Int 5) false (Int 7)))))))
    (is (= '(Foo 5 (Foo 6 (Int 3) (Bar 4 3 2 false 1 (Foo 3 (Int 8) (Int 9)))))
           (unparse-Lmaybe2 (add-one2 (P '(Foo 4 (Foo 5 (Int 2) (Bar 3 2 1 false 0 (Foo 2 (Int 7) (Int 8))))))))))
    (is (= '(Foo 4 (Bar 6 false 8 false 9 (Foo 7 false)) (Bool true) false)
           (unparse-Lmaybe2 (add-one2 (P '(Foo 3 (Bar 5 false 7 false 8 (Foo 6 false)) (Bool true) false))))))
    (is (= '(Foo 4 (Bar false (Foo 7 false)) (Bool true) false)
           (unparse-Lmaybe2 (add-one2 (P '(Foo 3 (Bar false (Foo 6 false)) (Bool true) false))))))))

(define-language LMaybeNoBool
  (terminals (symbol (x)) (number (n)))
  (Expr (e)
    (foo x (maybe n))
    (bar (maybe e) x)
    (num n)
    (ref x)))

(define-language LMaybeListNoBool
  (terminals (symbol (x)) (number (n)))
  (Expr (e)
    (foo ([x (maybe n)] ...) e)
    (bar (maybe e) ... x)
    (num n)
    (ref x)))

(deftest maybe-unparse
  (is (= '(foo x 10) (unparse-LMaybeNoBool (with-output-language [LMaybeNoBool Expr] (out (foo x 10))))))
  (is (= '(bar (foo x false) x) (unparse-LMaybeNoBool (with-output-language [LMaybeNoBool Expr] (out (bar (foo x false) x))))))
  (is (= '(bar (bar (foo y false) y) z) (unparse-LMaybeNoBool (with-output-language [LMaybeNoBool Expr] (out (bar (bar (foo y false) y) z))))))
  (is (= '(bar (bar (bar false x) y) z) (unparse-LMaybeNoBool (with-output-language [LMaybeNoBool Expr] (out (bar (bar (bar false x) y) z)))))))

(deftest maybe-unparse-dots
  (is (= '(foo ((x 10) (y 12)) (ref x)) (unparse-LMaybeListNoBool (with-output-language [LMaybeListNoBool Expr] (out (foo ([x 10] [y 12]) (ref x)))))))
  (is (= '(foo ((x false) (y 12) (z false)) (ref y))
         (unparse-LMaybeListNoBool (with-output-language [LMaybeListNoBool Expr] (out (foo ([x false] [y 12] [z false]) (ref y)))))))
  (is (= '(bar false false (num 10) (ref x) false (foo ((x false) (y 10) (z 5) (w false)) (bar false z)) false w)
         (unparse-LMaybeListNoBool (with-output-language [LMaybeListNoBool Expr]
                                     (out (bar false false (num 10) (ref x) false (foo ([x false] [y 10] [z 5] [w false]) (bar false z)) false w)))))))

;; —— 重复的产生式报错（上游 github issue #12）——

(deftest duplicate-production-is-an-error
  (is (re-find #"lists the same production more than once"
               (try (macroexpand-1 '(cleat.nanopass/define-language L-duplicate-alt (terminals (number (n))) (Expr (e) (const n) (const n))))
                    nil
                    (catch Exception e (.Message e))))))

;; —— argument-name-matching（上游 R. Kent Dybvig 报的回归）——

(define-language L
  (terminals (symbol (x)))
  (A (a) b)
  (B (b) x))

(defn- say-bar [bar] (println (str "bar = " (pr-str bar))))

(define-pass P1 L [ir] -> L []
  (A [ir foo bar ignore] -> A [])
  (B [ir foo bar] -> B [] [else (say-bar bar) ir])
  (A ir "I am not bar" "I am bar" "extra stuff"))

(define-pass P2 L [ir] -> L []
  (A [ir foo bar ignore] -> A [])
  (B [ir bar] -> B [] [else (say-bar bar) ir])
  (A ir "I am not bar" "I am bar" "extra stuff"))

(define-pass P3 L [ir] -> L []
  (A [ir xxfoo xxbar ignore] -> A [])
  (B [ir foo bar] -> B [] [else (say-bar bar) ir])
  (B2 B [ir] -> B [] [else (println "calling B2") ir])
  (A ir "I am not bar" "I am bar" "extra stuff"))

(define-pass P4 L [ir] -> L []
  (A [ir] -> A [])
  (B [ir [foo "I am not bar"] [bar "I am bar"]] -> B [] [else (say-bar bar) ir])
  (A ir))

(define-pass P5 L [ir] -> L []
  (B [ir foo bar ignore] -> B [])
  (symbol [ir foo bar] -> symbol [] (say-bar bar) ir)
  (B ir "I am not bar" "I am bar" "extra stuff"))

(define-pass P6 L [ir] -> L []
  (B [ir foo bar ignore] -> B [])
  (symbol [ir bar] -> symbol [] (say-bar bar) ir)
  (B ir "I am not bar" "I am bar" "extra stuff"))

(define-pass P7 L [ir] -> L []
  (B [ir foo bar ignore] -> B [])
  (symbol [ir xxfoo xxbar] -> symbol [] (say-bar xxbar) ir)
  (symbol2 symbol [ir] -> symbol [] (println "calling symbol2") ir)
  (B ir "I am not bar" "I am bar" "extra stuff"))

(define-pass P8 L [ir] -> L []
  (B [ir] -> B [])
  (symbol [ir [foo "I am not bar"] [bar "I am bar"]] -> symbol [] (say-bar bar) ir)
  (B ir))

(define-pass P9 L [ir foo bar ignore] -> L []
  (A [ir foo bar] -> A [] [else (say-bar bar) ir]))

(define-pass P10 L [ir foo bar ignore] -> L []
  (A [ir bar] -> A [] [else (say-bar bar) ir]))

(define-pass P11 L [ir foo bar ignore] -> L []
  (A [ir xxfoo xxbar] -> A [])
  (A2 A [ir] -> A [] [else (println "calling A2") ir]))

(define-pass P12 L [ir] -> L []
  (A [ir [foo "I am not bar"] [bar "I am bar"]] -> A [] [else (say-bar bar) ir]))

(deftest argument-name-matching
  (testing "sub-nonterminal"
    (is (= "bar = \"I am bar\"\n" (with-out-str (P1 'q))))
    (is (= "bar = \"I am bar\"\n" (with-out-str (P2 'q))))
    (is (= "calling B2\n" (with-out-str (P3 'q))))
    (is (= "bar = \"I am bar\"\n" (with-out-str (P4 'q)))))
  (testing "sub-terminal"
    (is (= "bar = \"I am bar\"\n" (with-out-str (P5 'q))))
    (is (= "bar = \"I am bar\"\n" (with-out-str (P6 'q))))
    (is (= "calling symbol2\n" (with-out-str (P7 'q))))
    (is (= "bar = \"I am bar\"\n" (with-out-str (P8 'q)))))
  (testing "pass formals"
    (is (= "bar = \"I am bar\"\n" (with-out-str (P9 'q "I am not bar" "I am bar" "extra stuff"))))
    (is (= "bar = \"I am bar\"\n" (with-out-str (P10 'q "I am not bar" "I am bar" "extra stuff"))))
    (is (= "calling A2\n" (with-out-str (P11 'q "I am not bar" "I am bar" "extra stuff"))))
    (is (= "bar = \"I am bar\"\n" (with-out-str (P12 'q))))))

;; —— SKI → λ：* 输入、* 输出、不以关键字开头的产生式、pass-input-parser / pass-output-unparser ——

(defn ski-combinator? [x] (contains? '#{S K I} x))

(define-language Lski
  (terminals (ski-combinator (C)))
  (Expr (e)
    C
    (e0 e1)))

(define-language Llc
  (terminals (symbol (x)))
  (Expr (e)
    x
    (lambda (x) e)
    (e0 e1)))

(define-pass ski->lc Lski [ir] -> Llc []
  (definitions
    (def counter (atom 0))
    (defn make-variable [x] (symbol (str x "." (let [n @counter] (swap! counter inc) n)))))
  (Expr [e] -> Expr []
    [~C (case C
          S (let [x (make-variable 'x) y (make-variable 'y) z (make-variable 'z)]
              (out (lambda (~x) (lambda (~y) (lambda (~z) ((~x ~z) (~y ~z)))))))
          K (let [x (make-variable 'x) y (make-variable 'y)]
              (out (lambda (~x) (lambda (~y) ~x))))
          I (let [x (make-variable 'x)]
              (out (lambda (~x) ~x))))]
    [(~e0 ~e1) (let [e0 (Expr e0) e1 (Expr e1)] (out (~e0 ~e1)))]))

(define-pass ski-in * [ir] -> Lski []
  (Expr * [ir] -> Expr []
    (cond
      (ski-combinator? ir) ir
      (and (sequential? ir) (= 2 (count ir))) (let [[e0 e1] ir] (out (~(Expr e0) ~(Expr e1))))
      :else (throw (ex-info (str "ski-in: unrecognized ski input " (pr-str ir)) {}))))
  (Expr ir))

(define-pass lc-out Llc [ir] -> * [sexpr]
  (Expr [ir] -> * [sexpr]
    [(lambda (~x) ~[sexpr]) (list 'lambda (list x) sexpr)]
    [(~[sexpr0] ~[sexpr1]) (list sexpr0 sexpr1)]
    [~x x])
  (Expr ir))

(deftest pass-parsers
  (is (= '((S K) I) ((pass-input-parser ski-in) '((S K) I))))
  (is (= (unparse-Lski (with-output-language [Lski Expr] (out ((S K) I))))
         (unparse-Lski ((pass-input-parser ski->lc) '((S K) I)))))
  (is (= (unparse-Llc (with-output-language [Llc Expr] (out (lambda (x) (x x)))))
         (unparse-Llc ((pass-input-parser lc-out) '(lambda (x) (x x)))))))

(deftest pass-unparsers
  (is (= '((S I) K) ((pass-output-unparser ski-in) (with-output-language [Lski Expr] (out ((S I) K))))))
  (is (= '((lambda (x) (x x)) (lambda (y) (y y)))
         ((pass-output-unparser ski->lc) (with-output-language [Llc Expr] (out ((lambda (x) (x x)) (lambda (y) (y y))))))))
  (is (= 'bob ((pass-output-unparser lc-out) 'bob))))

(deftest pass-parser-unparser
  (is (= '(((lambda (x.0) (lambda (y.1) x.0)) (lambda (x.2) x.2)) (lambda (x.3) x.3))
         ((pass-output-unparser ski->lc) (ski->lc ((pass-input-parser ski->lc) '((K I) I))))))
  (is (= '((lambda (x.0) x.0) (lambda (x.1) x.1)) (lc-out (ski->lc (ski-in '(I I)))))))

;; —— 两层省略号的解析回归（each-any）——

(define-language Leach-any
  (terminals (symbol (x)))
  (Program (p) (locals ((x** ...) ...) e))
  (Expr (e) x))

(define-parser parse-Leach-any Leach-any)

(deftest each-any-regression
  (let [ts '(locals ((x y z) (w k v l)) x)]
    (is (= ts (unparse-Leach-any (parse-Leach-any ts))))))

;; —— trace-define-pass / echo-define-pass ——

(np/trace-define-pass traced Lmaybe [ir] -> Lmaybe []
  (Exp [ir] -> Exp [] [(Int ~i) (out (Int ~(inc i)))]))

(deftest trace-and-echo
  (let [s (with-out-str (traced (np/parse Lmaybe '(Int 1))))]
    (is (re-find #"traced input:\n\(Int 1\)" s))
    (is (re-find #"traced output:\n\(Int 2\)" s)))
  (is (re-find #"clojure.core/defn echoed"
               (with-out-str (macroexpand-1 '(cleat.nanopass/echo-define-pass echoed cleat.upstream.unit-tests/Lmaybe [ir] -> cleat.upstream.unit-tests/Lmaybe []
                                              (Exp [ir] -> Exp [] [(Int ~i) (out (Int ~(inc i)))])))))))
