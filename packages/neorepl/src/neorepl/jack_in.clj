(ns neorepl.jack-in
  "Starting an nREPL server for a project, the way CIDER's jack-in does: a
  cleat process with the project's source on its load path, running
  nrepl.cmdline.

  cleat: the JVM version knows the Clojure CLI, Leiningen, babashka and
  shadow-cljs, and adds cider-nrepl and piggieback to the server. Here the one
  tool is cleat, a project is a directory with a cleat.edn, and the server gets
  the nrepl package from the load path neorepl itself runs with. cleat.edn is
  EDN, every key optional:

    {:paths [\"src\"]           ; directories to put on the load path
     :middleware [my.ns/wrap]}  ; middleware for nrepl.cmdline's --middleware"
  (:require [clojure.edn :as edn]
            [clojure.string :as str]
            [neorepl.discovery :as discovery]
            [neorepl.shutdown :as shutdown])
  (:import (System.Diagnostics Process ProcessStartInfo)
           (System.IO File Path)
           (System.Threading Thread ThreadStart)))

(def tools
  "The build tools jack-in knows, by the file a project of theirs has,
  whether they take :aliases? and a :port?."
  [{:tool "cleat" :file "cleat.edn" :port? true}])

(defn tool
  "The entry in tools for the tool called name."
  [name]
  (some #(when (= name (:tool %)) %) tools))

(defn project-files
  "The files that make a project tool's, or any tool's without one, as text."
  [tool-name]
  (let [files (map :file (if tool-name [(tool tool-name)] tools))]
    (if (next files)
      (str (str/join ", " (butlast files)) " or " (last files))
      (first files))))

(defn- tools-in
  "The tools with their files in dir, in the order to pick them."
  [dir]
  (filter #(File/Exists (Path/Combine ^String dir ^String (:file %))) tools))

(defn find-project
  "The project dir is in: the nearest directory, dir or a parent, with a file
  of tool's, or of any tool's without one, as its :dir and the :tool to use.
  Nil when there's none."
  [dir tool-name]
  (first (for [d (discovery/dirs dir)
               t (tools-in d)
               :when (or (nil? tool-name) (= tool-name (:tool t)))]
           {:dir (str d) :tool (:tool t)})))

(defn no-project
  "The error for there being no project for find-project to find."
  [dir tool-name]
  (ex-info (str "No " (project-files tool-name) " in " (discovery/canonical-path dir) " or its parents")
           {:type ::no-project}))

(defn- cleat-executable
  "The command line running the cleat this process runs on: its apphost, or
  dotnet and cleat.dll."
  []
  (let [path System.Environment/ProcessPath]
    (if (= "dotnet" (Path/GetFileNameWithoutExtension path))
      [path (.Location (System.Reflection.Assembly/GetEntryAssembly))]
      [path])))

(defn- nrepl-source-dir
  "The load path directory nREPL is in, for the server's load path: the one
  with its package assembly (Cleat.Nrepl.dll), or with its sources."
  []
  (or (some-> (Cleat.Core.Runtime.PackageIndex/Locate "nrepl/cmdline") Path/GetDirectoryName)
      (some (fn [^String dir]
              (when (File/Exists (Path/Combine dir "nrepl" "cmdline.clj"))
                (Path/GetFullPath dir)))
            (seq Cleat.Core.Runtime.Loader/LoadPath))
      (throw (ex-info "Can't find the nrepl package on the load path (cleat -I packages/nrepl/src)"
                      {:type ::failed}))))

(defn- project-config
  "The project's cleat.edn, read."
  [dir]
  (let [file (Path/Combine ^String dir "cleat.edn")
        text (when (File/Exists file) (slurp file))]
    (try
      (or (some-> text str/trim not-empty edn/read-string) {})
      (catch Exception e
        (throw (ex-info (str "Can't read " file ": " (ex-message e)) {:type ::failed}))))))

(defn command
  "The command line that starts an nREPL server for tool in the project in
  dir, on port, or any port without one."
  ([tool-name opts] (command tool-name opts "."))
  ([tool-name {:keys [port]} dir]
   (case tool-name
     "cleat" (let [{:keys [paths middleware]} (project-config dir)]
               (cond-> (into (cleat-executable) ["-I" (nrepl-source-dir)])
                 true (into (mapcat #(vector "-I" (str %)) (or paths ["src"])))
                 true (into ["-m" "nrepl.cmdline"])
                 (seq middleware) (into ["--middleware" (pr-str (vec middleware))])
                 port (into ["--port" (str port)]))))))

(defn- address
  "Where the server that printed line says it's listening, if it does."
  [line]
  (when-let [[_ port host] (re-find #"nREPL server started on port (\d+)(?: on host (\S+))?" line)]
    {:host (or host "127.0.0.1") :port (Int64/Parse ^String port)}))

(def ^:private windows?
  (System.OperatingSystem/IsWindows))

(defn- launch-command
  "The command line that runs command so that the server ignores SIGINT,
  which a terminal sends everything in the foreground on Ctrl-C, when it's
  meant for whatever runs the server: a signal ignored before exec stays
  ignored."
  [command]
  (if windows?
    command
    (concat ["sh" "-c" "trap '' INT; exec \"$@\"" "sh"] command)))

(defn- run
  "Runs a command line, returning its standard output, or nil when it can't."
  [& args]
  (try
    (let [psi (doto (ProcessStartInfo. ^String (first args))
                (.set_RedirectStandardOutput true)
                (.set_UseShellExecute false))]
      (doseq [a (rest args)] (.Add (.ArgumentList psi) (str a)))
      (with-open [p (Process/Start psi)]
        (let [out (.ReadToEnd (.StandardOutput p))]
          (.WaitForExit p 5000)
          out)))
    (catch Exception _ nil)))

(defn- descendant-pids
  "The ids of the processes pid started, and the ones they did, as pgrep
  finds them (the JVM has ProcessHandle.descendants; .NET doesn't say)."
  [pid]
  (when-not windows?
    (let [children (keep #(some-> % str/trim not-empty Int32/Parse)
                         (str/split-lines (or (run "pgrep" "-P" (str pid)) "")))]
      (concat children (mapcat descendant-pids children)))))

(defn- signal [sig pid]
  (run "kill" (str "-" sig) (str pid)))

(defn- alive? [pid]
  (try (not (.HasExited (Process/GetProcessById (int pid)))) (catch Exception _ false)))

(defn- stop
  "Stops process, and the processes it started, the way they would on
  SIGTERM, giving them a few seconds to before killing them."
  [^Process process]
  (if windows?
    (when-not (.HasExited process)
      (.Kill process true))
    (let [all (cons (.Id process) (descendant-pids (.Id process)))
          deadline (+ (Environment/TickCount64) 5000)]
      (run! #(signal "TERM" %) all)
      (doseq [pid all]
        (while (and (alive? pid) (< (Environment/TickCount64) deadline))
          (Thread/Sleep 50))
        (when (alive? pid)
          (signal "KILL" pid))))))

(defn- follow
  "Starts a thread handing each line reader reads to (on-line line)."
  ^Thread [^System.IO.TextReader reader on-line]
  (doto (Thread. ^ThreadStart (gen-delegate ThreadStart []
                                (try
                                  (loop []
                                    (when-let [line (.ReadLine reader)]
                                      (on-line line)
                                      (recur)))
                                  (catch Exception _ nil))))
    (.set_Name "neorepl-server-output")
    (.set_IsBackground true)))

(defn with-server
  "Starts the server command describes in dir and, once it says where it
  listens, calls (f server) with its :host, :port and :process, returning
  what f does. The server stops when f returns, or when neorepl exits
  first, even before the server's up. Each line it prints goes to
  (on-line line), on a thread of its own. Throws ::failed when the server
  can't start, or ends before it's up. Ctrl-C in the terminal doesn't stop
  it, see launch-command."
  [dir command on-line f]
  (let [[exe & args] (vec (launch-command command))
        psi (doto (ProcessStartInfo. ^String exe)
              (.set_WorkingDirectory (str dir))
              (.set_RedirectStandardOutput true)
              (.set_RedirectStandardError true)
              (.set_RedirectStandardInput true)
              (.set_UseShellExecute false))
        _ (doseq [a args] (.Add (.ArgumentList psi) a))
        process (try
                  (Process/Start psi)
                  (catch Exception e
                    (throw (ex-info (str "Couldn't start the server: " (ex-message e)) {:type ::failed}))))
        up (promise)
        on-line (fn [line]
                  (on-line line)
                  (when-not (realized? up)
                    (some->> (address line) (deliver up))))
        out (follow (.StandardOutput process) on-line)
        err (follow (.StandardError process) on-line)
        waiter (doto (Thread. ^ThreadStart (gen-delegate ThreadStart []
                                             (try
                                               (.WaitForExit process)
                                               (.Join out 1000)
                                               (.Join err 1000)
                                               (catch Exception _ nil))
                                             (deliver up nil)))
                 (.set_IsBackground true))]
    ;; Nothing for it to read.
    (.Close (.StandardInput process))
    (shutdown/call-with-cleanup
     ;; What it says as it stops goes out too.
     #(do (stop process)
          (.Join out 1000)
          (.Join err 1000))
     (fn []
       (.Start out)
       (.Start err)
       (.Start waiter)
       (if-let [address @up]
         (f (assoc address :process process))
         (throw (ex-info (str "The server ended before it started, with exit status "
                              (do (.WaitForExit process) (.ExitCode process)))
                         {:type ::failed})))))))
