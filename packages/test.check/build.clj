;; Builds test.check with tools.build: one package assembly
;; Cleat.TestCheck.dll, packed as the NuGet package Cleat.TestCheck.
;;
;;   cleat -T:build pack | install [:local-feed '"dir"'] | push [:source '"url"']
(ns build
  (:require [clojure.tools.build.api :as b]))

(def lib 'Cleat.TestCheck)
(def default-version "1.1.4")   ; cleat-166
(def class-dir "target/classes")

(defn- nupkg [version] (format "target/%s.%s.nupkg" lib version))

(defn clean [_]
  (b/delete {:path "target"}))

(defn pack
  [{:keys [version] :or {version default-version} :as params}]
  (clean nil)
  (let [basis (b/create-basis)]
    (b/compile-clj {:basis basis :class-dir class-dir :src-dirs ["src"] :lib lib :package "test.check"})
    (b/pack {:basis basis :lib lib :version version :class-dir class-dir :nupkg-file (nupkg version)
             :description "clojure.test.check (from ClojureCLR's clr.test.check) for cleat (Clojure on .NET): property-based testing and generators"
             :authors ["Gary Fredericks" "Reid Draper" "David Miller" "cleat"] :license "EPL-1.0" :tags ["clojure" "cleat"]
             :project-url "https://github.com/clojure/clr.test.check"})
    (assoc params :nupkg-file (nupkg version))))

(defn install [params]
  (let [{:keys [nupkg-file]} (pack params)]
    (b/install (merge {:nupkg-file nupkg-file} (select-keys params [:local-feed])))))

(defn push [params]
  (let [{:keys [nupkg-file]} (pack params)]
    (b/push (merge {:nupkg-file nupkg-file} (select-keys params [:source :api-key :skip-duplicate :configfile])))))
