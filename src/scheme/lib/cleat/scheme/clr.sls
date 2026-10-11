#| License
Copyright (c) 2007-2016 Llewellyn Pritchard
All rights reserved.
This source code is subject to terms and conditions of the BSD License.
See docs/license.txt. |#

(library (cleat scheme clr)
  (export
    clr-namespaces
    clr-type-of
    clr-static-event-add!
    clr-static-event-remove!
    clr-event-add!
    clr-event-remove!
    clr-using
    clr-reference
    clr-is
    clr-cast
    clr-call
    clr-static-call
    clr-field-get
    clr-field-set!
    clr-static-field-get
    clr-static-field-set!
    clr-prop-get
    clr-prop-set!
    clr-static-prop-get
    clr-static-prop-set!
    clr-indexer-get
    clr-indexer-set!
    clr-new
    clr-new-array
    clr-guard
    clr-exception-of?
    pinvoke-call)
  (import
    (rnrs)
    (only (cleat scheme) clr-condition? clr-condition-exception)
    (cleat scheme clr helpers)
    (cleat scheme clr internal))
    
  (define-syntax clr-type-of
    (syntax-rules ()
      [(_ type) 
        (clr-type-of-internal 'type)]))    
    
  (define-syntax clr-namespaces
    (syntax-rules ()
      [(_) 
        (clr-namespaces-internal)]))    
    
  ;; clr-guard（cleat-058，skeg）：try 上的宏——接住 .NET 异常（var 绑定原异常）交给 cond 子句；与 guard / try 同一套处理器机制
  (define-syntax clr-guard
    (syntax-rules ()
      [(_ [var cls ...] b b* ...)
        (try b b* ...
          (catch System.Exception var
            (cond cls ...)))]))
        
  ;; ((clr-exception-of? Type) e)：e 是宿主异常包成的 &clr-exception 条件，且异常是 Type（含子类）（cleat-058，skeg）。
  ;;   (guard (e [((clr-exception-of? System.IO.IOException) e) …]) (clr-static-call File ReadAllText "x"))
  (define-syntax clr-exception-of?
    (syntax-rules ()
      [(_ type)
        (lambda (e)
          (and (clr-condition? e)
               (clr-is type (clr-condition-exception e))))]))

  (define-syntax pinvoke-call
    (lambda (x)
      (define (->string id) 
        (symbol->string (syntax->datum id)))
      (syntax-case x ()
        [(_ lib proc ret (args ...))
          (with-syntax (((args ...) (map ->string #'(lib proc ret args ...))))
            #'(pinvoke-call-internal args ...))])))

  (define-syntax clr-using
    (lambda (e)
      (syntax-case e ()
        [(_ namespace)
         #'(define using (clr-using-internal 'namespace))])))

  (define-syntax clr-reference
    (lambda (e)
      (syntax-case e ()
        [(_ assname)
         #'(define reference (clr-reference-internal 'assname))])))

  (define-syntax clr-is
    (lambda (e)
      (syntax-case e ()
        [(_ type arg)
         #'(clr-is-internal 'type arg)])))

  (define-syntax clr-call
    (lambda (e)
      (syntax-case e ()
        [(_ type member instance args ...)
         #'(clr-call-internal 'type 'member instance args ...)])))

  (define-syntax clr-field-get
    (lambda (e)
      (syntax-case e ()
        [(_ type member instance)
         #'(clr-field-get-internal 'type 'member instance)])))

  (define-syntax clr-field-set!
    (lambda (e)
      (syntax-case e ()
        [(_ type member instance value)
         #'(clr-field-set!-internal 'type 'member instance value)])))

  (define-syntax clr-static-field-get
    (syntax-rules ()
      [(_ type member)
       (clr-field-get type member '())]))

  (define-syntax clr-static-field-set!
    (syntax-rules ()
      [(_ type member value)
       (clr-field-set! type member '() value)]))

  (define-syntax clr-static-call
    (syntax-rules ()
      [(_ type member args ...)
       (clr-call type member '() args ...)]))

  (define-syntax clr-static-event-add!
    (syntax-rules ()
      [(_ type event handler)
       (clr-event-add! type event '() handler)]))

  (define-syntax clr-static-event-remove!
    (syntax-rules ()
      [(_ type event handler)
       (clr-event-remove! type event '() handler)]))
       
  (define-syntax clr-event-add!
    (lambda (e)
      (syntax-case e ()
        [(_ type event-name instance handler)
         #`(clr-call-internal
            'type
            '#,(prefix-syntax "add_" #'event-name)
            instance handler)])))

  (define-syntax clr-event-remove!
    (lambda (e)
      (syntax-case e ()
        [(_ type event-name instance handler)
         #`(clr-call-internal
            'type
            '#,(prefix-syntax "remove_" #'event-name)
            instance handler)])))

  (define-syntax clr-prop-get
    (lambda (e)
      (syntax-case e ()
        [(_ type prop-name instance args ...)
         #`(clr-call-internal
            'type
            '#,(prefix-syntax "get_" #'prop-name)
            instance args ...)])))

  (define-syntax clr-prop-set!
    (lambda (e)
      (syntax-case e ()
        [(_ type prop-name instance args ...)
         #`(clr-call-internal
            'type
            '#,(prefix-syntax "set_" #'prop-name)
            instance args ...)])))

  (define-syntax clr-indexer-get
    (syntax-rules ()
      [(_ type instance arg args* ...)
       (clr-prop-get type Item instance arg args* ...)]))

  (define-syntax clr-indexer-set!
    (syntax-rules ()
      [(_ type instance arg args* ... value)
       (clr-prop-set! type Item instance arg args* ... value)]))

  (define-syntax clr-static-prop-get
    (syntax-rules ()
      [(_ type prop-name args ...)
       (clr-prop-get type prop-name '() args ...)]))

  (define-syntax clr-static-prop-set!
    (syntax-rules ()
      [(_ type prop-name args ...)
       (clr-prop-set! type prop-name '() args ...)]))

  (define-syntax clr-new
    (lambda (e)
      (syntax-case e ()
        [(_ type args ...)
         #'(clr-new-internal 'type args ...)])))

  (define-syntax clr-new-array
    (lambda (e)
      (syntax-case e ()
        [(_ type size)
          #'(clr-new-array-internal 'type size)])))

  (define-syntax clr-cast
    (lambda (e)
      (syntax-case e ()
        [(_ type arg)
         #'(clr-cast-internal 'type arg)]))))
