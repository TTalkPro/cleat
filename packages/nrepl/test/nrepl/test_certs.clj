(ns nrepl.test-certs
  "Throwaway TLS certificates for the tests: a CA, and a server and a client
  certificate it issued, as PEM strings.

  cleat: added in the CLR port, standing in for the locksmith library the JVM
  tests use. Built with .NET's CertificateRequest, on EC P-256 keys."
  (:import (System DateTimeOffset)
           (System.Security.Cryptography ECCurve ECDsa HashAlgorithmName)
           (System.Security.Cryptography.X509Certificates CertificateRequest
                                                         X509BasicConstraintsExtension
                                                         X509Certificate2
                                                         X509KeyUsageExtension
                                                         X509KeyUsageFlags)))

(defn- ec-key ^ECDsa []
  (ECDsa/Create (ECCurve/CreateFromFriendlyName "nistP256")))

(defn- request ^CertificateRequest [^String subject ^ECDsa key]
  (CertificateRequest. subject key HashAlgorithmName/SHA256))

(defn- pem [^X509Certificate2 cert]
  (str (.ExportCertificatePem cert) "\n"))

(defn- key-pem [^ECDsa key]
  (str (.ExportPkcs8PrivateKeyPem key) "\n"))

(defn gen-certs
  "Returns {:ca-cert :server-cert :server-key :client-cert :client-key}, PEM
  strings valid for duration-days days."
  [{:keys [duration-days] :or {duration-days 1}}]
  (let [not-before (.AddMinutes DateTimeOffset/UtcNow -5)
        not-after (.AddDays DateTimeOffset/UtcNow (double duration-days))
        ca-key (ec-key)
        ca-req (doto (request "CN=nREPL test CA" ca-key)
                 (-> .CertificateExtensions (.Add (X509BasicConstraintsExtension. true false 0 true)))
                 (-> .CertificateExtensions (.Add (X509KeyUsageExtension.
                                                   (enum-or X509KeyUsageFlags/KeyCertSign X509KeyUsageFlags/CrlSign)
                                                   true))))
        ca (.CreateSelfSigned ca-req not-before not-after)
        leaf (fn [subject serial]
               (let [key (ec-key)
                     req (doto (request subject key)
                           (-> .CertificateExtensions (.Add (X509BasicConstraintsExtension. false false 0 true))))
                     cert (.Create req ca not-before not-after (byte-array [0 0 0 serial]))]
                 [(pem cert) (key-pem key)]))
        [server-cert server-key] (leaf "CN=localhost" 1)
        [client-cert client-key] (leaf "CN=nREPL test client" 2)]
    {:ca-cert (pem ca)
     :server-cert server-cert
     :server-key server-key
     :client-cert client-cert
     :client-key client-key}))
