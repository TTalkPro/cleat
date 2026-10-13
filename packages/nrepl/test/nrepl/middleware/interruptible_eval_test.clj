(ns nrepl.middleware.interruptible-eval-test
  {:author "Oleksandr Yakushev"}
  (:require
   [clojure.test :refer :all]
   [clojure.string :as str]
   [nrepl.core :as nrepl]
   [nrepl.core-test :refer [def-repl-test repl-server-fixture]]))

(use-fixtures :each repl-server-fixture)

;; cleat: the JVM version looks for the source file in the stack trace of an
;; exception thrown by the compiled function. Code cleat compiles at run time
;; carries no source file in CLR stack frames, so this checks what the compiler
;; records from the message instead: the var's :file and :line.

(def-repl-test preserves-source-location-test
  (testing "plain eval remembers source file for the compiled function"
    (dorun (repl-eval session "(in-ns 'nrepl.middleware.interruptible-eval-test)"))
    (dorun (nrepl/message session
                          {:op "eval" :code "(defn div0 [] (/ 1 0))"
                           :file "src/acme/foo.clj" :line 42}))
    (let [[resp] (repl-values session "((juxt :file :line) (meta #'div0))")]
      (is (= ["src/acme/foo.clj" 42] resp)))))
