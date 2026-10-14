;; 上游 tests/compiler-test.ss 的对应（cleat-095）：main-tests 与 final-tests 的每个表达式经 20 趟 pass 不报错（上游的 test-one 也只做到这里：
;; check-eval 是空操作、没有 generate-code），另加几处 unparse 的抽查。
(ns cleat.upstream.compiler-tests
  (:require [clojure.test :refer [deftest is testing]]
            [cleat.nanopass :as np]
            [cleat.upstream.compiler :as c :refer [run-passes test-one parse-LP verify-scheme]]
            [cleat.upstream.alltests :as all]))

(deftest run-main-tests
  (doseq [e all/main-tests]
    (is (test-one e) (pr-str e))))

(deftest run-final-tests
  (doseq [e all/final-tests]
    (is (test-one e) (pr-str e))))

(deftest first-passes-unparse
  (c/reset-seed)
  (is (= '(let ((x_1 (datum 0))) (var x_1)) (np/unparse (c/rename-vars-verify-legal '(let ([x 0]) x)))))
  (is (= '(let ((x_2 (datum 0))) (begin (var x_2))) (np/unparse (c/remove-implicit-begin (c/rename-vars-verify-legal '(let ([x 0]) x))))))
  (is (= '(let ((x (datum 0))) (var x)) (np/unparse (verify-scheme (parse-LP '(let ((x 0)) x))))))
  (is (thrown-with-msg? Exception #"unbound variable" (verify-scheme (parse-LP '(let ((x 0)) y)))))
  (is (thrown-with-msg? Exception #"unbound var" (c/rename-vars-verify-legal '(let ([x 0]) y)))))

(deftest settable-is-pretty-printed-away
  ;; L4 的 (settable (x ...) body) => body：缺省的 unparse 不显示它
  (c/reset-seed)
  (let [ir (reduce (fn [ir p] (p ir)) '(let ([x 0]) (set! x 1) x)
                   [c/rename-vars-verify-legal c/remove-implicit-begin c/remove-unquoted-constant c/remove-one-armed-if c/uncover-settable])]
    (is (= '(let ((x_1 (quoted-const 0))) (begin (set! x_1 (quoted-const 1)) (var x_1))) (np/unparse ir)))
    (is (= '(let ((x_1 (quoted-const 0))) (settable (x_1) (begin (set! x_1 (quoted-const 1)) (var x_1)))) (np/unparse ir true)))))

(deftest final-language
  (let [ir (run-passes '(let ([f (lambda (x) (+ x 1))]) (f 41)))]
    (is (c/L18:LetrecExpr? ir))
    (is (= 'letrec (first (np/unparse ir))))))
