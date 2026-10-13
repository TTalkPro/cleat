(ns sqltable.csv-test
  (:require [clojure.test :refer [deftest is]]
            [sqltable.csv :as csv]))

(deftest parse
  (is (= [["a" "b"] ["1" "2"]] (csv/parse "a,b\n1,2\n")))
  (is (= [["x,y" "say \"hi\""] ["two\nlines" ""]] (csv/parse "\"x,y\",\"say \"\"hi\"\"\"\r\n\"two\nlines\",\n")))
  (is (= [] (csv/parse ""))))
