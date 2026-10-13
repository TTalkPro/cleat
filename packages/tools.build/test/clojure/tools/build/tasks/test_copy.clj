;   Copyright (c) Rich Hickey. All rights reserved.
;   The use and distribution terms for this software are covered by the
;   Eclipse Public License 1.0 (http://opensource.org/licenses/eclipse-1.0.php)
;   which can be found in the file epl-v10.html at the root of this distribution.
;   By using this software in any fashion, you are agreeing to be bound by
;   the terms of this license.
;   You must not remove this notice, or any other, from this software.
(ns clojure.tools.build.tasks.test-copy
  (:require
    [clojure.test :refer :all]
    [clojure.string :as str]
    [clojure.tools.build.api :as api]
    [clojure.tools.build.util.file :as file]
    [clojure.tools.build.test-util :refer :all])
  (:import
    [System.IO File UnixFileMode]))

(deftest test-copy
  (with-test-dir "test-data/p1"
    (let [txt (str (Guid/NewGuid))]
      (api/set-project-root! *test-dir*)
      (api/copy-dir {:target-dir "target/classes"
                     :src-dirs ["src" "resources"]
                     :replace {"__REPLACE__" txt}})
      (let [source-file (project-path "target/classes/foo/bar.clj")
            contents    (slurp source-file)]
        (is (exists? source-file))
        (is (str/includes? contents txt)))

      ;; binary files in replaced exts should be copied but not replaced
      (let [binary-in (project-path "resources/test.png")
            binary-out (project-path "target/classes/test.png")]
        (is (exists? binary-out))
        (is (= (seq (File/ReadAllBytes binary-in)) (seq (File/ReadAllBytes binary-out))))))))

(deftest test-copy-include-glob
  (with-test-dir "test-data/p1"
    (api/set-project-root! *test-dir*)
    (api/copy-dir {:target-dir "target/edn" :src-dirs ["resources"] :include "*.edn"})
    (is (exists? (project-path "target/edn/data.edn")))
    (is (not (exists? (project-path "target/edn/test.png"))))))

(deftest test-replace-retains-perms
  (when-not windows?
   (with-test-dir "test-data/p1"
     (api/set-project-root! *test-dir*)
     (let [start-file (project-path "target/x/f")]
       (file/ensure-file start-file "abc")
       (File/SetUnixFileMode start-file (enum-or UnixFileMode/UserRead UnixFileMode/GroupRead UnixFileMode/UserExecute))
       (api/copy-dir {:src-dirs [(project-path "target/x")] :target-dir (project-path "target/y") :replace {"abc" "xyz"}})
       (let [end (project-path "target/y/f")]
         (is (= (slurp end) "xyz"))
         (is (.HasFlag (File/GetUnixFileMode end) UnixFileMode/UserExecute)))))))
