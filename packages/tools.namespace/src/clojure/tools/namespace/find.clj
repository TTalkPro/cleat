;; Copyright (c) Stuart Sierra, 2012. All rights reserved. The use and
;; distribution terms for this software are covered by the Eclipse
;; Public License 1.0 (http://opensource.org/licenses/eclipse-1.0.php)
;; which can be found in the file epl-v10.html at the root of this
;; distribution. By using this software in any fashion, you are
;; agreeing to be bound by the terms of this license. You must not
;; remove this notice, or any other, from this software.

;; cleat port: directories only (searching the classpath and JAR files
;; has no CLR counterpart); directories are System.IO.DirectoryInfo or
;; paths, files are System.IO.FileInfo.

(ns
  ^{:author "Stuart Sierra",
     :doc "Search for namespace declarations in directories."}
  clojure.tools.namespace.find
  (:require [clojure.tools.namespace.file :as file]
            [clojure.tools.namespace.parse :as parse])
  (:import (System.IO Directory DirectoryInfo FileInfo IOException SearchOption)))

(def ^{:added "0.3.0"}
  clj
  "Platform definition of file extensions and reader options for
  Clojure (.clj, .cljc and .cljr) source files."
  {:read-opts parse/clj-read-opts
   :extensions file/clojure-extensions})

(def ^{:added "0.3.0"}
  cljs
  "Platform definition of file extensions and reader options for
  ClojureScript (.cljs and .cljc) source files."
  {:read-opts parse/cljs-read-opts
   :extensions file/clojurescript-extensions})

(defmacro ^:private ignore-reader-exception
  "If body throws an exception from reading (a syntax error), returns nil.
  Rethrows I/O errors. (The cleat reader throws plain CLR exceptions, not
  tools.reader's ex-info with :type :reader-exception.)"
  [& body]
  `(try ~@body
        (catch IOException e# (throw e#))
        (catch UnauthorizedAccessException e# (throw e#))
        (catch Exception e#
          nil)))

(defn- dir-path ^String [dir]
  (cond
    (instance? DirectoryInfo dir) (.FullName ^DirectoryInfo dir)
    (instance? FileInfo dir) (.FullName ^FileInfo dir)
    :else (System.IO.Path/GetFullPath (str dir))))

(defn- dir-name ^String [dir]
  (.Name (DirectoryInfo. (dir-path dir))))

;;; Finding namespaces in a directory tree

(defn- sort-files-breadth-first
  [files]
  (sort-by #(.FullName ^FileInfo %) files))

(defn find-sources-in-dir
  "Searches recursively under dir for source files. Returns a sequence
  of FileInfo objects, in breadth-first sort order.

  Optional second argument is either clj (default) or cljs, both
  defined in clojure.tools.namespace.find."
  {:added "0.3.0"}
  ([dir]
   (find-sources-in-dir dir nil))
  ([dir platform]
   (let [{:keys [extensions]} (or platform clj)
         path (dir-path dir)]
     (if (Directory/Exists path)
       (->> (Directory/EnumerateFiles path "*" SearchOption/AllDirectories)
            (map #(FileInfo. ^String %))
            (filter #(file/file-with-extension? % extensions))
            sort-files-breadth-first)
       ()))))

(defn find-clojure-sources-in-dir
  "DEPRECATED: replaced by find-sources-in-dir

  Searches recursively under dir for Clojure source files.
  Returns a sequence of FileInfo objects, in breadth-first sort order."
  {:added "0.2.0"
   :deprecated "0.3.0"}
  [dir]
  (find-sources-in-dir dir clj))

(defn find-ns-decls-in-dir
  "Searches dir recursively for (ns ...) declarations in Clojure
  source files; returns the unevaluated ns declarations.
  The symbol name in the returned ns declaration will contain metadata
  for the corresponding directory and located file within at keys
  :dir and :file.

  Optional second argument platform is either clj (default) or cljs,
  both defined in clojure.tools.namespace.find."
  {:added "0.2.0"}
  ([dir] (find-ns-decls-in-dir dir nil))
  ([dir platform]
   (keep #(ignore-reader-exception
           (let [[_ nom & more :as decl] (file/read-file-ns-decl % (:read-opts platform))]
             (when (and decl nom (symbol? nom))
               (list* 'ns (with-meta nom
                            {:dir (dir-name dir) :file (.Name ^FileInfo %)})
                      more))))
         (find-sources-in-dir dir platform))))

(defn find-namespaces-in-dir
  "Searches dir recursively for (ns ...) declarations in Clojure
  source files; returns the symbol names of the declared namespaces.

  Optional second argument platform is either clj (default) or cljs,
  both defined in clojure.tools.namespace.find."
  {:added "0.3.0"}
  ([dir] (find-namespaces-in-dir dir nil))
  ([dir platform]
   (map parse/name-from-ns-decl (find-ns-decls-in-dir dir platform))))

;;; Finding namespaces

(defn find-ns-decls
  "Searches a sequence of directories for platform source files
  containing (ns...) declarations. Returns a sequence of the unevaluated
  ns declaration forms. (Entries that are not directories are skipped:
  there is no JAR search on cleat.)

  Optional second argument platform is either clj (default) or cljs,
  both defined in clojure.tools.namespace.find."
  ([files]
   (find-ns-decls files nil))
  ([files platform]
   (mapcat #(find-ns-decls-in-dir % platform)
           (filter #(Directory/Exists (dir-path %)) files))))

(defn find-namespaces
  "Searches a sequence of directories for platform source files
  containing (ns...) declarations. Returns a sequence of the symbol
  names of the declared namespaces.

  Optional second argument platform is either clj (default) or cljs,
  both defined in clojure.tools.namespace.find."
  ([files]
   (find-namespaces files nil))
  ([files platform]
   (map parse/name-from-ns-decl (find-ns-decls files platform))))
