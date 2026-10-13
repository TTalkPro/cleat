(ns nrepl.config
  "Server configuration utilities.
  Some server options can be configured via configuration
  files (local or global).  This namespace provides
  convenient API to work with them.

  The config resolution algorithm is the following:
  The global config file .nrepl/nrepl.edn is merged with
  any local config file (.nrepl.edn) if present.
  The values in the local config file take precedence."
  {:author "Bozhidar Batsov"
   :added  "0.5"}
  (:require
   [clojure.edn :as edn]
   [clojure.string :as str])
  (:import
   (System.IO File Path)))

;; cleat: java.io.File is replaced by path strings (System.IO.Path / File).

(def ^:private home-dir
  "The user's home directory."
  (System.Environment/GetFolderPath System.Environment+SpecialFolder/UserProfile))

(def ^:private xdg-config-default-dir
  "Default XDG config directory (~/.config)."
  (Path/Combine ^String home-dir ".config"))

(defn- non-empty-env
  "Get the value of the environment variable.
  Return the value of the environment variable if it's non-empty,
  or `nil` otherwise."
  [env-var]
  (let [value (System.Environment/GetEnvironmentVariable env-var)]
    (when-not (str/blank? value) value)))

(def ^:private config-file-name "nrepl.edn")

(defn- path [& parts]
  (reduce #(Path/Combine ^String %1 ^String %2) parts))

(def config-file
  "nREPL's global configuration file.

  The location is determined with the following precedence:
  * $NREPL_CONFIG_DIR/nrepl.edn
  * $XDG_CONFIG_HOME/nrepl/nrepl.edn
  * ~/.config/nrepl/nrepl.edn (if ~/.config exists)
  * ~/.nrepl/nrepl.edn

  Return the first existing config file in this order. If no config
  file exists in any of these locations, return the default path
  `~/.nrepl/nrepl.edn`."
  (let [candidates
        [(some-> (non-empty-env "NREPL_CONFIG_DIR") (path config-file-name))
         (some-> (non-empty-env "XDG_CONFIG_HOME") (path "nrepl" config-file-name))
         (path xdg-config-default-dir "nrepl" config-file-name)
         (path home-dir ".nrepl" config-file-name)]]
    (or (some (fn [file]
                (when (and file (File/Exists ^String file)) file))
              candidates)
        (last candidates))))

(def config-dir
  "nREPL's global configuration directory."
  (Path/GetDirectoryName ^String config-file))

(defn- load-edn
  "Load edn from a file."
  [^String file]
  (with-open [r (System.IO.StreamReader. file)]
    (edn/read (clojure.lang.PushbackTextReader. r))))

(defn- load-config
  "Load the configuration file identified by `file`.
  Return its contents as EDN if the file exists,
  or an empty map otherwise."
  [file]
  (if (File/Exists ^String file)
    (load-edn file)
    {}))

(def config
  "Configuration map.
  It's created by merging the global configuration file
  with a local configuration file that would normally
  be placed in the directory in which you're running
  nREPL."
  (merge
   (load-config config-file)
   (load-config ".nrepl.edn")))
