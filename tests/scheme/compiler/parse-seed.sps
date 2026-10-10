;;; iron-022 验收：文本种子中的全部核心形式都能通过 parse-core。
(import (rnrs) (ironscheme) (nanopass) (ironscheme compiler languages) (ironscheme compiler parse))

(define forms
  (call-with-input-file "boot/boot-seed.core"
    (lambda (p)
      (let loop ((acc '()))
        (let ((x (read p)))
          (if (eof-object? x) (reverse acc) (loop (cons x acc))))))))

(define n 0)
(for-each
  (lambda (f)
    (parse-core f)
    (set! n (+ n 1)))
  forms)
(display "parse-core: ")
(display n)
(display " of ")
(display (length forms))
(display " seed forms parsed")
(newline)

;; 小样本：打印 L0
(pretty-print
  (unparse-L0
    (parse-core
      '(library-letrec* (t)
         ((f g$f (case-lambda ((x) (if (pair? x) (car x) (g$other x 1))) ((x . r) r)))
          (u g$u (clr-using-internal 'System.Text)))
         (begin ($try/finally (f 1) (clr-call-internal 'StringBuilder 'ToString (clr-new-internal 'StringBuilder))) (set! f 2))))))
