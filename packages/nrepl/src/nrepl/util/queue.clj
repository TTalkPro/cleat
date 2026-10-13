(ns nrepl.util.queue
  "A blocking FIFO queue, standing in for java.util.concurrent's
  LinkedBlockingQueue and SynchronousQueue, which nREPL uses throughout.

  cleat: added in the CLR port. The BCL's BlockingCollection is generic and
  returns through an out parameter, so this one is built on a persistent
  queue and System.Threading.Monitor. Waits are interruptible: a
  Thread.Interrupt makes them throw ThreadInterruptedException, like an
  interrupted take on the JVM."
  (:import (System.Threading Monitor)))

(defprotocol BlockingQueue
  (put! [q x] "Adds x at the tail. Never blocks.")
  (take! [q] "Removes and returns the head, waiting for one if there's none.")
  (poll! [q] [q timeout-ms]
    "Removes and returns the head, or nil when there's none, after waiting up
    to timeout-ms milliseconds for one with the two-argument arity.")
  (clear! [q] "Removes everything.")
  (size [q] "The number of items in the queue."))

(defn- remaining
  "The milliseconds left until deadline, at most Int32/MaxValue."
  ^long [^long deadline]
  (min (- deadline (System.Environment/TickCount64)) Int32/MaxValue))

(deftype LinkedQueue [^:unsynchronized-mutable items lock]
  BlockingQueue
  (put! [this x]
    (locking lock
      (set! items (conj items x))
      (Monitor/PulseAll lock))
    this)
  (take! [_]
    (locking lock
      (loop []
        (if (seq items)
          (let [x (peek items)]
            (set! items (pop items))
            x)
          (do (Monitor/Wait lock)
              (recur))))))
  (poll! [_]
    (locking lock
      (when (seq items)
        (let [x (peek items)]
          (set! items (pop items))
          x))))
  (poll! [_ timeout-ms]
    (let [timeout-ms (long timeout-ms)
          ;; Long/MAX_VALUE, which callers use for "forever", would overflow.
          deadline (if (>= timeout-ms (- Int64/MaxValue (System.Environment/TickCount64)))
                     Int64/MaxValue
                     (+ (System.Environment/TickCount64) timeout-ms))]
      (locking lock
        (loop []
          (if (seq items)
            (let [x (peek items)]
              (set! items (pop items))
              x)
            (let [ms (remaining deadline)]
              (when (pos? ms)
                (Monitor/Wait lock (int ms))
                (recur))))))))
  (clear! [_]
    (locking lock
      (set! items clojure.lang.PersistentQueue/EMPTY)))
  (size [_]
    (locking lock
      (count items)))
  ;; A snapshot of what's in the queue, head first (a LinkedBlockingQueue is
  ;; Iterable on the JVM, and callers look through it).
  clojure.lang.Seqable
  (seq [_]
    (locking lock
      (seq items))))

(defn linked-queue
  "Returns a new, empty blocking queue."
  []
  (LinkedQueue. clojure.lang.PersistentQueue/EMPTY (Object.)))
