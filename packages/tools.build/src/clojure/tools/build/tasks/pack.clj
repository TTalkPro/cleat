;; cleat (designs/18 §4): write-pom + jar upstream -> a nupkg, written
;; directly (an OPC zip: <id>.nuspec, [Content_Types].xml, _rels/.rels and the
;; files), so packing needs neither msbuild nor a restore.

(ns clojure.tools.build.tasks.pack
  (:require
    [clojure.string :as str]
    [clojure.tools.build.api :as api]
    [clojure.tools.build.util.file :as file]
    [clojure.tools.build.util.zip :as zip])
  (:import
    [System.IO File Path]
    [System.IO.Compression ZipArchive ZipArchiveMode]))

(defn- xml-escape [s]
  (-> (str s) (str/replace "&" "&amp;") (str/replace "<" "&lt;") (str/replace ">" "&gt;") (str/replace "\"" "&quot;")))

(defn package-id
  "NuGet id for the params: :package-id, else :lib (my.org/my-lib -> my.org.my-lib)."
  [{:keys [package-id lib]}]
  (or package-id (if (namespace lib) (str (namespace lib) "." (name lib)) (name lib))))

(defn- dependencies
  "[[id version-range]...]: Cleat.Runtime (unless this is it) and the project's
  top-level NuGet deps."
  [basis runtime-version id]
  (let [deps (:deps basis)
        local (filter (fn [[_ coord]] (contains? coord :local/root)) deps)]
    (when (seq local)
      (throw (ex-info (str "pack: :local/root deps can't be NuGet dependencies: " (str/join ", " (map key local))
                           " (publish them as packages and depend on :nuget/version)")
                      {:deps (vec (map key local))})))
    (into (if (= "cleat.runtime" (str/lower-case id)) [] [["Cleat.Runtime" runtime-version]])
          (for [[lib coord] (sort-by (comp str key) deps)
                :when (and (contains? coord :nuget/version)
                           (not= "cleat.runtime" (str/lower-case (str lib))))]
            [(str lib) (str (:nuget/version coord))]))))

(defn nuspec
  "The nuspec XML for the params."
  [{:keys [basis version description authors license project-url repository-url tags readme
           runtime-version target-framework]
    :or {target-framework "net10.0"} :as params}]
  (let [id (package-id params)
        authors (if (sequential? authors) (str/join ", " authors) (or authors id))
        runtime-version (or runtime-version Cleat.Core.Runtime.CoreInfo/Version)
        el (fn [tag v] (when v (str "    <" tag ">" (xml-escape v) "</" tag ">\n")))]
    (str "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"
         "<package xmlns=\"http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd\">\n"
         "  <metadata>\n"
         (el "id" id)
         (el "version" version)
         (el "authors" authors)
         (el "description" (or description id))
         (when license (str "    <license type=\"expression\">" (xml-escape license) "</license>\n"))
         (el "projectUrl" project-url)
         (when repository-url (str "    <repository type=\"git\" url=\"" (xml-escape repository-url) "\" />\n"))
         (el "tags" (when (seq tags) (str/join " " tags)))
         (when readme "    <readme>README.md</readme>\n")
         "    <dependencies>\n"
         "      <group targetFramework=\"" (xml-escape target-framework) "\">\n"
         (apply str (for [[dep range] (dependencies basis runtime-version id)]
                      (str "        <dependency id=\"" (xml-escape dep) "\" version=\"" (xml-escape range) "\" exclude=\"Build,Analyzers\" />\n")))
         "      </group>\n"
         "    </dependencies>\n"
         "  </metadata>\n"
         "</package>\n")))

(defn- content-types [part-names]
  (let [exts (->> part-names
                  (keep #(let [e (Path/GetExtension ^String %)] (when (seq e) (str/lower-case (subs e 1)))))
                  (concat ["rels" "nuspec"])
                  distinct sort)
        no-ext (remove #(seq (Path/GetExtension ^String %)) part-names)]
    (str "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"
         "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">"
         (apply str (for [e exts]
                      (str "<Default Extension=\"" (xml-escape e) "\" ContentType=\""
                           (if (= e "rels") "application/vnd.openxmlformats-package.relationships+xml" "application/octet")
                           "\" />")))
         (apply str (for [p no-ext] (str "<Override PartName=\"/" (xml-escape p) "\" ContentType=\"application/octet\" />")))
         "</Types>")))

(defn- rels [nuspec-name]
  (str "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"
       "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
       "<Relationship Type=\"http://schemas.microsoft.com/packaging/2010/07/manifest\" Target=\"/" (xml-escape nuspec-name)
       "\" Id=\"R" (subs (Convert/ToHexString (System.Security.Cryptography.SHA256/HashData (.GetBytes System.Text.Encoding/UTF8 ^String nuspec-name))) 0 16)
       "\" /></Relationships>"))

(defn- files-under
  "[[part-name source-path]...] for the files under dir, parts prefixed."
  [dir prefix]
  (let [dir (Path/GetFullPath dir)]
    (for [f (file/collect-files dir)]
      [(str prefix (str/replace (Path/GetRelativePath dir (Path/GetFullPath f)) "\\" "/")) f])))

(defn- write-text [^ZipArchive zip ^String part ^String text]
  (with-open [w (System.IO.StreamWriter. (.Open (.CreateEntry zip part)) (System.Text.UTF8Encoding. false))]
    (.Write w text)))

(defn pack
  [{:keys [class-dir nupkg-file src-dirs readme target-framework] :or {target-framework "net10.0"} :as params}]
  (let [id (package-id params)
        nupkg (Path/GetFullPath (api/resolve-path nupkg-file))
        files (concat
                (files-under (api/resolve-path class-dir) (str "lib/" target-framework "/"))
                (mapcat #(files-under (api/resolve-path %) "src/") src-dirs)
                (when readme [["README.md" (api/resolve-path readme)]]))
        nuspec-name (str id ".nuspec")
        spec (nuspec params)]
    (when (empty? (filter #(str/starts-with? (first %) "lib/") files))
      (throw (ex-info (str "pack: no files in :class-dir " class-dir " (run compile-clj first)") {})))
    (file/ensure-dir (Path/GetDirectoryName nupkg))
    (with-open [z (zip/open-zip nupkg)]
      (write-text z "_rels/.rels" (rels nuspec-name))
      (write-text z nuspec-name spec)
      (doseq [[part src] files]
        (with-open [in (File/OpenRead src)
                    out (.Open (.CreateEntry z part))]
          (.CopyTo in out)))
      (write-text z "[Content_Types].xml" (content-types (cons nuspec-name (map first files)))))
    nil))
