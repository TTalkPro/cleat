;;; 用新编译器把文本种子的全部核心形式编译成一个 boot 镜像（Lcil 与 dll），输出统计（iron-024、iron-027）。
;;; 不加载执行（那是 Phase A，iron-028）。
(import (rnrs) (ironscheme) (ironscheme clr) (ironscheme compiler))

(define forms
  (call-with-input-file "boot/boot-seed.core"
    (lambda (p)
      (let loop ((acc '()))
        (let ((x (read p)))
          (if (eof-object? x) (reverse acc) (loop (cons x acc))))))))

(define out-dir
  (let ((d (string-append (clr-static-call System.IO.Path GetTempPath) "iron-compile-seed")))
    (clr-static-call System.IO.Directory CreateDirectory d)
    d))

(define (ticks) (clr-static-prop-get System.Environment TickCount))

(define t0 (ticks))
(define lcil (compile-forms->lcil forms "cleat.scheme.boot" "#Module"))
(define t1 (ticks))
(define text (compile->lcil-text-from-sexpr lcil))
(define lcil-path (string-append out-dir "/cleat.scheme.boot.lcil"))
(define dll-path (string-append out-dir "/cleat.scheme.boot.dll"))
(when (file-exists? lcil-path) (delete-file lcil-path))
(call-with-output-file lcil-path (lambda (p) (put-string p text)))
(define t2 (ticks))
(clr-static-call Cleat.Emit.LcilEmitter (Save String String String Boolean) text lcil-path dll-path #f)
(define t3 (ticks))

;; 统计：类型、已知过程 / 单例（静态）与闭包、方法、指令
(define types (filter (lambda (x) (and (pair? x) (eq? (car x) 'type))) (cddr lcil)))
(define (type-static? t) (exists (lambda (c) (and (pair? c) (eq? (car c) 'field) (equal? (cadr c) "Instance"))) t))
(define (methods t) (filter (lambda (c) (and (pair? c) (eq? (car c) 'method))) t))
(define (method-static? m) (exists (lambda (c) (and (pair? c) (eq? (car c) 'attrs) (memq 'static (cdr c)))) m))
(define (body-size m)
  (let ((b (find (lambda (c) (and (pair? c) (eq? (car c) 'body))) m)))
    (if b (length (cdr b)) 0)))
(define procs (filter (lambda (t) (not (equal? (cadr t) "#Module"))) types))
(define statics (filter type-static? procs))
(define all-methods (apply append (map methods types)))
(define clause-methods (filter (lambda (m) (let ((n (cadr m))) (and (> (string-length n) 1) (char=? (string-ref n 0) #\C) (char-numeric? (string-ref n 1))))) all-methods))

(display "compile-seed: ") (display (length forms)) (display " forms") (newline)
(display "  front end + codegen: ") (display (- t1 t0)) (display " ms; text: ") (display (- t2 t1))
(display " ms; emit: ") (display (- t3 t2)) (display " ms") (newline)
(display "  Lcil: ") (display (string-length text)) (display " chars; constants: ")
(display (let ((c (assq 'constants (cddr lcil)))) (if c (length (cdr c)) 0))) (newline)
(display "  procedure classes: ") (display (length procs))
(display " (static/singleton: ") (display (length statics))
(display ", closures: ") (display (- (length procs) (length statics))) (display ")") (newline)
(display "  clause methods: ") (display (length clause-methods))
(display " (static: ") (display (length (filter method-static? clause-methods))) (display ")") (newline)
(display "  instructions: ") (display (apply + (map body-size all-methods))) (newline)
(display "  -> ") (display dll-path) (newline)
