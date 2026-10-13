(ns nrepl.tls-client-proxy
  "Proxy a local regular TCP connection to a remote TLS TCP connection.
  Can be used if your editor/IDE does not (yet) support TLS connections.

  Example usage:
  $ cleat -I packages/nrepl/src -e \"(require 'nrepl.tls-client-proxy) (nrepl.tls-client-proxy/start-tls-proxy {:remote-host \\\"localhost\\\" :remote-port 4001 :port 9001 :tls-keys-file \\\"client.keys\\\"})\"

  This will start a standalone program that will forward local TCP connections on 127.0.0.1:9001
  to the remote TLS TCP connection at 127.0.0.1:4001 using the key provided in the file `client.keys`."
  {:added "1.1"}
  (:require [nrepl.cmdline :as cmdline]
            [nrepl.socket :as socket]
            [nrepl.tls :as tls])
  (:import (System IDisposable)
           (System.IO IOException Stream)
           (System.Net.Sockets NetworkStream Socket)
           (System.Threading Thread)))

;; cleat: ported to the CLR. Connections are System.Net.Sockets sockets and
;; nrepl.tls's TlsConnection; the pumps copy in chunks rather than byte by
;; byte, flushing each chunk as the JVM version flushes when no more input is
;; available; uncaught exceptions on the proxy's threads are logged by the
;; threads themselves (the CLR has no default uncaught exception handler to
;; set).

(defonce ^:private lock (Object.))

(defn- atomic-println [& args]
  (locking lock
    (apply println args)))

(defn- now []
  (.ToString System.DateTimeOffset/Now "yyyy-MM-dd HH:mm:ss zzz"))

(defn- info [& args]
  (apply atomic-println
         (into [(now)
                (str "[" (.Name (Thread/CurrentThread)) "]")
                "INFO"]
               args)))

(defn- warn [& args]
  (apply atomic-println
         (into [(now)
                (str "[" (.Name (Thread/CurrentThread)) "]")
                "WARN"]
               args)))

(defn- error [& args]
  (binding [*out* *err*]
    (apply atomic-println
           (into [(now)
                  (str "[" (.Name (Thread/CurrentThread)) "]")
                  "ERROR"]
                 args))))

(defn- close [s]
  (when s
    (try
      (.Dispose ^IDisposable s)
      (catch Exception _
        nil))))

(defn- running? [state]
  (when state
    (:running? @state)))

(defn- accept [state ^Socket server]
  (try
    (.Accept server)
    (catch Exception e
      (when (running? state)
        (error "Error during .accept:" (.Message e)))
      nil)))

(defmacro socket-thread [state sock other-sock f]
  `(let [state# ~state]
     (future
       (try
         (swap! state# update :threads (fnil conj #{}) (Thread/CurrentThread))
         (let [sock# ~sock
               other-sock# ~other-sock
               f# ~f]
           (try
             (swap! state# update :sockets (fnil conj #{}) sock#)
             (f# sock#)
             (finally
               (close sock#)
               (close other-sock#)
               (swap! state# update :sockets (fnil disj #{}) sock#))))
         (catch Exception t#
           (error "Unhandled exception:" (.Message t#))
           (swap! state# update :unhandled-exceptions (fnil conj #{}) t#))
         (finally
           (swap! state# update :threads (fnil disj #{}) (Thread/CurrentThread)))))))

(defn- stream-of ^Stream [s]
  (if (instance? nrepl.tls.TlsConnection s)
    (.stream ^nrepl.tls.TlsConnection s)
    (NetworkStream. ^Socket s false)))

(defn- read-socket [state ^Stream inp ^bytes buffer]
  (try
    (let [n (.Read inp buffer 0 (alength buffer))]
      (when (pos? n)
        n))
    (catch Exception e
      (when (running? state)
        (warn "Exception while reading from socket:" (.Message e)))
      nil)))

(defn- write-socket [state ^Stream out ^bytes buffer n]
  (try
    (.Write out buffer 0 (int n))
    (.Flush out)
    true
    (catch Exception e
      (when (running? state)
        (warn "Exception while writing to socket:" (.Message e)))
      nil)))

(defn- pump! [state src dst]
  (try
    (let [inp (stream-of src)
          out (stream-of dst)
          buffer (byte-array 8192)]
      (loop []
        (when (running? state)
          (when-let [n (read-socket state inp buffer)]
            (when (write-socket state out buffer n)
              (recur))))))
    (catch Exception e
      (if (running? state)
        (throw e)
        nil))))

(defn stop!
  "Stops all threads and closes all sockets associated with this TLS proxy. Waits for threads to exit."
  [state]
  (swap! state assoc :running? false)
  (while (not-empty (:threads @state))
    (doseq [sock (:sockets @state)]
      (close sock))
    (Thread/Sleep 100)))

(defn- proxy-connection [{:keys [tls-context remote-host remote-port connect-timeout-ms]}
                         state sock]
  (when-let [tls-sock (try
                        (tls/socket tls-context remote-host remote-port connect-timeout-ms)
                        (catch Exception e
                          (error "Could not connect to remote"
                                 (str remote-host ":" remote-port)
                                 ". Error message: " (.Message e))
                          (close sock)
                          nil))]
    (socket-thread
     state
     sock
     tls-sock
     (fn [_]
       (socket-thread state tls-sock sock (fn [_] (pump! state tls-sock sock)))
       (pump! state sock tls-sock)))))

(defn- run-server [cfg state server-socket]
  (while (running? state)
    (when-let [sock (accept state server-socket)]
      (proxy-connection cfg state sock))))

(defn- err-exit [block? msg]
  (error msg)
  (if (true? block?)
    (do
      (shutdown-agents)
      (Environment/Exit 1))
    (throw (ex-info msg {}))))

(defn start-tls-proxy
  "Start a local TLS proxy. This will forward a local regular TCP connection to a remote TLS TCP connection.

  Required options:

  * :remote-host - The host that is hosting a TLS nREPL server
  * :remote-port - The port of the remote host that is hosting a TLS nREPL server
  * One of:
    :tls-keys-file - The TLS certificates and key to use is located in this file
    :tls-keys-str - The TLS certificates and key to use in the form of a string

  Other configuration options include:
  * :port — defaults to 0, which autoselects an open port. The port number will be written to :port-file
  * :bind - bind address, by default \"127.0.0.1\"
  * :port-file - where to write the port number. Defaults to \".nrepl-tls-proxy-port\"
  * :connect-timeout-ms - Connection timeout in milliseconds. Defaults to 10000.
  * :block? - Whether to block or not in this function. Defaults to true. For REPL usage/testing you'll want to set this to false.

  Return value:
  If :block? is false, this function will return a state atom. You can stop this server with `(stop! state)`.
  If :block? is true, i.e. the default value, this function will not return (and exit) before the end user presses Ctrl-C."
  [{:keys [remote-host remote-port tls-keys-str tls-keys-file bind port port-file block? state connect-timeout-ms]
    :or   {bind               "127.0.0.1"
           port               0
           port-file          ".nrepl-tls-proxy-port"
           connect-timeout-ms 10000
           block?             true
           state              (atom {})}}]
  (stop! state)
  (swap! state assoc :running? true)
  (when (empty? remote-host) (err-exit block? ":remote-host not specified"))
  (when (nil? remote-port) (err-exit block? ":remote-port not specified"))
  (let [port-promise (promise)
        block (promise)
        cfg {:remote-host        remote-host
             :remote-port        remote-port
             :connect-timeout-ms connect-timeout-ms
             :tls-context        (try
                                   (tls/ssl-context-or-throw tls-keys-str tls-keys-file)
                                   (catch Exception e
                                     (err-exit block? (str "Could not create TLS context: " (.Message e)))))}]
    (swap! state assoc :port-promise port-promise)
    (socket-thread
     state
     (try
       (socket/inet-socket bind port)
       (catch Exception e
         (err-exit block? (str "Could not bind to: " bind ":" port ". Error message: " (.Message e)))))
     nil
     (fn [sock]
       (let [local-port (socket/local-port sock)]
         (deliver port-promise local-port)
         (when port-file
           (spit port-file (str local-port)))
         (info "Started TLS proxy at"
               (str bind ":" local-port ".")
               "Proxying to" (str remote-host ":" remote-port "."))
         (run-server cfg state sock))))
    (when block?
      (cmdline/set-signal-handler!
       "INT"
       (fn [_signal]
         (stop! state)
         (deliver block :done)))
      @block
      (info "TLS proxy exiting")
      (shutdown-agents))
    state))

(comment
  (do
    (defonce st (atom {}))
    (start-tls-proxy {:port          0
                      :remote-host   "127.0.0.1"
                      :remote-port   5633
                      :tls-keys-file "client.keys"
                      :state         st
                      :block?        false})))
