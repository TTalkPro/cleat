;;; cleat-049：Clojure 的命名空间模型（designs/14 §6 / §8 的通过判据）在镜像上的端到端测试。
;;; Clojure 源码由 C# 的 LispReader 读入（带 :line / :column），经 (clojure namespaces) 转换、在 *ns* 的查找环境里
;;; 展开 → 编译 → 运行。clojure.core 未加载，所以 ns / require 由下面的 ns-form! 直接调命名空间原语完成；
;;; 特殊形式（def、let*、set! …）来自 cleat-050 的 (clojure syntax)，入口 clojure-eval 等也在那里。
(import (rnrs) (rnrs eval) (cleat scheme) (cleat scheme clr) (cleat scheme compiler) (cleat scheme syntax-ext)
        (clojure syntax-kinds) (clojure namespaces) (clojure syntax))

(clr-static-call clojure.lang.RT Init)

(define failures 0)
(define checks 0)
(define (fail! name expected got)
  (set! failures (+ failures 1))
  (display "FAIL: ") (display name) (newline)
  (display "  expected: ") (write expected) (newline)
  (display "  got:      ") (write got) (newline))
(define (clr-message e)
  (if (clr-is System.Exception e) (clr-call System.Exception get_Message e) e))
(define (check name thunk expected)
  (set! checks (+ checks 1))
  (let ((got (with-clr-exception-handler
               (lambda (e) (list 'exception (clr-message e)))
               (lambda ()
                 (guard (e ((clr-condition? e) (list 'exception (clr-message (clr-condition-exception e)))) (#t (list 'condition (if (message-condition? e) (condition-message e) e))))
                   (thunk))))))
    (unless (equal? got expected) (fail! name expected got))))
(define-syntax test
  (syntax-rules ()
    ((_ name expr expected) (check name (lambda () expr) expected))))

;;; —— 工具 ——
(define (read-all s)
  (let ((r (clr-new clojure.lang.LineNumberingTextReader (clr-new System.IO.StringReader s)))
        (eof (clr-new System.Object)))
    (let loop ((acc '()))
      (let ((x (clr-static-call clojure.lang.LispReader
                 (read clojure.lang.PushbackTextReader System.Boolean System.Object System.Boolean System.Object)
                 r #f eof #f (clj-map))))
        (if (eq? x eof) (reverse acc) (loop (cons x acc)))))))
(define (read1 s) (car (read-all s)))
(define (str x) (clr-static-call clojure.lang.RT (printString System.Object) x))
(define (vstr v) (clr-call System.Object ToString v))
(define (clj= a b) (clr-static-call clojure.lang.Util (equiv System.Object System.Object) a b))
(define (sym s) (clj-symbol s))
(define (kw s) (clj-keyword s))
(define (first* x) (clr-static-call clojure.lang.RT (first System.Object) x))
(define (meta x) (clr-call clojure.lang.IMeta meta x))
(define (deref v) (clr-call clojure.lang.IDeref deref v))
(define (ns-mapping ns s) (clr-call clojure.lang.Namespace GetMapping (find-ns ns) (sym s)))

;;; 最小的 ns：(ns name (:require [lib :as alias :refer [x …]] …))，直接调 in-ns! / alias! / refer!
(define (ns-form! form)
  (let ((parts (seq->list form)))
    (in-ns! (cadr parts))
    (for-each
      (lambda (clause)
        (let ((c (seq->list clause)))
          (when (clj= (car c) (kw "require"))
            (for-each
              (lambda (spec)
                (let ((s (seq->list spec)))
                  (unless (find-ns (car s)) (error 'ns-form! "no such namespace" (str (car s))))
                  (let loop ((opts (cdr s)))
                    (unless (null? opts)
                      (cond
                        ((clj= (car opts) (kw "as")) (alias! (cadr opts) (car s)))
                        ((clj= (car opts) (kw "refer")) (apply refer! (car s) (seq->list (cadr opts)))))
                      (loop (cddr opts))))))
              (cdr c)))))
      (cddr parts))))

;;; 加载器：逐个顶层形式，ns 形式走 ns-form!，其余 clojure-eval；返回最后一个值
(define (load-string s)
  (let loop ((forms (read-all s)) (last #f))
    (if (null? forms)
        last
        (let ((f (car forms)))
          (loop (cdr forms)
                (if (and (clr-is clojure.lang.ISeq f) (clj= (first* f) (sym "ns")))
                    (begin (ns-form! f) #f)
                    (clojure-eval f)))))))

;;; 特殊形式来自 (clojure syntax)（cleat-050）；另登记一个测试用的转换器式特殊形式
(define env (environment '(rnrs) '(cleat scheme) '(cleat scheme clr) '(clojure syntax-kinds)))
(register-clj-special! 'dummy-special (eval '(lambda (stx) #''expanded-dummy) env))

(with-ns-binding (find-ns 'user)
  (lambda ()
    ;; —— 通过判据：(ns a) (def x 1) (ns b (:require [a :as q])) q/x ——
    (test "gate: q/x through the alias is 1"
      (load-string "(ns a) (def x 1) (ns b (:require [a :as q])) q/x")
      1)
    (test "*ns* is b after the ns form"
      (ns-name* (current-ns))
      "b")
    (test "def returns the Var"
      (begin (in-ns! 'a) (let ((v (load-string "(def x 1)"))) (in-ns! 'b) (vstr v)))
      "#'a/x")

    ;; —— 重定义：先展开（编译）的引用看到新值；标签不变；resolve 与展开器一致 ——
    (let* ((ref (compile-core (clojure-expand (read1 "q/x"))))
           (label-before (symbol-label 'q/x))
           (v (clj-resolve (sym "q/x"))))
      (test "compiled reference before redefinition" (ref) 1)
      (in-ns! 'a)
      (load-string "(def x 2)")
      (test "same Var after (def x 2) in a" (eq? (clj-resolve (sym "x")) v) #t)
      (in-ns! 'b)
      (test "previously compiled reference sees 2" (ref) 2)
      (test "label is stable across redefinition" (eq? (symbol-label 'q/x) label-before) #t)
      (test "label is the Var's label" (eq? label-before (var-label v)) #t)
      (test "resolve agrees: (deref (resolve 'q/x))" (deref (clj-resolve (sym "q/x"))) 2)
      (test "ns-resolve with another ns" (eq? (clj-ns-resolve (find-ns 'a) (sym "x")) v) #t)
      (test "re-evaluated reference" (load-string "q/x") 2))

    ;; —— 词法遮蔽 ——
    (load-string "(def y 10)")
    (test "Var y in b" (load-string "y") 10)
    (test "let* shadows the Var y" (load-string "(let* [y 7] y)") 7)
    (test "let* binding x does not touch q/x" (load-string "(let* [x 5] q/x)") 2)
    (test "Var y visible again after let*" (load-string "(let* [z 1] y)") 10)
    (test "nested let* shadows outer" (load-string "(let* [y 1] (let* [y 2] y))") 2)

    ;; —— 私有 Var ——
    (in-ns! 'a)
    (load-string "(def ^:private p 3)")
    (test "private Var visible in its own ns" (load-string "p") 3)
    (in-ns! 'b)
    (test "private Var rejected through the alias"
      (load-string "q/p")
      '(exception "Syntax error (NO_SOURCE_PATH:0:0) var: q/p is not public"))
    (test "private Var rejected with the qualified name"
      (load-string "\n  (a/p)")
      '(exception "Syntax error (NO_SOURCE_PATH:2:3) var: a/p is not public"))
    (test "resolve of a private Var throws like Clojure"
      (clj-resolve (sym "q/p"))
      '(exception "var: q/p is not public"))
    (test "refer of a private Var is rejected"
      (refer! 'a 'p)
      '(exception "p is not public"))

    ;; —— refer 一个 Var ——
    (in-ns! 'c)
    (refer! 'a 'x)
    (test "referred x in c" (load-string "x") 2)
    (test "referred mapping is a/x's Var in ns-map"
      (eq? (ns-mapping 'c "x") (clj-resolve (sym "a/x")))
      #t)
    (test "referred symbol has the Var's label"
      (eq? (symbol-label 'x) (var-label (clj-resolve (sym "a/x"))))
      #t)
    (test "y was not referred into c"
      (load-string "y")
      '(exception "Syntax error (NO_SOURCE_PATH:0:0) Unable to resolve symbol: y in this context"))
    (test "def in c replaces the referred x with c/x"
      (begin (load-string "(def x 30)") (list (load-string "x") (vstr (clj-resolve (sym "x"))) (load-string "a/x")))
      '(30 "#'c/x" 2))

    ;; —— in-ns 切换与别名的作用域 ——
    (in-ns! 'a)
    (test "after in-ns a, x is a/x" (load-string "x") 2)
    (test "alias q belongs to b only"
      (load-string "q/x")
      '(exception "Syntax error (NO_SOURCE_PATH:0:0) No such namespace: q"))
    (in-ns! 'b)

    ;; —— import ——
    (import! "System.Text.StringBuilder")
    (test "imported type resolves to the Type"
      (clr-call System.Type get_FullName (load-string "StringBuilder"))
      "System.Text.StringBuilder")
    (test "fully qualified type without import"
      (clr-call System.Type get_FullName (load-string "System.Text.StringBuilder"))
      "System.Text.StringBuilder")
    (test "in-ns and ns always resolve (RT.InNSVar / NSVar)"
      (list (vstr (clj-resolve (sym "in-ns"))) (vstr (clj-resolve (sym "ns"))))
      '("#'clojure.core/in-ns" "#'clojure.core/ns"))

    ;; —— 动态 Var 与 set! ——
    (load-string "(def ^:dynamic *d* 1)")
    (let ((d (clj-resolve (sym "*d*"))))
      (test "^:dynamic from the symbol's metadata" (clr-call clojure.lang.Var isDynamic d) #t)
      (clr-static-call clojure.lang.Var pushThreadBindings (clj-map d 4))
      (test "dynamic Var read goes through the thread binding" (load-string "*d*") 4)
      (test "set! on a dynamic Var" (begin (load-string "(set! *d* 5)") (load-string "*d*")) 5)
      (clr-static-call clojure.lang.Var popThreadBindings)
      (test "root value after the binding is popped" (load-string "*d*") 1))

    ;; —— 未解析符号的错误文本（file:line:col）——
    (test "unresolved symbol with position of the enclosing form"
      (load-string "\n\n   (nope 1)")
      '(exception "Syntax error (NO_SOURCE_PATH:3:4) Unable to resolve symbol: nope in this context"))
    (test "bare unresolved symbol has no position (as in C#)"
      (load-string "nope")
      '(exception "Syntax error (NO_SOURCE_PATH:0:0) Unable to resolve symbol: nope in this context"))
    (test "no such var"
      (load-string "(q/zz)")
      '(exception "Syntax error (NO_SOURCE_PATH:1:1) No such var: q/zz"))
    (test "file name from *file*"
      (begin
        (clr-static-call clojure.lang.Var pushThreadBindings
          (clj-map (clr-static-field-get clojure.lang.RT SourcePathVar) "src/t.clj"))
        (let ((r (with-clr-exception-handler (lambda (e) (clr-message e)) (lambda () (load-string "(do-it)")))))
          (clr-static-call clojure.lang.Var popThreadBindings)
          r))
      "Syntax error (src/t.clj:1:1) Unable to resolve symbol: do-it in this context")
    (test "ExpanderException data"
      (with-clr-exception-handler
        (lambda (e) (str (clr-call clojure.lang.IExceptionInfo getData e)))
        (lambda () (load-string "\n (nope)")))
      "{:clojure.error/source \"NO_SOURCE_PATH\", :clojure.error/line 2, :clojure.error/column 2, :clojure.error/phase :clojure.error/compile-syntax-check}")
    (test "def errors"
      (load-string "(def)")
      '(exception "Syntax error (NO_SOURCE_PATH:1:1) Too few arguments to def"))
    (test "def outside the current namespace"
      (load-string "(def a/x 1)")
      '(exception "Syntax error (NO_SOURCE_PATH:1:1) Can't create defs outside of current ns"))

    ;; —— macroexpand-1：宏 Var 展开一步，特殊形式不展开 ——
    ;; 还没有 fn*，宏函数用一个 map 充当（map 是 IFn：(m &form &env) = (get m &form &env)，所以宏调用不带参数）
    (load-string "(def m1) (def m2)")
    (let ((m1 (clj-resolve (sym "m1"))) (m2 (clj-resolve (sym "m2"))))
      (clr-call clojure.lang.Var bindRoot m1 (clj-map (read1 "(m1)") (read1 "(dummy-special)")))
      (clr-call clojure.lang.Var setMacro m1)
      (clr-call clojure.lang.Var bindRoot m2 (clj-map (read1 "(m2)") (read1 "(m1)")))
      (clr-call clojure.lang.Var setMacro m2))
    (test "macroexpand-1 of a macro call returns Clojure data"
      (let ((x (clojure-macroexpand-1 (read1 "(m2)"))))
        (list (str x) (clr-is clojure.lang.IPersistentList x) (clr-is clojure.lang.Symbol (first* x))))
      '("(m1)" #t #t))
    (test "macroexpand-1 one step at a time"
      (str (clojure-macroexpand-1 (clojure-macroexpand-1 (read1 "(m2)"))))
      "(dummy-special)")
    (let ((form (read1 "(dummy-special)")))
      (test "macroexpand-1 stops at a clj-special (same object back)"
        (eq? (clojure-macroexpand-1 form) form)
        #t))
    (let ((form (read1 "(def z 1)")))
      (test "macroexpand-1 does not expand def" (eq? (clojure-macroexpand-1 form) form) #t))
    (let ((form (read1 "(q/x 1)")))
      (test "macroexpand-1 of a function call returns the form" (eq? (clojure-macroexpand-1 form) form) #t))
    (let ((form (read1 "(nope 1)")))
      (test "macroexpand-1 of an unresolvable head returns the form" (eq? (clojure-macroexpand-1 form) form) #t))
    (test "macro chain evaluates through the special" (load-string "(m2)") 'expanded-dummy)
    (test "value of a macro is an error"
      (load-string "(let* [k m1] k)")
      '(exception "Syntax error (NO_SOURCE_PATH:1:1) Can't take the value of a macro: #'b/m1"))
    (test "special form name as a value is unresolved"
      (load-string "(let* [k def] k)")
      '(exception "Syntax error (NO_SOURCE_PATH:1:1) Unable to resolve symbol: def in this context"))

    ;; —— 符号与标识符的往返 ——
    (let* ((s (read1 "^:foo q/x"))
           (d (syntax->clj (clj-datum->syntax* #'here s))))
      (test "identifier round trip keeps the Clojure symbol and its metadata"
        (list (clr-is clojure.lang.Symbol d) (str d) (str (meta d)) (eq? d s))
        '(#t "q/x" "{:foo true}" #t)))
    (let* ((f (read1 "(q/x [y] {:k z} #{w} ())"))
           (x (clj->scheme f)))
      (test "unchanged form comes back eq?" (eq? (syntax->datum (datum->syntax #'here x)) f) #t)
      (test "converted form is a Scheme list of identifiers"
        (syntax-case (datum->syntax #'here x) () ((h a b c d) (list (identifier? #'h) (syntax->datum #'h) (str (syntax->clj #'a)))))
        (list #t (sym "q/x") "[y]")))
    (test "scheme->clj turns template output into Clojure data"
      (str (scheme->clj (list 'a/b 1 (clj-vector 'c '()) '())))
      "(a/b 1 [c nil] nil)")

    ;; —— 规模：1000 个 def 后查找不随定义数增长 ——
    (let ()
      (define (time-lookups n-defs)
        (let ((name (string->symbol (string-append "perf" (number->string n-defs)))))
          (in-ns! name)
          (let loop ((i 0))
            (when (< i n-defs)
              (clojure-eval (read1 (string-append "(def v" (number->string i) " " (number->string i) ")")))
              (loop (+ i 1))))
          (let* ((last (string->symbol (string-append "v" (number->string (- n-defs 1)))))
                 (env (namespace-environment (current-ns)))
                 (reps 20000)
                 (sw (clr-static-call System.Diagnostics.Stopwatch StartNew)))
            ;; 标识符解析（查找 rib 的过程：Namespace.GetMapping + Var → 标签）
            (let loop ((i 0)) (when (< i reps) (symbol-label last) (loop (+ i 1))))
            (let ((t1 (clr-call System.Diagnostics.Stopwatch get_ElapsedTicks sw)))
              (clr-call System.Diagnostics.Stopwatch Restart sw)
              ;; 完整展开一个引用（syntax-type → id->label → 绑定扩展 → Lcore）
              (let ((form (read1 (symbol->string last))))
                (let loop ((i 0)) (when (< i 2000) (clojure-expand form) (loop (+ i 1)))))
              (let ((t2 (clr-call System.Diagnostics.Stopwatch get_ElapsedTicks sw))
                    (freq (clr-static-field-get System.Diagnostics.Stopwatch Frequency)))
                (in-ns! 'b)
                (list (/ (* 1e9 (/ t1 freq)) reps) (/ (* 1e9 (/ t2 freq)) 2000)))))))
      (time-lookups 10)                     ; 预热
      (let* ((small (time-lookups 10)) (large (time-lookups 1000)) (huge (time-lookups 3000)))
        (for-each
          (lambda (label r)
            (display "lookup after ") (display label) (display " defs: ")
            (display (exact (round (car r)))) (display " ns/label, ")
            (display (exact (round (cadr r)))) (display " ns/expansion") (newline))
          '(10 1000 3000) (list small large huge))
        ;; 线性查找时 1000 / 10 的比值约为 100；这里只要求明显低于线性
        (test "lookup is not linear in the number of defs (1000 vs 10)" (< (car large) (* 5 (car small))) #t)
        (test "lookup is not linear in the number of defs (3000 vs 10)" (< (car huge) (* 10 (car small))) #t)))))

(display checks) (display " checks, ") (display failures) (display " failures") (newline)
(unless (= failures 0) (exit 1))
