(ns nrepl.middleware.print
  "Support for configurable printing. See the docstring of `wrap-print` and the
  Pretty Printing section of the Middleware documentation for more information."
  {:author "Michael Griffiths"
   :added  "0.6"}
  (:refer-clojure :exclude [print])
  (:require
   [nrepl.middleware :refer [set-descriptor!]]
   [nrepl.misc :as misc]
   [nrepl.transport :as transport]
   [nrepl.util.streams :as streams])
  (:import
   (System.IO StringWriter TextWriter)))

;; cleat: the writers come from nrepl.util.streams (the JVM version uses
;; nREPL's Java classes), and running out of quota is the ex-info that
;; streams/quota-exceeded makes rather than the nrepl.out.QuotaExceeded error.

(def ^:dynamic *print-fn*
  "Function to use for printing. Takes two arguments: `value`, the value to print,
  and `writer`, the `System.IO.TextWriter` to print on.

  Defaults to the equivalent of `clojure.core/pr`."
  @#'clojure.core/pr-on) ;; Private in clojure.core

(def ^:dynamic *stream?*
  "If logical true, the result of printing each value will be streamed to the
  client over one or more messages. Defaults to false."
  false)

(def ^:dynamic *buffer-size*
  "The size of the buffer to use when streaming results. Defaults to 1024."
  1024)

(def ^:dynamic *quota*
  "A hard limit on the number of bytes printed for each value. Defaults to nil. No
  limit will be used if not set."
  nil)

(defn- bound-configuration
  "Returns a map, suitable for merging into responses handled by this middleware,
  of the currently-bound dynamic vars used for configuration."
  []
  {::print-fn *print-fn*
   ::stream? *stream?*
   ::buffer-size *buffer-size*
   ::quota *quota*})

(def configuration-keys
  [::print-fn ::stream? ::buffer-size ::quota ::keys])

(defn with-quota-bound-writer
  "Returns a `System.IO.TextWriter` that wraps `writer` and throws once it has
  written more than `quota` characters (see `nrepl.util.streams/quota-exceeded?`)."
  ^TextWriter
  [^TextWriter writer quota]
  (if quota
    (streams/quota-bound-writer writer quota)
    writer))

(defn replying-PrintWriter
  "Returns a `System.IO.TextWriter` suitable for binding as `*out*` or `*err*`. All
  of the content written to that writer will be sent as messages on the
  transport of `msg`, keyed by `key`."
  ^TextWriter
  [key msg {:keys [::buffer-size ::quota]}]
  (-> (streams/callback-writer #(transport/respond-to msg key %)
                               (or buffer-size 1024))
      ;; Output goes out when it's flushed, as on the JVM, where an
      ;; OutputStreamWriter buffers in front of the callback stream.
      (streams/buffered-writer 8192)
      (with-quota-bound-writer quota)))

(defn- send-streamed
  [{:keys [transport] :as msg}
   resp
   {:keys [::print-fn ::keys] :as opts}]
  ;; Iterator is used instead of reduce for cleaner stacktrace if an exception
  ;; gets thrown during printing.
  (doseq [key keys]
    (let [value (get resp key)
          ^TextWriter writer (replying-PrintWriter key msg opts)]
      (try (try (print-fn value writer)
                (finally (.Flush writer)))
           (catch Exception e
             (if (streams/quota-exceeded? e)
               (transport/respond-to msg :status ::truncated)
               (throw e))))))
  (transport/send transport (apply dissoc resp keys)))

(defn- send-nonstreamed
  [{:keys [transport]}
   resp
   {:keys [::print-fn ::quota ::keys]}]
  ;; Iterator is used instead of reduce for cleaner stacktrace if an exception
  ;; gets thrown during printing.
  (loop [resp resp
         keys (seq keys)]
    (if keys
      (let [key (first keys)
            value (get resp key)
            writer (with-quota-bound-writer (StringWriter.) quota)
            truncated? (volatile! false)]
        (try (print-fn value writer)
             (catch Exception e
               (if (streams/quota-exceeded? e)
                 (vreset! truncated? true)
                 (throw e))))
        (recur (cond-> (assoc resp key (str writer))
                 @truncated? (update ::truncated-keys (fnil conj []) key))
               (next keys)))

      (transport/send transport (cond-> resp
                                  (::truncated-keys resp)
                                  (update :status #(set (conj % ::truncated))))))))

(defn- printing-transport
  [{:keys [transport] :as msg}]
  (reify transport/Transport
    (recv [_this]
      (transport/recv transport))
    (recv [_this timeout]
      (transport/recv transport timeout))
    (send [this resp]
      (let [resp-pr (::print-fn resp)
            ;; Request config has priority over response config, but if the
            ;; request didn't have explicit ::print set, prefer ::print-fn from
            ;; the response.
            opts (cond-> (merge (bound-configuration) resp msg)
                   (and resp-pr (nil? (::print msg))) (assoc ::print-fn resp-pr))
            resp (apply dissoc resp configuration-keys)]
        (if (::stream? opts)
          (send-streamed msg resp opts)
          (send-nonstreamed msg resp opts)))
      this)))

(defn- resolve-print
  [{:keys [::print] :as msg}]
  (when-let [var-sym (some-> print (symbol))]
    (let [print-var (try
                      (requiring-resolve var-sym)
                      ;; The symbol comes from the client, so a missing
                      ;; namespace or an unqualified name is a user error.
                      (catch Exception _ nil))]
      (when-not print-var
        (transport/respond-to msg {::error (str "Couldn't resolve var " var-sym)
                                   :status ::error}))
      print-var)))

(defn- booleanize-bencode-val [m key]
  (if (contains? m key)
    ;; As a convention, empty list is treated as logical false.
    (update m key #(if (= % []) false (boolean %)))
    m))

(defn wrap-print
  "Middleware that provides printing functionality to other middlewares.

  Returns a handler which transforms any slots specified by
  `:nrepl.middleware.print/keys` in messages sent via the request's transport to
  strings using the provided printing function and options.

  Supports the following options:

  * `::print` – a fully-qualified symbol naming a var whose function to use for
  printing. Must point to a function with signature [value writer options].

  * `::options` – a map of options to pass to the printing function. Defaults to
  `nil`.

  * `::print-fn` – the function to use for printing. In requests, will be
  resolved from the above two options (if provided). Defaults to the equivalent
  of `clojure.core/pr`. Must have signature [writer options].

  * `::stream?` – if logical true, the result of printing each value will be
  streamed to the client over one or more messages.

  * `::buffer-size` – the size of the buffer to use when streaming results.
  Defaults to 1024.

  * `::quota` – a hard limit on the number of bytes printed for each value.

  * `::keys` – a seq of the keys in the response whose values should be printed.

  The options may be specified in either the request or the responses sent on
  its transport. If any options are specified in both, those in the request will
  be preferred."
  [handler]
  (fn [{:keys [::options] :as msg}]
    (let [print-var (resolve-print msg)
          print-fn (if print-var
                     (fn [value writer]
                       (print-var value writer options))
                     (misc/resolve-in-session msg *print-fn*))
          msg (-> msg
                  (assoc ::print-fn print-fn)
                  (booleanize-bencode-val ::stream?))]
      (handler (assoc msg :transport (printing-transport msg))))))

(set-descriptor! #'wrap-print {:requires #{"clone"}
                               :expects #{}
                               :handles {}
                               :session-dynvars #{#'*print-fn* #'*stream?*
                                                  #'*buffer-size* #'*quota*}})

(def wrap-print-optional-arguments
  {"nrepl.middleware.print/print" "A fully-qualified symbol naming a var whose function to use for printing. Must point to a function with signature [value writer options]."
   "nrepl.middleware.print/options" "A map of options to pass to the printing function. Defaults to `nil`."
   "nrepl.middleware.print/stream?" "If logical true, the result of printing each value will be streamed to the client over one or more messages."
   "nrepl.middleware.print/buffer-size" "The size of the buffer to use when streaming results. Defaults to 1024."
   "nrepl.middleware.print/quota" "A hard limit on the number of bytes printed for each value."
   "nrepl.middleware.print/keys" "A seq of the keys in the response whose values should be printed."})
