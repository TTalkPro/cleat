;; Builds tools.build with itself: one package assembly Cleat.ToolsBuild.dll,
;; packed as the NuGet package Cleat.ToolsBuild (depends on Cleat.ToolsNamespace).
;;
;;   packages/tools.build/bin/cleat-build pack | install [:local-feed '"dir"'] | push [:source '"url"']
(ns build
  (:require [clojure.tools.build.api :as b]))

(def lib 'Cleat.ToolsBuild)
(def default-version "0.10.14")
(def class-dir "target/classes")

(defn- nupkg [version] (format "target/%s.%s.nupkg" lib version))

(defn clean [_]
  (b/delete {:path "target"}))

(defn pack
  [{:keys [version local-feed] :or {version default-version} :as params}]
  (clean nil)
  (let [basis (b/create-basis (cond-> {} local-feed (assoc :local-feed local-feed)))]
    (b/compile-clj {:basis basis :class-dir class-dir :src-dirs ["src"] :lib lib :package "tools.build"})
    (b/pack {:basis basis :lib lib :version version :class-dir class-dir :nupkg-file (nupkg version)
             :description "tools.build ported to cleat (Clojure on .NET): build scripts, NuGet deps (BaGetter), package assemblies, dotnet build, pack, push"
             :authors ["Rich Hickey" "Alex Miller" "cleat"] :license "EPL-1.0" :tags ["clojure" "cleat" "build"]
             :project-url "https://github.com/clojure/tools.build"})
    (assoc params :nupkg-file (nupkg version))))

(defn install [params]
  (let [{:keys [nupkg-file]} (pack params)]
    (b/install (merge {:nupkg-file nupkg-file} (select-keys params [:local-feed])))))

(defn push [params]
  (let [{:keys [nupkg-file]} (pack params)]
    (b/push (merge {:nupkg-file nupkg-file} (select-keys params [:source :api-key :skip-duplicate :configfile])))))
