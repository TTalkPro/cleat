;; Builds cleat.nanopass with tools.build: one package assembly
;; Cleat.Nanopass.dll, packed as the NuGet package Cleat.Nanopass.
;;
;;   cleat -T:build pack | install [:local-feed '"dir"'] | push [:source '"url"']
(ns build
  (:require [clojure.tools.build.api :as b]))

(def lib 'Cleat.Nanopass)
(def default-version "1.0.0")   ; cleat-182: split out of the Clojure compiler package
(def class-dir "target/classes")

(defn- nupkg [version] (format "target/%s.%s.nupkg" lib version))

(defn clean [_]
  (b/delete {:path "target"}))

(defn pack
  [{:keys [version] :or {version default-version} :as params}]
  (clean nil)
  (let [basis (b/create-basis)]
    (b/compile-clj {:basis basis :class-dir class-dir :src-dirs ["src"] :lib lib :package "nanopass"})
    (b/pack {:basis basis :lib lib :version version :class-dir class-dir :nupkg-file (nupkg version)
             :description "nanopass-framework-scheme ported to Clojure on cleat (Clojure on .NET)"
             :authors ["Dipanwita Sarkar" "Andrew W. Keep" "R. Kent Dybvig" "Oscar Waddell" "cleat"] :license "MIT" :tags ["clojure" "cleat"]
             :project-url "https://github.com/nanopass/nanopass-framework-scheme"})
    (assoc params :nupkg-file (nupkg version))))

(defn install [params]
  (let [{:keys [nupkg-file]} (pack params)]
    (b/install (merge {:nupkg-file nupkg-file} (select-keys params [:local-feed])))))

(defn push [params]
  (let [{:keys [nupkg-file]} (pack params)]
    (b/push (merge {:nupkg-file nupkg-file} (select-keys params [:source :api-key :skip-duplicate :configfile])))))
