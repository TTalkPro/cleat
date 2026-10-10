#| License
Copyright (c) 2007-2016 Llewellyn Pritchard
All rights reserved.
This source code is subject to terms and conditions of the BSD License.
See docs/license.txt. |#

(library (boot records inspection)
  (export
    record?
    record-rtd
    record-type-name
    record-type-parent
    record-type-uid
    record-type-generative?
    record-type-sealed?
    record-type-opaque?
    record-type-field-names
    record-field-mutable?)
  (import (except (ironscheme)
            record-type-name
            record-type-parent
            record-type-uid
            record-type-generative?
            record-type-sealed?
            record-type-opaque?
            record-type-field-names
            record-field-mutable?)
          (ironscheme contracts)
          (ironscheme unsafe))
          
          
  (define/contract (record-type-name rtd:record-type-descriptor)
    ($rtd-name rtd))
    
  (define/contract (record-type-parent rtd:record-type-descriptor)
    ($rtd-parent rtd))
          
  (define/contract (record-type-uid rtd:record-type-descriptor)
    ($rtd-uid rtd))
    
  (define/contract (record-type-generative? rtd:record-type-descriptor)
    ($rtd-generative? rtd))    
    
  (define/contract (record-type-sealed? rtd:record-type-descriptor)
    ($rtd-sealed? rtd))    

  (define/contract (record-type-opaque? rtd:record-type-descriptor)
    ($rtd-opaque? rtd))    

  (define/contract (record-type-field-names rtd:record-type-descriptor)
    ($rtd-field-names rtd))

  (define/contract (record-field-mutable? rtd:record-type-descriptor k:fixnum)
    ($rtd-field-mutable? rtd k)))