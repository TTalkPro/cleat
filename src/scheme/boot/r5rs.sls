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
    (ironscheme contracts)
    (ironscheme records printer)
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
 
  (define/contract (quotient n1:integer n2:integer)
    (* (sign n1) (sign n2) (div (abs n1) (abs n2))))
  
  (define/contract (remainder n1:integer n2:integer)
    (* (sign n1) (mod (abs n1) (abs n2))))
  
  (define/contract (modulo n1:integer n2:integer)
    (let ((s (sign n2)))
      (* s (mod (* s n1) (abs n2)))))
    
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