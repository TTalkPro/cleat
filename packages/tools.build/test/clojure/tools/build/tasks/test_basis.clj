;   Copyright (c) Rich Hickey. All rights reserved.
;   The use and distribution terms for this software are covered by the
;   Eclipse Public License 1.0 (http://opensource.org/licenses/eclipse-1.0.php)
;   which can be found in the file epl-v10.html at the root of this distribution.
;   By using this software in any fashion, you are agreeing to be bound by
;   the terms of this license.
;   You must not remove this notice, or any other, from this software.
(ns clojure.tools.build.tasks.test-basis
  (:require
    [clojure.test :refer :all]
    [clojure.tools.build.api :as api]
    [clojure.tools.build.test-util :refer :all])
  (:import
    [System.IO Directory Path]))

(deftest test-missing-project-deps-file
  (let [path (Path/Combine test-out "empty-project")]
    (Directory/CreateDirectory path)
    (with-test-dir path
      (api/set-project-root! *test-dir*)
      (is (= ["src"] (:classpath-roots (api/create-basis {})))))))

;; cleat: what cleat.edn adds over deps.edn's shape
(deftest test-paths-aliases-and-local-deps
  (with-test-dir "test-data/p1"
    (api/set-project-root! *test-dir*)
    (let [basis (api/create-basis nil)]
      (is (= ["src" "resources"] (:paths basis)) ":paths entries naming aliases")
      (is (= {"src" {:path-key :paths} "resources" {:path-key :paths}} (:classpath basis))))
    (api/write-file {:path "lib/cleat.edn" :content {:paths ["src" "res"]}})
    (let [basis (api/create-basis {:extra {:deps {'my/lib {:local/root "lib"}}
                                           :aliases {:test {:extra-paths ["test"] :cleat-opts ["-e" "1"]}}}
                                   :aliases [:test]})
          lib (get-in basis [:libs 'my/lib])]
      (is (= ["src" "resources" "test"] (vec (take 3 (:classpath-roots basis)))))
      (is (= {:path-key :extra-paths} (get-in basis [:classpath "test"])))
      (is (= (Path/GetFullPath (project-path "lib")) (:local/root lib)))
      (is (= [(Path/GetFullPath (project-path "lib/src")) (Path/GetFullPath (project-path "lib/res"))] (:paths lib)))
      (is (= (:paths lib) (vec (drop 3 (:classpath-roots basis)))))
      (is (= {:cleat-opts ["-e" "1"]} (:argmap basis))))
    (is (thrown-with-msg? clojure.lang.ExceptionInfo #"Unsupported dep coordinate"
          (api/create-basis {:project {:deps {'x/y {:mvn/version "1.0"}}}})))))
