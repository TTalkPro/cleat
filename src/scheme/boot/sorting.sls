#| License
Copyright (c) 2007-2016 Llewellyn Pritchard
All rights reserved.
This source code is subject to terms and conditions of the BSD License.
See docs/license.txt. |#

(library (boot sorting)
  (export
    list-sort
    vector-sort
    vector-sort!)
  (import 
    (except (rnrs) list-sort vector-sort vector-sort!)
    (rnrs mutable-pairs)
    (ironscheme unsafe)
    (only (ironscheme) with-clr-exception-handler void)
    (ironscheme contracts))
  
  (define/contract (list-sort less?:procedure seq:list)
    ;; "sort.scm" from SLIB
    ;; Author: Richard A. O'Keefe (based on Prolog code by D.H.D.Warren)
    ;; This code is in the public domain.
    (define (sort:merge! a b less?)
      (define (key x) x)
      (define (loop r a kcara b kcarb)
        (cond ((less? kcarb kcara)
               (set-cdr! r b)
               (if (null? (cdr b))
                   (set-cdr! b a)
                   (loop b a kcara (cdr b) (key (cadr b)))))
              (else                     ; (car a) <= (car b)
               (set-cdr! r a)
               (if (null? (cdr a))
                   (set-cdr! a b)
                   (loop a (cdr a) (key (cadr a)) b kcarb)))))
      (cond ((null? a) b)
            ((null? b) a)
            (else
             (let ((kcara (key (car a)))
                   (kcarb (key (car b))))
               (cond
                 ((less? kcarb kcara)
                  (if (null? (cdr b))
                      (set-cdr! b a)
                      (loop b a kcara (cdr b) (key (cadr b))))
                  b)
                 (else			; (car a) <= (car b)
                  (if (null? (cdr a))
                      (set-cdr! a b)
                      (loop a (cdr a) (key (cadr a)) b kcarb))
                  a))))))

    ;; takes two sorted lists a and b and smashes their cdr fields to form a
    ;; single sorted list including the elements of both.
    ;; Note:  this does _not_ accept arrays.
    (define (merge! a b less?)
      (sort:merge! a b less?))

    (define (sort:sort-list! seq less?)
      (define keyer (lambda (x) x))
      (define (step n)
        (cond ((> n 2) (let* ((j (div n 2))
                              (a (step j))
                              (k (- n j))
                              (b (step k)))
                         (merge! a b less?)))
              ((= n 2) (let ((x (car seq))
                             (y (cadr seq))
                             (p seq))
                         (set! seq (cddr seq))
                         (cond ((less? (keyer y) (keyer x))
                                (set-car! p y)
                                (set-car! (cdr p) x)))
                         (set-cdr! (cdr p) '())
                         p))
              ((= n 1) (let ((p seq))
                         (set! seq (cdr seq))
                         (set-cdr! p '())
                         p))
              (else '())))
      (step (length seq)))

    (define (list-sort! less? seq)
      (let ((ret (sort:sort-list! seq less?)))
        (if (not (eq? ret seq))
            (do ((crt ret (cdr crt)))
                ((eq? (cdr crt) seq)
                 (set-cdr! crt ret)
                 (let ((scar (car seq)) (scdr (cdr seq)))
                   (set-car! seq (car ret)) (set-cdr! seq (cdr ret))
                   (set-car! ret scar) (set-cdr! ret scdr)))))
        seq))

    (list-sort! less? (append seq '())))      
                       
  (define/contract (vector-sort! pred?:procedure vec:vector)
    ;; 比较过程不一致时宿主的排序抛出异常，转为 &assertion
    (with-clr-exception-handler
      (lambda (e) (assertion-violation 'vector-sort! ($exception-message e) pred? vec))
      (lambda ()
        ($vector-sort! pred? vec)))
    (void))

  (define/contract (vector-sort pred?:procedure vec:vector)
    (list->vector (list-sort pred? (vector->list vec)))))
