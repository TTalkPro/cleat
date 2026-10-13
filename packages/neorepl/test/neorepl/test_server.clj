(ns neorepl.test-server
  "An nREPL server and temporary directories for the tests that need them."
  (:require [clojure.string :as str]
            [neorepl.nrepl :as nrepl]
            [nrepl.middleware :as middleware]
            [nrepl.server :as server]
            [nrepl.transport :as transport])
  (:import (System.IO Directory File Path)
           (System.Net IPAddress IPEndPoint)
           (System.Net.Sockets Socket)))

;; cleat: ported to the CLR. Directories and files are path strings (`file`
;; joins them, like clojure.java.io/file), and the processes the tests start run
;; cleat with the nrepl and neorepl packages on the load path.

(def ^:dynamic *port* nil)

(def ^:private late-reads
  "The evaluations of (late-read) and (stubborn-read) that are running, by
  message id: their :session, whether they're :stubborn and the :answer
  they wait for, a line or :interrupted."
  (atom {}))

(defn- late-read
  "Answers msg, an eval, the way wrap-stuck says (late-read) and
  (stubborn-read) do."
  [{:keys [id session] :as msg} stubborn?]
  (let [answer (promise)]
    (swap! late-reads assoc id {:session session :stubborn? stubborn? :answer answer})
    (future
      (System.Threading.Thread/Sleep 300)
      (transport/respond-to msg :status "need-input")
      (let [line (deref answer 5000 :interrupted)]
        (swap! late-reads dissoc id)
        (if (= :interrupted line)
          (transport/respond-to msg :status ["interrupted" "done"])
          (do (transport/respond-to msg :ns "user" :value (pr-str line))
              (transport/respond-to msg :status "done")))))))

(defn wrap-stuck
  "Never answers an evaluation of (stuck), the way shadow-cljs doesn't one
  that keeps the JavaScript runtime busy, and answers one of (slow) after a
  second. nREPL can't interrupt either, since they don't run in the
  session. One of (hang-up) closes the connection. One of (late-read) reads
  a line from *in*, but only says so after 300ms, as if the server had been
  slow to say it, and ends once interrupted. One of (stubborn-read) does
  the same, but takes 500ms to say it can't be interrupted."
  [handler]
  (fn [{:keys [op code session interrupt-id stdin] :as msg}]
    (let [interrupted (get @late-reads interrupt-id)
          reading (some #(when (= session (:session %)) %) (vals @late-reads))]
      (cond
        (and (= "interrupt" op) interrupted)
        (if (:stubborn? interrupted)
          (future
            (System.Threading.Thread/Sleep 500)
            (transport/respond-to msg :status ["session-idle" "done"]))
          (do (deliver (:answer interrupted) :interrupted)
              (transport/respond-to msg :status "done")))
        (and (= "stdin" op) reading)
        (do (deliver (:answer reading) (str/trim-newline stdin))
            (transport/respond-to msg :status "done"))
        (not= "eval" op) (handler msg)
        (= "(stuck)" code) nil
        (= "(hang-up)" code) (.Dispose ^System.IDisposable (:transport msg))
        (= "(slow)" code) (future
                            (System.Threading.Thread/Sleep 1000)
                            (transport/respond-to msg :ns "user" :value ":slow")
                            (transport/respond-to msg :status "done"))
        (= "(late-read)" code) (late-read msg false)
        (= "(stubborn-read)" code) (late-read msg true)
        :else (handler msg)))))

(middleware/set-descriptor! #'wrap-stuck {:expects #{"eval" "interrupt" "stdin"}})

(defn fixture
  "A :once fixture that runs the tests with an nREPL server listening on
  *port*, with wrap-stuck unless opts, which go to
  nrepl.server/start-server, give it a :handler."
  [& opts]
  (fn [f]
    (let [srv (apply server/start-server :port 0 :handler (server/default-handler #'wrap-stuck) opts)]
      (try
        (binding [*port* (:port srv)]
          (f))
        (finally
          (server/stop-server srv))))))

(defmacro with-client
  "Evaluates body with sym bound to a client connected with opts."
  [[sym opts] & body]
  `(let [~sym (nrepl/connect ~opts)]
     (try ~@body (finally (nrepl/close ~sym)))))

(defn windows? []
  (System.OperatingSystem/IsWindows))

(defn lf
  "s with \n for the \r\n that println ends lines with on Windows."
  [s]
  (some-> s (str/replace "\r\n" "\n")))

(defn file
  "The path of parts joined, like clojure.java.io/file."
  ^String [dir & parts]
  (reduce #(Path/Combine ^String %1 ^String (str %2)) (str dir) parts))

(def ^:private package-dir
  "packages/neorepl."
  (-> *file* Path/GetFullPath Path/GetDirectoryName Path/GetDirectoryName Path/GetDirectoryName))

(defn clojure-command
  "The command that runs cleat with the nrepl and neorepl packages on its load
  path, to which the host's arguments (-e, -m, ...) go."
  []
  (let [path System.Environment/ProcessPath
        exe (if (= "dotnet" (Path/GetFileNameWithoutExtension path))
              [path (.Location (System.Reflection.Assembly/GetEntryAssembly))]
              [path])]
    (into exe ["-I" (Path/Combine (Path/GetDirectoryName package-dir) "nrepl" "src")
               "-I" (Path/Combine package-dir "src")])))

(defn free-port
  "A port nothing listens on, for the time being."
  []
  (with-open [socket (Socket. System.Net.Sockets.SocketType/Stream System.Net.Sockets.ProtocolType/Tcp)]
    (.Bind socket (IPEndPoint. IPAddress/Loopback 0))
    (.Port ^IPEndPoint (.LocalEndPoint socket))))

(defn call-with-temp-dir
  "Calls f with a fresh temporary directory (a path), deleting it afterwards."
  [f]
  (let [dir (.FullName (Directory/CreateTempSubdirectory "neorepl-test"))]
    (try
      (f dir)
      (finally
        (try (Directory/Delete dir true) (catch Exception _ nil))))))

(defn spit-file
  "Writes content to path under dir, creating the directories in between, and
  returns the file's path."
  ^String [dir path content]
  (let [f (file dir path)]
    (Directory/CreateDirectory (Path/GetDirectoryName f))
    (spit f content)
    f))

(defn run-process
  "Runs the command cmd (strings) in dir with the environment variables in env
  added and the text in (optional) input on its standard input; returns
  {:out :err :exit}, waiting at most timeout-ms (60s by default)."
  [cmd & {:keys [dir env input timeout-ms] :or {timeout-ms 60000}}]
  (let [psi (doto (System.Diagnostics.ProcessStartInfo. ^String (first cmd))
              (.set_RedirectStandardInput true)
              (.set_RedirectStandardOutput true)
              (.set_RedirectStandardError true)
              (.set_UseShellExecute false))]
    (doseq [a (rest cmd)] (.Add (.ArgumentList psi) (str a)))
    (when dir (.set_WorkingDirectory psi (str dir)))
    (doseq [[k v] env] (.set_Item (.Environment psi) (str k) (when v (str v))))
    (with-open [p (System.Diagnostics.Process/Start psi)]
      (let [out (.ReadToEndAsync (.StandardOutput p))
            err (.ReadToEndAsync (.StandardError p))]
        (when input (.Write (.StandardInput p) ^String input))
        (.Close (.StandardInput p))
        (if (.WaitForExit p (int timeout-ms))
          {:out (.Result out) :err (.Result err) :exit (.ExitCode p)}
          (do (try (.Kill p true) (catch Exception _ nil))
              {:out (.Result out) :err (.Result err) :exit :timeout}))))))
