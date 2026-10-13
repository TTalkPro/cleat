(ns web.test-runner
  "Runs the web.*-test namespaces (cleat-run -M:test, or build.clj's test);
  the exit status is 0 when they all pass."
  (:require [clojure.test :as t]
            [web.handlers-test]))

(defn -main [& _]
  (let [{:keys [fail error]} (t/run-tests 'web.handlers-test)]
    (shutdown-agents)
    (System.Environment/Exit (if (zero? (+ fail error)) 0 1))))
