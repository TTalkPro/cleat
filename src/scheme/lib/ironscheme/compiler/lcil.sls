#| License
Copyright (c) 2026 the IronScheme contributors
All rights reserved.
This source code is subject to terms and conditions of the BSD License.
See docs/license.txt. |#

;;; pass 15 emit-lcil：Lcil 的 S 表达式 → 文本（designs/04-lcil-and-emitter.md §2）。
;;; lcil / constants / type / method / body / try / catch / finally 的子项各占一行，其余写在一行，便于 diff。
(library (ironscheme compiler lcil)
  (export write-lcil lcil->string)
  (import (rnrs) (ironscheme compiler constants))

  (define block-heads '(lcil constants type method body try catch finally))

  (define (write-lcil x port)
    (let loop ((x x) (indent 0))
      (if (and (pair? x) (memq (car x) block-heads))
          (let ((inner (+ indent 2)))
            (put-char port #\()
            (write-lcil-datum (car x) port)
            ;; 头部的原子（名字、版本号等）写在同一行
            (let head ((xs (cdr x)))
              (cond
                ((null? xs) (put-char port #\)))
                ((and (not (pair? (car xs))) (not (null? (car xs))))
                 (put-char port #\space)
                 (write-lcil-datum (car xs) port)
                 (head (cdr xs)))
                (else
                 (for-each
                   (lambda (c)
                     (put-char port #\newline)
                     (do ((i 0 (+ i 1))) ((= i inner)) (put-char port #\space))   ; 不用 make-string：它分配可变字符串，eval 时是热点（iron-033）
                     (loop c inner))
                   xs)
                 (put-char port #\))))))
          (write-lcil-datum x port))))

  (define (lcil->string x)
    (call-with-string-output-port
      (lambda (p)
        (write-lcil x p)
        (newline p)))))
