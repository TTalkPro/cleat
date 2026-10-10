;;; iron-025 验收：每个内联模板（以及几个静态调用的原语）与 C# 原语实现对照。
;;; 对每组实参分别在值上下文、谓词上下文（if 的测试）编译，实参经 lambda 参数传入以免被常量折叠。
(import (rnrs) (ironscheme) (ironscheme compiler))

(define cases
  ;; (原语 实参组 …)
  `((void ())
    ($car ((1 . 2))) ($cdr ((1 . 2)))
    (cons (1 2) (a ()))
    (eq? (a a) (a b) (() ()) (#f #f))
    (not (#f) (#t) (0) (()))
    (null? (()) (1) ((1)))
    (pair? ((1)) (()) (#(1)))
    ($fx+ (1 2) (2147483647 1) (-5 3))
    ($fx* (6 7) (65536 65536))
    ($fx- (5) (5 7) (-2147483648))
    ($fxand (12 10)) ($fxior (12 10)) ($fxxor (12 10)) ($fxnot (0) (5))
    ($fx=? (1 1) (1 2)) ($fx<? (1 2) (2 1) (2 2)) ($fx>? (1 2) (2 1))
    ($fx<=? (1 2) (2 2) (3 2)) ($fx>=? (1 2) (2 2) (3 2))
    ($fxzero? (0) (1)) ($fxnegative? (-1) (0)) ($fxpositive? (1) (0))
    ($fl+ (1.5 2.25)) ($fl* (1.5 2.0)) ($fl/ (1.0 4.0) (1.0 0.0)) ($fl- (1.5) (1.5 0.25))
    ($fl=? (1.0 1.0) (+nan.0 +nan.0) (0.0 -0.0)) ($fl<? (1.0 2.0) (+nan.0 1.0))
    ($fl>? (2.0 1.0) (+nan.0 1.0)) ($fl<=? (1.0 1.0) (+nan.0 1.0) (2.0 1.0)) ($fl>=? (1.0 1.0) (+nan.0 1.0) (0.0 1.0))
    ;; 静态调用的原语
    (car ((1 . 2))) (cdr ((1 . 2))) (eqv? (1.5 1.5) (a b)) (vector (1 2 3) ()) (list* (1 2 (3))) ($vector-ref (#(a b c) 1))))

(define (args->lcore args) (map (lambda (a) `(quote ,a)) args))

;; ((case-lambda ((a0 a1 …) (prim a0 a1 …))) 'v0 'v1 …)
(define (compile-call pr args test?)
  (let* ((vars (let loop ((i 0) (acc '())) (if (= i (length args)) (reverse acc) (loop (+ i 1) (cons (string->symbol (string-append "a" (number->string i))) acc)))))
         (call `(,pr ,@vars))
         (body (if test? `(if ,call 'yes 'no) call)))
    ((compile-core `((case-lambda (,vars ,body)) ,@(args->lcore args))))))

(define (same? a b)
  (or (equal? a b)
      (and (flonum? a) (flonum? b) (nan? a) (nan? b))))

(define failures 0)
(define checks 0)
(for-each
  (lambda (c)
    (let ((pr (car c)))
      (for-each
        (lambda (args)
          (let* ((expected (apply (symbol-value pr) args))
                 (got (compile-call pr args #f))
                 (got-test (compile-call pr args #t))
                 (expected-test (if expected 'yes 'no)))
            (set! checks (+ checks 2))
            (unless (same? got expected)
              (set! failures (+ failures 1))
              (display "FAIL value: ") (write (cons pr args)) (display " => ") (write got) (display ", primitive: ") (write expected) (newline))
            (unless (eq? got-test expected-test)
              (set! failures (+ failures 1))
              (display "FAIL test: ") (write (cons pr args)) (display " => ") (write got-test) (newline))))
        (cdr c))))
  cases)

(display "inline-templates: ") (display (- checks failures)) (display " of ") (display checks) (display " checks passed") (newline)
(unless (= failures 0) (exit 1))
