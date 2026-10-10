;;; iron-023 验收：boot 源文件（文本种子的全部形式）通过前中端 pass；打印一个小样本的 L2。
(import (rnrs) (ironscheme) (nanopass)
        (ironscheme compiler languages) (ironscheme compiler parse) (ironscheme compiler front))

(define (front x)
  (let* ((l0 (parse-core x))
         (an (analyze l0))
         (l1 (cp0-lite (check-forward (simplify-letrec l0 an) an))))
    (convert-assignments l1 an)))

(define forms
  (call-with-input-file "boot/boot-seed.core"
    (lambda (p)
      (let loop ((acc '()))
        (let ((x (read p)))
          (if (eof-object? x) (reverse acc) (loop (cons x acc))))))))

(define n 0)
(for-each (lambda (f) (front f) (set! n (+ n 1))) forms)
(display "front passes: ") (display n) (display " of ") (display (length forms)) (display " seed forms") (newline)

(pretty-print
  (unparse-L2
    (front
      '(case-lambda
         ((a b)
          (letrec* ((f (case-lambda ((x) (g x))))
                    (g (case-lambda ((y) (if ($fx<? y '3) (f ($fx+ y '1)) count))))
                    (count ($fx+ '1 '2))
                    (h (case-lambda (() (set! a (cons a b)) a))))
            ((case-lambda ((t) (begin '1 (f t)))) (h))))))))
