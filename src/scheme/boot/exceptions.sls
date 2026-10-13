#| License
Copyright (c) 2007-2016 Llewellyn Pritchard
All rights reserved.
This source code is subject to terms and conditions of the BSD License.
See docs/license.txt. |#

(library (boot exceptions)
  (export
    with-exception-handler
    guard
    raise
    else
    =>
    assertion-violation
    error
    raise-continuable
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
    (only (cleat scheme) import make-stacktrace-condition stacktrace-condition? display-stacktrace ungensym make-clr-condition
          with-clr-exception-handler)
    (cleat scheme unsafe)
    (cleat scheme contracts)
    (except (rnrs) 
      with-exception-handler
      raise 
      raise-continuable 
      assertion-violation 
      error
      values))
      
      
  ;; 固定个数的子句不建剩余参数表（cleat-076，照 skeg iron-067）
  (define values
    (case-lambda
      [(x) x]
      [(a b) ($make-multiple-values (vector a b))]
      [(a b c) ($make-multiple-values (vector a b c))]
      [x ($make-multiple-values (list->vector x))]))      
    
  ;; dynamic-wind 已删除（cleat-058，skeg 13 §3）：用 try … (finally …) 或 $try/finally

  ;; 异常模型（cleat-058，按 skeg 13 恢复 R6RS 语义）：处理器栈在 C# 里（Cleat.Scheme.ExceptionHandlers，线程静态）——
  ;; 栈顶在前的过程列表，缺省是只含默认处理器（把条件包成 SchemeException 抛出）的表。
  ;; 没经过处理器的 .NET 异常（C# 原语、宿主调用、throw 抛出的）回卷到 with-exception-handler 时包成 &clr-exception 条件
  ;; 在本处理器下 raise（skeg 13 §5；本仓库只在处理器这一级转换，不在每个宿主调用处包 try 区域）；续延逃逸与 SchemeException 原样重抛
  (define/contract (with-exception-handler handler:procedure thunk:procedure)
    (with-exception-handlers (cons handler ($current-handlers))
      (lambda ()
        (with-clr-exception-handler
          (lambda (ex) ($raise-clr-exception ex))
          thunk))))

  (define (with-exception-handlers new-handlers thunk)
    (let ((old ($current-handlers)))
      ($set-current-handlers! new-handlers)
      ($try/finally (thunk)
                    ($set-current-handlers! old))))

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
    (let ((obj (add-stacktrace obj)))
      (let ((handlers ($current-handlers)))
        (with-exception-handlers (cdr handlers)
          (lambda ()
            ((car handlers) obj)
            (raise
              (condition
                (make-non-continuable-violation)
                (make-message-condition "handler returned"))))))))

  (define (raise-continuable obj)
    (let ((obj (add-stacktrace obj)))
      (let ((handlers ($current-handlers)))
        (with-exception-handlers (cdr handlers)
          (lambda ()
            ((car handlers) obj))))))
          
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

  ;; 宿主异常进入 Scheme 的边界（Errors.RaiseClrException，skeg 13 §5.2）：
  ;; .NET 异常包成 &clr-exception + &who（异常类型名）+ &message 条件 raise，guard 直接接住
  (define (clr-exception-violation ex)
    (raise
      (condition
        (make-clr-condition ex)
        (make-who-condition (string->symbol ($type-name ex)))
        (make-message-condition ($exception-message ex)))))

  ;; 运行时（Cleat.Scheme）经 RuntimeHooks 回调这些过程，取代旧 Glue.il 对 boot dll 的直接引用
  ($set-runtime-hook! 'raise raise)
  ($set-runtime-hook! 'raise-continuable raise-continuable)
  ($set-runtime-hook! 'with-exception-handler with-exception-handler)
  ($set-runtime-hook! 'assertion-violation assertion-violation)
  ($set-runtime-hook! 'undefined-error undefined-error)
  ($set-runtime-hook! 'lexical-error lexical-error)
  ($set-runtime-hook! 'syntax-error syntax-error)
  ($set-runtime-hook! 'i/o-port-violation i/o-port-violation)
  ($set-runtime-hook! 'file-not-found-violation file-not-found-violation)
  ($set-runtime-hook! 'file-in-use-violation file-in-use-violation)
  ($set-runtime-hook! 'file-already-exists-violation file-already-exists-violation)
  ($set-runtime-hook! 'io-decoding-error io-decoding-error)
  ($set-runtime-hook! 'io-encoding-error io-encoding-error)
  ($set-runtime-hook! 'clr-exception-violation clr-exception-violation))