;;; CLEAT-184: not upstream. cleat's proxy expands into its own special form proxy-class* (the subclass is generated at compile
;;; time, cleat-043); tools.analyzer.clr analyzes it as an opaque :proxy-class node, and the proxy's method bodies are ordinary
;;; fns, so go-block locals they close over survive parking like any other closure.
(ns clojure.core.async.cleat-go-proxy-test
  (:require [clojure.test :refer :all]
            [clojure.core.async :as a]))

(deftest proxy-in-go
  (is (= "p1" (a/<!! (a/go (str (proxy [Object] [] (ToString [] "p1"))))))))

(deftest proxy-closing-over-go-locals
  (is (= "x=42" (a/<!! (a/go (let [x 41] (str (proxy [Object] [] (ToString [] (str "x=" (inc x)))))))))))

(deftest proxy-across-parking
  (let [c (a/chan)]
    (a/go (a/>! c 5))
    (is (= ["y=5" 10 5]
           (a/<!! (a/go (let [y (a/<! c)
                              p (proxy [Object] [] (ToString [] (str "y=" y)))
                              z (a/<! (a/go 10))]
                          [(str p) z y])))))))

(deftest proxy-implementing-an-interface
  (is (= [3 2 1]
         (a/<!! (a/go (let [cmp (proxy [System.Collections.IComparer] [] (Compare [a b] (- (compare a b))))
                            arr (object-array [1 3 2])]
                        (System.Array/Sort arr cmp)
                        (vec arr)))))))

(deftest generic-overloads-in-go
  ;; tools.analyzer.clr's convertible? used to throw on a generic method's unresolvable parameter type (Array/Sort<T>(T[]))
  (is (= [1 2 3] (a/<!! (a/go (let [arr (object-array [3 1 2])] (System.Array/Sort arr) (vec arr)))))))

(deftest await-in-go-is-a-clear-error
  (is (thrown-with-msg? Exception #"only valid directly inside an \^:async fn"
                        (try (eval '(clojure.core.async/go (await* nil)))
                             (catch Exception e (throw (or (some #(when (re-find #"only valid directly" (str (.Message ^Exception %))) %)
                                                                 (take-while some? (iterate #(.InnerException ^Exception %) e)))
                                                           e)))))))
