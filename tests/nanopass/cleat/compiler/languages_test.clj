;; cleat.compiler.languages 的验收（cleat-083）：Scheme 编译器的快照（tests/scheme/compiler/snapshots/*.l0 / .l2 / .l3，
;; Scheme 的 unparse 输出）读进来，按对应的 Clojure 语言解析成记录再 unparse，得到相同的数据。
(ns cleat.compiler.languages-test
  (:require [clojure.test :refer [deftest is testing]]
            [clojure.string :as str]
            [cleat.nanopass :as np]
            [cleat.compiler.languages :as l]
            [cleat.test.scheme-data :refer [read-scheme-all snapshot-dir]]))

(deftest scheme-snapshots-round-trip
  (let [langs {"p0" l/L0 "l0" l/L0 "l1" l/L1 "l2" l/L2 "l3" l/L3}
        files (sort (filter #(langs (subs (System.IO.Path/GetExtension ^String %) 1))
                            (System.IO.Directory/GetFiles snapshot-dir)))]
    (is (< 120 (count files)) "快照文件都在")
    (doseq [f files]
      (let [lang (langs (subs (System.IO.Path/GetExtension ^String f) 1))
            forms (read-scheme-all (slurp f))]
        (testing (System.IO.Path/GetFileName ^String f)
          (is (= 1 (count forms)))
          (let [x (first forms)
                n (np/parse lang x)]
            (is (= x (np/unparse n)))))))))

(deftest l1-letrec-binds-lambdas
  (is (l/L1:Expr:letrec? (np/parse l/L1 '(letrec ((f (lambda false (clause () false (quote 1))))) (call f)))))
  (is (thrown? Exception (np/parse l/L1 '(letrec ((f (quote 1))) f)))))

(deftest l3-lambda-free-variables
  (let [n (np/parse l/L3 '(lambda "f" (y z) (clause (x) false (primcall + x y z))))]
    (is (l/L3:LambdaExpr:lambda? n))
    (is (= '[y z] (vec (.-x* ^cleat.compiler.languages.L3-LambdaExpr-lambda n))))))

(deftest clj-fn-name-is-a-name
  (let [nm (l/->CljFnName "user$f" nil nil false)
        n (l/make-L0:LambdaExpr:lambda nm [(l/make-L0:Clause:clause '[x] nil 'x)])]
    (is (identical? nm (.-nm ^cleat.compiler.languages.L0-LambdaExpr-lambda n)))))
