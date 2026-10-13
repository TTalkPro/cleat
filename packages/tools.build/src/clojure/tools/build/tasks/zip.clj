;   Copyright (c) Rich Hickey. All rights reserved.
;   The use and distribution terms for this software are covered by the
;   Eclipse Public License 1.0 (http://opensource.org/licenses/eclipse-1.0.php)
;   which can be found in the file epl-v10.html at the root of this distribution.
;   By using this software in any fashion, you are agreeing to be bound by
;   the terms of this license.
;   You must not remove this notice, or any other, from this software.

(ns clojure.tools.build.tasks.zip
  (:require
    [clojure.tools.build.api :as api]
    [clojure.tools.build.util.file :as file]
    [clojure.tools.build.util.zip :as zip])
  (:import
    [System.IO Path]))

(defn zip
  [{:keys [src-dirs zip-file] :as params}]
  (let [zip-file (api/resolve-path zip-file)]
    (file/ensure-dir (Path/GetDirectoryName (Path/GetFullPath zip-file)))
    (with-open [archive (zip/open-zip zip-file)]
      (doseq [zpath src-dirs]
        (let [zip-from (file/ensure-dir (api/resolve-path zpath))]
          (zip/copy-to-zip archive zip-from))))))

(defn unzip
  [{:keys [zip-file target-dir] :as params}]
  (let [{:keys [zip-file target-dir]} params
        ret (zip/unzip (api/resolve-path zip-file) (api/resolve-path target-dir))]
    (when-not ret
      (throw (ex-info (format "Zip file does not exist: %s" zip-file) {})))))
