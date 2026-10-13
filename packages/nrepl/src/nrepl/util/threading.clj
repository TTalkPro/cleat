(ns nrepl.util.threading
  "Functions and tools for dealing with all threads and threadpools necessary for
  nREPL operation."
  {:added "1.3"}
  (:require
   [nrepl.misc :as misc])
  (:import
   (System.Threading Thread ThreadStart)))

;; cleat: the JVM version runs tasks on cached thread pools (Executors) and
;; kills runaway session threads with Thread.stop or a JVMTI agent. Here:
;;
;; - An "executor" names the threads it starts; each task gets a fresh
;;   background thread. Threads are cheap next to the network round trip a
;;   message takes, and a fresh thread can't inherit a stuck one's state.
;; - Every thread gets a large stack (*thread-stack-size*): evaluating code
;;   runs cleat's expander and compiler, which recurse deeply, and so does
;;   loading a namespace through requiring-resolve on a handler thread. The
;;   cleat host runs its main thread with the same size.
;; - .NET has no Thread.stop (Thread.Abort is gone since .NET 5), so
;;   `interrupt-stop` interrupts the thread, which stops it in a Sleep, a Wait,
;;   a Join or a blocking queue take, and remembers that it did (`interrupted?`):
;;   the writers nREPL binds to *out* and *err* throw on such a thread, which
;;   stops code that keeps printing, and its evaluation's errors go unreported,
;;   as an evaluation killed by Thread.stop's ThreadDeath does on the JVM. After
;;   5 seconds it gives up: a thread busy computing keeps running, abandoned, in
;;   the background.

(def ^:dynamic *thread-stack-size*
  "The stack size, in bytes, of the threads nREPL starts. Only reserved address
  space until used."
  (* 512 1024 1024))

(defn start-thread
  "Starts f on a new background thread called name and returns the thread. An
  exception f throws is logged: on the CLR an exception escaping a thread ends
  the whole process, where the JVM's default handler prints it and only the
  thread ends."
  ^Thread [^String name f]
  (doto (Thread. ^ThreadStart (gen-delegate ThreadStart []
                                (try
                                  (f)
                                  (catch System.Threading.ThreadInterruptedException _)
                                  (catch Exception e
                                    (misc/log e (str "Uncaught exception on thread " name)))))
                 (int *thread-stack-size*))
    (.set_Name name)
    (.set_IsBackground true)
    (.Start)))

(defn thread-factory
  "Returns an executor: a function of a task (a function of no arguments) that
  runs it on a new daemon thread named after `thread-name-fmt`, a format string
  for a counter, and returns the thread."
  [thread-name-fmt]
  (let [counter (atom -1)]
    (fn [f]
      (start-thread (format thread-name-fmt (swap! counter inc)) f))))

(defmacro run-with
  "Run the provided `body` using `executor`. Don't wait for the result (unless the
  executor itself is blocking). Returns the thread running it."
  {:style/indent 1}
  [executor & body]
  `(~executor (fn [] ~@body)))

;; Executors are wrapped in delays to keep the same API as the JVM version.

(def listen-executor
  "Executor used to accept incoming connections."
  (delay (thread-factory "nREPL-server-%d")))

(def transport-executor
  "Executor used to run the transport handler loop and handle individual requests."
  (delay (thread-factory "nREPL-transport-%d")))

(def handle-executor
  "Executor used to run the handler loop and handle individual requests."
  (delay (thread-factory "nREPL-handler-%d")))

;;;; Thread interruption. Used by session middleware to make eval interruptible.

(defonce ^:private interrupted-threads (atom #{}))

(defn interrupted?
  "Whether `interrupt-stop` has interrupted thread t (the current thread by
  default). The CLR keeps no interrupt status one could ask about, unlike the
  JVM's Thread.isInterrupted, so nREPL keeps its own."
  ([] (interrupted? (Thread/CurrentThread)))
  ([t] (contains? @interrupted-threads t)))

(def ^:private force-stop-delay-ms 5000)

(defn interrupt-stop
  "Try to interrupt the thread normally. Asynchronously wait for 5000ms and, if
  the thread didn't terminate itself, log that it's left running: the CLR can't
  kill a thread from the outside."
  [^Thread t]
  ;; TODO: make timeouts configurable?
  (swap! interrupted-threads #(conj (into #{} (filter (fn [^Thread t] (.IsAlive t))) %) t))
  (.Interrupt t)
  (start-thread "nREPL-thread-reaper"
                #(misc/log-exceptions
                  (when-not (.Join t (int force-stop-delay-ms))
                    (misc/log :warning "Thread" (.Name t)
                              "did not respond to the interrupt and is left running"
                              "in the background (the CLR has no Thread.stop).")))))
