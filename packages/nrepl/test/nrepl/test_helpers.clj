(ns nrepl.test-helpers
  (:require [clojure.string :as str]
            [clojure.test :refer [is]]
            [nrepl.core :as nrepl]
            [nrepl.test-matchers :as mc])
  (:import (System.Diagnostics Process ProcessStartInfo)
           (System.Net IPAddress IPEndPoint)
           (System.Net.Sockets Socket)))

;; cleat: ported to the CLR. `sh` starts a process with System.Diagnostics, and
;; `match?` comes from nrepl.test-matchers rather than matcher-combinators.

(def sys-newline
  "The system's newline character sequence."
  System.Environment/NewLine)

(def win?
  "Whether we are running on MS-Windows."
  (System.OperatingSystem/IsWindows))

(def base-dir
  "The nrepl package's directory (packages/nrepl)."
  (-> *file* System.IO.Path/GetFullPath System.IO.Path/GetDirectoryName
      System.IO.Path/GetDirectoryName System.IO.Path/GetDirectoryName))

(defn string=
  "Like `clojure.core/=` applied on STRING1 and STRING2, but treats
  all line endings as equal."
  [string1 string2]
  (= (str/split-lines string1) (str/split-lines string2)))

(defn newline->sys
  "Returns TEXT, converting `\n` line endings to those of the underlying
  system's, if different."
  [text]
  (if-not (= sys-newline "\n")
    (str/replace text "\n" sys-newline)
    text))

(def sys-file-sep
  "The character separating components in a path."
  (str System.IO.Path/DirectorySeparatorChar))

(defn dir-sep->sys
  "Replace the `/` separator component used in tests with the one used
  by the system."
  [path]
  (if-not (= sys-file-sep "/")
    (str/replace path "/" sys-file-sep)
    path))

(def cleat-executable
  "The command running the cleat host running these tests: the apphost, or
  dotnet and cleat.dll."
  (let [path System.Environment/ProcessPath]
    (if (= "dotnet" (System.IO.Path/GetFileNameWithoutExtension path))
      [path (.Location (System.Reflection.Assembly/GetEntryAssembly))]
      [path])))

(defn cleat-command
  "The command line running cleat with the nrepl package's source on the load
  path, then args."
  [& args]
  (-> cleat-executable
      (into ["-I" (System.IO.Path/Combine ^String base-dir "src")])
      (into args)))

(defn sh
  "Starts the process cmd (a vector of strings), with its standard input, output
  and error redirected and the environment variables in env added; returns the
  Process."
  ^Process
  [cmd & [env]]
  (let [psi (ProcessStartInfo. ^String (first cmd))]
    (doseq [a (rest cmd)] (.Add (.ArgumentList psi) a))
    (doseq [[k v] env] (.set_Item (.Environment psi) (str k) (str v)))
    (.set_RedirectStandardInput psi true)
    (.set_RedirectStandardOutput psi true)
    (.set_RedirectStandardError psi true)
    (.set_UseShellExecute psi false)
    (Process/Start psi)))

(defmacro with-process [[binding cmd-vector env] & body]
  (let [sym (gensym)]
    `(let [~sym (sh ~cmd-vector ~env)
           ~binding ~sym]
       (try ~@body
            (finally (try (.Kill ~binding true) (catch Exception _#)))))))

(defmacro with-timeout
  "Runs BODY on another thread and gives up after MS milliseconds, throwing
  instead of hanging the whole test run. For code that blocks on responses
  with no timeout of its own, like the built-in client."
  [ms & body]
  `(let [f# (future ~@body)
         v# (deref f# ~ms ::timeout)]
     (if (identical? v# ::timeout)
       (do (future-cancel f#)
           (throw (ex-info (str "Timed out after " ~ms "ms") {:timeout-ms ~ms})))
       v#)))

(defn short-temp-dir
  "Creates a temporary directory whose name starts with prefix and returns
  its path: under /tmp on Unix, since a Unix domain socket's path can't be
  longer than about 100 bytes and TMPDIR may be deep."
  [prefix]
  (let [base (if (and (not win?) (System.IO.Directory/Exists "/tmp"))
               "/tmp"
               (System.IO.Path/GetTempPath))
        dir (System.IO.Path/Combine ^String base (str prefix (subs (str (System.Guid/NewGuid)) 0 8)))]
    (.FullName (System.IO.Directory/CreateDirectory dir))))

(defn free-port []
  (with-open [sock (Socket. System.Net.Sockets.SocketType/Stream System.Net.Sockets.ProtocolType/Tcp)]
    (.Bind sock (IPEndPoint. IPAddress/Loopback 0))
    (.Port ^IPEndPoint (.LocalEndPoint sock))))

;; nrepl-specific helpers

(defn eval-value1
  "Transform `code-form` into a string, send to `client-or-session` via `eval` op,
  read and combine the response and return the first successful value."
  [client-or-session code-form]
  (-> (nrepl/message client-or-session {:op "eval", :code (nrepl/code* code-form)})
      nrepl/response-values
      first))

(defn match?
  "Whether actual matches expected, matcher-combinators style."
  [expected actual]
  (mc/match? expected actual))

(defmacro is+
  "Like `is` but wraps expected value in `match?`."
  ([expected actual]
   `(is+ ~expected ~actual nil))
  ([expected actual message]
   `(is (match? ~expected ~actual) ~message)))
