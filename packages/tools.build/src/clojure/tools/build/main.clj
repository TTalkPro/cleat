;; cleat (designs/18 §5): running build functions, as `clj -T:build fn :k v`
;; does upstream:
;;
;;   cleat -I <tools.build>/src -I <tools.namespace>/src -I . -m clojure.tools.build.main jar :version '"1.0.0"'
;;
;; The first argument names the function (an unqualified name is in the
;; `build` namespace, i.e. build.clj in the project root); the rest are
;; key/value pairs read as EDN, passed as one params map.
;;
;; `-M:alias1:alias2 args...` runs the project instead, as `clj -M:alias` does:
;; the basis of cleat.edn with those aliases gives the load path (-I each
;; entry), the aliases' :cleat-opts and :main-opts come next, then args; the
;; terminal is passed on, and the exit status is the program's.
;;
;; The cleat host has both built in (cleat-071): `cleat -T:build fn :k v` and
;; `cleat -M:alias args`, the latter in the same process (deps-args).

(ns clojure.tools.build.main
  (:require
    [clojure.edn :as edn]
    [clojure.string :as str]))

(defn- usage []
  (binding [*out* *err*]
    (println "Usage: cleat -T:NS FUNCTION [:key value]...   (NS: build for build.clj)")
    (println "       cleat -M:alias[:alias...] [args...]")
    (println "  FUNCTION - a function taking a params map; unqualified names are in the build namespace (build.clj)")
    (println "  :key value - EDN pairs, merged into the params map")))

(defn- alias-keys
  "The aliases in an -M:a:b / -X:a:b / -T:a style argument (after its 2-char flag)."
  [arg]
  (->> (str/split (subs arg 2) #":") (remove str/blank?) (map keyword) vec))

(defn- basis
  [params]
  ((requiring-resolve 'clojure.tools.build.api/create-basis) params))

(defn- extra-deps
  "-Sdeps's EDN (a map merged over cleat.edn), or nil."
  [sdeps]
  (when (seq sdeps) (edn/read-string sdeps)))

(defn deps-args
  "What -M:alias1:alias2 means for the project in the current directory:
  {:load-path [entries of the basis], :args [the aliases' :cleat-opts, then
  :main-opts]}. The cleat host (cleat -M:…, cleat-071) puts :load-path on its
  load path and runs :args followed by the rest of its command line.
  sdeps is -Sdeps's EDN, merged over cleat.edn."
  ([alias-arg] (deps-args alias-arg nil))
  ([alias-arg sdeps]
   (let [{:keys [classpath-roots argmap]} (basis (cond-> {:aliases (alias-keys alias-arg)}
                                                   (seq sdeps) (assoc :extra (extra-deps sdeps))))]
     {:load-path (mapv str classpath-roots)
      :args (vec (concat (:cleat-opts argmap) (:main-opts argmap)))})))

(defn- read-kvs
  "Command line key/value pairs as EDN: a key is a keyword, or a vector of
  keywords (a path, as clj -X allows)."
  [kvs]
  (when (odd? (count kvs))
    (throw (ex-info (str "key/value args must come in pairs: " (str/join " " kvs)) {:type ::usage})))
  (reduce (fn [m [k v]]
            (let [k (edn/read-string k)
                  v (edn/read-string v)]
              (if (vector? k) (assoc-in m k v) (assoc m k v))))
          {} (partition 2 kvs)))

(defn exec-plan
  "What -X:aliases or -T:alias with args (a function name, then key/value
  pairs) means: {:load-path [...] :fn qualified-symbol :params map}.

  -X (clj -X): the project's basis with the aliases; the function is the first
  arg when it isn't a key (unqualified names take the aliases' :ns-default),
  else the aliases' :exec-fn; params are the aliases' :exec-args merged with the
  key/value pairs.

  -T (clj -T): a tool, without the project's deps or paths: the alias's :deps
  and :paths (default [\".\"], the project root) when cleat.edn has the alias,
  its :ns-default and :exec-args. Without such an alias the name is the
  namespace (cleat: -T:build calls build.clj's functions with the bundled
  tools.build, no alias needed)."
  [mode alias-arg args sdeps]
  (let [aliases (alias-keys alias-arg)
        [f kvs] (if (and (seq args) (not (str/starts-with? (first args) ":")) (not (str/starts-with? (first args) "[")))
                  [(first args) (rest args)]
                  [nil args])
        project (let [read (requiring-resolve 'clojure.tools.build.tasks.create-basis/read-project)] (read))
        [load-path argmap]
        (case mode
          "X" (let [{:keys [classpath-roots argmap]} (basis (cond-> {:aliases aliases}
                                                                 (seq sdeps) (assoc :extra (extra-deps sdeps))))]
                [classpath-roots argmap])
          "T" (let [alias (first aliases)
                    tool (get-in project [:aliases alias])]
                (when (next aliases)
                  (throw (ex-info "-T takes one tool alias" {:type ::usage})))
                (if tool
                  (let [{:keys [classpath-roots]} (basis {:project (merge {:paths ["."] :deps {}}
                                                                          (select-keys tool [:paths :deps]))
                                                          :extra (extra-deps sdeps)})]
                    [classpath-roots (dissoc tool :paths :deps)])
                  [["."] {:ns-default (symbol (name alias))}])))
        f (or (some-> f symbol) (:exec-fn argmap)
              (throw (ex-info (str "-" mode ": no function to run (give one, or :exec-fn in the alias)") {:type ::usage})))
        f (if (namespace f) f
              (if-let [ns (:ns-default argmap)]
                (symbol (str ns) (name f))
                (throw (ex-info (str "-" mode ": unqualified function " f " and no :ns-default") {:type ::usage}))))]
    {:load-path (mapv str load-path)
     :fn f
     :params (merge (:exec-args argmap) (read-kvs kvs))}))

(defn exec!
  "Runs -X / -T in this process (the cleat host calls it): adds the plan's load
  path, then calls the function with the params map."
  [mode alias-arg args sdeps]
  (let [{:keys [load-path fn params]} (exec-plan mode alias-arg (vec args) sdeps)]
    (doseq [p load-path] (Cleat.Core.Runtime.Loader/AddPath (System.IO.Path/GetFullPath ^String p)))
    (let [fvar (or (requiring-resolve fn)
                   (throw (ex-info (str "-" mode ": no function " fn) {:type ::usage})))]
      (@fvar params))))

(defn run-command
  "The command line for -M:aliases with args (a vector of strings), as a
  separate cleat process."
  [alias-arg args]
  (let [{:keys [load-path] deps :args} (deps-args alias-arg nil)
        cleat (requiring-resolve 'clojure.tools.build.tasks.process/cleat-executable)]
    (vec (concat (cleat)
                 (mapcat #(vector "-I" %) load-path)
                 deps
                 args))))

(defn- run-aliases
  "-M:aliases: run the project's cleat with the basis's load path and the
  aliases' :main-opts, standard streams inherited; returns the exit status.
  SIGTERM is passed on to the program (and this process waits for it), so
  killing the runner stops what it runs instead of orphaning it; Ctrl-C reaches
  both through the terminal anyway."
  [alias-arg args]
  (let [[cmd & cmd-args] (run-command alias-arg args)
        psi (System.Diagnostics.ProcessStartInfo. ^String cmd)
        _ (doseq [a cmd-args] (.Add (.ArgumentList psi) (str a)))
        _ (set! (.UseShellExecute psi) false)
        proc (System.Diagnostics.Process/Start psi)
        forward (fn [^System.Runtime.InteropServices.PosixSignalContext ctx]
                  (set! (.Cancel ctx) true)
                  (try
                    (.WaitForExit (System.Diagnostics.Process/Start "kill" (str "-TERM " (.Id proc))))
                    (catch Exception _ (.Kill proc true))))
        registration (when-not (System.OperatingSystem/IsWindows)
                       (System.Runtime.InteropServices.PosixSignalRegistration/Create
                         System.Runtime.InteropServices.PosixSignal/SIGTERM
                         (gen-delegate |System.Action`1[System.Runtime.InteropServices.PosixSignalContext]| [ctx] (forward ctx))))]
    (try
      (.WaitForExit proc)
      (.ExitCode proc)
      (finally
        (some-> registration .Dispose)))))

(defn -main
  [& args]
  (when (some-> (first args) (str/starts-with? "-M"))
    (let [exit (run-aliases (first args) (vec (rest args)))]
      (shutdown-agents)
      (System.Environment/Exit (int exit))))
  (let [[f & kvs] args]
    (when (or (nil? f) (#{"-h" "--help"} f))
      (usage)
      (System.Environment/Exit (if f 0 2)))
    (when (odd? (count kvs))
      (binding [*out* *err*] (println "cleat -T: key/value args must come in pairs:" (str/join " " kvs)))
      (System.Environment/Exit 2))
    (let [sym (symbol f)
          sym (if (namespace sym) sym (symbol "build" (name sym)))
          params (into {} (map (fn [[k v]] [(edn/read-string k) (edn/read-string v)]) (partition 2 kvs)))
          fvar (or (requiring-resolve sym)
                   (throw (ex-info (str "cleat -T: no function " sym) {:fn sym})))]
      (@fvar params)
      (shutdown-agents))))
