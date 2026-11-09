;;   Copyright (c) Rich Hickey and contributors. All rights reserved.
;;   The use and distribution terms for this software are covered by the
;;   Eclipse Public License 1.0 (http://opensource.org/licenses/eclipse-1.0.php)
;;   which can be found in the file epl-v10.html at the root of this distribution.
;;   By using this software in any fashion, you are agreeing to be bound by
;;   the terms of this license.
;;   You must not remove this notice, or any other, from this software.

(ns ^{:skip-wiki true}
  clojure.core.async.impl.dispatch
  (:require [clojure.core.async.impl.protocols :as impl]) 
  (:import #?(:clj [java.util.concurrent Executors Executor ThreadFactory] :cljr [System.Threading ThreadPool WaitCallback Thread ThreadStart])))

#?(:cljr

(alias-type ThreadLocal |System.Threading.ThreadLocal`1[Object]|)

)

(set! *warn-on-reflection* true)

(declare ex-handler)   ;;; CLEAT-167: thread-pool-executor's work item reports through it; defined further down


#?(

:cljr 
		
;;; Given that we are not implementing our own fixed-size thread pool but, rather, using the system thread pool,
;;; here we need only reify impl/Executor to queue a method to the thread pool.
		
(defn thread-pool-executor
  ([]
    (thread-pool-executor nil))
  ([init-fn]
     (reify impl/Executor
       (impl/exec [_ r]
         ;;; CLEAT-167: an exception escaping a thread-pool work item kills the .NET process
         (ThreadPool/QueueUserWorkItem (gen-delegate WaitCallback [_] (try (when init-fn (init-fn)) (r) (catch Exception t (ex-handler t)))))))))
)		 



#?(

:clj

(defonce executor nil)

:cljr

(defonce executor
  (delay (thread-pool-executor )))                                        ;;;  #(.set ^ThreadLocal in-dispatch true)  -- getting rid of this init-fn.

)

;;;(defn counted-thread-factory
;;;  "Create a ThreadFactory that maintains a counter for naming Threads.
;;;     name-format specifies thread names - use %d to include counter
;;;     daemon is a flag for whether threads are daemons or not
;;;     opts is an options map:
;;;       init-fn - function to run when thread is created"
;;;  ([name-format daemon]
;;;    (counted-thread-factory name-format daemon nil))
;;;  ([name-format daemon {:keys [init-fn] :as opts}]
;;;   (let [counter (atom 0)]
;;;     (reify
;;;       ThreadFactory
;;;       (newThread [_this runnable]
;;;         (let [body (if init-fn
;;;                      (fn [] (init-fn) (.run ^Runnable runnable))
;;;                      runnable)
;;;               t (Thread. ^Runnable body)]
;;;           (doto t
;;;             (.setName (format name-format (swap! counter inc)))
;;;             (.setDaemon daemon))))))))  
  

;;; DM: Added this type to match java.util.concurrent.ThreadFactory

(defprotocol ThreadFactory 
   "Protocol to match java.util.concurrent.ThreadFactory"
  (newThread [_ runnable] "create a new thread"))

;;; In ClojureJVM, an IFn is a Runnable.  Not so in ClojureCLR.
;;; We take in something callable, but have to wrap it as a ThreadStart delegate.

(defn counted-thread-factory
  "Create a ThreadFactory that maintains a counter for naming Threads.
     name-format specifies thread names - use %d to include counter
     daemon is a flag for whether threads are daemons or not
     opts is an options map:
       init-fn - function to run when thread is created"
  ([name-format daemon]
    (counted-thread-factory name-format daemon nil))
  ([name-format daemon {:keys [init-fn] :as opts}]
   (let [counter (atom 0)]
     (reify
       ThreadFactory
       (newThread [_this runnable]
         (let [body (if init-fn
                      (gen-delegate ThreadStart [] (init-fn) (runnable))
                      (gen-delegate ThreadStart [] (runnable)))
               t (Thread. ^ThreadStart body)]
           (doto t
             (.set_Name (format name-format (swap! counter inc)))
             (.set_IsBackground daemon))))))))	


(defonce in-go-dispatch (ThreadLocal.))

#?(

:clj

(defmacro with-dispatch-thread-marking
  [& body]
  (if (Boolean/getBoolean "clojure.core.async.go-checking")
    `(try
       (.set in-go-dispatch true)
       ~@body
       (finally
         (.set in-go-dispatch false)))
    `(do ~@body)))
	
:cljr

;;; We have to replace the Boolean/getBoolean call with an explicit env var check

(defmacro with-dispatch-thread-marking
  [& body]
  (let [v (Environment/GetEnvironmentVariable "clojure.core.async.go-checking")
         b (boolean false)
		ok (Boolean/TryParse ^String v (by-ref b))
		do-check (if ok b false)]
    (if do-check
      `(try
         (.Set ^ThreadLocal in-go-dispatch true)
         ~@body
         (finally
           (.Set ^ThreadLocal in-go-dispatch false)))
      `(do ~@body))))
)


(defn in-dispatch-thread?
  "Returns true if the current thread is used for go block dispatch"
  []
  (boolean (.Value ^ThreadLocal in-go-dispatch)))                           ;;; .get

(defn check-blocking-in-dispatch
  "If the current thread is being used for go block dispatch, throw an exception"  
  []                                                                         ;
  (when (in-dispatch-thread?)
    (throw (#?(:clj IllegalStateException. :cljr InvalidOperationException.) "Invalid blocking call in dispatch thread"))))

(defn ex-handler
  "conveys given Exception to current thread's default uncaught handler. returns nil"
  [ex]
  ;;;(-> (Thread/currentThread)                                              ;;; no equivalent on CLR
  ;;;    .getUncaughtExceptionHandler
  ;;;    (.uncaughtException (Thread/currentThread) ex))
  ;;; CLEAT-167: report it like the JVM's default uncaught-exception handler instead of dropping it
  (binding [*out* *err*]
    (println (str "Exception in thread \"" (or (.Name (Thread/CurrentThread)) "async-dispatch") "\" " ex)))
  nil)
  
;;; The CLR does not have thread pools that can be created by the user.
;;; So we have to fake a lot of this.
;;; We could be creating our own threads and managing them, but we decided to use ThreadPool/QueueUserWorkItem for everything.
;;; Maybe someday we can manage our own threadpools.  Certainly there is example code out there.

#?(

:clj  

(defn- make-ctp-named
  [workload]
  (Executors/newCachedThreadPool (counted-thread-factory (str "async-" (name workload) "-%d") true)))

:cljr

;; We don't have an equivalent to ExecutorService, so we really can't do this.  
;; I do, however record the call to counted-thread-factory for potential future use.

(defn- make-ctp-named
  [workload]
  ;;;(Executors/newCachedThreadPool (counted-thread-factory (str "async-" (name workload) "-%d")  true)))
  nil)

)

(def virtual-threads-available?
  #?(:clj 
      (try
        (Class/forName "java.lang.Thread$Builder$OfVirtual")
        true
        (catch ClassNotFoundException _
          false))
	 :cljr false))

(defn- make-io-executor
  []
  #?(:clj 
     (if virtual-threads-available?
       (let [svt (.getDeclaredMethod Thread "startVirtualThread" (into-array Class [Runnable]))]
         (reify Executor
           (execute [_ r]
             (.invoke svt nil (object-array [r])))))
       (make-ctp-named :io))
	  :cljr (make-ctp-named :io)))


(defn ^:private create-default-executor
  [workload]
  (case workload
    :compute (make-ctp-named :compute)
    :io      (make-ctp-named :io)
    :mixed   (make-ctp-named :mixed)))

#?(

:clj

(def executor-for
  "Given a workload tag, returns an Executor instance and memoizes the result. By
  default, core.async will defer to a user factory (if provided via sys prop) or construct
  a specialized Executor instance for each tag :io, :compute, and :mixed. When
  given the tag :core-async-dispatch it will default to the executor service for :io."
  (memoize
   (fn ^Executor [workload]
     (let [sysprop-factory (when-let [esf (System/getProperty "clojure.core.async.executor-factory")]
                             (requiring-resolve (symbol esf)))
           sp-exec (and sysprop-factory (sysprop-factory workload))]
       (or sp-exec
           (if (= workload :core-async-dispatch)
             (executor-for :mixed)
             (create-default-executor workload)))))))
			 
:cljr

;;; we are only ever going to return one value, so no need to memoize
;;; Instead of return an ExectorService, we return a reification fo impl/ExectorService

(defn executor-for [workload]
   @executor)
   
)

#?(

:clj

(defn exec
  [^Runnable r workload]
  (let [^Executor e (executor-for workload)]
    (.execute e r)))
  
:cljr

(defn exec
    [r workload]                                         ;;; No equivalent type for Runnable, so we just take an IFn
  (let [e (executor-for workload)]
    (impl/exec e r)))
  
)


#?(

:clj

(defn run
  "Runs Runnable r on current thread when :on-caller? meta true, else in a thread pool thread."
  [^Runnable r]
  (if (-> r meta :on-caller?)
    (try (.run r) (catch Throwable t (ex-handler t)))
    (exec r :core-async-dispatch)))

:cljr

(defn run
  "Runs Runnable r on current thread when :on-caller? meta true, else in a thread pool thread."
  [r]  
  (if (-> r meta :on-caller?)
    (try (.invoke ^clojure.lang.IFn r) (catch Exception t (ex-handler t)))
    (exec r :core-async-dispatch)))
  )