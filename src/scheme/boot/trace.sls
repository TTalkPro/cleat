#| License
Copyright (c) 2007-2016 Llewellyn Pritchard
All rights reserved.
This source code is subject to terms and conditions of the BSD License.
See docs/license.txt. |#

(library (boot trace)
  (export
    make-traced-macro
    make-traced-procedure )
  (import 
    (ironscheme)
    (ironscheme contracts)
    (except (ironscheme core) make-traced-macro make-traced-procedure ))
    
  (define/contract make-traced-procedure 
    (case-lambda 
      [(name proc)
        (make-traced-procedure name proc #f)]
      [(name:symbol proc:procedure f)
        (traced-procedure name proc f)]))

  ;; 原为 C# 的 TraceClosure：调用前后把参数和结果经 trace-printer 打印到标准错误
  (define depth 0)

  (define (trace-print pp prefix arrow name x)
    (let ([err (current-error-port)])
      (put-string err (string-append prefix arrow (symbol->string name) "\n"))
      (put-string err (string-append
                        (let ([s (call-with-string-output-port (lambda (p) (pp x p)))])
                          (let loop ([n (string-length s)])
                            (if (and (> n 0) (memv (string-ref s (- n 1)) '(#\newline #\return)))
                                (loop (- n 1))
                                (substring s 0 n))))
                        "\n"))))

  (define (single x*)
    (if (and (pair? x*) (null? (cdr x*))) (car x*) x*))

  (define (traced-procedure name proc filter)
    (lambda args
      (let ([pp (if (symbol-bound? 'trace-printer) ((symbol-value 'trace-printer)) write)]
            [filt (lambda (x) (if filter (filter x) x))])
        (dynamic-wind
          (lambda () (set! depth (+ depth 1)))
          (lambda ()
            (let ([prefix (make-string depth #\|)])
              (trace-print pp prefix " -> " name (single (map filt args)))
              (call-with-values
                (lambda () (apply proc args))
                (lambda results
                  (trace-print pp prefix " <- " name (filt (single results)))
                  (apply values results)))))
          (lambda () (set! depth (- depth 1)))))))
    
  (define make-traced-macro 
    (lambda (name x) 
      (cond 
        [(procedure? x)  
         (make-traced-procedure name x syntax->datum)] 
        [(variable-transformer? x) 
         (make-variable-transformer 
           (make-traced-procedure name  
             (variable-transformer-procedure x) 
             syntax->datum))] 
        [else x]))))
