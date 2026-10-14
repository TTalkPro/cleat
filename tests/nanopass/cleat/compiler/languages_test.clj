;; cleat.compiler.languages 的验收（cleat-083）：语言定义的特性。原来还把原版（Scheme 实现，cleat-093 删）编译器的快照读进来做往返比较，
;; cleat-092 去掉原版专有的节点之后快照不再能解析，该测试删去。
(ns cleat.compiler.languages-test
  (:require [clojure.test :refer [deftest is testing]]
            [cleat.nanopass :as np]
            [cleat.compiler.languages :as l]))

(deftest l1-letrec-binds-lambdas
  (is (l/L1:Expr:letrec? (np/parse l/L1 '(letrec ((f (lambda false (clause () false (quote 1))))) (call f)))))
  (is (thrown? Exception (np/parse l/L1 '(letrec ((f (quote 1))) f)))))

(deftest l3-lambda-free-variables
  (let [n (np/parse l/L3 '(lambda "f" (y z) (clause (x) false (clj-invoke x y z))))]
    (is (l/L3:LambdaExpr:lambda? n))
    (is (= '[y z] (vec (.-x* ^cleat.compiler.languages.L3-LambdaExpr-lambda n))))))

(deftest clj-fn-name-is-a-name
  (let [nm (l/->CljFnName "user$f" nil nil false)
        n (l/make-L0:LambdaExpr:lambda nm [(l/make-L0:Clause:clause '[x] nil 'x)])]
    (is (identical? nm (.-nm ^cleat.compiler.languages.L0-LambdaExpr-lambda n)))))
