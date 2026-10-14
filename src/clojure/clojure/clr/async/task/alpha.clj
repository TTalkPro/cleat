(ns clojure.clr.async.task.alpha
  "Task-based async/await interop.
   Provides idiomatic Clojure wrappers around System.Threading.Tasks.Task."
  (:refer-clojure :exclude [await])
  (:import [System.Threading.Tasks Task]))

;; CLEAT-043: ClojureCLR 的 clojure.clr.async.task.alpha（同名同 API）。^:async fn 与 await* 由 cleat 的编译器实现：
;; 运行时 async 可用时（.NET 11 起缺省；.NET 10 要 DOTNET_RuntimeAsync=1）是真正的挂起 / 恢复，否则 await 同步等待、
;; async 体返回已完成的 Task（见 Cleat.Core.Runtime.AsyncSupport）。取值走 AsyncSupport/ResultOf（任意 Task<T>，
;; 失败时抛出原来的异常），不经反射的 GetAwaiter。

;;  ── Extracting a Task result ──────────────────────

(defn result
  "Blocks the calling thread until the task completes and returns its result.
   For Task<T>, returns T. For void Task (non-generic), returns nil.
   Unwraps AggregateException to throw the inner exception directly.

   Usage:
     (t/result (t/->task 42))       ;=> 42
     (t/result (t/completed-task))   ;=> nil
     (t/result (t/async (t/await (t/delay-task 100)) \"done\"))  ;=> \"done\""
  [task]
  (Cleat.Core.Runtime.AsyncSupport/ResultOf (Cleat.Core.Runtime.AsyncSupport/ToTask task)))

;; ── Macros (only these two require macro status) ──────────────────────

(defmacro await
  "Suspends the current ^:async function until the given Task completes.
   Returns the task's result value. Only valid inside a ^:async defn
   or an (async ...) block.

   Usage:
     (t/await (.ReadAllTextAsync System.IO.File path))"
  [task-expr]
  `(await* ~task-expr))

(defmacro async
  "Executes body in an async context, returning Task<object>.
   The body may use (await ...) to suspend on Tasks.

   Usage:
     (t/async
       (let [data (t/await (fetch url))]
         (process data)))"
  [& body]
  `((^:async fn* [] ~@body)))

;; ── Waiting, but staying non-:async ─────────────────────────────────────────────────────────

(defn- convert-timeout
  "Converts a Clojure timeout value to an int for Task.WaitAll.
   Accepts nil (no timeout), a number (milliseconds), or a TimeSpan."
  [timeout]
  (cond
    (nil? timeout) -1
    (number? timeout) (int timeout)
    :else (int (.TotalMilliseconds ^TimeSpan timeout))))

(defn- task-array [tasks]
  (into-array Task (map #(Cleat.Core.Runtime.AsyncSupport/ToTask %) tasks)))

(defn wait-all
  "Waits for all of the provided Tasks to complete execution.  Returns nil.

   Usage: (t/wait-all tasks)
          (t/wait-all tasks timeout)
          (t/wait-all task timeout cancellation-token)

   timeout values can be any numeric value (cast to int, milliseconds, -1 = no limit) or a TimeSpan"
  ([tasks] (Task/WaitAll ^"System.Threading.Tasks.Task[]" (task-array tasks)) nil)
  ([tasks timeout] (Task/WaitAll ^"System.Threading.Tasks.Task[]" (task-array tasks) (int (convert-timeout timeout))))
  ([tasks timeout cancellation-token]
   (Task/WaitAll ^"System.Threading.Tasks.Task[]" (task-array tasks) (int (convert-timeout timeout)) cancellation-token)))

(defn wait-any
  "Waits for any of the provided Task to complete execution.  Returns the task that completed or nil if a timeout occurred.

   Usage: (t/wait-any tasks)
          (t/wait-any tasks timeout)
          (t/wait-any task timeout cancellation-token)

   timeout values can be any numeric value (cast to int, milliseconds, -1 = no limit) or a TimeSpan"
  ([tasks]
   (nth tasks (Task/WaitAny ^"System.Threading.Tasks.Task[]" (task-array tasks))))
  ([tasks timeout]
   (let [idx (Task/WaitAny ^"System.Threading.Tasks.Task[]" (task-array tasks) (int (convert-timeout timeout)))]
     (when-not (= idx -1) (nth tasks idx))))
  ([tasks timeout cancellation-token]
   (let [idx (Task/WaitAny ^"System.Threading.Tasks.Task[]" (task-array tasks) (int (convert-timeout timeout)) cancellation-token)]
     (when-not (= idx -1) (nth tasks idx)))))

(defn wait-all-results
  "Waits for all of the provided Tasks to complete execution.  Returns a lazy sequence of the result for each task, with nil with for non-generic Tasks.

   Usage: (t/wait-all-results tasks)
          (t/wait-all-results tasks timeout)
          (t/wait-all-results task timeout cancellation-token)

   timeout values can be any numeric value (cast to int, milliseconds, -1 = no limit) or a TimeSpan"
  ([tasks]
   (wait-all tasks)
   (map result tasks))
  ([tasks timeout]
   (when (wait-all tasks timeout)
     (map result tasks)))
  ([tasks timeout cancellation-token]
   (when (wait-all tasks timeout cancellation-token)
     (map result tasks))))

(defn wait-any-result
  "Waits for any of the provided Task to complete execution.  Returns the result of the task that completed or nil if a timeout occurred.

   Usage: (t/wait-any-results tasks)
          (t/wait-any-results tasks timeout)
          (t/wait-any-results task timeout cancellation-token)

   timeout values can be any numeric value (cast to int, milliseconds, -1 = no limit) or a TimeSpan"
  ([tasks]
   (result (wait-any tasks)))
  ([tasks timeout]
   (when-let [t (wait-any tasks timeout)] (result t)))
  ([tasks timeout cancellation-token]
   (when-let [t (wait-any tasks timeout cancellation-token)] (result t))))

;; ── Creating standard tasks

(defn delay-task
  "Returns a Task that completes after the specified duration in milliseconds.
   For TimeSpan, use (Task/Delay timespan) directly.

   Usage:
     (t/await (t/delay-task 1000))"
  ^Task [milliseconds]
  (Task/Delay (int milliseconds)))

(defn ->task
  "Wraps a value in a completed Task<object>.

   Usage:
     (t/->task 42)  ;=> completed Task whose result is 42"
  [value]
  (Task/FromResult (type-args Object) value))

(defn completed-task
  "Returns a cached, already-completed void Task."
  ^Task []
  Task/CompletedTask)

(defn task?
  "Returns true if x is a Task."
  [x]
  (instance? Task x))

(defn run
  "Runs f (zero-arg fn) on the thread pool. Returns Task<object>.

   Usage:
     (t/await (t/run (fn [] (+ 1 2 3))))"
  [f]
  ;; CLEAT-043: 显式泛型实参——Task.Run<TResult>(Func<TResult>) 与 Task.Run(Func<Task>) 只凭实参类型选不出来
  (Task/Run (type-args Object) (gen-delegate |System.Func`1[System.Object]| [] (f))))
