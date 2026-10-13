(ns sqltable.main-test
  (:require [clojure.string :as str]
            [clojure.test :refer [deftest is testing]]
            [sqltable.db :as db]
            [sqltable.main :as main]
            [sqltable.table :as table]))

(def people "data/people.csv")

(deftest csv-import
  (with-open [conn (db/open people)]
    (testing "column types inferred, empty is NULL"
      (is (= {:columns ["name" "typeof(age)" "typeof(score)"]
              :rows [["王芳" "integer" "null"]]}
             (db/query conn "select name, typeof(age), typeof(score) from people where id = 4"))))
    (testing "a quoted field with a comma"
      (is (= [["Chen, Wei"]] (:rows (db/query conn "select name from people where id = 3")))))
    (testing "aggregates"
      (is (= [["Beijing" 2] ["Shanghai" 2] ["深圳" 1]]
             (:rows (db/query conn "select city, count(*) from people group by city order by city")))))))

(deftest render
  (is (= (str "+----+-------+-------+\n"
              "| id | name  | score |\n"
              "+----+-------+-------+\n"
              "|  4 | 王芳  |  NULL |\n"
              "|  1 | Alice |  88.5 |\n"
              "+----+-------+-------+\n"
              "2 rows")
         (table/render {:columns ["id" "name" "score"] :rows [[4 "王芳" nil] [1 "Alice" 88.5]]}))))

(deftest run
  (is (str/ends-with? (with-out-str (main/run people "select count(*) as n from people")) "1 row\n")))
