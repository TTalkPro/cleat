;; Copyright (c) Stuart Sierra, 2012. All rights reserved. The use and
;; distribution terms for this software are covered by the Eclipse
;; Public License 1.0 (http://opensource.org/licenses/eclipse-1.0.php)
;; which can be found in the file epl-v10.html at the root of this
;; distribution. By using this software in any fashion, you are
;; agreeing to be bound by the terms of this license. You must not
;; remove this notice, or any other, from this software.

;; cleat port: files are System.IO.FileInfo (paths as strings are accepted
;; too); .cljr is a Clojure extension (the cleat loader tries it first).

(ns ^{:author "Stuart Sierra"
      :doc "Read and track namespace information from files"}
  clojure.tools.namespace.file
  (:require [clojure.tools.namespace.parse :as parse]
            [clojure.tools.namespace.track :as track])
  (:import (System.IO FileInfo StreamReader)
           (clojure.lang PushbackTextReader)))

(defn as-file
  "A System.IO.FileInfo for file: a FileInfo, or a path."
  ^FileInfo [file]
  (if (instance? FileInfo file) file (FileInfo. (str file))))

(defn read-file-ns-decl
  "Attempts to read a (ns ...) declaration from file, and returns the
  unevaluated form. Returns nil if ns declaration cannot be found.
  read-opts is passed through to read."
  ([file]
   (read-file-ns-decl file nil))
  ([file read-opts]
   (with-open [rdr (PushbackTextReader. (StreamReader. (.FullName (as-file file))))]
     (parse/read-ns-decl rdr read-opts))))

(defn file-with-extension?
  "Returns true if the file (a FileInfo or path) is a file whose name ends
  with one of the Strings in extensions."
  {:added "0.3.0"}
  [file extensions]
  (let [file (as-file file)]
    (and (.Exists file)
         (let [name (.Name file)]
           (boolean (some #(.EndsWith name ^String % StringComparison/Ordinal) extensions))))))

(def ^{:added "0.3.0"}
  clojure-extensions
  "File extensions for Clojure files (on cleat: .cljr too)."
  (list ".clj" ".cljc" ".cljr"))

(def ^{:added "0.3.0"}
  clojurescript-extensions
  "File extensions for ClojureScript files."
  (list ".cljs" ".cljc"))

(defn clojure-file?
  "Returns true if the file will be read by the Clojure compiler."
  [file]
  (file-with-extension? file clojure-extensions))

(defn clojurescript-file?
  "Returns true if the file will be read by the ClojureScript compiler."
  {:added "0.3.0"}
  [file]
  (file-with-extension? file clojurescript-extensions))

;;; Dependency tracker

(defn- files-and-deps [files read-opts]
  (reduce (fn [m file]
            (if-let [decl (read-file-ns-decl file read-opts)]
              (let [deps (parse/deps-from-ns-decl decl)
                    name (parse/name-from-ns-decl decl)]
                (-> m
                    (assoc-in [:depmap name] deps)
                    (assoc-in [:filemap file] name)))
              m))
          {} files))

(def ^:private merge-map (fnil merge {}))

(defn add-files
  "Reads ns declarations from files; returns an updated dependency
  tracker with those files added. read-opts is passed through to read."
  ([tracker files]
   (add-files tracker files nil))
  ([tracker files read-opts]
   (let [{:keys [depmap filemap]} (files-and-deps files read-opts)]
     (-> tracker
         (track/add depmap)
         (update-in [::filemap] merge-map filemap)))))

(defn remove-files
  "Returns an updated dependency tracker with files removed. The files
  must have been previously added with add-files."
  [tracker files]
  (-> tracker
      (track/remove (keep (::filemap tracker {}) files))
      (update-in [::filemap] #(apply dissoc % files))))
