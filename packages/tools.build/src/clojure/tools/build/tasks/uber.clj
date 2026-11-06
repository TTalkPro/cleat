;   Copyright (c) Rich Hickey. All rights reserved.
;   The use and distribution terms for this software are covered by the
;   Eclipse Public License 1.0 (http://opensource.org/licenses/eclipse-1.0.php)
;   which can be found in the file epl-v10.html at the root of this distribution.
;   By using this software in any fashion, you are agreeing to be bound by
;   the terms of this license.
;   You must not remove this notice, or any other, from this software.

;; cleat port (designs/18 §4): an uberjar upstream -> a deployable directory.
;; The cleat host and runtime go to its root; the project's class-dir and the
;; dependency package dirs are merged ("exploded") into lib/ with upstream's
;; exclusions and conflict handlers; :main writes launchers NAME / NAME.cmd
;; (:launcher, by default the uber dir's name).

(ns clojure.tools.build.tasks.uber
  (:require
    [clojure.pprint :as pprint]
    [clojure.set :as set]
    [clojure.string :as str]
    [clojure.tools.build.api :as api]
    [clojure.tools.build.util.file :as file])
  (:import
    [System.IO Directory File FileMode FileStream MemoryStream Path Stream]))

(def ^:private uber-exclusions
  [#"(.*/)?\.DS_Store" ;; Mac metadata
   #".+~" ;; emacs backup files
   #".#.*" ;; emacs
   #"(.*/)?\.keep" ;; convention in dirs to keep that are empty
   #".*\.nuspec"
   #".*\.nupkg"
   #".*\.nupkg\.metadata"
   #".*\.sha512"])

(defn- exclude-from-uber?
  [exclude-patterns ^String path]
  (loop [[re & res] exclude-patterns]
    (if re
      (if (re-matches re path)
        true
        (recur res))
      false)))

(defn stream->string
  [^Stream is]
  (with-open [r (System.IO.StreamReader. is System.Text.Encoding/UTF8 true 4096 true)]
    (.ReadToEnd r)))

(defn conflict-overwrite
  [{:keys [path in]}]
  {:write {path {:stream in}}})

(defn- conflict-append
  [{:keys [path in]}]
  {:write {path {:string (str "\n" (stream->string in)), :append true}}})

(defn- conflict-append-dedupe
  [{:keys [path in existing state] :as _params}]
  (let [existing-content (slurp existing)
        existing-lower (str/lower-case existing-content)
        new-content (stream->string in)
        new-content-lower (str/lower-case new-content)
        seen (or (get-in state [:append-dedupe path]) #{existing-lower})]
    (if (contains? seen new-content-lower)
      ;; already seen
      {:state (assoc-in state [:append-dedupe path] seen)}
      ;; record and append
      {:state (assoc-in state [:append-dedupe path] (conj seen new-content-lower))
       :write {path {:string (str "\n" new-content), :append true}}})))

(defn conflict-data-readers
  [{:keys [path in existing]}]
  (binding [*read-eval* false]
    (let [existing-str (slurp existing)
          existing-reader-fns (read-string
                               {:read-cond :preserve :features #{:cljr}}
                               existing-str)
          append-reader-fns (read-string
                             {:read-cond :preserve :features #{:cljr}}
                             (stream->string in))
          reader-str (with-out-str (pprint/pprint (merge existing-reader-fns append-reader-fns)))]
      {:write {path {:string reader-str}}})))

(defn- conflict-warn
  [{:keys [path lib]}]
  (println "Conflicting path at" path "from" lib))

(defn- conflict-error
  [{:keys [path lib]}]
  (throw (ex-info (str "Conflicting path at " path " from " lib) {})))

(defn- handler-emit
  [last-modified out-dir path write-spec]
  (let [{:keys [string stream append] :or {append false}} write-spec
        out-file (Path/Combine out-dir path)]
    (if string
      (spit out-file string :append (boolean append))
      (with-open [out (FileStream. out-file (if append FileMode/Append FileMode/Create))]
        (.CopyTo ^Stream stream out)))
    (File/SetLastWriteTimeUtc out-file last-modified)))

(defn- handle-conflict
  [handlers last-modified out-dir {:keys [state path] :as handler-params}]
  (let [use-handler (loop [[[re handler] & hs] (dissoc handlers :default)]
                      (if re
                        (if (re-matches re path)
                          handler
                          (recur hs))
                        (:default handlers)))]
    (if use-handler
      (let [{new-state :state, write :write} (use-handler handler-params)]
        (when write
          (doseq [[path write-spec] write]
            (handler-emit last-modified out-dir path write-spec)))
        (or new-state state))
      (throw (ex-info (format "No handler found for conflict at %s" path) {})))))

(defn- explode1
  "Given one src file, copy to target pursuant to excludes and handlers.
   Returns possibly updated state for further exploding."
  [^String src ^String path last-modified ^String out-file lib {:keys [out-dir exclude handlers]} state]
  (cond
    ;; excluded - do nothing
    (exclude-from-uber? exclude path)
    state

    ;; conflict, same file from multiple sources - handle
    (File/Exists out-file)
    (with-open [is (File/OpenRead src)]
      (handle-conflict handlers last-modified out-dir
                       {:lib lib, :path path, :in is, :existing out-file, :state state}))

    ;; parent dir is a file from another lib
    (File/Exists (Path/GetDirectoryName out-file))
    (throw (ex-info (format "Cannot write %s from %s as parent dir is a file from another lib. One of them must be excluded."
                            path lib) {}))

    :else
    (do
      (Directory/CreateDirectory (Path/GetDirectoryName out-file))
      (File/Copy src out-file)
      (File/SetLastWriteTimeUtc out-file last-modified)
      state)))

(defn- explode
  [lib-dir lib {:keys [out-dir] :as context} state]
  (let [lib-dir (Path/GetFullPath (file/path lib-dir))]
    (cond
      (Directory/Exists lib-dir)
      (reduce
        (fn [the-state ^String f]
          (let [path (str/replace (Path/GetRelativePath lib-dir (Path/GetFullPath f)) "\\" "/")]
            (explode1 f path (File/GetLastWriteTimeUtc f) (Path/Combine out-dir path) lib context the-state)))
        state
        (file/collect-files lib-dir))

      (File/Exists lib-dir)
      (let [path (Path/GetFileName lib-dir)]
        (explode1 lib-dir path (File/GetLastWriteTimeUtc lib-dir) (Path/Combine out-dir path) lib context state))

      :else state)))

(defn- built-ins
  [kw]
  (or
    (get {:ignore (fn [_])
          :overwrite conflict-overwrite
          :append conflict-append
          :append-dedupe conflict-append-dedupe
          :data-readers conflict-data-readers
          :warn conflict-warn
          :error conflict-error}
      kw)
    (throw (ex-info (str "Invalid handler: " kw) {}))))

(defn- prep-handler
  "Convert user handler to fn"
  [handler]
  (cond
    (keyword? handler) (built-ins handler)
    (symbol? handler) (deref (requiring-resolve handler))
    (ifn? handler) handler
    :else (throw (ex-info (str "Invalid handler: " handler) {}))))

(def ^:private default-handlers
  {"^data_readers.clj[cr]?$" :data-readers
   "(?i)^(META-INF/)?(COPYRIGHT|NOTICE|LICENSE)(\\.(txt|md))?$" :append-dedupe
   :default :ignore})

(defn- prep-handlers
  "Transform user handler map into a map of re->fn"
  [handlers]
  (reduce-kv
    (fn [m pattern handler]
      (assoc m (if (= pattern :default) :default (re-pattern pattern))
               (prep-handler handler)))
    {} (merge default-handlers handlers)))

(defn- runtime-files
  "[[source target-relative-path]...]: the cleat host (its directory, with the
  compiler Cleat.Compiler) and the runtime it runs (the image, the bundled libs)."
  []
  (let [host-dir (Path/GetFullPath System.AppContext/BaseDirectory)
        clojure-boot Cleat.Host.Program/ClojureBootPath
        beside (fn [p exts] (keep #(let [f (Path/ChangeExtension p %)] (when (File/Exists f) [f (Path/GetFileName f)])) exts))]
    (when-not clojure-boot
      (throw (ex-info "uber: this cleat has no Clojure boot image to copy" {})))
    (concat
      (for [f (Directory/GetFiles host-dir)
            :when (not (str/ends-with? f ".pdb"))]
        [f (Path/GetFileName f)])
      (beside clojure-boot [".dll" ".ifc" ".inl"])
      (keep (fn [n] (let [f (Path/Combine (Path/GetDirectoryName clojure-boot) n)] (when (File/Exists f) [f n])))
            ["Cleat.Clojure.Libs.dll"]))))

(defn- launcher-name
  "The launchers' name: :launcher, or the uber dir's own name (target/app -> app).
  It must not take the place of a file of the runtime at the root, or of lib/."
  [^String uber-dir launcher runtime-names]
  (let [n (or launcher (Path/GetFileName uber-dir))
        taken (into #{"lib"} (map str/lower-case) runtime-names)]
    (when (or (str/blank? n) (re-find #"[/\\]" n))
      (throw (ex-info (str "uber: invalid launcher name " (pr-str n)) {:launcher n})))
    (when (some taken [(str/lower-case n) (str/lower-case (str n ".cmd"))])
      (throw (ex-info (str "uber: the launcher " n " would replace a file of the runtime in " uber-dir
                           "; give another name with :launcher")
                      {:launcher n})))
    n))

(defn- write-launchers!
  [^String uber-dir ^String name main]
  (let [sh (Path/Combine uber-dir name)
        cmd (Path/Combine uber-dir (str name ".cmd"))]
    (spit sh (str "#!/bin/sh\n"
                  "# Runs " main " (generated by clojure.tools.build uber)\n"
                  "here=$(cd \"$(dirname \"$0\")\" && pwd)\n"
                  "exec dotnet \"$here/cleat.dll\" -I \"$here/lib\" -m " main " \"$@\"\n"))
    (spit cmd (str "@echo off\r\n"
                   "rem Runs " main " (generated by clojure.tools.build uber)\r\n"
                   "dotnet \"%~dp0cleat.dll\" -I \"%~dp0lib\" -m " main " %*\r\n"))
    (when-not (System.OperatingSystem/IsWindows)
      (File/SetUnixFileMode sh (enum-or System.IO.UnixFileMode/UserRead System.IO.UnixFileMode/UserWrite System.IO.UnixFileMode/UserExecute
                                        System.IO.UnixFileMode/GroupRead System.IO.UnixFileMode/GroupExecute
                                        System.IO.UnixFileMode/OtherRead System.IO.UnixFileMode/OtherExecute)))))

(defn uber
  [{:keys [basis class-dir uber-dir main launcher exclude conflict-handlers]}]
  (let [uber-dir (Path/GetFullPath (api/resolve-path (str/replace uber-dir #"[/\\]+$" "")))
        runtime (runtime-files)
        launcher (when main (launcher-name uber-dir launcher (map #(first (str/split (second %) #"/")) runtime)))
        lib-dir (Path/Combine uber-dir "lib")
        context {:out-dir lib-dir
                 :handlers (prep-handlers conflict-handlers)
                 :exclude (map re-pattern (into (map str uber-exclusions) exclude))}]
    (when (file/exists? uber-dir) (file/delete uber-dir))
    (file/ensure-dir lib-dir)
    (doseq [[src rel] runtime]
      (file/copy-file src (Path/Combine uber-dir rel)))
    (reduce
      (fn [state [lib coord]]
        (reduce
          (fn [state path] (explode path lib context state))
          state (:paths coord)))
      nil ;; initial state, usable by handlers if needed
      (cons [nil {:paths [(api/resolve-path class-dir)]}] (:libs basis)))
    (when main (write-launchers! uber-dir launcher main))
    nil))
