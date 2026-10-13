;; cleat (designs/18 §4): javac upstream -> dotnet build of a C# / F# project.

(ns clojure.tools.build.tasks.dotnet-build
  (:require
    [clojure.string :as str]
    [clojure.tools.build.api :as api]
    [clojure.tools.build.tasks.process :as process])
  (:import
    [System.IO Directory Path]))

(defn dotnet-build
  [{:keys [project output-dir configuration properties dotnet-opts] :or {configuration "Release"} :as params}]
  (let [project (Path/GetFullPath (api/resolve-path project))
        project-dir (if (Directory/Exists project) project (Path/GetDirectoryName project))
        output-dir (Path/GetFullPath (if output-dir
                                       (api/resolve-path output-dir)
                                       (Path/Combine project-dir "bin" configuration)))
        args (vec (concat ["dotnet" "build" project "-c" configuration "-o" output-dir "-nologo"]
                          (for [[k v] properties] (str "-p:" (name k) "=" v))
                          dotnet-opts))
        {:keys [exit out err]} (process/process (merge {:command-args args
                                                        :env {"DOTNET_CLI_TELEMETRY_OPTOUT" "1" "DOTNET_NOLOGO" "1"}}
                                                       (select-keys params [:out :err :out-file :err-file])))]
    (when-not (zero? exit)
      (throw (ex-info (str "dotnet build failed: " project) (cond-> {:exit exit} out (assoc :out out) err (assoc :err err)))))
    (cond-> {:output-dir output-dir}
      out (assoc :out out)
      err (assoc :err err))))
