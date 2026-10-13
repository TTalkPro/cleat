;; cleat (designs/18 §6): install puts the nupkg in the local NuGet feed
;; (upstream: the local Maven repo).

(ns clojure.tools.build.tasks.install
  (:require
    [clojure.string :as str]
    [clojure.tools.build.api :as api]
    [clojure.tools.build.util.file :as file]
    [clojure.tools.build.tasks.create-basis :as basis])
  (:import
    [System.IO Directory File Path]
    [System.IO.Compression ZipArchive ZipArchiveMode]))

(defn nupkg-identity
  "[id version] read from the nupkg's nuspec."
  [^String nupkg]
  (with-open [zip (ZipArchive. (File/OpenRead nupkg) ZipArchiveMode/Read false)]
    (let [entry (or (first (filter #(and (str/ends-with? (.FullName %) ".nuspec") (not (str/includes? (.FullName %) "/")))
                                   (.Entries zip)))
                    (throw (ex-info (str "Not a nupkg (no .nuspec): " nupkg) {})))
          xml (with-open [r (System.IO.StreamReader. (.Open entry))] (System.Xml.Linq.XDocument/Load r))
          meta (first (filter #(= "metadata" (.LocalName (.Name %))) (.Elements (.Root xml))))
          value (fn [n] (some #(when (= n (.LocalName (.Name %))) (.Value %)) (.Elements meta)))]
      [(value "id") (value "version")])))

(defn- global-packages-dir ^String []
  (or (some-> (System.Environment/GetEnvironmentVariable "NUGET_PACKAGES") str/trim not-empty)
      (Path/Combine (System.Environment/GetFolderPath System.Environment+SpecialFolder/UserProfile) ".nuget" "packages")))

(defn install
  [{:keys [nupkg-file local-feed] :as params}]
  (let [nupkg (Path/GetFullPath (api/resolve-path nupkg-file))
        feed (file/ensure-dir (or (some-> local-feed api/resolve-path) (basis/default-local-feed)))
        [id version] (nupkg-identity nupkg)
        lower-id (str/lower-case id)
        lower-version (str/lower-case version)]
    (file/copy-file nupkg (Path/Combine feed (str lower-id "." lower-version ".nupkg")))
    ;; reinstalling a version: restore would keep using the cached copy
    (let [cached (Path/Combine (global-packages-dir) lower-id lower-version)]
      (when (Directory/Exists cached) (file/delete cached)))
    nil))
