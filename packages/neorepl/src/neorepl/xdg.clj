(ns neorepl.xdg
  "Where neorepl keeps its files, following the XDG base directory spec.")

;; cleat: ported to the CLR. Directories are path strings (java.io.File on the
;; JVM).

(defn base-dir
  "The base directory value, an XDG_*_HOME variable's, names when that's an
  absolute path, as the spec has it, or else default under home."
  ^String [value home default]
  (if (and value (System.IO.Path/IsPathRooted ^String value))
    value
    (System.IO.Path/Combine ^String home ^String default)))

(defn- from-env ^String [var default]
  (base-dir (System.Environment/GetEnvironmentVariable var)
            (System.Environment/GetFolderPath System.Environment+SpecialFolder/UserProfile)
            default))

(defn cache-home ^String []
  (from-env "XDG_CACHE_HOME" ".cache"))

(defn state-home ^String []
  (from-env "XDG_STATE_HOME" ".local/state"))
