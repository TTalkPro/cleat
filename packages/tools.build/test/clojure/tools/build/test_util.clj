;   Copyright (c) Rich Hickey. All rights reserved.
;   The use and distribution terms for this software are covered by the
;   Eclipse Public License 1.0 (http://opensource.org/licenses/eclipse-1.0.php)
;   which can be found in the file epl-v10.html at the root of this distribution.
;   By using this software in any fashion, you are agreeing to be bound by
;   the terms of this license.
;   You must not remove this notice, or any other, from this software.

;; cleat port: System.IO; test projects are copied from the package's
;; test-data/ into a fresh directory under the system temp dir, and NuGet's
;; global packages folder is a temp dir for the whole run (the NuGet tests
;; create stub packages that must not reach the user's cache).

(ns clojure.tools.build.test-util
  (:require
    [clojure.string :as str]
    [clojure.test :as test]
    [clojure.tools.build.api :as api]
    [clojure.tools.build.tasks.process :as process]
    [clojure.tools.build.util.file :as file])
  (:import
    [System.IO Directory File Path]))

(def package-dir
  "packages/tools.build"
  (Path/GetFullPath (Path/Combine (Path/GetDirectoryName (Path/GetFullPath *file*)) ".." ".." ".." "..")))

(def test-out
  (Path/Combine (Path/GetTempPath) (str "cleat-tools-build-" (.ToString (Guid/NewGuid) "N"))))

(defonce ^:private isolate-nuget
  (System.Environment/SetEnvironmentVariable "NUGET_PACKAGES" (Path/Combine test-out "nuget-packages")))

(def ^:dynamic *test-dir* nil)

(defmacro with-test-dir
  [test-project & body]
  `(let [name# (-> test/*testing-vars* last symbol str (str/replace "/" "_"))
         dir# (Path/Combine test-out name#)
         src# (let [p# ~test-project]
                (if (Path/IsPathRooted p#) p# (Path/Combine package-dir p#)))]
     (file/delete dir#)
     (Directory/CreateDirectory dir#)
     (file/copy-contents src# dir#)
     (binding [*test-dir* dir#]
       (try
         ~@body
         (finally
           (api/set-project-root! "."))))))

(defn project-path [& parts]
  (str/join "/" (cons *test-dir* parts)))

(defn exists? [p] (file/exists? p))

(defn submap?
  "Is m1 a subset of m2?"
  [m1 m2]
  (if (and (map? m1) (map? m2))
    (every? (fn [[k v]] (and (contains? m2 k)
                          (submap? v (get m2 k))))
      m1)
    (= m1 m2)))

(def windows? (System.OperatingSystem/IsWindows))

(defn cleat
  "Run cleat (the one running the tests) with args in dir; {:exit :out :err}."
  [dir & args]
  (api/process {:command-args (into (process/cleat-executable) args)
                :dir dir :out :capture :err :capture}))
