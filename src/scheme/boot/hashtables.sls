#| License
Copyright (c) 2007-2016 Llewellyn Pritchard
All rights reserved.
This source code is subject to terms and conditions of the BSD License.
See docs/license.txt. |#

(library (boot hashtables)
  (export
    make-eq-hashtable
    make-eqv-hashtable
    make-hashtable
    
    hashtable-copy
    hashtable-keys
    hashtable-mutable?
    eqv-hash
    
    hashtable?
    
    hashtable-size
    hashtable-ref
    hashtable-set!
    hashtable-delete!
    hashtable-contains?
    hashtable-update!
    hashtable-clear!
    
    string-hash
    string-ci-hash
    symbol-hash
    equal-hash

    hashtable-equivalence-function
    hashtable-hash-function)
    
  (import 
    (except (ironscheme)
      make-eq-hashtable
      make-eqv-hashtable
      make-hashtable
      
      hashtable-copy
      hashtable-keys
      hashtable-mutable?
      eqv-hash
      
      hashtable?
      
      hashtable-size
      hashtable-ref
      hashtable-set!
      hashtable-delete!
      hashtable-contains?
      hashtable-update!
      hashtable-clear!
      
      string-hash
      string-ci-hash
      symbol-hash
      equal-hash

      hashtable-equivalence-function
      hashtable-hash-function)
    (except (ironscheme core) eqv-hash)
    (ironscheme contracts)
    (ironscheme unsafe))
    
  
  ;; 散列表是宿主对象，经 $hashtable-* 原语操作（IronScheme.Core.HostPrimitives，iron-056）
  (define (hashtable? obj)
    ($hashtable? obj))
    
  (define-syntax to-null
    (syntax-rules ()
      [(_ o)
        ($hashtable-to-null o)]))

  (define-syntax from-null
    (syntax-rules ()
      [(_ o)
        ($hashtable-from-null o)]))
  
  (define/contract make-eq-hashtable
    (case-lambda
      [()           (make-eq-hashtable 32)]
      [(k:fixnum)   ($make-eq-hashtable k)]))
    
  (define/contract make-eqv-hashtable
    (case-lambda
      [()           (make-eqv-hashtable 32)]
      [(k:fixnum)   (make-hashtable eqv-hash eqv? k)]))
      
  (define/contract make-hashtable
    (case-lambda
      [(hash equiv)
        (make-hashtable hash equiv 32)]
      [(hash:procedure equiv:procedure k:fixnum)
        ($make-hashtable hash equiv k)]))  
          
  (define/contract hashtable-copy
    (case-lambda
      [(ht)
        (hashtable-copy ht #f)]
      [(ht:hashtable mutable?:boolean)
        (if mutable?
            ($hashtable-clone ht)
            ($hashtable-readonly-copy ht))]))
            
  (define/contract (hashtable-keys ht:hashtable)
    ($hashtable-keys ht))  
      
  (define/contract (hashtable-mutable? ht:hashtable)
    ($hashtable-mutable? ht))
    
  (define (eqv-hash obj)
    ($object-hash obj))
        
  (define/contract (hashtable-equivalence-function ht:hashtable)
    (or ($hashtable-equivalence-function ht) eq?))
         
  ;; R6RS：eq 与 eqv 散列表返回 #f（iron-054；eqv 表内部用 eqv-hash，原来把它返回出去）
  (define/contract (hashtable-hash-function ht:hashtable)
    (let ((hf ($hashtable-hash-function ht)))
      (if (eq? hf eqv-hash) #f hf)))
  
  (define/contract (hashtable-size ht:hashtable)
    ($hashtable-count ht))
    
  (define/contract (hashtable-ref ht:hashtable key default)
    (let ((key (to-null key)))
      (let ((r ($hashtable-get ht key)))
        (if (or (not (null? r)) (hashtable-contains? ht key))
            r
            default))))
      
  (define/contract (hashtable-set! ht:hashtable key obj)
    ($hashtable-put! ht (to-null key) obj))

  (define/contract (hashtable-delete! ht:hashtable key)
    ($hashtable-remove! ht (to-null key)))
  
  (define/contract (hashtable-contains? ht:hashtable key)
    ($hashtable-contains? ht (to-null key)))
    
  (define/contract (hashtable-update! ht:hashtable key proc:procedure default)
    (let ((key (to-null key)))
      (hashtable-set!
        ht key
        (proc (hashtable-ref ht key default)))))

  (define/contract hashtable-clear!
    (case-lambda 
      [(ht)     
        (hashtable-clear! ht 32)]
      [(ht:hashtable k:fixnum)   
        ($hashtable-clear! ht)]))
      
  ;; R6RS 要求散列值为非负精确整数。原来取 .NET 的 GetHashCode：可能为负，且字符串的值每个进程不同。
  ;; 现在用 FNV-1a，只取决于内容（iron-054）。
  (define/contract (string-hash str:string)
    ($string-hash str))

  (define/contract (string-ci-hash str:string)
    ($string-ci-hash str))
        
  (define/contract (symbol-hash sym:symbol)
    ($fxand #x3FFFFFFF ($object-hash sym)))
    
  (define (equal-hash obj)
    (define-syntax clobber
      (syntax-rules ()
        [(_ a b)
          ($fx+ ($fx* a 33) b)]))
    (define ht (make-eq-hashtable))
    ($fxand 
      #x7FFFFFFF
      (let hash ((obj obj))
        (cond
          [(or (null? obj) (hashtable-contains? ht obj)) 0]
          [else 
            (hashtable-set! ht obj obj)
            (cond
              [(string? obj)
                (string-hash obj)]
              [(pair? obj)
                (clobber 
                  (hash (car obj))
                  (hash (cdr obj)))]
              [(vector? obj)
                (vector-fold-left 
                  (lambda (a v)
                    (clobber a (hash v)))
                  100 obj)]
              [(bytevector? obj)
                (bytevector-fold-left 
                  (lambda (a v)
                    (clobber a (eqv-hash v)))
                  200 obj)]
              [else 
                (eqv-hash obj)])])))))