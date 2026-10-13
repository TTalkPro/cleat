;; Builds the nrepl package with tools.build (packages/tools.build):
;; one package assembly Nrepl.dll, packed as the NuGet package Nrepl.
;;
;;   packages/tools.build/bin/cleat-build pack [:version '"1.8.0"']
;;   packages/tools.build/bin/cleat-build install [:local-feed '"dir"']
;;   packages/tools.build/bin/cleat-build push [:source '"https://host/v3/index.json"'] [:api-key '"..."']
(ns build
  (:require [clojure.tools.build.api :as b]))

(def lib 'Nrepl)
(def default-version "1.8.0")
(def class-dir "target/classes")

(defn- nupkg [version] (format "target/%s.%s.nupkg" lib version))

(defn clean [_]
  (b/delete {:path "target"}))

(defn pack
  "Compile the package assembly and pack the nupkg; returns the params with :nupkg-file."
  [{:keys [version] :or {version default-version} :as params}]
  (clean nil)
  (let [basis (b/create-basis)]
    (b/compile-clj {:basis basis :class-dir class-dir :src-dirs ["src"] :lib lib :package "nrepl"})
    (b/pack {:basis basis :lib lib :version version :class-dir class-dir :nupkg-file (nupkg version)
             :description "nREPL 1.8.0 ported to cleat (Clojure on .NET): network REPL server, client and transports"
             :authors ["nREPL contributors" "cleat"] :license "EPL-1.0" :tags ["clojure" "cleat" "nrepl" "repl"]
             :project-url "https://github.com/nrepl/nrepl"})
    (assoc params :nupkg-file (nupkg version))))

(defn install [params]
  (let [{:keys [nupkg-file]} (pack params)]
    (b/install (merge {:nupkg-file nupkg-file} (select-keys params [:local-feed])))))

(defn push [params]
  (let [{:keys [nupkg-file]} (pack params)]
    (b/push (merge {:nupkg-file nupkg-file} (select-keys params [:source :api-key :skip-duplicate])))))
