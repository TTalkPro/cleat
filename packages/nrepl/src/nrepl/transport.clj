(ns nrepl.transport
  "Abstractions and implementations for nREPL message transports.

  A transport handles the wire-level communication between client and
  server -- encoding, sending, and receiving messages. nREPL ships with
  bencode, EDN, and TTY transport implementations.

  Also provides helper functions for middleware authors: `respond-to`
  for sending responses and `safe-handle` for op dispatch with error
  handling."
  {:author "Chas Emerick"
   :added  "0.4"}
  (:refer-clojure :exclude [send])
  (:require
   [clojure.edn :as edn]
   [clojure.walk :as walk]
   [nrepl.bencode :as bencode]
   [nrepl.misc :refer [response-for uuid]]
   [nrepl.socket :as socket]
   [nrepl.util.queue :as q]
   [nrepl.util.threading :as threading]
   nrepl.version)
  (:import
   (clojure.lang LineNumberingTextReader PushbackInputStream PushbackTextReader)
   (System IDisposable ObjectDisposedException)
   (System.IO EndOfStreamException IOException MemoryStream Stream StreamReader StreamWriter)
   (System.Text Encoding UTF8Encoding)
   (System.Threading SemaphoreSlim Thread ThreadInterruptedException)))

;; cleat: ported to the CLR. Transports are IDisposable (Dispose takes the
;; place of java.io.Closeable's close, and with-open calls it); a lost
;; connection surfaces as a System.IO.IOException (the JVM version throws
;; java.net.SocketException); the queues come from nrepl.util.queue.

(defprotocol Transport
  "Defines the interface for a wire protocol implementation for use
   with nREPL."
  (recv [this] [this timeout]
    "Reads and returns the next message received.  Will block.
     Should return nil if a message is not available after `timeout`
     ms or if the underlying channel has been closed.")
  (send [this msg] "Sends msg. Implementations should return the transport."))

(deftype FnTransport [recv-fn send-fn close]
  Transport
  (send [this msg] (send-fn msg) this)
  (recv [this] (recv this Int64/MaxValue))
  (recv [_this timeout] (recv-fn timeout))
  IDisposable
  (Dispose [_this] (close)))

(defn fn-transport
  "Returns a Transport implementation that delegates its functionality
   to the 2 or 3 functions provided."
  ([transport-read write] (fn-transport transport-read write nil))
  ([transport-read write close]
   (let [read-queue (q/linked-queue)
         stopped (atom false)
         ^Thread reader (threading/run-with @threading/transport-executor
                          (loop []
                            (when-not @stopped
                              (let [msg (try
                                          (transport-read)
                                          (catch ThreadInterruptedException _
                                            ;; Stopped: end the loop.
                                            (reset! stopped true)
                                            nil)
                                          (catch Exception t
                                            (reset! stopped true)
                                            t))]
                                (q/put! read-queue msg)
                                (recur)))))
         cancel (fn []
                  (reset! stopped true)
                  (.Interrupt reader))]
     (FnTransport.
      (let [failure (atom nil)]
        #(if @failure
           (throw @failure)
           (let [msg (q/poll! read-queue %)]
             (if (instance? Exception msg)
               (do (reset! failure msg)
                   (cancel)
                   (throw msg))
               msg))))
      write
      (fn [] (when close (close)) (cancel))))))

(def ^:private byte-array-class (class (byte-array 0)))

(defmulti ^:private <bytes class)

(defmethod <bytes :default
  [input]
  input)

(defmethod <bytes byte-array-class
  [input]
  (.GetString Encoding/UTF8 ^bytes input))

(defmethod <bytes clojure.lang.IPersistentVector
  [input]
  (vec (map <bytes input)))

(defmethod <bytes clojure.lang.IPersistentMap
  [input]
  (->> input
       (map (fn [[k v]] [k (<bytes v)]))
       (into {})))

(defn- disconnected []
  (IOException. "The transport's socket appears to have lost its connection to the nREPL server"))

(defn- disconnection?
  "Whether e, thrown reading from or writing to s, means the connection is
  gone."
  [^Exception e s]
  (or (instance? ObjectDisposedException e)
      (= "EOF while reading" (.Message e))
      (and (instance? EndOfStreamException e)
           (= "Invalid netstring. Unexpected end of input." (.Message e)))
      (and (satisfies? socket/Connectable s) (not (socket/is-connected? s)))))

(defmacro ^{:private true} rethrow-on-disconnection
  [s & body]
  `(try
     ~@body
     (catch Exception e#
       (if (disconnection? e# ~s)
         (throw (disconnected))
         (throw e#)))))

(defn ^{:private true} safe-write-bencode
  "Similar to `bencode/write-bencode`, except it will only writes to the output
   stream if the whole `thing` is writable. In practice, it avoids sending partial
    messages down the transport, which is almost always bad news for the client.

   This will still throw an exception if called with something unencodable."
  [output thing]
  (let [buffer (MemoryStream.)]
    (bencode/write-bencode buffer thing)
    (socket/write output (.ToArray buffer))))

(defn- dispose [x]
  (.Dispose ^IDisposable x))

(defn bencode
  "Returns a Transport implementation that serializes messages
   over the given Socket or Stream using bencode."
  ([s] (bencode s s s))
  ([in out & [s]]
   (let [in (PushbackInputStream. (socket/buffered-input in))
         out (socket/buffered-output out)]
     (fn-transport
      #(let [payload (rethrow-on-disconnection s (bencode/read-nrepl-message in))
             unencoded (<bytes (payload "-unencoded"))
             to-decode (apply dissoc payload "-unencoded" unencoded)]
         (walk/keywordize-keys (merge (dissoc payload "-unencoded")
                                      (when unencoded {"-unencoded" unencoded})
                                      (<bytes to-decode))))
      #(rethrow-on-disconnection s
                                 (locking out
                                   (safe-write-bencode out %)
                                   (.Flush ^Stream out)))
      (fn []
        (if s
          (dispose s)
          (do
            (dispose in)
            (dispose out))))))))

(defn edn
  "Returns a Transport implementation that serializes messages
   over the given Socket or Stream using EDN."
  {:added "0.7"}
  ([s] (edn s s s))
  ([in out & [s]]
   (let [in (PushbackTextReader. (StreamReader. ^Stream (socket/input-stream in) Encoding/UTF8))
         out (socket/buffered-output out)]
     (fn-transport
      #(rethrow-on-disconnection s (edn/read in))
      #(rethrow-on-disconnection s
                                 (locking out
                                   (binding [*print-readably* true
                                             *print-length*   nil
                                             *print-level*    nil]
                                     (socket/write out (.GetBytes Encoding/UTF8 ^String (str %)))
                                     (.Flush ^Stream out))))
      (fn []
        (if s
          (dispose s)
          (do
            (dispose in)
            (dispose out))))))))

(defn tty
  "Returns a Transport implementation suitable for serving an nREPL backend
   via simple in/out readers, as with a tty or telnet connection."
  ([s] (tty s s s))
  ([in out & [s]]
   (let [r (LineNumberingTextReader. (StreamReader. ^Stream (socket/input-stream in) Encoding/UTF8))
         w (StreamWriter. ^Stream (socket/buffered-output out) (UTF8Encoding. false))
         cns (atom "user")
         read-sync (SemaphoreSlim. 1)
         eval-ids (atom #{})
         session-id (atom nil)
         read-queue (q/linked-queue)
         read #(or (q/poll! read-queue)
                   (let [id (str "eval" (uuid))]
                     (.Wait read-sync)
                     (swap! eval-ids conj id)
                     {:op "eval", :code r, :ns @cns, :id id, :session @session-id}))
         write (fn [{:keys [out err value status ns new-session id]}]
                 (when new-session (reset! session-id new-session))
                 (when ns (reset! cns ns))
                 (doseq [^String x [out err value] :when x]
                   (.Write w x))
                 (when (and (some #{:done "done"} status) (contains? @eval-ids id))
                   (swap! eval-ids disj id)
                   (.Write w (str "\n" @cns "=> "))
                   (.Release read-sync))
                 (.Flush w))
         close (when s
                 #(do (q/put! read-queue {:op "close", :session @session-id})
                      (dispose s)))]
     (q/put! read-queue {:op "clone"})
     (fn-transport read write close))))

(defn tty-greeting
  "A greeting fn usable with `nrepl.server/start-server`,
   meant to be used in conjunction with Transports returned by the
   `tty` function.

   Usually, Clojure-aware client-side tooling would provide this upon connecting
   to the server, but telnet et al. isn't that."
  [transport]
  (send transport {:out (str ";; nREPL " (:version-string nrepl.version/version)
                             \newline
                             ";; Clojure " (clojure-version)
                             \newline
                             "user=> ")}))

(defmulti uri-scheme
  "Return the uri scheme associated with a transport var."
  identity)

(defmethod uri-scheme #'bencode [_] "nrepl")

(defmethod uri-scheme #'tty [_] "telnet")

(defmethod uri-scheme #'edn [_] "nrepl+edn")

(defmethod uri-scheme :default
  [transport]
  (printf "WARNING: No uri scheme associated with transport %s\n" transport)
  "unknown")

(deftype QueueTransport [in out]
  nrepl.transport.Transport
  (send [this msg] (q/put! out msg) this)
  (recv [_this] (q/take! in))
  (recv [_this timeout] (q/poll! in timeout)))

(defn piped-transports
  "Returns a pair of Transports that read from and write to each other."
  []
  (let [a (q/linked-queue)
        b (q/linked-queue)]
    [(QueueTransport. a b) (QueueTransport. b a)]))

(defn respond-to
  "Send a response for `msg` with `response-data` using message's transport."
  [msg & response-data]
  (send (:transport msg) (apply response-for msg response-data)))

(defmacro safe-handle
  "When given a message and a number of <op handler> pairs, invoke the handler
  with the op that matches message's op. If an exception is raised during
  handling, send an automatic error response through the message's transport
  with `:<op>-error` status. Special keyword `:else` can be used for an op to
  define a catch-all handler. Handlers should be functions of 1 argument `msg`."
  {:style/indent 1}
  [msg & body] ;; `body` is used, otherwise CIDER acts up and misindents
  (assert (even? (count body)))
  (let [msg-sym (gensym "msg")
        op-sym (gensym "op")]
    `(let [~msg-sym ~msg
           ~op-sym (:op ~msg-sym)]
       (cond ~@(mapcat (fn [[op handler]]
                         (if (= op :else)
                           [:else `(~handler ~msg-sym)]
                           [`(= ~op-sym ~op)
                            `(try (respond-to ~msg-sym (~handler ~msg-sym))
                                  (catch Exception e#
                                    (respond-to ~msg-sym
                                                :status #{~(keyword (str op "-error")) :error :done}
                                                :message (.Message e#))))]))
                       (partition 2 body))))))
