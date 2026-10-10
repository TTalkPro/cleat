;;; cleat-048：可扩展语法对象（designs/15 §2）的端到端测试，在镜像上运行。
;;; 导入 (clojure syntax-kinds) 即登记 Clojure 的向量 / map / set / 列表为语法扩展；
;;; Scheme 的 reader 读不出 Clojure 字面量，含 Clojure 数据的形式用准引用拼出来再 eval（展开 → 编译 → 运行）。
(import (rnrs) (rnrs eval) (ironscheme) (ironscheme clr) (ironscheme syntax-ext) (clojure syntax-kinds))

(define env (environment '(rnrs) '(ironscheme) '(ironscheme clr) '(ironscheme syntax-ext) '(clojure syntax-kinds)))
(define (run expr) (eval expr env))
(define failures 0)
(define checks 0)
(define (fail! name expected got)
  (set! failures (+ failures 1))
  (display "FAIL: ") (display name) (newline)
  (display "  expected: ") (write expected) (newline)
  (display "  got:      ") (write got) (newline))
(define (check name thunk expected)
  (set! checks (+ checks 1))
  (let ((got (guard (e (#t (list 'exception (if (message-condition? e) (condition-message e) e))))
               (thunk))))
    (unless (equal? got expected) (fail! name expected got))))
(define-syntax test
  (syntax-rules ()
    ((_ name expr expected) (check name (lambda () expr) expected))))

;;; 工具
(define (type-name x) (clr-call System.Type get_Name (clr-call System.Object GetType x)))
(define (clj-str x) (clr-static-call clojure.lang.RT (printString System.Object) x))
(define (clj= a b) (clr-static-call clojure.lang.Util (equiv System.Object System.Object) a b))
(define (with-meta x m) (clr-call clojure.lang.IObj withMeta x m))
(define (meta x) (clr-call clojure.lang.IMeta meta x))
(define k (clj-keyword "k"))
(define j (clj-keyword "j"))
(define tag (clj-map (clj-keyword "tag") (clj-keyword "t")))
(define here #'here)

;;; 登记
(test "registry: vector first, then the Clojure kinds"
  (map syntax-extension-kind (syntax-extensions))
  '(vector clj-vector clj-map clj-set clj-list))
(test "syntax-extension-of picks the kind by pred"
  (map (lambda (x) (let ((e (syntax-extension-of x))) (and e (syntax-extension-kind e))))
       (list '#(1) (clj-vector 1) (clj-map k 1) (clj-set 1) (clj-list 1) '(1) 'a 1))
  '(vector clj-vector clj-map clj-set clj-list #f #f #f))

;;; 往返：datum->syntax → syntax->datum 保留集合类型、元素顺序与元数据
(define nested
  (with-meta (clj-vector 1 (with-meta (clj-map k (clj-list 'a 'b) j (clj-set 3)) tag) "s" '(x . #(y)))
             tag))
(test "round trip: unchanged datum comes back"
  (let ((d (syntax->datum (datum->syntax here nested))))
    (list (type-name d) (clj-str d) (clj= (meta d) tag)))
  (list "PersistentVector" (clj-str nested) #t))
(test "round trip through a template: types, order and metadata of every level"
  (let ((d (run `(syntax-case (datum->syntax #'here ',nested) ()
                   (x (syntax->datum (syntax ,(clj-vector 'x 'x))))))))
    (list (type-name d) (clj-str d)
          (type-name (seq-first d)) (clj= (meta (seq-first d)) tag)
          (type-name (clj-nth (seq-first d) 1)) (clj= (meta (clj-nth (seq-first d) 1)) tag)))
  (list "PersistentVector" (clj-str (clj-vector nested nested))
        "PersistentVector" #t "PersistentArrayMap" #t))
(define (seq-first x) (clr-static-call clojure.lang.RT (first System.Object) x))
(define (clj-nth x i) (clr-static-call clojure.lang.RT (nth System.Object System.Int32) x i))
(test "strip pushes through wrapped children and rebuilds with metadata"
  ;; 元素是语法对象（wrap 在子项上）：syntax->datum 必须走 children / rebuild
  (let* ((v (with-meta (clj-vector (datum->syntax here 'a) (datum->syntax here (clj-list 1 2))) tag))
         (d (syntax->datum v)))
    (list (type-name d) (clj-str d) (clj= (meta d) tag)))
  (list "PersistentVector" "[a (1 2)]" #t))
(test "map children are k v in seq order"
  (syntax-extension-kind (syntax-extension-of (clj-map k 1 j 2)))
  'clj-map)
(test "map round trip through a syntax-case map pattern"
  (run `(syntax-case (datum->syntax #'here ',(clj-map k 1 j 2)) ()
          (,(clj-map k 'a j 'b) (list (syntax->datum #'a) (syntax->datum #'b)))))
  '(1 2))

;;; syntax-case：Clojure 向量模式 [a b ...]
(test "vector pattern [a b ...] matches a Clojure vector"
  (run `(syntax-case (datum->syntax #'here ',(clj-vector 1 2 3)) ()
          (,(clj-vector 'a 'b '...) (syntax->datum #'(a (b ...))))
          (_ 'no)))
  '(1 (2 3)))
(test "vector pattern does not match a Scheme vector, and #(…) does not match a Clojure vector"
  (run `(list (syntax-case (datum->syntax #'here '#(1 2)) () (,(clj-vector 'a 'b) 'clj) (#(a b) 'scheme))
              (syntax-case (datum->syntax #'here ',(clj-vector 1 2)) () (#(a b) 'scheme) (,(clj-vector 'a 'b) 'clj))))
  '(scheme clj))
(test "Clojure list pattern with a literal and nested vector"
  (run `(syntax-case (datum->syntax #'here ',(clj-list 'let (clj-vector 'x 1) 'x)) (let)
          (,(clj-list 'let (clj-vector 'n 'v) 'body) (syntax->datum #'(n v body)))))
  '(x 1 x))
(test "too few elements: no match"
  (run `(syntax-case (datum->syntax #'here ',(clj-vector 1)) () (,(clj-vector 'a 'b 'c '...) 'yes) (_ 'no)))
  'no)

;;; 模板：重建 Clojure 向量 / map，替换部分，保留模板上的元数据
(test "template rebuilds a Clojure vector with substitutions and ellipsis"
  (let ((d (run `(syntax-case (datum->syntax #'here '(1 2 3)) ()
                   ((a b ...) (syntax->datum (syntax ,(with-meta (clj-vector 'b '... 'a) tag))))))))
    (list (type-name d) (clj-str d) (clj= (meta d) tag)))
  (list "PersistentVector" "[2 3 1]" #t))
(test "template rebuilds a Clojure map with substituted keys and values"
  (let ((d (run `(syntax-case (datum->syntax #'here '(1 2)) ()
                   ((a b) (syntax->datum (syntax ,(with-meta (clj-map k 'a j (clj-vector 'b 'b)) tag))))))))
    (list (type-name d) (clj-str d) (clj= (meta d) tag)))
  (list "PersistentArrayMap" "{:k 1, :j [2 2]}" #t))
(test "constant template keeps the Clojure datum"
  (clj-str (run `(syntax->datum (syntax ,(clj-set 1)))))
  "#{1}")
(test "quasisyntax rebuilds a Clojure vector around unsyntax"
  (clj-str (run `(syntax->datum (quasisyntax ,(clj-vector 1 '(unsyntax (+ 1 1)) 3)))))
  "[1 2 3]")
(test "quasiquote rebuilds a Clojure vector around unquote"
  (clj-str (run `(let ((x 2)) ,(list 'quasiquote (clj-vector 1 '(unquote x) '(unquote-splicing (list 3 4)))))))
  "[1 2 3 4]")
(test "quasiquote without unquote keeps a constant"
  (clj-str (run (list 'quasiquote (clj-map k 1))))
  "{:k 1}")

;;; add-mark 穿过 Clojure map：宏输出（未包装的 map，子项是语法对象）里引入的标识符带上标记（卫生）
(test "hygiene through a Clojure map in macro output"
  (run `(let-syntax ((get-kj (lambda (x)
                               (syntax-case x ()
                                 ((_ ,(clj-map k 'a j 'b)) #'(list a b))))))
          (let-syntax ((mk (lambda (x)
                             (syntax-case x ()
                               ((_ e) (list #'let (list (list #'tmp #''inner))
                                            ;; 未包装的 map，子项是语法对象：add-mark 经 children / rebuild 穿过它
                                            (list #'get-kj (clj-map ',k #'tmp ',j #'e))))))))
            (let ((tmp 'outer))
              (mk tmp)))))
  '(inner outer))
(test "raw symbol inside a Clojure vector in macro output is an error"
  (run `(let-syntax ((bad (lambda (x) (list #'quote (clj-vector 'raw)))))
          (bad)))
  '(exception "raw symbol encountered in output of macro"))

;;; 源位置：元数据 :line / :column / :file → 注解
(define pos-meta (clj-map (clj-keyword "line") 3 (clj-keyword "column") 5 (clj-keyword "file") "t.clj"))
(test "clojure-source-of reads :line/:column/:file"
  (clojure-source-of (with-meta (clj-vector 1) pos-meta))
  '("t.clj" . "(3,5) - (3,5)"))
(test "annotated Clojure datum strips back to the original"
  (let* ((v (with-meta (clj-vector 1 (with-meta (clj-list 2) pos-meta)) pos-meta))
         (d (syntax->datum (clojure-datum->syntax here v))))
    (list (eq? d v) (clj-str d)))
  '(#t "[1 (2)]"))
(test "annotated Clojure datum matches patterns"
  (run `(syntax-case (clojure-datum->syntax #'here ',(with-meta (clj-vector 1 (with-meta (clj-vector 2) pos-meta)) pos-meta)) ()
          (,(clj-vector 'a (clj-vector 'b)) (syntax->datum #'(a b)))
          (_ 'other)))
  '(1 2))
(define (condition-source c)
  (let loop ((cs (simple-conditions c)))
    (cond
      ((null? cs) #f)
      ((eq? (record-type-name (record-rtd (car cs))) '&source-information)
       (let ((rtd (record-rtd (car cs))))
         (cons ((record-accessor rtd 0) (car cs)) ((record-accessor rtd 1) (car cs)))))
      (else (loop (cdr cs))))))
(test "syntax errors on an annotated Clojure form carry its position"
  (guard (c (#t (condition-source c)))
    (syntax-violation #f "bad form" (clojure-datum->syntax here (with-meta (clj-vector 1) pos-meta))))
  '("t.clj" . "(3,5) - (3,5)"))

;;; 绑定扩展：chi-expr / chi-set! 对未知种类查表
(register-binding-extension!
  (make-binding-extension 'test-const
    (lambda (e value expand) (list 'quote value))
    (lambda (e value expand)
      (syntax-case e () ((_ id v) (list 'quote (list 'set value (syntax->datum #'v))))))
    (lambda (e value expand)
      (syntax-case e () ((_ arg ...) (list 'quote (list 'form value (length #'(arg ...)))))))
    #f))
(test "binding extension: reference via expand-reference"
  (run '(let-syntax ((seven (cons 'test-const 7))) (list seven (+ seven 1))))
  '(7 8))
(test "binding extension: form via expand-form"
  (run '(let-syntax ((seven (cons 'test-const 7))) (seven 1 2)))
  '(form 7 2))
(test "binding extension: set! via expand-set!"
  (run '(let-syntax ((seven (cons 'test-const 7))) (set! seven 9)))
  '(set 7 9))
(test "binding extension: expand callback expands subexpressions"
  (begin
    (register-binding-extension!
      (make-binding-extension 'test-twice
        (lambda (e value expand) (list 'quote value))
        #f
        (lambda (e value expand)
          (syntax-case e () ((_ x) (let ((c (expand #'x))) (list (list 'primitive 'list) c c)))))
        #t))
    (run '(let-syntax ((twice (cons 'test-twice #f))) (let ((n 3)) (twice (+ n 1))))))
  '(4 4))
(test "unregistered kind is still an invalid transformer"
  (run '(let-syntax ((x (cons 'no-such-kind 1))) x))
  '(exception "invalid transformer"))
(test "binding-extension-of"
  (map (lambda (k) (and (binding-extension-of k) #t)) '(test-const test-twice no-such-kind))
  '(#t #t #f))

;;; 错误钩子
(test "syntax-error-hook sees who, message, form and condition and may raise its own"
  (guard (e ((and (pair? e) (eq? (car e) 'hooked)) (cdr e)))
    (parameterize ((syntax-error-hook
                    (lambda (who msg form c)
                      (raise (list 'hooked who msg (syntax->datum form) (syntax-violation? c))))))
      (run '(lambda))))
  '(lambda "invalid syntax" (lambda) #t))
(test "a hook that returns normally lets the condition be raised"
  (let ((seen #f))
    (list
      (guard (c ((syntax-violation? c) (condition-message c)))
        (parameterize ((syntax-error-hook (lambda (who msg form c) (set! seen msg))))
          (run '(if))))
      seen))
  '("invalid syntax" "invalid syntax"))
(test "syntax-violation called by user code goes through the hook"
  (guard (e ((and (pair? e) (eq? (car e) 'hooked)) (cdr e)))
    (parameterize ((syntax-error-hook (lambda (who msg form c) (raise (list 'hooked who msg)))))
      (syntax-violation 'me "custom" #'here)))
  '(me "custom"))
(test "hook is off by default"
  (syntax-error-hook)
  #f)

;;; Scheme 数据不受影响（零扩展的行为）
(test "Scheme vector patterns and templates"
  (run '(syntax-case #'#(1 2 3) () (#(a b ...) (syntax->datum #'#(b ... a)))))
  '#(2 3 1))
(test "Scheme quasiquote of a vector"
  (run '(let ((x 2)) `#(1 ,x ,@(list 3 4))))
  '#(1 2 3 4))

(display checks) (display " checks, ") (display failures) (display " failures") (newline)
(unless (= failures 0) (exit 1))
