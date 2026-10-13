;; cleat (designs/18 §5): running build functions, as `clj -T:build fn :k v`
;; does upstream:
;;
;;   cleat -I <tools.build>/src -I <tools.namespace>/src -I . -m clojure.tools.build.main jar :version '"1.0.0"'
;;
;; The first argument names the function (an unqualified name is in the
;; `build` namespace, i.e. build.clj in the project root); the rest are
;; key/value pairs read as EDN, passed as one params map.
;;
;; `-M:alias1:alias2 args...` runs the project instead, as `clj -M:alias` does
;; (bin/cleat-run): the basis of cleat.edn with those aliases gives the load
;; path (-I each entry), the aliases' :cleat-opts and :main-opts come next,
;; then args; the terminal is passed on, and the exit status is the program's.

(ns clojure.tools.build.main
  (:require
    [clojure.edn :as edn]
    [clojure.string :as str]))

(defn- usage []
  (binding [*out* *err*]
    (println "Usage: cleat-build FUNCTION [:key value]...")
    (println "       cleat-run -M:alias[:alias...] [args...]")
    (println "  FUNCTION - a function taking a params map; unqualified names are in the build namespace (build.clj)")
    (println "  :key value - EDN pairs, merged into the params map")))

(defn run-command
  "The command line for -M:aliases with args (a vector of strings)."
  [alias-arg args]
  (let [aliases (->> (str/split (subs alias-arg 2) #":") (remove str/blank?) (map keyword) vec)
        create-basis (requiring-resolve 'clojure.tools.build.api/create-basis)
        cleat (requiring-resolve 'clojure.tools.build.tasks.process/cleat-executable)
        {:keys [classpath-roots argmap]} (create-basis {:aliases aliases})]
    (vec (concat (cleat)
                 (mapcat #(vector "-I" (str %)) classpath-roots)
                 (:cleat-opts argmap)
                 (:main-opts argmap)
                 args))))

(defn- run-aliases
  "-M:aliases: run the project's cleat with the basis's load path and the
  aliases' :main-opts; returns the exit status."
  [alias-arg args]
  (let [process (requiring-resolve 'clojure.tools.build.api/process)
        command (run-command alias-arg args)]
    (:exit (process {:command-args command}))))

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
      (binding [*out* *err*] (println "cleat-build: key/value args must come in pairs:" (str/join " " kvs)))
      (System.Environment/Exit 2))
    (let [sym (symbol f)
          sym (if (namespace sym) sym (symbol "build" (name sym)))
          params (into {} (map (fn [[k v]] [(edn/read-string k) (edn/read-string v)]) (partition 2 kvs)))
          fvar (or (requiring-resolve sym)
                   (throw (ex-info (str "cleat-build: no function " sym) {:fn sym})))]
      (@fvar params)
      (shutdown-agents))))
