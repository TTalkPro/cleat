#| License
Copyright (c) 2007-2016 Llewellyn Pritchard
All rights reserved.
This source code is subject to terms and conditions of the BSD License.
See docs/license.txt. |#

(library (boot arithmetic bitwise)
  (export
    bitwise-not
    
    bitwise-and
    bitwise-ior
    bitwise-xor
    
    bitwise-if
    
    bitwise-bit-count
    bitwise-length
    bitwise-bit-field
    
    bitwise-first-bit-set
    bitwise-bit-set?
    
    bitwise-copy-bit
    bitwise-copy-bit-field
    
    bitwise-arithmetic-shift
    bitwise-arithmetic-shift-left
    bitwise-arithmetic-shift-right
    
    bitwise-rotate-bit-field
    bitwise-reverse-bit-field)
    
  (import 
    (ironscheme unsafe)
    (ironscheme core)
    (except (ironscheme) 
      bitwise-if 
      bitwise-copy-bit 
      bitwise-bit-field 
      bitwise-copy-bit-field
      bitwise-arithmetic-shift-left 
      bitwise-arithmetic-shift-right
      bitwise-rotate-bit-field
      bitwise-not
      bitwise-and
      bitwise-ior
      bitwise-xor
      bitwise-bit-count
      bitwise-length
      bitwise-first-bit-set
      bitwise-bit-set?
      bitwise-reverse-bit-field
      bitwise-arithmetic-shift))
      
      
  ;; 大整数的位运算经 $bignum-* 原语（IronScheme.Core.HostPrimitives，iron-056），结果已规范为 fixnum 或 bignum
  (define (->bignum ei)
    (cond
      [(bignum? ei) ei]
      [(fixnum? ei) 
        ($fixnum->bignum ei)]
      [else
        (assertion-violation #f "not a exact integer" ei)]))
        
  (define (bitwise-not ei)
    ($bignum-not (->bignum ei)))
                            
  (define bitwise-and
    (case-lambda
      [()  -1]
      [(ei)
        ei]
      [(ei1 ei2)
        (if (and (fixnum? ei1)
                 (fixnum? ei2))
            (fxand ei1 ei2)
            ($bignum-and (->bignum ei1) (->bignum ei2)))]
      [(ei1 ei2 . rest)
        (fold-left bitwise-and ei1 (cons ei2 rest))]))
        
  (define bitwise-ior
    (case-lambda
      [()  0]
      [(ei)
        ei]
      [(ei1 ei2)
        (if (and (fixnum? ei1)
                 (fixnum? ei2))
            (fxior ei1 ei2)      
            ($bignum-ior (->bignum ei1) (->bignum ei2)))]
      [(ei1 ei2 . rest)
        (fold-left bitwise-ior ei1 (cons ei2 rest))]))

  (define bitwise-xor
    (case-lambda
      [()  0]
      [(ei)
        ei]
      [(ei1 ei2)
        (if (and (fixnum? ei1)
                 (fixnum? ei2))
            (fxxor ei1 ei2)      
            ($bignum-xor (->bignum ei1) (->bignum ei2)))]
      [(ei1 ei2 . rest)
        (fold-left bitwise-xor ei1 (cons ei2 rest))]))

  ;; bit-count、length、first-bit-set 的大整数情形由宿主一次算出（iron-059；原来逐位右移，O(n²)）
  (define (bitwise-bit-count ei)
    (if (fixnum? ei)
        (fxbit-count ei)
        ($bignum-bit-count (->bignum ei))))
        
  (define (bitwise-length ei)
    (if (fixnum? ei)
        (fxlength ei)
        ($bignum-bit-length (->bignum ei))))

  (define (bitwise-first-bit-set ei)
    (if (fixnum? ei)
        (fxfirst-bit-set ei)
        ($bignum-first-bit-set (->bignum ei))))

  (define (bitwise-bit-set? ei k)
    (cond
      [(= -1 ei) #t]
      [(and (fixnum? ei) (< -1 k 32))
        (fxbit-set? ei k)]
      [else
        (let ((ei (->bignum ei)))
          (when (negative? k)
            (assertion-violation 'bitwise-bit-set? "cannot be negative" k))
          (if (fixnum? k)
              ($bignum-bit-set? ei k)
              (negative? ei)))]))   ; 下标超出 fixnum：只剩符号位
  
  (define (bitwise-if ei1 ei2 ei3)
    (bitwise-ior (bitwise-and ei1 ei2)
      (bitwise-and (bitwise-not ei1) ei3)))
      
  (define (bitwise-copy-bit ei1 ei2 ei3)
    (unless (or (zero? ei3) (= ei3 1))
      (assertion-violation 'bitwise-copy-bit "must be 0 or 1" ei3))
    (when (negative? ei2)
      (assertion-violation 'bitwise-copy-bit "cannot be negative" ei2))      
    ;; fixnum 只有 31 个数值位（第 31 位是符号），下标 31 不能走 fxcopy-bit（iron-054；原来写成 < 32，下标 31 报错）
    (if (and (fixnum? ei1) (< -1 ei2 31))
        (fxcopy-bit ei1 ei2 ei3)
        (bitwise-if 
          (bitwise-arithmetic-shift-left 1 ei2)
          (bitwise-arithmetic-shift-left ei3 ei2)
          ei1)))
        
  (define (bitwise-bit-field ei1 ei2 ei3)
    (when (negative? ei2)
      (assertion-violation 'bitwise-bit-field "cannot be negative" ei2))
    (when (negative? ei3)
      (assertion-violation 'bitwise-bit-field "cannot be negative" ei3)) 
    (unless (<= ei2 ei3)
      (assertion-violation 'bitwise-bit-field "must be less than or equal" ei2 ei3))
    (if (and (fixnum? ei1) (< -1 ei3 32))
        (fxbit-field ei1 ei2 ei3)
        (bitwise-arithmetic-shift-right
          (bitwise-and ei1 (bitwise-not (bitwise-arithmetic-shift-left -1 ei3)))
          ei2)))
  
  (define (bitwise-copy-bit-field to start end from)
    (when (negative? start)
      (assertion-violation 'bitwise-copy-bit-field "cannot be negative" start))
    (when (negative? end)
      (assertion-violation 'bitwise-copy-bit-field "cannot be negative" end))   
    (unless (<= start end)
      (assertion-violation 'bitwise-copy-bit-field "must be less than or equal" start end))  
    (if (and (fixnum? to) (fixnum? from) (< -1 end 32))
        (fxcopy-bit-field to start end from)
        (bitwise-if 
          (bitwise-and 
            (bitwise-arithmetic-shift-left -1 start) 
            (bitwise-not (bitwise-arithmetic-shift-left -1 end)))
          (bitwise-arithmetic-shift-left from start)
          to)))
          
  ;; 移位量为 fixnum 时直接调用宿主原语（iron-059）：原来先经通用的 negative? 检查，
  ;; 再进入外面套着 exact 的 cond，小整数移位每次约 350ns，原语本身约 25ns。
  ;; 宿主的右移是算术右移（向下取整），负数无须经 bitwise-not。
  (define (bitwise-arithmetic-shift ei k)
    (if (fixnum? k)
        (cond
          [($fx>=? k 0) ($bignum-shift-left (->bignum ei) k)]
          [($fx=? k (least-fixnum)) (if (negative? ei) -1 0)]
          [else ($bignum-shift-right (->bignum ei) ($fx- k))])
        (exact
          (cond
            [(negative? ei)
              (if (negative? k)
                  -1
                  (floor (* ei (expt 2 k))))]
            ;; handle bignum right shifts
            [(negative? k) 0]
            [else ;; else &implementation-restriction
              (raise
                (condition
                  (make-implementation-restriction-violation)
                  (make-who-condition 'bitwise-arithmetic-shift)
                  (make-message-condition "bignum left shift not supported")
                  (make-irritants-condition (list ei k))))]))))
                  
  (define (bitwise-arithmetic-shift-left ei1 ei2)
    (if (and (fixnum? ei2) ($fx>=? ei2 0))
        ($bignum-shift-left (->bignum ei1) ei2)
        (begin
          (when (negative? ei2)
            (assertion-violation 'bitwise-arithmetic-shift-left "cannot be negative" ei2))
          (bitwise-arithmetic-shift ei1 ei2))))
    
  (define (bitwise-arithmetic-shift-right ei1 ei2)
    (if (and (fixnum? ei2) ($fx>=? ei2 0))
        ($bignum-shift-right (->bignum ei1) ei2)
        (begin
          (when (negative? ei2)
            (assertion-violation 'bitwise-arithmetic-shift-right "cannot be negative" ei2))
          (bitwise-arithmetic-shift ei1 (- ei2)))))
    
  (define (bitwise-rotate-bit-field n start end count)
    (when (negative? start)
      (assertion-violation 'bitwise-rotate-bit-field "cannot be negative" start))
    (when (negative? end)
      (assertion-violation 'bitwise-rotate-bit-field "cannot be negative" end))
    (when (negative? count)
      (assertion-violation 'bitwise-rotate-bit-field "cannot be negative" count))      
    (unless (<= start end)
      (assertion-violation 'bitwise-rotate-bit-field "start must be less than or equal to end" start end))
    (let ((width (- end start)))
      (if (positive? width)
          (let ((count (mod count width))
                (field (bitwise-bit-field n start end)))
             (bitwise-copy-bit-field n start end 
              (bitwise-ior 
                (bitwise-arithmetic-shift-left field count) 
                (bitwise-arithmetic-shift-right field (- width count)))))
          n)))
        
  (define (bitwise-reverse-bit-field x1 start end)
    (when (negative? start)
      (assertion-violation 'bitwise-reverse-bit-field "cannot be negative" start))
    (when (negative? end)
      (assertion-violation 'bitwise-reverse-bit-field "cannot be negative" end))  
    (unless (<= start end)
      (assertion-violation 'bitwise-reverse-bit-field "start must be less than or equal to end" start end))
    ;; 逆序由宿主一次完成（iron-059；原来逐位拼接，O(n²)）
    (bitwise-copy-bit-field x1 start end
      ($bignum-reverse-bits (->bignum (bitwise-bit-field x1 start end)) (- end start)))))
