;   Copyright (c) Rich Hickey. All rights reserved.
;   The use and distribution terms for this software are covered by the
;   Eclipse Public License 1.0 (http://opensource.org/licenses/eclipse-1.0.php)
;   which can be found in the file epl-v10.html at the root of this distribution.
;   By using this software in any fashion, you are agreeing to be bound by
;   the terms of this license.
;   You must not remove this notice, or any other, from this software.
;; cleat port: uber writes a deployable directory (the runtime at its root,
;; the project and deps under lib/) instead of an uberjar; local deps are
;; directories (cleat has no jars), so upstream's test-conflicts (jars) is
;; covered by test-conflicts-but-files.

(ns clojure.tools.build.tasks.test-uber
  (:require
    [clojure.set :as set]
    [clojure.string :as str]
    [clojure.test :refer :all]
    [clojure.tools.build.api :as api]
    [clojure.tools.build.tasks.uber :as uber]
    [clojure.tools.build.test-util :refer :all]
    [clojure.tools.build.util.file :as file])
  (:import
    [clojure.lang ExceptionInfo]
    [System.IO MemoryStream Path]))

(defn- string->stream
  [^String s]
  (MemoryStream. (.GetBytes System.Text.Encoding/UTF8 s)))

(deftest string-stream-rt
  (are [s] (= s (uber/stream->string (string->stream s)))
    ""
    "abc"))

(defn- lib-files [uber-dir]
  (let [lib (Path/Combine uber-dir "lib")]
    (set (map #(str/replace (Path/GetRelativePath lib %) "\\" "/") (file/collect-files lib)))))

(deftest test-uber
  (with-test-dir "test-data/p1"
    (api/set-project-root! *test-dir*)
    (let [basis (api/create-basis nil)]
      (api/compile-clj {:class-dir "target/classes" :src-dirs ["src"] :assembly "P1" :basis basis})
      (api/dotnet-build {:project "dotnet/Demo" :output-dir "target/classes" :out :capture})
      (api/uber {:class-dir "target/classes"
                 :basis basis
                 :uber-dir "target/uber"
                 :main 'foo.bar}))
    (let [uber-dir (project-path "target/uber")]
      (is (set/subset? #{"P1.dll" "Demo.dll"} (lib-files uber-dir)))
      (doseq [f ["cleat.dll" "Cleat.Core.dll" "cleat.scheme.boot.dll" "cleat.boot.dll" "cleat.scheme.clojure" "run" "run.cmd"]]
        (is (exists? (Path/Combine uber-dir f)) f))
      (is (str/includes? (slurp (Path/Combine uber-dir "run")) "-m foo.bar"))
      ;; the directory runs on its own (only dotnet needed)
      (when-not windows?
        (let [{:keys [exit out]} (api/process {:command-args [(Path/Combine uber-dir "run")] :out :capture})]
          (is (= 0 exit))
          (is (= "hello\n" out)))))))

(deftest test-conflicts-but-files
  (with-test-dir "test-data/uber-conflict"
    (api/set-project-root! *test-dir*)

    ;; make dirs
    (doseq [j ["j1" "j2" "j3"]]
      (let [classes (format "target/%s/classes" j)]
        (api/copy-dir {:target-dir classes :src-dirs [j]})
        (spit (project-path (format "target/%s/classes/cleat.edn" j)) "{:paths [\".\"]}")))

    ;; uber including j1, j2, j3 as local dirs
    (api/uber {:class-dir "target/classes"
               :basis (api/create-basis {:project {:deps {'dummy/j1 {:local/root "target/j1/classes"}
                                                          'dummy/j2 {:local/root "target/j2/classes"}
                                                          'dummy/j3 {:local/root "target/j3/classes"}}}})
               :uber-dir "target/conflict"
               :conflict-handlers {"ignore.txt" :ignore
                                   "overwrite.txt" :overwrite
                                   "append.txt" :append}})

    ;; non-conflicting files are combined, conflicting files are reconciled
    (is
     (set/subset?
      #{"META-INF/LICENSE.txt"
        "data_readers.clj"
        "my/j1.txt" "my/j2.txt"
        "ignore.txt" "overwrite.txt" "append.txt"}
      (lib-files (project-path "target/conflict"))))

    ;; data_readers.clj merge
    (is (= '{j1a my.foo/j1a-reader, j1b my.bar/j1b-reader,
             j2a my.foo/j2a-reader, j2b my.bar/j2b-reader}
           (read-string (slurp (project-path "target/conflict/lib/data_readers.clj")))))

    ;; data_readers.cljc merge
    (is (= {'j1a (reader-conditional '(:cljs my.cljs.foo/j1a-reader :clj my.clj.foo/j1a-reader) false)
            'j1b (reader-conditional '(:cljs my.cljs.foo/j1b-reader :clj my.clj.foo/j1b-reader) false)
            'j2a (reader-conditional '(:cljs my.cljs.foo/j2a-reader :clj my.clj.foo/j2a-reader) false)
            'j2b (reader-conditional '(:cljs my.cljs.foo/j2b-reader :clj my.clj.foo/j2b-reader) false)}
           (read-string {:read-cond :preserve :features #{:clj}}
                        (slurp (project-path "target/conflict/lib/data_readers.cljc")))))

    ;; ignore files ignore, so first one wins
    (is (= (slurp (project-path "j1/ignore.txt"))
           (slurp (project-path "target/conflict/lib/ignore.txt"))))

    ;; overwrite files overwrite, so last wins
    (is (= (slurp (project-path "j2/overwrite.txt"))
           (slurp (project-path "target/conflict/lib/overwrite.txt"))))

    ;; append files append
    (is (= (str (slurp (project-path "j1/append.txt")) "\n"
                (slurp (project-path "j2/append.txt")))
           (slurp (project-path "target/conflict/lib/append.txt"))))

    ;; LICENSE files append but no dupes - include j1 and j2, but not j3 (dupe of j1)
    (is (= (str (slurp (project-path "j1/META-INF/LICENSE.txt")) "\n"
                (slurp (project-path "j2/META-INF/LICENSE.txt")))
           (slurp (project-path "target/conflict/lib/META-INF/LICENSE.txt"))))))

(deftest test-dir-file-collision
  (with-test-dir "test-data/case-sensitive-collision"
    (api/set-project-root! *test-dir*)
    (api/write-file {:path "j1/foo" :string "a file where j2 has a dir"})
    (doseq [j ["j1" "j2"]]
      (spit (project-path j "cleat.edn") "{:paths [\".\"]}"))
    (let [basis (api/create-basis {:project {:deps {'dummy/j1 {:local/root "j1"}
                                                    'dummy/j2 {:local/root "j2"}}}})]
      ;; a file from j1 where j2 has a directory: a conflict
      (is (= "Cannot write foo/hi.txt from dummy/j2 as parent dir is a file from another lib. One of them must be excluded."
             (try
               (api/uber {:class-dir "target/classes", :basis basis, :uber-dir "target/collision"})
               nil
               (catch ExceptionInfo ex (ex-message ex)))))

      ;; excluding one of the conflicts
      (api/uber {:class-dir "target/classes", :basis basis, :uber-dir "target/collision"
                 :exclude ["foo" "FOO" "cleat.edn"]})
      (is (= #{"foo/hi.txt"} (lib-files (project-path "target/collision")))))))
