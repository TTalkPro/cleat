;; cleat-084 的验收试点。原来用 cleat.nanopass 改写的 pass 在 Scheme 编译器的快照上与 Scheme 版比较输出（uncover-free、lower-loops、
;; convert-assignments）；cleat-092 去掉 Scheme 专有的节点之后快照不再能解析，那些测试删去（快照随 Scheme 在 cleat-093 删除）。
(ns cleat.compiler.pilot-test
  (:require [clojure.test :refer [deftest is testing]]
            [cleat.nanopass :as np]
            [cleat.compiler.languages :as l]
            [cleat.compiler.front :as front]))

(deftest check-forward-wraps-forward-references
  (let [an (front/analyze (np/parse l/L0 '(quote 1)))]
    (.set_Item ^System.Collections.Hashtable (:forward an) 'f true)
    (is (= '(begin (check-initialized f) g (lambda false (clause (a) false (check-initialized f))))
           (np/unparse (front/check-forward (np/parse l/L1 '(begin f g (lambda false (clause (a) false f)))) an))))))
