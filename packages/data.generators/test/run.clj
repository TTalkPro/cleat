;; Runs the clojure.data.generators package's tests on cleat:
;;
;;   cleat -I packages/data.generators/src -I packages/data.generators/test packages/data.generators/test/run.clj [ns-regex]
;;
;; Every *-test namespace under test/ (and clojure.test.check.test) runs (those matching ns-regex when
;; given); the exit status is 0 when they all pass, 1 otherwise.
;; TEST_VERBOSE=1 prints each test's name and time as it runs.
(ns cleat.data.generators.test-runner
  (:require [clojure.string :as str]
            [clojure.test :as t]))

(def ^:private test-dir
  (System.IO.Path/GetDirectoryName (System.IO.Path/GetFullPath *file*)))

(defn- test-namespaces []
  (->> (filter #(re-find #"(_test|[/\\]test)\.cljc?$" %) (System.IO.Directory/GetFiles test-dir "*.clj*" System.IO.SearchOption/AllDirectories))
       (map #(-> (System.IO.Path/GetRelativePath test-dir %)
                 (str/replace "\\" "/")
                 (str/replace #"\.cljc?$" "")
                 (str/replace "/" ".")
                 (str/replace "_" "-")
                 symbol))
       sort))

(let [pattern (some-> (first *command-line-args*) re-pattern)
      nses (cond->> (test-namespaces)
             pattern (filter #(re-find pattern (str %))))]
  (apply require nses)
  (in-ns 'user)
  (let [{:keys [fail error]}
        (if (System.Environment/GetEnvironmentVariable "TEST_VERBOSE")
          (let [report t/report
                start (atom 0)]
            (binding [t/report (fn [m]
                                 (case (:type m)
                                   :begin-test-var (do (reset! start (Environment/TickCount64))
                                                       (println "  " (-> m :var meta :name)))
                                   :end-test-var (println "    " (- (Environment/TickCount64) @start) "ms")
                                   nil)
                                 (report m))]
              (apply t/run-tests nses)))
          (apply t/run-tests nses))]
    (shutdown-agents)
    (flush)
    (Environment/Exit (if (zero? (+ fail error)) 0 1))))
