;; cleat.nanopass 语言层的测试（cleat-083）。由 tests/Cleat.Host.Tests/NanopassTests.cs 运行：cleat -I src/clojure -I tests/nanopass。
(ns cleat.nanopass-test
  (:require [clojure.test :refer [deftest is testing]]
            [cleat.nanopass :as np :refer [define-language]]))

(defn datum? [x] true)
(defn flag? [x] (boolean? x))

(define-language Lt
  (terminals (symbol (x)) (datum (d)) (flag (st)))
  (entry Expr)
  (Expr (e body)
    x
    (quote d)
    (if e0 e1 e2)
    (begin e* ... e)
    (set! x e)
    le
    (letrec ([x* le*] ...) body)
    (bind ((x** e**) ...) ...)
    (flagged st e))
  (LambdaExpr (le)
    (lambda (x* ...) (maybe x) body)))

(define-language Lt2 (extends Lt)
  (terminals (- (flag (st))) (+ (string (str))))
  (Expr (e body)
    (- (if e0 e1 e2) (flagged st e))
    (+ (when e0 e1) (named str e)))
  (Stmt (s)
    (assign x e)
    (expr e)))

(deftest construction-and-predicates
  (let [n (make-Lt:Expr:if 'a (make-Lt:Expr:quote 1) 'b)]
    (is (Lt:Expr:if? n))
    (is (Lt:Expr? n))
    (is (not (Lt:LambdaExpr? n)))
    (is (= ['a 'b] ((juxt first last) (vec (.nanoFields ^cleat.nanopass.records.INode n)))) "字段按出现顺序")
    (is (= 'a (.-e0 ^Lt-Expr-if n)) "字段按元变量名访问")
    (is (Lt:Expr? 'x) "终结符交替")
    (is (not (Lt:Expr? 1)))
    (is (Lt:Expr? (make-Lt:LambdaExpr:lambda '[a] nil 'a)) "非终结符交替")
    (testing "节点引用相等"
      (is (not= (make-Lt:Expr:set! 'a 'b) (make-Lt:Expr:set! 'a 'b))))))

(deftest constructor-checks
  (is (thrown-with-msg? Exception #"expected e0" (make-Lt:Expr:if 1 'b 'c)))
  (is (thrown-with-msg? Exception #"expected e\*" (make-Lt:Expr:begin [1] 'c)) "省略号字段逐个检查")
  (is (thrown-with-msg? Exception #"expected le\*" (make-Lt:Expr:letrec '[f] '[g] 'f)) "letrec 只绑 lambda")
  (is (thrown-with-msg? Exception #"expected st" (make-Lt:Expr:flagged nil 'a)))
  (is (Lt:LambdaExpr:lambda? (make-Lt:LambdaExpr:lambda [] false 'a)) "maybe 字段：false / nil 为无")
  (is (thrown-with-msg? Exception #"expected x\*\*" (make-Lt:Expr:bind '[[a 1]] '[[b]] )) "两层省略号"))

(def sample
  '(letrec ((f (lambda (a b) nil (begin a (set! b a) (quote [1 2]))))
            (g (lambda () r (if r (quote 1) (bind ((p q) (r s)) ((t u)))))))
     (flagged true f)))

(deftest parse-unparse
  (let [n (np/parse Lt sample)]
    (is (Lt:Expr:letrec? n))
    (is (= sample (np/unparse n)))
    (is (= sample (unparse-Lt n)))
    (is (= "#<Lt (if a b c)>" (pr-str (np/parse Lt '(if a b c)))))
    (is (thrown-with-msg? Exception #"invalid" (np/parse Lt '(if a b))))
    (is (thrown-with-msg? Exception #"invalid" (np/parse Lt '(letrec ((f g)) f))) "le 位置不接受变量")
    (is (= '(lambda (a) nil a) (np/unparse (np/parse Lt 'LambdaExpr '(lambda (a) nil a)))))))

(deftest extends
  (is (thrown? Exception (np/parse Lt2 '(if a b c))) "删掉的产生式")
  (is (Lt2:Expr:when? (np/parse Lt2 '(when a b))))
  (is (= '(named "s" x) (np/unparse (np/parse Lt2 '(named "s" x)))) "新增的终结符")
  (is (Lt2:Stmt:assign? (np/parse Lt2 'Stmt '(assign x (begin y z)))) "新增的非终结符")
  (testing "不共享记录类型（与上游相同）"
    (is (not (Lt:Expr? (np/parse Lt2 '(begin a b)))))
    (is (not (Lt2:Expr? (np/parse Lt '(begin a b))))))
  (is (= '(define-language Lt2
            (terminals (symbol (x)) (datum (d)) (string (str)))
            (entry Expr)
            (Expr (e body) x (quote d) (begin e* ... e) (set! x e) le (letrec ((x* le*) ...) body)
                  (bind ((x** e**) ...) ...) (when e0 e1) (named str e))
            (LambdaExpr (le) (lambda (x* ...) (maybe x) body))
            (Stmt (s) (assign x e) (expr e)))
         (np/language->s-expression Lt2))))

(deftest definition-errors
  (let [bad (fn [form] (try (macroexpand-1 form) nil (catch Exception e (.Message e))))]
    (is (nil? (bad '(cleat.nanopass/define-language Lb (terminals (symbol (x))) (E (e) (x e))))) "不以关键字开头的产生式按形状匹配（cleat-095）")
    (is (re-find #"invalid" (bad '(cleat.nanopass/define-language Lb (terminals (symbol (x))) (E (e) (1 e))))))
    (is (re-find #"unknown meta-variable" (bad '(cleat.nanopass/define-language Lb (terminals (symbol (x))) (E (e) (k y))))))
    (is (re-find #"share a keyword and a shape" (bad '(cleat.nanopass/define-language Lb (terminals (symbol (x))) (E (e) (k x) (k e))))))
    (is (re-find #"declared twice" (bad '(cleat.nanopass/define-language Lb (terminals (symbol (x))) (E (x) (k x))))))
    (is (re-find #"not defined" (bad '(cleat.nanopass/define-language Lb (terminals (nosuch (x))) (E (e) (k x))))))
    (is (re-find #"cannot remove" (bad '(cleat.nanopass/define-language Lb (extends cleat.nanopass-test/Lt) (Expr (e) (- (nope e)))))))))

(deftest deterministic-expansion
  (let [form '(cleat.nanopass/define-language Ld (terminals (symbol (x))) (E (e) x (k e* ... e)))]
    (is (= (pr-str (macroexpand-1 form)) (pr-str (macroexpand-1 form))))))
