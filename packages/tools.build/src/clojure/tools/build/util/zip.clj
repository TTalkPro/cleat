;   Copyright (c) Rich Hickey. All rights reserved.
;   The use and distribution terms for this software are covered by the
;   Eclipse Public License 1.0 (http://opensource.org/licenses/eclipse-1.0.php)
;   which can be found in the file epl-v10.html at the root of this distribution.
;   By using this software in any fashion, you are agreeing to be bound by
;   the terms of this license.
;   You must not remove this notice, or any other, from this software.

;; cleat port: System.IO.Compression.ZipArchive; no jar manifests.

(ns clojure.tools.build.util.zip
  (:require
    [clojure.tools.build.util.file :as file]
    [clojure.string :as str])
  (:import
    [System.IO Directory File FileMode FileStream Path Stream]
    [System.IO.Compression ZipArchive ZipArchiveEntry ZipArchiveMode]))

(defn- add-zip-entry
  [^ZipArchive archive ^String path ^String file]
  (let [dir (Directory/Exists file)
        path (str/replace path \\ \/) ;; only use unix-style paths in zips
        path (if (and dir (not (str/ends-with? path "/"))) (str path "/") path)
        entry (.CreateEntry archive path)]
    (set! (.LastWriteTime entry) (DateTimeOffset. (if dir (Directory/GetLastWriteTime file) (File/GetLastWriteTime file))))
    (when-not dir
      (with-open [in (File/OpenRead file)
                  out (.Open entry)]
        (.CopyTo in out)))))

(defn copy-to-zip
  "Add the files under root to the archive, with paths relative to root."
  [^ZipArchive archive root]
  (let [root (Path/GetFullPath (file/path root))
        files (file/collect-files root :dirs true)]
    (run! (fn [^String f]
            (let [rel-path (Path/GetRelativePath root (Path/GetFullPath f))]
              (when-not (= rel-path ".")
                (add-zip-entry archive rel-path f))))
      files)))

(defn open-zip
  "A ZipArchive creating the file at zip-path (overwriting it)."
  ^ZipArchive [zip-path]
  (ZipArchive. (FileStream. (file/path zip-path) FileMode/Create) ZipArchiveMode/Create false))

(defn list-zip
  [zip-path]
  (let [zip-path (file/path zip-path)]
    (when (File/Exists zip-path)
      (with-open [zip (ZipArchive. (File/OpenRead zip-path) ZipArchiveMode/Read false)]
        (sort-by :name
          (mapv (fn [^ZipArchiveEntry entry]
                  {:name (.FullName entry)
                   :modified (.LastWriteTime entry)
                   :size (.Length entry)})
                (.Entries zip)))))))

(defn unzip
  [zip-path target-dir]
  (let [zip-path (file/path zip-path)
        target (Path/GetFullPath (file/path target-dir))]
    (if (File/Exists zip-path)
      (with-open [zip (ZipArchive. (File/OpenRead zip-path) ZipArchiveMode/Read false)]
        (doseq [^ZipArchiveEntry entry (.Entries zip)]
          (let [out-file (Path/GetFullPath (Path/Combine target (.FullName entry)))]
            (when-not (str/starts-with? out-file target)
              (throw (ex-info (str "Zip entry outside the target dir: " (.FullName entry)) {})))
            (if (str/ends-with? (.FullName entry) "/")
              (Directory/CreateDirectory out-file)
              (do
                (Directory/CreateDirectory (Path/GetDirectoryName out-file))
                (with-open [in (.Open entry)
                            out (FileStream. out-file FileMode/Create)]
                  (.CopyTo in out))
                (File/SetLastWriteTime out-file (.DateTime (.LastWriteTime entry)))))))
        true)
      false)))
