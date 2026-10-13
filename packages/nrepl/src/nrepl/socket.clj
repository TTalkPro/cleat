(ns nrepl.socket
  "Sockets for nREPL servers and clients: TCP (plain or TLS) and filesystem
  (Unix domain) sockets, behind one set of protocols."
  (:require
   [clojure.string :as str]
   [nrepl.misc :refer [log]]
   [nrepl.tls :as tls])
  (:import
   (System.IO BufferedStream Stream)
   (System.Net IPAddress IPEndPoint)
   (System.Net.Sockets AddressFamily NetworkStream ProtocolType Socket
                       SocketOptionLevel SocketOptionName SocketType
                       UnixDomainSocketEndPoint)))

;; cleat: the ns form is compiled as a whole, so classes that a required
;; namespace defines get imported after it.
(import '(nrepl.tls TlsConnection TlsServerSocket))

;; cleat: on the JVM, TCP sockets are java.net sockets and filesystem sockets
;; java.nio channels (JDK 17+ or junixsocket), which can't share a stream
;; interface, hence the compatibility layer there. On the CLR,
;; System.Net.Sockets.Socket does both (UnixDomainSocketEndPoint on every
;; supported OS, Windows 10+ included), so a server socket is a listening
;; Socket, a connection is a Socket, and TLS wraps them in nrepl.tls's
;; TlsServerSocket and TlsConnection. `as-nrepl-uri` returns the URI as a
;; string.

;;; InetSockets (TCP)

(defn inet-socket
  ([bind port]
   (let [port (or port 0)
         ;; We fallback to 127.0.0.1 instead of to localhost to avoid
         ;; a dependency on the order of ipv4 and ipv6 records for
         ;; localhost in /etc/hosts
         bind (or bind "127.0.0.1")
         addr (tls/resolve-address bind)]
     (doto (Socket. (.AddressFamily addr) SocketType/Stream ProtocolType/Tcp)
       (.SetSocketOption SocketOptionLevel/Socket SocketOptionName/ReuseAddress true)
       (.Bind (IPEndPoint. addr (int port)))
       (.Listen 50))))
  ([bind port tls-context]
   (let [port (or port 0)
         ;; We fallback to 127.0.0.1 instead of to localhost to avoid
         ;; a dependency on the order of ipv4 and ipv6 records for
         ;; localhost in /etc/hosts
         bind (or bind "127.0.0.1")]
     (tls/server-socket tls-context bind port))))

;; Unix domain sockets

(defonce ^:private files-to-delete (atom #{}))

(defonce ^:private sigterm-registration (atom nil))

(defn exit-on-sigterm!
  "Has SIGTERM end the process through Environment/Exit, which raises the
  ProcessExit event. A .NET console program otherwise ends on SIGTERM without
  it, where the JVM runs its shutdown hooks (and so deleteOnExit)."
  []
  (when-not @sigterm-registration
    (reset! sigterm-registration
            (System.Runtime.InteropServices.PosixSignalRegistration/Create
             System.Runtime.InteropServices.PosixSignal/SIGTERM
             (gen-delegate |System.Action`1[System.Runtime.InteropServices.PosixSignalContext]| [ctx]
               (.set_Cancel ^System.Runtime.InteropServices.PosixSignalContext ctx true)
               (Environment/Exit 143))))))

(defonce ^:private delete-on-exit
  (delay
    (exit-on-sigterm!)
    (.add_ProcessExit System.AppDomain/CurrentDomain
                      (gen-delegate System.EventHandler [_ _]
                        (doseq [f @files-to-delete]
                          (try (System.IO.File/Delete f) (catch Exception _)))))))

(defn delete-on-exit!
  "Has the file at path deleted when the process exits, like
  java.io.File.deleteOnExit, SIGTERM included (see exit-on-sigterm!)."
  [path]
  @delete-on-exit
  (swap! files-to-delete conj (str path)))

(defn unix-server-socket
  "Returns a server filesystem socket bound to the path."
  ^Socket [^String path]
  (let [socket (Socket. AddressFamily/Unix SocketType/Stream ProtocolType/Unspecified)]
    (try
      (.Bind socket (UnixDomainSocketEndPoint. path))
      (.Listen socket 50)
      (catch Exception e
        (.Dispose socket)
        (throw e)))
    (delete-on-exit! (System.IO.Path/GetFullPath path))
    socket))

(defn unix-client-socket
  "Returns a client filesystem socket connected to the path."
  ^Socket [^String path]
  (let [socket (Socket. AddressFamily/Unix SocketType/Stream ProtocolType/Unspecified)]
    (try
      (.Connect socket (UnixDomainSocketEndPoint. path))
      socket
      (catch Exception e
        (.Dispose socket)
        (throw e)))))

(defn- unix-socket? [^Socket s]
  (= AddressFamily/Unix (.AddressFamily s)))

(defn- get-socket-address-path
  "Given a Unix domain socket endpoint, return its filesystem path as an
  absolute path string."
  [^UnixDomainSocketEndPoint addr]
  (System.IO.Path/GetFullPath (str addr)))

;;;; Common parts.

(defn- encode-uri-path
  "path with the characters a URI path can't hold percent-encoded, the way
  java.net.URI's multi-argument constructors quote them."
  [^String path]
  (apply str (for [b (.GetBytes System.Text.Encoding/UTF8 path)
                   :let [c (char b)]]
               (if (or (Char/IsLetterOrDigit c) (<= 0 (.IndexOf "/-._~!$&'()*+,;=:@" c)))
                 (if (< (int b) 128) c (format "%%%02X" (int b)))
                 (format "%%%02X" (int b))))))

(defn as-nrepl-uri
  "Constructs an nREPL URI from a server map and transport scheme.
  Takes a map with :server-socket and :host keys (as in nrepl.server/Server).
  Returns the URI as a string."
  ^String [{:keys [host server-socket]} transport-scheme]
  (if (and (instance? Socket server-socket) (unix-socket? server-socket))
    (str transport-scheme "+unix:"
         (encode-uri-path (get-socket-address-path (.LocalEndPoint ^Socket server-socket))))
    (let [^Socket sock (if (instance? TlsServerSocket server-socket)
                         (.socket ^TlsServerSocket server-socket)
                         server-socket)]
      (str transport-scheme
           (when (instance? TlsServerSocket server-socket) "s")
           "://"
           (if (str/includes? (str host) ":") (str "[" host "]") host)
           ":"
           (.Port ^IPEndPoint (.LocalEndPoint sock))))))

(defn local-port
  "The TCP port a server socket listens on."
  [server-socket]
  (let [^Socket sock (if (instance? TlsServerSocket server-socket)
                       (.socket ^TlsServerSocket server-socket)
                       server-socket)]
    (.Port ^IPEndPoint (.LocalEndPoint sock))))

(defprotocol Acceptable
  (accept [s]
    "Accepts a connection on s. Throws ObjectDisposedException if s is
    closed."))

(extend-protocol Acceptable
  TlsServerSocket
  (accept [s] (tls/accept s))

  Socket
  (accept [s] (.Accept s)))

(defprotocol Connectable
  (is-connected? [s]
    "Returns true if socket-like thing `s` is currently connected."))

(extend-protocol Connectable
  Socket
  (is-connected? [s] (.Connected s))

  TlsConnection
  (is-connected? [s] (.Connected ^Socket (.socket s))))

(defprotocol Writable
  (write
    [w byte-array]
    [w byte-array offset length]
    "Writes the given bytes to the output as per Stream.Write."))

(extend-protocol Writable
  Stream
  (write
    ([s byte-array] (.Write ^Stream s ^bytes byte-array 0 (alength ^bytes byte-array)))
    ([s byte-array offset length]
     (.Write ^Stream s ^bytes byte-array (int offset) (int length)))))

;; Reading and writing go through separate buffered streams over the same
;; network stream: a NetworkStream (and an SslStream) takes one reader and one
;; writer at a time, but a single BufferedStream doesn't.
;;
;; A BufferedStream is fine for bencode, which reads a byte at a time or exactly
;; the bytes a netstring has left. But once BufferedStream.Read has copied part
;; of a request out of its buffer, it blocks reading the network for the rest,
;; so a StreamReader on top of it (asking for its own buffer's worth) stalls in
;; the middle of a message. Text transports read `input-stream` instead, which
;; isn't buffered: the StreamReader is.

(defprotocol AsInputStream
  (input-stream [x]
    "Returns an unbuffered stream reading from x."))

(extend-protocol AsInputStream
  Socket (input-stream [s] (NetworkStream. s false))
  TlsConnection (input-stream [s] (.stream s))
  Stream (input-stream [s] s))

(defprotocol AsBufferedInputStreamSubset
  (buffered-input [x]
    "Returns a buffered stream reading from x."))

(extend-protocol AsBufferedInputStreamSubset
  Socket (buffered-input [s] (BufferedStream. (NetworkStream. s false)))
  TlsConnection (buffered-input [s] (BufferedStream. ^Stream (.stream s)))
  BufferedStream (buffered-input [s] s)
  Stream (buffered-input [s] (BufferedStream. s)))

(defprotocol AsBufferedOutputStreamSubset
  (buffered-output [x]
    "Returns a buffered stream writing to x."))

(extend-protocol AsBufferedOutputStreamSubset
  Socket (buffered-output [s] (BufferedStream. (NetworkStream. s false)))
  TlsConnection (buffered-output [s] (BufferedStream. ^Stream (.stream s)))
  BufferedStream (buffered-output [s] s)
  Stream (buffered-output [s] (BufferedStream. s)))
