#| License
Copyright (c) 2007-2016 Llewellyn Pritchard
All rights reserved.
This source code is subject to terms and conditions of the BSD License.
See docs/license.txt. |#

(library (boot r5rs)
  (export
    exact->inexact
    inexact->exact
    
    quotient
    remainder
    modulo
    
    delay
    force
    make-promise
    make-lazy-promise
    make-eager-promise
    promise?
    
    null-environment
    scheme-report-environment)
  
  (import 
    (rnrs)
    (rnrs mutable-pairs)
    (cleat scheme contracts)
    (cleat scheme records printer)
    (only (cleat scheme unsafe) $fx=? $fx<? $fx+ $fxdiv $fxmod $bignum? $bignum-quotient $bignum-remainder)
    (only (rnrs r5rs) 
      delay    
      null-environment
      scheme-report-environment))
    
  (define inexact->exact exact)    
  (define exact->inexact inexact)
    
  (define (sign n)
    (cond 
      [(> n 0) 1]
      [(< n 0) -1]
      (else 0)))
 
  ;; quotient / remainder / modulo（iron-061，参照 Chez s/5_3.ss）：按类型分派，类型检查就是分派本身。
  ;; 两个 fixnum：截断除法原语（$fxdiv / $fxmod 即 C# 的 / 与 %）；除数为 -1 时单独处理（最小 fixnum 取反溢出）。
  ;; 两个精确整数（含 bignum）：BigInteger 一次除法。其余（如 7.0 这样的非精确整数）走原来的通用实现与契约检查。
  (define (exact-integer*? x) (or (fixnum? x) ($bignum? x)))

  (define (divide-by-zero who n1 n2)
    (assertion-violation who "divide by zero" n1 n2))

  (define/contract (generic-quotient n1:integer n2:integer)
    (* (sign n1) (sign n2) (div (abs n1) (abs n2))))

  ;; 按定义 n1 - n2 * q 整除时得 0.0，不是 -0.0（(remainder -4.0 2.0)；iron-072 对拍 Chez mats）
  (define (drop-negative-zero r) (if (eqv? r -0.0) 0.0 r))
  (define/contract (generic-remainder n1:integer n2:integer)
    (drop-negative-zero (* (sign n1) (mod (abs n1) (abs n2)))))

  (define/contract (generic-modulo n1:integer n2:integer)
    (let ((s (sign n2)))
      (drop-negative-zero (* s (mod (* s n1) (abs n2))))))

  (define (quotient n1 n2)
    (cond
      [(and (fixnum? n1) (fixnum? n2))
       (cond
         [($fx=? n2 0) (divide-by-zero 'quotient n1 n2)]
         [($fx=? n2 -1) (- n1)]
         [else ($fxdiv n1 n2)])]
      [(and (exact-integer*? n1) (exact-integer*? n2))
       (if (eqv? n2 0) (divide-by-zero 'quotient n1 n2) ($bignum-quotient n1 n2))]
      [else (generic-quotient n1 n2)]))

  (define (remainder n1 n2)
    (cond
      [(and (fixnum? n1) (fixnum? n2))
       (cond
         [($fx=? n2 0) (divide-by-zero 'remainder n1 n2)]
         [($fx=? n2 -1) 0]
         [else ($fxmod n1 n2)])]
      [(and (exact-integer*? n1) (exact-integer*? n2))
       (if (eqv? n2 0) (divide-by-zero 'remainder n1 n2) ($bignum-remainder n1 n2))]
      [else (generic-remainder n1 n2)]))

  ;; 余数不为 0 且与除数异号时加上除数（|r| < |n2|，fixnum 情形不会溢出）
  (define (modulo n1 n2)
    (cond
      [(and (fixnum? n1) (fixnum? n2))
       (cond
         [($fx=? n2 0) (divide-by-zero 'modulo n1 n2)]
         [($fx=? n2 -1) 0]
         [else
          (let ((r ($fxmod n1 n2)))
            (if (and (not ($fx=? r 0)) (not (eq? ($fx<? r 0) ($fx<? n2 0))))
                ($fx+ r n2)
                r))])]
      [(and (exact-integer*? n1) (exact-integer*? n2))
       (if (eqv? n2 0)
           (divide-by-zero 'modulo n1 n2)
           (let ((r ($bignum-remainder n1 n2)))
             (if (and (not (eqv? r 0)) (not (eq? (negative? r) (negative? n2))))
                 (+ r n2)
                 r)))]
      [else (generic-modulo n1 n2)]))
    
  ;; promise（iron-041，designs/08-std-library.md D7）：SRFI-45 / R7RS 的迭代强制，长的 lazy 链不增长栈。
  ;; box 是可变序对 (完成? . 值或 thunk)；未完成时 thunk 返回另一个 promise。强制时把它的 box 内容搬过来，
  ;; 并让它共享本 promise 的 box（R7RS 参考实现的 promise-update!），然后继续强制本 promise。
  (define-record-type (promise create-promise promise?)
    (fields (mutable box)))

  ;; (delay e) 展开为 (make-promise (lambda () e))：thunk 返回值
  (define (make-promise proc)
    (create-promise (cons #f (lambda () (make-eager-promise (proc))))))

  ;; SRFI-45 lazy / R7RS delay-force：thunk 返回一个 promise
  (define (make-lazy-promise thunk)
    (create-promise (cons #f thunk)))

  ;; SRFI-45 eager：已完成的 promise
  (define (make-eager-promise value)
    (create-promise (cons #t value)))

  (define (force promise)
    (let ((box (promise-box promise)))
      (if (car box)
          (cdr box)
          (let ((promise* ((cdr box))))
            ;; thunk 中可能已经强制了本 promise（重入）：那时以先完成的为准
            (unless (car (promise-box promise))
              (let ((box* (promise-box promise*)))
                (set-car! (promise-box promise) (car box*))
                (set-cdr! (promise-box promise) (cdr box*))
                (promise-box-set! promise* (promise-box promise))))
            (force promise)))))

  (add-record-printer! promise? 
    (lambda (x p wr)
      (display "#<promise>" p))))