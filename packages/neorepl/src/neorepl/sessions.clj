(ns neorepl.sessions
  "Named sessions that outlive the process that created them, so separate CLI
  calls can share *ns*, *1, *e and the like. nREPL sessions have no names,
  so the ids live in a cache file, keyed by server address and name."
  (:require [clojure.edn :as edn]
            [neorepl.nrepl :as nrepl])
  (:import (System.IO Directory File FileAccess FileMode FileShare FileStream IOException Path)))

;; cleat: ported to the CLR. The cache file is a path string; the lock is the
;; lock file opened with FileShare/None (FileChannel.lock on the JVM), which
;; another process gets by retrying, and the cache is replaced with File/Move.
;; The cache is read with File/ReadAllText (shared reading): slurp opens files
;; with FileShare/None (clojure.clr.io), so a reader outside the lock made the
;; locked one fail, see an empty cache and drop others' sessions.

(defn- make-parents [^String file]
  (Directory/CreateDirectory (Path/GetDirectoryName (Path/GetFullPath file))))

(defn- read-cache [^String file]
  (let [cache (when (File/Exists file)
                ;; It's only a cache, so a broken one counts as empty.
                (try (edn/read-string (File/ReadAllText file)) (catch Exception _ nil)))]
    (if (map? cache) cache {})))

(defn- write-cache!
  "Replaces file in one go, so concurrent readers never see half of it."
  [^String file cache]
  (try
    (make-parents file)
    (let [tmp (str file "." (System.Guid/NewGuid) ".tmp")]
      (try
        (spit tmp (pr-str cache))
        (File/Move tmp file true)
        (finally
          (File/Delete tmp))))
    ;; The session works without the cache, it just won't be there next time.
    (catch Exception e
      (binding [*out* *err*]
        (println (str "neorepl: Couldn't save the session in " file ": " (ex-message e)))))))

(def ^:private in-process
  "What threads hold while one of them has the cache locked: file locks keep
  other processes out, and fail for a thread whose process holds one already."
  (Object.))

(defn- open-lock
  "The lock file for file, opened for this process alone, or nil when another
  process still holds it after 10 seconds, or when it can't be opened at all."
  [^String file]
  (try
    (make-parents file)
    (let [deadline (+ (Environment/TickCount64) 10000)]
      (loop []
        (let [stream (try
                       (FileStream. (str file ".lock") FileMode/OpenOrCreate FileAccess/ReadWrite FileShare/None)
                       (catch IOException e
                         (if (< (Environment/TickCount64) deadline) ::busy (throw e))))]
          (if (= ::busy stream)
            (do (System.Threading.Thread/Sleep 20) (recur))
            stream))))
    (catch Exception _ nil)))

(defn- call-with-lock
  "Calls f holding a lock on file's lock file, so CLI calls running at the
  same time update the cache one after the other instead of dropping each
  other's sessions. When there's no locking it (e.g. on a file system
  without locks), f runs all the same, as sessions work without the cache."
  [^String file f]
  (locking in-process
    (let [^FileStream lock-file (open-lock file)]
      (try
        (f)
        (finally
          ;; Closing the file releases the lock.
          (some-> lock-file .Dispose))))))

(defn- live-sessions
  "A predicate telling whether client's server knows a session id. Servers
  without ls-sessions get the benefit of the doubt."
  [client]
  (if (contains? (:ops client) "ls-sessions")
    (set (some :sessions (nrepl/request client {:op "ls-sessions"})))
    any?))

(defn named
  "The sessions on client's server that have a name in file, as a sorted map
  of names to ids. Sessions the server doesn't know anymore, typically
  because it restarted, are left out."
  [file client]
  (let [live? (live-sessions client)]
    (into (sorted-map)
          (filter (comp live? val))
          (get (read-cache file) (:address client)))))

(defn- cached [file client name]
  (get-in (read-cache file) [(:address client) name]))

(defn ensure!
  "The id of the session called name on client's server, as cached in file.
  Clones and caches a new one when there's none yet or the server doesn't
  know the cached one anymore."
  [file client name]
  (let [before (cached file client name)]
    (if (and before ((live-sessions client) before))
      before
      (call-with-lock file
        #(let [cache (read-cache file)
               now (get-in cache [(:address client) name])]
           ;; Another call may have cached a new one in the meantime.
           (if (and now (not= now before))
             now
             (let [session (nrepl/clone-session client)]
               (write-cache! file (assoc-in cache [(:address client) name] session))
               session)))))))

(defn interrupt
  "Interrupts whatever runs in the session called name, returning how that
  went as nrepl/interrupt does."
  [file client name]
  ;; No need to check the session is alive: the server says so if it isn't.
  (if-let [session (cached file client name)]
    (nrepl/interrupt client session nil)
    :unknown-session))

(defn close!
  "Closes the session called name on client's server and drops it from file.
  Returns its id, or nil when there's no such session."
  [file client name]
  (when-let [session (cached file client name)]
    (let [closed? (nrepl/close-session client session)]
      (call-with-lock file #(write-cache! file (update (read-cache file) (:address client) dissoc name)))
      (when closed? session))))
