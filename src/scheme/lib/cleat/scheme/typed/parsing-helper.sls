#| License
Copyright (c) 2007-2016 Llewellyn Pritchard
All rights reserved.
This source code is subject to terms and conditions of the BSD License.
See docs/license.txt. |#

(library (cleat scheme typed parsing-helper)
  (export
    ->
    parse-type)
  (import 
    (cleat scheme))

  (define-syntax ->
    (lambda (x)
      (syntax-violation '-> "invalid usage of auxilliary keyword" x)))    
    
  (define (parse-type x)
    (syntax-case x (->)
      [(a ... -> r) ; typed procedure type
        (with-syntax (((a ...) (map parse-type #'(a ...)))
                      (r (parse-type #'r)))
          #'Cleat.Scheme.Procedure)]
      [(t a ...) ; generic type
        (identifier? #'t) 
        (with-syntax (((a ...) (map parse-type #'(a ...))))
          #'(t a ...))]
      [t 
        (identifier? #'t)
        #'t])))    
