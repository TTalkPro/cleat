;;; cleat-050：(clojure syntax) 的特殊形式、字面量、调用、宿主互操作与 defmacro 垫片，在 Scheme 镜像上展开 → 编译 → 运行。
;;; 规格是 C# 展开器的 tests/Cleat.Core.Tests/ExpanderTests.cs：同名分组逐条翻译，能运行的断言运行结果（Lcore 的形状
;;; 允许不同），需要 cleat-051 的 deftype* / reify* 断言展开结果里的形状数据；错误一律断言 Clojure 的文本。
;;; 另有 test_clojure/special.clj、macros.clj 里不依赖 clojure.core 的回归用例。clojure.core 未加载：测试需要的
;;; 几个函数（list、=、inc …）在下面用 def + fn* + 互操作定义。
(import (rnrs) (rnrs eval) (ironscheme) (ironscheme clr) (ironscheme compiler)
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
(define (clr-type-name e) (clr-call System.Type get_FullName (clr-call System.Object GetType e)))
(define (run thunk)
  (with-clr-exception-handler
    (lambda (e) (list 'exception (clr-message e)))
    (lambda ()
      (guard (e (#t (list 'condition (if (message-condition? e) (condition-message e) e))))
        (thunk)))))
(define (check name thunk expected)
  (set! checks (+ checks 1))
  (let ((got (run thunk)))
    (unless (equal? got expected) (fail! name expected got))))
(define (check-pred name thunk pred)
  (set! checks (+ checks 1))
  (let ((got (run thunk)))
    (unless (pred got) (fail! name "(predicate)" got))))

;;; —— 读入与求值 ——
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
(define (ev s) (let loop ((fs (read-all s)) (last #f)) (if (null? fs) last (loop (cdr fs) (clojure-eval (car fs))))))

;;; (t 名字 源码 期望的打印形式)：求值源码中的全部形式，比较最后一个值的 Clojure 打印形式
(define-syntax t
  (syntax-rules () ((_ name src expected) (check name (lambda () (str (ev src))) expected))))
;;; (t-type 名字 源码 类型全名)：类型在 Clojure 侧取（Scheme 的 clr-call 会把 long 实参转成 fixnum，测不出 Clojure 的值）
(define-syntax t-type
  (syntax-rules ()
    ((_ name src expected)
     (check name (lambda () (str (ev (string-append "(. (class (do " src ")) FullName)")))) (string-append "\"" expected "\"")))))
;;; Var、Type 等用 ToString（clojure.core 未加载，RT 的打印对它们输出 #= 形式）
(define (vstr x) (clr-call System.Object ToString x))
(define-syntax t-str
  (syntax-rules () ((_ name src expected) (check name (lambda () (vstr (ev src))) expected))))
;;; (t-err 名字 源码 完整消息)：期望 CLR 异常（ExpanderException 的消息含 "Syntax error (t.clj:行:列)"）
(define-syntax t-err
  (syntax-rules () ((_ name src expected) (check name (lambda () (ev src)) (list 'exception expected)))))
;;; (t-err-has 名字 源码 子串)
(define (string-contains? s sub)
  (let ((n (string-length s)) (k (string-length sub)))
    (let loop ((i 0)) (cond ((> (+ i k) n) #f) ((string=? (substring s i (+ i k)) sub) #t) (else (loop (+ i 1)))))))
(define-syntax t-err-has
  (syntax-rules ()
    ((_ name src sub)
     (check-pred name (lambda () (ev src))
       (lambda (got) (and (pair? got) (eq? (car got) 'exception) (string-contains? (cadr got) sub)))))))
;;; (t-mx 名字 源码 期望的打印形式)：macroexpand-1
(define-syntax t-mx
  (syntax-rules () ((_ name src expected) (check name (lambda () (str (clojure-macroexpand-1 (read1 src)))) expected))))

;;; Lcore 里第一个 (quote X)，X 是以 head 符号开头的 Clojure 列表（deftype* / reify* 的形状）
(define (find-shape lcore head)
  (let walk ((x lcore))
    (cond
      ((and (pair? x) (eq? (car x) 'quote) (pair? (cdr x))
            (clr-is clojure.lang.ISeq (cadr x))
            (string=? (str (clr-static-call clojure.lang.RT (first System.Object) (cadr x))) head))
       (cadr x))
      ((pair? x) (or (walk (car x)) (walk (cdr x))))
      (else #f))))
(define (shape src head) (let ((s (find-shape (clojure-expand (read1 src)) head))) (if s (str s) "no shape")))

;;; —— 环境：clojure.core 的几个函数、测试命名空间（同 ExpanderTests.SetUp）——
(define source-path-var (clr-static-field-get clojure.lang.RT SourcePathVar))

(with-ns-binding (find-ns 'user)
 (lambda ()
  (clr-static-call clojure.lang.Var pushThreadBindings (clj-map source-path-var "t.clj"))
  (in-ns! 'clojure.core)
  (ev "(def identity (fn* identity [x] x))
       (def instance? (fn* instance? [t x] (. t (IsInstanceOfType x))))
       (def list clojure.lang.PersistentList/creator)
       (def = (fn* = [a b] (. clojure.lang.Util (equiv a b))))
       (def inc (fn* inc [x] (. clojure.lang.Numbers (inc x))))
       (def dec (fn* dec [x] (. clojure.lang.Numbers (dec x))))
       (def < (fn* < [a b] (. clojure.lang.Numbers (lt a b))))
       (def first (fn* first [x] (. clojure.lang.RT (first x))))
       (def second (fn* second [x] (. clojure.lang.RT (second x))))
       (def next (fn* next [x] (. clojure.lang.RT (next x))))
       (def meta (fn* meta [x] (. clojure.lang.RT (meta x))))
       (def with-meta (fn* with-meta [x m] (. x (withMeta m))))
       (def identical? (fn* identical? [a b] (. Object (ReferenceEquals a b))))
       (def class (fn* class [x] (if x (. x (GetType)) nil)))")
  (in-ns! 'cleat.test.expander)
  (apply refer! 'clojure.core '(identity instance? list = inc dec < first second next meta with-meta identical? class))
  (import! "System.String")
  (import! "System.Text.StringBuilder")
  (ev "(def f (fn* [& xs] xs))
       (def ^:dynamic dyn 1)
       (def when2 (fn* [&form &env t b] (list 'if t b nil)))
       (. (var when2) (setMacro))
       (def env-keys (fn* [&form &env] (list 'quote (. clojure.lang.RT (keys &env)))))
       (. (var env-keys) (setMacro))
       (def ^{:inline (fn* [a b] (list 'System.Math/Max a b)) :inline-arities (fn* [n] (= n 2))} plus (fn* [& xs] xs))")

  ;; ———————— ConstantsAndCollections ————————
  (t "const 1" "1" "1")
  (t-type "long literal stays Int64" "1" "System.Int64")
  (t "const nil" "nil" "nil")
  (t "const :k" ":k" ":k")
  (t "const string" "\"s\"" "\"s\"")
  (t "quoted list" "'(a b)" "(a b)")
  (t-type "quoted list is a PersistentList" "'(a b)" "clojure.lang.PersistentList")
  (t "constant vector" "[1 2]" "[1 2]")
  (check "constant vector is a constant in the expansion"
    (lambda () (let ((x (clojure-expand (read1 "[1 2]")))) (and (pair? x) (eq? (car x) 'quote) (str (cadr x)))))
    "[1 2]")
  (check "constructed vector is not a constant"
    (lambda () (let ((x (clojure-expand (read1 "[1 f]")))) (and (pair? x) (eq? (car x) 'quote))))
    #f)
  (t "vector with a Var element is constructed" "[1 (identical? (second [1 f]) f)]" "[1 true]")
  (t-type "elements of a constructed vector keep their type" "(first [1 f])" "System.Int64")
  (t "map with a Var value" "(identical? (:a {:a f}) f)" "true")
  (t "set with a Var element" "(identical? (first #{f}) f)" "true")
  (t "metadata on a constant vector" "(meta ^:foo [1])" "{:foo true}")
  (t "metadata on a constructed vector" "(meta ^{:a 1} [f])" "{:a 1}")
  (t "quoted element inside a constant vector is its value" "[1 'a]" "[1 a]")
  (t "nested constant collections" "{:a [1 #{2}]}" "{:a [1 #{2}]}")
  (t-type "empty list literal is not nil" "()" "clojure.lang.PersistentList+EmptyList")
  (t-type "regex literal" "#\"a+\"" "System.Text.RegularExpressions.Regex")
  (t "char literal" "\\a" "\\a")
  (t-type "bigint literal" "1N" "clojure.lang.BigInt")
  (t "ratio literal" "1/2" "1/2")
  (t-err "duplicate keys in a constructed map" "(let* [k :a] {k 1 :a 2})" "Duplicate key: :a")

  ;; ———————— SymbolsAndVars ————————
  (t "Var reference" "(identical? f (. (var f) (getRawRoot)))" "true")
  (t "dynamic Var reference" "dyn" "1")
  (check "dynamic Var reads the thread binding"
    (lambda ()
      (let ((v (clj-resolve (clj-symbol "dyn"))))
        (clr-static-call clojure.lang.Var pushThreadBindings (clj-map v 5))
        (let ((r (str (ev "dyn")))) (clr-static-call clojure.lang.Var popThreadBindings) r)))
    "5")
  (t-str "#'f" "#'f" "#'cleat.test.expander/f")
  (t-str "imported type" "String" "System.String")
  (t-str "fully qualified type" "System.Int64" "System.Int64")
  (t "Type/static field" "String/Empty" "\"\"")
  (t "Type/static long field stays long" "Int64/MaxValue" "9223372036854775807")
  (t "array type long/1" "(. long/1 FullName)" "\"System.Int64[]\"")
  (t-err "unresolved symbol" "nope" "Syntax error (t.clj:0:0) Unable to resolve symbol: nope in this context")
  (t-err "no such namespace" "no.such/x" "Syntax error (t.clj:0:0) No such namespace: no.such")
  (t-err "value of a macro" "when2" "Syntax error (t.clj:0:0) Can't take the value of a macro: #'cleat.test.expander/when2")
  (t "^:const Var is inlined"
     "(def ^:const k5 5) (def get-k5 (fn* [] k5)) (def k5 6) [(get-k5) k5]" "[5 6]")

  ;; ———————— ControlFormsAndLocals ————————
  (t "if then" "(if 1 2 3)" "2")
  (t "if without else" "(if 1 2)" "2")
  (t "if nil is false" "(if nil 2 3)" "3")
  (t "if false is false" "(if false 2 3)" "3")
  (t "if false without else is nil" "(if false 2)" "nil")
  (t "if 0 is true" "(if 0 2 3)" "2")
  (t "if empty list is true" "(if () 2 3)" "2")
  (t "do" "(do 1 2)" "2")
  (t "empty do is nil" "(do)" "nil")
  (t "let*" "(let* [x 1 y x] y)" "1")
  (t "nested let* shadows" "(let* [x 1] [(let* [x 2] x) x])" "[2 1]")
  (t "loop* / recur" "(loop* [i 0 acc []] (if (< i 3) (recur (inc i) [acc i]) acc))" "[[[[] 0] 1] 2]")
  (t "loop* bindings are sequential" "(loop* [a 1 b (inc a)] b)" "2")
  (t "loop* in argument position" "[(loop* [i 0] (if (< i 5) (recur (inc i)) i))]" "[5]")
  (t "letfn* mutual recursion"
     "(letfn* [ev? (fn* ev? [n] (if (= n 0) true (od? (dec n)))) od? (fn* od? [n] (if (= n 0) false (ev? (dec n))))] [(ev? 10) (od? 7)])"
     "[true true]")
  (t "letfn* names the functions" "(letfn* [g (fn* [] 1)] (. (class g) Name))" "\"ClojureFn\"")
  (t "throw / catch" "(try (throw (Exception. \"boom\")) (catch Exception e (.-Message e)))" "\"boom\"")
  (t-err-has "throw of a non-exception" "(throw f)" "Unable to cast")
  (t "monitor-enter / monitor-exit" "(let* [o (Object.)] (monitor-enter o) (monitor-exit o))" "nil")
  (t-err "if: too few" "(if 1)" "Syntax error (t.clj:1:1) Too few arguments to if")
  (t-err "if: too many" "(if 1 2 3 4)" "Syntax error (t.clj:1:1) Too many arguments to if")
  (t-err "let*: odd bindings" "(let* [x] x)" "Syntax error (t.clj:1:1) Bad binding form, expected matched symbol/value pairs.")
  (t-err "let*: not a vector" "(let* (x 1) x)" "Syntax error (t.clj:1:1) Bad binding form, expected vector")
  (t-err "let*: not a symbol" "(let* [1 2] 1)" "Syntax error (t.clj:1:1) Bad binding form, expected symbol, got 1")
  (t-err "let*: qualified" "(let* [a/b 2] 1)" "Syntax error (t.clj:1:1) Can't let qualified name: a/b")
  (t-err "recur: count" "(loop* [i 0] (recur 1 2))" "Syntax error (t.clj:1:14) Mismatched argument count to recur, expected: 1 args, got: 2")
  (t-err "recur: not in tail position" "(loop* [i 0] (do (recur 1) 2))" "Syntax error (t.clj:1:18) Can only recur from tail position")
  (t-err "recur: across try" "(loop* [i 0] (try (recur 1)))" "Syntax error (t.clj:1:19) Cannot recur across try")
  (t-err "recur: in an argument" "(loop* [i 0] (f (recur 1)))" "Syntax error (t.clj:1:17) Can only recur from tail position")
  (t-err "recur: in an if test" "(loop* [i 0] (if (recur 1) 1 2))" "Syntax error (t.clj:1:18) Can only recur from tail position")
  (t-err "recur: at top level" "(recur 1)" "Syntax error (t.clj:1:1) Can only recur from tail position")
  (t-err "recur: in a loop* init" "(loop* [i 0] (loop* [j (recur 1)] j))" "Syntax error (t.clj:1:24) Can only recur from tail position")
  (t "recur through a macro in tail position" "(loop* [x 0] (when2 (< x 3) (recur (inc x))))" "nil")
  (t "special form in head position is not shadowed by a local" "(let* [if false] (if if 2 3))" "3")
  (t "a local named like a special is a value" "(let* [do 5] do)" "5")
  (t-err "special-form name as a value is unresolved" "(let* [k def] k)" "Syntax error (t.clj:1:1) Unable to resolve symbol: def in this context")
  (t-err "& as a let* binding" "(let* [& 42] &)" "Syntax error (t.clj:1:1) Can't use & as a local binding")
  (t-err "& as a loop* binding" "(loop* [& 42] &)" "Syntax error (t.clj:1:1) Can't use & as a local binding")
  (t-err "catch outside try" "(catch Exception e 1)" "Syntax error (t.clj:1:1) catch must be inside try")

  ;; ———————— Functions ————————
  (t "fn* call" "((fn* [a b] a) 1 2)" "1")
  (t "fn* is a clojure.lang.Fn and IFn" "[(instance? clojure.lang.Fn (fn* [] 1)) (instance? clojure.lang.IFn (fn* [] 1))]" "[true true]")
  (t "multi-arity fn*" "(let* [g (fn* ([a] a) ([a & r] r))] [(g 1) (g 1 2 3)])" "[1 (2 3)]")
  (t "rest args are a seq or nil" "(let* [g (fn* [& r] r)] [(g) (instance? clojure.lang.ISeq (g 1))])" "[nil true]")
  (t "self name" "(let* [g (fn* me [] me)] (identical? g (g)))" "true")
  (t "^:once fn*" "((^:once fn* [] 1))" "1")
  (t "recur in fn*" "((fn* [n acc] (if (< n 1) acc (recur (dec n) (inc acc)))) 5 0)" "5")
  (t "recur in a variadic fn* passes the rest seq" "((fn* [n & r] (if (< n 1) r (recur (dec n) [n r]))) 2 :x)" "[1 [2 (:x)]]")
  (t "closure captures a local" "(let* [x 7] ((fn* [] x)))" "7")
  (t "applyTo passes the rest lazily" "(. (fn* [a & r] a) (applyTo (. clojure.lang.Repeat (create 1))))" "1")
  (t "fn* with 20 params" "((fn* [a1 a2 a3 a4 a5 a6 a7 a8 a9 a10 a11 a12 a13 a14 a15 a16 a17 a18 a19 a20] a20) 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15 16 17 18 19 20)" "20")
  (t "variadic call with more than 20 args" "(let* [g (fn* [& r] (. clojure.lang.RT (count r)))] (g 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15 16 17 18 19 20 21 22))" "22")
  (t "metadata on a fn* form" "(meta ^{:a 1} (fn* [] 1))" "{:a 1}")
  (t-err "arity error names the def'd fn" "(def g1 (fn* [a] a)) (g1)" "Wrong number of args (0) passed to: cleat.test.expander/g1")
  (t-err-has "arity error of an anonymous fn" "((fn* [a] a))" "Wrong number of args (0) passed to: cleat.test.expander/fn--")
  (t-err "fn: same arity" "(fn* ([a] a) ([b] b))" "Syntax error (t.clj:1:1) Can't have 2 overloads with the same arity.")
  (t-err "fn: two variadic" "(fn* ([a & r] a) ([b & s] b))" "Syntax error (t.clj:1:1) Can't have more than 1 variadic overload")
  (t-err "fn: fixed more than variadic" "(fn* ([a b] a) ([b & s] b))" "Syntax error (t.clj:1:1) Can't have fixed arity methods with more params than the variadic method.")
  (t-err "fn: qualified param" "(fn* [a/b] a)" "Syntax error (t.clj:1:1) Can't use qualified name as parameter: a/b")
  (t-err "fn: hinted rest" "(fn* [a & ^String r] a)" "Syntax error (t.clj:1:1) & arg cannot have type hint")
  (t-err "fn: 21 params" "(fn* [a a1 a2 a3 a4 a5 a6 a7 a8 a9 a10 a11 a12 a13 a14 a15 a16 a17 a18 a19 a20] a)" "Syntax error (t.clj:1:1) Can't specify more than 20 params")
  (t-err "fn: params not a vector" "(fn* (a) a)" "Syntax error (t.clj:1:1) Parameter declaration should be a vector")
  (t-err "fn: & without rest" "(fn* [a &] a)" "Syntax error (t.clj:1:1) Invalid parameter list")

  ;; ———————— DefAndSetBang ————————
  (t-str "def returns the Var" "(def x 1)" "#'cleat.test.expander/x")
  (t "def meta has line, column, file" "(def x 1) (meta (var x))" "{:line 1, :column 1, :file \"t.clj\", :name x, :ns cleat.test.expander}")
  (t-type "def meta line is a long" "(:line (meta (def x 1)))" "System.Int64")
  (t "def value" "x" "1")
  (t "dynamic def with docstring" "(def ^:dynamic *y* \"doc\" 2) [(. (var *y*) (isDynamic)) (:doc (meta (var *y*))) *y*]" "[true \"doc\" 2]")
  (t "def without init is unbound" "(def z) (. (var z) (isBound))" "false")
  (t "def metadata is evaluated" "(def ^{:k (inc 1)} w 1) (:k (meta #'w))" "2")
  (t ":arglists is unquoted" "(def ^{:arglists '([a])} al 1) (:arglists (meta #'al))" "([a])")
  (check "warning for a non-dynamic earmuffed name"
    (lambda ()
      (let ((sw (clr-new System.IO.StringWriter)))
        (clr-static-call clojure.lang.Var pushThreadBindings (clj-map (clr-static-field-get clojure.lang.RT ErrVar) sw))
        (ev "(def *nd* 1)")
        (clr-static-call clojure.lang.Var popThreadBindings)
        (clr-call System.String Trim (clr-call System.Object ToString sw))))
    "Warning: *nd* not declared dynamic and thus is not dynamically rebindable, but its name suggests otherwise. Please either indicate ^:dynamic *nd* or change the name. (t.clj:1)")
  (check "set! of a bound dynamic Var"
    (lambda ()
      (let ((v (clj-resolve (clj-symbol "dyn"))))
        (clr-static-call clojure.lang.Var pushThreadBindings (clj-map v 1))
        (let ((r (str (ev "[(set! dyn 3) dyn]")))) (clr-static-call clojure.lang.Var popThreadBindings) r)))
    "[3 3]")
  (t-err "set! of an unbound dynamic Var" "(set! dyn 3)" "Can't change/establish root binding of: dyn with set")
  (t-err "def: too few" "(def)" "Syntax error (t.clj:1:1) Too few arguments to def")
  (t-err "def: too many" "(def a b c d)" "Syntax error (t.clj:1:1) Too many arguments to def")
  (t-err "def: not a symbol" "(def 1 2)" "Syntax error (t.clj:1:1) First argument to def must be a Symbol")
  (t-err "set!: invalid target" "(set! (let* [] 1) 2)" "Syntax error (t.clj:1:1) Invalid assignment target")
  (t-err "set!: immutable local" "(let* [a 1] (set! a 2))" "Syntax error (t.clj:1:13) Cannot assign to non-mutable: a")
  (t-err "set!: malformed" "(set! a)" "Syntax error (t.clj:1:1) Malformed assignment, expecting (set! target val)")

  ;; ———————— MacrosInlineAndEnv ————————
  (t "macro" "(when2 1 2)" "2")
  (t "macro, false branch" "(when2 nil 2)" "nil")
  (t "&env has the lexical locals" "(let* [a 1] (env-keys))" "(a)")
  (t "&env is nil at top level" "(env-keys)" "nil")
  (t "&env sees fn params, loop and catch locals"
     "(let* [a 1] ((fn* [b] (loop* [c 1] (try (throw (Exception.)) (catch Exception e (. clojure.lang.RT (count (env-keys))))))) 2))" "4")
  (t "&env values are LocalBindings" "(def lb (fn* [&form &env] (. (class (first (. clojure.lang.RT (vals &env)))) Name))) (. #'lb (setMacro)) (let* [q 1] (lb))" "\"LocalBinding\"")
  (t-mx "macroexpand-1" "(when2 1 2)" "(if 1 2 nil)")
  (check "macroexpand" (lambda () (str (clojure-macroexpand (read1 "(when2 1 2)")))) "(if 1 2 nil)")
  (t "inline" "(plus 1 2)" "2")
  (t "inline arities: not inlined" "(plus 1 2 3)" "(1 2 3)")
  (t-err-has "a local shadows a macro" "(let* [when2 1] (when2 1 2))" "clojure.lang.IFn")
  (t "macro receives &form as Clojure data"
     "(def mf (fn* [&form &env x] (list 'quote [(. (class &form) Name) (. (class (first &form)) Name) &form]))) (. #'mf (setMacro)) (mf [1])"
     "[\"PersistentList\" \"Symbol\" (mf [1])]")
  (t-err "macro arity error hides &form and &env" "(when2 1)" "Syntax error (t.clj:1:1) Wrong number of args (1) passed to: cleat.test.expander/when2")
  (t-err "macro throwing" "(def boom (fn* [&form &env] (throw (ArgumentException. \"bad\")))) (. #'boom (setMacro)) (boom)" "Syntax error (t.clj:1:88) bad")
  (t "unhygienic capture, as in Clojure"
     "(def capt (fn* [&form &env body] (list 'let* ['x 10] body))) (. #'capt (setMacro)) (let* [x 1] (capt x))" "10")
  (t "macro building a symbol from a string"
     "(def defmaker (fn* [&form &env n] (list 'def (. clojure.lang.Symbol (intern (. String (Concat \"make-\" (. n Name))))) (list 'fn* [] 42)))) (. #'defmaker (setMacro)) (defmaker foo) (make-foo)"
     "42")
  (ev "(def th (fn* [&form &env x form] (with-meta (. clojure.lang.RT (listStar (first form) x (next form))) (meta form))))
       (. #'th (setMacro))")
  (check "macroexpand-1 keeps metadata of the parts (->metadata-test)"
    (lambda ()
      (let* ((form (read1 "(th ^{:foo :bar} a ^{:bar :baz} (b c d))"))
             (x (clojure-macroexpand-1 form)))
        (list (str x)
              (str (clr-static-call clojure.lang.RT (get System.Object System.Object)
                     (clr-static-call clojure.lang.RT (meta System.Object) x) (clj-keyword "bar")))
              (str (clr-static-call clojure.lang.RT (meta System.Object) (clr-static-call clojure.lang.RT (second System.Object) x))))))
    '("(b a c d)" ":baz" "{:foo :bar}"))
  (in-ns! 'other)
  (ev "(def ^:private pm (fn* [&form &env] 1)) (. #'pm (setMacro))")
  (in-ns! 'cleat.test.expander)
  (t-err "private macro of another namespace" "(other/pm)" "Syntax error (t.clj:1:1) var: other/pm is not public")
  (t "macro c (macros.clj ->test)" "(def c (fn* [&form &env arg] (if (= 'b (first arg)) :foo :bar))) (. #'c (setMacro)) [(c (b a)) (c (x a))]" "[:foo :bar]")

  ;; macroexpand-1 返回每个特殊形式本身（designs/14 §4 的停止点）
  (for-each
    (lambda (src)
      (check (string-append "macroexpand-1 stops at " src)
        (lambda () (let ((f (read1 src))) (eq? (clojure-macroexpand-1 f) f)))
        #t))
    '("(def a 1)" "(if 1 2 3)" "(do 1)" "(let* [a 1] a)" "(loop* [a 1] a)" "(recur 1)" "(fn* [a] a)" "(quote a)"
      "(var f)" "(throw e)" "(try 1)" "(monitor-enter o)" "(monitor-exit o)" "(. o m)" "(new Object)" "(set! a 1)"
      "(letfn* [a (fn* [] 1)] a)" "(case* x 0 0 nil {} :compact :int)" "(deftype* A B [])" "(reify* [])"
      "(import* \"System.String\")" "(f 1)" "(nope 1)"))
  (t-mx "macroexpand-1 of .method" "(.Length s)" "(. s Length)")
  (t-mx "macroexpand-1 of Type." "(StringBuilder. \"x\")" "(new StringBuilder \"x\")")
  (t-mx "macroexpand-1 of Type/member" "(String/Concat \"a\")" "(. String Concat \"a\")")
  (t-mx "macroexpand-1 of Type/new" "(StringBuilder/new \"x\")" "(new StringBuilder \"x\")")
  (t-mx "macroexpand-1 of .method on a type" "(.Name String)" "(. (clojure.core/identity String) Name)")

  ;; ———————— Invocations ————————
  (t "call a Var" "(f 1)" "(1)")
  (t "keyword call" "(:k {:k 3})" "3")
  (t "keyword call with default" "(:k {} 4)" "4")
  (t "instance? with a literal type" "[(instance? String \"s\") (instance? String f)]" "[true false]")
  (t "immediate fn call" "((fn* [] 1))" "1")
  (t "map, vector, set and Var as functions" "[({:a 1} :a) ([5 6] 1) (#{:s} :s) (#'f 2)]" "[1 6 :s (2)]")
  (t-err "call nil" "(nil 1)" "Syntax error (t.clj:1:1) Can't call nil, form: (nil 1)")
  (t-err-has "call a number" "(1 2)" "clojure.lang.IFn")

  ;; ———————— HostInterop ————————
  (t ". instance property" "(. \"abc\" Length)" "3")
  (t-type "interop results follow Clojure's conversions (int → long)" "(. \"abc\" Length)" "System.Int64")
  (t ".method" "(.Length \"abc\")" "3")
  (t ".-field" "(.-Length \"abc\")" "3")
  (t ". with a method form" "(. \"abcd\" (Substring 1 2))" "\"bc\"")
  (t "hinted local" "(let* [^String s \"abcd\"] (.Substring s 1))" "\"bcd\"")
  (t "static call" "(String/Concat \"a\" \"b\")" "\"ab\"")
  (t ". static with a method form" "(. String (Concat \"a\"))" "\"a\"")
  (t "Type/.method" "(String/.Trim \" x \")" "\"x\"")
  (t "Type." "(. (StringBuilder. \"x\") ToString)" "\"x\"")
  (t "new" "(. (new StringBuilder) ToString)" "\"\"")
  (t "Type/new" "(.ToString (StringBuilder/new \"x\"))" "\"x\"")
  (t "set! of a property" "(let* [sb (StringBuilder. \"abc\")] (set! (.-Length sb) 1) (.ToString sb))" "\"a\"")
  (t "set! of a property via ." "(let* [sb (StringBuilder. \"abc\")] (set! (. sb Length) 2) (.ToString sb))" "\"ab\"")
  (t "generic method with type-args" "(. String (Join (type-args Object) \",\" [1 2]))" "\"1,2\"")
  (t "import*" "(import* \"System.Text.Encoding\")" "nil")
  (t "imported type in a later form" "(. (. Encoding (get_UTF8)) WebName)" "\"utf-8\"")
  (t "string array hint" "(let* [^\"System.String[]\" a (. \"a,b,c\" (Split (.ToCharArray \",\")))] (.Length a))" "3")
  (t-err-has "method call on nil" "(.ToString nil)" "on nil")
  (t-err "new: unknown type" "(new NoSuchType)" "Syntax error (t.clj:1:1) Unable to resolve classname: NoSuchType")
  (t-err "Type.: unknown type" "(NoSuchType. 1)" "Syntax error (t.clj:1:1) Unable to resolve classname: NoSuchType")
  (t-err ". malformed" "(. f)" "Syntax error (t.clj:1:1) Malformed member expression, expecting (. target member ... ) or  (. target (member ...))")
  (t-err ".method without target" "(.Length)" "Syntax error (t.clj:1:1) Malformed member expression, expecting (.member target ...)")
  (t-err "import* of an unknown class" "(import* \"No.Such\")" "Syntax error (t.clj:1:1) Unable to resolve classname: No.Such")
  (t-err "by-ref" "(. String (Concat (by-ref f)))" "Syntax error (t.clj:1:1) by-ref arguments are not supported yet")

  ;; ———————— TryCatchFinally ————————
  (t "try / catch / finally" "(let* [sb (StringBuilder.)] [(try 1 (catch Exception e e) (finally (.Append sb \"f\"))) (.ToString sb)])" "[1 \"f\"]")
  (t "try without clauses" "(try 1)" "1")
  (t "empty try" "(try)" "nil")
  (t "first matching catch" "(try (throw (ArgumentException. \"x\")) (catch System.ArgumentException e 2) (catch Exception e 3))" "2")
  (t "second catch" "(try (throw (Exception. \"x\")) (catch ArgumentException e 2) (catch Exception e 3))" "3")
  (t "catch ExceptionInfo" "(try (throw (clojure.lang.ExceptionInfo. \"m\" {:a 1})) (catch clojure.lang.ExceptionInfo e (.getData e)))" "{:a 1}")
  (t "finally runs on throw"
     "(let* [sb (StringBuilder.)] (try (try (throw (Exception. \"x\")) (finally (.Append sb \"f\"))) (catch Exception e (.Append sb \"c\"))) (.ToString sb))"
     "\"fc\"")
  (t "catch local is hinted" "(try (throw (Exception. \"m\")) (catch Exception e (.-Message e)))" "\"m\"")
  (t-err "catch then body" "(try 1 (catch Exception e 2) 3)" "Syntax error (t.clj:1:1) Only catch or finally clause can follow catch in try expression")
  (t-err "finally not last" "(try 1 (finally 2) 3)" "Syntax error (t.clj:1:1) finally clause must be last in try expression")
  (t-err "unknown catch class" "(try 1 (catch Nope e 2))" "Syntax error (t.clj:1:1) Unable to resolve classname: Nope")
  (t-err "recur in catch" "(loop* [i 0] (try 1 (catch Exception e (recur 1))))" "Syntax error (t.clj:1:40) Cannot recur across try")

  ;; ———————— CaseStar ————————
  ;; (case x 1 :a 2 :b :d) 的 case 宏输出形态
  (t "case* :int hit" "(let* [x 1] (case* x 0 0 :d {1 [1 :a], 2 [2 :b]} :compact :int))" ":a")
  (t "case* :int second" "(let* [x 2] (case* x 0 0 :d {1 [1 :a], 2 [2 :b]} :compact :int))" ":b")
  (t "case* :int default" "(let* [x 3] (case* x 0 0 :d {1 [1 :a], 2 [2 :b]} :compact :int))" ":d")
  (t "case* :int non-number" "(let* [x \"1\"] (case* x 0 0 :d {1 [1 :a]} :compact :int))" ":d")
  (let ((h (lambda (x) (number->string (clr-static-call clojure.lang.Util (hash System.Object) x)))))
    (t "case* :hash-equiv"
       (string-append "(let* [x \"s\"] (case* x 0 0 :d {" (h "s") " [\"s\" :a]} :sparse :hash-equiv))") ":a")
    (t "case* :hash-equiv miss"
       (string-append "(let* [x \"t\"] (case* x 0 0 :d {" (h "s") " [\"s\" :a]} :sparse :hash-equiv))") ":d")
    (t "case* skip-check"
       (string-append "(let* [x \"s\"] (case* x 0 0 :d {" (h "s") " [nil :hit]} :sparse :hash-equiv #{" (h "s") "}))") ":hit")
    (t "case* :hash-identity"
       (string-append "(let* [x :b] (case* x 0 0 :d {" (h (clj-keyword "a")) " [:a 1] " (h (clj-keyword "b")) " [:b 2]} :sparse :hash-identity))") "2"))
  (t "case* then in tail position can recur"
     "(loop* [i 0] (case* i 0 0 i {0 [0 (recur 1)]} :compact :int))" "1")
  (t-err "case* needs a local" "(case* 1 0 0 :d {} :compact :int)" "Syntax error (t.clj:1:1) case* expects a local as its expression")

  ;; ———————— DeftypeAndReify（需要 cleat-051：断言展开形状与运行时报错）————————
  (check "deftype* shape"
    (lambda () (shape "(deftype* P cleat.test.expander.P [a ^:unsynchronized-mutable b] :implements [clojure.lang.IDeref clojure.lang.Counted] (deref [this] a) (count [_] (set! b 2) 1))" "deftype"))
    "(deftype \"P\" \"cleat.test.expander.P\" ((field \"a\" \"System.Object\" false false) (field \"b\" \"System.Object\" true false)) (\"clojure.lang.IDeref\" \"clojure.lang.Counted\") ((method \"clojure.lang.IDeref\" \"deref\" \"this\" () \"System.Object\") (method \"clojure.lang.Counted\" \"count\" \"_\" () \"System.Int32\")) {:implements [clojure.lang.IDeref clojure.lang.Counted], :reify false})")
  (t-err "deftype* needs cleat-051 at run time"
     "(deftype* P cleat.test.expander.P [a] :implements [clojure.lang.IDeref] (deref [this] a))"
     "deftype* is not supported yet: it needs the cleat-051 compiler (Clojure nodes: real CLR types)")
  (check "overloaded method chosen by hints"
    (lambda () (shape "(deftype* P P [] :implements [System.IComparable |System.IComparable`1[System.String]|] (CompareTo [_ ^String o] 0) (CompareTo [_ ^Object o] 1))" "deftype"))
    "(deftype \"P\" \"P\" () (\"System.IComparable\" \"System.IComparable`1[[System.String, System.Private.CoreLib, Version=10.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]]\") ((method \"System.IComparable`1[[System.String, System.Private.CoreLib, Version=10.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]]\" \"CompareTo\" \"_\" ((\"o\" \"System.String\")) \"System.Int32\") (method \"System.IComparable\" \"CompareTo\" \"_\" ((\"o\" \"System.Object\")) \"System.Int32\")) {:implements [System.IComparable System.IComparable`1[System.String]], :reify false})")
  (check "explicit Iface.name"
    (lambda () (shape "(deftype* P P [] :implements [System.IComparable |System.IComparable`1[System.String]|] (System.IComparable.CompareTo [_ o] 0))" "deftype"))
    "(deftype \"P\" \"P\" () (\"System.IComparable\" \"System.IComparable`1[[System.String, System.Private.CoreLib, Version=10.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]]\") ((method \"System.IComparable\" \"CompareTo\" \"_\" ((\"o\" \"System.Object\")) \"System.Int32\")) {:implements [System.IComparable System.IComparable`1[System.String]], :reify false})")
  (check "reify* shape"
    (lambda ()
      (let* ((s (shape "(reify* [clojure.lang.IDeref] (deref [_] 1) (ToString [_] \"p\"))" "deftype"))
             (i (let loop ((i 1)) (if (char=? (string-ref s i) #\() i (loop (+ i 1))))))
        (list (substring s 0 37) (substring s i (string-length s)))))
    (list "(deftype \"cleat.test.expander$reify__" "() (\"clojure.lang.IDeref\" \"clojure.lang.IObj\") ((method \"clojure.lang.IDeref\" \"deref\" \"_\" () \"System.Object\") (method nil \"ToString\" \"_\" () \"System.String\")) {:reify true})"))
  (t-err "reify* needs cleat-051 at run time" "(reify* [clojure.lang.IDeref] (deref [_] 1))"
     "reify* is not supported yet: it needs the cleat-051 compiler (Clojure nodes: real CLR types)")
  (t-err "method not in interfaces" "(deftype* P P [a] :implements [clojure.lang.IDeref] (Nope [this] a))" "Syntax error (t.clj:1:1) Can't define method not in interfaces: Nope")
  (t-err "overloaded method needs a hint" "(deftype* P P [] :implements [System.IComparable |System.IComparable`1[System.String]|] (CompareTo [_ o] 0))" "Syntax error (t.clj:1:1) Must hint overloaded method: CompareTo")
  (t-err "hint matches no overload" "(deftype* P P [] :implements [System.IComparable |System.IComparable`1[System.String]|] (CompareTo [_ ^long o] 0))" "Syntax error (t.clj:1:1) Can't find matching overloaded method: CompareTo")
  (t-err "set! of an immutable field" "(deftype* P P [a] :implements [clojure.lang.IDeref] (deref [this] (set! a 1)))" "Syntax error (t.clj:1:67) Cannot assign to non-mutable: a")
  (t-err "only interfaces" "(deftype* P P [a] :implements [String] (First [this] a))" "Syntax error (t.clj:1:1) only interfaces are supported, had: String")
  (t-err "unknown explicit interface" "(deftype* P P [] :implements [clojure.lang.IDeref] (Nope.deref [_] 1))" "Syntax error (t.clj:1:1) Unable to find interface Nope for explicit method implementation: Nope.deref")
  (t-err "missing this" "(deftype* P P [] :implements [clojure.lang.IDeref] (deref [] 1))" "Syntax error (t.clj:1:1) Must supply at least one argument for 'this' in: deref")
  (t-err "recur arity in a method" "(deftype* P P [] :implements [clojure.lang.IDeref] (deref [_] (recur 1)))" "Syntax error (t.clj:1:63) Mismatched argument count to recur, expected: 0 args, got: 1")

  ;; ———————— quote / var / 其他 ————————
  (t-err "quote with multiple args (special.clj)" "(quote 1 2 3)" "Syntax error (t.clj:1:1) Wrong number of args to quote")
  (t-err "var of an unknown name" "(var nope)" "Syntax error (t.clj:1:1) Unable to resolve var: nope in this context")
  (t-err "var of a type" "(var String)" "Syntax error (t.clj:1:1) Expecting var, but String is mapped to System.String")
  (t-str "var of a qualified name" "(var clojure.core/inc)" "#'clojure.core/inc")
  (t-err "throw with two args" "(throw 1 2)" "Syntax error (t.clj:1:1) Too many arguments to throw, throw expects a single Exception instance")
  (t-err "monitor-enter without args" "(monitor-enter)" "Syntax error (t.clj:1:1) monitor-enter requires one argument")

  (clr-static-call clojure.lang.Var popThreadBindings)))

(display checks) (display " checks, ") (display failures) (display " failures") (newline)
(unless (= failures 0) (exit 1))
