;; SqlTable's build (tools.build): dotnet cleat -T:build TASK
;;
;;   test     the clojure.test suite
;;   compile  the Clojure code into one assembly, target/classes/SqlTable.dll
;;   uber     a self-contained directory target/sqltable: cleat, the app and its
;;            NuGet packages (SQLite's native library included) and a launcher named after the directory:
;;              target/sqltable/sqltable data/people.csv "select * from people"
;;   clean
(ns build
  (:refer-clojure :exclude [compile test])
  (:require [clojure.tools.build.api :as b]))

(def class-dir "target/classes")

(defn clean [_]
  (b/delete {:path "target"}))

(defn test [_]
  (let [{:keys [exit]} (b/process (b/cleat-command {:basis (b/create-basis {:aliases [:test]})
                                                     :main 'sqltable.test-runner}))]
    (when-not (zero? exit)
      (throw (ex-info "tests failed" {:exit exit})))))

(defn compile [_]
  (b/compile-clj {:basis (b/create-basis) :class-dir class-dir :src-dirs ["src"] :assembly "SqlTable"}))

(defn uber [_]
  (clean nil)
  (compile nil)
  (b/uber {:basis (b/create-basis) :class-dir class-dir :uber-dir "target/sqltable" :main 'sqltable.main}))
