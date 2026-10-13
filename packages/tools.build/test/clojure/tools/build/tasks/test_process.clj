;   Copyright (c) Rich Hickey. All rights reserved.
;   The use and distribution terms for this software are covered by the
;   Eclipse Public License 1.0 (http://opensource.org/licenses/eclipse-1.0.php)
;   which can be found in the file epl-v10.html at the root of this distribution.
;   By using this software in any fashion, you are agreeing to be bound by
;   the terms of this license.
;   You must not remove this notice, or any other, from this software.
(ns clojure.tools.build.tasks.test-process
  (:require
    [clojure.test :refer :all]
    [clojure.set :as set]
    [clojure.string :as str]
    [clojure.tools.build.api :as api]
    [clojure.tools.build.tasks.process :as process]
    [clojure.tools.build.test-util :refer :all]))

(deftest test-large-process-output
  (is (string? (api/git-process {:git-args ["log" "-n" "200"] :dir package-dir}))))

;; cleat: dotnet instead of java
(deftest test-capture
  (is (re-find #"^\d+\." (:out (api/process {:command-args ["dotnet" "--version"]
                                                 :out :capture})))))

(deftest test-env-and-capture
  (when-not windows?
    (is (= "hi\n"
          (:out
            (api/process {:env {"FOO" "hi"}
                          :command-args ["/bin/sh" "-c" "echo $FOO"]
                          :out :capture}))))))

(deftest test-write-and-ignore
  (when-not windows?
    (with-test-dir "test-data/p1"
      (api/set-project-root! *test-dir*)
      (is (= {:exit 0} (api/process {:command-args ["/bin/sh" "-c" "echo out; echo err >&2"]
                                     :out :write :out-file "target/out.txt" :err :ignore})))
      (is (= "out\n" (slurp (project-path "target/out.txt")))))))

(deftest test-inherit-goes-to-out
  (when-not windows?
    (is (= "via *out*\n" (with-out-str (api/process {:command-args ["/bin/sh" "-c" "echo via \\*out\\*"]}))))))

;; cleat: java-command -> cleat-command
(deftest test-cleat-command-uses-and-merges-basis-cleat-opts
  (let [basis (api/create-basis {:project {:paths ["src"]}
                                 :extra {:aliases {:opts {:cleat-opts ["-e" "1"]}}}
                                 :aliases [:opts]})
        command (api/cleat-command {:basis basis, :main 'my.app, :cleat-opts ["--report" "stdio"]
                                    :cp ["extra"] :main-args ["a" "b"]})
        args (:command-args command)]
    (is (= ["-I" "extra" "-I" "src" "--report" "stdio" "-e" "1" "-m" "my.app" "a" "b"]
           (subvec args (- (count args) 12))))))
