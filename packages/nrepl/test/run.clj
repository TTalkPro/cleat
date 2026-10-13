;; Runs the nrepl package's tests on cleat:
;;
;;   cleat -I packages/nrepl/src -I packages/nrepl/test packages/nrepl/test/run.clj [ns-regex]
;;
;; Every nrepl.*-test namespace under test/ runs (those matching ns-regex when
;; given); the exit status is 0 when they all pass, 1 otherwise.
(ns nrepl.test-runner
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
  ;; Tests run in user, as under clojure.main.
  (in-ns 'user)
  (let [{:keys [fail error] :as summary}
        ;; NREPL_TEST_VERBOSE=1 prints each test's name and time as it runs.
        (if (System.Environment/GetEnvironmentVariable "NREPL_TEST_VERBOSE")
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
