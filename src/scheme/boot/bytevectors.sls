#| License
Copyright (c) 2007-2016 Llewellyn Pritchard
All rights reserved.
This source code is subject to terms and conditions of the BSD License.
See docs/license.txt. |#

(library (boot bytevectors)
  (export
    endianness
    native-endianness
    bytevector?
    make-bytevector
    bytevector-length
    bytevector=?
    bytevector-fill!
    bytevector-copy!
    bytevector-copy
    
    bytevector-u8-ref
    bytevector-s8-ref
    bytevector-u8-set!
    bytevector-s8-set!
    
    bytevector->u8-list
    u8-list->bytevector
    
    bytevector-uint-ref
    bytevector-sint-ref
    bytevector-uint-set!
    bytevector-sint-set!
    
    bytevector->uint-list
    bytevector->sint-list
    uint-list->bytevector
    sint-list->bytevector
    
    bytevector-u16-ref
    bytevector-s16-ref
    bytevector-u16-native-ref
    bytevector-s16-native-ref
    bytevector-u16-set!
    bytevector-s16-set!
    bytevector-u16-native-set!
    bytevector-s16-native-set!
    
    bytevector-u32-ref
    bytevector-s32-ref
    bytevector-u32-native-ref
    bytevector-s32-native-ref
    bytevector-u32-set!
    bytevector-s32-set!
    bytevector-u32-native-set!
    bytevector-s32-native-set!
    
    bytevector-u64-ref
    bytevector-s64-ref
    bytevector-u64-native-ref
    bytevector-s64-native-ref
    bytevector-u64-set!
    bytevector-s64-set!
    bytevector-u64-native-set!
    bytevector-s64-native-set!
    
    bytevector-ieee-single-native-ref
    bytevector-ieee-single-ref
    bytevector-ieee-double-native-ref
    bytevector-ieee-double-ref
    
    bytevector-ieee-single-native-set!
    bytevector-ieee-single-set!
    bytevector-ieee-double-native-set!
    bytevector-ieee-double-set!
    
    string->utf8
    string->utf16
    string->utf32
    utf8->string
    utf16->string
    utf32->string
    
    bytevector-fold-left)
  (import 
    (ironscheme integrable)
    (ironscheme unsafe)
    (except (ironscheme) 
      bytevector-u16-ref
      bytevector-s16-ref
      bytevector-u16-native-ref
      bytevector-s16-native-ref
      bytevector-u16-set!
      bytevector-s16-set!
      bytevector-u16-native-set!
      bytevector-s16-native-set!
      
      bytevector-u32-ref
      bytevector-s32-ref
      bytevector-u32-native-ref
      bytevector-s32-native-ref
      bytevector-u32-set!
      bytevector-s32-set!
      bytevector-u32-native-set!
      bytevector-s32-native-set!
      
      bytevector-u64-ref
      bytevector-s64-ref
      bytevector-u64-native-ref
      bytevector-s64-native-ref
      bytevector-u64-set!
      bytevector-s64-set!
      bytevector-u64-native-set!
      bytevector-s64-native-set!
      
      bytevector-ieee-single-native-ref
      bytevector-ieee-double-native-ref
      bytevector-ieee-single-native-set!
      bytevector-ieee-double-native-set!
      
      native-endianness
      
      make-bytevector
      bytevector-length
      bytevector=?
      bytevector-fill!
      bytevector-copy!
      bytevector-copy
      bytevector-u8-ref
      bytevector-u8-set!
      bytevector-s8-ref
      bytevector-s8-set!
      bytevector->u8-list
      u8-list->bytevector
      string->utf8
      string->utf16
      string->utf32
      utf8->string
      utf16->string
      utf32->string
      bytevector-ieee-single-ref
      bytevector-ieee-single-set!
      bytevector-ieee-double-ref
      bytevector-ieee-double-set!
      uint-list->bytevector
      sint-list->bytevector
      bytevector->uint-list
      bytevector->sint-list
      
      bytevector-uint-ref
      bytevector-sint-ref
      bytevector-uint-set!
      bytevector-sint-set!
      
      bytevector-fold-left)
    (ironscheme contracts)
    (ironscheme typed))
  
     
  (define (native-endianness) 'little)
  
  ;; 宿主操作经 $ 原语（IronScheme.Core.HostPrimitives，iron-056）：$bytevector-ref 给出 fixnum，$bytevector-set! 收 fixnum
  (define utf8    ($utf-8-codec))
  (define utf16le ($make-utf16-codec #f))
  (define utf16be ($make-utf16-codec #t))
  (define utf32le ($make-utf32-codec #f))
  (define utf32be ($make-utf32-codec #t))
  
  (define (bignum? obj)
    ($bignum? obj))      
    
  (define (->bignum ei)
    (cond
      [(bignum? ei) ei]
      [(fixnum? ei) 
        ($fixnum->bignum ei)]
      [else
        (assertion-violation #f "not a exact integer" ei)]))
            
  (define (get-bytes enc str)
    ($codec-get-bytes enc str))

  (define (get-string enc bv)
    ($codec-get-string enc bv))
  
  (define (byte->sbyte b)
    (if ($fx>? b 127)
        ($fx- b 256)
        b))
  
  (define (->byte k)
    (unless (fixnum? k)
      (assertion-violation #f "not a fixnum" k))
    (when (or ($fx<? k -128) ($fx>? k 255))
      (assertion-violation #f "too big or small for octect or byte" k))
    (if ($fx<? k 0) ($fx+ k 256) k))
    
  (define (->fixnum b) b)
  
  (define/contract make-bytevector
    (case-lambda
      [(k:fixnum)
        ($make-bytevector k)]
      [(k:fixnum fill)
        (let ((bv (make-bytevector k)))
          (bytevector-fill! bv fill)
          bv)]))
          
  (define/contract (bytevector-fold-left combine:procedure nil bv:bytevector)
    (let ((len ($bytevector-length bv)))
      (let f ((i 0)(nil nil))
        (if ($fx=? i len)
            nil
            (f ($fx+ i 1) 
               (combine nil (->fixnum ($bytevector-ref bv i))))))))          
                  
  (define/contract (bytevector-length bv:bytevector)
    ($bytevector-length bv))  
    
  (define/contract (bytevector=? bv1:bytevector bv2:bytevector)
    (let: (((bv1 : Byte[]) bv1)((bv2 : Byte[]) bv2))
      (cond
        [(eq? bv1 bv2) #t]
        [(let ((bl ($bytevector-length bv1)))
          (if ($fx=? bl ($bytevector-length bv2))
              (let: f (((i : Int32) 0))
                (cond 
                  [($fx=? i bl) #t]
                  [($fx=? ($bytevector-ref bv1 i) ($bytevector-ref bv2 i))
                    (f ($fx+ i 1))]
                  [else #f]))
              #f))]
        [else #f])))
                      
  (define/contract (bytevector-fill! bv:bytevector fill)
    (let: (((bv : Byte[]) bv))
      (let ((fill (->byte fill))
            (k ($bytevector-length bv)))
        (let f ((i 0))
          (unless ($fx=? i k)
            ($bytevector-set! bv i fill)
            (f ($fx+ i 1)))))))
          
  (define/contract (bytevector-copy! bv1:bytevector s1:fixnum bv2:bytevector s2:fixnum len:fixnum)
    ($bytevector-copy! bv1 s1 bv2 s2 len))  
    
  (define/contract (bytevector-copy bv:bytevector)
    ($bytevector-clone bv))  
    
  (define/contract (bytevector-u8-ref bv:bytevector k:fixnum)
    (let: (((bv : Byte[]) bv)((k : Int32) k))
      (unless ($and? ($fx>=? k 0) ($fx<? k ($bytevector-length bv)))
        (assertion-violation 'bytevector-u8-ref "indexer out of bounds" bv k)) 
      ($bytevector-ref bv k)))
      
  (define/contract (bytevector-u8-set! bv:bytevector k:fixnum value:fixnum)
    (let: (((bv : Byte[]) bv)((k : Int32) k))
      (unless ($and? ($fx>=? k 0) ($fx<? k ($bytevector-length bv)))
        (assertion-violation 'bytevector-u8-set! "indexer out of bounds" bv k)) 
      ($bytevector-set! bv k (->byte value))))
   
  (define/contract (bytevector-s8-ref bv:bytevector k:fixnum)
    (let: (((bv : Byte[]) bv)((k : Int32) k))
      (unless ($and? ($fx>=? k 0) ($fx<? k ($bytevector-length bv)))
        (assertion-violation 'bytevector-s8-ref "indexer out of bounds" bv k)) 
      (byte->sbyte ($bytevector-ref bv k))))
      
  (define/contract (bytevector-s8-set! bv:bytevector k:fixnum value:fixnum)
    (let: (((bv : Byte[]) bv)((k : Int32) k))
      (unless ($and? ($fx>=? k 0) ($fx<? k ($bytevector-length bv)))
        (assertion-violation 'bytevector-s8-set! "indexer out of bounds" bv k)) 
      ($bytevector-set! bv k (->byte value))))
   
  (define/contract (bytevector->u8-list bv:bytevector)
    (let: (((bv : Byte[]) bv))
      (let ((l ($bytevector-length bv)))
        (let f ((i ($fx- l 1))(a '()))
          (if ($fxnegative? i)
              a
              (f ($fx- i 1) (cons (bytevector-u8-ref bv i) a)))))))
            
  (define/contract (u8-list->bytevector lst:list)
    (let* ((l (length lst))
           (bv (make-bytevector l)))
      (let f ((i 0)(lst lst))
        (if ($fx=? i l)
            bv
            (begin
              (bytevector-u8-set! bv i (car lst))
              (f ($fx+ i 1) (cdr lst)))))))
              
  (define/contract (bytevector-uint-ref bv:bytevector k:fixnum end:symbol size:fixnum)
    (when ($fxnegative? k)
      (assertion-violation 'bytevector-uint-ref "not a non-negative exact integer" k))
    (when ($fxnegative? size)
      (assertion-violation 'bytevector-uint-ref "not a non-negative exact integer" size))
    (let ((sb (make-bytevector size)))
      (bytevector-copy! bv k sb 0 size)
      (when (eq? end 'big)
        ($bytevector-reverse! sb))
      (case size
        [(1)
          ($bytevector-ref sb 0)]
        [(2 4 8)
          ($bytes->uint sb size)]
        [else
          (let ((data (make-bytevector (+ size 1))))
            (bytevector-copy! sb 0 data 0 size)
            ($bytes->bignum data))])))
                                                   
  (define/contract (bytevector-sint-ref bv:bytevector k:fixnum end:symbol size:fixnum)
    (when ($fxnegative? k)
      (assertion-violation 'bytevector-sint-ref "not a non-negative exact integer" k))
    (when ($fxnegative? size)
      (assertion-violation 'bytevector-sint-ref "not a non-negative exact integer" size))
    (let ((sb (make-bytevector size)))
      (bytevector-copy! bv k sb 0 size)
      (when (eq? end 'big)
        ($bytevector-reverse! sb))
      (case size
        [(1)
          (byte->sbyte ($bytevector-ref sb 0))]
        [(2 4 8)
          ($bytes->sint sb size)]
        [else
          ($bytes->bignum sb)])))
                           
  (define/contract (bytevector-uint-set! bv:bytevector k:fixnum n end:symbol size:fixnum) 
    (when ($fxnegative? k)
      (assertion-violation 'bytevector-uint-set! "not a non-negative exact integer" k))
    (when ($fxnegative? size)
      (assertion-violation 'bytevector-uint-set! "not a non-negative exact integer" size))
	  (when (negative? n)
	    (assertion-violation 'bytevector-uint-set! "not a non-negative exact integer" n))
    (case size
      [(1)
        ($bytevector-set! bv k (->byte n))]
      [(2 4 8)
        (let ((data ($uint->bytes n size)))
          (when (eq? end 'big)
            ($bytevector-reverse! data))
          (bytevector-copy! data 0 bv k size))]
      [else
        (let* ((data ($bignum->bytes (->bignum n)))
               (dl (bytevector-length data)))
          (when (> dl size)
            (assertion-violation 'bytevector-uint-set! "cannot fit number into size" dl size))
          (let ((b (make-bytevector size)))
            (bytevector-copy! data 0 b 0 dl)
            (when (eq? end 'big)
              ($bytevector-reverse! b))
            (bytevector-copy! b 0 bv k size)))])
    (void))
          
  (define/contract (bytevector-sint-set! bv:bytevector k:fixnum n end:symbol size:fixnum) 
    (when ($fxnegative? k)
      (assertion-violation 'bytevector-sint-set! "not a non-negative exact integer" k))
    (when ($fxnegative? size)
      (assertion-violation 'bytevector-sint-set! "not a non-negative exact integer" size))
    (case size
      [(1)
        ($bytevector-set! bv k (->byte n))]
      [(2 4 8)
        (let ((data ($sint->bytes n size)))
          (when (eq? end 'big)
            ($bytevector-reverse! data))
          (bytevector-copy! data 0 bv k size))]
      [else
        (let* ((data ($bignum->bytes (->bignum n)))
               (dl (bytevector-length data)))
          (when (> dl size)
            (assertion-violation 'bytevector-sint-set! "cannot fit number into size" dl size))
          (let ((b (make-bytevector size (if (negative? n) 255 0))))
            (bytevector-copy! data 0 b 0 dl)
            (when (eq? end 'big)
              ($bytevector-reverse! b))
            (bytevector-copy! b 0 bv k size)))])
    (void))  
    
  (define/contract (string->utf8 s:string)
    (get-bytes utf8 s))
    
  (define/contract string->utf16
    (case-lambda
      [(s)
        (string->utf16 s 'big)]
      [(s:string end)
        (case end
          [(big)    (get-bytes utf16be s)]
          [(little) (get-bytes utf16le s)]
          [else
            (assertion-violation 'string->utf16 "unknown endianness" end)])]))
               
  (define/contract string->utf32
    (case-lambda
      [(s)
        (string->utf32 s 'big)]
      [(s:string end)
        (case end
          [(big)    (get-bytes utf32be s)]
          [(little) (get-bytes utf32le s)]
          [else
            (assertion-violation 'string->utf32 "unknown endianness" end)])]))
            
  (define/contract (utf8->string bv:bytevector)
    (get-string utf8 bv))
    
  (define (trim-front bv k)
    (let ((d (make-bytevector ($fx- ($bytevector-length bv) k))))
      (bytevector-copy! bv k d 0 ($bytevector-length d))
      d))
      
  (define/contract utf16->string           
    (case-lambda
      [(bv end)
        (utf16->string bv end #f)]
      [(bv:bytevector end:symbol endman?)
        (if endman?
            (if (eq? end 'big)
                (get-string utf16be bv)
                (get-string utf16le bv))
            (let ((b0 ($bytevector-ref bv 0))
                  (b1 ($bytevector-ref bv 1)))
              (cond
                [(and ($fx=? #xff b0) ($fx=? b1 #xfe))
                  (utf16->string (trim-front bv 2) 'little #t)]
                [(and ($fx=? #xfe b0) ($fx=? b1 #xff))
                  (utf16->string (trim-front bv 2) 'big #t)]
                [else
                  (utf16->string bv end #t)])))]))
                
  (define/contract utf32->string           
    (case-lambda
      [(bv end)
        (utf32->string bv end #f)]
      [(bv:bytevector end:symbol endman?)
        (if endman?
            (if (eq? end 'big)
                (get-string utf32be bv)
                (get-string utf32le bv))
            (let ((b0 ($bytevector-ref bv 0))
                  (b1 ($bytevector-ref bv 1))
                  (b2 ($bytevector-ref bv 2))
                  (b3 ($bytevector-ref bv 3)))                
              (cond
                  [(and ($fx=? #xff b0) ($fx=? b1 #xfe) ($fxzero? b2) ($fxzero? b3))
                    (utf32->string (trim-front bv 4) 'little #t)]
                  [(and ($fxzero? b0) ($fxzero? b1) ($fx=? #xfe b2) ($fx=? b3 #xff))
                    (utf32->string (trim-front bv 4) 'big #t)]
                [else
                  (utf32->string bv end #t)])))]))  
                  
  (define/contract (uint-list->bytevector lst:list end:symbol size:fixnum)
    (let: (((lst : Cons) lst)((size : Int32) size))
      (when ($fxnegative? size)
        (assertion-violation 'uint-list->bytevector "invalid size" size))
      (let: (((bv : Byte[]) (make-bytevector ($fx* (length lst) size))))
        (let: f (((i : Int32) 0)((lst : Cons) lst))
          (if (null? lst)
              bv
              (begin
                (bytevector-uint-set! bv i ($car lst) end size)
                (f ($fx+ i size) ($cdr lst))))))))
              
  (define/contract (sint-list->bytevector lst:list end:symbol size:fixnum)
    (let: (((lst : Cons) lst)((size : Int32) size))
      (when ($fxnegative? size)
        (assertion-violation 'sint-list->bytevector "invalid size" size))
      (let: (((bv : Byte[]) (make-bytevector ($fx* (length lst) size))))
        (let: f (((i : Int32) 0)((lst : Cons) lst))
          (if (null? lst)
              bv
              (begin
                (bytevector-sint-set! bv i ($car lst) end size)
                (f ($fx+ i size) ($cdr lst))))))))
              
  (define/contract (bytevector->uint-list bv:bytevector end:symbol size:fixnum)
    (let: (((bv : Byte[]) bv)((size : Int32) size))
      (unless ($fxpositive? size)
        (assertion-violation 'bytevector->uint-list "invalid size" size))
      (let: f (((l : Int32) ($bytevector-length bv)) ((a : Cons) '()))
        (if ($fxzero? l)
            a
            (f ($fx- l size) (cons (bytevector-uint-ref bv ($fx- l size) end size) a))))))

  (define/contract (bytevector->sint-list bv:bytevector end:symbol size:fixnum)
    (let: (((bv : Byte[]) bv)((size : Int32) size))
      (unless ($fxpositive? size)
        (assertion-violation 'bytevector->sint-list "invalid size" size))
      (let: f (((l : Int32) ($bytevector-length bv)) ((a : Cons) '()))
        (if ($fxzero? l)
            a
            (f ($fx- l size) (cons (bytevector-sint-ref bv ($fx- l size) end size) a))))))
          
  (define (bytevector-ieee-single-ref bv k end)
    (let ((d (make-bytevector 4)))
      (bytevector-copy! bv k d 0 4)
      (when (eq? end 'big)
        ($bytevector-reverse! d))
      ($bytes->single d)))

  (define (bytevector-ieee-double-ref bv k end)
    (let ((d (make-bytevector 8)))
      (bytevector-copy! bv k d 0 8)
      (when (eq? end 'big)
        ($bytevector-reverse! d))
      ($bytes->double d)))
      
  (define (bytevector-ieee-single-set! bv k value end)
    (let ((data ($single->bytes value)))
      (when (eq? end 'big)
        ($bytevector-reverse! data))
      (bytevector-copy! data 0 bv k 4)))
      
  (define (bytevector-ieee-double-set! bv k value end)
    (let ((data ($double->bytes value)))
      (when (eq? end 'big)
        ($bytevector-reverse! data))
      (bytevector-copy! data 0 bv k 8)))
     
  (define (bytevector-u16-ref bytevector k endianness)
    (bytevector-uint-ref bytevector k endianness 2))
    
  (define (bytevector-s16-ref bytevector k endianness)     
    (bytevector-sint-ref bytevector k endianness 2))
    
  (define (bytevector-u16-native-ref bytevector k)     
    (bytevector-uint-ref bytevector k (native-endianness) 2))
    
  (define (bytevector-s16-native-ref bytevector k)     
    (bytevector-sint-ref bytevector k (native-endianness) 2))
    
  (define (bytevector-u16-set! bytevector k n endianness)     
    (bytevector-uint-set! bytevector k n endianness 2))
    
  (define (bytevector-s16-set! bytevector k n endianness)     
    (bytevector-sint-set! bytevector k n endianness 2))
    
  (define (bytevector-u16-native-set! bytevector k n)     
    (bytevector-uint-set! bytevector k n (native-endianness) 2))
    
  (define (bytevector-s16-native-set! bytevector k n)     
    (bytevector-sint-set! bytevector k n (native-endianness) 2))
     
  (define (bytevector-u32-ref bytevector k endianness)     
    (bytevector-uint-ref bytevector k endianness 4))
    
  (define (bytevector-s32-ref bytevector k endianness)     
    (bytevector-sint-ref bytevector k endianness 4))
    
  (define (bytevector-u32-native-ref bytevector k)     
    (bytevector-uint-ref bytevector k (native-endianness) 4))
    
  (define (bytevector-s32-native-ref bytevector k)     
    (bytevector-sint-ref bytevector k (native-endianness) 4))
    
  (define (bytevector-u32-set! bytevector k n endianness)     
    (bytevector-uint-set! bytevector k n endianness 4))
    
  (define (bytevector-s32-set! bytevector k n endianness)     
    (bytevector-sint-set! bytevector k n endianness 4))
    
  (define (bytevector-u32-native-set! bytevector k n)       
    (bytevector-uint-set! bytevector k n (native-endianness) 4))
    
  (define (bytevector-s32-native-set! bytevector k n)     
    (bytevector-sint-set! bytevector k n (native-endianness) 4))
     
  (define (bytevector-u64-ref bytevector k endianness)     
    (bytevector-uint-ref bytevector k endianness 8))
    
  (define (bytevector-s64-ref bytevector k endianness)     
    (bytevector-sint-ref bytevector k endianness 8))
    
  (define (bytevector-u64-native-ref bytevector k)     
    (bytevector-uint-ref bytevector k (native-endianness) 8))
    
  (define (bytevector-s64-native-ref bytevector k)     
    (bytevector-sint-ref bytevector k (native-endianness) 8))
    
  (define (bytevector-u64-set! bytevector k n endianness)     
    (bytevector-uint-set! bytevector k n endianness 8))
    
  (define (bytevector-s64-set! bytevector k n endianness)     
    (bytevector-sint-set! bytevector k n endianness 8))
    
  (define (bytevector-u64-native-set! bytevector k n)     
    (bytevector-uint-set! bytevector k n (native-endianness) 8))
    
  (define (bytevector-s64-native-set! bytevector k n)    
    (bytevector-sint-set! bytevector k n (native-endianness) 8))
    
  (define/contract (bytevector-ieee-single-native-ref bytevector k:fixnum)
    (unless ($fxzero? ($fxmod k 4))
      (assertion-violation 'bytevector-ieee-single-native-ref "must be multiple of 4" k))
    (bytevector-ieee-single-ref bytevector k (native-endianness)))
    
  (define/contract (bytevector-ieee-double-native-ref bytevector k:fixnum)     
    (unless ($fxzero? ($fxmod k 8))
      (assertion-violation 'bytevector-ieee-double-native-ref "must be multiple of 8" k))
    (bytevector-ieee-double-ref bytevector k (native-endianness)))
    
  (define/contract (bytevector-ieee-single-native-set! bytevector k:fixnum x)     
    (unless ($fxzero? ($fxmod k 4))
      (assertion-violation 'bytevector-ieee-single-native-set! "must be multiple of 4" k))
    (bytevector-ieee-single-set! bytevector k x (native-endianness)))
    
  (define/contract (bytevector-ieee-double-native-set! bytevector k:fixnum x)     
    (unless ($fxzero? ($fxmod k 8))
      (assertion-violation 'bytevector-ieee-double-native-set! "must be multiple of 8" k))
    (bytevector-ieee-double-set! bytevector k x (native-endianness))))