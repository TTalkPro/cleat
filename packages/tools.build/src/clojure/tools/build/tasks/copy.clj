;   Copyright (c) Rich Hickey. All rights reserved.
;   The use and distribution terms for this software are covered by the
;   Eclipse Public License 1.0 (http://opensource.org/licenses/eclipse-1.0.php)
;   which can be found in the file epl-v10.html at the root of this distribution.
;   By using this software in any fashion, you are agreeing to be bound by
;   the terms of this license.
;   You must not remove this notice, or any other, from this software.

(ns clojure.tools.build.tasks.copy
  (:require
    [clojure.string :as str]
    [clojure.tools.build.api :as api]
    [clojure.tools.build.util.file :as file]
    [clojure.tools.build.util.glob :as glob])
  (:import
    [System.IO Directory File Path SearchOption]))

(defn- match-paths
  "Match glob to the files under root and return a collection of paths"
  [root glob]
  (let [root (Path/GetFullPath root)
        matches? (glob/matcher glob)]
    (if (Directory/Exists root)
      (->> (Directory/EnumerateFiles root "*" SearchOption/AllDirectories)
           (filter #(matches? (Path/GetRelativePath root %)))
           sort)
      [])))

(def default-ignores
  [".*~$"
   "^#.*#$"
   "^\\.#.*"
   "^.DS_Store$"])

(defn ignore? [name ignore-regexes]
  (boolean (some #(re-matches % name) ignore-regexes)))

(def default-non-replaced-exts
  ["jpg" "jpeg" "png" "gif" "bmp"])

(defn- ends-with-ext?
  [exts path]
  (loop [[ext & es] exts]
    (if ext
      (if (str/ends-with? path ext)
        true
        (recur es))
      false)))

(defn copy
  [{:keys [target-dir src-dirs include replace ignores non-replaced-exts]
    :or {include "**", ignores default-ignores, non-replaced-exts default-non-replaced-exts}
    :as _params}]
  (let [to-path (Path/GetFullPath (file/ensure-dir (api/resolve-path target-dir)))
        ignore-regexes (map re-pattern ignores)
        non-replaced (map #(str "." %) non-replaced-exts)]
    (doseq [dir src-dirs]
      (let [from-dir (Path/GetFullPath (api/resolve-path dir))
            paths (match-paths from-dir include)]
        (doseq [^String path paths]
          (let [path-file-name (Path/GetFileName path)
                target-file (Path/Combine to-path (Path/GetRelativePath from-dir path))]
            (when-not (ignore? path-file-name ignore-regexes)
              (if (or (empty? replace) (ends-with-ext? non-replaced path-file-name))
                (file/copy-file path target-file)
                (let [contents (slurp path)
                      replaced (reduce (fn [s [find replace]] (str/replace s find replace))
                                 contents replace)]
                  (file/ensure-file target-file replaced :append false)
                  ;; keep the permissions (upstream: posix permissions)
                  (when-not (System.OperatingSystem/IsWindows)
                    (File/SetUnixFileMode target-file (File/GetUnixFileMode path))))))))))))
