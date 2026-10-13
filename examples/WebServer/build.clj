;; The example's build (tools.build): dotnet cleat -T:build TASK [:key value]...
;;
;;   prep     dotnet build the C# library into target/dotnet (the REPL's load path)
;;   compile  prep, then the Clojure code into one package assembly,
;;            target/classes/Web.Handlers.dll (the host's build runs this)
;;   test     the clojure.test suite
;;   pack     a NuGet package Web.Handlers (the assemblies, depending on
;;            Cleat.Runtime and Cleat.Nrepl) -- for other .NET apps
;;   push     pack, then push to BaGetter: the nuget.config source "bagetter"
;;            (:source to override; :api-key, or $NUGET_API_KEY)
;;   publish  dotnet publish the host into target/publish
(ns build
  (:refer-clojure :exclude [compile test])
  (:require [clojure.tools.build.api :as b])
  (:import (System.IO Path)))

(def lib 'Web.Handlers)
(def version "1.0.0")
(def class-dir "target/classes")
(def dotnet-dir "target/dotnet")

(defn clean [_]
  (b/delete {:path "target"}))

(defn prep [_]
  (b/dotnet-build {:project "csharp/WebCore/WebCore.csproj" :output-dir dotnet-dir}))

(defn compile
  "The Clojure package assembly. :dotnet-dir is where WebCore.dll is (the host's
  build passes its own); without it, prep builds it."
  [{dir :dotnet-dir}]
  (let [dir (or dir (do (prep nil) dotnet-dir))
        basis (update (b/create-basis) :classpath-roots conj (str dir))]
    (b/compile-clj {:basis basis :class-dir class-dir :src-dirs ["src"] :lib lib :package "web"})))

(defn test [_]
  (prep nil)
  (let [{:keys [exit]} (b/process (b/cleat-command {:basis (b/create-basis {:aliases [:test]})
                                                     :main 'web.test-runner}))]
    (when-not (zero? exit)
      (throw (ex-info "tests failed" {:exit exit})))))

(defn pack [params]
  (clean nil)
  (compile params)
  ;; the C# library ships in the package too
  (b/copy-file {:src (str dotnet-dir "/WebCore.dll") :target (str class-dir "/WebCore.dll")})
  (b/pack {:basis (b/create-basis) :lib lib :version version :class-dir class-dir
           :nupkg-file (format "target/%s.%s.nupkg" lib version)
           :description "The cleat example's handlers: Clojure behind a C# interface"
           :authors ["cleat"] :license "EPL-1.0"}))

(defn push [params]
  (pack params)
  (b/push (merge {:nupkg-file (format "target/%s.%s.nupkg" lib version)
                  ;; by name: nuget.config allows its http:// URL (allowInsecureConnections)
                  :source "bagetter"}
                 (select-keys params [:source :api-key :configfile]))))

(defn publish [_]
  (let [{:keys [exit]} (b/process {:command-args ["dotnet" "publish" "host" "-c" "Release" "-o" "target/publish"]})]
    (when-not (zero? exit)
      (throw (ex-info "dotnet publish failed" {:exit exit})))))
