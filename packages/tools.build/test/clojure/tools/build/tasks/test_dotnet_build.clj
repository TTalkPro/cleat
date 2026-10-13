;   Copyright (c) Rich Hickey. All rights reserved.
;   The use and distribution terms for this software are covered by the
;   Eclipse Public License 1.0 (http://opensource.org/licenses/eclipse-1.0.php)
;   which can be found in the file epl-v10.html at the root of this distribution.
;   By using this software in any fashion, you are agreeing to be bound by
;   the terms of this license.
;   You must not remove this notice, or any other, from this software.
;; cleat port: javac -> dotnet-build, a C# project instead of Java sources.

(ns clojure.tools.build.tasks.test-dotnet-build
  (:require
    [clojure.string :as str]
    [clojure.test :refer :all :as test]
    [clojure.tools.build.api :as api]
    [clojure.tools.build.test-util :refer :all]))

(deftest test-dotnet-build
  (with-test-dir "test-data/p1"
    (api/set-project-root! *test-dir*)
    (let [{:keys [output-dir]} (api/dotnet-build {:project "dotnet/Demo/Demo.csproj"
                                                  :output-dir "target/dotnet"
                                                  :out :capture})]
      (is (= output-dir (System.IO.Path/GetFullPath (project-path "target/dotnet"))))
      (is (true? (exists? (project-path "target/dotnet/Demo.dll"))))
      ;; the output dir on the load path: Clojure code uses the types by name
      (let [{:keys [exit out]} (cleat *test-dir* "-I" (project-path "target/dotnet")
                                      "-e" "(Foo.Demo1/Hello)" "-e" "(.Greet (Foo.Demo2.) \"cleat\")")]
        (is (= 0 exit))
        (is (= "\"Hello\"\n\"Hello cleat\"\n" out))))))

(deftest test-dotnet-build-failure
  (with-test-dir "test-data/p1"
    (api/set-project-root! *test-dir*)
    (api/write-file {:path "dotnet/Demo/Broken.cs" :string "namespace Foo { class Broken { int X() => \"no\"; } }"})
    (is (thrown-with-msg? clojure.lang.ExceptionInfo #"dotnet build failed"
          (api/dotnet-build {:project "dotnet/Demo" :output-dir "target/dotnet" :out :capture})))))
