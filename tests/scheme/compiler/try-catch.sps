;;; cleat-047 / cleat-058：核心形式 $try/catch 与 guard 的端到端测试（在镜像上运行：展开 → 编译 → 发射到内存 → 运行），
;;; 以及"try 区域内不发 tail."的 Lcil 检查。异常模型见 designs/15 §3。
(import (rnrs) (rnrs eval) (cleat scheme) (cleat scheme compiler) (cleat scheme unsafe))

(define env (environment '(rnrs) '(cleat scheme) '(cleat scheme unsafe) '(cleat scheme clr)))
(define (run expr) ((compile-core (expand->core expr env))))
(define failures 0)
(define (fail! name expected got)
  (set! failures (+ failures 1))
  (display "FAIL: ") (display name) (newline)
  (display "  expected: ") (write expected) (newline)
  (display "  got:      ") (write got) (newline))
;; 外框不装 Scheme 处理器（cleat-058：raise 先交给动态最近的处理器，$try/catch '#f 只接住走到缺省处理器的 raise）：
;; 在 CLR 一级接住 SchemeException 与其他异常；运行器（eval-top-level）装的处理器在用例期间清空
(define (without-handlers thunk)
  (let ((old ($current-handlers)))
    ($set-current-handlers! '())
    ($try/finally (thunk) ($set-current-handlers! old))))
(define (check name expr expected)
  (let ((got (without-handlers
               (lambda ()
                 (with-clr-exception-handler
                   (lambda (e) (list 'clr-exception e))
                   (lambda ()
                     ($try/catch (run expr) '#f
                       (lambda (e) (list 'exception (if (message-condition? e) (condition-message e) e))))))))))
    (unless (equal? got expected) (fail! name expected got))))

;; $try/catch 本身
(check "catch SchemeException: handler gets the condition"
  '($try/catch (begin (raise 'oops) 'not-reached) '#f (lambda (c) (cons 'caught c)))
  '(caught . oops))
(check "normal return: handler not called"
  '($try/catch 42 '#f (lambda (c) 'wrong))
  42)
(check "catch a CLR exception type: handler gets the exception object"
  '($try/catch (clr-static-call System.Int32 Parse "zz") '"System.FormatException" (lambda (e) (list 'format-error (clr-is System.Exception e))))
  '(format-error #t))
(check "clauses in order: first matching type wins"
  '($try/catch (clr-static-call System.Int32 Parse "zz") '#f (lambda (c) 'a) '"System.Exception" (lambda (e) 'b))
  'b)
(check "non-matching type passes through to the outer try"
  '($try/catch ($try/catch (raise 'inner) '"System.InvalidOperationException" (lambda (e) 'wrong)) '#f (lambda (c) (cons 'outer c)))
  '(outer . inner))
(check "unmatched CLR exception escapes a '#f clause"
  '(with-clr-exception-handler (lambda (e) 'clr-escaped) (lambda () ($try/catch (clr-static-call System.Int32 Parse "zz") '#f (lambda (c) 'wrong))))
  'clr-escaped)
(check "finally runs before the outer catch handler"
  '(let ((log '())) (list ($try/catch ($try/finally (begin (raise 'x) 1) (set! log (cons 'fin log))) '#f (lambda (c) (set! log (cons 'catch log)) 2)) log))
  '(2 (catch fin)))
(check "handlers are evaluated before the body"
  '(let ((log '())) ($try/catch (begin (set! log (cons 'body log)) log) '#f (begin (set! log (cons 'handler log)) (lambda (c) c))))
  '(body handler))
(check "multiple values pass through the try as one value"
  '(call-with-values (lambda () ($try/catch (values 1 2) '#f (lambda (c) 'wrong))) list)
  '(1 2))
;; cleat-058：外层有 guard（处理器）时 raise 直接交给它；$try/catch '#f 不是处理器，只接住走到缺省处理器的 raise
(check "raise inside an outer guard goes to the guard's handler, not to $try/catch"
  '(guard (e (#t (list 'outer e))) ($try/catch (raise 'a) '#f (lambda (c) (raise (list 'reraised c)))))
  '(outer a))
(check "raise from a $try/catch handler (no Scheme handler installed) reaches the outer $try/catch"
  '($try/catch ($try/catch (raise 'a) '#f (lambda (c) (raise (list 'reraised c)))) '#f (lambda (c) (list 'outer c)))
  '(outer (reraised a)))
(check "try/catch in effect and test contexts"
  '(let ((n 0)) ($try/catch (raise 'x) '#f (lambda (c) (set! n 1))) (if ($try/catch #f '#f (lambda (c) #t)) 'wrong (list n 'ok)))
  '(1 ok))

;; guard（展开到 $try/catch）
(check "guard: no clause matches re-raises to the outer guard"
  '(guard (e ((symbol? e) (list 'outer e))) (guard (e ((string? e) 'wrong)) (raise 'inner)))
  '(outer inner))
(check "guard: else clause"
  '(guard (e ((string? e) 'wrong) (else (list 'else e))) (raise 42))
  '(else 42))
(check "guard: => clause and test-only clause"
  '(list (guard (e ((assq 'a e) => cdr)) (raise (list (cons 'a 42))))
         (guard (e ((memq 'k e))) (raise '(j k l))))
  '(42 (k l)))
(check "guard: nanopass style (guard (c [else #f]) …) on error"
  '(guard (c [else #f]) (error 'who "nanopass style"))
  #f)
(check "guard: condition from a primitive"
  '(guard (e ((assertion-violation? e) (condition-message e))) (car 5))
  "not a pair")
(check "guard: raise from the handler"
  '(guard (e (#t (list 'second e))) (guard (e (#t (raise (list 'from-handler e)))) (raise 'first)))
  '(second (from-handler first)))
(check "guard: body returns multiple values"
  '(call-with-values (lambda () (guard (e (#t 'no)) (values 1 2 3))) list)
  '(1 2 3))
(check "guard: loop inside the try region (no tail. there, still a loop)"
  '(guard (e (#t 'unexpected)) (let loop ((i 0)) (if (= i 200000) 'done (loop (+ i 1)))))
  'done)
(check "guard: finally runs before the clause body (dynamic-wind removed, cleat-058)"
  '(let ((log '())) (guard (e (#t (set! log (cons 'handler log)))) (set! log (cons 'before log)) (try (raise 'x) (finally (set! log (cons 'after log))))) log)
  '(handler after before))

;; Lcil：try 区域内不发 tail.；区域外照发
(define (lcil-text expr) (compile->lcil-text (list (expand->core expr env)) "tc" "#Module"))
(define (contains? s sub)
  (let ((n (string-length s)) (m (string-length sub)))
    (let loop ((i 0))
      (cond ((> (+ i m) n) #f)
            ((string=? (substring s i (+ i m)) sub) #t)
            (else (loop (+ i 1)))))))
(define (index-of s sub)
  (let ((n (string-length s)) (m (string-length sub)))
    (let loop ((i 0))
      (cond ((> (+ i m) n) #f)
            ((string=? (substring s i (+ i m)) sub) i)
            (else (loop (+ i 1)))))))
;; (try …) 区域的文本：从 "(try" 到与之配对的右括号（字符串里没有括号，可直接数）
(define (try-region s)
  (let ((start (index-of s "(try")))
    (and start
         (let loop ((i start) (depth 0))
           (let* ((c (string-ref s i))
                  (depth (cond ((char=? c #\() (+ depth 1)) ((char=? c #\)) (- depth 1)) (else depth))))
             (if (and (char=? c #\)) (= depth 0))
                 (substring s start (+ i 1))
                 (loop (+ i 1) depth)))))))
(let* ((t (lcil-text '(lambda (th h) ($try/catch (th) '#f h '"System.InvalidOperationException" h))))
       (region (try-region t)))
  (unless (and region (contains? region "(catch \"Cleat.Scheme.SchemeException\"")
               (contains? region "(catch \"System.InvalidOperationException\"") (contains? region "get_Condition")
               (not (contains? region "(tail)")))
    (fail! "lcil: try/catch shape and no tail. inside the try region" "try with two catches, no (tail)" t)))
(let ((t (lcil-text '(lambda (th) (th)))))
  (unless (contains? t "(tail)")
    (fail! "lcil: control — tail. outside any try" "(tail)" t)))

(display "try-catch: ")
(display (- 23 failures))
(display " of 23 passed")
(newline)
(unless (= failures 0) (exit 1))
