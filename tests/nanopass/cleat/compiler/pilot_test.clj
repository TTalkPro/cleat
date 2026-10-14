;; cleat-084 的验收试点：用 cleat.nanopass 改写的 pass 在 Scheme 编译器的快照上与 Scheme 版输出相同。
;;   uncover-free / attach（closures.sls）：.l2 → .l3
;;   lower-loops（loops.sls）：.p0（parse-core 的输出）→ .l0（lower-loops 之后）
;;   convert-assignments（front.sls，分析表由 analyze 算出）：.l1 → .l2
(ns cleat.compiler.pilot-test
  (:require [clojure.test :refer [deftest is testing]]
            [cleat.nanopass :as np]
            [cleat.compiler.languages :as l]
            [cleat.compiler.closures :as closures]
            [cleat.test.scheme-data :refer [snapshot snapshot-names canonical-temps]]))

(deftest uncover-free-on-snapshots
  (let [names (snapshot-names "l2")]
    (is (< 20 (count names)))
    (doseq [n names]
      (testing n
        (is (= (snapshot n "l3") (np/unparse (closures/uncover-free (np/parse l/L2 (snapshot n "l2"))))))))))

(require '[cleat.compiler.loops :as loops] '[cleat.compiler.front :as front])

(deftest lower-loops-on-snapshots
  (let [names (snapshot-names "p0")]
    (is (< 20 (count names)))
    (doseq [n names]
      (testing n
        (loops/reset-fresh-names!)
        (is (= (canonical-temps (snapshot n "l0")) (canonical-temps (np/unparse (loops/lower-loops (np/parse l/L0 (snapshot n "p0")))))))))))

;; l1 里 set! 的目标（simplify-letrec 引入的前向变量在 Scheme 里另行标为已赋值）
(defn- set-targets [x]
  (cond
    (and (seq? x) (= 'set! (first x))) (cons (second x) (mapcat set-targets (rest x)))
    (or (seq? x) (vector? x)) (mapcat set-targets x)
    :else ()))

(deftest convert-assignments-on-snapshots
  (let [names (snapshot-names "l1")]
    (is (< 20 (count names)))
    (doseq [n names]
      (testing n
        (let [an (front/analyze (np/parse l/L0 (snapshot n "l0")))
              l1 (snapshot n "l1")]
          (doseq [x (set-targets l1)] (.set_Item ^System.Collections.Hashtable (:assigned an) x true))
          (is (= (snapshot n "l2") (np/unparse (front/convert-assignments (np/parse l/L1 l1) an)))))))))

(deftest check-forward-wraps-forward-references
  (let [an (front/analyze (np/parse l/L0 '(quote 1)))]
    (.set_Item ^System.Collections.Hashtable (:forward an) 'f true)
    ;; 引号里的名字照 Scheme 版是 Scheme 符号（cleat-090）
    (let [sf (Cleat.Scheme.Symbol/Intern "f")]
      (is (= (list 'begin (list 'primcall '$check-initialized 'f (list 'quote sf)) 'g
                   (list 'lambda false (list 'clause '(a) false (list 'primcall '$check-initialized 'f (list 'quote sf)))))
             (np/unparse (front/check-forward (np/parse l/L1 '(begin f g (lambda false (clause (a) false f)))) an)))))))
