;; Runs the clojure.test.generative package's tests (defspecs, not deftests) on cleat:
;;
;;   cleat -I packages/test.generative/src -I packages/data.generators/src -I packages/tools.namespace/src \
;;         -I packages/test.generative/test packages/test.generative/test/run.clj [msec]
;;
;; Every defspec under test/ runs on 2 threads for msec milliseconds (default 2000; upstream's runner reads
;; clojure.test.generative.msec, default 10000) with the upstream runner's run-suite; the exit status is the
;; number of failing specs (0 when they all pass). Prints "0 failures" on success, as the other packages' run.clj do.
(ns cleat.test.generative.test-runner
  (:require [clojure.test.generative.runner :as runner]))

(def ^:private test-dir
  (System.IO.Path/GetDirectoryName (System.IO.Path/GetFullPath *file*)))

(let [msec (if-let [a (first *command-line-args*)] (Int64/Parse a) 2000)
      tests (runner/dir-tests [test-dir])
      {:keys [failures iters tests] :as result} (runner/run-suite {:nthreads 2 :msec msec} tests)]
  (println)
  (println result)
  (println (str "Ran " tests " specs, " iters " iterations; " failures " failures."))
  (shutdown-agents)
  (flush)
  (Environment/Exit (if (zero? failures) 0 1)))
