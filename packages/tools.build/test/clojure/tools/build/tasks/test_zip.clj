;   Copyright (c) Rich Hickey. All rights reserved.
;   The use and distribution terms for this software are covered by the
;   Eclipse Public License 1.0 (http://opensource.org/licenses/eclipse-1.0.php)
;   which can be found in the file epl-v10.html at the root of this distribution.
;   By using this software in any fashion, you are agreeing to be bound by
;   the terms of this license.
;   You must not remove this notice, or any other, from this software.
(ns clojure.tools.build.tasks.test-zip
  (:require
    [clojure.test :refer :all :as test]
    [clojure.tools.build.api :as api]
    [clojure.tools.build.test-util :refer :all]
    [clojure.tools.build.util.zip :as zip]
    [clojure.tools.build.util.file :as file])
  (:import
    [System.IO File FileInfo Path]))

(deftest test-zip
  (let [zip-path "target/output.zip"]
    (with-test-dir "test-data/p1"
      (api/set-project-root! *test-dir*)
      (api/zip {:src-dirs ["src"]
                :zip-file zip-path})
      (is (true? (exists? (project-path zip-path))))
      (is (= #{"foo/" "foo/bar.clj"}
            (set (map :name (zip/list-zip (project-path zip-path)))))))))

(deftest test-unzip
  (let [zip-path "target/output.zip"]
    (with-test-dir "test-data/p1"
      (api/set-project-root! *test-dir*)
      (api/zip {:src-dirs ["src"]
                :zip-file zip-path})
      (api/unzip {:zip-file zip-path :target-dir "target/unzipped"})
      (let [original (file/collect-files (project-path "src"))
            roundtrip (file/collect-files (project-path "target/unzipped"))]
        (is (= 1 (count original) (count roundtrip)))
        (let [f1 (FileInfo. (first original))
              f2 (FileInfo. (first roundtrip))]
          (is (= (.Name f1) (.Name f2)))
          (is (= (.Length f1) (.Length f2)))
          ;; zip times have a 2 second resolution
          (is (< (Math/Abs (.TotalSeconds (.Subtract (.LastWriteTimeUtc f1) (.LastWriteTimeUtc f2)))) 2.0)))))))

(deftest test-unzip-missing
  (is (thrown? clojure.lang.ExceptionInfo
        (api/unzip {:zip-file (Path/Combine test-out "nope.zip") :target-dir (Path/Combine test-out "nope")}))))
