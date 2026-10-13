(ns nrepl.version
  "nREPL version information."
  {:author "Colin Jones"
   :added  "0.5"})

;; cleat: on the JVM the version comes from the Maven POM properties in the
;; jar. This package is a port of nREPL 1.8.0, so the version is fixed here;
;; the `nrepl.version` environment variable overrides it like the JVM's system
;; property does.

(def ^:private version-string
  "Current version of nREPL as a string.
  See also `version`."
  (or (System.Environment/GetEnvironmentVariable "nrepl.version")
      "1.8.0"))

(def version
  "Current version of nREPL.
  Map of :major, :minor, :incremental, :qualifier, and :version-string."
  (assoc (->> version-string
              (re-find #"(\d+)\.(\d+)\.(\d+)-?(.*)")
              rest
              (map #(try (Int32/Parse ^String %) (catch Exception _e nil)))
              (zipmap [:major :minor :incremental :qualifier]))
         :version-string version-string))
