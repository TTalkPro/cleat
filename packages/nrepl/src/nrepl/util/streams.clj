(ns nrepl.util.streams
  "The readers and writers nREPL's middleware binds to *in*, *out* and *err*.

  cleat: added in the CLR port. On the JVM these are Java classes shipped with
  nREPL (nrepl.in.QueuePollingReader, nrepl.out.CallbackBufferedOutputStream,
  QuotaBoundWriter, QuotaExceeded and TeeOutputStream). Here they are proxies
  of System.IO.TextReader and TextWriter. CLR text writers take characters, not
  bytes, so there's no UTF-8 tearing to guard against. A proxy method gets all
  the overloads of its name and arity, hence the dispatch on argument types in
  `Write`."
  (:require [nrepl.util.queue :as q]
            [nrepl.util.threading :as threading])
  (:import (System.IO TextReader TextWriter)
           (System.Text Encoding StringBuilder)
           (System.Threading ThreadInterruptedException)))

(defn- append!
  "Appends what TextWriter.Write's one-argument overloads take to sb."
  [^StringBuilder sb x]
  (cond
    (string? x) (.Append sb ^String x)
    (char? x) (.Append sb (char x))
    (instance? (class (char-array 0)) x) (.Append sb ^chars x)
    (nil? x) sb
    (instance? Boolean x) (.Append sb (if x "True" "False"))
    :else (.Append sb (str x))))

(defn- written
  "The text a call to one of TextWriter.Write's overloads writes: Write(x),
  Write(char[], index, count) or Write(format, args...)."
  ([x] (str (append! (StringBuilder.) x)))
  ([a b] (if (string? a) (String/Format ^String a b) (str a b)))
  ([a b c] (if (string? a)
             (String/Format ^String a b c)
             (String. ^chars a (int b) (int c)))))

;;;; Quota

(defn quota-exceeded
  "The exception a quota-bound writer throws once its quota is used up."
  []
  (ex-info "nREPL print quota exceeded" {::quota-exceeded true}))

(defn quota-exceeded?
  "Whether e is, or was caused by, the exception `quota-exceeded` makes."
  [e]
  (loop [e e]
    (cond
      (nil? e) false
      (::quota-exceeded (ex-data e)) true
      :else (recur (ex-cause e)))))

(defn quota-bound-writer
  "A writer passing what it's given on to writer until quota characters have
  been written, then throwing what `quota-exceeded` makes."
  ^TextWriter [^TextWriter writer quota]
  (when-not (pos? quota)
    (throw (ArgumentException. (str "Invalid quota: " quota))))
  (let [remaining (atom (long quota))
        lock (Object.)
        write (fn [^String s]
                (locking lock
                  (let [n (.Length s)
                        left @remaining
                        too-big? (> n left)
                        n (min n left)]
                    (when (pos? n)
                      (.Write writer (subs s 0 n))
                      (swap! remaining - n))
                    (when too-big?
                      (throw (quota-exceeded))))))]
    (proxy [TextWriter] []
      (get_Encoding [] (.Encoding writer))
      (Write
        ([x] (write (written x)))
        ([a b] (write (written a b)))
        ([a b c] (write (written a b c))))
      (Flush [] (.Flush writer))
      (ToString [] (str writer)))))

;;;; Callback writer

(defn callback-writer
  "A writer handing what's written to it to (callback text) in chunks: the
  lines it has once a newline comes, everything once buffer-size characters
  pile up, and the rest on Flush and Dispose. Writing on a thread nREPL has
  interrupted throws ThreadInterruptedException, so that code printing in a
  loop stops (see nrepl.util.threading/interrupted?)."
  ^TextWriter [callback buffer-size]
  (let [buffer-size (long buffer-size)
        sb (StringBuilder.)
        lock (Object.)
        maybe-flush (fn [force?]
                      (let [size (.Length sb)]
                        (when (pos? size)
                          (let [content (str sb)
                                n (if (or force? (>= size buffer-size))
                                    size
                                    (inc (.LastIndexOf content \newline)))
                                ;; Don't tear a surrogate pair apart, unless
                                ;; flushing it all.
                                n (if (and (not force?) (pos? n)
                                           (Char/IsHighSurrogate (.get_Chars content (int (dec n)))))
                                    (dec n)
                                    n)]
                            (when (pos? n)
                              (.Clear sb)
                              (when (< n size)
                                (.Append sb (subs content n)))
                              (callback (subs content 0 n)))))))
        write (fn [^String s]
                (when (threading/interrupted?)
                  (throw (ThreadInterruptedException. "The evaluation was interrupted")))
                (locking lock
                  ;; Fill the buffer up to buffer-size at a time, like the
                  ;; JVM's CallbackBufferedOutputStream, so streamed chunks
                  ;; come out buffer-size long.
                  (let [len (.Length s)]
                    (loop [off 0]
                      (when (< off len)
                        (let [n (-> (- buffer-size (.Length sb)) (max 1) (min (- len off)))
                              ;; Keep a surrogate pair together.
                              n (if (and (< (+ off n) len)
                                         (Char/IsHighSurrogate (.get_Chars s (int (+ off n -1)))))
                                  (inc n)
                                  n)]
                          (.Append sb s (int off) (int n))
                          (when (or (>= (.Length sb) buffer-size)
                                    (>= (.IndexOf s \newline (int off) (int n)) 0))
                            (maybe-flush false))
                          (recur (+ off n))))))))]
    (proxy [TextWriter] []
      (get_Encoding [] Encoding/UTF8)
      (Write
        ([x] (write (written x)))
        ([a b] (write (written a b)))
        ([a b c] (write (written a b c))))
      (Flush [] (locking lock (maybe-flush true)))
      (Dispose [disposing] (locking lock (maybe-flush true))))))

;;;; Buffered writer

(defn buffered-writer
  "A writer holding on to what's written to it until it's flushed or
  buffer-size characters have piled up, then writing it all to writer. This is
  the buffering the JVM's OutputStreamWriter does in front of nREPL's callback
  stream, which decides how output is cut into messages: what's printed
  between two flushes goes out together."
  ^TextWriter [^TextWriter writer buffer-size]
  (let [buffer-size (long buffer-size)
        sb (StringBuilder.)
        lock (Object.)
        drain (fn []
                (when (pos? (.Length sb))
                  (let [content (str sb)]
                    (.Clear sb)
                    (.Write writer content))))
        write (fn [^String s]
                (when (threading/interrupted?)
                  (throw (ThreadInterruptedException. "The evaluation was interrupted")))
                (locking lock
                  (.Append sb s)
                  (when (>= (.Length sb) buffer-size)
                    (drain))))]
    (proxy [TextWriter] []
      (get_Encoding [] (.Encoding writer))
      (Write
        ([x] (write (written x)))
        ([a b] (write (written a b)))
        ([a b c] (write (written a b c))))
      (Flush [] (locking lock (drain) (.Flush writer)))
      (Dispose [disposing] (locking lock (drain) (.Flush writer))))))

;;;; Tee writer

(defn tee-writer
  "A writer writing everything to both out1 and out2."
  ^TextWriter [^TextWriter out1 ^TextWriter out2]
  (let [flushing (atom false)
        write (fn [^String s]
                (.Write out1 s)
                (.Write out2 s))]
    (proxy [TextWriter] []
      (get_Encoding [] (.Encoding out1))
      (Write
        ([x] (write (written x)))
        ([a b] (write (written a b)))
        ([a b c] (write (written a b c))))
      (Flush []
        ;; Prevent recursive flushing loops.
        (when (compare-and-set! flushing false true)
          (try
            (.Flush out1)
            (.Flush out2)
            (finally
              (reset! flushing false))))))))

;;;; Queue polling reader

(def ^:private eof-marker (Object.))

(defn queue-polling-reader
  "A reader of the input that's added to it, for binding *in* to (wrapped in a
  LineNumberingTextReader). A read finding nothing to read calls
  (send-need-input-request) and waits for input. Returns a map of the :reader,
  and functions to :add-input (a string), :add-eof, :clear the input and tell
  whether it's :ready? to be read without waiting."
  [send-need-input-request]
  (let [queue (q/linked-queue)
        current (volatile! nil)
        index (volatile! 0)
        poll-input-char (fn []
                          (loop []
                            (let [^String input @current]
                              (cond
                                ;; Intentionally don't clear the EOF marker, let
                                ;; the caller do it.
                                (identical? input eof-marker) -2
                                (and input (< @index (.Length input))) (int (.get_Chars input (int @index)))
                                :else (do (vreset! index 0)
                                          (vreset! current (q/poll! queue))
                                          (if (nil? @current) -1 (recur)))))))
        read-input-char (fn []
                          (loop []
                            (let [c (poll-input-char)]
                              (case (int c)
                                -2 (do (vreset! current nil) ; Clear EOF marker
                                       -1)
                                -1 (do (send-need-input-request)
                                       (if-let [input (try
                                                        (q/take! queue)
                                                        (catch ThreadInterruptedException _ nil))]
                                         (do (vreset! current input)
                                             (recur))
                                         -1))
                                (do (vswap! index inc) ; Advance read pointer
                                    c)))))
        reader (proxy [TextReader] []
                 (Read
                   ([] (read-input-char))
                   ([buf off len]
                    (let [^chars buf buf
                          off (int off)
                          len (int len)]
                      (if (zero? len)
                        0
                        ;; First char taken from an empty queue will cause
                        ;; `need-input` message to be sent to the client.
                        (let [first-char (read-input-char)]
                          (if (neg? first-char)
                            0
                            (do (aset buf off (char first-char))
                                ;; For subsequent chars, only poll the queue for
                                ;; data already there, and when it's empty,
                                ;; return the number of chars read.
                                (loop [i 1]
                                  (if (< i len)
                                    ;; Peek, so that an EOF marker stays in place
                                    ;; and is reported by the next read instead of
                                    ;; being swallowed here.
                                    (let [c (poll-input-char)]
                                      (if (neg? c)
                                        i
                                        (do (vswap! index inc)
                                            (aset buf (+ off i) (char c))
                                            (recur (inc i)))))
                                    i)))))))))
                 (Peek []
                   (let [c (poll-input-char)]
                     (if (neg? c) -1 c)))
                 (Dispose [disposing]
                   (q/clear! queue)))]
    {:reader reader
     :add-input #(q/put! queue %)
     :add-eof #(q/put! queue eof-marker)
     :clear #(q/clear! queue)
     :ready? #(>= (poll-input-char) 0)}))
