;; r7rs 移植的可移植小工具（cleat-107）：JVM 与 CLR 写法不同的部分集中在这里，基准源码只用 clojure.core 与本命名空间。
(ns r7rs.util)

;; 浮点数学：JVM 是 Math/sqrt，CLR 是 Math/Sqrt
(defn sqrt ^double [^double x] #?(:clj (Math/sqrt x) :cljr (Math/Sqrt x)))
(defn sin ^double [^double x] #?(:clj (Math/sin x) :cljr (Math/Sin x)))
(defn cos ^double [^double x] #?(:clj (Math/cos x) :cljr (Math/Cos x)))
(defn atan ^double [^double x] #?(:clj (Math/atan x) :cljr (Math/Atan x)))
(defn atan2 ^double [^double y ^double x] #?(:clj (Math/atan2 y x) :cljr (Math/Atan2 y x)))
(defn exp ^double [^double x] #?(:clj (Math/exp x) :cljr (Math/Exp x)))
(defn log ^double [^double x] #?(:clj (Math/log x) :cljr (Math/Log x)))
(defn floor ^double [^double x] #?(:clj (Math/floor x) :cljr (Math/Floor x)))
(defn ceiling ^double [^double x] #?(:clj (Math/ceil x) :cljr (Math/Ceiling x)))
(defn fl-round
  "Scheme 的 round（就近取偶）作用于 double。"
  ^double [^double x]
  #?(:clj (Math/rint x) :cljr (Math/Round x)))
(def pi #?(:clj Math/PI :cljr Math/PI))

;; 字符与字符串
(defn char->integer [c] (long (int c)))
(defn integer->char [n] (char n))
(defn char-alphabetic? [c] #?(:clj (Character/isLetter (char c)) :cljr (Char/IsLetter (char c))))
(defn char-whitespace? [c] #?(:clj (Character/isWhitespace (char c)) :cljr (Char/IsWhiteSpace (char c))))
(defn char-upcase [c] #?(:clj (Character/toUpperCase (char c)) :cljr (Char/ToUpper (char c))))
(defn char-downcase [c] #?(:clj (Character/toLowerCase (char c)) :cljr (Char/ToLower (char c))))

(defn string-builder [] #?(:clj (StringBuilder.) :cljr (System.Text.StringBuilder.)))
(defn sb-append! [sb x] #?(:clj (.append ^StringBuilder sb (str x)) :cljr (.Append ^System.Text.StringBuilder sb (str x))) sb)
(defn sb-append-char! [sb c] #?(:clj (.append ^StringBuilder sb (char c)) :cljr (.Append ^System.Text.StringBuilder sb (char c))) sb)
(defn sb-length [sb] #?(:clj (.length ^StringBuilder sb) :cljr (.Length ^System.Text.StringBuilder sb)))

;; 文件（基准的工作目录是 r7rs-benchmarks 仓库，路径相对于它）
(defn file-reader
  "按字符读文件的读取器：返回 [read-char peek-char close]，read-char / peek-char 在文件末尾返回 nil。"
  [path]
  #?(:clj (let [r (java.io.PushbackReader. (java.io.BufferedReader. (java.io.FileReader. (str path))))]
            [(fn [] (let [c (.read r)] (when-not (neg? c) (char c))))
             (fn [] (let [c (.read r)] (if (neg? c) nil (do (.unread r c) (char c)))))
             (fn [] (.close r))])
     :cljr (let [r (System.IO.StreamReader. (str path))]
             [(fn [] (let [c (.Read r)] (when-not (neg? c) (char c))))
              (fn [] (let [c (.Peek r)] (when-not (neg? c) (char c))))
              (fn [] (.Close r))])))

(defn file-lines
  "文件的全部行（向量）。"
  [path]
  (with-open [r #?(:clj (java.io.BufferedReader. (java.io.FileReader. (str path))) :cljr (System.IO.StreamReader. (str path)))]
    (vec (line-seq r))))

(defn file-forms
  "按 Clojure 读取器读出文件里的全部数据（Scheme 源码里 Clojure 读得懂的部分）。"
  [path]
  (let [rdr #?(:clj (java.io.PushbackReader. (java.io.FileReader. (str path)))
               :cljr (clojure.lang.PushbackTextReader. (System.IO.StreamReader. (str path))))]
    (loop [acc []]
      (let [x (read {:eof ::eof} rdr)]
        (if (= x ::eof) acc (recur (conj acc x)))))))

;; 错误
(defn fatal [& xs] (throw (ex-info (apply str xs) {})))

;; 逃逸续延（原版只把 call/cc 用作逃逸时）：(call-with-escape (fn [k] … (k v) …))，k 只能在动态范围内调用。
;; 抛的是预先分配的同一个异常（JVM 构造异常时记录调用栈，每次新建会让 ctak / fibc 只测到这个开销）；值与"谁抛的"记在各次调用自己的盒子里，嵌套时外层据此区分。
(def ^:private escape-ex (ex-info "escape" {}))

(defn call-with-escape [f]
  (let [box (object-array 2)]
    (try
      (f (fn [v] (aset box 0 v) (aset box 1 box) (throw escape-ex)))
      (catch clojure.lang.ExceptionInfo e
        (if (and (identical? e escape-ex) (identical? (aget box 1) box))
          (aget box 0)
          (throw e))))))

;; 可变序对（原版用 set-car! / set-cdr! 的程序）：mcons / mcar / mcdr / set-mcar! / set-mcdr!，空表用 nil
(definterface IMPair
  (getCar [])
  (getCdr [])
  (setCar [v])
  (setCdr [v]))

(deftype MPair [^:unsynchronized-mutable a ^:unsynchronized-mutable d]
  IMPair
  (getCar [_] a)
  (getCdr [_] d)
  (setCar [_ v] (set! a v) v)
  (setCdr [_ v] (set! d v) v))

(defn mcons [a d] (MPair. a d))
(defn mpair? [x] (instance? MPair x))
(defn mcar [p] (.getCar ^r7rs.util.IMPair p))
(defn mcdr [p] (.getCdr ^r7rs.util.IMPair p))
(defn set-mcar! [p v] (.setCar ^r7rs.util.IMPair p v))
(defn set-mcdr! [p v] (.setCdr ^r7rs.util.IMPair p v))

;; 可变向量（原版的 make-vector / vector-set!）：对象数组
(defn make-vector
  ([n] (object-array n))
  ([n fill] (let [a (object-array n)] (dotimes [i n] (aset a i fill)) a)))
