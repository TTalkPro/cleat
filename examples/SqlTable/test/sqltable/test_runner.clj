(ns sqltable.test-runner
  (:require [clojure.test :as t]
            [sqltable.csv-test]
            [sqltable.main-test]))

(defn -main [& _]
  (let [{:keys [fail error]} (t/run-tests 'sqltable.csv-test 'sqltable.main-test)]
    (shutdown-agents)
    (System.Environment/Exit (if (zero? (+ fail error)) 0 1))))
