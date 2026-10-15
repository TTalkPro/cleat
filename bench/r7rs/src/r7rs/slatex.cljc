;;; SLATEX -- Scheme to Latex processor.
;;
;; slatex.scm file generated using config.scm
;; (c) Dorai Sitaram, Rice U., 1991, 1994
;;
;; 移植说明：
;; - 原版到处用 append! / remove-if! / reverse! / set-car! / set-cdr! 改写表，所以程序里的表一律是 util 的可变序对（空表 nil），
;;   car / cdr / cons / list / map / for-each / member / memv / append / length / list-tail 等是本文件里作用于序对的私有版本。
;; - 行缓冲（make-string + string-set!）是 char 数组（mstring-ref / string-set!），行与 case / bq 帧（make-vector）是对象数组；
;;   其余字符串是不可变的 Clojure 字符串（string-ref）。
;; - 名字去掉 slatex. 前缀（Clojure 里带点的符号是类名）；被 set! 的全局变量与闭包 / 局部变量用 volatile!；
;;   只在单次求值里重设的局部（c、c+1）改成重新绑定。符号常量（typ、'skip 等）用关键字。
;; - 端口：本文件私有的 InPort / OutPort（读者条件：JVM java.io，CLR System.IO），read-char / peek-char 在文件末尾返回 nil（eof）。
;; - string->number 只用作真假判断（data-token?），用正则判断 Scheme 的十进制 / 分数记法。
;; - 原版没有 call/cc；输出文件写到 outputs/（相对于工作目录，与原版相同），z*.tex 文件不删除。
(ns r7rs.slatex
  (:refer-clojure :exclude [cons list map])
  (:require [r7rs.common :refer [hide read-input run-r7rs-benchmark]]
            [r7rs.util :as u :refer [mcons mcar mcdr set-mcar! set-mcdr! mpair? char-alphabetic? char-whitespace?
                                     char-downcase integer->char]]))

;;; ---------------------------------------------------------------------------
;;; Scheme 序对、向量、字符串与端口

(def ^:private car mcar)
(def ^:private cdr mcdr)
(def ^:private cons mcons)
(def ^:private set-car! set-mcar!)
(def ^:private set-cdr! set-mcdr!)
(defn- null? [x] (nil? x))
(defn- cadr [x] (car (cdr x)))

(defn- list
  ([] nil)
  ([a] (cons a nil))
  ([a b] (cons a (cons b nil)))
  ([a b c] (cons a (cons b (cons c nil))))
  ([a b c & more]
   (cons a (cons b (cons c (loop [xs (reverse more) acc nil]
                             (if (empty? xs) acc (recur (rest xs) (cons (first xs) acc)))))))))

(defn- seq->list [xs]
  (loop [xs (reverse xs) acc nil]
    (if (empty? xs) acc (recur (rest xs) (cons (first xs) acc)))))

(defn- list->seq [l]
  (loop [l l acc []]
    (if (null? l) (seq acc) (recur (cdr l) (conj acc (car l))))))

(defn- map [f l]
  (if (null? l) nil (cons (f (car l)) (map f (cdr l)))))

(defn- for-each [f l]
  (loop [l l] (when-not (null? l) (f (car l)) (recur (cdr l)))))

(defn- append [a b]
  (if (null? a) b (cons (car a) (append (cdr a) b))))

(defn- length [l]
  (loop [l l n 0] (if (null? l) n (recur (cdr l) (+ n 1)))))

(defn- list-tail [l k]
  (loop [l l k k] (if (zero? k) l (recur (cdr l) (- k 1)))))

(defn- memv [x l]
  (loop [l l] (cond (null? l) false (= x (car l)) l :else (recur (cdr l)))))

(def ^:private memq memv)
(def ^:private member memv)

(defn- vector-ref [^objects v i] (aget v (int i)))
(defn- vector-set! [^objects v i x] (aset v (int i) x))
(defn- make-vector [n] (object-array n))

(defn- make-string [n c] (char-array n (char c)))
(defn- mstring-ref [^chars s i] (aget s (int i)))
(defn- string-set! [^chars s i c] (aset s (int i) (char c)))
(defn- string-ref [s i]
  #?(:clj (.charAt ^String s (int i)) :cljr (.get_Chars ^String s (int i))))
(defn- string-length [s] (count s))
(defn- substring [s start end] (subs s start end))

(defn- string->list [s]
  (loop [i (- (string-length s) 1) acc nil]
    (if (< i 0) acc (recur (- i 1) (cons (string-ref s i) acc)))))

(defn- list->string [l]
  (let [sb (u/string-builder)]
    (loop [l l]
      (if (null? l) (str sb) (do (u/sb-append-char! sb (car l)) (recur (cdr l)))))))

(defn- string-ci=? [a b]
  (and (== (string-length a) (string-length b))
       (loop [i 0]
         (or (>= i (string-length a))
             (and (= (char-downcase (string-ref a i)) (char-downcase (string-ref b i)))
                  (recur (+ i 1)))))))

(defn- string->number
  "只用作真假判断：s 是否是 Scheme 的十进制 / 分数数字记法。"
  [s]
  (re-matches #"[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?|[+-]?\d+/\d+" s))

(definterface IInPort (readChar []) (peekChar []) (closeIn []))
(definterface IOutPort (putStr [s]) (closeOut []))

#?(:clj
   (deftype InPort [^java.io.PushbackReader r]
     IInPort
     (readChar [_] (let [c (.read r)] (if (neg? c) nil (char c))))
     (peekChar [_] (let [c (.read r)] (if (neg? c) nil (do (.unread r c) (char c)))))
     (closeIn [_] (.close r)))
   :cljr
   (deftype InPort [^System.IO.StreamReader r]
     IInPort
     (readChar [_] (let [c (.Read r)] (if (neg? c) nil (char c))))
     (peekChar [_] (let [c (.Peek r)] (if (neg? c) nil (char c))))
     (closeIn [_] (.Close r))))

#?(:clj
   (deftype OutPort [^java.io.Writer w]
     IOutPort
     (putStr [_ s] (.write w ^String s))
     (closeOut [_] (.close w)))
   :cljr
   (deftype OutPort [^System.IO.StreamWriter w]
     IOutPort
     (putStr [_ s] (.Write w ^String s))
     (closeOut [_] (.Close w))))

(defn- open-input-file [f]
  #?(:clj (InPort. (java.io.PushbackReader. (java.io.BufferedReader. (java.io.FileReader. (str f)))))
     :cljr (InPort. (System.IO.StreamReader. (str f)))))

(defn- open-output-file [f]
  #?(:clj (OutPort. (java.io.BufferedWriter. (java.io.FileWriter. (str f))))
     :cljr (OutPort. (System.IO.StreamWriter. (str f)))))

(defn- read-char [p] (.readChar ^r7rs.slatex.IInPort p))
(defn- peek-char [p] (.peekChar ^r7rs.slatex.IInPort p))
(defn- eof-object? [c] (nil? c))

(defn- display
  ([x] (print (str x)))
  ([x p] (.putStr ^r7rs.slatex.IOutPort p (str x))))

(defn- newline*
  ([] (newline))
  ([p] (.putStr ^r7rs.slatex.IOutPort p "\n")))

(defn- call-with-input-file [f proc]
  (let [p (open-input-file f)]
    (try (proc p) (finally (.closeIn ^r7rs.slatex.IInPort p)))))

(defn- call-with-output-file [f proc]
  (let [p (open-output-file f)]
    (try (proc p) (finally (.closeOut ^r7rs.slatex.IOutPort p)))))

(defn- file-exists? [f]
  #?(:clj (.exists (java.io.File. (str f))) :cljr (System.IO.File/Exists (str f))))

(defn- delete-file [f]
  #?(:clj (.delete (java.io.File. (str f))) :cljr (System.IO.File/Delete (str f))))

;;; ---------------------------------------------------------------------------

(declare error* token=? remove-token! slatex-member-token assoc-token tex-analog texify-aux
         blank-line? flush-comment-line? remove-some-tabs add-some-tabs clean-init-spaces clean-inner-spaces
         display-tex-line display-scm-line do-token output-token data-token? set-keyword
         new-secondary-aux-file new-primary-aux-file process-tex-file trigger-scheme2tex trigger-region
         process-scheme-file inline-protected-files inline-protected read-filename dump-intext dump-display)

(def op-sys :unix)

(defn ormap [f l]
  (loop [l l] (if (null? l) false (or (f (car l)) (recur (cdr l))))))

(defn ormapcdr [f l]
  (loop [l l] (if (null? l) false (or (f l) (recur (cdr l))))))

(defn append! [l1 l2]
  (cond (null? l1) l2
        (null? l2) l1
        :else (do (loop [l1 l1]
                    (if (null? (cdr l1)) (set-cdr! l1 l2) (recur (cdr l1))))
                  l1)))

(defn append-map! [f l]
  ((fn lp [l]
     (if (null? l) nil (append! (f (car l)) (lp (cdr l)))))
   l))

(defn remove-if! [p s]
  ((fn lp [s]
     (cond (null? s) nil
           (p (car s)) (recur (cdr s))
           :else (let [r (lp (cdr s))] (set-cdr! s r) s)))
   s))

(defn reverse! [s]
  (loop [s s r nil]
    (if (null? s) r (let [d (cdr s)] (set-cdr! s r) (recur d s)))))

(defn list-set! [l i v]
  (loop [l l i i]
    (cond (null? l) (error* 'slatex.list-set! 'list-too-small)
          (== i 0) (set-car! l v)
          :else (recur (cdr l) (- i 1)))))

(defn list-prefix? [pfx l]
  (cond (null? pfx) true
        (null? l) false
        (= (car pfx) (car l)) (recur (cdr pfx) (cdr l))
        :else false))

(defn string-prefix? [pfx s]
  (let [pfx-len (string-length pfx) s-len (string-length s)]
    (if (> pfx-len s-len)
      false
      (loop [i 0]
        (if (>= i pfx-len)
          true
          (and (= (string-ref pfx i) (string-ref s i))
               (recur (+ i 1))))))))

(defn string-suffix? [sfx s]
  (let [sfx-len (string-length sfx) s-len (string-length s)]
    (if (> sfx-len s-len)
      false
      (loop [i (- sfx-len 1) j (- s-len 1)]
        (if (< i 0)
          true
          (and (= (string-ref sfx i) (string-ref s j))
               (recur (- i 1) (- j 1))))))))

(def member-string member)

(defn adjoin-string [s l] (if (member-string s l) l (cons s l)))

(defn remove-string! [s l] (remove-if! (fn [l_i] (= l_i s)) l))

(defn adjoin-char [c l] (if (memv c l) l (cons c l)))

(defn remove-char! [c l] (remove-if! (fn [l_i] (= l_i c)) l))

(defn sublist [l i f]
  (loop [l (list-tail l i) k i r nil]
    (cond (>= k f) (reverse! r)
          (null? l) (error* 'slatex.sublist 'list-too-small)
          :else (recur (cdr l) (+ k 1) (cons (car l) r)))))

(defn position-char [c l]
  (loop [l l i 0]
    (cond (null? l) false
          (= (car l) c) i
          :else (recur (cdr l) (+ i 1)))))

(defn string-position-right [c s]
  (let [n (string-length s)]
    (loop [i (- n 1)]
      (cond (< i 0) false
            (= (string-ref s i) c) i
            :else (recur (- i 1))))))

(def slatex-case-sensitive? (volatile! true))

(defn token=? [t1 t2]
  ((if @slatex-case-sensitive? = string-ci=?) t1 t2))

(defn assoc-token [x s]
  (ormap (fn [s_i] (if (token=? (car s_i) x) s_i false)) s))

(defn member-token [x s]
  (ormapcdr (fn [s_i_] (if (token=? (car s_i_) x) s_i_ false)) s))

(defn remove-token! [x s] (remove-if! (fn [s_i] (token=? s_i x)) s))

(defn slatex-file-exists? [f] (file-exists? f))

(defn slatex-delete-file [f] (delete-file f))

(defn force-output [& z] :assume-output-forced)

(def return-char (integer->char 13))

(def tab-char (integer->char 9))

(defn error* [error-type error-values]
  (display "Error: ")
  (display error-type)
  (display ": ")
  (newline*)
  (print (pr-str error-values))
  (u/fatal ""))

(def keyword-tokens
  (volatile!
   (seq->list
    (clojure.core/map name
                      '(=> % abort and begin begin0 case case-lambda cond define define! define-macro!
                           define-syntax defrec! delay do else extend-syntax fluid-let if lambda let let*
                           letrec let-syntax letrec-syntax or quasiquote quote rec record-case record-evcase
                           recur set! sigma struct syntax syntax-rules trace trace-lambda trace-let
                           trace-recur unless unquote unquote-splicing untrace when with)))))

(def variable-tokens (volatile! nil))

(def constant-tokens (volatile! nil))

(def special-symbols
  (volatile!
   (list (cons "." ".")
         (cons "..." "{\\dots}")
         (cons "-" "$-$")
         (cons "1-" "\\va{1$-$}")
         (cons "-1+" "\\va{$-$1$+$}"))))

(def macro-definers
  (seq->list '("define-syntax" "syntax-rules" "defmacro" "extend-syntax" "define-macro!")))

(def case-and-ilk (seq->list '("case" "record-case")))

(def ^:private tex-special-chars (seq->list '(\$ \& \% \# \_)))
(def ^:private brace-chars (seq->list '(\{ \})))

(defn tex-analog [c]
  (cond (memv c tex-special-chars) (str \\ c)
        (memv c brace-chars) (str \$ \\ c \$)
        (= c \\) "$\\backslash$"
        (= c \+) "$+$"
        (= c \=) "$=$"
        (= c \<) "$\\lt$"
        (= c \>) "$\\gt$"
        (= c \^) "\\^{}"
        (= c \|) "$\\vert$"
        (= c \~) "\\~{}"
        (= c \@) "{\\atsign}"
        (= c \") "{\\tt\\dq}"
        :else (str c)))

(def slatex-enabled? (volatile! true))

(def slatex-reenabler (volatile! "UNDEFINED"))

(def intext-triggerers (volatile! (list "scheme")))

(def resultintext-triggerers (volatile! (list "schemeresult")))

(def display-triggerers (volatile! (list "schemedisplay")))

(def box-triggerers (volatile! (list "schemebox")))

(def input-triggerers (volatile! (list "schemeinput")))

(def region-triggerers (volatile! (list "schemeregion")))

(def math-triggerers (volatile! nil))

(def slatex-in-protected-region? (volatile! false))

(def protected-files (volatile! nil))

(def include-onlys (volatile! :all))

(def latex? (volatile! true))

(def slatex-separate-includes? (volatile! false))

(defn set-keyword [x]
  (if (member-token x @keyword-tokens)
    :skip
    (do (vreset! constant-tokens (remove-token! x @constant-tokens))
        (vreset! variable-tokens (remove-token! x @variable-tokens))
        (vreset! keyword-tokens (cons x @keyword-tokens)))))

(defn set-constant [x]
  (if (member-token x @constant-tokens)
    :skip
    (do (vreset! keyword-tokens (remove-token! x @keyword-tokens))
        (vreset! variable-tokens (remove-token! x @variable-tokens))
        (vreset! constant-tokens (cons x @constant-tokens)))))

(defn set-variable [x]
  (if (member-token x @variable-tokens)
    :skip
    (do (vreset! keyword-tokens (remove-token! x @keyword-tokens))
        (vreset! constant-tokens (remove-token! x @constant-tokens))
        (vreset! variable-tokens (cons x @variable-tokens)))))

(defn set-special-symbol [x transl]
  (let [c (assoc-token x @special-symbols)]
    (if c
      (set-cdr! c transl)
      (vreset! special-symbols (cons (cons x transl) @special-symbols)))))

(defn unset-special-symbol [x]
  (vreset! special-symbols
           (remove-if! (fn [c] (token=? (car c) x)) @special-symbols)))

(defn texify [s] (list->string (texify-aux s)))

(defn texify-data [s]
  (loop [l (texify-aux s) r nil]
    (if (null? l)
      (list->string (reverse! r))
      (let [c (car l)]
        (recur (cdr l)
               (if (= c \-)
                 (append! (list \$ c \$) r)
                 (cons c r)))))))

(def texify-aux
  (let [arrow (string->list "-$>$") arrow-lh (length arrow)]
    (fn [s]
      (let [sl (string->list s)
            texified-sl (append-map! (fn [c] (string->list (tex-analog c))) sl)]
        (ormapcdr
         (fn [d]
           (when (list-prefix? arrow d)
             (let [to (string->list "$\\to$")]
               (set-car! d (car to))
               (set-cdr! d (append (cdr to) (list-tail d arrow-lh)))))
           false)
         texified-sl)
        texified-sl))))

(def latex-paragraph-mode? (volatile! :fwd1))

(def intext? (volatile! :fwd2))

(def code-env-spec (volatile! "UNDEFINED"))

(def in-port (volatile! :fwd3))

(def out-port (volatile! :fwd4))

(def in-qtd-tkn (volatile! :fwd5))

(def in-bktd-qtd-exp (volatile! :fwd6))

(def in-mac-tkn (volatile! :fwd7))

(def in-bktd-mac-exp (volatile! :fwd8))

(def case-stack (volatile! :fwd9))

(def bq-stack (volatile! :fwd10))

(defn display-begin-sequence [out]
  (if (or @intext? (not @latex?))
    (do (display "\\" out)
        (display @code-env-spec out)
        (newline* out))
    (do (display "\\begin{" out)
        (display @code-env-spec out)
        (display "}" out)
        (newline* out))))

(defn display-end-sequence [out]
  (if (or @intext? (not @latex?))
    (do (display "\\end" out)
        (display @code-env-spec out)
        (newline* out))
    (do (display "\\end{" out)
        (display @code-env-spec out)
        (display "}" out)
        (newline* out))))

(defn display-tex-char [c p]
  (display (if (char? c) (tex-analog c) c) p))

(defn display-token [s typ p]
  (cond (= typ :syntax)
        (do (display "\\sy{" p)
            (display (texify s) p)
            (display "}" p))
        (= typ :variable)
        (do (display "\\va{" p)
            (display (texify s) p)
            (display "}" p))
        (= typ :constant)
        (do (display "\\cn{" p)
            (display (texify s) p)
            (display "}" p))
        (= typ :data)
        (do (display "\\dt{" p)
            (display (texify-data s) p)
            (display "}" p))
        :else (error* 'slatex.display-token typ)))

(def max-line-length 200)

(def &inner-space (integer->char 7))
(def &quote-space (integer->char 6))
(def &bracket-space (integer->char 5))
(def &paren-space (integer->char 4))
(def &init-plain-space (integer->char 3))
(def &init-space (integer->char 2))
(def &plain-space (integer->char 1))
(def &void-space (integer->char 0))

(def &plain-crg-ret (integer->char 4))
(def &tabbed-crg-ret (integer->char 3))
(def &move-tab (integer->char 2))
(def &set-tab (integer->char 1))
(def &void-tab (integer->char 0))

(def &end-math (integer->char 8))
(def &mid-math (integer->char 7))
(def &begin-math (integer->char 6))
(def &end-string (integer->char 5))
(def &mid-string (integer->char 4))
(def &begin-string (integer->char 3))
(def &mid-comment (integer->char 2))
(def &begin-comment (integer->char 1))
(def &void-notab (integer->char 0))

(defn make-raw-line [] (make-vector 5))
(def =notab 4)
(def =tab 3)
(def =space 2)
(def =char 1)
(def =rtedge 0)

(defn make-line []
  (let [l (make-raw-line)]
    (vector-set! l =rtedge 0)
    (vector-set! l =char (make-string max-line-length \space))
    (vector-set! l =space (make-string max-line-length &void-space))
    (vector-set! l =tab (make-string max-line-length &void-tab))
    (vector-set! l =notab (make-string max-line-length &void-notab))
    l))

(def line1* (make-line))

(def line2* (make-line))

(defn make-case-frame [] (make-vector 3))
(def =in-case-exp 2)
(def =in-bktd-ctag-exp 1)
(def =in-ctag-tkn 0)

(defn make-bq-frame [] (make-vector 3))
(def =in-bktd-bq-exp 2)
(def =in-bq-tkn 1)
(def =in-comma 0)

(defn display-space [s p]
  (cond (= s &plain-space) (display \space p)
        (= s &init-plain-space) (display \space p)
        (= s &init-space) (display "\\HL " p)
        (= s &paren-space) (display "\\PRN " p)
        (= s &bracket-space) (display "\\BKT " p)
        (= s &quote-space) (display "\\QUO " p)
        (= s &inner-space) (display "\\ " p)))

(defn display-tab [tab p]
  (cond (= tab &set-tab) (display "\\=" p)
        (= tab &move-tab) (display "\\>" p)))

(defn display-notab [notab p]
  (cond (= notab &begin-string) (display "\\dt{" p)
        (= notab &end-string) (display "}" p)))

(def get-line
  (let [curr-notab (volatile! &void-notab)]
    (fn [line]
      (let [graphic-char-seen? (volatile! false)]
        (loop [i 0]
          (let [c (read-char @in-port)]
            (cond @graphic-char-seen? :already-seen
                  (or (eof-object? c)
                      (= c return-char)
                      (= c \newline)
                      (= c \space)
                      (= c tab-char))
                  :not-yet
                  :else (vreset! graphic-char-seen? true))
            (cond (eof-object? c)
                  (do (cond (= @curr-notab &mid-string)
                            (when (> i 0)
                              (string-set! (vector-ref line =notab) (- i 1) &end-string))
                            (= @curr-notab &mid-comment)
                            (vreset! curr-notab &void-notab)
                            (= @curr-notab &mid-math)
                            (error* 'slatex.get-line 'runaway-math-subformula))
                      (string-set! (vector-ref line =char) i \newline)
                      (string-set! (vector-ref line =space) i &void-space)
                      (string-set! (vector-ref line =tab) i &void-tab)
                      (string-set! (vector-ref line =notab) i &void-notab)
                      (vector-set! line =rtedge i)
                      (when (= (mstring-ref (vector-ref line =notab) 0) &mid-string)
                        (string-set! (vector-ref line =notab) 0 &begin-string))
                      (if (== i 0) false true))

                  (or (= c return-char) (= c \newline))
                  (do (when (and (= op-sys :dos) (= c return-char))
                        (when (= (peek-char @in-port) \newline)
                          (read-char @in-port)))
                      (cond (= @curr-notab &mid-string)
                            (when (> i 0)
                              (string-set! (vector-ref line =notab) (- i 1) &end-string))
                            (= @curr-notab &mid-comment)
                            (vreset! curr-notab &void-notab)
                            (= @curr-notab &mid-math)
                            (error* 'slatex.get-line 'runaway-math-subformula))
                      (string-set! (vector-ref line =char) i \newline)
                      (string-set! (vector-ref line =space) i &void-space)
                      (string-set! (vector-ref line =tab) i
                                   (cond (eof-object? (peek-char @in-port)) &plain-crg-ret
                                         @intext? &plain-crg-ret
                                         :else &tabbed-crg-ret))
                      (string-set! (vector-ref line =notab) i &void-notab)
                      (vector-set! line =rtedge i)
                      (when (= (mstring-ref (vector-ref line =notab) 0) &mid-string)
                        (string-set! (vector-ref line =notab) 0 &begin-string))
                      true)

                  (= @curr-notab &mid-comment)
                  (do (string-set! (vector-ref line =char) i c)
                      (string-set! (vector-ref line =space) i
                                   (cond (= c \space) &plain-space
                                         (= c tab-char) &plain-space
                                         :else &void-space))
                      (string-set! (vector-ref line =tab) i &void-tab)
                      (string-set! (vector-ref line =notab) i &mid-comment)
                      (recur (+ i 1)))

                  (= c \\)
                  (do (string-set! (vector-ref line =char) i c)
                      (string-set! (vector-ref line =space) i &void-space)
                      (string-set! (vector-ref line =tab) i &void-tab)
                      (string-set! (vector-ref line =notab) i @curr-notab)
                      (let [i+1 (+ i 1)
                            c+1 (read-char @in-port)
                            c+1 (if (= c+1 tab-char) \space c+1)]
                        (string-set! (vector-ref line =char) i+1 c+1)
                        (string-set! (vector-ref line =space) i+1
                                     (if (= c+1 \space) &plain-space &void-space))
                        (string-set! (vector-ref line =tab) i+1 &void-tab)
                        (string-set! (vector-ref line =notab) i+1 @curr-notab)
                        (recur (+ i+1 1))))

                  (= @curr-notab &mid-math)
                  (let [c (if (= c tab-char) \space c)]
                    (string-set! (vector-ref line =space) i
                                 (if (= c \space) &plain-space &void-space))
                    (string-set! (vector-ref line =tab) i &void-tab)
                    (cond (memv c @math-triggerers)
                          (do (string-set! (vector-ref line =char) i \$)
                              (string-set! (vector-ref line =notab) i &end-math)
                              (vreset! curr-notab &void-notab))
                          :else
                          (do (string-set! (vector-ref line =char) i c)
                              (string-set! (vector-ref line =notab) i &mid-math)))
                    (recur (+ i 1)))

                  (= @curr-notab &mid-string)
                  (let [c (if (= c tab-char) \space c)]
                    (string-set! (vector-ref line =char) i c)
                    (string-set! (vector-ref line =space) i
                                 (if (= c \space) &inner-space &void-space))
                    (string-set! (vector-ref line =tab) i &void-tab)
                    (string-set! (vector-ref line =notab) i
                                 (cond (= c \")
                                       (do (vreset! curr-notab &void-notab)
                                           &end-string)
                                       :else &mid-string))
                    (recur (+ i 1)))

                  (= c \space)
                  (do (string-set! (vector-ref line =char) i c)
                      (string-set! (vector-ref line =space) i
                                   (cond @intext? &plain-space
                                         @graphic-char-seen? &inner-space
                                         :else &init-space))
                      (string-set! (vector-ref line =tab) i &void-tab)
                      (string-set! (vector-ref line =notab) i &void-notab)
                      (recur (+ i 1)))

                  (= c tab-char)
                  (do (loop [i i j 0]
                        (when (< j 8)
                          (string-set! (vector-ref line =char) i \space)
                          (string-set! (vector-ref line =space) i
                                       (cond @intext? &plain-space
                                             @graphic-char-seen? &inner-space
                                             :else &init-space))
                          (string-set! (vector-ref line =tab) i &void-tab)
                          (string-set! (vector-ref line =notab) i &void-notab)
                          (recur (+ i 1) (+ j 1))))
                      (recur (+ i 8)))

                  (= c \")
                  (do (string-set! (vector-ref line =char) i c)
                      (string-set! (vector-ref line =space) i &void-space)
                      (string-set! (vector-ref line =tab) i &void-tab)
                      (string-set! (vector-ref line =notab) i &begin-string)
                      (vreset! curr-notab &mid-string)
                      (recur (+ i 1)))

                  (= c \;)
                  (do (string-set! (vector-ref line =char) i c)
                      (string-set! (vector-ref line =space) i &void-space)
                      (string-set! (vector-ref line =tab) i &void-tab)
                      (string-set! (vector-ref line =notab) i &begin-comment)
                      (vreset! curr-notab &mid-comment)
                      (recur (+ i 1)))

                  (memv c @math-triggerers)
                  (do (string-set! (vector-ref line =char) i \$)
                      (string-set! (vector-ref line =space) i &void-space)
                      (string-set! (vector-ref line =tab) i &void-tab)
                      (string-set! (vector-ref line =notab) i &begin-math)
                      (vreset! curr-notab &mid-math)
                      (recur (+ i 1)))

                  :else
                  (do (string-set! (vector-ref line =char) i c)
                      (string-set! (vector-ref line =space) i &void-space)
                      (string-set! (vector-ref line =tab) i &void-tab)
                      (string-set! (vector-ref line =notab) i &void-notab)
                      (recur (+ i 1))))))))))

(def ^:private quote-chars (seq->list '(\' \` \,)))

(defn peephole-adjust [curr prev]
  (if (or (blank-line? curr) (flush-comment-line? curr))
    (if @latex-paragraph-mode?
      :skip
      (do (vreset! latex-paragraph-mode? true)
          (if @intext?
            :skip
            (do (remove-some-tabs prev 0)
                (let [prev-rtedge (vector-ref prev =rtedge)]
                  (when (= (mstring-ref (vector-ref prev =tab) prev-rtedge) &tabbed-crg-ret)
                    (string-set! (vector-ref prev =tab) (vector-ref prev =rtedge) &plain-crg-ret)))))))
    (do (if @latex-paragraph-mode?
          (vreset! latex-paragraph-mode? false)
          (if @intext?
            :skip
            (let [remove-tabs-from (volatile! false)]
              (loop [i 0]
                (cond (= (mstring-ref (vector-ref curr =char) i) \newline)
                      (vreset! remove-tabs-from i)

                      (= (mstring-ref (vector-ref prev =char) i) \newline)
                      (vreset! remove-tabs-from false)

                      (= (mstring-ref (vector-ref curr =space) i) &init-space)
                      (do (when (= (mstring-ref (vector-ref prev =notab) i) &void-notab)
                            (cond (or (= (mstring-ref (vector-ref prev =char) i) \()
                                      (= (mstring-ref (vector-ref prev =space) i) &paren-space))
                                  (string-set! (vector-ref curr =space) i &paren-space)
                                  (or (= (mstring-ref (vector-ref prev =char) i) \[)
                                      (= (mstring-ref (vector-ref prev =space) i) &bracket-space))
                                  (string-set! (vector-ref curr =space) i &bracket-space)
                                  (or (memv (mstring-ref (vector-ref prev =char) i) quote-chars)
                                      (= (mstring-ref (vector-ref prev =space) i) &quote-space))
                                  (string-set! (vector-ref curr =space) i &quote-space))
                            (when (memv (mstring-ref (vector-ref prev =tab) i)
                                        (list &set-tab &move-tab))
                              (string-set! (vector-ref curr =tab) i &move-tab)))
                          (recur (+ i 1)))

                      (== i 0)
                      (vreset! remove-tabs-from 0)

                      (not (= (mstring-ref (vector-ref prev =tab) i) &void-tab))
                      (do (vreset! remove-tabs-from (+ i 1))
                          (when (memv (mstring-ref (vector-ref prev =tab) i)
                                      (list &set-tab &move-tab))
                            (string-set! (vector-ref curr =tab) i &move-tab)))

                      (memv (mstring-ref (vector-ref prev =space) i)
                            (list &init-space &init-plain-space &paren-space &bracket-space &quote-space))
                      (vreset! remove-tabs-from (+ i 1))

                      (and (= (mstring-ref (vector-ref prev =char) (- i 1)) \space)
                           (= (mstring-ref (vector-ref prev =notab) (- i 1)) &void-notab))
                      (do (vreset! remove-tabs-from (+ i 1))
                          (string-set! (vector-ref prev =tab) i &set-tab)
                          (string-set! (vector-ref curr =tab) i &move-tab))

                      :else
                      (do (vreset! remove-tabs-from (+ i 1))
                          (loop [j (- i 1)]
                            (cond (<= j 0) :exit-loop1
                                  (not (= (mstring-ref (vector-ref curr =tab) j) &void-tab)) :exit-loop1
                                  (memv (mstring-ref (vector-ref curr =space) j)
                                        (list &paren-space &bracket-space &quote-space))
                                  (recur (- j 1))
                                  (or (not (= (mstring-ref (vector-ref prev =notab) j) &void-notab))
                                      (= (mstring-ref (vector-ref prev =char) j) \space))
                                  (let [k (+ j 1)]
                                    (if (memv (mstring-ref (vector-ref prev =notab) k)
                                              (list &mid-comment &mid-math &end-math &mid-string &end-string))
                                      :skip
                                      (do (when (= (mstring-ref (vector-ref prev =tab) k) &void-tab)
                                            (string-set! (vector-ref prev =tab) k &set-tab))
                                          (string-set! (vector-ref curr =tab) k &move-tab))))
                                  :else :anything-else?)))))
              (remove-some-tabs prev @remove-tabs-from))))
        (if @intext? :skip (add-some-tabs curr))
        (clean-init-spaces curr)
        (clean-inner-spaces curr))))

(defn add-some-tabs [line]
  (loop [i 1 succ-parens? false]
    (let [c (mstring-ref (vector-ref line =char) i)]
      (cond (= c \newline) :exit-loop
            (not (= (mstring-ref (vector-ref line =notab) i) &void-notab))
            (recur (+ i 1) false)
            (= c \[)
            (do (when (= (mstring-ref (vector-ref line =tab) i) &void-tab)
                  (string-set! (vector-ref line =tab) i &set-tab))
                (recur (+ i 1) false))
            (= c \()
            (do (when (= (mstring-ref (vector-ref line =tab) i) &void-tab)
                  (if succ-parens?
                    :skip
                    (string-set! (vector-ref line =tab) i &set-tab)))
                (recur (+ i 1) true))
            :else (recur (+ i 1) false)))))

(defn remove-some-tabs [line i]
  (when i
    (loop [i i]
      (cond (= (mstring-ref (vector-ref line =char) i) \newline) :exit
            (= (mstring-ref (vector-ref line =tab) i) &set-tab)
            (do (string-set! (vector-ref line =tab) i &void-tab)
                (recur (+ i 1)))
            :else (recur (+ i 1))))))

(defn clean-init-spaces [line]
  (loop [i (vector-ref line =rtedge)]
    (cond (< i 0) :exit-loop
          (= (mstring-ref (vector-ref line =tab) i) &move-tab)
          (loop [i (- i 1)]
            (cond (< i 0) :exit-loop2
                  (memv (mstring-ref (vector-ref line =space) i)
                        (list &init-space &paren-space &bracket-space &quote-space))
                  (do (string-set! (vector-ref line =space) i &init-plain-space)
                      (recur (- i 1)))
                  :else (recur (- i 1))))
          :else (recur (- i 1)))))

(defn clean-inner-spaces [line]
  (loop [i 0 succ-inner-spaces? false]
    (cond (= (mstring-ref (vector-ref line =char) i) \newline)
          :exit-loop
          (= (mstring-ref (vector-ref line =space) i) &inner-space)
          (do (if succ-inner-spaces?
                :skip
                (string-set! (vector-ref line =space) i &plain-space))
              (recur (+ i 1) true))
          :else (recur (+ i 1) false))))

(defn blank-line? [line]
  (loop [i 0]
    (let [c (mstring-ref (vector-ref line =char) i)]
      (cond (= c \space)
            (if (= (mstring-ref (vector-ref line =notab) i) &void-notab)
              (recur (+ i 1))
              false)
            (= c \newline)
            (do (loop [j (- i 1)]
                  (if (<= j 0)
                    :skip
                    (do (string-set! (vector-ref line =space) i &void-space)
                        (recur (- j 1)))))
                true)
            :else false))))

(defn flush-comment-line? [line]
  (and (= (mstring-ref (vector-ref line =char) 0) \;)
       (= (mstring-ref (vector-ref line =notab) 0) &begin-comment)
       (not (= (mstring-ref (vector-ref line =char) 1) \;))))

(defn do-all-lines []
  (loop [line1 line1* line2 line2*]
    (let [line2-paragraph? @latex-paragraph-mode?
          more? (get-line line1)]
      (peephole-adjust line1 line2)
      ((if line2-paragraph? display-tex-line display-scm-line) line2)
      (if (= line2-paragraph? @latex-paragraph-mode?)
        :else
        ((if @latex-paragraph-mode? display-end-sequence display-begin-sequence)
         @out-port))
      (when more? (recur line2 line1)))))

(defn scheme2tex [inport outport]
  (vreset! in-port inport)
  (vreset! out-port outport)
  (vreset! latex-paragraph-mode? true)
  (vreset! in-qtd-tkn false)
  (vreset! in-bktd-qtd-exp 0)
  (vreset! in-mac-tkn false)
  (vreset! in-bktd-mac-exp 0)
  (vreset! case-stack nil)
  (vreset! bq-stack nil)
  (let [flush-line (fn [line]
                     (vector-set! line =rtedge 0)
                     (string-set! (vector-ref line =char) 0 \newline)
                     (string-set! (vector-ref line =space) 0 &void-space)
                     (string-set! (vector-ref line =tab) 0 &void-tab)
                     (string-set! (vector-ref line =notab) 0 &void-notab))]
    (flush-line line1*)
    (flush-line line2*))
  (do-all-lines))

(defn display-tex-line [line]
  (loop [i (if (flush-comment-line? line) 1 0)]
    (let [c (mstring-ref (vector-ref line =char) i)]
      (if (= c \newline)
        (if (= (mstring-ref (vector-ref line =tab) i) &void-tab)
          :skip
          (newline* @out-port))
        (do (display c @out-port) (recur (+ i 1)))))))

(def ^:private open-chars (seq->list '(\( \[)))
(def ^:private close-chars (seq->list '(\) \])))

(defn display-scm-line [line]
  (loop [i 0]
    (let [c (mstring-ref (vector-ref line =char) i)]
      (cond (= c \newline)
            (let [tab (mstring-ref (vector-ref line =tab) i)]
              (cond (= tab &tabbed-crg-ret)
                    (do (display "\\\\" @out-port)
                        (newline* @out-port))
                    (= tab &plain-crg-ret)
                    (newline* @out-port)
                    (= tab &void-tab)
                    (do (display \% @out-port)
                        (newline* @out-port))))

            (= (mstring-ref (vector-ref line =notab) i) &begin-comment)
            (do (display-tab (mstring-ref (vector-ref line =tab) i) @out-port)
                (display c @out-port)
                (recur (+ i 1)))

            (= (mstring-ref (vector-ref line =notab) i) &mid-comment)
            (do (display c @out-port)
                (recur (+ i 1)))

            (= (mstring-ref (vector-ref line =notab) i) &begin-string)
            (do (display-tab (mstring-ref (vector-ref line =tab) i) @out-port)
                (display "\\dt{" @out-port)
                (if (= c \space)
                  (display-space (mstring-ref (vector-ref line =space) i) @out-port)
                  (display-tex-char c @out-port))
                (recur (+ i 1)))

            (= (mstring-ref (vector-ref line =notab) i) &mid-string)
            (do (if (= c \space)
                  (display-space (mstring-ref (vector-ref line =space) i) @out-port)
                  (display-tex-char c @out-port))
                (recur (+ i 1)))

            (= (mstring-ref (vector-ref line =notab) i) &end-string)
            (do (if (= c \space)
                  (display-space (mstring-ref (vector-ref line =space) i) @out-port)
                  (display-tex-char c @out-port))
                (display "}" @out-port)
                (recur (+ i 1)))

            (= (mstring-ref (vector-ref line =notab) i) &begin-math)
            (do (display-tab (mstring-ref (vector-ref line =tab) i) @out-port)
                (display c @out-port)
                (recur (+ i 1)))

            (memv (mstring-ref (vector-ref line =notab) i) (list &mid-math &end-math))
            (do (display c @out-port)
                (recur (+ i 1)))

            (= c \space)
            (do (display-tab (mstring-ref (vector-ref line =tab) i) @out-port)
                (display-space (mstring-ref (vector-ref line =space) i) @out-port)
                (recur (+ i 1)))

            (= c \')
            (do (display-tab (mstring-ref (vector-ref line =tab) i) @out-port)
                (display c @out-port)
                (if (or @in-qtd-tkn (> @in-bktd-qtd-exp 0))
                  :skip
                  (vreset! in-qtd-tkn true))
                (recur (+ i 1)))

            (= c \`)
            (do (display-tab (mstring-ref (vector-ref line =tab) i) @out-port)
                (display c @out-port)
                (when (or (null? @bq-stack)
                          (vector-ref (car @bq-stack) =in-comma))
                  (vreset! bq-stack
                           (cons (let [f (make-bq-frame)]
                                   (vector-set! f =in-comma false)
                                   (vector-set! f =in-bq-tkn true)
                                   (vector-set! f =in-bktd-bq-exp 0)
                                   f)
                                 @bq-stack)))
                (recur (+ i 1)))

            (= c \,)
            (do (display-tab (mstring-ref (vector-ref line =tab) i) @out-port)
                (display c @out-port)
                (if (or (null? @bq-stack)
                        (vector-ref (car @bq-stack) =in-comma))
                  :skip
                  (vreset! bq-stack
                           (cons (let [f (make-bq-frame)]
                                   (vector-set! f =in-comma true)
                                   (vector-set! f =in-bq-tkn true)
                                   (vector-set! f =in-bktd-bq-exp 0)
                                   f)
                                 @bq-stack)))
                (if (= (mstring-ref (vector-ref line =char) (+ i 1)) \@)
                  (do (display-tex-char \@ @out-port)
                      (recur (+ 2 i)))
                  (recur (+ i 1))))

            (memv c open-chars)
            (do (display-tab (mstring-ref (vector-ref line =tab) i) @out-port)
                (display c @out-port)
                (cond @in-qtd-tkn
                      (do (vreset! in-qtd-tkn false)
                          (vreset! in-bktd-qtd-exp 1))
                      (> @in-bktd-qtd-exp 0)
                      (vreset! in-bktd-qtd-exp (+ @in-bktd-qtd-exp 1)))
                (cond @in-mac-tkn
                      (do (vreset! in-mac-tkn false)
                          (vreset! in-bktd-mac-exp 1))
                      (> @in-bktd-mac-exp 0)
                      (vreset! in-bktd-mac-exp (+ @in-bktd-mac-exp 1)))
                (if (null? @bq-stack)
                  :skip
                  (let [top (car @bq-stack)]
                    (cond (vector-ref top =in-bq-tkn)
                          (do (vector-set! top =in-bq-tkn false)
                              (vector-set! top =in-bktd-bq-exp 1))
                          (> (vector-ref top =in-bktd-bq-exp) 0)
                          (vector-set! top =in-bktd-bq-exp (+ (vector-ref top =in-bktd-bq-exp) 1)))))
                (if (null? @case-stack)
                  :skip
                  (let [top (car @case-stack)]
                    (cond (vector-ref top =in-ctag-tkn)
                          (do (vector-set! top =in-ctag-tkn false)
                              (vector-set! top =in-bktd-ctag-exp 1))
                          (> (vector-ref top =in-bktd-ctag-exp) 0)
                          (vector-set! top =in-bktd-ctag-exp (+ (vector-ref top =in-bktd-ctag-exp) 1))
                          (> (vector-ref top =in-case-exp) 0)
                          (do (vector-set! top =in-case-exp (+ (vector-ref top =in-case-exp) 1))
                              (when (== (vector-ref top =in-case-exp) 2)
                                (vreset! in-qtd-tkn true))))))
                (recur (+ i 1)))

            (memv c close-chars)
            (do (display-tab (mstring-ref (vector-ref line =tab) i) @out-port)
                (display c @out-port)
                (when (> @in-bktd-qtd-exp 0)
                  (vreset! in-bktd-qtd-exp (- @in-bktd-qtd-exp 1)))
                (when (> @in-bktd-mac-exp 0)
                  (vreset! in-bktd-mac-exp (- @in-bktd-mac-exp 1)))
                (if (null? @bq-stack)
                  :skip
                  (let [top (car @bq-stack)]
                    (when (> (vector-ref top =in-bktd-bq-exp) 0)
                      (vector-set! top =in-bktd-bq-exp (- (vector-ref top =in-bktd-bq-exp) 1))
                      (when (== (vector-ref top =in-bktd-bq-exp) 0)
                        (vreset! bq-stack (cdr @bq-stack))))))
                (loop []
                  (if (null? @case-stack)
                    :skip
                    (let [top (car @case-stack)]
                      (cond (> (vector-ref top =in-bktd-ctag-exp) 0)
                            (do (vector-set! top =in-bktd-ctag-exp (- (vector-ref top =in-bktd-ctag-exp) 1))
                                (when (== (vector-ref top =in-bktd-ctag-exp) 0)
                                  (vector-set! top =in-case-exp 1)))
                            (> (vector-ref top =in-case-exp) 0)
                            (do (vector-set! top =in-case-exp (- (vector-ref top =in-case-exp) 1))
                                (when (== (vector-ref top =in-case-exp) 0)
                                  (vreset! case-stack (cdr @case-stack))
                                  (recur)))))))
                (recur (+ i 1)))

            :else
            (do (display-tab (mstring-ref (vector-ref line =tab) i) @out-port)
                (recur (do-token line i)))))))

(def do-token
  (let [token-delims (list \( \) \[ \] \space return-char \newline \, \@ \;)]
    (fn [line i]
      (loop [buf nil i i]
        (let [c (mstring-ref (vector-ref line =char) i)]
          (cond (= c \\)
                (recur (cons (mstring-ref (vector-ref line =char) (+ i 1))
                             (cons c buf))
                       (+ i 2))
                (or (memv c token-delims) (memv c @math-triggerers))
                (do (output-token (list->string (reverse! buf)))
                    i)
                (char? c)
                (recur (cons (mstring-ref (vector-ref line =char) i) buf)
                       (+ i 1))
                :else (error* 'slatex.do-token 1)))))))

(defn output-token [token]
  (if (null? @case-stack)
    :skip
    (let [top (car @case-stack)]
      (when (vector-ref top =in-ctag-tkn)
        (vector-set! top =in-ctag-tkn false)
        (vector-set! top =in-case-exp 1))))
  (if (assoc-token token @special-symbols)
    (display (cdr (assoc-token token @special-symbols)) @out-port)
    (display-token
     token
     (cond @in-qtd-tkn
           (do (vreset! in-qtd-tkn false)
               (cond (= token "else") :syntax
                     (data-token? token) :data
                     :else :constant))
           (data-token? token) :data
           (> @in-bktd-qtd-exp 0) :constant
           (and (not (null? @bq-stack))
                (not (vector-ref (car @bq-stack) =in-comma)))
           :constant
           @in-mac-tkn
           (do (vreset! in-mac-tkn false)
               (set-keyword token)
               :syntax)
           (> @in-bktd-mac-exp 0)
           (do (set-keyword token)
               :syntax)
           (member-token token @constant-tokens) :constant
           (member-token token @variable-tokens) :variable
           (member-token token @keyword-tokens)
           (do (cond (token=? token "quote")
                     (vreset! in-qtd-tkn true)
                     (member-token token macro-definers)
                     (vreset! in-mac-tkn true)
                     (member-token token case-and-ilk)
                     (vreset! case-stack
                              (cons (let [f (make-case-frame)]
                                      (vector-set! f =in-ctag-tkn true)
                                      (vector-set! f =in-bktd-ctag-exp 0)
                                      (vector-set! f =in-case-exp 0)
                                      f)
                                    @case-stack)))
               :syntax)
           :else :variable)
     @out-port))
  (when (and (not (null? @bq-stack))
             (vector-ref (car @bq-stack) =in-bq-tkn))
    (vreset! bq-stack (cdr @bq-stack))))

(defn data-token? [token]
  (or (= (string-ref token 0) \#) (string->number token)))

(def texinputs "")

(def texinputs-list (volatile! nil))

(def path-separator
  (cond (= op-sys :unix) \:
        (= op-sys :dos) \;
        :else (error* 'slatex.*path-separator* 'cant-determine)))

(def directory-mark
  (cond (= op-sys :unix) "/"
        (= op-sys :dos) "\\"
        :else (error* 'slatex.*directory-mark* 'cant-determine)))

(def file-hider
  (cond (= op-sys :unix) "outputs/"
        (= op-sys :dos) "x"
        :else "."))

(defn path->list [p]
  (loop [p (string->list p) r (list "")]
    (let [separator-pos (position-char path-separator p)]
      (if separator-pos
        (recur (list-tail p (+ separator-pos 1))
               (cons (list->string (sublist p 0 separator-pos)) r))
        (reverse! (cons (list->string p) r))))))

(defn find-some-file [path & files]
  (let [files (seq->list files)]
    (loop [path path]
      (if (null? path)
        false
        (let [dir (car path)
              r (loop [files (if (or (= dir "") (= dir "."))
                               files
                               (map (fn [file] (str dir directory-mark file)) files))]
                  (if (null? files)
                    ::next
                    (let [file (car files)]
                      (if (slatex-file-exists? file)
                        file
                        (recur (cdr files))))))]
          ;; 原版内层 loop2 在找不到时尾调用外层 loop：这里内层返回 ::next，外层 recur
          (if (= r ::next) (recur (cdr path)) r))))))

(defn file-extension [filename]
  (let [i (string-position-right \. filename)]
    (if i (substring filename i (string-length filename)) false)))

(defn basename [filename ext]
  (let [filename-len (string-length filename)
        ext-len (string-length ext)
        len-diff (- filename-len ext-len)]
    (cond (> ext-len filename-len) filename
          (= ext (substring filename len-diff filename-len)) (substring filename 0 len-diff)
          :else filename)))

(defn full-texfile-name [filename]
  (let [extn (file-extension filename)]
    (if (and extn (or (= extn ".sty") (= extn ".tex")))
      (find-some-file @texinputs-list filename)
      (find-some-file @texinputs-list (str filename ".tex") filename))))

(defn full-scmfile-name [filename]
  (apply find-some-file
         @texinputs-list
         filename
         (list->seq (map (fn [extn] (str filename extn))
                         (list ".scm" ".ss" ".s")))))

(defn new-aux-file [& e]
  (apply (if @slatex-in-protected-region?
           new-secondary-aux-file
           new-primary-aux-file)
         e))

(def subjobname (volatile! :fwd))

(def primary-aux-file-count (volatile! -1))

(defn new-primary-aux-file [& e]
  (vreset! primary-aux-file-count (+ @primary-aux-file-count 1))
  (apply str file-hider "z" (str @primary-aux-file-count) e))

(def new-secondary-aux-file
  (let [n (volatile! -1)]
    (fn [& e]
      (vreset! n (+ @n 1))
      (apply str file-hider "zz" (str @n) e))))

(defn eat-till-newline [in]
  (loop []
    (let [c (read-char in)]
      (cond (eof-object? c) :done
            (= c \newline) :done
            :else (recur)))))

(defn read-ctrl-seq [in]
  (let [c (read-char in)]
    (when (eof-object? c) (error* 'read-ctrl-exp 1))
    (if (char-alphabetic? c)
      (list->string
       (reverse!
        (loop [s (list c)]
          (let [c (peek-char in)]
            (cond (eof-object? c) s
                  (char-alphabetic? c) (do (read-char in) (recur (cons c s)))
                  (= c \%) (do (eat-till-newline in) (recur s))
                  :else s)))))
      (str c))))

(defn eat-tabspace [in]
  (loop []
    (let [c (peek-char in)]
      (cond (eof-object? c) :done
            (or (= c \space) (= c tab-char)) (do (read-char in) (recur))
            :else :done))))

(defn eat-whitespace [in]
  (loop []
    (let [c (peek-char in)]
      (cond (eof-object? c) :done
            (char-whitespace? c) (do (read-char in) (recur))
            :else :done))))

(defn eat-latex-whitespace [in]
  (loop []
    (let [c (peek-char in)]
      (cond (eof-object? c) :done
            (char-whitespace? c) (do (read-char in) (recur))
            (= c \%) (eat-till-newline in)
            :else :done))))

(defn chop-off-whitespace [l]
  (ormapcdr (fn [d] (if (char-whitespace? (car d)) false d)) l))

(defn read-grouped-latexexp [in]
  (eat-latex-whitespace in)
  (let [c (read-char in)]
    (when (eof-object? c) (error* 'slatex.read-grouped-latexexp 1))
    (if (= c \{) :ok (error* 'slatex.read-grouped-latexexp 2))
    (eat-latex-whitespace in)
    (list->string
     (reverse!
      (chop-off-whitespace
       (loop [s nil nesting 0 escape? false]
         (let [c (read-char in)]
           (when (eof-object? c)
             (error* 'slatex.read-grouped-latexexp 3))
           (cond escape? (recur (cons c s) nesting false)
                 (= c \\) (recur (cons c s) nesting true)
                 (= c \%) (do (eat-till-newline in)
                              (recur s nesting false))
                 (= c \{) (recur (cons c s) (+ nesting 1) false)
                 (= c \}) (if (== nesting 0) s (recur (cons c s) (- nesting 1) false))
                 :else (recur (cons c s) nesting false)))))))))

(def read-filename
  (let [filename-delims (list \{ \} \[ \] \( \) \# \% \\ \, \space return-char \newline tab-char)]
    (fn [in]
      (eat-latex-whitespace in)
      (let [c (peek-char in)]
        (when (eof-object? c) (error* 'slatex.read-filename 1))
        (if (= c \{)
          (read-grouped-latexexp in)
          (list->string
           (reverse!
            (loop [s nil escape? false]
              (let [c (peek-char in)]
                (cond (eof-object? c)
                      (if escape? (error* 'slatex.read-filename 2) s)
                      escape? (do (read-char in) (recur (cons c s) false))
                      (= c \\) (do (read-char in) (recur (cons c s) true))
                      (memv c filename-delims) s
                      :else (do (read-char in) (recur (cons c s) false))))))))))))

(def read-schemeid
  (let [schemeid-delims (list \{ \} \[ \] \( \) \space return-char \newline tab-char)]
    (fn [in]
      (eat-whitespace in)
      (list->string
       (reverse!
        (loop [s nil escape? false]
          (let [c (peek-char in)]
            (cond (eof-object? c) s
                  escape? (do (read-char in) (recur (cons c s) false))
                  (= c \\) (do (read-char in) (recur (cons c s) true))
                  (memv c schemeid-delims) s
                  :else (do (read-char in) (recur (cons c s) false))))))))))

(defn read-delimed-commaed-filenames [in lft-delim rt-delim]
  (eat-latex-whitespace in)
  (let [c (read-char in)]
    (when (eof-object? c)
      (error* 'slatex.read-delimed-commaed-filenames 1))
    (if (= c lft-delim)
      :ok
      (error* 'slatex.read-delimed-commaed-filenames 2))
    (loop [s nil]
      (eat-latex-whitespace in)
      (let [c (peek-char in)]
        (when (eof-object? c)
          (error* 'slatex.read-delimed-commaed-filenames 3))
        (if (= c rt-delim)
          (do (read-char in) (reverse! s))
          (let [s (cons (read-filename in) s)]
            (eat-latex-whitespace in)
            (let [c (peek-char in)]
              (when (eof-object? c)
                (error* 'slatex.read-delimed-commaed-filenames 4))
              (cond (= c \,) (read-char in)
                    (= c rt-delim) :void
                    :else (error* 'slatex.read-delimed-commaed-filenames 5))
              (recur s))))))))

(defn read-grouped-commaed-filenames [in] (read-delimed-commaed-filenames in \{ \}))

(defn read-bktd-commaed-filenames [in] (read-delimed-commaed-filenames in \[ \]))

(defn read-grouped-schemeids [in]
  (eat-latex-whitespace in)
  (let [c (read-char in)]
    (when (eof-object? c) (error* 'slatex.read-grouped-schemeids 1))
    (if (= c \{) :ok (error* 'slatex.read-grouped-schemeids 2))
    (loop [s nil]
      (eat-whitespace in)
      (let [c (peek-char in)]
        (when (eof-object? c) (error* 'slatex.read-grouped-schemeids 3))
        (if (= c \})
          (do (read-char in) (reverse! s))
          (recur (cons (read-schemeid in) s)))))))

(defn disable-slatex-temply [in]
  (vreset! slatex-enabled? false)
  (vreset! slatex-reenabler (read-grouped-latexexp in)))

(defn enable-slatex-again []
  (vreset! slatex-enabled? true)
  (vreset! slatex-reenabler "UNDEFINED"))

(defn ignore2 [i ii] :void)

(declare add-to-slatex-db-basic add-to-slatex-db-special)

(def ^:private basic-categs (list :keyword :constant :variable))

(defn add-to-slatex-db [in categ]
  (if (memq categ basic-categs)
    (add-to-slatex-db-basic in categ)
    (add-to-slatex-db-special in categ)))

(defn add-to-slatex-db-basic [in categ]
  (let [setter (cond (= categ :keyword) set-keyword
                     (= categ :constant) set-constant
                     (= categ :variable) set-variable
                     :else (error* 'slatex.add-to-slatex-db-basic 1))
        ids (read-grouped-schemeids in)]
    (for-each setter ids)))

(defn add-to-slatex-db-special [in what]
  (let [ids (read-grouped-schemeids in)]
    (cond (= what :unsetspecialsymbol)
          (for-each unset-special-symbol ids)
          (= what :setspecialsymbol)
          (do (if (== (length ids) 1)
                :ok
                (error* 'slatex.add-to-slatex-db-special 'setspecialsymbol-takes-one-arg-only))
              (let [transl (read-grouped-latexexp in)]
                (set-special-symbol (car ids) transl)))
          :else (error* 'slatex.add-to-slatex-db-special 2))))

(defn process-slatex-alias [in what which]
  (let [triggerer (read-grouped-latexexp in)]
    (cond (= which :intext)
          (vreset! intext-triggerers (what triggerer @intext-triggerers))
          (= which :resultintext)
          (vreset! resultintext-triggerers (what triggerer @resultintext-triggerers))
          (= which :display)
          (vreset! display-triggerers (what triggerer @display-triggerers))
          (= which :box)
          (vreset! box-triggerers (what triggerer @box-triggerers))
          (= which :input)
          (vreset! input-triggerers (what triggerer @input-triggerers))
          (= which :region)
          (vreset! region-triggerers (what triggerer @region-triggerers))
          (= which :mathescape)
          (do (if (== (string-length triggerer) 1)
                :ok
                (error* 'slatex.process-slatex-alias 'math-escape-should-be-character))
              (vreset! math-triggerers (what (string-ref triggerer 0) @math-triggerers)))
          :else (error* 'slatex.process-slatex-alias 2))))

(defn decide-latex-or-tex [latex]
  (vreset! latex? latex)
  (let [pltexchk-jnk "pltexchk.jnk"]
    (when (slatex-file-exists? pltexchk-jnk) (slatex-delete-file pltexchk-jnk))
    (when (not @latex?)
      (call-with-output-file pltexchk-jnk
        (fn [outp] (display 'junk outp) (newline* outp))))))

(defn process-include-only [in]
  (vreset! include-onlys nil)
  (for-each
   (fn [filename]
     (let [filename (full-texfile-name filename)]
       (when filename
         (vreset! include-onlys (adjoin-string filename @include-onlys)))))
   (read-grouped-commaed-filenames in)))

(defn process-documentstyle [in]
  (eat-latex-whitespace in)
  (when (= (peek-char in) \[)
    (for-each
     (fn [filename]
       (let [g0 @slatex-in-protected-region?]
         (vreset! slatex-in-protected-region? false)
         (let [temp (process-tex-file (str filename ".sty"))]
           (vreset! slatex-in-protected-region? g0)
           temp)))
     (read-bktd-commaed-filenames in))))

(defn process-case-info [in]
  (let [bool (read-grouped-latexexp in)]
    (vreset! slatex-case-sensitive?
             (cond (string-ci=? bool "true") true
                   (string-ci=? bool "false") false
                   :else (error* 'slatex.process-case-info 'bad-schemecasesensitive-arg)))))

(def seen-first-command? (volatile! false))

(defn process-main-tex-file [filename]
  (vreset! texinputs-list (path->list texinputs))
  (let [file-hide-file "xZfilhid.tex"]
    (when (slatex-file-exists? file-hide-file)
      (slatex-delete-file file-hide-file))
    (when (= op-sys :dos)
      (call-with-output-file file-hide-file
        (fn [out] (display "\\def\\filehider{x}" out) (newline* out)))))
  (vreset! subjobname (basename filename ".tex"))
  (vreset! seen-first-command? false)
  (process-tex-file filename))

(defn dump-intext [in out]
  (let [display (if out display ignore2)
        delim-char (do (eat-whitespace in) (read-char in))
        delim-char (cond (= delim-char \{) \} :else delim-char)]
    (when (eof-object? delim-char) (error* 'slatex.dump-intext 1))
    (loop []
      (let [c (read-char in)]
        (when (eof-object? c) (error* 'slatex.dump-intext 2))
        (if (= c delim-char) :done (do (display c out) (recur)))))))

(defn dump-display [in out ender]
  (eat-tabspace in)
  (let [display (if out display ignore2)
        ender-lh (string-length ender)
        c (peek-char in)]
    (when (eof-object? c) (error* 'slatex.dump-display 1))
    (when (= c \newline) (read-char in))
    (loop [buf ""]
      (let [c (read-char in)]
        (when (eof-object? c) (error* 'slatex.dump-display 2))
        (let [buf (str buf c)]
          (if (string-prefix? buf ender)
            (if (== (string-length buf) ender-lh) :done (recur buf))
            (do (display buf out) (recur ""))))))))

(def debug? false)

(defn process-tex-file [raw-filename]
  (when debug?
    (display "begin ") (display raw-filename) (newline*))
  (let [filename (full-texfile-name raw-filename)]
    (if (not filename)
      (do (display "[")
          (display raw-filename)
          (display "]")
          (force-output))
      (call-with-input-file filename
        (fn [in]
          (let [done? (volatile! false)]
            (loop []
              (if @done?
                :exit-loop
                (do (let [c (read-char in)]
                      (cond (eof-object? c) (vreset! done? true)
                            (= c \%) (eat-till-newline in)
                            (= c \\)
                            (let [cs (read-ctrl-seq in)]
                              (if @seen-first-command?
                                :skip
                                (do (vreset! seen-first-command? true)
                                    (decide-latex-or-tex (= cs "documentstyle"))))
                              (cond (not @slatex-enabled?)
                                    (when (= cs @slatex-reenabler)
                                      (enable-slatex-again))
                                    (= cs "slatexignorecurrentfile")
                                    (vreset! done? true)
                                    (= cs "slatexseparateincludes")
                                    (when @latex?
                                      (vreset! slatex-separate-includes? true))
                                    (= cs "slatexdisable")
                                    (disable-slatex-temply in)
                                    (= cs "begin")
                                    (let [cs (read-grouped-latexexp in)]
                                      (cond (member cs @display-triggerers)
                                            (trigger-scheme2tex :envdisplay in cs)
                                            (member cs @box-triggerers)
                                            (trigger-scheme2tex :envbox in cs)
                                            (member cs @region-triggerers)
                                            (trigger-region :envregion in cs)))
                                    (member cs @intext-triggerers)
                                    (trigger-scheme2tex :intext in false)
                                    (member cs @resultintext-triggerers)
                                    (trigger-scheme2tex :resultintext in false)
                                    (member cs @display-triggerers)
                                    (trigger-scheme2tex :plaindisplay in cs)
                                    (member cs @box-triggerers)
                                    (trigger-scheme2tex :plainbox in cs)
                                    (member cs @region-triggerers)
                                    (trigger-region :plainregion in cs)
                                    (member cs @input-triggerers)
                                    (process-scheme-file (read-filename in))
                                    (= cs "input")
                                    (let [g1 @slatex-in-protected-region?]
                                      (vreset! slatex-in-protected-region? false)
                                      (let [temp (process-tex-file (read-filename in))]
                                        (vreset! slatex-in-protected-region? g1)
                                        temp))
                                    (= cs "include")
                                    (when @latex?
                                      (let [f (full-texfile-name (read-filename in))]
                                        (when (and f
                                                   (or (= @include-onlys :all)
                                                       (member f @include-onlys)))
                                          (let [g2 @slatex-in-protected-region?
                                                g3 @subjobname
                                                g4 @primary-aux-file-count]
                                            (vreset! slatex-in-protected-region? false)
                                            (vreset! subjobname @subjobname)
                                            (vreset! primary-aux-file-count @primary-aux-file-count)
                                            (let [temp (do (when @slatex-separate-includes?
                                                             (vreset! subjobname (basename f ".tex"))
                                                             (vreset! primary-aux-file-count -1))
                                                           (process-tex-file f))]
                                              (vreset! slatex-in-protected-region? g2)
                                              (vreset! subjobname g3)
                                              (vreset! primary-aux-file-count g4)
                                              temp)))))
                                    (= cs "includeonly")
                                    (when @latex? (process-include-only in))
                                    (= cs "documentstyle")
                                    (when @latex? (process-documentstyle in))
                                    (= cs "schemecasesensitive")
                                    (process-case-info in)
                                    (= cs "defschemetoken")
                                    (process-slatex-alias in adjoin-string :intext)
                                    (= cs "undefschemetoken")
                                    (process-slatex-alias in remove-string! :intext)
                                    (= cs "defschemeresulttoken")
                                    (process-slatex-alias in adjoin-string :resultintext)
                                    (= cs "undefschemeresulttoken")
                                    (process-slatex-alias in remove-string! :resultintext)
                                    (= cs "defschemedisplaytoken")
                                    (process-slatex-alias in adjoin-string :display)
                                    (= cs "undefschemedisplaytoken")
                                    (process-slatex-alias in remove-string! :display)
                                    (= cs "defschemeboxtoken")
                                    (process-slatex-alias in adjoin-string :box)
                                    (= cs "undefschemeboxtoken")
                                    (process-slatex-alias in remove-string! :box)
                                    (= cs "defschemeinputtoken")
                                    (process-slatex-alias in adjoin-string :input)
                                    (= cs "undefschemeinputtoken")
                                    (process-slatex-alias in remove-string! :input)
                                    (= cs "defschemeregiontoken")
                                    (process-slatex-alias in adjoin-string :region)
                                    (= cs "undefschemeregiontoken")
                                    (process-slatex-alias in remove-string! :region)
                                    (= cs "defschememathescape")
                                    (process-slatex-alias in adjoin-char :mathescape)
                                    (= cs "undefschememathescape")
                                    (process-slatex-alias in remove-char! :mathescape)
                                    (= cs "setkeyword")
                                    (add-to-slatex-db in :keyword)
                                    (= cs "setconstant")
                                    (add-to-slatex-db in :constant)
                                    (= cs "setvariable")
                                    (add-to-slatex-db in :variable)
                                    (= cs "setspecialsymbol")
                                    (add-to-slatex-db in :setspecialsymbol)
                                    (= cs "unsetspecialsymbol")
                                    (add-to-slatex-db in :unsetspecialsymbol)))))
                    (recur)))))))))
  (when debug?
    (display "end ") (display raw-filename) (newline*)))

(defn process-scheme-file [raw-filename]
  (let [filename (full-scmfile-name raw-filename)]
    (if (not filename)
      (do (display "process-scheme-file: ")
          (display raw-filename)
          (display " doesn't exist")
          (newline*))
      (let [aux-tex (new-aux-file ".tex")]
        (force-output)
        (when (slatex-file-exists? aux-tex) (slatex-delete-file aux-tex))
        (call-with-input-file filename
          (fn [in]
            (call-with-output-file aux-tex
              (fn [out]
                (let [g5 @intext?
                      g6 @code-env-spec]
                  (vreset! intext? false)
                  (vreset! code-env-spec "ZZZZschemedisplay")
                  (let [temp (scheme2tex in out)]
                    (vreset! intext? g5)
                    (vreset! code-env-spec g6)
                    temp))))))
        (when @slatex-in-protected-region?
          (vreset! protected-files (cons aux-tex @protected-files)))
        (process-tex-file filename)))))

(def ^:private intext-typs (list :intext :resultintext))
(def ^:private env-typs (list :envdisplay :envbox))
(def ^:private plain-typs (list :plaindisplay :plainbox))
(def ^:private display-typs (list :envdisplay :plaindisplay))
(def ^:private box-typs (list :envbox :plainbox))
(def ^:private displayish-typs (list :envdisplay :plaindisplay :envbox :plainbox))

(defn trigger-scheme2tex [typ in env]
  (let [aux (new-aux-file)
        aux-scm (str aux ".scm")
        aux-tex (str aux ".tex")]
    (when (slatex-file-exists? aux-scm) (slatex-delete-file aux-scm))
    (when (slatex-file-exists? aux-tex) (slatex-delete-file aux-tex))
    (force-output)
    (call-with-output-file aux-scm
      (fn [out]
        (cond (memq typ intext-typs) (dump-intext in out)
              (memq typ env-typs) (dump-display in out (str "\\end{" env "}"))
              (memq typ plain-typs) (dump-display in out (str "\\end" env))
              :else (error* 'slatex.trigger-scheme2tex 1))))
    (call-with-input-file aux-scm
      (fn [in]
        (call-with-output-file aux-tex
          (fn [out]
            (let [g7 @intext? g8 @code-env-spec]
              (vreset! intext? (memq typ intext-typs))
              (vreset! code-env-spec
                       (cond (= typ :intext) "ZZZZschemecodeintext"
                             (= typ :resultintext) "ZZZZschemeresultintext"
                             (memq typ display-typs) "ZZZZschemedisplay"
                             (memq typ box-typs) "ZZZZschemebox"
                             :else (error* 'slatex.trigger-scheme2tex 2)))
              (let [temp (scheme2tex in out)]
                (vreset! intext? g7)
                (vreset! code-env-spec g8)
                temp))))))
    (when @slatex-in-protected-region?
      (vreset! protected-files (cons aux-tex @protected-files)))
    (when (memq typ displayish-typs)
      (process-tex-file aux-tex))
    (slatex-delete-file aux-scm)))

(defn trigger-region [typ in env]
  (let [aux-tex (new-primary-aux-file ".tex")
        aux2-tex (new-secondary-aux-file ".tex")]
    (when (slatex-file-exists? aux2-tex) (slatex-delete-file aux2-tex))
    (when (slatex-file-exists? aux-tex) (slatex-delete-file aux-tex))
    (force-output)
    (let [g9 @slatex-in-protected-region?
          g10 @protected-files]
      (vreset! slatex-in-protected-region? true)
      (vreset! protected-files nil)
      (let [temp (do (call-with-output-file aux2-tex
                       (fn [out]
                         (cond (= typ :envregion)
                               (dump-display in out (str "\\end{" env "}"))
                               (= typ :plainregion)
                               (dump-display in out (str "\\end" env))
                               :else
                               (error* 'slatex.trigger-region 1))))
                     (process-tex-file aux2-tex)
                     (vreset! protected-files (reverse! @protected-files))
                     (call-with-input-file aux2-tex
                       (fn [in]
                         (call-with-output-file aux-tex
                           (fn [out]
                             (inline-protected-files in out)))))
                     (slatex-delete-file aux2-tex))]
        (vreset! slatex-in-protected-region? g9)
        (vreset! protected-files g10)
        temp))))

(defn inline-protected-files [in out]
  (let [done? (volatile! false)]
    (loop []
      (if @done?
        :exit-loop
        (do (let [c (read-char in)]
              (cond (eof-object? c) (do (display "{}" out) (vreset! done? true))
                    (= c \%) (eat-till-newline in)
                    (= c \\)
                    (let [cs (read-ctrl-seq in)]
                      (cond (= cs "begin")
                            (let [cs (read-grouped-latexexp in)]
                              (cond (member cs @display-triggerers)
                                    (inline-protected :envdisplay in out cs)
                                    (member cs @box-triggerers)
                                    (inline-protected :envbox in out cs)
                                    (member cs @region-triggerers)
                                    (inline-protected :envregion in out cs)
                                    :else
                                    (do (display "\\begin{" out)
                                        (display cs out)
                                        (display "}" out))))
                            (member cs @intext-triggerers)
                            (inline-protected :intext in out false)
                            (member cs @resultintext-triggerers)
                            (inline-protected :resultintext in out false)
                            (member cs @display-triggerers)
                            (inline-protected :plaindisplay in out cs)
                            (member cs @box-triggerers)
                            (inline-protected :plainbox in out cs)
                            (member cs @region-triggerers)
                            (inline-protected :plainregion in out cs)
                            (member cs @input-triggerers)
                            (inline-protected :input in out cs)
                            :else (do (display "\\" out) (display cs out))))
                    :else (display c out)))
            (recur))))))

(defn inline-protected [typ in out env]
  (cond (= typ :envregion)
        (do (display "\\begin{" out)
            (display env out)
            (display "}" out)
            (dump-display in out (str "\\end{" env "}"))
            (display "\\end{" out)
            (display env out)
            (display "}" out))
        (= typ :plainregion)
        (do (display "\\" out)
            (display env out)
            (dump-display in out (str "\\end" env))
            (display "\\end" out)
            (display env out))
        :else
        (let [f (car @protected-files)]
          (vreset! protected-files (cdr @protected-files))
          (call-with-input-file f
            (fn [in] (inline-protected-files in out)))
          (slatex-delete-file f)
          (cond (memq typ intext-typs) (dump-intext in false)
                (memq typ env-typs) (dump-display in false (str "\\end{" env "}"))
                (memq typ plain-typs) (dump-display in false (str "\\end" env))
                (= typ :input) (read-filename in)
                :else (error* 'slatex.inline-protected 1)))))

(defn -main [& _]
  (let [count (read-input) input1 (read-input) input2 (read-input) output (read-input)
        s3 (str count) s2 input2 s1 input1
        name "slatex"]
    (run-r7rs-benchmark (str name ":" s3) count
                        (fn [] (process-main-tex-file (hide count input1)))
                        (fn [result] true))))
