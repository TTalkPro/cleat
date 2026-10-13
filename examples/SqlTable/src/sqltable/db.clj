(ns sqltable.db
  "SQLite through Microsoft.Data.Sqlite (a NuGet package, cleat.edn's :deps;
  its native library e_sqlite3 comes with it): a .csv file becomes a table in
  an in-memory database, a .db / .sqlite file is opened read-only."
  (:require [clojure.string :as str]
            [sqltable.csv :as csv])
  (:import (Microsoft.Data.Sqlite SqliteConnection SqliteDataReader)
           (System.IO File Path)))

(defn table-name
  "The table a CSV file becomes: its file name without the extension, as an
  identifier (people.csv -> people)."
  [^String path]
  (let [n (str/replace (Path/GetFileNameWithoutExtension path) #"[^A-Za-z0-9_]" "_")]
    (if (re-find #"^[0-9]" n) (str "_" n) n)))

(defn- integer-text? [s] (boolean (re-matches #"[+-]?\d{1,18}" s)))
(defn- real-text? [s] (boolean (re-matches #"[+-]?(\d+\.\d*|\.\d+|\d+)([eE][+-]?\d+)?" s)))

(defn column-type
  "INTEGER, REAL or TEXT for a column's (non-empty) values."
  [values]
  (let [vs (remove str/blank? values)]
    (cond
      (empty? vs) "TEXT"
      (every? integer-text? vs) "INTEGER"
      (every? real-text? vs) "REAL"
      :else "TEXT")))

(defn- value [type ^String s]
  (cond
    (str/blank? s) DBNull/Value
    (= type "INTEGER") (Int64/Parse s)
    (= type "REAL") (Double/Parse s System.Globalization.CultureInfo/InvariantCulture)
    :else s))

(defn- quote-id [s] (str "\"" (str/replace s "\"" "\"\"") "\""))

(defn- execute! [^SqliteConnection conn sql]
  (let [cmd (.CreateCommand conn)]
    (set! (.CommandText cmd) sql)
    (.ExecuteNonQuery cmd)))

(defn import-csv!
  "Creates table from the CSV file's header and rows; returns the row count."
  [^SqliteConnection conn ^String path table]
  (let [[header & rows] (csv/parse (File/ReadAllText path))
        _ (when-not header (throw (ex-info (str path " is empty") {:path path})))
        width (count header)
        rows (map #(vec (take width (concat % (repeat "")))) rows)
        types (mapv (fn [i] (column-type (map #(nth % i) rows))) (range width))]
    (execute! conn (str "CREATE TABLE " (quote-id table) " ("
                        (str/join ", " (map #(str (quote-id %1) " " %2) header types)) ")"))
    (with-open [tx (.BeginTransaction conn)]
      (let [cmd (.CreateCommand conn)]
        (set! (.Transaction cmd) tx)
        (set! (.CommandText cmd) (str "INSERT INTO " (quote-id table) " VALUES ("
                                      (str/join ", " (map #(str "$p" %) (range width))) ")"))
        (doseq [i (range width)] (.AddWithValue (.Parameters cmd) (str "$p" i) DBNull/Value))
        (doseq [row rows]
          (doseq [i (range width)]
            (set! (.Value (.get_Item (.Parameters cmd) (int i))) (value (nth types i) (nth row i))))
          (.ExecuteNonQuery cmd))
        (.Commit tx)))
    (count rows)))

(defn open
  "A connection for the file: a .csv imported into memory (as a table named
  after the file), or a SQLite database opened read-only."
  ^SqliteConnection [^String path]
  (when-not (File/Exists path)
    (throw (ex-info (str "No such file: " path) {:path path})))
  (if (str/ends-with? (str/lower-case path) ".csv")
    (let [conn (SqliteConnection. "Data Source=:memory:")]
      (.Open conn)
      (import-csv! conn path (table-name path))
      conn)
    (doto (SqliteConnection. (str "Data Source=" path ";Mode=ReadOnly"))
      (.Open))))

(defn query
  "Runs sql: {:columns [names] :rows [[values]]} (NULL is nil); a statement
  without a result set gives {:affected n}."
  [^SqliteConnection conn sql]
  (let [cmd (.CreateCommand conn)]
    (set! (.CommandText cmd) sql)
    (with-open [^SqliteDataReader r (.ExecuteReader cmd)]
      (let [n (.FieldCount r)]
        (if (zero? n)
          {:affected (.RecordsAffected r)}
          {:columns (mapv #(.GetName r (int %)) (range n))
           :rows (loop [rows []]
                   (if (.Read r)
                     (recur (conj rows (mapv #(when-not (.IsDBNull r (int %)) (.GetValue r (int %))) (range n))))
                     rows))})))))
