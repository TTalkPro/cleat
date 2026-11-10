;; Builds test.check with tools.build: one package assembly
;; Cleat.TestCheck.dll, packed as the NuGet package Cleat.TestCheck.
;;
;;   cleat -T:build pack | install [:local-feed '"dir"'] | push [:source '"url"']
(ns build
  (:require [clojure.tools.build.api :as b]
            [clojure.tools.namespace.find :as find]))

;; clojure_test/cljs.cljc 声明的命名空间是 clojure.test.check.clojure-test.assertions.cljs（与文件路径不符，只给 ClojureScript 用），
;; compile-clj 按 ns 名找文件找不到：编译列表里去掉它（cleat-187；源码照样进包）
(defn- nses-to-compile []
  (remove #{'clojure.test.check.clojure-test.assertions.cljs}
          (find/find-namespaces-in-dir (System.IO.DirectoryInfo. "src"))))

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
    (b/compile-clj {:basis basis :class-dir class-dir :src-dirs ["src"] :ns-compile (nses-to-compile) :lib lib :package "test.check"})
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
