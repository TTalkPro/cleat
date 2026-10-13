(ns nrepl.edn-test
  (:require [clojure.test :refer [deftest is testing]]
            [nrepl.core :as nrepl]
            [nrepl.server :as server]
            [nrepl.test-helpers :as th :refer [eval-value1]]
            [nrepl.transport :as transport]))

(deftest edn-transport-communication
  (with-open [server (server/start-server :transport-fn transport/edn)
              conn (nrepl/connect :transport-fn transport/edn
                                  :port (:port server))]
    (let [client (nrepl/client conn 1000)]
      (testing "op as a string value"
        (is (= 5 (eval-value1 client '(+ 2 3)))))
      (testing "op as a keyword value"
        (is (= [5] (nrepl/response-values
                    (nrepl/message client {:op :eval :code "(+ 2 3)"})))))
      (testing "simple expressions"
        (is (= (eval '(range 40)) (eval-value1 client '(range 40))))))))

;; cleat: Unix domain sockets are part of the BCL on every OS .NET supports.
(deftest edn-transport-unix-socket
  (let [tmpdir (th/short-temp-dir "edn-socket-test-")
        sock-path (System.IO.Path/Combine tmpdir "socket")]
    (try
      (with-open [server (server/start-server :transport-fn transport/edn
                                              :socket sock-path)
                  conn (nrepl/connect :transport-fn transport/edn
                                      :socket sock-path)]
        (let [client (nrepl/client conn 1000)]
          (testing "basic eval over Unix socket"
            (is (= 5 (eval-value1 client '(+ 2 3)))))
          (testing "expressions returning collections"
            (is (= (range 40) (eval-value1 client '(range 40)))))
          (testing "multiple sequential evaluations"
            (is (= 10 (eval-value1 client '(* 2 5)))))))
      (finally
        (System.IO.Directory/Delete tmpdir true)))))
