;; cleat (designs/18 §4): push a nupkg with dotnet nuget push (nuget.org,
;; BaGetter, any NuGet v3 server).

(ns clojure.tools.build.tasks.push
  (:require
    [clojure.string :as str]
    [clojure.tools.build.api :as api]
    [clojure.tools.build.tasks.process :as process])
  (:import
    [System.IO Path]))

(defn push
  [{:keys [nupkg-file source api-key skip-duplicate configfile] :or {source "https://api.nuget.org/v3/index.json"} :as params}]
  (let [api-key (or api-key (some-> (System.Environment/GetEnvironmentVariable "NUGET_API_KEY") str/trim not-empty))
        args (cond-> ["dotnet" "nuget" "push" (Path/GetFullPath (api/resolve-path nupkg-file)) "--source" source]
               api-key (conj "--api-key" api-key)
               skip-duplicate (conj "--skip-duplicate")
               configfile (conj "--configfile" (Path/GetFullPath (api/resolve-path configfile))))
        {:keys [exit out err]} (process/process (merge {:command-args args
                                                        ;; a source name is looked up in the project's nuget.config
                                                        :dir (api/resolve-path ".")
                                                        :env {"DOTNET_CLI_TELEMETRY_OPTOUT" "1" "DOTNET_NOLOGO" "1"}}
                                                       (select-keys params [:out :err :out-file :err-file])))]
    (when-not (zero? exit)
      (throw (ex-info (str "dotnet nuget push failed: " nupkg-file " -> " source) (cond-> {:exit exit} out (assoc :out out) err (assoc :err err)))))
    nil))
