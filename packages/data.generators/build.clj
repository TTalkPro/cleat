;; Builds clojure.data.generators with tools.build: one package assembly
;; Cleat.DataGenerators.dll, packed as the NuGet package Cleat.DataGenerators.
;;
;;   cleat -T:build pack | install [:local-feed '"dir"'] | push [:source '"url"']
(ns build
  (:require [clojure.tools.build.api :as b]))

(def lib 'Cleat.DataGenerators)
(def default-version "1.1.0")   ; cleat-191
(def class-dir "target/classes")

(defn- nupkg [version] (format "target/%s.%s.nupkg" lib version))

(defn clean [_]
  (b/delete {:path "target"}))

(defn pack
  [{:keys [version] :or {version default-version} :as params}]
  (clean nil)
  (let [basis (b/create-basis)]
    (b/compile-clj {:basis basis :class-dir class-dir :src-dirs ["src"] :lib lib :package "data.generators"})
    (b/pack {:basis basis :lib lib :version version :class-dir class-dir :nupkg-file (nupkg version)
             :description "clojure.data.generators (from ClojureCLR's clr.data.generators) for cleat (Clojure on .NET)"
             :authors ["Stuart Halloway" "David Miller" "cleat"] :license "EPL-1.0" :tags ["clojure" "cleat"]
             :project-url "https://github.com/clojure/clr.data.generators"})
    (assoc params :nupkg-file (nupkg version))))

(defn install [params]
  (let [{:keys [nupkg-file]} (pack params)]
    (b/install (merge {:nupkg-file nupkg-file} (select-keys params [:local-feed])))))

(defn push [params]
  (let [{:keys [nupkg-file]} (pack params)]
    (b/push (merge {:nupkg-file nupkg-file} (select-keys params [:source :api-key :skip-duplicate :configfile])))))
