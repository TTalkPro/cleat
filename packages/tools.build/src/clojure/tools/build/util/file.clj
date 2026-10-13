;   Copyright (c) Rich Hickey. All rights reserved.
;   The use and distribution terms for this software are covered by the
;   Eclipse Public License 1.0 (http://opensource.org/licenses/eclipse-1.0.php)
;   which can be found in the file epl-v10.html at the root of this distribution.
;   By using this software in any fashion, you are agreeing to be bound by
;   the terms of this license.
;   You must not remove this notice, or any other, from this software.

;; cleat port: paths are strings (System.IO), not java.io.File.

(ns clojure.tools.build.util.file
  (:require
    [clojure.string :as str])
  (:import
    [System.IO Directory File FileAttributes FileInfo Path]
    [clojure.lang PersistentQueue]))

(defn path
  "The string path of f: a string, a FileSystemInfo, or anything with a
  string representation."
  ^String [f]
  (if (instance? System.IO.FileSystemInfo f)
    (.FullName ^System.IO.FileSystemInfo f)
    (str f)))

(defn exists?
  "Whether a file or directory exists at path p."
  [p]
  (let [p (path p)]
    (or (File/Exists p) (Directory/Exists p))))

(defn directory? [p] (Directory/Exists (path p)))

(defn- link? [^String p]
  (try
    (.HasFlag (File/GetAttributes p) FileAttributes/ReparsePoint)
    (catch Exception _ false)))

(defn collect-files
  "Recursively collect all paths under path, starting from root.
   Options:
    :dirs - whether to collect directories (default false)
    :collect - function for whether to collect a path (default yes)"
  [root & {:keys [dirs collect]
           :or {dirs false
                collect (constantly true)}}]
  (let [root (path root)]
    (when (exists? root)
      (loop [queue (conj PersistentQueue/EMPTY root)
             collected []]
        (let [file (peek queue)]
          (if file
            ;; symbolic links to directories are not followed (NOFOLLOW_LINKS upstream)
            (let [is-dir (and (Directory/Exists file) (not (link? file)))
                  children (when is-dir (vec (sort (Directory/GetFileSystemEntries file))))
                  collect? (and (if is-dir dirs true) (collect file))]
              (recur (into (pop queue) children) (if collect? (conj collected file) collected)))
            (when (seq collected) collected)))))))

(defn suffixes
  "Returns a predicate matching suffixes"
  [& suffixes]
  (apply some-fn
    (map #(fn [f] (str/ends-with? (path f) ^String %)) suffixes)))

(defn delete
  "Recursively delete file or directory at path"
  [file]
  (run! (fn [^String p]
          (cond
            (and (Directory/Exists p) (not (link? p))) (Directory/Delete p false)
            (File/Exists p) (File/Delete p)
            (Directory/Exists p) (Directory/Delete p false)))
        (reverse (collect-files file :dirs true))))

(defn copy-file
  "Copy file from src to target, retaining file attributes (the last write
  time). Returns nil."
  [src-file target-file]
  (let [src (path src-file)
        target (path target-file)]
    (when-let [parent (Path/GetDirectoryName (Path/GetFullPath target))]
      (Directory/CreateDirectory parent))
    (File/Copy src target true)
    (File/SetLastWriteTimeUtc target (File/GetLastWriteTimeUtc src))
    nil))

(defn- relativize ^String [^String root ^String p]
  (Path/GetRelativePath root p))

(defn copy-contents
  "Copy files in src dir to target dir, optionally filtering by prefix paths"
  ([src-dir target-dir]
   (let [source (Path/GetFullPath (path src-dir))
         target (path target-dir)]
     (run!
       (fn [^String f]
         (copy-file f (Path/Combine target (relativize source (Path/GetFullPath f)))))
       (collect-files source))))
  ([src-dir target-dir prefixes]
   (let [root (Path/GetFullPath (path src-dir))
         target (path target-dir)
         sep #(str/replace % "\\" "/")]
     (when (exists? root)
       (loop [queue (conj PersistentQueue/EMPTY root)]
         (let [file (peek queue)]
           (when file
             (let [relative (sep (relativize root file))
                   relative (if (= relative ".") "" relative)]
               (cond
                 ;; match, copy this file/dir
                 (and (not= relative "") (some #(str/starts-with? relative %) prefixes))
                 (let [end-path (Path/Combine target relative)]
                   (if (Directory/Exists file)
                     (copy-contents file end-path)
                     (copy-file file end-path))
                   (recur (pop queue)))

                 ;; no match, but continue looking in this directory if it could match later
                 (and (Directory/Exists file)
                      (some #(str/starts-with? % relative) prefixes))
                 (recur (into (pop queue) (sort (Directory/GetFileSystemEntries file))))

                 ;; work the queue
                 :else
                 (recur (pop queue)))))))))))

(defn ensure-dir
  "Ensure dir exists by making all parent directories and return it (path)"
  ^String [dir]
  (let [d (path dir)]
    (try
      (Directory/CreateDirectory d)
      d
      (catch Exception e
        (throw (ex-info (str "Can't create directory " dir) {} e))))))

(defn ensure-file
  "Write contents (default empty) to file, creating parent dirs. opts are
  spit's (:append, :encoding)."
  ([file] (ensure-file file ""))
  ([file contents & opts]
   (let [file (path file)
         parent (Path/GetDirectoryName (Path/GetFullPath file))]
     (when parent (ensure-dir parent))
     (apply spit file contents opts))))
