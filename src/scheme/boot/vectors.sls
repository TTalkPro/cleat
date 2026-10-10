#| License
Copyright (c) 2007-2016 Llewellyn Pritchard
All rights reserved.
This source code is subject to terms and conditions of the BSD License.
See docs/license.txt. |#

(library (boot vectors)
  (export)
  (import 
    (rnrs)
    (only (ironscheme) void export)
    (ironscheme unsafe)
    (ironscheme typed)
    (ironscheme syntax utils))

  (define-syntax define-vector-type
    (lambda (x)
      (syntax-case x ()
        [(_ prefix type)
          (with-syntax [(vector? 
                          (syntax-format "~avector?" #'type #'prefix))
                        (vector-ref
                          (syntax-format "~avector-ref" #'type #'prefix))
                        (vector-set!
                          (syntax-format "~avector-set!" #'type #'prefix))
                        (make-vector
                          (syntax-format "make-~avector" #'type #'prefix))
                        (vector-length
                          (syntax-format "~avector-length" #'type #'prefix))
                        (vector->list
                          (syntax-format "~avector->list" #'type #'prefix))
                        (vector-fill!
                          (syntax-format "~avector-fill!" #'type #'prefix))
                        (list->vector
                          (syntax-format "list->~avector" #'type #'prefix))
                        (type[]
                          (syntax-format "~a[]" #'type #'type))]
            #'(begin 
                (define: (vector? obj -> bool)
                  ($typed-vector? 'type obj))

                (define: (vector-ref (x : type[]) (n : int) -> type)
                  (when ($fx>? 0 n)
                    (assertion-violation 'vector-ref "negative index" n))
                  ($vector-ref x n))

                (define: (vector-set! (x : type[]) (n : int) (value : type))
                  (when ($fx>? 0 n)
                    (assertion-violation 'vector-set! "negative index" n))
                  ($vector-set! x n value)
                  (void))

                (define: (make-vector (k : int) -> type[])
                  (when ($fx>? 0 k)
                    (assertion-violation 'make-vector "cannot be negative" k))
                  ($make-typed-vector 'type k))

                (define: (vector-length (vec : type[]) -> int)
                  ($vector-length vec))

                (define: (vector->list (vec : type[]) -> list)
                  ($vector->list vec))

                (define: (list->vector (lst : list) -> type[])
                  ($list->typed-vector 'type lst))

                (define: (vector-fill! (vec : type[]) (val : type))
                  (let ((len (vector-length vec)))
                    (do ((i 0 ($fx+ i 1)))
                        (($fx=? i len))
                      ($vector-set! vec i val))))

                (export vector? vector-ref vector-set! make-vector vector-length vector->list vector-fill! list->vector)))])))

  (define-vector-type fl Double)
  (define-vector-type fx Int32))
