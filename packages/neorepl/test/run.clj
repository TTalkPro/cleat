;; Runs the neorepl package's tests on cleat:
;;
;;   cleat -I packages/nrepl/src -I packages/tools.build/src -I packages/tools.namespace/src \
;;         -I packages/neorepl/src -I packages/neorepl/test packages/neorepl/test/run.clj [ns-regex]
;;
;; Every neorepl.*-test namespace under test/ runs (those matching ns-regex when
;; given); the exit status is 0 when they all pass, 1 otherwise.
;; NEOREPL_TEST_VERBOSE=1 prints each test's name and time as it runs.
(ns neorepl.test-runner
  (:require [clojure.string :as str]
            [clojure.test :as t]))

(def ^:private test-dir
  (System.IO.Path/GetDirectoryName (System.IO.Path/GetFullPath *file*)))

(defn- test-namespaces []
  (->> (System.IO.Directory/GetFiles test-dir "*_test.clj" System.IO.SearchOption/AllDirectories)
       (map #(-> (System.IO.Path/GetRelativePath test-dir %)
                 (str/replace "\\" "/")
                 (str/replace #"\.clj$" "")
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
        (if (System.Environment/GetEnvironmentVariable "NEOREPL_TEST_VERBOSE")
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
