#| License
Copyright (c) 2007-2016 Llewellyn Pritchard
All rights reserved.
This source code is subject to terms and conditions of the BSD License.
See docs/license.txt. |#

(library (boot predicates)
  (export
    bignum?
    rectnum?
    ratnum?
    complexnum?
    flonum?
    fixnum?
    number?
    stringbuilder?
    string?
    clr-string?
    char?
    vector?
    bytevector?
    symbol?
    boolean?
    procedure?
    condition?
    valuetype-vector?)
  (import 
    (except 
      (rnrs)
      fixnum?
      flonum?
      number?
      string?
      char?
      vector?
      bytevector?
      symbol?
      boolean?
      procedure?
      condition?)
    (ironscheme unsafe))
    
  
  (define (valuetype-vector? obj)
    ($valuetype-vector? obj))
  
  (define (condition? obj)
    ($condition? obj))
    
  (define (fixnum? obj)
    ($fixnum? obj))
    
  (define (flonum? obj)
    ($flonum? obj))

  (define (bignum? obj)
    ($bignum? obj))
    
  (define (rectnum? obj)
    ($rectnum? obj)) 
  
  (define (ratnum? obj)
    ($ratnum? obj))
  
  (define (complexnum? obj)
    ($complexnum? obj))
    
  (define (number? obj)
    ($or? (fixnum? obj)
          (flonum? obj)
          (bignum? obj)
          (ratnum? obj)
          (complexnum? obj)
          (rectnum? obj))) 

  ;; 宿主的不可变字符串对象（名字沿用；iron-056 起经原语判断）
  (define (clr-string? obj)
    ($immutable-string? obj))

  ;; 不是 System.String 的 Scheme 字符串：SchemeString（可变串与含 BMP 外字符的不可变串，iron-055）；
  ;; 旧种子过渡期也认 StringBuilder。名字沿用旧称。
  (define (stringbuilder? obj)
    ($mutable-string? obj))
    
  (define (string? obj)
    (or (clr-string? obj) 
        (stringbuilder? obj)))

  (define (char? obj)
    ($char? obj))

  (define (vector? obj)
    ($vector? obj))

  (define (bytevector? obj)
    ($bytevector? obj))

  (define (symbol? obj)
    ($symbol? obj))
    
  (define (boolean? obj)
    ($boolean? obj))
   
  (define (procedure? obj)
    ($procedure? obj)))
