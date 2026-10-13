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

(defn deps-args
  "What -M:alias1:alias2 means for the project in the current directory:
  {:load-path [entries of the basis], :args [the aliases' :cleat-opts, then
  :main-opts]}. The cleat host (cleat -M:…, cleat-071) puts :load-path on its
  load path and runs :args followed by the rest of its command line."
  [alias-arg]
  (let [aliases (->> (str/split (subs alias-arg 2) #":") (remove str/blank?) (map keyword) vec)
        create-basis (requiring-resolve 'clojure.tools.build.api/create-basis)
        {:keys [classpath-roots argmap]} (create-basis {:aliases aliases})]
    {:load-path (mapv str classpath-roots)
     :args (vec (concat (:cleat-opts argmap) (:main-opts argmap)))}))

(defn run-command
  "The command line for -M:aliases with args (a vector of strings), as a
  separate cleat process."
  [alias-arg args]
  (let [{:keys [load-path] deps :args} (deps-args alias-arg)
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
