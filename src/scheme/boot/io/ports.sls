#| License
Copyright (c) 2007-2016 Llewellyn Pritchard
All rights reserved.
This source code is subject to terms and conditions of the BSD License.
See docs/license.txt. |#

(library (boot io ports)
  (export
    file-options
    
    buffer-mode?
    
    latin-1-codec
    utf-8-codec
    utf-16-codec
    eol-style
    native-eol-style
    
    &i/o-decoding
    make-i/o-decoding-error
    i/o-decoding-error?
    &i/o-encoding
    make-i/o-encoding-error
    i/o-encoding-error?
    i/o-encoding-error-char
    
    error-handling-mode
    make-transcoder
    native-transcoder
    transcoder-codec
    transcoder-eol-style
    transcoder-error-handling-mode
    
    bytevector->string
    string->bytevector
    
    eof-object
    eof-object?
    
    port?
    port-transcoder
    textual-port?
    binary-port?
    transcoded-port
    
    port-has-port-position?
    port-position
    port-has-set-port-position!?
    set-port-position!
    
    close-input-port
    close-output-port
    close-port
    call-with-port
    
    input-port?
    port-eof?
    open-file-input-port
    open-bytevector-input-port
    open-string-input-port
    standard-input-port
    current-input-port
    make-custom-binary-input-port
    make-custom-textual-input-port
    
    get-u8
    lookahead-u8
    get-bytevector-n
    get-bytevector-n!
    get-bytevector-some
    get-bytevector-all
    
    get-char
    lookahead-char
    get-string-n
    get-string-n!
    get-string-all
    get-line
    get-datum
    
    output-port?
    flush-output-port
    output-port-buffer-mode
    open-file-output-port
    open-bytevector-output-port
    call-with-bytevector-output-port
    open-string-output-port
    call-with-string-output-port
    
    standard-output-port
    standard-error-port
    
    current-output-port
    current-error-port
    
    make-custom-binary-output-port
    make-custom-textual-output-port
    
    put-u8
    put-bytevector
    
    put-char
    put-string
    put-datum
    
    open-file-input/output-port
    
    make-custom-binary-input/output-port
    make-custom-textual-input/output-port
    
    open-input-file
    open-output-file
    
    read
    
    open-output-string
    get-output-string
    
    annotation?
    annotation-expression
    annotation-source
    annotation-stripped)  
  (import 
    (ironscheme contracts)
    (ironscheme unsafe)
    (only (ironscheme reader) read-annotated)
    (only (boot expander compat) parameterize)
    (except (ironscheme) 
      parameterize
      latin-1-codec
      utf-8-codec
      utf-16-codec
      make-transcoder
      transcoder-codec
      transcoder-eol-style
      transcoder-error-handling-mode
      port-transcoder
      textual-port?
      binary-port?
      input-port?
      output-port?
      current-error-port
      current-input-port
      current-output-port
      native-transcoder
      open-input-file
      open-output-file
      close-input-port
      close-output-port
      read
      transcoded-port
      port-has-port-position?
      port-position
      port-has-set-port-position!?
      set-port-position!
      close-port
      port-eof?
      open-file-input-port
      call-with-port
      open-string-output-port 
      open-bytevector-input-port
      open-string-input-port
      make-custom-binary-input-port
      make-custom-textual-input-port
      get-u8
      lookahead-u8
      get-bytevector-n
      get-bytevector-n!
      get-bytevector-some
      get-bytevector-all
      get-char
      lookahead-char
      get-string-n
      get-string-n!
      get-string-all
      get-line
      flush-output-port
      output-port-buffer-mode
      open-bytevector-output-port
      call-with-bytevector-output-port
      make-custom-binary-output-port
      make-custom-textual-output-port
      put-u8
      put-bytevector
      put-char
      put-string
      make-custom-binary-input/output-port
      make-custom-textual-input/output-port
      
      textual-input-port?
      textual-output-port?      
      
      port? 
      call-with-string-output-port
      open-output-string
      get-output-string
      put-datum
      get-datum
      buffer-mode?
      native-eol-style
      standard-error-port
      standard-input-port
      standard-output-port))
  
  ;; 宿主操作全部经 $port-* 原语（IronScheme.Core.PortPrimitives，iron-056）：端口对象是 .NET 的 Stream / TextReader /
  ;; TextWriter，文本双向端口是 CustomTextReaderWriter；原语对双向端口自动取输入端或输出端。
  ;; 宿主抛出的异常用 with-clr-exception-handler 接住，转成 &i/o-port 条件。
  (define-syntax host-guard
    (syntax-rules ()
      [(_ (var handler) b b* ...)
       (with-clr-exception-handler
         (lambda (var) handler)
         (lambda () b b* ...))]))

  (define (codec? obj)
    ($codec? obj))
    
  (define (textual-input-port? obj)
    (and (input-port? obj)
         (textual-port? obj)))   
         
  (define (textual-output-port? obj)
    (and (output-port? obj)
         (textual-port? obj)))                        
    
  (define (latin-1-codec) 
    ($latin-1-codec))
    
  (define (utf-8-codec) 
    ($utf-8-codec))
    
  (define (utf-16-codec) 
    ($utf-16-codec))
  
  (define (transcoder? obj)
    ($transcoder? obj))
    
  (define/contract make-transcoder
    (case-lambda
      [(codec)
        (make-transcoder codec (native-eol-style))]
      [(codec eol)
        (make-transcoder codec eol 'replace)]
      [(codec:codec eol:symbol handling-mode:symbol)
        ($make-transcoder codec eol handling-mode)]))
                     
  (define (native-transcoder)
    ($native-transcoder))

  (define/contract (transcoder-codec tc:transcoder)
    ($transcoder-codec tc))

  (define/contract (transcoder-eol-style tc:transcoder)
    ($transcoder-eol-style tc))

  (define/contract (transcoder-error-handling-mode tc:transcoder)
    ($transcoder-error-handling-mode tc))
    
  (define (has-preamble? bv)
    (let ((b0 ($bytevector-ref bv 0))
          (b1 ($bytevector-ref bv 1)))
      ($or? ($and? ($fx=? b0 #xff) ($fx=? b1 #xfe))
            ($and? ($fx=? b0 #xfe) ($fx=? b1 #xff)))))

  (define/contract (port-transcoder port:port) 
    ($port-transcoder port))
  
  (define (textual-port? obj)
    ($textual-port? obj))
        
  (define (binary-port? obj)
    ($binary-port? obj))
    
  (define (binary-input-port? obj)
    ($binary-input-port? obj))
         
  (define (binary-output-port? obj)
    ($binary-output-port? obj))
    
  (define (input-port? obj)
    ($input-port? obj))
        
  (define (output-port? obj)
    ($output-port? obj))
  
  (define current-input-port 
    (make-parameter ($console-input-port) 
                    (lambda (x) 
                      (unless (and (textual-port? x)
                                   (input-port? x))
                        (assertion-violation 'current-input-port
                                             "not a textual input port"
                                             x))
                      x)))

  (define current-output-port 
    (make-parameter ($console-output-port) 
                    (lambda (x) 
                      (unless (and (textual-port? x)
                                   (output-port? x))
                        (assertion-violation 'current-output-port
                                             "not a textual output port"
                                             x))
                      x)))
                      
  (define current-error-port 
    (make-parameter ($console-error-port) 
                    (lambda (x) 
                      (unless (and (textual-port? x)
                                   (output-port? x))
                        (assertion-violation 'current-error-port
                                             "not a textual output port"
                                             x))
                      x)))
   
  (define (standard-error-port)
    ($standard-error-port))
    
  (define (standard-input-port)
    ($standard-input-port))

  (define (standard-output-port)
    ($standard-output-port))
  
  (define/contract (open-input-file filename:string)
    (unless (file-exists? filename)
      (file-not-found 'open-file-input-port filename))
    (host-guard (e (assertion-violation 'open-input-file "oops" filename))
      ($open-input-file filename)))

  (define/contract open-output-file 
    (case-lambda
      [(filename)
        (open-output-file filename #f)]
      [(filename:string append?:boolean)
        (host-guard (e (assertion-violation 'open-output-file "oops" filename))
          ($open-output-file filename append?))]))
  
  (define (get-input-port port)
    ($custom-port-input port))
    
  (define (get-output-port port)
    ($custom-port-output port))
    
  (define (close-input-port port)
    ($port-close-input port))
        
  (define (close-output-port port)
    ($port-close-output port))
        
  (define (annotation? obj)
    ($annotation? obj))
    
  (define/contract (annotation-expression anno:annotation)
    ($annotation-expression anno))

  (define/contract (annotation-source anno:annotation)
    ($annotation-source anno))

  (define/contract (annotation-stripped anno:annotation)
    ($annotation-stripped anno))
    
  (define/contract read    
    (case-lambda
      [()
        (read (current-input-port))]
      [(port:textual-input-port)
        (let ((r (read-annotated port)))
          (if (annotation? r)
              (annotation-stripped r)
              r))]))
              
  (define/contract (transcoded-port port:binary-port tc:transcoder)
    ($transcoded-port port tc))
          
  (define (port-has-port-position? port)
    ($port-has-position? port))
        
  (define (port-position port)
    ($port-position port))

  (define (port-has-set-port-position!? port)
    ($port-has-set-position? port))

  (define/contract (set-port-position! port pos:fixnum)        
    ($port-set-position! port pos))
        
  (define (close-port port)
    (when (input-port? port)
      (close-input-port port))
    (when (output-port? port)
      (close-output-port port)))        
      
  (define/contract (port-eof? port:input-port)
    (eof-object?
      (cond
        [(binary-port? port)
          (lookahead-u8 port)]
        [(textual-port? port)
          (lookahead-char port)]
        [else
          (assertion-violation 'port-eof? "not supported" port)])))
          
  (define (file-not-found who filename)
    (raise-continuable
      (condition
        (make-i/o-file-does-not-exist-error filename)
        (make-who-condition who))))
          
  (define/contract open-file-input-port
    (case-lambda
      [(filename)
        (open-file-input-port filename '())]
      [(filename options)
        (open-file-input-port filename options 'line)]
      [(filename options buffer-mode)
        (open-file-input-port filename options buffer-mode #f)]        
      [(filename:string options buffer-mode:buffer-mode tc)
        (unless (file-exists? filename)
          (file-not-found 'open-file-input-port filename))
        (host-guard (e (assertion-violation 'open-file-input-port "oops" filename))
          (let ((s ($open-file-input-port filename (eq? buffer-mode 'block))))
            (if tc
                (transcoded-port s tc)
                s)))]))

  (define/contract open-bytevector-input-port
    (case-lambda
      [(bv)
        (open-bytevector-input-port bv #f)]
      [(bv:bytevector tc)
        (let ((s ($open-bytevector-input-port bv)))
          (if tc
              (transcoded-port s tc)
              s))]))

  (define/contract (open-string-input-port str:string)
    ($open-string-input-port str))
    
  (define (proc/false? obj)
    (or (eq? obj #f)
        (procedure? obj)))  
    
  (define/contract 
      (make-custom-binary-input-port id:string 
                                     read:procedure 
                                     get-pos:proc/false 
                                     set-pos:proc/false 
                                     close:proc/false)
    ($make-custom-binary-input-port id read get-pos set-pos close))
                    
  (define/contract 
      (make-custom-textual-input-port id:string 
                                      read:procedure 
                                      get-pos:proc/false 
                                      set-pos:proc/false 
                                      close:proc/false)
    ($make-custom-textual-input-port id read get-pos set-pos close))
    
  (define (io-port-error who port)    
    (raise-continuable
      (condition
        (make-i/o-port-error port)
        (make-who-condition who))))
    
  (define/contract (get-u8 port:binary-input-port)
    (host-guard (e (io-port-error 'get-u8 port))
      (let ((c ($port-read-byte port)))
        (if (fx=? c -1) 
            (eof-object)
            c))))
      
  (define/contract (lookahead-u8 port:binary-input-port)
    (host-guard (e (io-port-error 'lookahead-u8 port))
      (if (port-has-set-port-position!? port)
          (let ((c ($port-read-byte port)))
            (if (fx=? c -1) 
                (eof-object)
                (begin 
                  (set-port-position! port (- (port-position port) 1))
                  c)))
          #f))) ;; check me

  (define-syntax read-buffer
    (syntax-rules ()
      [(_ port buffer start length)
        ($port-read-bytes port buffer start length)])) 
        
  (define (trim-buffer bv k)
    ($bytevector-truncate bv k))
      
  (define/contract (get-bytevector-n port:binary-input-port count:fixnum)
    (host-guard (e (io-port-error 'get-bytevector-n port))
      (let* ((buffer (make-bytevector count))
             (r (read-buffer port buffer 0 count)))
        (if (and ($fxzero? r) (not ($fxzero? count)))
            (eof-object)
            (let ((r (let f ((r r))
                      (if ($fx=? r count)
                          r
                          (let ((rr (read-buffer port buffer r ($fx- count r))))
                            (if ($fxzero? rr)
                                (if (not ($fxzero? r))
                                    r
                                    (eof-object))
                                (f ($fx+ rr r))))))))
              (if (eof-object? r)
                  r                              
                  (if (not ($fx=? r count))
                      (trim-buffer buffer r)
                      buffer)))))))
                      
  (define/contract (get-bytevector-n! port:binary-input-port bv:bytevector start:fixnum count:fixnum)
    (host-guard (e (io-port-error 'get-bytevector-n! port))
      (let* ((r (read-buffer port bv start count)))
        (if (and ($fxzero? r) (not ($fxzero? count)))
            (eof-object)
            (let f ((r r))
              (if ($fx=? r count)
                  r
                  (let ((rr (read-buffer port bv ($fx+ r start) ($fx- count r))))
                    (if ($fxzero? rr)
                        (if (not ($fxzero? r))
                            r
                            (eof-object))
                        (f ($fx+ rr r))))))))))
                        
  (define/contract (get-bytevector-some port:binary-input-port)
    (host-guard (e (io-port-error 'get-bytevector-some port))
      (let f ((a '()))
        (let ((c ($port-read-byte port)))
          (if ($fx=? c -1)
              (if (null? a)
                  (eof-object)
                  (u8-list->bytevector (reverse a)))
              (f (cons c a)))))))
        
  (define/contract (get-bytevector-all port:binary-input-port)
    (host-guard (e (io-port-error 'get-bytevector-all port))
      (let f ((a '()))
        (let ((c ($port-read-byte port)))
          (if ($fx=? c -1)
              (if (null? a)
                  (eof-object)
                  (u8-list->bytevector (reverse a)))
              (f (cons c a)))))))
              
  (define/contract (get-char port:textual-input-port)
    (host-guard (e (io-port-error 'get-char port))
      ;; 按码点：代理对组合为一个字符（iron-055，原来读到代理码元时 integer->char 报错）
      ($port-read-char port)))

  (define/contract (lookahead-char port:textual-input-port)
    (host-guard (e (io-port-error 'lookahead-char port))
      ($port-peek-char port)))

  (define/contract (get-string-n port:textual-input-port count:fixnum)
    (host-guard (e (io-port-error 'get-string-n port))
      ;; count 按字符（码点）计
      ($port-read-string port count)))

  (define/contract (get-string-n! port:textual-input-port str:string start:fixnum count:fixnum)
    (let* ((ss (get-string-n port count))
           (sl (string-length ss)))
      (do ((i 0 ($fx+ i 1)))
        (($fx=? i sl) sl)
        (string-set! str 
                     ($fx+ start i)
                     (string-ref ss i)))))
                     
  (define/contract (get-string-all port:textual-input-port)
    (host-guard (e (io-port-error 'get-string-all port))
      ;; R6RS：没有字符时为文件尾对象（iron-055；原来返回空串）
      ($port-read-all port)))

  (define/contract (get-line port:textual-input-port)
    (host-guard (e (io-port-error 'get-line port))
      ($port-read-line port)))
                
  (define (flush-output-port port)
    ($port-flush port))
        
  (define/contract (output-port-buffer-mode port:output-port)
    (if ($buffered-port? port)
        'block
        'line)) 
    
  (define/contract open-bytevector-output-port
    (case-lambda
      [()
        (open-bytevector-output-port #f)]
      [(tc)
        (let ((s ($open-bytevector-output-port)))
          (values (if tc (transcoded-port s tc) s)
                  (lambda () ($bytevector-output-port-extract s))))]))   
                      
  (define/contract call-with-bytevector-output-port
    (case-lambda
      [(p)
        (call-with-bytevector-output-port p #f)]
      [(p:procedure tc)
        (let-values (((s e) (open-bytevector-output-port tc)))
          (p s)
          (e))]))
          
  (define/contract 
      (make-custom-binary-output-port id:string 
                                      write:procedure 
                                      get-pos:proc/false 
                                      set-pos:proc/false 
                                      close:proc/false)
    ($make-custom-binary-output-port id write get-pos set-pos close))
                    
  (define/contract 
      (make-custom-textual-output-port id:string 
                                       write:procedure 
                                       get-pos:proc/false 
                                       set-pos:proc/false 
                                       close:proc/false)
    ($make-custom-textual-output-port id write get-pos set-pos close))          

  (define/contract (put-u8 port:binary-output-port byte:fixnum)
    (host-guard (e (io-port-error 'put-u8 port))
      ($port-write-byte port byte)))
            
  (define/contract put-bytevector 
    (case-lambda
      [(port bv)
        (put-bytevector port bv 0)]
      [(port bv start)
        (put-bytevector port bv start (fx- (bytevector-length bv) start))]
      [(port:binary-output-port bv:bytevector start:fixnum count:fixnum)
        ($port-write-bytes port bv start count)]))

  (define/contract (put-char port:textual-output-port chr:char)
    ($port-write-char port chr))
        
  (define/contract put-string 
    (case-lambda
      [(port str)
        (put-string port str 0)]
      [(port str start)
        (put-string port str start (fx- (string-length str) start))]
      [(port:textual-output-port str:string start:fixnum count:fixnum)
        ;; start、count 按码点（iron-055）：先按码点取子串，再交给宿主
        ($port-write-string port (substring str start (fx+ start count)))]))

  (define/contract 
      (make-custom-binary-input/output-port id:string 
                                            read:procedure
                                            write:procedure 
                                            get-pos:proc/false 
                                            set-pos:proc/false 
                                            close:proc/false)
    ($make-custom-binary-input/output-port id read write get-pos set-pos close))
                    
  (define/contract 
      (make-custom-textual-input/output-port id:string 
                                             read:procedure 
                                             write:procedure
                                             get-pos:proc/false 
                                             set-pos:proc/false 
                                             close:proc/false)
    ($make-custom-textual-input/output-port id read write get-pos set-pos close))
           
  ;; 原生行尾：Windows 为 crlf，其他平台为 lf（iron-054；原来总是 crlf，Linux 上的原生转码器端口因此写出 \r\n）
  (define (native-eol-style)
    ($native-eol-style))
  
  (define (buffer-mode? obj)
    (and (symbol? obj) 
         (memq obj '(none line block)) 
         #t))

  (define/contract (put-datum p:textual-output-port datum) 
    (write datum p))
  
  (define/contract (get-datum port:textual-input-port) 
    (read port))
    
  (define (get-output-string port)
    ($get-output-string port))
    
  (define (open-output-string)
    ($open-output-string))
  
  (define (open-string-output-port)
    (let ((p (open-output-string)))
      (values p (lambda () (get-output-string p)))))
      
  (define (port? obj)
    (or (textual-port? obj) 
        (binary-port? obj)))
    
  (define/contract (call-with-string-output-port proc:procedure)
    (let ((p (open-output-string)))
      (call-with-port p proc)
      (get-output-string p)))
      
  (define/contract (call-with-port port:port proc:procedure)
    (let ((r (proc port)))
      (close-port port)
      r)))
