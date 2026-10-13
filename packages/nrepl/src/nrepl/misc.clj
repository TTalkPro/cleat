(ns nrepl.misc
  "Misc utilities used in nREPL's implementation (potentially also
  useful for anyone extending it)."
  {:author "Chas Emerick"}
  (:require [clojure.string :as str]
            [nrepl.config :refer [config]]))

;; cleat: ported from nREPL 1.8.0 to the CLR. The JVM-only helpers
;; (java-version, java-8?, attach-self-enabled?, jvmti-agent-enabled?,
;; with-session-classloader's Compiler/LOADER binding) have no CLR counterpart;
;; clr-version takes java-version's place in `describe`.

(defn log
  "Log a message to *err*. The first argument can optionally be a keyword
  log level (e.g. :warning, :error). If the first non-keyword argument is
  a Throwable, its stack trace is printed."
  [?kw & [ex-or-msg & msgs]]
  (let [[kw ex-or-msg msgs] (if (keyword? ?kw)
                              [?kw ex-or-msg msgs]
                              [:error ?kw (cons ex-or-msg msgs)])
        ex (when (instance? Exception ex-or-msg) ex-or-msg)
        msgs (if ex msgs (filter identity (cons ex-or-msg msgs)))]
    (binding [*out* *err*]
      (apply println (str (str/upper-case (name kw)) ":") msgs)
      (when ex (println (str ex)))
      (flush))))

(defmacro log-exceptions
  "Execute body, logging and re-throwing any exception."
  [& body]
  `(try
     ~@body
     (catch Exception ex#
       (log ex#)
       (throw ex#))))

(defmacro returning
  "Executes `body`, returning `x`."
  {:deprecated "1.4"
   :style/indent 1}
  [x & body]
  `(let [x# ~x] ~@body x#))

(defn uuid
  "Returns a new UUID string."
  []
  (str (System.Guid/NewGuid)))

(defn take-until
  "Like (take-while (complement f) coll), but includes the first item in coll that
   returns true for f."
  [f coll]
  (lazy-seq
   (when-first [el coll]
     (cons el (when-not (f el)
                (take-until f (rest coll)))))))

(defn response-for
  "Returns a map containing the :session and :id from the \"request\" `msg`
   as well as all entries specified in `response-data`, which can be one
   or more maps (which will be merged), *or* key-value pairs.

   (response-for msg :status :done :value \"5\")
   (response-for msg {:status :interrupted})

   The :session value in `msg` may be any Clojure reference type (to accommodate
   likely implementations of sessions) that has an :id slot in its metadata,
   or a string."
  [{:keys [session id]} & response-data]
  {:pre [(seq response-data)]}
  (let [{:keys [status] :as response} (if (map? (first response-data))
                                        (reduce merge response-data)
                                        (apply hash-map response-data))
        response (if (not status)
                   response
                   (assoc response :status (if (coll? status)
                                             status
                                             #{status})))
        basis (merge (when id {:id id})
                     ;; AReference should make this suitable for any session implementation?
                     (when session {:session (if (instance? clojure.lang.AReference session)
                                               (-> session meta :id)
                                               session)}))]
    (merge basis response)))

(defmacro resolve-in-session
  "Given an nREPL message and a dynamic variable, return the value of that dynamic
  variable within the message's session, and only if override is missing, then
  get the current dynvar's value.. It is important to use this macro inside
  middleware that accesses dynamic variables and expects user to be able to
  rebind those variables inside user's session and the middleware to observe the
  effect. If you just dereference the dynamic variable, you'll get its value in
  the nREPL server context, not in the user session context."
  [msg dynvar]
  `(get (some-> (:session ~msg) deref) (var ~dynvar) ~dynvar))

(defmacro with-session-classloader
  "On the JVM this binds `clojure.lang.Compiler/LOADER` to the context
  classloader. The CLR has no classloaders, so it only runs `body`."
  [& body]
  `(do ~@body))

(defn clr-version
  "The version of the .NET runtime, as a map like `*clojure-version*`'s."
  []
  (let [v System.Environment/Version]
    {:major (.Major v)
     :minor (.Minor v)
     :incremental (.Build v)
     :version-string (str v)}))

(def safe-var-metadata
  "A list of var metadata attributes are safe to return to the clients.
  We need to guard ourselves against EDN data that's not encodeable/decodable
  with bencode. We also optimize the response payloads by not returning
  redundant metadata."
  [:ns :name :doc :file :arglists :forms :macro :special-form
   :protocol :line :column :added :deprecated :resource])

(defn- resolve-source-file
  "The absolute path of `path`, a source file relative to the load path (like
  \"clojure/core.clj\"), or nil when it's not on it."
  [^String path]
  (some (fn [dir]
          (let [f (System.IO.Path/Combine ^String dir path)]
            (when (System.IO.File/Exists f)
              (System.IO.Path/GetFullPath f))))
        (seq Cleat.Core.Runtime.Loader/LoadPath)))

(defn- handle-file-meta
  "Convert :file metadata to string.
  Typically `value` would be a string, a FileInfo or an Uri."
  [value]
  (when value
    (str (if (string? value)
           ;; try to convert relative file paths like "clojure/core.clj"
           ;; to absolute file paths
           (if (System.IO.Path/IsPathRooted ^String value)
             value
             (or (resolve-source-file value) value))
           value))))

(defn sanitize-meta
  "Sanitize a Clojure metadata map such that it can be bencoded."
  [m]
  (-> m
      (select-keys safe-var-metadata)
      (update :ns str)
      (update :name str)
      (update :protocol str)
      (update :file handle-file-meta)
      (cond-> (:deprecated m) (update :deprecated str))
      (cond-> (:macro m) (update :macro str))
      (cond-> (:special-form m) (update :special-form str))
      (assoc :arglists-str (str (:arglists m)))
      (cond-> (:arglists m) (update :arglists str))))
