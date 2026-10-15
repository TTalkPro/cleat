;;; PARSING -- Copyright 2006 William D Clinger.
;;
;; Parsing benchmark.
;;
;; Reads nboyer.sch into a string before timing begins.
;; The timed portion of the benchmark parses the string
;; representation of nboyer.sch 1000 times.
;; The output of that parse is checked by comparing it
;; the the value returned by the read procedure.
;;
;; 移植说明：parse-string 的 set! 局部变量改为 volatile!；状态机与 LL(1) 分析器的局部过程放在一个 letfn 里，
;; 状态自身的尾调用用 recur，状态之间的尾调用是普通调用（深度只随一段连续空白 / 注释里的状态切换次数增长）；
;; 记号种类与 Scheme 符号常量用关键字；string_accumulator 是 char 数组。
;; 输入里有点对 (nboyer-benchmark . args)，Clojure 的 cons 不能以符号结尾，所以分析器构造的序对一律用 util 的可变序对（空表 nil），
;; 检查时再转成 Clojure 表与 read 的结果比较。
(ns r7rs.parsing
  (:require [r7rs.common :refer [hide read-input run-r7rs-benchmark]]
            [r7rs.util :as u :refer [char-whitespace? file-reader mcons mcar mcdr mpair?]]))

(defn- string-ref [s i]
  #?(:clj (.charAt ^String s (int i)) :cljr (.get_Chars ^String s (int i))))

(defn- chars->string [^chars a n]
  (String. a 0 (int n)))

(defn- substring [s start end]
  (subs s start end))

(defn- string->number [s]
  (or (parse-long s) (parse-double s)))

(defn- memq [x lst]
  (loop [l lst]
    (cond (empty? l) false
          (identical? x (first l)) l
          :else (recur (rest l)))))

;; 正确性检查用：把可变序对组成的（真）表转成 Clojure 表，供正确性检查与 read 的结果比较
(defn- pairs->data [x]
  (cond (mpair? x) (loop [acc [] p x]
                     (cond (nil? p) (apply list acc)
                           (mpair? p) (recur (conj acc (pairs->data (mcar p))) (mcdr p))
                           :else (list* (conj acc '.) [p])))
        (vector? x) (mapv pairs->data x)
        (nil? x) ()
        :else x))

(defn read-file-as-string [name]
  (let [[read-char _ close] (file-reader name)]
    (try
      (loop [x (read-char) chars ()]
        (if (nil? x)
          (apply str (reverse chars))
          (recur (read-char) (cons x chars))))
      (finally (close)))))

(defn parse-string [input-string]
  (let [eof \~
        max_token_size 1024
        errLongToken 1
        errincompletetoken 2
        errLexGenBug 3
        kindOfNextToken (volatile! :z1)
        nextTokenIsReady (volatile! false)
        tokenValue (volatile! "")
        totalErrors 0
        lineNumber (volatile! 1)
        lineNumberOfLastError 0
        ^chars string_accumulator (char-array max_token_size)
        string_accumulator_length (volatile! 0)
        nextCharacter (volatile! \space)
        nextCharacterIsReady (volatile! false)
        input-index (volatile! 0)
        input-length (count input-string)]
    (letfn [;; LexGen generated the code for the state machine.
            (scanner0 []
              (loop [c (scanchar)]
                (when (char-whitespace? c)
                  (consumechar)
                  (vreset! string_accumulator_length 0)
                  (recur (scanchar))))
              (let [c (scanchar)]
                (if (= c eof) (accept :eof) (state0 c))))
            (state0 [c]
              (case c
                \` (do (consumechar) (accept :backquote))
                \' (do (consumechar) (accept :quote))
                \) (do (consumechar) (accept :rparen))
                \( (do (consumechar) (accept :lparen))
                \; (do (consumechar) (state29 (scanchar)))
                (\+ \-) (do (consumechar) (state28 (scanchar)))
                (\0 \1 \2 \3 \4 \5 \6 \7 \8 \9) (do (consumechar) (state27 (scanchar)))
                \. (do (consumechar) (state16 (scanchar)))
                (\a \b \c \d \e \f \g \h \i \j \k \l \m \n \o \p \q \r \s \t \u \v \w \x \y \z
                 \A \B \C \D \E \F \G \H \I \J \K \L \M \N \O \P \Q \R \S \T \U \V \W \X \Y \Z
                 \! \$ \% \& \* \/ \: \< \= \> \? \^ \_ \~)
                (do (consumechar) (state14 (scanchar)))
                \# (do (consumechar) (state13 (scanchar)))
                \" (do (consumechar) (state2 (scanchar)))
                \, (do (consumechar) (state1 (scanchar)))
                (if (char-whitespace? c)
                  (do (consumechar) (state30 (scanchar)))
                  (scannererror errincompletetoken))))
            (state1 [c]
              (case c
                \@ (do (consumechar) (accept :splicing))
                (accept :comma)))
            (state2 [c]
              (case c
                \" (do (consumechar) (accept :string))
                (if (isnotdoublequote? c)
                  (do (consumechar) (recur (scanchar)))
                  (scannererror errincompletetoken))))
            (state3 [c]
              (case c
                \n (do (consumechar) (state8 (scanchar)))
                (scannererror errincompletetoken)))
            (state4 [c]
              (case c
                \i (do (consumechar) (state3 (scanchar)))
                (scannererror errincompletetoken)))
            (state5 [c]
              (case c
                \l (do (consumechar) (state4 (scanchar)))
                (scannererror errincompletetoken)))
            (state6 [c]
              (case c
                \w (do (consumechar) (state5 (scanchar)))
                (scannererror errincompletetoken)))
            (state7 [c]
              (case c
                \e (do (consumechar) (state6 (scanchar)))
                (scannererror errincompletetoken)))
            (state8 [c]
              (case c
                \e (do (consumechar) (accept :character))
                (scannererror errincompletetoken)))
            (state9 [c]
              (case c
                \c (do (consumechar) (state8 (scanchar)))
                (scannererror errincompletetoken)))
            (state10 [c]
              (case c
                \a (do (consumechar) (state9 (scanchar)))
                (scannererror errincompletetoken)))
            (state11 [c]
              (case c
                \p (do (consumechar) (state10 (scanchar)))
                (scannererror errincompletetoken)))
            (state12 [c]
              (case c
                \s (do (consumechar) (state11 (scanchar)))
                \n (do (consumechar) (state7 (scanchar)))
                (if (char? c)
                  (do (consumechar) (accept :character))
                  (scannererror errincompletetoken))))
            (state13 [c]
              (case c
                \( (do (consumechar) (accept :vecstart))
                (\t \f) (do (consumechar) (accept :boolean))
                \\ (do (consumechar) (state12 (scanchar)))
                (scannererror errincompletetoken)))
            (state14 [c]
              (case c
                (\a \b \c \d \e \f \g \h \i \j \k \l \m \n \o \p \q \r \s \t \u \v \w \x \y \z
                 \A \B \C \D \E \F \G \H \I \J \K \L \M \N \O \P \Q \R \S \T \U \V \W \X \Y \Z
                 \! \$ \% \& \* \/ \: \< \= \> \? \^ \_ \~
                 \0 \1 \2 \3 \4 \5 \6 \7 \8 \9 \+ \- \. \@)
                (do (consumechar) (recur (scanchar)))
                (accept :id)))
            (state15 [c]
              (case c
                \. (do (consumechar) (accept :id))
                (scannererror errincompletetoken)))
            (state16 [c]
              (case c
                (\0 \1 \2 \3 \4 \5 \6 \7 \8 \9) (do (consumechar) (state18 (scanchar)))
                \. (do (consumechar) (state15 (scanchar)))
                (accept :period)))
            (state17 [c]
              (case c
                (\0 \1 \2 \3 \4 \5 \6 \7 \8 \9) (do (consumechar) (state18 (scanchar)))
                (scannererror errincompletetoken)))
            (state18 [c]
              (case c
                (\e \s \f \d \l) (do (consumechar) (state22 (scanchar)))
                \# (do (consumechar) (state19 (scanchar)))
                (\0 \1 \2 \3 \4 \5 \6 \7 \8 \9) (do (consumechar) (recur (scanchar)))
                (accept :number)))
            (state19 [c]
              (case c
                (\e \s \f \d \l) (do (consumechar) (state22 (scanchar)))
                \# (do (consumechar) (recur (scanchar)))
                (accept :number)))
            (state20 [c]
              (case c
                (\0 \1 \2 \3 \4 \5 \6 \7 \8 \9) (do (consumechar) (recur (scanchar)))
                (accept :number)))
            (state21 [c]
              (case c
                (\0 \1 \2 \3 \4 \5 \6 \7 \8 \9) (do (consumechar) (state20 (scanchar)))
                (scannererror errincompletetoken)))
            (state22 [c]
              (case c
                (\+ \-) (do (consumechar) (state21 (scanchar)))
                (\0 \1 \2 \3 \4 \5 \6 \7 \8 \9) (do (consumechar) (state20 (scanchar)))
                (scannererror errincompletetoken)))
            (state23 [c]
              (case c
                \# (do (consumechar) (recur (scanchar)))
                (accept :number)))
            (state24 [c]
              (case c
                (\0 \1 \2 \3 \4 \5 \6 \7 \8 \9) (do (consumechar) (recur (scanchar)))
                \# (do (consumechar) (state23 (scanchar)))
                (accept :number)))
            (state25 [c]
              (case c
                (\0 \1 \2 \3 \4 \5 \6 \7 \8 \9) (do (consumechar) (state24 (scanchar)))
                (scannererror errincompletetoken)))
            (state26 [c]
              (case c
                \# (do (consumechar) (recur (scanchar)))
                \/ (do (consumechar) (state25 (scanchar)))
                (\e \s \f \d \l) (do (consumechar) (state22 (scanchar)))
                \. (do (consumechar) (state19 (scanchar)))
                (accept :number)))
            (state27 [c]
              (case c
                (\0 \1 \2 \3 \4 \5 \6 \7 \8 \9) (do (consumechar) (recur (scanchar)))
                \# (do (consumechar) (state26 (scanchar)))
                \/ (do (consumechar) (state25 (scanchar)))
                (\e \s \f \d \l) (do (consumechar) (state22 (scanchar)))
                \. (do (consumechar) (state18 (scanchar)))
                (accept :number)))
            (state28 [c]
              (case c
                (\0 \1 \2 \3 \4 \5 \6 \7 \8 \9) (do (consumechar) (state27 (scanchar)))
                \. (do (consumechar) (state17 (scanchar)))
                (accept :id)))
            (state29 [c]
              (case c
                \newline (do (consumechar)
                             (vreset! string_accumulator_length 0)
                             (state0 (scanchar)))
                (if (isnotnewline? c)
                  (do (consumechar) (recur (scanchar)))
                  (scannererror errincompletetoken))))
            (state30 [c]
              (if (char-whitespace? c)
                (do (consumechar) (recur (scanchar)))
                (do (vreset! string_accumulator_length 0)
                    (state0 (scanchar)))))
            (state31 [c]
              (vreset! string_accumulator_length 0)
              (state0 (scanchar)))
            (state32 [c] (accept :id))
            (state33 [c] (accept :boolean))
            (state34 [c] (accept :character))
            (state35 [c] (accept :vecstart))
            (state36 [c] (accept :string))
            (state37 [c] (accept :lparen))
            (state38 [c] (accept :rparen))
            (state39 [c] (accept :quote))
            (state40 [c] (accept :backquote))
            (state41 [c] (accept :splicing))

            ;; ParseGen generated the code for the strong LL(1) parser.
            (parse-datum []
              (case (next-token)
                (:splicing :comma :backquote :quote :lparen :vecstart)
                (let [ast1 (parse-compound-datum)] (identity ast1))
                (:boolean :number :character :string :id)
                (let [ast1 (parse-simple-datum)] (identity ast1))
                (parse-error :<datum>
                             '(:backquote :boolean :character :comma :id :lparen :number :quote :splicing :string :vecstart))))
            (parse-simple-datum []
              (case (next-token)
                :id (let [ast1 (parse-symbol)] (identity ast1))
                :string (do (consume-token!) (makeString))
                :character (do (consume-token!) (makeChar))
                :number (do (consume-token!) (makeNum))
                :boolean (do (consume-token!) (makeBool))
                (parse-error :<simple-datum> '(:boolean :character :id :number :string))))
            (parse-symbol []
              (case (next-token)
                :id (do (consume-token!) (makeSym))
                (parse-error :<symbol> '(:id))))
            (parse-compound-datum []
              (case (next-token)
                :vecstart (let [ast1 (parse-vector)] (identity ast1))
                (:lparen :quote :backquote :comma :splicing) (let [ast1 (parse-list)] (identity ast1))
                (parse-error :<compound-datum> '(:backquote :comma :lparen :quote :splicing :vecstart))))
            (parse-list []
              (case (next-token)
                (:splicing :comma :backquote :quote)
                (let [ast1 (parse-abbreviation)] (identity ast1))
                :lparen
                (do (consume-token!)
                    (let [ast1 (parse-list2)] (identity ast1)))
                (parse-error :<list> '(:backquote :comma :lparen :quote :splicing))))
            (parse-list2 []
              (case (next-token)
                (:id :string :character :number :boolean :vecstart :lparen :quote :backquote :comma :splicing)
                (let [ast1 (parse-datum)]
                  (let [ast2 (parse-list3)] (mcons ast1 ast2)))
                :rparen (do (consume-token!) (emptyList))
                (parse-error :<list2>
                             '(:backquote :boolean :character :comma :id :lparen :number :quote :rparen :splicing :string :vecstart))))
            (parse-list3 []
              (case (next-token)
                (:rparen :period :splicing :comma :backquote :quote :lparen :vecstart :boolean :number :character :string :id)
                (let [ast1 (parse-data)]
                  (let [ast2 (parse-list4)]
                    (pseudoAppend ast1 ast2)))
                (parse-error :<list3>
                             '(:backquote :boolean :character :comma :id :lparen :number :period :quote :rparen :splicing :string :vecstart))))
            (parse-list4 []
              (case (next-token)
                :period
                (do (consume-token!)
                    (let [ast1 (parse-datum)]
                      (if (identical? (next-token) :rparen)
                        (do (consume-token!) (identity ast1))
                        (parse-error :<list4> '(:rparen)))))
                :rparen (do (consume-token!) (emptyList))
                (parse-error :<list4> '(:period :rparen))))
            (parse-abbreviation []
              (case (next-token)
                (:quote :backquote :comma :splicing)
                (let [ast1 (parse-abbrev-prefix)]
                  (let [ast2 (parse-datum)] (mcons ast1 (mcons ast2 nil))))
                (parse-error :<abbreviation> '(:backquote :comma :quote :splicing))))
            (parse-abbrev-prefix []
              (case (next-token)
                :splicing (do (consume-token!) (symSplicing))
                :comma (do (consume-token!) (symUnquote))
                :backquote (do (consume-token!) (symBackquote))
                :quote (do (consume-token!) (symQuote))
                (parse-error :<abbrev-prefix> '(:backquote :comma :quote :splicing))))
            (parse-vector []
              (case (next-token)
                :vecstart
                (do (consume-token!)
                    (let [ast1 (parse-data)]
                      (if (identical? (next-token) :rparen)
                        (do (consume-token!) (list2vector ast1))
                        (parse-error :<vector> '(:rparen)))))
                (parse-error :<vector> '(:vecstart))))
            (parse-data []
              (case (next-token)
                (:id :string :character :number :boolean :vecstart :lparen :quote :backquote :comma :splicing)
                (let [ast1 (parse-datum)]
                  (let [ast2 (parse-data)] (mcons ast1 ast2)))
                (:rparen :period) (emptyList)
                (parse-error :<data>
                             '(:backquote :boolean :character :comma :id :lparen :number :period :quote :rparen :splicing :string :vecstart))))

            ;; Help predicates used by the lexical analyzer's state machine.
            (isnotdoublequote? [c] (not (= c \")))
            (isnotnewline? [c] (not (= c \newline)))

            ;; Lexical analyzer.
            (next-token []
              (if @nextTokenIsReady
                @kindOfNextToken
                (do (vreset! string_accumulator_length 0)
                    (scanner0))))
            (consume-token! []
              (vreset! nextTokenIsReady false))
            (scannererror [msg]
              (let [msgtxt (cond (== msg errLongToken) "Amazingly long token"
                                 (== msg errincompletetoken) "in line "
                                 (== msg errLexGenBug) "Bug in lexical analyzer (generated)"
                                 :else "Bug in lexical analyzer")]
                (u/fatal "Lexical Error: " msgtxt " " @lineNumber)
                (vreset! nextTokenIsReady false)
                (vreset! nextCharacterIsReady false)
                (next-token)))
            (accept [t]
              (when (memq t '(:boolean :character :id :number :string))
                (vreset! tokenValue (chars->string string_accumulator @string_accumulator_length)))
              (vreset! kindOfNextToken t)
              (vreset! nextTokenIsReady true)
              t)

            ;; Character i/o, so to speak.  Uses the input-string as input.
            (scanchar []
              (if @nextCharacterIsReady
                @nextCharacter
                (do (if (< @input-index input-length)
                      (do (vreset! nextCharacter (string-ref input-string @input-index))
                          (vreset! input-index (+ @input-index 1)))
                      (vreset! nextCharacter eof))
                    (vreset! nextCharacterIsReady true)
                    (recur))))
            (consumechar []
              (when-not @nextCharacterIsReady
                (scanchar))
              (if (< @string_accumulator_length max_token_size)
                (do (vreset! nextCharacterIsReady false)
                    (when (= @nextCharacter \newline)
                      (vreset! lineNumber (+ @lineNumber 1)))
                    (aset string_accumulator (int @string_accumulator_length) (char @nextCharacter))
                    (vreset! string_accumulator_length (+ @string_accumulator_length 1)))
                (scannererror errLongToken)))

            ;; Action procedures called by the parser.
            (emptyList [] nil)
            (identity [x] x)
            (list2vector [vals] (loop [v (transient []) l vals] (if (nil? l) (persistent! v) (recur (conj! v (mcar l)) (mcdr l)))))
            (makeBool [] (= @tokenValue "#t"))
            (makeChar [] (string-ref @tokenValue 0))
            (makeNum [] (string->number @tokenValue))
            (makeString []
              ;; Must strip off outer double quotes.
              (substring @tokenValue 1 (- (count @tokenValue) 1)))
            (makeSym [] (symbol @tokenValue))
            ;; Like append, but allows the last argument to be a non-list.
            (pseudoAppend [vals terminus]
              (if (nil? vals)
                terminus
                (mcons (mcar vals) (pseudoAppend (mcdr vals) terminus))))
            (symBackquote [] 'quasiquote)
            (symQuote [] 'quote)
            (symSplicing [] 'unquote-splicing)
            (symUnquote [] 'unquote)

            ;; Error procedure called by the parser.
            ;; As a hack, this error procedure recovers from end-of-file.
            (parse-error [nonterminal expected-terminals]
              (if (identical? :eof (next-token))
                :eof
                (do (println (str "Syntax error in line " @lineNumber " while parsing a " (pr-str nonterminal)))
                    (println (str "  Encountered a " (next-token) " while expecting something in"))
                    (println (str "  " (pr-str expected-terminals)))
                    (u/fatal "Syntax error"))))]
      ;; Parses repeatedly, returning the last <datum> parsed.
      (loop [x (parse-datum) y :eof]
        (if (identical? x :eof)
          y
          (recur (parse-datum) x))))))

(defn parsing-benchmark [& rest]
  (let [n (if (empty? rest) 1000 (first rest))
        input (if (or (empty? rest) (empty? (next rest))) "nboyer.sch" (second rest))
        input-string (read-file-as-string input)]
    (loop [i 0 result false]
      (if (== i n)
        result
        (recur (+ i 1) (parse-string input-string))))))

(defn -main [& _]
  (let [count (read-input) input1 (read-input) output (read-input)
        s2 (str count) s1 input1
        name "parsing"]
    (run-r7rs-benchmark (str name ":" s2) 1
                        (fn [] (parsing-benchmark (hide count count) (hide count input1)))
                        (fn [result] (= (pairs->data result) output)))))
