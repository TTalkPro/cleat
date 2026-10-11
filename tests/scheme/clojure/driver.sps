;;; cleat-052：编译驱动 (clojure driver)。顶层 do 拆分（后面的子形式看得到前面的 in-ns / def / 宏）、Clojure 的 Compiler.eval 先 macroexpand、
;;; 简单形式的解释与编译两条路径结果一致（逐个形式对照，关掉快速路径再求值一遍）、哪些形式走快速路径、load 的命名空间切换与恢复、
;;; 以宏 Var 对象开头的形式（展开与 macroexpand-1、私有宏的报错）、带位置的执行错误文本、钩子已登记。
;;; 宿主（进程级）的 REPL / 脚本 / require / compile 在 tests/Cleat.Host.Tests。
(import (rnrs) (cleat scheme) (cleat scheme clr)
        (clojure syntax-kinds) (clojure namespaces) (clojure syntax) (clojure driver))

(clr-static-call Cleat.Core.Runtime.PrimitiveRegistry RegisterAll)
(install-clojure-hooks!)

(define failures 0)
(define checks 0)
(define (fail! name expected got)
  (set! failures (+ failures 1))
  (display "FAIL: ") (display name) (newline)
  (display "  expected: ") (write expected) (newline)
  (display "  got:      ") (write got) (newline))
(define (clr-message e)
  (if (clr-is System.Exception e) (clr-call System.Exception get_Message e) e))
(define (run thunk)
  (with-clr-exception-handler
    (lambda (e) (list 'exception (clr-message e)))
    (lambda () (guard (e (#t (list 'condition (if (message-condition? e) (condition-message e) e)))) (thunk)))))
(define (check name thunk expected)
  (set! checks (+ checks 1))
  (let ((got (run thunk)))
    (unless (equal? got expected) (fail! name expected got))))
(define (string-contains? s sub)
  (let ((n (string-length s)) (k (string-length sub)))
    (let loop ((i 0)) (cond ((> (+ i k) n) #f) ((string=? (substring s i (+ i k)) sub) #t) (else (loop (+ i 1)))))))
(define (check-contains name thunk sub)
  (set! checks (+ checks 1))
  (let* ((got (run thunk))
         (text (if (and (pair? got) (pair? (cdr got)) (string? (cadr got))) (cadr got) got)))
    (unless (and (string? text) (string-contains? text sub)) (fail! name (string-append "... " sub " ...") got))))

(define (str x) (clr-static-call clojure.lang.RT (printString System.Object) x))
(define (read-all s)
  (let ((r ((clr-static-field-get Cleat.Scheme.Clojure.Driver MakeReader) (clr-new System.IO.StringReader s)))
        (eof (clr-new System.Object)))
    (let loop ((acc '()))
      (let ((x ((clr-static-field-get Cleat.Scheme.Clojure.Driver ReadForm) r eof)))
        (if (eq? x eof) (reverse acc) (loop (cons x acc)))))))
(define (ev s) (let loop ((fs (read-all s)) (last '())) (if (null? fs) last (loop (cdr fs) (clojure-eval-toplevel (car fs))))))
(define (load-string s) (clojure-load-reader (clr-new System.IO.StringReader s) "t.clj" "t.clj"))
(define-syntax t
  (syntax-rules () ((_ name src expected) (check name (lambda () (str (ev src))) expected))))

(define (simple-count) (clr-static-field-get Cleat.Scheme.Clojure.Driver SimpleCount))
(define (compiled-count) (clr-static-field-get Cleat.Scheme.Clojure.Driver CompiledCount))
(define (format-error thunk)
  (with-clr-exception-handler
    (lambda (e) (clr-static-call Cleat.Scheme.Clojure.Driver FormatError e))
    thunk))
(define (set-simple! on?) (clr-static-field-set! Cleat.Scheme.Clojure.Driver SimpleEvalEnabled on?))

;;; 一个形式走哪条路径：'simple / 'compiled
(define (path-of src)
  (let ((s0 (simple-count)) (c0 (compiled-count)))
    (ev src)
    (cond ((> (simple-count) s0) (if (> (compiled-count) c0) 'both 'simple))
          ((> (compiled-count) c0) 'compiled)
          (else 'none))))

(with-ns-binding (find-ns 'user)
 (lambda ()
  (in-ns! 'drv.test)
  (ev "(def list2 (fn* [a b] (cleat.kernel/list2 a b)))
       (def ^:dynamic *d* 10)
       (def v 7)")

  ;; ———————— 钩子 ————————
  (check "eval hook registered" (lambda () (not (null? (clr-static-prop-get Cleat.Core.Runtime.RuntimeHooks Eval)))) #t)
  (check "load-source hook registered" (lambda () (not (null? (clr-static-prop-get Cleat.Core.Runtime.RuntimeHooks LoadSource)))) #t)
  (check "compile hook registered" (lambda () (not (null? (clr-static-prop-get Cleat.Core.Runtime.RuntimeHooks Compile)))) #t)
  (check "macroexpand-1 hook registered" (lambda () (not (null? (clr-static-prop-get Cleat.Core.Runtime.RuntimeHooks Macroexpand1)))) #t)
  (check "report-error hook registered" (lambda () (not (null? (clr-static-prop-get Cleat.Core.Runtime.RuntimeHooks ReportError)))) #t)

  ;; ———————— 顶层 do 拆分 ————————
  (t "in-ns inside a top-level do affects the following subforms" "(do (in-ns 'drv.other) (def x 1)) (in-ns 'drv.test) drv.other/x" "1")
  (t "a macro defined earlier in the same do is used by a later subform"
     "(do (def m (fn* [&form &env] 5)) (. (var m) setMacro) (m))" "5")
  (t "nested do is split too" "(do (do (def n1 1)) (do (def n2 (cleat.kernel/add n1 1)) n2))" "2")
  (t "empty do is nil" "(do)" "nil")
  (t "a macro expanding to do is split (Compiler.eval macroexpands first)"
     "(def mdo (fn* [&form &env] (list2 (quote do) (list2 (quote def) (quote via-mdo))))) (. (var mdo) setMacro) (mdo) (. (var via-mdo) hasRoot)"
     "false")

  ;; ———————— 简单形式 / 编译 ————————
  (check "constant is simple" (lambda () (path-of "1")) 'simple)
  (check "def of a constant is simple" (lambda () (path-of "(def c1 [1 2])")) 'simple)
  (check "Var ref and direct call are simple" (lambda () (path-of "(cleat.kernel/add v 1)")) 'simple)
  (check "interop is simple" (lambda () (path-of "(. \"abc\" Length)")) 'simple)
  (check "let* and if are simple" (lambda () (path-of "(let* [a v] (if a [a a] nil))")) 'simple)
  (check "keyword invoke is simple" (lambda () (path-of "(:k {:k v})")) 'simple)
  (check "fn* is compiled" (lambda () (path-of "(fn* [a] a)")) 'compiled)
  (check "def of a fn is compiled" (lambda () (path-of "(def f1 (fn* [a] a))")) 'compiled)
  (check "loop* is compiled" (lambda () (path-of "(loop* [i 0] (if (cleat.kernel/lt i 3) (recur (cleat.kernel/inc i)) i))")) 'compiled)
  (check "try is compiled" (lambda () (path-of "(try 1 (catch Exception e 2))")) 'compiled)
  (check "case* is compiled" (lambda () (path-of "(let* [x v] (case* x 0 0 :d {7 [7 :seven]} :compact :int))")) 'compiled)

  ;; 两条路径的结果一致
  (for-each
    (lambda (src)
      (set-simple! #t)
      (let ((a (run (lambda () (str (ev src))))))
        (set-simple! #f)
        (let ((b (run (lambda () (str (ev src))))))
          (set-simple! #t)
          (set! checks (+ checks 1))
          (unless (equal? a b) (fail! (string-append "simple = compiled: " src) b a)))))
    '("1" "nil" "true" "false" "\"s\"" ":k" "'(a b)" "[v 1 {:a v} #{v}]" "(:k {:k 1} 2)" "(:missing {:k 1} 2)" "(:k nil)"
      "(cleat.kernel/add 1 2)" "(. (cleat.kernel/add 1 2) GetType)" "(. \"abc\" Length)" "(. (. \"abc\" Length) GetType)"
      "(. System.Math (Max 3 4))" "(new System.Text.StringBuilder \"x\")" "(. (new System.Text.StringBuilder \"x\") ToString)"
      "(let* [a 1 b (cleat.kernel/add a 1)] [a b])" "(if nil 1 2)" "(if false 1 2)" "(if 0 1 2)" "(if () 1 2)"
      "*d*" "(. (var *d*) isDynamic)" "(def d2 (let* [x 3] x))" "d2" "(:line (. (var d2) meta))" "(instance? String \"a\")"
      "(. clojure.lang.Numbers (divide 1 0))" "(list2 1 2)" "(v)" "(nil 1)" "String" "(var v)" "^{:m 1} [v]" "(. (quote ^{:m 1} [1]) meta)"))

  ;; ———————— load：文件里的 in-ns 对后续形式生效，加载结束后 *ns* 恢复 ————————
  (check "load switches namespace for the rest of the file"
    (lambda () (str (load-string "(in-ns 'drv.loaded) (def here clojure.core/*ns*) (def y 5) [here y]")))
    "[drv.loaded 5]")
  (check "*ns* restored after load" (lambda () (str (current-ns))) "drv.test")
  (t "definitions from the loaded file" "drv.loaded/y" "5")
  (check "load returns the last value" (lambda () (str (load-string "1 2 (cleat.kernel/add 1 2)"))) "3")
  (check "load of an empty source is nil" (lambda () (str (load-string ""))) "nil")

  ;; ———————— 以宏 Var 对象开头的形式（cleat-050 未做的部分）————————
  (ev "(def twice (fn* [&form &env x] (list2 (quote cleat.kernel/list1) x))) (. (var twice) setMacro)
       (def via-var (fn* [&form &env] (list2 (var twice) 5))) (. (var via-var) setMacro)")
  (t "(#'m …) built by a macro is macroexpanded" "(via-var)" "(5)")
  (check "macroexpand-1 of a Var-headed form"
    (lambda () (str (clojure-macroexpand-1 (car (list (ev "(list2 (var twice) 7)"))))))
    "(cleat.kernel/list1 7)")
  (ev "(in-ns 'drv.priv) (def pm (fn* [&form &env] 1)) (. (var pm) setMeta {:private true}) (. (var pm) setMacro) (in-ns 'drv.test)
       (def via-priv (fn* [&form &env] (list2 (var drv.priv/pm) 1))) (. (var via-priv) setMacro)")
  (check-contains "private macro Var from another namespace"
    (lambda () (ev "(via-priv)"))
    "Var: #'drv.priv/pm is not public")

  ;; ———————— 错误：执行错误带最内层形式的位置；语法错误是 ExpanderException ————————
  (check-contains "execution error names the type and location"
    (lambda () (format-error (lambda () (load-string "\n  (. clojure.lang.Numbers (divide 1 0))"))))
    "Execution error (ArithmeticException) at (REPL:2).")
  (check-contains "execution error message on the next line"
    (lambda () (format-error (lambda () (load-string "(. clojure.lang.Numbers (divide 1 0))"))))
    "Divide by zero")
  (check-contains "syntax error keeps the expander text"
    (lambda () (format-error (lambda () (ev "(nope 1)"))))
    "Syntax error compiling at (REPL:1:1).\nUnable to resolve symbol: nope in this context")
  (check-contains "exceptions are not wrapped (Clojure catch semantics)"
    (lambda () (with-clr-exception-handler (lambda (e) (clr-call System.Object ToString (clr-call System.Object GetType e)))
                 (lambda () (ev "(. clojure.lang.Numbers (divide 1 0))"))))
    "System.ArithmeticException")))

(display checks) (display " checks, ") (display failures) (display " failures") (newline)
(unless (= failures 0) (exit 1))
