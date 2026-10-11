;;; cleat-058：#nil（.NET null）与 '()（EmptyList.Instance）（移植自 skeg 的 host-smoke.sps，skeg 13 §7）
(import (rnrs) (cleat scheme) (cleat scheme clr))

(define failures 0)
(define checks 0)
(define-syntax check
  (syntax-rules ()
    ((_ expr expected)
     (let ((got (guard (e (#t (list 'exception (if (message-condition? e) (condition-message e) e)))) expr)))
       (set! checks (+ checks 1))
       (unless (equal? got expected)
         (set! failures (+ failures 1))
         (display "FAIL: ") (write 'expr) (display " => ") (write got) (display "  expected ") (write expected) (newline))))))

;; #nil、#f、'() 两两不同：#nil 与 #f 为假，'() 为真；nil? 只认 #nil，null? 只认 '()
(check (list (nil? #nil) (nil? 0) (nil? '()) (nil? #f)) '(#t #f #f #f))
(check (list (null? '()) (null? #nil) (null? #f)) '(#t #f #f))
(check (list (eq? #nil '()) (eq? #nil #f) (eq? #f '()) (equal? #nil '()) (eqv? #nil #f)) '(#f #f #f #f #f))
(check (list (if '() 'T 'F) (if #nil 'T 'F) (if #f 'T 'F) (not #nil) (not '())) '(T F F #t #f))
(check (list (list? (cons 1 #nil)) (list? (cons 1 '())) (cdr (cons 1 #nil))) '(#f #t #nil))
(check (nil? (clr-static-call System.Environment GetEnvironmentVariable "CLEAT_NO_SUCH_VARIABLE")) #t)
(check (let ((ht (make-eq-hashtable))) (hashtable-set! ht 'a #nil) (list (hashtable-ref ht 'a 'd) (hashtable-ref ht 'b 'd))) '(#nil d))
(check (call-with-string-output-port (lambda (p) (write (list #nil '() (cons 1 #nil)) p))) "(#nil () (1 . #nil))")
;; 表的构造与遍历以 '() 结尾
(check (list (length '(1 2 3)) (reverse '(1 2)) (append '(1) '(2) '()) (apply + '(1 2 3)) (vector->list (make-vector 2 0))) '(3 (2 1) (1 2) 6 (0 0)))
(check (let-values ((x (values))) x) '())
(check ((lambda args args)) '())
(check (map (lambda (x) x) '()) '())
(check (clr-is Cleat.Scheme.EmptyList '()) #t)
;; 编译后的代码里的常量与 null? / nil? 内联
(check (let ((f (lambda (x) (list (null? x) (nil? x))))) (list (f '()) (f #nil) (f 1))) '((#t #f) (#f #t) (#f #f)))

(display "nil-empty: ") (display (- checks failures)) (display " of ") (display checks) (display " passed") (newline)
(unless (= failures 0) (exit 1))
