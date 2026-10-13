;   Copyright (c) Rich Hickey. All rights reserved.
;   The use and distribution terms for this software are covered by the
;   Eclipse Public License 1.0 (http://opensource.org/licenses/eclipse-1.0.php)
;   which can be found in the file epl-v10.html at the root of this distribution.
;   By using this software in any fashion, you are agreeing to be bound by
;   the terms of this license.
;   You must not remove this notice, or any other, from this software.
;; cleat port: the output is one package assembly (designs/17) instead of
;; class files; cleat does not report reflection, so upstream's
;; test-capture-reflection is not ported.

(ns clojure.tools.build.tasks.test-compile-clj
  (:require
    [clojure.test :refer :all :as test]
    [clojure.string :as str]
    [clojure.tools.build.api :as api]
    [clojure.tools.build.tasks.compile-clj :as compile-clj]
    [clojure.tools.build.test-util :refer :all])
  (:import
    [System.IO Path]))

(deftest test-topo
  ;; deps: a -> b -> c, d
  ;; expect: c b a (reverse topo sort), then d at the end
  (is (= '[c b a d]
        (#'compile-clj/nses-in-topo [(Path/Combine package-dir "test-data/nses/src")]))))

(defn- run-hello
  "Load foo.bar from the package assembly only (no sources on the load path)."
  []
  (cleat *test-dir* "-I" (project-path "target/classes") "-e" "(require 'foo.bar) (foo.bar/hello)"
         "-e" "(.Name (.GetName (.Assembly (class foo.bar/hello))))"))

(deftest test-compile
  (with-test-dir "test-data/p1"
    (api/set-project-root! *test-dir*)
    (api/compile-clj {:class-dir "target/classes"
                      :src-dirs ["src"]
                      :lib 'my/p1
                      :basis (api/create-basis nil)})
    (is (true? (exists? (project-path "target/classes/my.p1.dll"))))
    (let [{:keys [exit out]} (run-hello)]
      (is (= 0 exit))
      (is (= "hello\n\"my.p1\"\n" out)))))

(deftest test-compile-basis-paths
  (with-test-dir "test-data/p1"
    (api/set-project-root! *test-dir*)
    (api/compile-clj {:class-dir "target/classes"
                      :assembly "P1"
                      :basis (api/create-basis nil)})
    (is (true? (exists? (project-path "target/classes/P1.dll"))))
    (is (= 0 (:exit (run-hello))))))

(deftest test-compile-passthrough-opts
  (with-test-dir "test-data/p1"
    (api/set-project-root! *test-dir*)
    (api/compile-clj {:class-dir "target/classes"
                      :src-dirs ["src"]
                      :assembly "P1"
                      :basis (api/create-basis nil)
                      ;; pass these through to the cleat command
                      :cleat-opts ["--report" "stdio"]
                      :out :capture})
    (is (true? (exists? (project-path "target/classes/P1.dll"))))))

(deftest test-filter-nses
  (with-test-dir "test-data/nses"
    (api/set-project-root! *test-dir*)
    (api/compile-clj {:class-dir "target/classes" :src-dirs ["src"] :assembly "Nses"
                      :basis (api/create-basis nil) :filter-nses '[a b]})
    ;; c is loaded (b requires it) but stays out of the assembly
    (let [{:keys [out]} (cleat *test-dir* "-I" (project-path "target/classes")
                               "-e" "(map #(Cleat.Core.Runtime.PackageIndex/Contains %) [\"a\" \"b\" \"c\" \"d\"])")]
      (is (= "(true true false false)\n" out)))))

(deftest test-turn-off-assert-with-bindings
  (with-test-dir "test-data/assert"
    (api/set-project-root! *test-dir*)
    (let [basis (api/create-basis nil)
          invoke #(-> {:basis basis :main 'foo.check-assert :cp ["target/classes"]}
                    api/cleat-command
                    (merge {:out :capture, :err :ignore})
                    api/process)
          compile-params {:class-dir "target/classes" :src-dirs ["src"] :basis basis :assembly "Assert"}]

      ;; by default, assertions are on when compiling, then invocation fails (assertion expects keyword)
      (api/compile-clj compile-params) ;; no :bindings set
      (is (= {:exit 1} (invoke)))

      ;; recompile with binding to turn off assertions, then it passes (assertion not checked)
      (api/delete {:path "target/classes"})
      (api/compile-clj (assoc compile-params :bindings {#'clojure.core/*assert* false})) ;; turn off asserts
      (is (= {:exit 0, :out (str "100" System.Environment/NewLine)} (invoke))))))

(deftest test-accidental-basis-delay
  (with-test-dir "test-data/p1"
    (api/set-project-root! *test-dir*)
    (is (thrown? clojure.lang.ExceptionInfo
          (api/compile-clj {:class-dir "target/classes"
                            :src-dirs ["src"]
                            :basis (delay (api/create-basis nil))})))))

(deftest test-compile-failure-keeps-working-dir
  (with-test-dir "test-data/p1"
    (api/set-project-root! *test-dir*)
    (api/write-file {:path "src/foo/broken.clj" :string "(ns foo.broken)\n(this-does-not-resolve)\n"})
    (let [e (try (api/compile-clj {:class-dir "target/classes" :src-dirs ["src"] :assembly "P1"
                                   :basis (api/create-basis nil) :err :capture})
                 nil
                 (catch clojure.lang.ExceptionInfo e e))]
      (is (some? e))
      (is (str/starts-with? (ex-message e) "Clojure compilation failed, working dir preserved: "))
      (is (str/includes? (:err (ex-data e)) "this-does-not-resolve")))))
