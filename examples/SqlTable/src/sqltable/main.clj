(ns sqltable.main
  "sqltable FILE SQL: runs one SQL statement over a CSV file (imported into an
  in-memory SQLite table named after the file) or a SQLite database, and prints
  the result as a table.

    dotnet cleat -M:run data/people.csv \"select city, count(*) n from people group by city\"
    dotnet cleat -M:run app.db \"select * from users limit 5\""
  (:require [sqltable.db :as db]
            [sqltable.table :as table]))

(defn- usage []
  (binding [*out* *err*]
    (println "usage: sqltable FILE SQL")
    (println "  FILE  a .csv file (a table named after the file) or a SQLite database (.db / .sqlite)")
    (println "  SQL   one statement, e.g. \"select * from people where age > 30\"")))

(defn run
  "Prints the result of sql over the file; returns the exit status."
  [file sql]
  (with-open [conn (db/open file)]
    (let [result (db/query conn sql)]
      (if (contains? result :affected)
        (let [n (:affected result)] (println n (if (= 1 n) "row affected" "rows affected")))
        (println (table/render result)))
      0)))

(defn -main [& args]
  (let [[file sql & more] args
        exit (if (or (nil? sql) (seq more))
               (do (usage) 2)
               (try
                 (run file sql)
                 (catch Exception e
                   (binding [*out* *err*] (println "sqltable:" (ex-message e)))
                   1)))]
    (flush)
    (when-not (zero? exit) (System.Environment/Exit exit))))
