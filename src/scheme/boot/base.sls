#| License
Copyright (c) 2007-2016 Llewellyn Pritchard
All rights reserved.
This source code is subject to terms and conditions of the BSD License.
See docs/license.txt. |#

(library (boot base)
  (export
    define
    define-syntax
    quote
    lambda
    if
    set!
    cond
    ...
    =>
    else
    case
    and
    or
    let
    let*
    letrec
    letrec*
    let-values
    let*-values
    begin
    
    eqv?
    eq?
    equal?
    
    real-valued?
    rational-valued?
    integer-valued?
    
    inexact
    exact
    
    odd?
    even?
    finite?
    infinite?
    nan?
    
    max
    min
    
    +
    *
    -
    /
    
    abs
    div-and-mod
    div
    mod
    div0-and-mod0
    div0
    mod0
    
    gcd
    lcm
    
    numerator
    denominator
    
    floor
    ceiling
    truncate
    round
    
    rationalize
    
    exp
    log
    sin
    cos
    tan
    asin
    acos
    atan
    
    sqrt
    exact-integer-sqrt
    expt
    
    make-rectangular
    make-polar
    real-part
    imag-part
    magnitude
    angle
    
    number->string
    string->number
    
    not
    boolean?
    boolean=?
    
    pair?
    cons
    car
    cdr
    caar
    cadr
    cdar
    cddr
    caaar
    caadr
    cadar
    caddr
    cdaar
    cdadr
    cddar
    cdddr
    caaaar
    caaadr
    caadar
    caaddr
    cadaar
    cadadr
    caddar
    cadddr
    cdaaar
    cdaadr
    cdadar
    cdaddr
    cddaar
    cddadr
    cdddar
    cddddr
    null?
    list?
    list
    length
    append
    reverse
    list-tail
    list-ref
    map
    for-each
    
    symbol?
    symbol->string
    symbol=?
    string->symbol
    
    char?
    char->integer
    integer->char
    
    string?
    make-string
    string
    string-length
    string-ref

    substring
    string-append
    string->list
    list->string
    string-for-each
    string-copy
    
    vector?
    make-vector
    vector
    vector-length
    vector-ref
    vector-set!
    vector->list
    list->vector
    vector-fill!
    vector-map
    vector-for-each
    vector-fold-left
    
    error
    assertion-violation
    assert
    
    apply
    call-with-current-continuation
    call/cc
    call-with-values
    dynamic-wind
    procedure?
    
    quasiquote
    unquote
    unquote-splicing
    
    let-syntax
    letrec-syntax
    syntax-rules
    identifier-syntax

    fixnum?
    flonum?
    fixnum-width
    bytevector?
    
    string-set!
    string-fill!
    string-format)
  (import 
    (except (ironscheme) 
      caar
      cadr
      cdar
      cddr
      caaar
      caadr
      cadar
      caddr
      cdaar
      cdadr
      cddar
      cdddr
      caaaar
      caaadr
      caadar
      caaddr
      cadaar
      cadadr
      caddar
      cadddr
      cdaaar
      cdaadr
      cdadar
      cdaddr
      cddaar
      cddadr
      cdddar
      cddddr
 
      symbol=?
      boolean=?
      vector-fold-left
      vector-map
      vector-for-each
      string-for-each
      reverse
      vector-fill!

      list->vector
      list->string 
      
      vector-ref
      vector-set!  

      symbol->string
      string->symbol 
      
      make-vector
      vector-length  
      
      make-string  
      string-ref
      string-set!
      string-fill!
      string-length
      string-copy
      substring
      string-append
      string->list
      string
      
      string-format
      
      vector->list
      char->integer
      integer->char)
    (ironscheme contracts)
    (ironscheme typed)
    (ironscheme core)
    (ironscheme unsafe))
    
  
  (define (char->integer chr)
    (unless (char? chr)
      (assertion-violation 'char->integer "not a char" chr))
    ($char->integer chr))
  
  (define/contract (integer->char num:fixnum)
    (let: (((num : Int32) num))
      (when ($or? ($fxnegative? num)      
                  ($fx>? num #x10ffff)
                  ($and? ($fx>? num #xd7ff)
                         ($fx<? num #xe000)))
        (assertion-violation 'integer->char "not a valid unicode value" num))
      ($integer->char num)))
    
;; 字符串按码点（iron-055，designs/12-strings.md）：不可变且不含代理码元的串为 System.String，
  ;; 可变串与含 BMP 外字符的串为 SchemeString；基本操作在 C#（Builtins 的 Runtime/Strings.cs）。
  (define/contract make-string
    (case-lambda
      [(k)
        (make-string k #\nul)]
      [(k:fixnum fill:char)
        (when ($fxnegative? k)
          (assertion-violation 'make-string "cannot be negative" k))        
        ($make-string k fill)]))

  (define (string-ref str k)
    (unless (and (fixnum? k) ($fx>=? k 0))
      (assertion-violation 'string-ref "not a non-negative integer" k))        
    (unless (string? str)
      (assertion-violation 'string-ref "not a string" str))
    ($string-ref str k))
          
  (define (string-set! str k val)
    (unless (stringbuilder? str)
      (assertion-violation 'string-set! "not a mutable string" str))
    (unless (and (fixnum? k) ($fx>=? k 0))
      (assertion-violation 'string-set! "not a non-negative integer" k))
    (unless (char? val)
      (assertion-violation 'string-set! "not a character" val))
    ($string-set! str k val))
    
  (define (string-fill! str fill)
    (unless (stringbuilder? str)
      (assertion-violation 'string-fill! "not a mutable string" str))
    (unless (char? fill)
      (assertion-violation 'string-fill! "not a character" fill))
    ($string-fill! str fill))
          
  (define (string-length str)
    (unless (string? str)
      (assertion-violation 'string-length "not a string" str))
    ($string-length str))

  (define/contract (string . args:char)
    ($chars->string args))
        
  (define/contract (string->list str:string)
    ($string->list str))
  
;; R6RS：新分配的副本可以 string-set!，所以总是返回可变字符串（iron-054；原来对不可变字符串返回不可变副本）
  (define (string-copy str)
    (if (string? str)
        ($string-copy str)
        (assertion-violation 'string-copy "not a string" str)))

  (define/contract (substring str start:fixnum end:fixnum)
    (let: (((start : Int32) start)((end : Int32) end))
      (unless ($fx>=? start 0)
        (assertion-violation 'substring "not a non-negative integer" start)) 
      (unless ($fx>=? end 0)
        (assertion-violation 'substring "not a non-negative integer" end))
      (when ($fx<? end start)
        (assertion-violation 'substring "start must be smaller or equal to end" start end))
      (unless ($fx<=? end (string-length str))
        (assertion-violation 'substring "outside string bounds" end))
      (if (string? str)
          ($substring str start end)
          (assertion-violation 'substring "not a string" str))))
    
  ; probably need to be made faster  
  (define/contract (string-append . args:string)
    ($string-append args))
                     
  (define/contract (string-format fmt:string . args)
    ($string-format fmt args))
  
  (define/contract (symbol->string sym:symbol)
    ;; 含 BMP 外字符的名字得到宽串（iron-055）
    ($symbol-name sym))
    
  (define/contract (string->symbol str:string)
    ($string->symbol str))

  (define/contract (list->vector lst:list)
    ($list->vector lst))
    
  (define/contract (list->string lst:list)
    ($chars->string lst))
    
  (define/contract (vector-ref x:vector n:fixnum)
    (when ($fxnegative? n)
      (assertion-violation 'vector-ref "negative index" n))
    (when ($fx>=? n (vector-length x))
      (assertion-violation 'vector-ref "index out of bounds" n))
    ($vector-ref x n))
    
  (define/contract (vector-set! x:vector n:fixnum value)
    (when ($fxnegative? n)
      (assertion-violation 'vector-set! "negative index" n))
    (when ($fx>=? n (vector-length x))
      (assertion-violation 'vector-set! "index out of bounds" n))
    ($vector-set! x n value)
    (void))   
    
  (define/contract make-vector         
    (case-lambda
      [(k:fixnum)
        (when ($fxnegative? k)
          (assertion-violation 'make-vector "cannot be negative" k))
        ($make-vector k)]
      [(k fill)
        (let ((vec (make-vector k)))
          (vector-fill! vec fill)
          vec)]))
          
  (define/contract (vector-length vec:vector)
    ($vector-length vec))            
  
  (define/contract (vector-fill! vec:vector val)
    (let: (((vec : Object[]) vec)
           ((len : Int32) (vector-length vec)))
      (do ((i 0 ($fx+ i 1)))
          (($fx=? i len))
        ($vector-set! vec i val))))
        
  (define/contract (vector->list vec:vector)
    ($vector->list vec))   
    
  (define/contract (reverse lst:list)
    (let f ((l lst)(a '()))
      (if (null? l)
          a
          (f ($cdr l) (cons ($car l) a)))))
   
  (define (caar   x) (car (car x)))
  (define (cadr   x) (car (cdr x)))
  (define (cdar   x) (cdr (car x)))
  (define (cddr   x) (cdr (cdr x)))

  (define (caaar  x) (caar (car x)))
  (define (caadr  x) (caar (cdr x)))
  (define (cadar  x) (cadr (car x)))
  (define (caddr  x) (cadr (cdr x)))
  (define (cdaar  x) (cdar (car x)))
  (define (cdadr  x) (cdar (cdr x)))
  (define (cddar  x) (cddr (car x)))
  (define (cdddr  x) (cddr (cdr x)))

  (define (caaaar x) (caaar (car x)))
  (define (caaadr x) (caaar (cdr x)))
  (define (caadar x) (caadr (car x)))
  (define (caaddr x) (caadr (cdr x)))
  (define (cadaar x) (cadar (car x)))
  (define (cadadr x) (cadar (cdr x)))
  (define (caddar x) (caddr (car x)))
  (define (cadddr x) (caddr (cdr x)))
  (define (cdaaar x) (cdaar (car x)))
  (define (cdaadr x) (cdaar (cdr x)))
  (define (cdadar x) (cdadr (car x)))
  (define (cdaddr x) (cdadr (cdr x)))
  (define (cddaar x) (cddar (car x)))
  (define (cddadr x) (cddar (cdr x)))
  (define (cdddar x) (cdddr (car x)))
  (define (cddddr x) (cdddr (cdr x)))

  (define (symbol=? a b . rest)
    (unless (symbol? a) (assertion-violation 'symbol=? "not a symbol" a))
    (for-all 
      (lambda (x) 
        (unless (symbol? x) (assertion-violation 'symbol=? "not a symbol" x))
        (eq? a x)) 
      (cons b rest)))

  (define (boolean=? a b . rest)
    (unless (boolean? a) (assertion-violation 'boolean=? "not a boolean" a))
    (for-all 
      (lambda (x) 
        (unless (boolean? x) (assertion-violation 'boolean=? "not a boolean" x))
        (eq? a x)) 
      (cons b rest)))
      
  (define (vector-length=? vecs)
    (apply = (map vector-length vecs)))

  (define (string-length=? vecs)
    (apply = (map string-length vecs)))
    
  (define/contract (vector-fold-left combine:procedure nil vec:vector)
    (let ((len (vector-length vec)))
      (let f ((i 0)(nil nil))
        (if ($fx=? i len)
            nil
            (f ($fx+ i 1) 
               (combine nil ($vector-ref vec i)))))))

  (define/contract vector-map
    (case-lambda
      [(p:procedure vec:vector)
        ((lambda: ((p : Procedure)(vec : Object[])(len : Int32))
          (let ((res (make-vector len '())))
            (do ((i 0 ($fx+ i 1)))
                (($fx=? i len) res)
              ($vector-set! res i (p ($vector-ref vec i))))))
           p vec (vector-length vec))]
      [(p:procedure vec1:vector . vecs:vector)
        (define (ref i) (lambda (x) ($vector-ref x i)))
        (unless (vector-length=? (cons vec1 vecs))
          (assertion-violation 'vector-map "Unbalanced vectors" (cons vec1 vecs)))
        (let* ((len (vector-length vec1))
               (res (make-vector len '())))
          (do ((i 0 ($fx+ i 1)))
              (($fx=? i len) res)
            ($vector-set! res i
                  (apply p (map (ref i) 
                                (cons vec1 vecs))))))]))
        
  (define/contract vector-for-each
    (case-lambda
      [(p:procedure vec:vector)
        ((lambda: ((p : Procedure)(vec : Object[])(len : Int32))
          (do ((i 0 ($fx+ i 1)))
              (($fx=? i len))
            (p ($vector-ref vec i))))
          p vec (vector-length vec))]
      [(p:procedure vec1:vector . vecs:vector)
        (define (ref i) (lambda (x) ($vector-ref x i)))
        (unless (vector-length=? (cons vec1 vecs))
          (assertion-violation 'vector-for-each "Unbalanced vectors" (cons vec1 vecs)))
        (let ((len (vector-length vec1)))
          (do ((i 0 ($fx+ i 1)))
              (($fx=? i len))
            (apply p (map (ref i) 
                          (cons vec1 vecs)))))]))
          
  (define/contract string-for-each
    (case-lambda
      [(p:procedure str:string)
        (let: (((len : Int32) (string-length str))
               ((p : Procedure) p))
          (do ((i 0 ($fx+ i 1)))
              (($fx=? i len))
            (p (string-ref str i))))]
      [(p:procedure str1:string . strs:string)
        (define (ref i) (lambda (x) (string-ref x i)))
        (unless (string-length=? (cons str1 strs))
          (assertion-violation 'string-for-each "Unbalanced strings" (cons str1 strs)))
        (let ((len (string-length str1)))
          (do ((i 0 ($fx+ i 1)))
              (($fx=? i len))
            (apply p (map (ref i) 
                          (cons str1 strs)))))])))
