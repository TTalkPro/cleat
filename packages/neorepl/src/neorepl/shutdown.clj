(ns neorepl.shutdown
  "Not leaving things behind when neorepl exits before it's done with them,
  on Ctrl-C or SIGTERM."
  (:require nrepl.socket))

;; cleat: ported to the CLR. The JVM's shutdown hook is the ProcessExit event,
;; which runs on Environment/Exit, and on SIGTERM through nREPL's
;; exit-on-sigterm!.

(defonce ^:private cleanups
  ;; What runs when neorepl exits, in the order it was added, or ::exiting
  ;; once it's running.
  (atom []))

(defn- run-cleanups
  "Runs the cleanups latest first, the way they'd run had neorepl got to
  finish, so an evaluation gets interrupted before its session closes and
  that before its server stops."
  []
  (let [[before] (reset-vals! cleanups ::exiting)]
    (when (vector? before)
      (doseq [cleanup (rseq before)]
        ;; The rest still run.
        (try (cleanup) (catch Exception _ nil))))))

(defonce ^:private hook
  (delay
    ;; On SIGTERM .NET doesn't raise ProcessExit by itself.
    (nrepl.socket/exit-on-sigterm!)
    (.add_ProcessExit System.AppDomain/CurrentDomain
                      (gen-delegate System.EventHandler [_ _] (run-cleanups)))))

(defn exiting?
  "Whether neorepl's exiting."
  []
  (= ::exiting @cleanups))

(defn add!
  "Has f run when neorepl exits, before whatever was added earlier, until
  it's removed. Returns what remove! takes, nil when neorepl's exiting
  already."
  [f]
  @hook
  ;; A fresh fn, so that f added twice takes removing twice.
  (let [cleanup (fn [] (f))]
    (when (vector? (swap! cleanups #(cond-> % (vector? %) (conj cleanup))))
      cleanup)))

(defn remove!
  "Takes what add! returned off what runs when neorepl exits. Returns
  whether it's up to the caller to clean up now: false when neorepl's
  exiting, which ran it or runs it."
  [cleanup]
  (or (nil? cleanup)
      (vector? (first (swap-vals! cleanups #(cond->> % (vector? %) (filterv (complement #{cleanup}))))))))

(defn call-with-cleanup
  "Calls f, then cleanup, which runs when neorepl exits before f returns
  too."
  [cleanup f]
  (let [added (add! cleanup)]
    (try
      (f)
      (finally
        (when (remove! added)
          (cleanup))))))
