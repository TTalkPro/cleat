#| License
Copyright (c) 2007-2016 Llewellyn Pritchard
All rights reserved.
This source code is subject to terms and conditions of the BSD License.
See docs/license.txt. |#

(library (boot records procedural)
  (export
    make-record-type-descriptor
    record-type-descriptor?
    make-record-constructor-descriptor
    record-constructor-descriptor?
    record-constructor
    record-predicate
    record-accessor
    record-mutator)
  (import 
    (except (ironscheme) record-type-descriptor? record-constructor-descriptor?)
    (ironscheme contracts)
    (ironscheme unsafe))
    
  
  (define (record-type-descriptor? obj)
    ($rtd? obj))
    
  (define (record-constructor-descriptor? obj)
    ($rcd? obj)))
