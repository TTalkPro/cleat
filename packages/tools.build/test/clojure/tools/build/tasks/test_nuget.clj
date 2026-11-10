;   Copyright (c) Rich Hickey. All rights reserved.
;   The use and distribution terms for this software are covered by the
;   Eclipse Public License 1.0 (http://opensource.org/licenses/eclipse-1.0.php)
;   which can be found in the file epl-v10.html at the root of this distribution.
;   By using this software in any fashion, you are agreeing to be bound by
;   the terms of this license.
;   You must not remove this notice, or any other, from this software.
;; cleat: write-pom + jar -> pack (nupkg), install -> the local NuGet feed,
;; push (new), and create-basis resolving a packed package through NuGet. The
;; runtime package is a stub (Cleat.Runtime 0.0.1-stub) so that restore runs
;; offline; the consumer's nuget.config clears all sources, the local feed is
;; the only one.

(ns clojure.tools.build.tasks.test-nuget
  (:require
    [clojure.string :as str]
    [clojure.test :refer :all]
    [clojure.tools.build.api :as api]
    [clojure.tools.build.tasks.install :as install]
    [clojure.tools.build.util.zip :as zip]
    [clojure.tools.build.test-util :refer :all])
  (:import
    [System.IO Directory File Path SearchOption]
    [System.IO.Compression ZipArchive ZipArchiveMode]))

(def ^:private stub-runtime "0.0.1-stub")

(defn- nupkg-text
  "The text of a part of the nupkg."
  [nupkg part]
  (with-open [z (ZipArchive. (File/OpenRead nupkg) ZipArchiveMode/Read false)]
    (when-let [e (.GetEntry z part)]
      (with-open [r (System.IO.StreamReader. (.Open e))] (.ReadToEnd r)))))

(defn- pack-p1!
  "Compile p1 and pack it as P1 1.0.0; returns the nupkg path."
  []
  (let [basis (api/create-basis nil)]
    (api/compile-clj {:class-dir "target/classes" :src-dirs ["src"] :assembly "P1" :basis basis})
    (api/pack {:basis basis :lib 'P1 :version "1.0.0" :class-dir "target/classes" :nupkg-file "target/P1.1.0.0.nupkg"
               :description "p1 test package" :authors ["a" "b"] :license "EPL-1.0" :tags ["clojure" "cleat"]
               :src-dirs ["src"] :runtime-version stub-runtime})
    (project-path "target/P1.1.0.0.nupkg")))

(defn- stub-runtime!
  "A Cleat.Runtime package with no assemblies; returns its nupkg path."
  []
  ;; _._ marks an intentionally empty lib/net10.0 (NuGet convention)
  (api/write-file {:path "target/stub/_._" :string ""})
  (api/pack {:basis {} :lib 'Cleat.Runtime :version stub-runtime :class-dir "target/stub"
             :nupkg-file "target/Cleat.Runtime.nupkg"})
  (project-path "target/Cleat.Runtime.nupkg"))

(deftest test-pack
  (with-test-dir "test-data/p1"
    (api/set-project-root! *test-dir*)
    (let [nupkg (pack-p1!)
          names (set (map :name (zip/list-zip nupkg)))
          spec (nupkg-text nupkg "P1.nuspec")]
      ;; cleat-158：包程序集现在带 PDB（序列点），pack 把它一起装进 nupkg
      (is (= #{"_rels/.rels" "[Content_Types].xml" "P1.nuspec" "lib/net10.0/P1.dll" "lib/net10.0/P1.pdb" "src/foo/bar.clj"} names))
      (is (str/includes? spec "<id>P1</id>"))
      (is (str/includes? spec "<version>1.0.0</version>"))
      (is (str/includes? spec "<authors>a, b</authors>"))
      (is (str/includes? spec "<license type=\"expression\">EPL-1.0</license>"))
      (is (str/includes? spec "<tags>clojure cleat</tags>"))
      (is (str/includes? spec (str "<dependency id=\"Cleat.Runtime\" version=\"[" stub-runtime "]\"")))   ; 精确版本（cleat-187）
      (is (str/includes? (nupkg-text nupkg "[Content_Types].xml") "Extension=\"dll\""))
      (is (= ["P1" "1.0.0"] (install/nupkg-identity nupkg))))))

(deftest test-pack-rejects-local-deps
  (with-test-dir "test-data/p1"
    (api/set-project-root! *test-dir*)
    (api/write-file {:path "target/classes/x.dll" :string ""})
    (is (thrown-with-msg? clojure.lang.ExceptionInfo #":local/root deps can't be NuGet dependencies"
          (api/pack {:basis {:deps {'my/lib {:local/root "../lib"}}} :lib 'P1 :version "1.0.0"
                     :class-dir "target/classes" :nupkg-file "target/x.nupkg"})))))

(deftest test-install-restore-and-push
  (with-test-dir "test-data/p1"
    (api/set-project-root! *test-dir*)
    (let [feed (project-path "feed")
          p1 (pack-p1!)]
      (api/install {:nupkg-file (stub-runtime!) :local-feed feed})
      (api/install {:nupkg-file p1 :local-feed feed})
      (is (exists? (Path/Combine feed "p1.1.0.0.nupkg")))
      (is (exists? (Path/Combine feed (str "cleat.runtime." stub-runtime ".nupkg"))))

      ;; a consumer project depending on P1 through NuGet
      (api/write-file {:path "app/cleat.edn" :content {:paths ["src"] :deps {'P1 {:nuget/version "1.0.0"}}}})
      (api/write-file {:path "app/nuget.config"
                       :string "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<configuration><packageSources><clear /></packageSources></configuration>\n"})
      (let [basis (api/with-project-root (project-path "app") (api/create-basis {:local-feed feed}))
            p1-lib (get-in basis [:libs 'P1])
            [p1-dir] (:paths p1-lib)]
        (is (= "1.0.0" (:nuget/version p1-lib)))
        (is (exists? (Path/Combine p1-dir "P1.dll")))
        (is (not (contains? (:libs basis) 'Cleat.Runtime)) "the runtime package stays off the load path")
        (is (= ["src" p1-dir] (:classpath-roots basis)))
        (is (= {:lib-name 'P1} (get-in basis [:classpath p1-dir])))

        ;; run code from the restored package
        (let [{:keys [exit out]} (api/with-project-root (project-path "app")
                                   (api/process (merge (api/cleat-command {:basis basis :main 'foo.bar})
                                                       {:out :capture})))]
          (is (= 0 exit))
          (is (= "hello\n" out)))

        ;; reinstalling the same version drops it from the global packages folder
        (let [cached (Path/Combine (System.Environment/GetEnvironmentVariable "NUGET_PACKAGES") "p1" "1.0.0")]
          (is (exists? cached))
          (api/install {:nupkg-file p1 :local-feed feed})
          (is (not (exists? cached)))))

      ;; push to a folder source (the same command pushes to nuget.org or BaGetter)
      (let [pushed (project-path "pushed")]
        (Directory/CreateDirectory pushed)
        (api/push {:nupkg-file p1 :source pushed :out :capture})
        (is (seq (Directory/GetFiles pushed "*.nupkg" SearchOption/AllDirectories)))))))
