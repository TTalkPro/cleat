;   Copyright (c) Rich Hickey. All rights reserved.
;   The use and distribution terms for this software are covered by the
;   Eclipse Public License 1.0 (http://opensource.org/licenses/eclipse-1.0.php)
;   which can be found in the file epl-v10.html at the root of this distribution.
;   By using this software in any fashion, you are agreeing to be bound by
;   the terms of this license.
;   You must not remove this notice, or any other, from this software.
;; cleat: running build functions (clj -T:build upstream), designs/18 §5.

(ns clojure.tools.build.test-main
  (:require
    [clojure.edn :as edn]
    [clojure.test :refer :all]
    [clojure.tools.build.test-util :refer :all])
  (:import
    [System.IO Path]))

(deftest test-run-build-function
  (with-test-dir "test-data/p1"
    (spit (project-path "build.clj")
          (str "(ns build (:require [clojure.tools.build.api :as b]))\n"
               "(defn write [{:keys [x path]}] (b/write-file {:path path :content x}))\n"))
    (let [{:keys [exit err]} (cleat *test-dir*
                                    "-I" (Path/Combine package-dir "src")
                                    "-I" (Path/Combine package-dir ".." "tools.namespace" "src")
                                    "-I" "."
                                    "-m" "clojure.tools.build.main" "write" ":x" "[1 :two \"3\"]" ":path" "\"target/out.edn\"")]
      (is (= 0 exit) err)
      (is (= [1 :two "3"] (edn/read-string (slurp (project-path "target/out.edn"))))))
    (let [{:keys [exit err]} (cleat *test-dir* "-I" (Path/Combine package-dir "src") "-I" "."
                                    "-m" "clojure.tools.build.main" "write" ":x")]
      (is (= 2 exit))
      (is (re-find #"must come in pairs" err)))))
