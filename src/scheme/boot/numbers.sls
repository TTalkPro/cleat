#| License
Copyright (c) 2007-2016 Llewellyn Pritchard
All rights reserved.
This source code is subject to terms and conditions of the BSD License.
See docs/license.txt. |#

(library (boot numbers)
  (export
    =
    <
    >
    <=
    >=
    zero?
    positive?
    negative?
    complex?
    real?
    rational?
    integer?
    real-valued?
    rational-valued?
    integer-valued?    
    exact?
    inexact?
    make-polar
    make-rectangular
    angle
    magnitude
    finite?
    exact-integer?
    numerator
    denominator
    imag-part
    real-part
    nan?
    infinite?
    exp
    sin
    asin
    sinh
    cos
    acos
    cosh
    tan
    tanh
    log
    atan
    div
    abs
    floor
    ceiling
    truncate
    round
    fixnum->flonum
    real->flonum
    inexact
    exact
    sqrt
    exact-integer-sqrt
    expt
    rationalize
    even?
    odd?
    max
    min
    gcd
    lcm
    div0
    mod
    mod0
    div-and-mod
    div0-and-mod0    
    number->string
    fx+/carry
    fx-/carry
    fx*/carry
    add1
    sub1
    )
  (import 
    (except 
      (cleat scheme)
      =
      <
      >
      <=
      >=
      zero?
      positive?
      negative?
      complex?
      real?
      rational?
      integer?
      real-valued?
      rational-valued?
      integer-valued?    
      exact?
      inexact?
      make-polar
      make-rectangular
      angle
      magnitude
      finite?
      numerator
      denominator
      imag-part
      real-part
      nan?
      infinite?
      exp
      sin
      asin
      sinh
      cos
      acos
      cosh
      tan
      tanh
      log
      atan
      div
      abs
      floor
      ceiling
      truncate
      round
      fixnum->flonum
      real->flonum
      inexact
      exact
      sqrt
      exact-integer-sqrt
      expt
      rationalize
      even?
      odd?
      max
      min
      gcd
      lcm
      div0
      mod
      mod0
      div-and-mod
      div0-and-mod0       
      number->string
      fx+/carry
      fx-/carry
      fx*/carry
      add1
      sub1)
    (cleat scheme core)
    (cleat scheme contracts)
    (cleat scheme unsafe))
    
    
  ;; 数的宿主表示经 $ 原语操作（Cleat.Scheme 的 HostPrimitives，iron-056）
  (define (make-rectnum r1 r2)
    ($make-rectnum r1 r2))
    
  (define (rectnum-imag-part c)
    ($rectnum-imag c))
    
  (define (rectnum-real-part c)
    ($rectnum-real c))    
    
  (define (ratnum-denominator rat)
    ($ratnum-denominator rat))   
    
  (define (ratnum-numerator rat)
    ($ratnum-numerator rat))  
  
  (define (make-complexnum r1 r2)
    ($make-complexnum r1 r2))
    
  (define (complexnum-imag-part c)
    ($complexnum-imag c))
    
  (define (complexnum-real-part c)
    ($complexnum-real c))
    
  (define (bignum/ a b)
    ($bignum-quotient a b))    
    
  (define (bignum% a b)
    ($bignum-remainder a b))
    
  (define (bignum->fixnum b)
    ($bignum->fixnum b))
    
  (define (flonum->ratnum f)
    ($flonum->ratnum f))

  (define (ratnum->flonum r)
    ($ratnum->flonum r))
    
  (define (fixnum->bignum f)
    ($fixnum->bignum f))   
    
  (define (real->complexnum num)
    (cond 
      [(complexnum? num) num]
      [(rectnum? num)
        (rectnum->complexnum num)]
      [else  
        (make-complexnum (inexact num) 0.0)]))
        
  (define (complexnum->rectnum num)
    ($complexnum->rectnum num))     
    
  (define (rectnum->complexnum num)
    ($rectnum->complexnum num))               
        
  (define (->fixnum num)
    (if (fixnum? num)
        num
        ($bignum->fixnum num)))
    
  (define (->ratnum num)
    (cond 
      [(ratnum? num) num]
      [(real? num)
        (flonum->ratnum (real->flonum num))]
      [else
        (assertion-violation '->ratnum "not a real" num)]))
        
  (define (->bignum num)
    (cond 
      [(bignum? num) num]
      [(fixnum? num) (fixnum->bignum num)]
      [else
        (assertion-violation '->bignum "not an integer" num)]))     

  (define (real->flonum x)
    (cond
      [(flonum? x)
        x]
      [(fixnum? x)
        (fixnum->flonum x)]
      [(bignum? x)
        ($bignum->flonum x)]
      [(real? x)
        ($ratnum->flonum x)]
      [else
        (assertion-violation 'real->flonum "not a real" x)]))
    
  (define (fixnum->flonum x)
    (unless (fixnum? x)
      (assertion-violation 'fixnum->flonum "not a fixnum" x))
    ($fixnum->flonum x))
        
  ;; nan? / finite? / infinite?：R6RS 11.7.4.3 的参数是实数，复数报错（iron-072 对拍 Chez mats）。
  ;; 库内部（integer?、rational? 等谓词，div-and-mod）对复数也要问，用带 * 的宽松版本。
  (define (nan*? num)
    (cond
      [(or (fixnum? num)
           (bignum? num)
           (ratnum? num)
           (rectnum? num))
        #f]
      [(flonum? num)
        (flnan? num)]
      [(complexnum? num)
        (or (flnan? (complexnum-real-part num))
            (flnan? (complexnum-imag-part num)))]
      [else
        (assertion-violation 'nan? "not a number" num)]))

  (define (finite*? num)
    (cond
      [(or (fixnum? num)
           (bignum? num)
           (ratnum? num)
           (rectnum? num))
        #t]
      [(flonum? num)
        (flfinite? num)]
      [(complexnum? num)
        (and (flfinite? (complexnum-real-part num))
             (flfinite? (complexnum-imag-part num)))]
      [else
        (assertion-violation 'finite? "not a number" num)]))

  (define (infinite*? num)
    (cond
      [(or (fixnum? num)
           (bignum? num)
           (ratnum? num)
           (rectnum? num))
        #f]
      [(flonum? num)
        (flinfinite? num)]
      [(complexnum? num)
        (or (flinfinite? (complexnum-real-part num))
            (flinfinite? (complexnum-imag-part num)))]
      [else
        (assertion-violation 'infinite? "not a number" num)]))

  (define (check-real who num)
    (when (or (complexnum? num) (rectnum? num))
      (assertion-violation who "not a real" num)))
  (define (nan? num) (check-real 'nan? num) (nan*? num))
  (define (finite? num) (check-real 'finite? num) (finite*? num))
  (define (infinite? num) (check-real 'infinite? num) (infinite*? num))
        
  (define (exact? obj)
    (cond
      [(or (fixnum? obj) 
           (bignum? obj)
           (ratnum? obj)
           (rectnum? obj))
       #t]
      [(or (flonum? obj)
           (complexnum? obj))
       #f]
      [else
        (assertion-violation 'exact "not a number" obj)]))

  (define (inexact? obj)
    (cond
      [(or (fixnum? obj) 
           (bignum? obj)
           (ratnum? obj)
           (rectnum? obj))
       #f]
      [(or (flonum? obj)
           (complexnum? obj))
       #t]
      [else
        (assertion-violation 'inexact "not a number" obj)]))
        
  (define (complex? obj)
    (number? obj))
    
  (define (real? obj)
    (cond
      [(or (fixnum? obj) 
           (bignum? obj)
           (ratnum? obj)
           (flonum? obj))
       #t]
      [(or (complexnum? obj) (rectnum? obj))
        (let ((i (imag-part obj)))
         (and (zero? i)
              (exact? i)))]
      [else #f]))
        
  (define (rational? obj)
    (cond
      [(or (fixnum? obj) 
           (bignum? obj)
           (ratnum? obj))
       #t]
      [(and (or (complexnum? obj) 
                (rectnum? obj)
                (flonum? obj)) 
            (finite*? obj) 
            (not (nan*? obj)))
        (let ((i (imag-part obj)))
          (and (exact? i) 
               (zero? i)))]
      [else #f]))
        
  (define (integer? obj)
    (cond
      [(or (fixnum? obj) 
           (bignum? obj))
       #t]
      [(and (or (ratnum? obj) 
                (complexnum? obj) 
                (rectnum? obj)
                (flonum? obj))
            (finite*? obj) 
            (not (nan*? obj)))            
        (let ((i (imag-part obj)))
          (and (exact? i) 
               (zero? i)
               (= (denominator (real-part obj)) 1)))]
      [else #f]))
      
  (define (real-valued? obj)
    (cond
      [(or (fixnum? obj) 
           (bignum? obj)
           (ratnum? obj)
           (flonum? obj))
       #t]
      [(or (complexnum? obj) (rectnum? obj))
        (let ((i (imag-part obj)))
          (zero? i))]
      [else #f])) 
      
  (define (rational-valued? obj)
    (cond
      [(or (fixnum? obj) 
           (bignum? obj)
           (ratnum? obj))
       #t]
      [(and (or (complexnum? obj) 
                (rectnum? obj)
                (flonum? obj)) 
            (finite*? obj) 
            (not (nan*? obj)))
        (let ((i (imag-part obj)))
          (zero? i))]
      [else #f])) 
      
  (define (integer-valued? obj)
    (cond
      [(or (fixnum? obj) 
           (bignum? obj))
       #t]
      [(and (or (ratnum? obj) 
                (complexnum? obj) 
                (rectnum? obj)
                (flonum? obj))
            (finite*? obj) 
            (not (nan*? obj)))            
        (let ((i (imag-part obj)))
          (and (zero? i)
               (= (denominator (real-part obj)) 1)))]
      [else #f]))                

;; fixnum / flonum 快路径（iron-064，参照 Chez s/5_3.ss 的 type-case）：入口先按类型分派，
  ;; 原来的通用实现（含契约检查）放在同名的内层定义里，报错信息不变。
  (define zero?
    (let ()
      (define/contract (zero? num:number) (= num 0))
      (lambda (num)
        (cond
          [($fixnum? num) ($fx=? num 0)]
          [($flonum? num) ($fl=? num 0.0)]
          [else (zero? num)]))))

  (define positive?
    (let ()
      (define/contract (positive? num:number) (> num 0))
      (lambda (num)
        (cond
          [($fixnum? num) ($fx>? num 0)]
          [($flonum? num) ($fl>? num 0.0)]
          [else (positive? num)]))))

  (define negative?
    (let ()
      (define/contract (negative? num:number) (< num 0))
      (lambda (num)
        (cond
          [($fixnum? num) ($fx<? num 0)]
          [($flonum? num) ($fl<? num 0.0)]
          [else (negative? num)]))))
    
  (define (inexact num)
    (cond
      [($fixnum? num) ($fixnum->flonum num)]
      [(or (complexnum? num) 
           (flonum? num)) 
         num]
      [(or (exact-integer? num)
           (ratnum? num))
         (real->flonum num)]
      [(rectnum? num)
        (rectnum->complexnum num)]
      [else
        (assertion-violation 'inexact "not a number" num)]))
        
  (define (exact num)
    (cond
      [($fixnum? num) num]
      [(and ($flonum? num) (not (flnan? num)) (not (flinfinite? num))) ($flonum->exact num)]
      [(complexnum? num)
        (cond 
          [(zero? (complexnum-imag-part num))
            (exact (complexnum-real-part num))]
          [(let ((i (complexnum-imag-part num))
                 (r (complexnum-real-part num)))
              (and (rational? r) 
                   (rational? i)
                   (make-rectangular (exact r) (exact i))))]
          [else            
            (assertion-violation 'exact "no exact equivalent" num)])]
      [(flonum? num)
        (if (or (flnan? num) (flinfinite? num))
            (assertion-violation 'exact "no exact equivalent" num)
            (exact (flonum->ratnum num)))]
      [(bignum? num)
        (if ($bignum-fits-fixnum? num)
            (bignum->fixnum num)            
            num)]
      [(ratnum? num)
        (if (= (ratnum-denominator num) 1)
            (exact (ratnum-numerator num))
            num)]
      [(rectnum? num)
        (if (zero? (rectnum-imag-part num))
            (exact (rectnum-real-part num))
            num)]
      [(fixnum? num)
         num]
      [else
        (assertion-violation 'exact "not a number" num)])) 
        
  ;; bignum 与整数值 flonum 混合时先把 bignum 转 flonum 会溢出成 ±inf.0，(remainder #e1e1000 3.0) 得 +nan.0；
  ;; 这种情况按精确整数计算后再取不精确（Chez 同；iron-072 对拍 Chez mats）
  (define (mixed-big-flonum? x1 x2)
    (or (and (bignum? x1) (flonum? x2) (integer? x2))
        (and (flonum? x1) (integer? x1) (bignum? x2))))

  (define/contract (div0 x1:real x2:real)
    (if (mixed-big-flonum? x1 x2)
        (inexact (div0 (exact x1) (exact x2)))
        (let* ((d (div x1 x2))
               (m (- x1 (* d x2))))
          (cond 
            [(< m (magnitude (/ x2 2))) d]
            [(positive? x2) (+ d 1)]
            [else (- d 1)]))))
  
  ;; 两个 fixnum、除数不是 0 / -1 时直接取截断余数再修正到 [0, |x2|)（cleat-077：iron-072 给 div 加了 nan / 无穷与
  ;; bignum-flonum 混合的检查，mod 又经 div，fixnum 的 mod 慢了近一半）。除数 -1 时 .NET 的 % 对最小 fixnum 抛溢出，走原实现
  (define mod
    (let ()
      (define/contract (mod x1:real x2:real)
        (if (mixed-big-flonum? x1 x2)
            (inexact (mod (exact x1) (exact x2)))
            (- x1 (* (div x1 x2) x2))))
      (lambda (x1 x2)
        (if (and ($fixnum? x1) ($fixnum? x2) (not ($fx=? x2 0)) (not ($fx=? x2 -1)))
            (let ((r ($fxmod x1 x2)))
              (cond
                [(not ($fx<? r 0)) r]
                [($fx>? x2 0) ($fx+ r x2)]
                [else ($fx- r x2)]))
            (mod x1 x2)))))

  (define/contract (mod0 x1:real x2:real)
    (cond
      [(infinite*? x2) +nan.0]
      [(mixed-big-flonum? x1 x2) (inexact (mod0 (exact x1) (exact x2)))]
      [else (- x1 (* (div0 x1 x2) x2))]))
    
  (define/contract (div-and-mod x1:real x2:real)
    (if (mixed-big-flonum? x1 x2)
        (let-values (((d m) (div-and-mod (exact x1) (exact x2))))
          (values (inexact d) (inexact m)))
        (let ((d (div x1 x2)))
          (values d (- x1 (* d x2))))))

  (define/contract (div0-and-mod0 x1:real x2:real)
    (cond
      [(infinite*? x2) (values (div0 x1 x2) +nan.0)]
      [(mixed-big-flonum? x1 x2)
       (let-values (((d m) (div0-and-mod0 (exact x1) (exact x2))))
         (values (inexact d) (inexact m)))]
      [else
       (let ((d (div0 x1 x2)))
         (values d (- x1 (* d x2))))]))
      
  (define/contract (add1 x:number)
    (+ x 1))

  (define/contract (sub1 x:number)
    (- x 1))
        
  (define (fixnum->string num radix)
    (if ($fxnegative? num)
        (string-append "-" (number->string (abs num) radix))
        ($fixnum->string num radix)))
     
  ;; 宿主原语（iron-059）：原来在这里逐位 div/mod，O(n²)
  (define (bignum->string num radix)
    ($bignum->string num radix))
        
  (define number->string
    (case-lambda
      [(num)
        (number->string num 10)]
      [(num radix prec)
        ;; 精度参数：精确正整数，且 num 须不精确（R6RS 11.7.4.4；Chez 同），输出本身不受影响
        (unless (and (fixnum? prec) ($fx>? prec 0))
          (assertion-violation 'number->string "not a positive fixnum" prec))
        (unless (and (number? num) (inexact? num))
          (assertion-violation 'number->string "precision given for exact number" num prec))
        ;; R6RS 11.7.4.4：不精确的实部 / 虚部（无穷与 NaN 除外）写出尾数宽度 |p，p 是 ≥ 精度且能还原 z 的最小值；
        ;; double 一律 53 位，故 p = max(prec, 53)（Chez 同：(number->string 100.5 10 10) 为 "100.5|53"）
        (let ((p (number->string (if ($fx<? prec 53) 53 prec))))
          (define (part x)
            (let ((s (number->string x radix)))
              (if (and (flonum? x) (not (nan*? x)) (not (infinite*? x))) (string-append s "|" p) s)))
          (if (complexnum? num)
              (let ((r (real-part num)) (i (imag-part num)))
                (string-append (if (zero? r) "" (part r))
                               (if (or (negative? i) (nan*? i) (infinite*? i) (eqv? i -0.0)) "" "+")
                               (part i) "i"))
              (part num)))]
      [(num radix)
        (unless (fixnum? radix)
          (assertion-violation 'number->string "not a fixnum" radix))
        (unless (memv radix '(2 8 10 16))
          (assertion-violation 'number->string "not a valid radix" radix))
        (cond
          [(fixnum? num)
            (fixnum->string num radix)]
          [(flonum? num)
            (unless (= radix 10)
              (assertion-violation 'number->string "invalid radix" radix))
            (flonum->string num)]
          [(ratnum? num)
            (string-append (if (negative? num) "-" "") 
                           (number->string (abs (ratnum-numerator num)) radix)
                           "/"
                           (number->string (abs (ratnum-denominator num)) radix))]
          [(bignum? num)
            (bignum->string num radix)]
          [(complexnum? num)
            (unless (= radix 10)
              (assertion-violation 'number->string "invalid radix" radix))
            (string-append (if (zero? (real-part num)) 
                               "" 
                               (number->string (real-part num) radix))
                           (if (let ((i (imag-part num)))
                                 (or (negative? i)
                                     (nan*? i)
                                     (infinite*? i)
                                     (eqv? i -0.0)))
                               "" 
                               "+")
                           (number->string (imag-part num) radix)
                           "i")]
          [(rectnum? num)
            (string-append (if (zero? (real-part num)) 
                               "" 
                               (number->string (real-part num) radix))
                           (if (negative? (imag-part num)) "" "+")
                           (if (= 1 (imag-part num))
                               ""
                               (number->string (imag-part num) radix))
                           "i")]
          [else
            (assertion-violation 'number->string "not a number" num)])]))  

  (define-syntax fixnum-or-flonum->flonum
    (syntax-rules ()
      [(_ x) (let ((v x)) (if (fixnum? v) ($fixnum->flonum v) v))]))

  ;; 多参数比较短路为 #f 后仍检查余下参数（R6RS 要求参数都是数 / 实数，(= 4 3 'a) 须报错；iron-072 对拍 Chez mats）
  (define (check-rest-numbers who l)
    (for-each (lambda (x) (unless (number? x) (assertion-violation who "not a number" x))) l))
  (define (check-rest-reals who l)
    (for-each (lambda (x) (unless (real? x) (assertion-violation who "not a real" x))) l))

  (define-syntax define-comparer 
    (lambda (x)
      (syntax-case x ()
        [(_ name)
          (with-syntax ((uname 
                          (datum->syntax #'name
                            (string->symbol
                              (string-append 
                                "$fx"
                                (symbol->string (syntax->datum #'name))
                                "?"))))
                        (flname
                          (datum->syntax #'name
                            (string->symbol
                              (string-append
                                "$fl"
                                (symbol->string (syntax->datum #'name))
                                "?")))))
            #'(define name
                (case-lambda
                  [(a) 
                    (if (number? a)
                        #t
                        (assertion-violation 'name "not a number" a))]
                  [(a b)
                    (cond 
                      ;; 快速路径（iron-059）：同为 fixnum 或同为 flonum 时直接比较。
                      ;; 原来两个 fixnum 也要调用 real?、finite?、nan?、exact 与 $exact-compare，每次约 160ns。
                      ;; flonum 的 IEEE 比较与通用路径一致：有 NaN 为 #f，无穷正常比较，-0.0 与 0.0 相等。
                      ;; fixnum 与 flonum 混合时把 fixnum 转为 flonum：32 位整数转换精确，比较结果与精确比较相同。
                      [(and (fixnum? a) (fixnum? b)) (uname a b)]
                      [(and (flonum? a) (flonum? b)) (flname a b)]
                      [(and (or (fixnum? a) (flonum? a)) (or (fixnum? b) (flonum? b)))
                        (flname (fixnum-or-flonum->flonum a) (fixnum-or-flonum->flonum b))]
                      [else
                        ;; 其余实数一次比较完（iron-059）：-1 / 0 / 1，有 NaN 为 2，有非实数为 #f。
                        ;; 原来先调 real?、finite?、nan?、exact，有 ±∞ 时两边都转为 flonum，
                        ;; 超出 double 范围的精确数变成 ∞，(= (expt 2 2000) +inf.0) 得 #t。
                        (let ((c ($real-compare a b)))
                          (cond
                            [c (and (not ($fx=? c 2)) (uname c 0))]
                            [(and (number? a) (number? b))
                              (inexact=? (inexact a) (inexact b))]
                            [else
                              (assertion-violation 'name "not number arguments" a b)]))])]
                  [(x1 x2 . rest)
                    (let f ((a x1)(b (cons x2 rest)))
                      (cond 
                        [(null? b) #t]
                        [(name a ($car b))
                          (f ($car b) ($cdr b))]
                        [else (check-rest-numbers 'name ($cdr b)) #f]))])))])))
                        
  (define-syntax define-real-comparer 
    (lambda (x)
      (syntax-case x ()
        [(_ name)
          (with-syntax ((uname 
                          (datum->syntax #'name
                            (string->symbol
                              (string-append 
                                "$fx"
                                (symbol->string (syntax->datum #'name))
                                "?"))))
                        (flname
                          (datum->syntax #'name
                            (string->symbol
                              (string-append
                                "$fl"
                                (symbol->string (syntax->datum #'name))
                                "?")))))
            #'(define name
                (case-lambda
                  [(a b)
                    (cond 
                      ;; 快速路径（iron-059），同 define-comparer
                      [(and (fixnum? a) (fixnum? b)) (uname a b)]
                      [(and (flonum? a) (flonum? b)) (flname a b)]
                      [(and (or (fixnum? a) (flonum? a)) (or (fixnum? b) (flonum? b)))
                        (flname (fixnum-or-flonum->flonum a) (fixnum-or-flonum->flonum b))]
                      [else
                        ;; 其余实数一次比较完（iron-059），同 define-comparer
                        (let ((c ($real-compare a b)))
                          (if c
                              (and (not ($fx=? c 2)) (uname c 0))
                              (assertion-violation 'name "not real arguments" a b)))])]
                  [(x1 x2 . rest)
                    (let f ((a x1)(b (cons x2 rest)))
                      (cond 
                        [(null? b) #t]
                        [(name a ($car b))
                          (f ($car b) ($cdr b))]
                        [else (check-rest-reals 'name ($cdr b)) #f]))])))])))                        
                        
  (define-comparer =)
  (define-real-comparer <)
  (define-real-comparer <=)
  (define-real-comparer >)
  (define-real-comparer >=)   
  
  (define/contract (make-rectangular r1:real r2:real)
    (cond 
      [(and (exact? r1) (exact? r2))
        (make-rectnum (->ratnum r1) (->ratnum r2))]
      [(and (exact? r2) (zero? r2)) r1]
      [else 
        (make-complexnum (inexact r1) (inexact r2))]))
      
  (define/contract (make-polar r1:real r2:real)
    (if (and (exact? r2) (zero? r2))
      r1      
      (* r1 (make-rectangular (cos r2) (sin r2)))))
        
  (define/contract (angle num:number)
    (if (rectnum? num)
        (angle (inexact num))      
        (atan (imag-part num)
              (real-part num))))
          
  (define (magnitude num)
    (cond
      [(rectnum? num)
        (magnitude (inexact num))]
      [(complexnum? num)
        (let ((i (imag-part num))
              (r (real-part num)))
          (sqrt (+ (* i i) (* r r))))]   
      [(real? num)
        (abs num)]              
      [else
        (assertion-violation 'magnitude "not a number" num)]))
    
  (define (exact-integer? obj)
    (or (fixnum? obj)
        (bignum? obj)))    
    
  (define (numerator num)
    (cond
      [(exact-integer? num) num]
      [(ratnum? num)
        (exact (ratnum-numerator num))]
      [(and (real? num)
            (finite*? num)
            (not (nan*? num)))
        (inexact (numerator (exact num)))]
      [else
        (assertion-violation 'numerator "not a real" num)]))

  (define (denominator num)
    (cond
      [(exact-integer? num) 1]
      [(ratnum? num)
        (exact (ratnum-denominator num))]
      [(and (real? num)
            (finite*? num)
            (not (nan*? num)))
        (inexact (denominator (exact num)))]
      [else
        (assertion-violation 'denominator "not a real" num)]))
        
  (define (real-part num)
    (cond
      [(complexnum? num)
        (complexnum-real-part num)]
      [(rectnum? num)
        (exact (rectnum-real-part num))]
      [(real? num) num]
      [else 
        (assertion-violation 'real-part "not a number" num)]))

  (define (imag-part num)
    (cond
      [(complexnum? num)
        (complexnum-imag-part num)]
      [(rectnum? num)
        (exact (rectnum-imag-part num))]
      [(real? num) 0]
      [else 
        (assertion-violation 'imag-part "not a number" num)]))
        
  ;; 复数版与实数版各一个原语
  (define-syntax define-math-proc
    (syntax-rules ()
      [(_ name complex-op real-op)
        (define (name num)
          (cond
            [(rectnum? num)
              (name (rectnum->complexnum num))]
            [(complexnum? num)
              (complex-op num)]
            [(real? num)
              (real-op (inexact num))]
            [else
              (assertion-violation 'name "not a number" num)]))]))
              
  (define-syntax define-math-proc*
    (syntax-rules ()
      [(_ name complex-op real-op)
        (define (name num)
          (cond
            [(rectnum? num)
              (name (rectnum->complexnum num))]
            [(complexnum? num)
              (complex-op num)]
            [(real? num)
              (if (> (abs num) 1)
                (name (make-rectangular num 0.0))
                (real-op (inexact num)))]
            [else
              (assertion-violation 'name "not a number" num)]))]))              
              
  (define-math-proc exp $complex-exp $flexp)
  (define-math-proc sin $complex-sin $flsin)
  (define-math-proc* asin $complex-asin $flasin)
  (define-math-proc* sinh $complex-sinh $flsinh)
  (define-math-proc cos $complex-cos $flcos)
  (define-math-proc* acos $complex-acos $flacos)
  (define-math-proc* cosh $complex-cosh $flcosh)
  (define-math-proc tan $complex-tan $fltan)
  (define-math-proc* tanh $complex-tanh $fltanh)
  
  (define atan
    (case-lambda
      [(num)
        (cond
          [(rectnum? num)
              (atan (rectnum->complexnum num))]
          [(complexnum? num)
            ($complex-atan num)]
          [(real? num)
            ($flatan (inexact num))]
          [else
            (assertion-violation 'atan "not a number" num)])]
      [(num1 num2)
        (unless (real? num1)
          (assertion-violation 'atan "not a real" num1))
        (unless (real? num2)
          (assertion-violation 'atan "not a real" num2))
        ($flatan2 (inexact num1) (inexact num2))]))
            
  (define log
    (case-lambda
      [(num)
        (unless (number? num)
          (assertion-violation 'atan "not a number" num))
        (cond
          [(rectnum? num)
              (log (rectnum->complexnum num))]
          [(complexnum? num) 
            ($complex-log num)]
          [(negative? num) 
            ($complex-log (make-complexnum (inexact num) 0.0))]
          [(zero? num)
            (if (exact? num)
              (assertion-violation 'log "not possible" num)
              -inf.0)]
          [(infinite*? num)
            (if (negative? num)
              (make-complexnum (inexact (abs num)) 0)
              num)]
          [else
            ($fllog (inexact num))])]
      [(num1 num2)
        (/ (log num1) (log num2))]))
        
  ;; 两个 fixnum、除数不是 0 / -1 时：截断商，余数为负时向使余数非负的方向修正（R6RS 的 div：x1 = q·x2 + m，0 ≤ m < |x2|）；
  ;; |x2| ≥ 2 时 |q| ≤ 2^30，修正不会溢出（cleat-077，见 mod）。其余走原实现（报错、bignum、flonum、最小 fixnum ÷ -1）
  (define div
    (let ()
      (define/contract (div x1:real x2:real)
        (when (zero? x2)
          (assertion-violation 'div "divide by zero" x1 x2))
        (when (or (nan*? x1) (infinite*? x1))
          (assertion-violation 'div "cannot be nan or infinite" x1 x2))
        (if (mixed-big-flonum? x1 x2)
            (inexact (generic-div (exact x1) (exact x2)))
            (generic-div x1 x2)))
      (lambda (x1 x2)
        (if (and ($fixnum? x1) ($fixnum? x2) (not ($fx=? x2 0)) (not ($fx=? x2 -1)))
            (let ((q ($fxdiv x1 x2)))
              (cond
                [(not ($fx<? ($fxmod x1 x2) 0)) q]
                [($fx>? x2 0) ($fx- q 1)]
                [else ($fx+ q 1)]))
            (div x1 x2)))))
             
  (define abs
    (let ()
      (define/contract (abs x1:real)
        (if (negative? x1)
            (- x1)
            x1))
      (lambda (x)
        (cond
          [(and ($fixnum? x) (not ($fx=? x -2147483648))) ($fxabs x)]
          [($flonum? x) ($flabs x)]
          [else (abs x)]))))
        
  (define floor
    (let ()
      (define/contract (floor x:real)
    (cond
      [(exact-integer? x) x]
      [(ratnum? x)
        (let ((r (bignum/ (ratnum-numerator x) (ratnum-denominator x))))
          (exact (if (negative? x) (- r 1) r)))]
      [else
        ($flfloor (inexact x))]))
      (lambda (x)
        (cond
          [($fixnum? x) x]
          [($flonum? x) ($flfloor x)]
          [else (floor x)]))))
             
  (define ceiling
    (let ()
      (define/contract (ceiling x:real)
    (cond
      [(exact-integer? x) x]
      [(ratnum? x)
        (let ((r (bignum/ (ratnum-numerator x) (ratnum-denominator x))))
          (exact (if (positive? x) (+ r 1) r)))]
      [else
        ($flceiling (inexact x))]))
      (lambda (x)
        (cond
          [($fixnum? x) x]
          [($flonum? x) ($flceiling x)]
          [else (ceiling x)]))))

  (define truncate
    (let ()
      (define/contract (truncate x:real)
    (cond
      [(exact-integer? x) x]
      [(ratnum? x)
        (let ((r (bignum/ (ratnum-numerator x) (ratnum-denominator x))))
          (exact r))]      
      [else
        (let ((r ($fltruncate (inexact x))))
          (if (exact? x)
              (exact r)
              r))]))
      (lambda (x)
        (cond
          [($fixnum? x) x]
          [($flonum? x) ($fltruncate x)]
          [else (truncate x)]))))
            
  (define round
    (let ()
      (define/contract (round x:real)
    (cond
      [(exact-integer? x) x]
      [(ratnum? x)
        (let* ((neg? (negative? x))
               (x (abs x))
               (num (ratnum-numerator x))
               (den (ratnum-denominator x))
               (d (bignum/ num den))
               (r (bignum% num den))
               (hd (/ den 2))) 
          (let ((n (exact 
                     (cond 
                       [(> r hd) (+ d 1)]
                       [(< r hd) d]
                       ;; deal with denominator == 2, round to even
                       [(even? (div (+ num 1) 2)) (+ d 1)] 
                       [else d]))))
             (if neg?
                 (- n)
                 n)))]
      [else
        ($flround (inexact x))]))
      (lambda (x)
        (cond
          [($fixnum? x) x]
          [($flonum? x) ($flround x)]
          [else (round x)]))))
        
  ;; 非负 flonum 直接 $flsqrt；精确整数交给宿主（完全平方数得精确根，否则 flonum，iron-062）。
  ;; 原来精确参数把 flonum 结果再 exact，(sqrt 2) 得到一个分数。
  (define sqrt
    (let ()
      (define/contract (sqrt num:number)
    (cond
      [(rectnum? num)
        (sqrt (rectnum->complexnum num))]
      [(complexnum? num)
        ($complex-sqrt num)]
      [(negative? num)
        (make-rectangular 0 (sqrt (- num)))]
      [(bignum? num)
        (bignum-sqrt num)]
      [(infinite*? num) num]
      [(ratnum? num)
        (let ((n (bignum-sqrt (ratnum-numerator num)))
              (d (bignum-sqrt (ratnum-denominator num))))
          (if (and (exact? n) (exact? d))
              (/ n d)
              ($flsqrt (inexact num))))]
      [(fixnum? num) (bignum-sqrt num)]
      [else ($flsqrt (inexact num))]))
      (lambda (num)
        (cond
          [(and ($flonum? num) ($fl>=? num 0.0)) ($flsqrt num)]
          [(and ($fixnum? num) ($fx>=? num 0)) (bignum-sqrt num)]
          [else (sqrt num)]))))
              
  (define even?
    (let ()
      (define/contract (even? n:integer) (zero? (mod n 2)))
      (lambda (n)
        (if ($fixnum? n) ($fx=? ($fxand n 1) 0) (even? n)))))

  (define odd?
    (let ()
      (define/contract (odd? n:integer) (not (zero? (mod n 2))))
      (lambda (n)
        (if ($fixnum? n) (not ($fx=? ($fxand n 1) 0)) (odd? n)))))
  
  (define max
    (let ()
      (define/contract (max a:real . rest:real)
    (fold-left 
      (lambda (a b)
        (cond
          [(nan*? a) a]
          [(nan*? b) b]
          [else        
            (let ((r (if (< a b) b a)))
              (if (or (inexact? a) (inexact? b))
                  (inexact r)
                  r))]))
      a 
      rest))
      (case-lambda
        [(a b) (if (and ($fixnum? a) ($fixnum? b)) (if ($fx<? a b) b a) (max a b))]
        [(a . rest) (apply max a rest)])))
    
  (define min
    (let ()
      (define/contract (min a:real . rest:real)
    (fold-left 
      (lambda (a b)
        (cond
          [(nan*? a) a]
          [(nan*? b) b]
          [else        
            (let ((r (if (> a b) b a)))
              (if (or (inexact? a) (inexact? b))
                  (inexact r)
                  r))]))
      a 
      rest))
      (case-lambda
        [(a b) (if (and ($fixnum? a) ($fixnum? b)) (if ($fx>? a b) b a) (min a b))]
        [(a . rest) (apply min a rest)])))   
    
  (define/contract gcd
    (case-lambda
      [() 0]
      [(a:integer) (abs a)]
      [(a:integer b:integer) 
        (if (zero? b)
            (abs a)
            (abs (gcd b (mod a b))))]
      [(a:integer b:integer . rest)
        (fold-left gcd (abs a) (cons b rest))]))
          
  (define/contract lcm
    (case-lambda
      [() 1]
      [(a:integer) (abs a)]
      [(a:integer b:integer) 
        (if (or (zero? a)(zero? b))
            (if (and (exact? a) (exact? b)) 0 0.0)   ; (lcm 10.0 0) 为 0.0：不精确传染
            (abs (* (/ a (gcd a b)) b)))]
      [(a:integer b:integer . rest)
        (fold-left lcm (abs a) (cons b rest))]))
              
  ;; from SLIB
  (define/contract (rationalize x:real e:real) 
    (if (or (and (infinite*? x) (infinite*? e)) (nan*? x) (nan*? e))
        +nan.0
        (let ((r (apply / (find-ratio x e))))
          (if (and (exact? x) (exact? e))
              r
              (inexact r)))))

  (define (find-ratio x e) 
    (find-ratio-between (- x e) (+ x e)))

  (define (find-ratio-between x y)
    (define (sr x y)
      (let ((fx (exact (floor x))) 
            (fy (exact (floor y))))
        (cond 
          ((>= fx x) (list fx 1))
          ((= fx fy) (let ((rat (sr (/ (- y fy)) (/ (- x fx)))))
		                   (list (+ (cadr rat) (* fx (car rat))) (car rat))))
          (else (list (+ 1 fx) 1)))))
    (cond 
      ((< y x) (find-ratio-between y x))
      ((>= x y) (list x 1))
      ((positive? x) (sr x y))
      ((negative? y) (let ((rat (sr (- y) (- x))))
	                     (list (- (car rat)) (cadr rat))))
      (else '(0 1))))              
                
;; (iron-062) fixnum 与 bignum 都交给宿主的 Karatsuba 平方根（Numbers.SqrtRem）；
  ;; 原来 fixnum 经 sqrt / floor / exact，bignum 用从 n/2 起步的牛顿迭代
  (define (exact-integer-sqrt num)
    (if (and (or (fixnum? num) (bignum? num)) (not (negative? num)))
        (bignum-sqrt-exact num)
        (assertion-violation 'exact-integer-sqrt "not a non-negative exact integer" num)))
  
  ;; fixnum 的非负 fixnum 次幂、flonum 的 fixnum 次幂直接算（iron-064）；其余走通用实现。
  ;; 通用实现每次调用要分配内部 define 的闭包，所以快路径放在它外面。
  (define (expt obj1 obj2)
    (cond
      [(and ($fixnum? obj2) ($fx>=? obj2 0))
       (cond
         [($fixnum? obj1) ($bignum-expt obj1 obj2)]
         [($flonum? obj1) ($flexpt obj1 ($fixnum->flonum obj2))]
         [else (generic-expt obj1 obj2)])]
      [(and ($flonum? obj1) ($flonum? obj2) ($fl>=? obj1 0.0)) ($flexpt obj1 obj2)]
      [else (generic-expt obj1 obj2)]))

  (define (generic-expt obj1 obj2)
    (define (make-restriction-violation)
      (condition
        (make-implementation-restriction-violation)
        (make-who-condition 'expt)
        (make-message-condition "not supported")
        (make-irritants-condition (list obj1 obj2))))
    ;; 整数幂：bignum 指数运算；负指数取倒数（0 无精确倒数，报实现限制违例）
    (define (expt/bignum obj1 obj2 neg?)
      (cond
        ;; 指数超出 fixnum：底数只能是 0、±1，其余结果装不下（原来 ->fixnum 抛 OverflowException；Chez 报 out of memory）。
        ;; 按数值而不是 bignum? 判断：flonum 打印器会传来值很小但未规范化成 fixnum 的 BigInteger 指数
        [(> obj2 (greatest-fixnum))
         (cond
           [(or (= obj1 0) (= obj1 1)) obj1]
           [(= obj1 -1) (if (even? obj2) 1 -1)]
           [else (raise (make-restriction-violation))])]
        [else
         (let* ((a (->bignum obj1))
                (r ($bignum-expt a (->fixnum obj2))))
           (if neg?
               (if (zero? r)
                   (raise (make-restriction-violation))
                   (/ 1 r))
               (exact r)))]))
    ;; 确定幂指数符号后按操作数类型分派
    (define (expt/number obj1 obj2 e)
      (let* ((neg? (negative? obj2))
             (obj2 (if neg? (abs obj2) obj2)))
        (cond
          [(and e (integer? obj1) (integer? obj2))
           (expt/bignum obj1 obj2 neg?)]
          [(and e (rational? obj1) (integer? obj2))
           (let* ((f (->ratnum obj1)))
             (if neg?
                 (/ (expt (denominator f) obj2) (expt (numerator f) obj2))
                 (/ (expt (numerator f) obj2) (expt (denominator f) obj2))))]
          ;; flonum 底数、精确整数指数：奇数大指数转 flonum 后成了偶数，Math.Pow 会丢掉负底数的符号，
          ;; (expt -inf.0 (+ 1 (expt 2 100))) 应为 -inf.0、(expt -0.0 (+ 1 (expt 2 55))) 应为 -0.0（iron-072）
          [(and (flonum? obj1) (exact? obj2) (integer? obj2))
           (let* ((r ($flexpt ($flabs obj1) (inexact obj2)))
                  (r (if (and (odd? obj2) (or (negative? obj1) (eqv? obj1 -0.0))) (- r) r)))
             (if neg? (/ 1 r) r))]
          [(and (real? obj1) (real? obj2))
           (let ((r ($flexpt (inexact obj1) (inexact obj2))))
             (if neg?
                 (/ 1 r)
                 r))]
          [else
           (raise (make-restriction-violation))])))
    ;; 零底数、非零指数的不精确结果：-0.0 的精确奇数次幂保留符号（(expt -0.0 (+ 1 (expt 2 55))) 为 -0.0，
    ;; 负指数为 -inf.0）；大指数转 flonum 后成了偶数，Math.Pow 看不出奇偶
    (define (zero-expt obj1 obj2)
      (if (and (eqv? obj1 -0.0) (exact? obj2) (integer? obj2))
          (let ((r ($flexpt 0.0 (inexact obj2))))
            (if (odd? obj2) (- r) r))
          ($flexpt (inexact obj1) (inexact obj2))))
    ;; 通用分支：先按零值/单位元短路，再进入类型分派
    (define (expt* obj1 obj2)
      (let ((e (and (exact? obj1) (exact? obj2)))
            (z1 (zero? obj1))
            (z2 (zero? obj2)))
        (cond
          ;; 0 的负实数次幂：精确时没有值（1/0），报 &implementation-restriction（R6RS 允许报错或返回未指定数，
          ;; 其测试套件按此条件类型接住；Chez 也报错）；不精确时按 IEEE 给 ±inf.0（iron-072）
          [(and z1 (real? obj2) (negative? obj2))
           (if e
               (raise (make-restriction-violation))
               (zero-expt obj1 obj2))]
          [(and z1 (not z2))
           (if e 0 (zero-expt obj1 obj2))]
          [(or z2 (= obj1 1))
           (if e 1 1.0)]
          [(= obj2 1)
           (if e obj1 (inexact obj1))]
          [else
           (expt/number obj1 obj2 e)])))
    (cond
      [(infinite*? obj2)
        (if (negative? obj2)
            (if (> (abs obj1) 1)
                0.0
                +inf.0)
            (if (> (abs obj1) 1)
                +inf.0
                0.0))]      
      [(rectnum? obj1)
        (expt (rectnum->complexnum obj1) obj2)]
      [(rectnum? obj2)
        (expt obj1 (rectnum->complexnum obj2))]
      [(or (complexnum? obj1) 
           (complexnum? obj2) 
           (and (negative? obj1) 
                (not (and (real? obj1)
                          (integer? obj2)))))
        (let ((r ($complex-expt (real->complexnum obj1)
                                (real->complexnum obj2))))
           (if (zero? (imag-part r))
               (real-part r)
               r))]
      [else
        (expt* obj1 obj2)]))
                   
  ; TODO: improve this    
  (define/contract (fx*/carry fx1:fixnum fx2:fixnum fx3:fixnum)
    (let ((s (+ (* fx1 fx2) fx3))
          (e (expt 2 (fixnum-width))))
      (let-values (((d m) (div0-and-mod0 s e)))
        (values m d))))

  (define/contract (fx-/carry fx1:fixnum fx2:fixnum fx3:fixnum)
    (let ((s (- fx1 fx2 fx3))
          (e (expt 2 (fixnum-width))))
      (let-values (((d m) (div0-and-mod0 s e)))
        (values m d))))

  (define/contract (fx+/carry fx1:fixnum fx2:fixnum fx3:fixnum)
    (let ((s (+ fx1 fx2 fx3))
          (e (expt 2 (fixnum-width))))
      (let-values (((d m) (div0-and-mod0 s e)))
        (values m d))))
                  
;;; Free-format algorithm for printing IEEE double-precision positive
;;; floating-point numbers in base 10

;;; It uses the floating-point logarithm to estimate the scaling factor
;;; and a table to look up powers of ten.

;;; Input to flonum->digits:
;;;       v -- a positive floating-point number, f x 2^e
;;;       f -- mantissa of v
;;;       e -- exponent of v

;;; Output: (k d_1 d_2 ... d_n),
;;;   where 0.d_1...d_n x 10^k is the shortest correctly rounded base-10
;;;   number that rounds to v when input (it assumes the input
;;;   routine rounds to even)

;;; See also "Printing Floating-Point Numbers Quickly and Accurately"
;;; in Proceedings of the SIGPLAN '96 Conference on Programming Language
;;; Design and Implementation.

;;; Author: Bob Burger  Date: March 1996                            

  (define flonum->digits
    (lambda (v f e)
      (let ([min-e -1074]
            [bp-1 (expt 2 52)]
            [round? (even? f)])
        (if (>= e 0)
            (if (not (= f bp-1))
                (let ([be (expt 2 e)])
                  (scale (* f be 2) 2 be be 0 round? round? v))
                (let ([be (expt 2 e)])
                  (scale (* f be 4) 4 (* be 2) be 0 round? round? v)))
            (if (or (= e min-e) (not (= f bp-1)))
                (scale (* f 2) (expt 2 (- 1 e)) 1 1 0 round? round? v)
                (scale (* f 4) (expt 2 (- 2 e)) 2 1 0 round? round? v))))))

  (define scale
    (lambda (r s m+ m- k low-ok? high-ok? v)
      (let ([est (inexact->exact (ceiling (- (log10 v) 1e-10)))])
        (if (>= est 0)
            (fixup r (* s (expt10 est)) m+ m- est low-ok? high-ok?)
            (let ([scale (expt10 (- est))])
              (fixup (* r scale) s (* m+ scale) (* m- scale)
                     est low-ok? high-ok?))))))

  (define fixup
    (lambda (r s m+ m- k low-ok? high-ok?)
      (if ((if high-ok? >= >) (+ r m+) s) ; too low?
          (cons (+ k 1) (generate r s m+ m- low-ok? high-ok?))
          (cons k
                (generate (* r 10) s (* m+ 10) (* m- 10) low-ok? high-ok?)))))

  (define generate
    (lambda (r s m+ m- low-ok? high-ok?)
      (let ([d (quotient r s)]
            [r (remainder r s)])
        (let ([tc1 ((if low-ok? <= <) r m-)]
              [tc2 ((if high-ok? >= >) (+ r m+) s)])
          (if (not tc1)
              (if (not tc2)
                  (cons d (generate (* r 10) s (* m+ 10) (* m- 10)
                                    low-ok? high-ok?))
                  (list (+ d 1)))
              (if (not tc2)
                  (list d)
                  (if (< (* r 2) s)
                      (list d)
                      (list (+ d 1)))))))))

  (define expt10
    (let ([table (make-vector 326)])
      (do ([k 0 (+ k 1)] [v 1 (* v 10)])
          ((= k 326))
        (vector-set! table k v))
      (lambda (k)
        (vector-ref table k))))

  (define log10
    (let ([f (/ (log 10))])
      (lambda (x)
        (* (log x) f))))

; lets get printing!        

  (define (get-digits flo)
    (call-with-values (lambda () (decompose-flonum flo)) flonum->digits))  

  (define (get-chr i)
    (integer->char (+ (char->integer #\0)(fxmod i 10))))  

  ;; -3 ≤ k ≤ 0 的小负指数输出："0." 后接 -k 个 0 再接数字
  (define (flonum->string/negative k n p r)
    (put-string p "0.")
    (let f ((i k))
      (unless (zero? i)
        (put-string p "0")
        (f (+ i 1))))
    (let f ((n n))
      (unless (null? n)
        (put-char p (get-chr (car n)))
        (f (cdr n))))
    (r))

  ;; 指数记法输出：d.ddddde±xx
  (define (flonum->string/exponent k n p r)
    (put-char p (get-chr (car n)))
    (unless (null? (cdr n))          ; 只有一位有效数字时写 1e23 而不是 1.e23
      (put-string p "."))
    (let f ((n (cdr n)))
      (unless (null? n)
        (put-char p (get-chr (car n)))
        (f (cdr n))))
    (put-string p "e")
    (display (- k 1) p)
    (r))

  ;; 0 ≤ k ≤ 9 的普通十进制输出：逐位写数字，k 处补小数点，末尾不足补 "0"
  (define (flonum->string/plain k n p r)
    (let f ((i 0) (n n))
      (cond
        [(null? n)
         (if (< i k)
             (begin
               (put-string p "0")
               (f (+ i 1) n))
             (begin
               (when (= i k)
                 (put-string p ".0"))
               (r)))]
        [else
         (when (= i k)
           (when (zero? k)
             (put-string p "0"))
           (put-string p "."))
         (put-char p (get-chr (car n)))
         (f (+ i 1) (cdr n))])))

  (define (flonum->string flo)
    (cond
      [(flzero? flo) (if (fl<? (fl/ 1.0 flo) 0.0) "-0.0" "0.0")]
      [(flnan? flo) "+nan.0"]
      [(flinfinite? flo) 
        (if (flpositive? flo)
            "+inf.0"
            "-inf.0")]
      [else
        (let-values (((p r) (open-string-output-port)))
          (let* ((d (get-digits (flabs flo)))
                 (k (car d))
                 (n (cdr d)))
            (when (flnegative? flo)
              (put-string p "-"))
            (cond
              [(<= 0 k 9)
               ; 指数非负且不大：直接十进制
               (flonum->string/plain k n p r)]
              [(<= -3 k 0)
                ; 小负指数
                (flonum->string/negative k n p r)]
              [else
                ; 指数记法
                (flonum->string/exponent k n p r)])))]))

  ;; 运行时（Cleat.Scheme）经 RuntimeHooks 回调这些过程（见 boot/exceptions.sls）
  ($set-runtime-hook! 'exact exact)
  ($set-runtime-hook! 'inexact inexact))
