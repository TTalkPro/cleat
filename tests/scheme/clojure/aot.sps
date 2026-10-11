;;; cleat-051：Clojure 代码保存为 dll（不只是进程内求值）。一个小命名空间（def、^:dynamic、fn（闭包、多 arity、变长、递归）、
;;; deftype*（可变字段）、reify*、try / catch、各种常量）经 (clojure compile) 编成持久化程序集；ILVerify（装了的话）校验；
;;; 然后在**新的进程**里（stage0 + 同一个 Scheme 镜像）只加载这个 dll——命名空间与 Var 由常量表的 (var ns name) 在加载时
;;; intern，Initialize 执行全部顶层形式——再从 Clojure 代码调用其中的函数与类型，比较结果。
(import (rnrs) (rnrs eval) (cleat scheme) (cleat scheme clr) (cleat scheme compiler)
        (clojure syntax-kinds) (clojure namespaces) (clojure syntax) (clojure compile))

(clr-static-call clojure.lang.RT Init)

(define (read-all s)
  (let ((r (clr-new clojure.lang.LineNumberingTextReader (clr-new System.IO.StringReader s)))
        (eof (clr-new System.Object)))
    (let loop ((acc '()))
      (let ((x (clr-static-call clojure.lang.LispReader
                 (read clojure.lang.PushbackTextReader System.Boolean System.Object System.Boolean System.Object)
                 r #f eof #f (clj-map))))
        (if (eq? x eof) (reverse acc) (loop (cons x acc)))))))

(define failures 0)
(define (fail! . xs) (set! failures (+ failures 1)) (display "FAIL: ") (for-each display xs) (newline))

;;; —— 目录、程序集名（每次运行唯一：同名程序集不能在一个进程里加载两次，dll 也可能被上次的进程占用）——
(define out-dir
  (let ((d (clr-static-call System.IO.Path Combine (clr-static-call System.IO.Path GetTempPath)
             (string-append "cleat-aot-" (number->string (clr-prop-get System.Diagnostics.Process Id (clr-static-call System.Diagnostics.Process GetCurrentProcess)))))))
    (clr-static-call System.IO.Directory CreateDirectory d)
    d))
(define asm-name "cleat.aot.sample")
(define dll (clr-static-call System.IO.Path Combine out-dir (string-append asm-name ".dll")))
(when (file-exists? dll) (delete-file dll))

;;; —— 编译（在 cleat.aot.sample 命名空间里；编译时也执行了一遍，之后删掉命名空间，证明子进程不依赖本进程）——
(define sample "
(def answer 42)
(def ^:dynamic *level* 1)
(def add (fn* add [a b] (. clojure.lang.Numbers (add a b))))
(def fact (fn* fact [n] (if (. clojure.lang.Numbers (lt n 2)) 1 (. clojure.lang.Numbers (multiply n (fact (. clojure.lang.Numbers (dec n))))))))
(def multi (fn* ([] :none) ([a] [:one a]) ([a & r] [:many a r])))
(def adder (fn* [n] (fn* [x] (add x n))))
(def consts (fn* [] [:a :ns/b 'c 'd/e \"s\" 1 2.5 #{1} {:x [1 2] :y '(3)} nil true false \\z 10000000000 1/2 3N 4.5M #\"r+\" (quote ^:m [5])]))
(def string-type (fn* [] String))
(def count-to (fn* [n] (loop* [i 0] (if (. clojure.lang.Numbers (lt i n)) (recur (. clojure.lang.Numbers (inc i))) i))))
(deftype* cleat.aot.sample/Point cleat.aot.sample.Point [x ^:unsynchronized-mutable y] :implements [clojure.lang.IDeref clojure.lang.Counted]
  (deref [this] (set! y (. clojure.lang.Numbers (inc y))) [x y])
  (count [_] 2))
(def mk (fn* [a b] (new cleat.aot.sample.Point a b)))
(def boxed (fn* [v] (let* [k (:k {:k v})] (reify* [clojure.lang.IDeref] (deref [_] [k v])))))
(def safe-div (fn* [a b] (try (. clojure.lang.Numbers (divide a b)) (catch ArithmeticException e :div0) (catch Exception e :other) (finally nil))))
(def level (fn* [] *level*))
")

(with-ns-binding (find-ns 'user)
 (lambda ()
  (in-ns! 'cleat.aot.sample)
  (clojure-compile-forms (read-all sample) dll)
  (in-ns! 'user)))
(clr-static-call clojure.lang.Namespace remove (clj-symbol "cleat.aot.sample"))
(unless (file-exists? dll) (fail! "no dll written"))
(display "saved ") (display dll) (newline)

;;; —— ILVerify（dotnet tool install -g dotnet-ilverify；没有装时跳过，与 BootstrapTests 相同）——
(define (assembly-dir-of type-name)
  (clr-static-call System.IO.Path GetDirectoryName
    (clr-prop-get System.Reflection.Assembly Location (clr-prop-get System.Type Assembly (clr-static-call System.Type GetType type-name)))))
(define (quote-arg a) (string-append "\"" a "\""))
(define (run-process file args)
  (let ((psi (clr-new System.Diagnostics.ProcessStartInfo file
               (fold-left (lambda (acc a) (if (string=? acc "") (quote-arg a) (string-append acc " " (quote-arg a)))) "" args))))
    (clr-prop-set! System.Diagnostics.ProcessStartInfo UseShellExecute psi #f)
    (clr-prop-set! System.Diagnostics.ProcessStartInfo RedirectStandardOutput psi #t)
    (let* ((p (clr-static-call System.Diagnostics.Process Start psi))
           (out (clr-call System.IO.StreamReader ReadToEnd (clr-prop-get System.Diagnostics.Process StandardOutput p))))
      (clr-call System.Diagnostics.Process WaitForExit p)
      (cons (clr-prop-get System.Diagnostics.Process ExitCode p) out))))
(define cmd-args (vector->list (clr-static-call System.Environment GetCommandLineArgs)))
(define boot-image (let ((m (member "--boot" cmd-args))) (and m (pair? (cdr m)) (clr-static-call System.IO.Path GetFullPath (cadr m)))))

(define (env name) (let ((v (clr-static-call System.Environment GetEnvironmentVariable name))) (and (string? v) v)))
(let ((tool (or (env "ILVERIFY")
                (let ((home (or (env "HOME") (env "USERPROFILE"))))
                  (and home (clr-static-call System.IO.Path Combine home ".dotnet" "tools" "ilverify"))))))
  (if (not (and tool (file-exists? tool)))
      (begin (display "ilverify not installed: skipped") (newline))
      (let ((r (run-process tool
                 (list dll
                       "-r" (string-append (assembly-dir-of "System.Object") "/*.dll")
                       "-r" (clr-prop-get System.Reflection.Assembly Location (clr-prop-get System.Type Assembly (clr-static-call System.Type GetType "clojure.lang.RT, Cleat.Core")))
                       "-r" (clr-prop-get System.Reflection.Assembly Location (clr-prop-get System.Type Assembly (clr-static-call System.Type GetType "Cleat.Scheme.Procedure, Cleat.Scheme")))
                       "-r" boot-image))))
        (display (cdr r))
        (unless (= (car r) 0) (fail! "ILVerify failed")))))

;;; —— 新进程：只加载 dll，调用 ——
(define loader (clr-static-call System.IO.Path Combine out-dir "load.sps"))
(when (file-exists? loader) (delete-file loader))
(call-with-output-file loader
  (lambda (p)
    (write '(import (rnrs) (cleat scheme) (cleat scheme clr) (clojure syntax-kinds) (clojure namespaces) (clojure syntax)) p)
    (write
      `(begin
         (clr-static-call clojure.lang.RT Init)
         (define (read1 s)
           (clr-static-call clojure.lang.LispReader (read clojure.lang.PushbackTextReader System.Boolean System.Object System.Boolean System.Object)
             (clr-new clojure.lang.LineNumberingTextReader (clr-new System.IO.StringReader s)) #f '() #f (clj-map)))
         (define (str x) (clr-static-call clojure.lang.RT (printString System.Object) x))
         (define failures 0)
         (define (t src expected)
           (let ((got (with-clr-exception-handler (lambda (e) (string-append "EXC " (clr-call System.Exception get_Message e)))
                        (lambda () (str (clojure-eval (read1 src)))))))
             (unless (equal? got expected)
               (set! failures (+ failures 1))
               (display "FAIL in child: ") (display src) (display " => ") (display got) (display " (expected ") (display expected) (display ")") (newline))))
         (unless (find-ns 'cleat.aot.sample) (display "namespace absent before loading: ok") (newline))
         (clr-static-call Cleat.Scheme.ModuleLoader (LoadModule String Boolean) ,dll #f)
         (with-ns-binding (find-ns 'user)
           (lambda ()
             (in-ns! 'user)
             (t "cleat.aot.sample/answer" "42")
             (t "(cleat.aot.sample/add 1 2)" "3")
             (t "(cleat.aot.sample/fact 20)" "2432902008176640000")
             (t "[(cleat.aot.sample/multi) (cleat.aot.sample/multi 1) (cleat.aot.sample/multi 1 2 3)]" "[:none [:one 1] [:many 1 (2 3)]]")
             (t "((cleat.aot.sample/adder 10) 5)" "15")
             (t "(cleat.aot.sample/consts)" "[:a :ns/b c d/e \"s\" 1 2.5 #{1} {:x [1 2], :y (3)} nil true false \\z 10000000000 1/2 3N 4.5M #\"r+\" [5]]")
             (t "(. (cleat.aot.sample/string-type) FullName)" "\"System.String\"")
             (t "(. (cleat.aot.sample/consts) (nth 5))" "1")
             (t "(. (. (. (cleat.aot.sample/consts) (nth 5)) GetType) FullName)" "\"System.Int64\"")
             (t "(. clojure.lang.RT (meta (. (cleat.aot.sample/consts) (nth 18))))" "{:m true}")
             (t "(cleat.aot.sample/count-to 1000000)" "1000000")
             (t "(let* [p (cleat.aot.sample/mk 1 2)] [(. p deref) (. p deref) (. clojure.lang.RT (count p))])" "[[1 3] [1 4] 2]")
             (t "(. (. (. (cleat.aot.sample/mk 1 2) GetType) Assembly) FullName)" ,(string-append "\"" asm-name ", Version=0.0.0.0, Culture=neutral, PublicKeyToken=null\""))
             (t "(. cleat.aot.sample.Point getBasis)" "[x y]")
             (t "(. (cleat.aot.sample/boxed 7) deref)" "[7 7]")
             (t "[(cleat.aot.sample/safe-div 6 3) (cleat.aot.sample/safe-div 1 0)]" "[2 :div0]")
             (t "(cleat.aot.sample/level)" "1")
             (t "(. (var cleat.aot.sample/*level*) isDynamic)" "true")
             (t "(. (. (var cleat.aot.sample/add) meta) (valAt :line))" "4")))
         (display "child: ") (display failures) (display " failures") (newline)
         (exit (if (= failures 0) 0 1)))
      p)))

(let ((r (run-process (clr-static-prop-get System.Environment ProcessPath)
           (list (clr-prop-get System.Reflection.Assembly Location (clr-static-call System.Reflection.Assembly GetEntryAssembly))
                 "-I" "lib" "-I" "nanopass" "--boot" boot-image loader))))
  (display (cdr r))
  (unless (= (car r) 0) (fail! "child process failed (exit " (car r) ")")))

(with-clr-exception-handler (lambda (e) #f) (lambda () (clr-static-call System.IO.Directory Delete out-dir #t)))
(display failures) (display " failures") (newline)
(unless (= failures 0) (exit 1))
