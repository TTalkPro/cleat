;   Copyright (c) Rich Hickey. All rights reserved.
;   The use and distribution terms for this software are covered by the
;   Eclipse Public License 1.0 (http://opensource.org/licenses/eclipse-1.0.php)
;   which can be found in the file epl-v10.html at the root of this distribution.
;   By using this software in any fashion, you are agreeing to be bound by
;   the terms of this license.
;   You must not remove this notice, or any other, from this software.

;; cleat port: System.Diagnostics.Process; java-command -> cleat-command.

(ns clojure.tools.build.tasks.process
  (:require
    [clojure.tools.build.api :as api]
    [clojure.tools.build.util.file :as file]
    [clojure.string :as str])
  (:import
    [System.Diagnostics Process ProcessStartInfo]
    [System.IO File FileMode FileStream Path Stream]))

(defn- trim-blank [^String s]
  (if (str/blank? s) nil s))

(defn- pump
  "Copy the process stream to target (a Stream) on another thread; returns a future."
  [^Stream from ^Stream to close?]
  (future
    (try
      (.CopyTo from to)
      (.Flush to)
      (finally
        (when close? (.Dispose to))))))

(defn- pump-text
  "Copy the process's text output to a TextWriter (*out* / *err*) on another thread; returns a future."
  [^System.IO.StreamReader from ^System.IO.TextWriter to]
  (future
    (let [buf (char-array 4096)]
      (loop []
        (let [n (.Read from buf 0 4096)]
          (when (pos? n)
            (.Write to buf 0 n)
            (.Flush to)
            (recur)))))))

(defn- output-file
  "A FileStream writing (or appending to) file, creating its parent dirs."
  ^FileStream [file ^FileMode mode]
  (let [path (Path/GetFullPath (api/resolve-path file))]
    (file/ensure-dir (Path/GetDirectoryName path))
    (FileStream. path mode)))

(defn process
  "Exec the command made from command-args, redirect out and err as directed,
  and return {:exit exit-code, :out captured-out, :err captured-err}

  Options:
    :command-args - required, coll of string args
    :dir - directory to run the command from, default current directory
    :out - one of :inherit :capture :write :append :ignore
    :err - one of :inherit :capture :write :append :ignore
    :out-file - file path to write if :out is :write or :append
    :err-file - file path to write if :err is :write or :append
    :env - map of environment variables to set

  The :out and :err input flags take one of the following options:
    :inherit - inherit the stream and write the subprocess io to this process's stream (default)
    :capture - capture the stream to a string and return it
    :write - write to :out-file or :err-file
    :append - append to :out-file or :err-file
    :ignore - ignore the stream"
  [{:keys [command-args dir env out err out-file err-file]
    :or {dir ".", out :inherit, err :inherit} :as opts}]
  (when (not (seq command-args))
    (throw (ex-info "process missing required arg :command-args" opts)))
  (let [[cmd & args] command-args
        psi (ProcessStartInfo. (str cmd))
        ;; :inherit goes through *out* / *err* when they are not the console's
        ;; (a REPL, a test capturing output), like the child writing to this process's streams
        redirect? (fn [opt console] (not (and (= opt :inherit) console)))
        out-console? (identical? *out* System.Console/Out)
        err-console? (identical? *err* System.Console/Error)]
    (doseq [a args] (.Add (.ArgumentList psi) (str a)))
    (set! (.WorkingDirectory psi) (Path/GetFullPath (api/resolve-path (or dir "."))))
    (set! (.UseShellExecute psi) false)
    (set! (.RedirectStandardOutput psi) (redirect? out out-console?))
    (set! (.RedirectStandardError psi) (redirect? err err-console?))
    (doseq [[k v] env] (.set_Item (.Environment psi) (str k) (some-> v str)))
    (let [proc (Process/Start psi)
          sink (fn [opt file capture]
                 (case opt
                   :write (output-file file FileMode/Create)
                   :append (output-file file FileMode/Append)
                   :ignore Stream/Null
                   (:capture :pipe) capture))
          start (fn [opt file ^System.IO.StreamReader from writer buf]
                  (if (= opt :inherit)
                    (pump-text from writer)
                    (pump (.BaseStream from) (sink opt file buf) (contains? #{:write :append} opt))))
          out-buf (System.IO.MemoryStream.)
          err-buf (System.IO.MemoryStream.)
          out-task (when (redirect? out out-console?)
                     (start out out-file (.StandardOutput proc) *out* out-buf))
          err-task (when (redirect? err err-console?)
                     (start err err-file (.StandardError proc) *err* err-buf))]
      (.WaitForExit proc)
      (some-> out-task deref)
      (some-> err-task deref)
      (let [exit (.ExitCode proc)
            text #(trim-blank (.GetString System.Text.Encoding/UTF8 (.ToArray ^System.IO.MemoryStream %)))
            out-str (when (= out :capture) (text out-buf))
            err-str (when (= err :capture) (text err-buf))]
        (.Dispose proc)
        (cond-> {:exit exit}
          out-str (assoc :out out-str)
          err-str (assoc :err err-str))))))

(comment
  (api/process {:command-args ["ls" "-l"]})
  (api/process {:command-args ["git" "log"] :out :ignore})
  (api/process {:env {"FOO" "hi"}
                :command-args ["sh" "-c" "echo $FOO"]
                :out :capture})
  )

(defn which
  "Given the name of an executable, return either a full path to
  its location on the system PATH or nil if not found"
  [cmd]
  (when-let [path (System.Environment/GetEnvironmentVariable "PATH")]
    (let [paths (str/split path (re-pattern (System.Text.RegularExpressions.Regex/Escape (str Path/PathSeparator))))
          names (if (System.OperatingSystem/IsWindows) [(str cmd ".exe") (str cmd ".cmd") cmd] [cmd])]
      (first (for [p paths, n names
                   :let [f (Path/Combine p n)]
                   :when (File/Exists f)]
               (Path/GetFullPath f))))))

(defn cleat-executable
  "The command line (a vector) running cleat: $CLEAT_CMD (split on
  whitespace), else the cleat this process runs on: its apphost, or dotnet
  and cleat.dll."
  []
  (if-let [cmd (some-> (System.Environment/GetEnvironmentVariable "CLEAT_CMD") str/trim not-empty)]
    (str/split cmd #"\s+")
    (let [path System.Environment/ProcessPath]
      (if (= "dotnet" (Path/GetFileNameWithoutExtension path))
        [path (.Location (System.Reflection.Assembly/GetEntryAssembly))]
        [path]))))

(defn cleat-command
  "Create cleat command line args. The load path will be the combination of
  :cp followed by the load path from the basis, both are optional.

  Options:
    :cleat-cmd - coll of strings, default = $CLEAT_CMD or the running cleat
    :cp - coll of string load path entries, used first (if provided)
    :basis - runtime basis used for the load path, used last (if provided)
    :cleat-opts - coll of string cleat options
    :main - required, main namespace symbol
    :main-args - coll of main args

  Returns:
    :command-args - coll of command arg strings"
  [{:keys [cleat-cmd cp basis cleat-opts main main-args] :as _params}]
  (let [cmd (or (seq cleat-cmd) (cleat-executable))
        {:keys [classpath-roots argmap]} basis
        entries (distinct (concat cp classpath-roots))
        combined-opts (concat cleat-opts (:cleat-opts argmap))]
    {:command-args (vec (concat cmd
                                (mapcat #(vector "-I" (str %)) entries)
                                combined-opts
                                ["-m" (name main)]
                                main-args))}))
