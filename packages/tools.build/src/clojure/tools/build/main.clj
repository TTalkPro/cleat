;; cleat (designs/18 §5): running build functions, as `clj -T:build fn :k v`
;; does upstream:
;;
;;   cleat -I <tools.build>/src -I <tools.namespace>/src -I . -m clojure.tools.build.main jar :version '"1.0.0"'
;;
;; The first argument names the function (an unqualified name is in the
;; `build` namespace, i.e. build.clj in the project root); the rest are
;; key/value pairs read as EDN, passed as one params map.

(ns clojure.tools.build.main
  (:require
    [clojure.edn :as edn]
    [clojure.string :as str]))

(defn- usage []
  (binding [*out* *err*]
    (println "Usage: cleat-build FUNCTION [:key value]...")
    (println "  FUNCTION - a function taking a params map; unqualified names are in the build namespace (build.clj)")
    (println "  :key value - EDN pairs, merged into the params map")))

(defn -main
  [& args]
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
