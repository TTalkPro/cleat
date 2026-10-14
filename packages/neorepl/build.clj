;; Builds the neorepl package with tools.build (packages/tools.build): one
;; package assembly Cleat.Neorepl.dll, packed as the NuGet package Cleat.Neorepl,
;; which depends on Cleat.Nrepl (resolved from the local feed or nuget.config's sources).
;;
;;   cleat -T:build pack [:version '"0.1.0"']
;;   cleat -T:build install [:local-feed '"dir"']
;;   cleat -T:build push [:source '"https://host/v3/index.json"'] [:api-key '"..."']
(ns build
  (:require [clojure.tools.build.api :as b]))

(def lib 'Cleat.Neorepl)
(def default-version "0.1.4")   ; neorepl 0.1.0's port, package revision 4 (cleat-094: rebuilt for the self-hosted runtime 1.1.0)
(def class-dir "target/classes")

(defn- nupkg [version] (format "target/%s.%s.nupkg" lib version))

(defn clean [_]
  (b/delete {:path "target"}))

(defn pack
  "Compile the package assembly and pack the nupkg; returns the params with :nupkg-file."
  [{:keys [version local-feed] :or {version default-version} :as params}]
  (clean nil)
  (let [basis (b/create-basis (cond-> {} local-feed (assoc :local-feed local-feed)))]
    (b/compile-clj {:basis basis :class-dir class-dir :src-dirs ["src"] :lib lib :package "neorepl"})
    (b/pack {:basis basis :lib lib :version version :class-dir class-dir :nupkg-file (nupkg version)
             :description "neorepl 0.1.0 ported to cleat (Clojure on .NET): a terminal REPL and CLI for nREPL servers"
             :authors ["neorepl contributors" "cleat"] :license "EPL-1.0" :tags ["clojure" "cleat" "nrepl" "repl"]
             :project-url "https://github.com/nrepl/neorepl"})
    (assoc params :nupkg-file (nupkg version))))

(defn install [params]
  (let [{:keys [nupkg-file]} (pack params)]
    (b/install (merge {:nupkg-file nupkg-file} (select-keys params [:local-feed])))))

(defn push [params]
  (let [{:keys [nupkg-file]} (pack params)]
    (b/push (merge {:nupkg-file nupkg-file} (select-keys params [:source :api-key :skip-duplicate :configfile])))))
