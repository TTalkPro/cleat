#| License
Copyright (c) 2007-2016 Llewellyn Pritchard
All rights reserved.
This source code is subject to terms and conditions of the BSD License.
See docs/license.txt. |#

(library (boot exceptions)
  (export
    guard
    raise
    else
    =>
    assertion-violation
    error
    raise-continuable
    dynamic-wind
    io-decoding-error
    io-encoding-error
    undefined-error
    lexical-error
    syntax-error
    file-not-found-violation
    i/o-port-violation
    file-already-exists-violation
    file-in-use-violation
    values)
  (import 
    ;(boot config) 
    (only (cleat scheme) import make-stacktrace-condition stacktrace-condition? display-stacktrace ungensym)
    (only (cleat scheme clr) clr-new)
    (cleat scheme unsafe)
    (cleat scheme contracts)
    (except (rnrs) 
      raise 
      raise-continuable 
      assertion-violation 
      error
      dynamic-wind
      values))
      
      
  (define (values . x)
    (if (and (not (null? x))
             (null? (cdr x)))
        (car x)
        ($make-multiple-values (list->vector x))))      
    
  (define (dynamic-wind in proc out)
    (in)
    ($try/finally (proc)
                  (out)))      

  ;; 异常模型（designs/15 §3，cleat-047）：没有处理器栈与 with-exception-handler。
  ;; raise 把对象包成 Cleat.Scheme.SchemeException 抛出（CLR throw）；捕获用核心形式
  ;; ($try/catch body '#f (lambda (c) …))（类型 #f 即 SchemeException，处理器收到其条件），guard 展开到它。
  ;; raise-continuable 等同 raise：元层不依赖"处理器返回后继续"的语义。
  (define (add-stacktrace con)
    (if (or (not (display-stacktrace))
            (not (serious-condition? con))
            (not (condition? con))
            (non-continuable-violation? con)
            (stacktrace-condition? con))
        con
        (let ((cc (simple-conditions con)))
          (apply condition
                 (append cc
                        (list (make-stacktrace-condition
                                ($stack-trace))))))))

  (define (raise obj)
    ($throw (clr-new Cleat.Scheme.SchemeException (add-stacktrace obj))))

  (define (raise-continuable obj)
    (raise obj))
          
  (define (assertion-violation who msg . irritants)
    (raise
      (condition
        (make-assertion-violation)
        (if who (make-who-condition who) (condition))
        (make-message-condition msg)
        (make-irritants-condition irritants))))

  (define (error who msg . irritants)
    (raise
      (condition
        (make-error)
        (if who (make-who-condition who) (condition))
        (make-message-condition msg)
        (make-irritants-condition irritants))))
        
  (define (io-decoding-error)
    (raise (make-i/o-decoding-error #f)))
            
  (define (io-encoding-error)
    (raise (make-i/o-encoding-error #f #f)))
        
  (define (undefined-error sym)
    (raise 
      (condition
        (make-undefined-violation)
        (make-message-condition "attempted to use undefined symbol")
        (make-irritants-condition (list (ungensym sym))))))
        
  (define (lexical-error msg what)
    (raise 
      (condition
        (make-lexical-violation)
        (make-message-condition msg)
        (make-irritants-condition (list what)))))           

  (define (syntax-error who msg form subform)
    (raise 
      (condition
        (make-who-condition who)
        (make-message-condition msg)
        (make-syntax-violation form subform))))
        
  (define (file-not-found-violation who msg filename)
    (raise
      (condition
        (make-assertion-violation)
        (if who (make-who-condition who) (condition))
        (make-message-condition msg)
        (make-i/o-file-does-not-exist-error filename))))  

  (define (i/o-port-violation who msg port)
    (raise
      (condition
        (make-assertion-violation)
        (if who (make-who-condition who) (condition))
        (make-message-condition msg)
        (make-i/o-port-error port))))

  (define (file-already-exists-violation who filename)
    (raise
      (condition
        (if who (make-who-condition who) (condition))
        (make-i/o-file-already-exists-error filename))))
        
  (define (file-in-use-violation who filename)
    (raise
      (condition
        (if who (make-who-condition who) (condition))
        (make-i/o-file-protection-error filename))))

  ;; 新运行时（IronScheme.Core）经 RuntimeHooks 回调这些过程，取代旧 Glue.il 对 boot dll 的直接引用
  ($set-runtime-hook! 'raise raise)
  ($set-runtime-hook! 'assertion-violation assertion-violation)
  ($set-runtime-hook! 'undefined-error undefined-error)
  ($set-runtime-hook! 'lexical-error lexical-error)
  ($set-runtime-hook! 'syntax-error syntax-error)
  ($set-runtime-hook! 'i/o-port-violation i/o-port-violation)
  ($set-runtime-hook! 'file-not-found-violation file-not-found-violation)
  ($set-runtime-hook! 'file-in-use-violation file-in-use-violation)
  ($set-runtime-hook! 'file-already-exists-violation file-already-exists-violation)
  ($set-runtime-hook! 'io-decoding-error io-decoding-error)
  ($set-runtime-hook! 'io-encoding-error io-encoding-error))