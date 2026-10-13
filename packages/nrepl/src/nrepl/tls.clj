; Original code from https://github.com/aphyr/less-awful-ssl/ :
; Copyright © 2013 Kyle Kingsbury (aphyr@aphyr.com)
; Distributed under the Eclipse Public License, the same as Clojure.

; 2022 Added TLSv1.3, Elliptic Curve support and misc. string utils by Ivar Refsdal (refsdal.ivar@gmail.com)

(ns nrepl.tls
  "TLS for nREPL servers and clients: both sides present a certificate, and
  each accepts only a peer whose certificate the CA in the key material
  issued."
  {:added "1.1"}
  (:require [clojure.string :as str])
  (:import (System.IO IOException)
           (System.Net IPAddress IPEndPoint)
           (System.Net.Security RemoteCertificateValidationCallback
                               SslClientAuthenticationOptions SslPolicyErrors
                               SslServerAuthenticationOptions SslStream)
           (System.Net.Sockets AddressFamily NetworkStream ProtocolType Socket
                               SocketOptionLevel SocketOptionName SocketType)
           (System.Security.Authentication AuthenticationException SslProtocols)
           (System.Security.Cryptography.X509Certificates X509Certificate X509Certificate2
                                                         X509Certificate2Collection X509CertificateLoader X509Chain
                                                         X509ChainTrustMode X509ContentType
                                                         X509RevocationMode)))

;; cleat: ported from the JVM's KeyStore/SSLContext to .NET's SslStream and
;; X509Certificate2. The key material has the same format (PEM: the CA
;; certificate, the own certificate and an unencrypted PKCS#8 private key) and
;; the checks are the same: TLS 1.3 only, a client certificate is required,
;; peers are verified against the CA alone (no host name check, as on the JVM),
;; and a client presenting the server's own certificate is turned away.
;; SSLException becomes System.Security.Authentication.AuthenticationException.
;; EdDSA keys, which Java 15+ reads, aren't supported by .NET.

(def ^:private key-material-description
  (str "The TLS keys material must contain the CA certificate, your own "
       "certificate and an unencrypted PKCS#8 private key (a "
       "`-----BEGIN PRIVATE KEY-----` block) in PEM format."))

(defn- get-parts [s begin? end?]
  (when (string? s)
    (loop [res []
           curr []
           consume? false
           [lin & rst :as lines] (str/split-lines s)]
      (cond (empty? lines)
            res

            (begin? lin)
            (recur res (conj curr lin) true rst)

            (end? lin)
            (recur (conj res (str/join "\n" (conj curr lin))) [] false rst)

            consume?
            (recur res (conj curr lin) true rst)

            (false? consume?)
            (recur res curr false rst)))))

(def ^:private private-key-block-pattern
  "Matches a PEM private-key block, capturing its label (e.g. \"PRIVATE KEY\"
  or \"EC PRIVATE KEY\") and its base64 payload. Tolerates surrounding
  horizontal whitespace, like the certificate extraction does."
  #"(?ms)^[ \t]*-----BEGIN ((?:[A-Z0-9]+ +)*PRIVATE KEY)-----[ \t]*$(.+?)^[ \t]*-----END \1-----[ \t]*$")

(defn- str->private-key [s]
  (let [blocks (when (string? s)
                 (re-seq private-key-block-pattern s))
        by-label (group-by second blocks)
        [pkcs8-block] (first (by-label "PRIVATE KEY"))
        legacy-label (->> (keys by-label)
                          (remove #{"PRIVATE KEY" "ENCRYPTED PRIVATE KEY"})
                          first)]
    (cond
      ;; The block itself: X509Certificate2/CreateFromPem reads it, whether it's
      ;; an RSA, EC or DSA key.
      pkcs8-block
      pkcs8-block

      (by-label "ENCRYPTED PRIVATE KEY")
      (throw (ex-info (str "The private key is password-protected, which is not supported. "
                           "Decrypt it first, e.g. `openssl pkey -in encrypted-key.pem -out key.pem`.")
                      {}))

      legacy-label
      (throw (ex-info (str "The private key is in the " legacy-label " format, but only "
                           "unencrypted PKCS#8 keys are supported. Convert it with "
                           (if (= legacy-label "OPENSSH PRIVATE KEY")
                             "`ssh-keygen -p -m PKCS8 -f key.pem` (which rewrites the file in place)."
                             "`openssl pkcs8 -topk8 -nocrypt -in key.pem -out key-pkcs8.pem`."))
                      {:label legacy-label}))

      (and (string? s) (str/includes? s "PRIVATE KEY"))
      (throw (ex-info (str "Found what looks like a private key, but its PEM structure could "
                           "not be parsed. Check that the BEGIN and END labels match and that "
                           "the block is intact.")
                      {}))

      :else
      (throw (ex-info (str "No private key found. " key-material-description)
                      {})))))

(defn- get-certs [cert-str]
  (get-parts cert-str
             (fn [s] (= (str/trim s) "-----BEGIN CERTIFICATE-----"))
             (fn [s] (= (str/trim s) "-----END CERTIFICATE-----"))))

(defn- parse-certificate
  "Parses a single X.509 certificate from a PEM string. (X509Certificate2's
  CreateFromPem takes a ReadOnlySpan, which cleat's interop can't pass, so the
  PEM is decoded here.)"
  ^X509Certificate2 [^String pem]
  (let [body (->> (str/split-lines pem)
                  (map str/trim)
                  (remove #(str/starts-with? % "-----"))
                  (apply str))]
    (X509CertificateLoader/LoadCertificate (Convert/FromBase64String body))))

(defn- verify-chain
  "True when `cert` chains up to `ca`, the only trusted root. Revocation isn't
  checked, as on the JVM."
  [^X509Certificate2 cert ^X509Certificate2 ca extra]
  (with-open [chain (X509Chain.)]
    (let [policy (.ChainPolicy chain)]
      (.set_TrustMode policy X509ChainTrustMode/CustomRootTrust)
      (.set_RevocationMode policy X509RevocationMode/NoCheck)
      (.Add (.CustomTrustStore policy) ca)
      (doseq [c extra] (.Add (.ExtraStore policy) ^X509Certificate2 c))
      (boolean (.Build chain cert)))))

(defn- issued-by?
  "Returns true if `cert` was issued by `issuer`: its signature verifies
  against `issuer`'s key."
  [^X509Certificate2 cert ^X509Certificate2 issuer]
  (try
    (and (= (.Issuer cert) (.Subject issuer))
         (verify-chain cert issuer nil))
    (catch Exception _
      false)))

(defn- ca-and-chain
  "Splits the PEM certificate blocks into the trust root (CA) and the own
  certificate chain. The documented layout is CA certificate first, own
  certificate second; for that common two-certificate case a swapped order
  is detected (by checking which certificate was issued by the other) and
  handled transparently. With three or more blocks the documented layout is
  assumed and the extra blocks are ignored (as they always have been)."
  [cert-pems]
  (let [n (count cert-pems)]
    (case n
      0 (throw (ex-info (str "No certificates found. " key-material-description)
                        {}))
      1 (throw (ex-info (str "Only one certificate found, but the TLS keys material must contain "
                             "both the CA certificate and your own certificate.")
                        {}))
      (let [[pa pb] (take 2 cert-pems)
            [a b] (map parse-certificate [pa pb])]
        (if (and (= 2 n) (issued-by? a b) (not (issued-by? b a)))
          {:ca b :chain [a] :own-pem pa}
          {:ca a :chain [b] :own-pem pb})))))

(defn- create-from-pem
  "X509Certificate2/CreateFromPem for strings: that one takes ReadOnlySpans,
  which cleat's interop can't pass, so the PEMs go through CreateFromPemFile,
  in files in a temporary directory only the user can read (CreateTempSubdirectory
  makes it 0700 on Unix), deleted right after."
  ^X509Certificate2 [^String cert-pem ^String key-pem]
  (let [dir (.FullName (System.IO.Directory/CreateTempSubdirectory "nrepl-tls-"))]
    (try
      (let [cert-file (System.IO.Path/Combine dir "cert.pem")
            key-file (System.IO.Path/Combine dir "key.pem")]
        (spit key-file key-pem)
        (spit cert-file cert-pem)
        (X509Certificate2/CreateFromPemFile cert-file key-file))
      (finally
        (System.IO.Directory/Delete dir true)))))

(defn- with-key
  "The certificate in own-pem paired with the private key in key-pem, usable
  for authenticating a TLS endpoint. On Windows SChannel needs the key to be
  persisted, so the pair goes through a PKCS#12 round trip there."
  ^X509Certificate2 [^String own-pem ^String key-pem]
  (let [paired (try
                 (create-from-pem own-pem key-pem)
                 (catch Exception e
                   (throw (ex-info (str "Could not parse the private key, or it does not belong to "
                                        "your certificate: " (.Message e)
                                        " (RSA, EC and DSA keys are supported; EdDSA keys are not "
                                        "supported on .NET.)")
                                   {:unavailable ["EdDSA"]}
                                   e))))]
    (if (System.OperatingSystem/IsWindows)
      (X509Certificate2. (.Export paired X509ContentType/Pkcs12))
      paired)))

(defn- ssl-str-context
  "Given a string containing a PKCS8 private key, a certificate and a trusted
  CA certificate used to verify peers, returns a TLS context: a map of the
  :ca and the own :cert with its private key."
  [tls-keys-str]
  (let [key-pem (str->private-key tls-keys-str)
        {:keys [ca own-pem]} (ca-and-chain (get-certs tls-keys-str))]
    {:ca ca
     :cert (with-key own-pem key-pem)
     :chain []}))

(defn ssl-context-or-throw
  "Create a SSL/TLS context from either a string or a file containing two certificates and a private key.
  Throws an exception if the SSL/TLS context could not be created."
  [tls-keys-str tls-keys-file]
  (cond
    (and (some? tls-keys-file) (not (System.IO.File/Exists ^String (str tls-keys-file))))
    (throw (ex-info (str ":tls-keys-file specified as " tls-keys-file ", but the file was not found.")
                    {:nrepl/kind :nrepl.server/invalid-start-request}))

    (some? tls-keys-file)
    (try
      (ssl-str-context (slurp (str tls-keys-file)))
      (catch Exception e
        (throw (ex-info (str "Could not create TLS context from file " tls-keys-file
                             ". Error message: " (.Message e))
                        {:nrepl/kind :nrepl.server/invalid-start-request}
                        e))))

    (string? tls-keys-str)
    (try
      (ssl-str-context tls-keys-str)
      (catch Exception e
        (throw (ex-info (str "Could not create TLS context from string. "
                             "Error message: " (.Message e))
                        {:nrepl/kind :nrepl.server/invalid-start-request}
                        e))))

    :else
    (throw (ex-info "Could not create TLS context. Neither :tls-keys-str nor :tls-keys-file given."
                    {:nrepl/kind :nrepl.server/invalid-start-request}))))

(def enabled-protocols
  "The protocols we support."
  SslProtocols/Tls13)

(defn- validator
  "A certificate validation callback accepting only peers whose certificate
  chains up to the context's CA."
  ^RemoteCertificateValidationCallback [{:keys [ca]}]
  (gen-delegate RemoteCertificateValidationCallback [sender certificate chain errors]
    (boolean
     (and certificate
          (verify-chain (X509Certificate2. ^X509Certificate certificate) ca
                        (when chain
                          (map #(.Certificate %) (.ChainElements ^X509Chain chain))))))))

(defn- client-certificates [{:keys [cert chain]}]
  (let [coll (X509Certificate2Collection.)]
    (.Add coll ^X509Certificate2 cert)
    (doseq [c chain] (.Add coll ^X509Certificate2 c))
    coll))

(deftype TlsServerSocket [^Socket socket context]
  System.IDisposable
  (Dispose [_] (.Dispose socket)))

(deftype TlsConnection [^Socket socket ^SslStream stream]
  System.IDisposable
  (Dispose [_]
    (try (.Dispose stream) (catch Exception _))
    (.Dispose socket)))

(defn resolve-address
  "The IP address host names: an address literal, or the first address a
  host name resolves to."
  ^IPAddress [^String host]
  (or (try (IPAddress/Parse host) (catch FormatException _ nil))
      (first (System.Net.Dns/GetHostAddresses host))
      (throw (IOException. (str "Unknown host: " host)))))

(defn server-socket
  "Given a TLS context, makes a server socket whose connections speak TLS."
  [context ^String host port]
  (let [addr (resolve-address host)
        sock (Socket. (.AddressFamily addr) SocketType/Stream ProtocolType/Tcp)]
    (.SetSocketOption sock SocketOptionLevel/Socket SocketOptionName/ReuseAddress true)
    (.Bind sock (IPEndPoint. addr (int port)))
    (.Listen sock 50)
    (TlsServerSocket. sock context)))

(defn socket
  "Given a TLS context, connects to host:port and returns a TLS connection."
  [context ^String host port connect-timeout-ms]
  (let [addr (resolve-address host)
        sock (Socket. (.AddressFamily addr) SocketType/Stream ProtocolType/Tcp)]
    (try
      (let [connecting (.ConnectAsync sock (IPEndPoint. addr (int port)))]
        (when-not (.Wait connecting (int connect-timeout-ms))
          (throw (IOException. (str "Timed out connecting to " host ":" port)))))
      (let [stream (SslStream. (NetworkStream. sock false) false)
            options (doto (SslClientAuthenticationOptions.)
                      (.set_TargetHost host)
                      (.set_ClientCertificates (client-certificates context))
                      (.set_EnabledSslProtocols enabled-protocols)
                      (.set_CertificateRevocationCheckMode
                       System.Security.Cryptography.X509Certificates.X509RevocationMode/NoCheck)
                      (.set_RemoteCertificateValidationCallback (validator context)))]
        (.AuthenticateAsClient stream options)
        (TlsConnection. sock stream))
      (catch Exception e
        (.Dispose sock)
        (throw (if (instance? AuthenticationException e)
                 e
                 (AuthenticationException. (.Message e) e)))))))

(def ^:dynamic *handshake-timeout-ms*
  "Number of milliseconds to wait for the TLS handshake to complete."
  30000)

(defn accept
  "Accepts a new TLS connection. Waits `*handshake-timeout-ms*` (default: 30 000) milliseconds for the TLS handshake
  to complete. Requires that the client certificate is different from the server certificate."
  [^TlsServerSocket server]
  (let [{:keys [^X509Certificate2 cert] :as context} (.context server)
        ;; Accept failing means the server socket is closed; the server's
        ;; accept loop tells that apart from a handshake that failed.
        ^Socket sock (.Accept ^Socket (.socket server))]
    (try
      (.set_ReceiveTimeout sock (int *handshake-timeout-ms*))
      (.set_SendTimeout sock (int *handshake-timeout-ms*))
      (let [stream (SslStream. (NetworkStream. sock false) false)
            options (doto (SslServerAuthenticationOptions.)
                      (.set_ServerCertificate cert)
                      (.set_ClientCertificateRequired true)
                      (.set_EnabledSslProtocols enabled-protocols)
                      (.set_CertificateRevocationCheckMode X509RevocationMode/NoCheck)
                      (.set_RemoteCertificateValidationCallback (validator context)))]
        (try
          (.AuthenticateAsServer stream options)
          (catch IOException e
            (throw (if (re-find #"(?i)time" (str (.Message e) (some-> (.InnerException e) .Message)))
                     (AuthenticationException. "TLS handshake timed out" e)
                     e))))
        (when (= (some-> (.RemoteCertificate stream) .GetCertHashString) (.GetCertHashString cert))
          (throw (AuthenticationException. "Cannot use same keys as server")))
        (.set_ReceiveTimeout sock 0)
        (.set_SendTimeout sock 0)
        (TlsConnection. sock stream))
      (catch Exception t
        (try (.Dispose sock) (catch Exception _))
        ;; Wrap other exceptions in AuthenticationException to make the server
        ;; accept loop continue.
        (throw (if (instance? AuthenticationException t)
                 t
                 (AuthenticationException. (.Message t) t)))))))
