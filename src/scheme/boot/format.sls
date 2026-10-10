#| License
Copyright (c) 2007-2016 Llewellyn Pritchard
All rights reserved.
This source code is subject to terms and conditions of the BSD License.
See docs/license.txt. |#

(library (boot format)
  (export printf
          fprintf
          format)
  (import 
    (except (ironscheme) printf fprintf format)
    (ironscheme unsafe)
    (ironscheme contracts))
  
  ;; dofmt does all of the work.  It loops through the control string
  ;; recognizing format directives and printing all other characters
  ;; without interpretation.  A tilde at the end of a control string is
  ;; treated as an ordinary character.  Directives may be given in either case.
  ;;
  ;; 指令（iron-054 起）：~a display、~s write、~d ~x ~o ~b 按进制 display 数、~% 与 ~n 换行、~~ 波浪号；
  ;; 参数指针：~n* 跳过 n 个（缺省 1），~n:* 退回 n 个（缺省 1），~n@* 跳到第 n 个（缺省 0，SRFI 29 的位置参数）。
  ;; 原来 ~d 等用 write 输出数的字符串（结果带引号），未知指令原样输出；现在未知指令与参数不足都报错。
  (define dofmt
    (lambda (p cntl args)
      (define (fail msg . irritants)
        (apply assertion-violation 'format msg cntl irritants))
      (define (next a)
        (when (null? a) (fail "too few arguments"))
        (car a))
      (define (radix a r)
        (let ((x (next a)))
          (unless (number? x) (fail "not a number" x))
          (display (number->string x r) p)))
      (let ((len (string-length cntl))
            (nargs (length args)))
        (let loop ((n 0) (a args))
          (when ($fx<? n len)
            (let ((c (string-ref cntl n)))
              (if (and (char=? c #\~) ($fx<? ($fx+ n 1) len))
                  ;; 可选的数字前缀与 : / @ 修饰
                  (let digits ((i ($fx+ n 1)) (num #f))
                    (let ((d (and ($fx<? i len) (string-ref cntl i))))
                      (if (and d (char<=? #\0 d #\9))
                          (digits ($fx+ i 1) (+ (* 10 (or num 0)) (- (char->integer d) 48)))
                          (let* ((colon? (and d (char=? d #\:)))
                                 (at? (and d (char=? d #\@)))
                                 (i (if (or colon? at?) ($fx+ i 1) i))
                                 (d (and ($fx<? i len) (string-ref cntl i)))
                                 (rest ($fx+ i 1)))
                            (unless d (fail "incomplete directive"))
                            (case (char-downcase d)
                              ((#\a) (display (next a) p) (loop rest (cdr a)))
                              ((#\s) (write (next a) p) (loop rest (cdr a)))
                              ((#\d) (radix a 10) (loop rest (cdr a)))
                              ((#\x) (radix a 16) (loop rest (cdr a)))
                              ((#\o) (radix a 8) (loop rest (cdr a)))
                              ((#\b) (radix a 2) (loop rest (cdr a)))
                              ((#\% #\n) (newline p) (loop rest a))
                              ((#\~) (write-char #\~ p) (loop rest a))
                              ((#\*)
                               (let ((pos (cond
                                            (at? (or num 0))
                                            (colon? (- (- nargs (length a)) (or num 1)))
                                            (else (+ (- nargs (length a)) (or num 1))))))
                                 (unless (<= 0 pos nargs) (fail "argument index out of range" pos))
                                 (loop rest (list-tail args pos))))
                              (else (fail "unknown directive" (substring cntl n rest))))))))
                  (begin
                    (write-char c p)
                    (loop ($fx+ n 1) a)))))))))

  ;; printf and fprintf differ only in that fprintf passes its
  ;; port argument to dofmt while printf passes the current output
  ;; port.
  (define/contract (printf control:string . args)
      (dofmt (current-output-port) control args))

  (define/contract (fprintf p:textual-output-port control:string . args)
      (dofmt p control args))
      
  (define/contract (format control:string . args)
    (call-with-values open-string-output-port
      (lambda (p c)
        (dofmt p control args)
        (c))))) 
    