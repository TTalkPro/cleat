#| License
Copyright (c) 2007-2016 Llewellyn Pritchard
All rights reserved.
This source code is subject to terms and conditions of the BSD License.
See docs/license.txt. |#

(library (boot mutable-pairs)
  (export
    set-car!
    set-cdr!)
  (import 
    (rnrs)
    (ironscheme contracts)
    (ironscheme unsafe))

    
  (define/contract (set-car! lst:pair val)
    ($set-car! lst val))      
    
  (define/contract (set-cdr! lst:pair val)
    ($set-cdr! lst val)))
