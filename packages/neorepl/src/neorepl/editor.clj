(ns neorepl.editor
  "The REPL's line editor. This is the only namespace that touches the
  terminal, so the rest of neorepl doesn't care how lines get read.

  cleat: the JVM version is built on JLine. This one is written on
  System.Console: keys come from Console/ReadKey, output is ANSI escape
  sequences, Ctrl-C is a key while a line is being read and Console's
  CancelKeyPress otherwise. It does what neorepl asks of JLine: multi-line
  input that Enter only submits once its forms are complete, highlighting,
  TAB completion, a tail tip after the cursor (eldoc), history kept in a file
  (Up/Down, Ctrl-R), output printed above the line being edited, C-x C-d for
  documentation, and Emacs-style editing keys."
  (:refer-clojure :exclude [read-line])
  (:require [clojure.string :as str]
            [neorepl.lexer :as lexer]
            [nrepl.util.queue :as q])
  (:import (System Console ConsoleKey ConsoleKeyInfo ConsoleModifiers)
           (System.IO File Path TextReader TextWriter)
           (System.Text StringBuilder)))

;;;; Styles

(def ^:private colors
  "ANSI styles for lexer token types, and for :err output and :notice
  messages."
  (let [faint "2"
        green "32"
        magenta "35"
        yellow "33"]
    {:comment faint
     :discard faint
     :string green
     :open-string green
     :regex green
     :char green
     :keyword "36"
     :number magenta
     :constant magenta
     :special "1;34"
     :reader-macro yellow
     :macro-char yellow
     :err "31"
     :notice faint
     :tip faint}))

(def ^:private specials
  "Symbols highlighted as :special: the special forms and the defining and
  binding macros that read like them."
  #{"def" "defn" "defn-" "defmacro" "defmulti" "defmethod" "defprotocol" "defrecord"
    "deftype" "definterface" "defonce" "fn" "fn*" "let" "letfn" "loop" "recur" "if"
    "if-let" "if-some" "if-not" "when" "when-let" "when-some" "when-not" "do" "cond"
    "condp" "case" "ns" "quote" "try" "catch" "finally" "throw" "var" "new" "set!"
    "binding" "doseq" "dotimes" "for"})

(defn- styled
  "text in the ANSI style called k in styles, as is when there's none."
  [styles k ^String text]
  (if-let [sgr (and (seq text) (styles k))]
    (str "\u001b[" sgr "m" text "\u001b[0m")
    text))

(defn- segments
  "code as [style text] segments, its tokens styled."
  [styles ^String code]
  (let [[segs end] (reduce (fn [[segs pos] {:keys [type start end]}]
                             (let [text (subs code start end)]
                               [(conj segs
                                      [nil (subs code pos start)]
                                      [(if (and (= :symbol type) (specials text)) :special type) text])
                                end]))
                           [[] 0]
                           (lexer/tokenize code))]
    (conj segs [nil (subs code end)])))

(defn- highlight
  "code as text with ANSI styles."
  [styles code]
  (apply str (map (fn [[k text]] (styled styles k text)) (segments styles code))))

(defn- highlighted-lines
  "code's lines, each highlighted on its own, so that styles don't run across
  a line break (where the prompt of the next line goes)."
  [styles ^String code]
  (let [lines (atom [[]])]
    (doseq [[k ^String text] (segments styles code)
            :let [parts (str/split text #"\n" -1)]]
      (swap! lines (fn [ls]
                     (reduce (fn [ls part] (conj ls [[k part]]))
                             (update ls (dec (count ls)) conj [k (first parts)])
                             (rest parts)))))
    (mapv (fn [segs] (apply str (map (fn [[k text]] (styled styles k text)) segs))) @lines)))

;;;; Display width

(defn- char-width
  "Columns a character takes on a terminal: 2 for East Asian wide ones, 0
  for combining marks and the low half of a surrogate pair, 1 otherwise."
  [c]
  (let [c (int c)]
    (cond
      (<= 0xDC00 c 0xDFFF) 0
      (<= 0xD800 c 0xDBFF) 2
      (<= 0x0300 c 0x036F) 0
      (or (<= 0x1100 c 0x115F) (<= 0x2E80 c 0xA4CF) (<= 0xAC00 c 0xD7A3) (<= 0xF900 c 0xFAFF)
          (<= 0xFE30 c 0xFE4F) (<= 0xFF00 c 0xFF60) (<= 0xFFE0 c 0xFFE6)) 2
      :else 1)))

(defn- text-width
  "Columns text takes, ANSI escape sequences not counted."
  [^String text]
  (let [plain (str/replace text #"\u001b\[[0-9;]*[A-Za-z]" "")]
    (reduce + 0 (map char-width plain))))

;;;; Terminals

(defprotocol Terminal
  (read-key [t] "The next key as a map with the :key (a character or a keyword such as :enter or :up) and :ctrl? and :alt?, or nil at the end of the input. A bracketed paste is {:key :paste :text text}.")
  (poll-key [t ms] "The next key, as read-key has it, when one comes within ms milliseconds, or ::none.")
  (write [t text] "Writes text.")
  (columns [t] "The width in columns.")
  (set-raw-ctrl-c [t on?] "Whether Ctrl-C is read as a key (true) or delivered as a signal.")
  (on-signal [t f] "Has (f) called on Ctrl-C while it's delivered as a signal, instead of what f replaces.")
  (close-terminal [t]))

(def ^:private control-keys
  {(char 9) :tab, (char 13) :enter, (char 10) :enter, (char 8) :backspace, (char 127) :backspace, (char 27) :escape})

(defn- char-key
  "The key a character typed is."
  [c]
  (let [c (char c)]
    (cond
      (control-keys c) {:key (control-keys c)}
      (< (int c) 32) {:key (char (+ (int c) 96)) :ctrl? true}
      :else {:key c})))

(def ^:private console-keys
  {ConsoleKey/UpArrow :up, ConsoleKey/DownArrow :down, ConsoleKey/LeftArrow :left,
   ConsoleKey/RightArrow :right, ConsoleKey/Home :home, ConsoleKey/End :end,
   ConsoleKey/Delete :delete, ConsoleKey/Backspace :backspace, ConsoleKey/Enter :enter,
   ConsoleKey/Tab :tab, ConsoleKey/Escape :escape, ConsoleKey/PageUp :page-up,
   ConsoleKey/PageDown :page-down})

(defn- console-key
  "The key a ConsoleKeyInfo is."
  [^ConsoleKeyInfo k]
  (let [mods (.Modifiers k)
        ctrl? (= ConsoleModifiers/Control (enum-and mods ConsoleModifiers/Control))
        alt? (= ConsoleModifiers/Alt (enum-and mods ConsoleModifiers/Alt))
        c (.KeyChar k)]
    (cond
      (console-keys (.Key k)) {:key (console-keys (.Key k)) :ctrl? ctrl? :alt? alt?}
      (and alt? (Char/IsLetter c)) {:key (Char/ToLower c) :alt? true}
      (and alt? (= (int c) 127)) {:key :backspace :alt? true}
      (and (zero? (int c)) ctrl?) {:key (Char/ToLower (char (+ 64 (int (.Key k))))) :ctrl? true}
      (zero? (int c)) {:key :unknown}
      :else (assoc (char-key c) :alt? alt?))))

(deftype ConsoleTerminal []
  Terminal
  (read-key [_]
    (console-key (Console/ReadKey true)))
  (poll-key [this ms]
    (let [deadline (+ (Environment/TickCount64) ms)]
      (loop []
        (cond
          (Console/KeyAvailable) (read-key this)
          (< (Environment/TickCount64) deadline) (do (System.Threading.Thread/Sleep 5) (recur))
          :else ::none))))
  (write [_ text]
    (.Write Console/Out ^String text)
    (.Flush Console/Out))
  (columns [_]
    (let [w (try Console/WindowWidth (catch Exception _ 0))]
      (if (pos? w) w 80)))
  (set-raw-ctrl-c [_ on?]
    (try (Console/set_TreatControlCAsInput (boolean on?)) (catch Exception _ nil)))
  (on-signal [_ f]
    (let [h (gen-delegate System.ConsoleCancelEventHandler [_ e]
              (.set_Cancel ^System.ConsoleCancelEventArgs e true)
              ;; Not on the thread that delivers the signal.
              (future (f)))]
      (Console/add_CancelKeyPress h)
      #(Console/remove_CancelKeyPress h)))
  (close-terminal [_]
    (set-raw-ctrl-c _ false)))

(defn- read-paste
  "The text of a bracketed paste, read from next-char up to the ESC [ 201 ~
  that ends it."
  [next-char]
  (let [end "\u001b[201~"]
    (loop [sb (StringBuilder.)]
      (let [c (next-char)]
        (if (nil? c)
          (str sb)
          (let [sb (.Append sb (char c))
                n (.Length sb)]
            (if (and (>= n (count end)) (= end (.ToString sb (- n (count end)) (count end))))
              (.ToString sb 0 (- n (count end)))
              (recur sb))))))))

(defn- decode-escape
  "The key the escape sequence after ESC is, reading it from next-char."
  [next-char]
  (let [c (next-char)]
    (cond
      (nil? c) {:key :escape}
      (= \[ c) (loop [params ""]
                 (let [c (next-char)]
                   (cond
                     (nil? c) {:key :escape}
                     (Char/IsDigit c) (recur (str params c))
                     (= \; c) (recur (str params c))
                     :else (case c
                             \A {:key :up} \B {:key :down} \C {:key :right} \D {:key :left}
                             \H {:key :home} \F {:key :end}
                             \~ (case params
                                  ("1" "7") {:key :home} ("4" "8") {:key :end}
                                  "3" {:key :delete} "5" {:key :page-up} "6" {:key :page-down}
                                  "200" {:key :paste :text (str/replace (read-paste next-char) "\r" "")}
                                  {:key :unknown})
                             {:key :unknown}))))
      (= \O c) (case (next-char)
                 \H {:key :home} \F {:key :end} \A {:key :up} \B {:key :down}
                 \C {:key :right} \D {:key :left} {:key :unknown})
      (= (char 127) c) {:key :backspace :alt? true}
      :else (assoc (char-key c) :alt? true))))

(defn- decode-key
  "The next key typed, read from next-char, or nil at the end of the input."
  [next-char]
  (let [c (next-char)]
    (cond
      (nil? c) nil
      (= (char 27) c) (decode-escape next-char)
      :else (char-key c))))

;; Keys are decoded on a thread of their own into keys, so that poll-key can
;; wait for one a little while. Ctrl-C typed while it's not read as a key calls
;; the signal handler, the way a terminal turns it into SIGINT.
(deftype StreamTerminal [keys ^TextWriter output width raw? handler]
  Terminal
  (read-key [_]
    (let [k (q/take! keys)]
      (when-not (= ::eof k) k)))
  (poll-key [_ ms]
    (let [k (q/poll! keys ms)]
      (cond
        (nil? k) ::none
        (= ::eof k) (do (q/put! keys ::eof) nil)
        :else k)))
  (write [_ text]
    (.Write output ^String text)
    (.Flush output))
  (columns [_] width)
  (set-raw-ctrl-c [_ on?] (reset! raw? on?))
  (on-signal [_ f]
    (reset! handler f)
    #(reset! handler nil))
  (close-terminal [_]))

(defn- stream-terminal
  "A terminal reading keys typed from the TextReader input and writing to the
  TextWriter output, width columns wide."
  [^TextReader input output width]
  (let [keys (q/linked-queue)
        raw? (atom false)
        handler (atom nil)
        next-char #(let [c (.Read input)] (when-not (neg? c) (char c)))]
    (doto (System.Threading.Thread.
           ^System.Threading.ThreadStart
           (gen-delegate System.Threading.ThreadStart []
             (try
               (loop []
                 (if-let [k (decode-key next-char)]
                   (do (if (and (= \c (:key k)) (:ctrl? k) (not @raw?) @handler)
                         (let [f @handler] (future (f)))
                         (q/put! keys k))
                       (recur))
                   (q/put! keys ::eof)))
               (catch Exception _ (q/put! keys ::eof)))))
      (.set_IsBackground true)
      (.Start))
    (StreamTerminal. keys output width raw? handler)))

(defn- maybe-terminal?
  "Whether there's a terminal worth editing on: standard input and output
  aren't redirected and it's not a dumb one, as in Emacs' M-x shell."
  []
  (boolean (and (not Console/IsInputRedirected)
                (not Console/IsOutputRedirected)
                (not= "dumb" (System.Environment/GetEnvironmentVariable "TERM")))))

;;;; History

(def ^:private max-history 1000)

(defn- unescape-history [^String line]
  (str/replace line #"\\(.)" (fn [[_ c]] (if (= "n" c) "\n" c))))

(defn- escape-history [^String entry]
  (-> entry (str/replace "\\" "\\\\") (str/replace "\n" "\\n")))

(defn- load-history [file]
  (if (and file (File/Exists (str file)))
    (try
      (vec (take-last max-history (map unescape-history (remove str/blank? (File/ReadAllLines (str file))))))
      (catch Exception _ []))
    []))

(defn- save-history [file entries]
  (when file
    (try
      (System.IO.Directory/CreateDirectory (Path/GetDirectoryName (Path/GetFullPath (str file))))
      (File/WriteAllLines (str file) ^"System.String[]" (into-array String (map escape-history entries)))
      (catch Exception _ nil))))

;;;; The editor

(defn open
  "A line editor on the terminal, or nil when there's no terminal worth
  editing on: when standard input or output isn't one, or it's a dumb one, as
  in Emacs' M-x shell. opts:

    :history-file  where to keep the history
    :color?        false for no colors
    :incomplete?   (incomplete? text) for whether Enter goes on to another
                   line (lexer/incomplete? by default)
    :complete      (complete prefix) for the completions of prefix, as
                   features/complete has them
    :eldoc         (eldoc text cursor) for the text to show after the
                   cursor while typing, like the arglists of the function
                   being called
    :eldoc?        false to show nothing there after all
    :doc           (doc text cursor) for the documentation C-x C-d shows
    :input, :output, :width and :type
                   a terminal reading keys typed from the TextReader input
                   and writing to the TextWriter output, for tests; one of
                   :type \"dumb\" gets no editor, as a dumb terminal doesn't"
  [{:keys [history-file color? incomplete? complete eldoc eldoc? doc input output width type]
    :or {color? true incomplete? lexer/incomplete? eldoc? true}}]
  (when (and (if input (not= "dumb" type) (maybe-terminal?)))
    (let [safely (fn [f] (when f (fn [& args] (try (apply f args) (catch Exception _ nil)))))]
      {:terminal (if input (stream-terminal input output (or width 80)) (ConsoleTerminal.))
       :styles (if color? colors {})
       :incomplete? incomplete?
       :complete (safely complete)
       :eldoc (when eldoc? (safely eldoc))
       :doc (safely doc)
       :history-file history-file
       :history (atom (load-history history-file))
       :lock (Object.)
       :suspended? (atom false)
       ;; The line being read: nil when none is, or {:buffer :cursor :prompt
       ;; :raw? ...}, and how many rows down from the edit area's top the
       ;; terminal's cursor is.
       :state (atom nil)
       :cursor-row (atom 0)
       ;; What's been printed of the line the cursor's on, when not reading.
       :line (atom "")
       ;; Output that came while reading, short of the newline ending it.
       :above (atom "")
       :interrupt-handler (atom nil)
       ;; A key read ahead, to be read again.
       :pushback (atom nil)})))

(defn close [{:keys [terminal interrupt-handler]}]
  (when-let [remove-handler @interrupt-handler]
    (remove-handler))
  (close-terminal terminal))

(defn width
  "The terminal's width in columns."
  [{:keys [terminal]}]
  (columns terminal))

(defn- secondary-prompt
  "The prompt of a continuation line: #_=> padded to line up with prompt."
  [^String prompt]
  (let [tail " #_=> "]
    (str (apply str (repeat (max 0 (- (text-width prompt) (count tail))) \space)) tail)))

(defn- rows-for
  "Rows a line w columns wide takes on a terminal width columns wide."
  [w width]
  (if (pos? w) (inc (quot (dec w) width)) 1))

(defn- render!
  "Redraws the line being read: the prompt and the buffer highlighted, the
  eldoc tail tip when the cursor's at the end, and the cursor where it
  belongs. Called with the lock held."
  [{:keys [terminal styles eldoc state cursor-row]}]
  (when-let [{:keys [^String buffer cursor prompt raw? search]} @state]
    (let [width (columns terminal)
          prompt (if search (str "(reverse-i-search)`" (:query search) "': ") prompt)
          lines (if raw? (str/split buffer #"\n" -1) (highlighted-lines styles buffer))
          plain-lines (str/split buffer #"\n" -1)
          prefixes (cons prompt (repeat (secondary-prompt prompt)))
          ;; Where the cursor is: its line and the text before it there.
          before (subs buffer 0 cursor)
          cursor-line (count (re-seq #"\n" before))
          cursor-text (subs before (inc (or (str/last-index-of before "\n") -1)))
          tip (when (and eldoc (not raw?) (not search) (= cursor (count buffer)))
                (some-> (eldoc buffer cursor) str not-empty))
          widths (mapv (fn [p l] (+ (text-width p) (text-width l))) prefixes plain-lines)
          last-i (dec (count lines))
          ;; The tail tip goes on the last row, cut to what fits there.
          tip (when tip
                (let [w (peek widths)
                      room (- width (inc (rem w width)))]
                  (when (pos? room)
                    (let [t (str/replace tip #"\s*\n\s*" " ")]
                      (subs t 0 (min (count t) room))))))
          cw (+ (text-width (nth prefixes cursor-line)) (text-width cursor-text))
          row-counts (mapv #(rows-for % width) widths)
          ;; The cursor right after a line that fills its last row exactly
          ;; goes on a row of its own.
          row-counts (update row-counts cursor-line max (inc (quot cw width)))
          cursor-target-row (+ (reduce + 0 (subvec row-counts 0 cursor-line)) (quot cw width))
          cursor-col (rem cw width)
          end-row (dec (reduce + 0 row-counts))
          sb (StringBuilder.)]
      ;; Back to the top of the edit area and clear it.
      (.Append sb "\r")
      (when (pos? @cursor-row) (.Append sb (str "\u001b[" @cursor-row "A")))
      (.Append sb "\u001b[J")
      (doseq [[i [p l]] (map-indexed vector (map vector prefixes lines))]
        (when (pos? i) (.Append sb "\r\n"))
        (.Append sb ^String p)
        (.Append sb ^String l)
        (when (= i last-i)
          (when tip (.Append sb (str " " (styled styles :tip tip))))))
      ;; From the start of the last row, to the cursor.
      (let [last-line-rows (rows-for (+ (peek widths) (if tip (inc (count tip)) 0)) width)
            drawn-end-row (+ (reduce + 0 (pop row-counts)) (dec last-line-rows))]
        (if (> end-row drawn-end-row)
          (.Append sb "\r\n")
          (.Append sb "\r"))
        (let [up (- end-row cursor-target-row)]
          (when (pos? up) (.Append sb (str "\u001b[" up "A"))))
        (when (pos? cursor-col) (.Append sb (str "\u001b[" cursor-col "C"))))
      (write terminal (str sb))
      (reset! cursor-row cursor-target-row))))

(defn- clear-edit-area!
  "Erases the line being read, leaving the cursor where it started. Called
  with the lock held."
  [{:keys [terminal cursor-row]}]
  (write terminal (str "\r" (when (pos? @cursor-row) (str "\u001b[" @cursor-row "A")) "\u001b[J"))
  (reset! cursor-row 0))

(defn- emit
  "Prints text right away, or above the prompt while a line is being read.
  Printing above the prompt only prints whole lines; the rest of a line waits
  for the newline ending it."
  [{:keys [terminal lock state line above] :as editor} ^String text]
  (locking lock
    (let [text (str @above text)]
      (if @state
        (if-let [end (str/last-index-of text "\n")]
          (do (clear-edit-area! editor)
              (write terminal (str/replace (subs text 0 (inc end)) "\n" "\r\n"))
              (reset! above (subs text (inc end)))
              (render! editor))
          (reset! above text))
        (do (reset! above "")
            (when (seq text)
              (write terminal (str/replace text "\n" "\r\n"))
              (swap! line #(if-let [end (str/last-index-of text "\n")]
                             (subs text (inc end))
                             (str % text)))))))))

(defn print-text
  "Prints text in the style k names, such as :err, or nil for none."
  [{:keys [styles] :as editor} k text]
  ;; Styled line by line, so a style doesn't run into what's printed above
  ;; the prompt later.
  (emit editor (str/join "\n" (map #(styled styles k %) (str/split (str text) #"\n" -1)))))

(defn print-code
  "Prints code highlighted."
  [{:keys [styles] :as editor} code]
  (emit editor (str/join "\n" (highlighted-lines styles (str code)))))

(defn fresh-line
  "Ends the line output left unfinished, if any."
  [{:keys [line] :as editor}]
  (when (seq (str/replace @line #"\u001b\[[0-9;]*[A-Za-z]" ""))
    (print-text editor nil "\n")))

(defn suspended
  "Calls f with the terminal let go of, for another program to have it, like
  an editor, and takes it back after. Output waits till then."
  [{:keys [lock suspended?]} f]
  (locking lock
    (reset! suspended? true)
    (try
      (f)
      (finally
        (reset! suspended? false)))))

(defn on-interrupt
  "Calls f on Ctrl-C, unless a line is being read: then Ctrl-C clears it."
  [{:keys [suspended? interrupt-handler terminal] :as editor} f]
  (when-let [remove-handler @interrupt-handler]
    (remove-handler))
  (reset! interrupt-handler
          (on-signal terminal
                     (fn []
                       ;; Ends the line the terminal echoed ^C on.
                       (if @suspended?
                         (write terminal "\r\n")
                         (print-text editor nil "\n"))
                       (f)))))

;;;; Editing

(defn- word-start
  "Where the word before i in s starts."
  [^String s i]
  (let [i (loop [i i] (if (and (pos? i) (not (Char/IsLetterOrDigit (.get_Chars s (int (dec i)))))) (recur (dec i)) i))]
    (loop [i i] (if (and (pos? i) (Char/IsLetterOrDigit (.get_Chars s (int (dec i))))) (recur (dec i)) i))))

(defn- word-end
  "Where the word after i in s ends."
  [^String s i]
  (let [n (count s)
        i (loop [i i] (if (and (< i n) (not (Char/IsLetterOrDigit (.get_Chars s (int i))))) (recur (inc i)) i))]
    (loop [i i] (if (and (< i n) (Char/IsLetterOrDigit (.get_Chars s (int i)))) (recur (inc i)) i))))

(defn- line-start [^String s i]
  (inc (or (str/last-index-of s "\n" (max 0 (dec i))) -1)))

(defn- line-end* [^String s i]
  (or (str/index-of s "\n" i) (count s)))

(defn- insert [{:keys [buffer cursor] :as st} ^String text]
  (assoc st :buffer (str (subs buffer 0 cursor) text (subs buffer cursor)) :cursor (+ cursor (count text))))

(defn- delete-range [{:keys [buffer] :as st} from to]
  (assoc st :buffer (str (subs buffer 0 from) (subs buffer to)) :cursor from))

(defn- vertical
  "The cursor moved a line up (-1) or down (1) in the buffer, at the same
  column as far as the line goes, or nil when there's no such line."
  [{:keys [buffer cursor] :as st} dir]
  (let [start (line-start buffer cursor)
        col (- cursor start)]
    (if (neg? dir)
      (when (pos? start)
        (let [prev-start (line-start buffer (dec start))]
          (assoc st :cursor (min (+ prev-start col) (dec start)))))
      (let [end (line-end* buffer cursor)]
        (when (< end (count buffer))
          (let [next-start (inc end)
                next-end (line-end* buffer next-start)]
            (assoc st :cursor (min (+ next-start col) next-end))))))))

(defn- history-move
  "The state with the history entry dir steps away (-1 older, 1 newer) in
  the buffer, or st when there's none."
  [{:keys [history-index saved] :as st} history dir]
  (let [n (count history)
        i (+ (or history-index n) dir)]
    (cond
      (or (neg? i) (> i n)) st
      (= i n) (assoc st :history-index nil :buffer (or saved "") :cursor (count (or saved "")))
      :else (let [entry (nth history i)]
              (assoc st :history-index i
                     :saved (if history-index saved (:buffer st))
                     :buffer entry :cursor (count entry))))))

(defn- common-prefix [strs]
  (reduce (fn [^String a ^String b]
            (let [n (min (count a) (count b))]
              (subs a 0 (loop [i 0] (if (and (< i n) (= (.get_Chars a (int i)) (.get_Chars b (int i)))) (recur (inc i)) i)))))
          strs))

(defn- complete!
  "Completes the word before the cursor, or lists what it could become.
  Returns the new state. Called with the lock held."
  [{:keys [complete terminal] :as editor} {:keys [^String buffer cursor] :as st}]
  (let [start (or (:start (lexer/symbol-at (lexer/lex buffer) cursor)) cursor)
        prefix (subs buffer start cursor)]
    (if (or (empty? prefix) (nil? complete))
      st
      (let [candidates (distinct (keep :candidate (complete prefix)))]
        (cond
          (empty? candidates) st
          (= 1 (count candidates)) (insert st (subs (first candidates) (count prefix)))
          :else (let [common (common-prefix candidates)]
                  (if (> (count common) (count prefix))
                    (insert st (subs common (count prefix)))
                    (let [width (columns terminal)
                          col (+ 2 (apply max (map count candidates)))
                          per-row (max 1 (quot width col))
                          rows (->> candidates
                                    (map #(str % (apply str (repeat (- col (count %)) \space))))
                                    (partition-all per-row)
                                    (map #(str/trimr (apply str %))))]
                      (clear-edit-area! editor)
                      (write terminal (str (str/join "\r\n" rows) "\r\n"))
                      st))))))))

(defn- search-history
  "The index of the newest history entry before from holding query, or nil."
  [history query from]
  (when (seq query)
    (first (for [i (range (dec from) -1 -1)
                 :when (str/includes? (nth history i) query)]
             i))))

(defn- read-with
  "Reads a line with prompt: the text, ::interrupted on Ctrl-C or ::eof at the
  end of the input, which is Ctrl-D on an empty line."
  [{:keys [terminal lock state incomplete? doc history] :as editor} prompt raw?]
  (locking lock
    (reset! state {:buffer "" :cursor 0 :prompt prompt :raw? raw?})
    (set-raw-ctrl-c terminal true)
    (render! editor))
  (try
    (loop [pending nil]
      (let [k (or (first (reset-vals! (:pushback editor) nil)) (read-key terminal))]
        (if (nil? k)
          ::eof
          (let [{key :key ctrl? :ctrl? alt? :alt?} k
                result
                (locking lock
                  (let [st @state
                        {:keys [buffer cursor search]} st
                        hist @history
                        done (fn [result] (reset! state (assoc st :done result)) result)]
                    (cond
                      ;; C-x C-d: documentation.
                      (= :ctrl-x pending)
                      (do (when (and (= \d key) ctrl? doc)
                            (when-let [text (doc buffer cursor)]
                              (clear-edit-area! editor)
                              (write terminal (str (str/replace (str text) "\n" "\r\n") "\r\n"))))
                          nil)

                      ;; Reverse incremental search.
                      search
                      (let [{:keys [query index]} search]
                        (cond
                          (and ctrl? (= \r key))
                          (let [i (search-history hist query (or index (count hist)))]
                            (reset! state (cond-> (assoc-in st [:search :index] (or i index))
                                            i (assoc :buffer (nth hist i) :cursor (count (nth hist i)))))
                            nil)
                          (or (and ctrl? (#{\g \c} key)) (= :escape key))
                          (do (reset! state (-> st (dissoc :search) (assoc :buffer (:saved search) :cursor (count (:saved search)))))
                              nil)
                          (= :backspace key)
                          (let [query (subs query 0 (max 0 (dec (count query))))
                                i (search-history hist query (count hist))]
                            (reset! state (cond-> (assoc st :search (assoc search :query query :index i))
                                            i (assoc :buffer (nth hist i) :cursor (count (nth hist i)))))
                            nil)
                          (and (char? key) (not ctrl?) (not alt?))
                          (let [query (str query key)
                                i (search-history hist query (inc (or index (dec (count hist)))))]
                            (reset! state (cond-> (assoc st :search (assoc search :query query :index i))
                                            i (assoc :buffer (nth hist i) :cursor (count (nth hist i)))))
                            nil)
                          ;; Anything else ends the search and does what it does.
                          :else (do (reset! state (dissoc st :search)) ::again)))

                      (and ctrl? (= \c key)) (done ::interrupted)

                      (= :paste key)
                      (do (reset! state (insert st (:text k))) nil)

                      (= :enter key)
                      (if (and (not raw?) (incomplete? buffer))
                        (do (reset! state (insert st "\n")) nil)
                        (done buffer))

                      (and ctrl? (= \d key))
                      (if (empty? buffer)
                        (done ::eof)
                        (do (when (< cursor (count buffer))
                              (reset! state (delete-range st cursor (inc cursor))))
                            nil))

                      (and ctrl? (= \x key)) :ctrl-x

                      (and ctrl? (= \r key) (not raw?))
                      (do (reset! state (assoc st :search {:query "" :index nil :saved buffer})) nil)

                      (= :tab key)
                      (do (when-not raw? (reset! state (complete! editor st))) nil)

                      (= :backspace key)
                      (do (if alt?
                            (let [s (word-start buffer cursor)] (reset! state (assoc (delete-range st s cursor) :kill (subs buffer s cursor))))
                            (when (pos? cursor)
                              (let [n (if (and (> cursor 1) (Char/IsLowSurrogate (.get_Chars ^String buffer (int (dec cursor))))) 2 1)]
                                (reset! state (delete-range st (- cursor n) cursor)))))
                          nil)

                      (= :delete key)
                      (do (when (< cursor (count buffer)) (reset! state (delete-range st cursor (inc cursor)))) nil)

                      (or (= :left key) (and ctrl? (= \b key)))
                      (do (reset! state (assoc st :cursor (max 0 (dec cursor)))) nil)

                      (or (= :right key) (and ctrl? (= \f key)))
                      (do (reset! state (assoc st :cursor (min (count buffer) (inc cursor)))) nil)

                      (and alt? (= \b key)) (do (reset! state (assoc st :cursor (word-start buffer cursor))) nil)
                      (and alt? (= \f key)) (do (reset! state (assoc st :cursor (word-end buffer cursor))) nil)
                      (and alt? (= \d key))
                      (let [e (word-end buffer cursor)] (reset! state (assoc (delete-range st cursor e) :kill (subs buffer cursor e))) nil)

                      (or (= :home key) (and ctrl? (= \a key)))
                      (do (reset! state (assoc st :cursor (line-start buffer cursor))) nil)

                      (or (= :end key) (and ctrl? (= \e key)))
                      (do (reset! state (assoc st :cursor (line-end* buffer cursor))) nil)

                      (and ctrl? (= \k key))
                      (let [e (line-end* buffer cursor)
                            e (if (and (= e cursor) (< e (count buffer))) (inc e) e)]
                        (reset! state (assoc (delete-range st cursor e) :kill (subs buffer cursor e)))
                        nil)

                      (and ctrl? (= \u key))
                      (let [s (line-start buffer cursor)]
                        (reset! state (assoc (delete-range st s cursor) :kill (subs buffer s cursor)))
                        nil)

                      (and ctrl? (= \w key))
                      (let [s (loop [i cursor] (if (and (pos? i) (Char/IsWhiteSpace (.get_Chars ^String buffer (int (dec i))))) (recur (dec i)) i))
                            s (loop [i s] (if (and (pos? i) (not (Char/IsWhiteSpace (.get_Chars ^String buffer (int (dec i)))))) (recur (dec i)) i))]
                        (reset! state (assoc (delete-range st s cursor) :kill (subs buffer s cursor)))
                        nil)

                      (and ctrl? (= \y key))
                      (do (reset! state (insert st (or (:kill st) ""))) nil)

                      (and ctrl? (= \l key))
                      (do (write terminal "\u001b[H\u001b[2J") (reset! (:cursor-row editor) 0) nil)

                      (or (= :up key) (and ctrl? (= \p key)))
                      (do (reset! state (or (vertical st -1) (if raw? st (history-move st hist -1)))) nil)

                      (or (= :down key) (and ctrl? (= \n key)))
                      (do (reset! state (or (vertical st 1) (if raw? st (history-move st hist 1)))) nil)

                      (and (char? key) (not ctrl?) (not alt?))
                      (do (reset! state (insert st (str key))) nil)

                      :else nil)))]
            (cond
              (= ::again result) (recur nil)
              (= :ctrl-x result) (do (locking lock (render! editor)) (recur :ctrl-x))
              (nil? result) (do (locking lock (render! editor)) (recur nil))
              (= ::interrupted result)
              ;; Ctrl-C can come twice at once (as a signal and as a key, on
              ;; some terminals): it clears the line once.
              (let [next-key (poll-key terminal 20)]
                (when-not (or (= ::none next-key)
                              (and (= \c (:key next-key)) (:ctrl? next-key)))
                  (reset! (:pushback editor) next-key))
                result)
              :else result)))))
    (finally
      (locking lock
        (when-let [{:keys [done] :as st} @state]
          ;; The finished line stays on the screen, without the tail tip, and
          ;; the cursor goes past it.
          (reset! state (cond-> (dissoc st :search)
                          true (assoc :cursor (count (:buffer st)))
                          (keyword? done) (assoc :buffer (:buffer st))))
          (render! (assoc editor :eldoc nil))
          (write terminal (str (when (= ::interrupted done) "^C") "\r\n")))
        (reset! state nil)
        (reset! (:cursor-row editor) 0)
        (reset! (:line editor) "")
        (set-raw-ctrl-c terminal false)
        ;; Output that came in the meantime, short of its newline.
        (let [above @(:above editor)]
          (reset! (:above editor) "")
          (when (seq above) (emit editor above)))))))

(defn- remember!
  "Adds entry to the history and its file."
  [{:keys [history history-file]} ^String entry]
  (when-not (str/blank? entry)
    (let [h (swap! history (fn [h]
                             (let [h (if (= entry (peek h)) h (conj h entry))]
                               (if (> (count h) max-history) (subvec h (- (count h) max-history)) h))))]
      (save-history history-file h))))

(defn read-code
  "Reads code from the user with prompt. Enter only submits it once its forms
  are complete, and Ctrl-C starts over. Returns nil at the end of input, which
  is Ctrl-D on an empty line."
  [editor prompt]
  (loop []
    (let [line (read-with editor prompt false)]
      (cond
        (= ::interrupted line) (recur)
        (= ::eof line) nil
        :else (do (remember! editor line) line)))))

(defn read-line
  "Reads a line as typed, for code reading *in*, leaving it out of the
  history. Returns nil at the end of input and ::interrupted on Ctrl-C."
  [{:keys [line] :as editor}]
  ;; Whatever's been printed on the line, like a question the code asks, goes
  ;; in the prompt, as the editor redraws the line it reads.
  (let [prompt @line
        result (read-with editor prompt true)]
    (if (= ::eof result) nil result)))
