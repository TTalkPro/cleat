(ns nrepl.cmdline-tty-test
  "TTY is not a full-featured transport (e.g. doesn't support ack), so are tested
   separately here."
  (:require [clojure.test :refer [deftest is testing]]
            [nrepl.cmdline :as cmd]
            [nrepl.server :as server]
            [nrepl.test-helpers :as th :refer [free-port is+ sh]]
            [nrepl.transport :as transport])
  (:import (System.IO StreamReader StreamWriter)
           (System.Net.Sockets TcpClient)))

;; cleat: ported to the CLR. A plain TcpClient stands in for commons-net's
;; TelnetClient, the server is cleat running nrepl.cmdline, the test property
;; is an environment variable, reader conditionals use :cljr and the alias is
;; clojure.clr.io's.

(defn- attempt-connection ^TcpClient [host port timeout-ms]
  (loop [n (/ timeout-ms 500)]
    (or (try
          (TcpClient. ^String host (int port))
          (catch System.Net.Sockets.SocketException ex
            (when-not (pos? n)
              (throw ex))))
        (do
          (System.Threading.Thread/Sleep 500)
          (recur (dec n))))))

(deftest ^:slow tty-server
  (let [port (free-port)
        server-process (sh (th/cleat-command "-m" "nrepl.cmdline"
                                             "--port" (str port)
                                             "--transport" "nrepl.transport/tty")
                           {"nreplacktest" "y"})]
    (try
      (with-open [c (attempt-connection "127.0.0.1" port 100000)]
        ;; A missing reply must fail the test, not hang the run.
        (.set_ReceiveTimeout c 60000)
        (let [stream (.GetStream c)
              out (doto (StreamWriter. stream) (.set_NewLine "\n"))
              br (StreamReader. stream)]
          (doseq [l ["(System.Environment/GetEnvironmentVariable \"nreplacktest\")"
                     "#?(:cljr :cljr-form)"
                     "#?(:cljs :cljs-form)"
                     "(clojure.core/require '[clojure.clr.io :as io])"
                     "::io/xyz"
                     "(clojure.core/require '[clojure.set :as sets])"
                     "{::io/x 1 ::sets/x 2}"]]
            (.WriteLine out l))
          (.Flush out)
          (is+ [#"^;; nREPL"
                #"^;; Clojure"
                "user=> \"y\""
                "user=> :cljr-form"
                "user=> nil"
                "user=> :clojure.clr.io/xyz"
                "user=> nil"
                "user=> {:clojure.clr.io/x 1, :clojure.set/x 2}"]
               (vec (repeatedly 8 #(.ReadLine br))))))
      (finally
        (try (.Kill server-process true) (catch Exception _))))))

(deftest no-tty-client
  (testing "Trying to connect with the tty transport should fail."
    (with-open [server (server/start-server :transport-fn #'transport/tty)]
      (let [options (cmd/connection-opts {:port      (:port server)
                                          :host      "localhost"
                                          :transport 'nrepl.transport/tty})]
        (is (thrown? clojure.lang.ExceptionInfo
                     (cmd/interactive-repl server options)))))))
