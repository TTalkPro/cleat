;;; Copyright (c) 2006, 2007 Abdulaziz Ghuloum and Kent Dybvig
;;; Copyright (c) 2007-2016 Llewellyn Pritchard
;;; 
;;; Permission is hereby granted, free of charge, to any person obtaining a
;;; copy of this software and associated documentation files (the "Software"),
;;; to deal in the Software without restriction, including without limitation
;;; the rights to use, copy, modify, merge, publish, distribute, sublicense,
;;; and/or sell copies of the Software, and to permit persons to whom the
;;; Software is furnished to do so, subject to the following conditions:
;;; 
;;; The above copyright notice and this permission notice shall be included in
;;; all copies or substantial portions of the Software.
;;; 
;;; THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
;;; IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
;;; FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.  IN NO EVENT SHALL
;;; THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
;;; LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
;;; FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
;;; DEALINGS IN THE SOFTWARE. 

(library (boot expander main)
  (export
    trace-printer
    command-line
    load
    load/unsafe
    load-port
    load/args
    load/program
    load/unload
    ironscheme-build
    ironscheme-test
    emacs-mode?
    display-stacktrace
    compile
    compile-system-libraries
    compile->closure
    compile-file
    compile-library
    compile-program
    compile-whole-program
    compile-imported-libraries)
  (import 
    (rnrs base)
    (rnrs exceptions)
    (rnrs control)
    (rnrs io simple)
    (rnrs io ports)
    (rnrs lists)
    (rnrs hashtables)
    (rnrs bytevectors)
    (only (rnrs conditions) serious-condition? condition?)
    (only (rnrs exceptions) raise raise-continuable with-exception-handler)
    (boot expander compat)
    (boot expander internal)
    (boot expander library-manager)
    (boot expander core)
    (only (ironscheme core) get-command-line format compile-library-dll load-library-dll load-fasl compress-constants? constant-fold)
    (boot enums)
    (boot files)
    (only (ironscheme unsafe) $fx+ $fx- $throw $program-failed
          $console-foreground-color $set-console-foreground-color! $system-exception? $exception-message
          $path-change-extension $path-extension $string-lower $path-combine $directory-create
          $directory-exists? $path-full $path-trim-separator $string-replace $directory-files
          $procedure-form $procedure-arity)
    (except (ironscheme library) file-locator alternative-file-locator)
    (only (ironscheme) with-clr-exception-handler printf pretty-print initialize-default-printers debug-mode? compile-to-current-directory? serialize-port deserialize-port time time-it string=?))
    
  (define trace-printer (make-parameter pretty-print))
  (define display-stacktrace (make-parameter #f))
  (define command-line (make-parameter (get-command-line))) 
  (define emacs-mode? (make-parameter #f))
     
  (define (local-library-path filename)
    (cons (get-directory-name filename) (library-path)))

  (define (load/args filename . args)
    (with-guard
      (lambda ()
        (apply load-r6rs-top-level filename 'load args)
        (void))))
    
  (define (load/unload filename)
    (let ((libs #f)) 
      (dynamic-wind
        (lambda ()
          (set! libs (installed-libraries)))
        (lambda ()
          (load filename))
        (lambda ()          
          (for-each
            (lambda (lib)
              (unless (memq lib libs)
                (uninstall-library lib)))
            (installed-libraries))))))

  (define (load/program filename)
    (when (with-guard
            (lambda ()
              (load/unsafe filename)
              #f))
      ($program-failed)))

  (define (load/unsafe filename)
    (apply load-r6rs-top-level filename 'load (cdr (command-line)))
    (void))

  (define (load filename)
    (with-guard
      (lambda ()
        (load/unsafe filename))))
    
  (define (ironscheme-test)
    ;; 先找当前目录（新宿主在 IronScheme.Host/bin 下，测试在 src/scheme 中），再找宿主所在目录
    (let ((path (if (file-exists? "tests/r6rs/run.sps")
                    "tests/r6rs/run.sps"
                    (string-append (application-directory) "/tests/r6rs/run.sps"))))
      (time-it "R6RS test suite"
        (lambda () 
          (load path)))))
      
  (define (ironscheme-build)
     (time-it "the entire bootstrap process"
        (lambda () 
          (load "ironscheme-buildscript-nano.sps")))   ; 新编译器构建 boot/ironscheme.boot.dll（iron-032）
     (display "IronScheme build completed.\n"))
    
  (define foreground-color
    (case-lambda
      [()           (and (not (emacs-mode?)) ($console-foreground-color))]
      [(color)      (and (not (emacs-mode?)) ($set-console-foreground-color! color))])) 
      
  (define (system-exception? e)
    ($system-exception? e))

  ;; 接住宿主异常（iron-056：原 clr-guard 的展开，with-clr-exception-handler 是原语）
  (define-syntax host-guard
    (syntax-rules ()
      [(_ [var cls ...] b b* ...)
       (with-clr-exception-handler
         (lambda (var) (cond cls ...))
         (lambda () b b* ...))]))  
    
  (define (eval-top-level x)
    (host-guard [e [e (parameterize ((foreground-color 'Red)
                                    (current-output-port (current-error-port)))
                       (display "Unhandled CLR exception during evaluation:\n")
                       (display e)
                       (newline))]]
      (call/cc
        (lambda (k)
          (with-exception-handler
            (lambda (e)
              (let ((serious? (or (serious-condition? e) (system-exception? e) (not (condition? e)))))
                (when serious?
                  (parameterize ((foreground-color (if serious? 'Red 'Yellow))
                                 (current-output-port (current-error-port)))
                    (display "Unhandled exception during evaluation:\n")
                    (display e)
                    (newline)))
                (k)))
            (lambda ()
              (parameterize ([allow-library-redefinition #t])
                (eval x (interaction-environment)))))))))
            
  (define (eval-embedded x)
    (eval x (interaction-environment)))            
    
  (define compile-system-libraries
    (case-lambda
      [()
        (compile-system-libraries #t)] ; make constant compression default (saves about 45% for all the libraries)
      [(constant-compression?)
        ;; 当前目录有 system-libraries.ss 时在当前目录编译（新宿主的应用目录是其 bin 目录，不含库源码）
        (parameterize ((current-directory (if (file-exists? "system-libraries.ss") (current-directory) (application-directory))))
          (let ((path "system-libraries.ss"))
            (time-it "total compile time"
              (lambda ()
                (eval-top-level 
                  `(begin
                     (include ,path)
                     (compile ,path #f ,constant-compression?)))))))]))
               
  (define (with-guard f)
    (host-guard [e [e (parameterize ((current-output-port (current-error-port)))
                       (display "Unhandled CLR exception during evaluation:\n")
                       (display e)
                       (newline))]]
      (call/cc
        (lambda (k)
          (with-exception-handler
            (lambda (e)
              (let ((serious? (or (serious-condition? e) (system-exception? e) (not (condition? e)))))
                (when serious?
                  (parameterize ((current-output-port (current-error-port)))
                    (display "Unhandled exception during evaluation:\n")
                    (display e)
                    (newline)))
                (k)))
            f)))))
    
  (define compile
    (case-lambda
      [()
        (serialize-all compile-dll compile-core-expr)]
      [(filename)
        (compile filename #f)]
      [(filename gen-wrapper?)
        (compile filename gen-wrapper? #t)]        
      [(filename gen-wrapper? constant-compression?)
        (with-guard
          (lambda ()
            (parameterize [(compress-constants? constant-compression?)]
              (load-r6rs-top-level filename 'compile-dll))))]))
    
  (define (compile->closure filename)
    (load-r6rs-top-level filename 'closure))
    
  (define (load-port port . args)
    (with-guard
      (lambda ()
        (load-port-r6rs-top-level port #f 'load args)
        (void))))
       
  (define (load-port-r6rs-top-level port close-port? how args)
    (let ((x* (let f ()
                (let ((x (read-annotated port)))
                   (if (eof-object? x) 
                       '()
                       (cons x (f)))))))
      (when close-port?
        (close-input-port port))
      (case how
        ((closure)   (pre-compile-r6rs-top-level x*))
        ((load)      
          (parameterize ([command-line (cons (format "~a" port) (map (lambda (x) (format "~a" x)) args))])
            ((compile-r6rs-top-level x*)))))))
    
  (define (load-r6rs-top-level filename how . args)
    (if (fasl-file? filename)
        (case how
          ((load)
            (parameterize ([library-path (local-library-path filename)]
                           [command-line (cons filename (map (lambda (x) (format "~a" x)) args))])
              (load-fasl-file filename)))
          (else (assertion-violation how "a compiled .fasl file cannot be compiled again" filename)))
        (apply load-r6rs-top-level/source filename how args)))

  (define (load-r6rs-top-level/source filename how . args)
    (parameterize ([library-path (local-library-path filename)])
      (let ((x* 
             (call-with-input-file filename
               (lambda (p)
                 (let f ()
                   (let ((x (read-annotated p)))
                     (if (eof-object? x) 
                         '()
                         (cons x (f)))))))))
        (case how
          ((closure)   (pre-compile-r6rs-top-level x*))
          ((load)      
            (parameterize ([command-line (cons filename (map (lambda (x) (format "~a" x)) args))])
              ((compile-r6rs-top-level x*))))
          ((compile-dll)
  		      (compile-r6rs-top-level x*)
			      (serialize-all compile-dll compile-core-expr))))))

  (define fo (make-enumeration '(no-fail no-create no-truncate append)))
 
  (define (load-library-from-dll libname filename sk)
    (or (load-library-from-fasl libname filename sk)
        (load-library-from-dll* libname filename sk)))

  (define (load-library-from-dll* libname filename sk)
    (let ((dll-filename (compiled-library-exists? libname)))
      (if dll-filename
          (if (or (not (file-exists? filename))
                  (file-newer? dll-filename filename))
              (host-guard [e 
                      (e 
                        (display (format "WARNING: precompiled library ~a could not load. ~s\n" libname ($exception-message e)) 
                                 (current-error-port))
                        #f)]
                (let ((content (load-library-dll dll-filename)))
                  (and content (apply sk content))))
              (begin
                (display (format "WARNING: precompiled library ~a is out of date.\n" libname) 
                         (current-error-port))
                #f))
          #f)))
          
  ;; 13 元素的库描述列表 → 编译产物入口中重建它的表达式（代码部分包成无参 lambda）
  (define (library-content->expr content)
      (let ((v (list->vector content)))
        (vector-set! v 0 `',(vector-ref v 0)) ; id
        (vector-set! v 1 `',(vector-ref v 1)) ; name
        (vector-set! v 2 `',(vector-ref v 2)) ; version
        (vector-set! v 3 `',(vector-ref v 3)) ; imp*
        (vector-set! v 4 `',(vector-ref v 4)) ; vis*
        (vector-set! v 5 `',(vector-ref v 5)) ; inv*
        (vector-set! v 6 `',(vector-ref v 6)) ; subst
        (vector-set! v 7 `',(vector-ref v 7)) ; env
        (vector-set! v 8 `(lambda () ,(vector-ref v 8))) ; visit-code
        (vector-set! v 9 `(lambda () ,(vector-ref v 9))) ; invoke-code
        (vector-set! v 10 `(lambda () ,(vector-ref v 10))) ; guard-code
        (vector-set! v 11 `',(vector-ref v 11)) ; guard-req*
        (vector-set! v 12 `',(vector-ref v 12)) ; visible?
        (cons 'list (vector->list v))))

  (define (compile-dll libname content)
    (let ((filename (library-name->dll-name libname)))
      (display "compiling ")
      (display (get-filename filename))
      (newline)
      (if (compile-library-dll filename (library-content->expr content))
          (parameterize ((allow-library-redefinition #t))
            (try-load-from-file libname filename))
          #f)))

  ;;; ———————————————— compile-file / compile-library / compile-program（iron-037，designs/07-tooling-and-embedding.md §2）————————————————
  ;;; 编译产物 .fasl 是一个程序集（与库 dll 同一格式），入口返回
  ;;;   (ironscheme-fasl 1 libraries (库描述 …))         库文件：加载时逐个安装
  ;;;   (ironscheme-fasl 1 program ((id 名字) …) thunk)   顶层程序：核对依赖库的编译实例后调用 thunk
  ;;; 依赖库按 id 核对（与 try-load-from-file 一致）：库若重新展开（例如从源码重新加载），其位置与 id 都会变，编译产物不能再用。

  ;; 为真时，编译期间从源码加载的库也编译为各自源文件旁的 .fasl（Chez 的 compile-imported-libraries）
  (define compile-imported-libraries (make-parameter #f))

  (define (fasl-file-name filename)
    ($path-change-extension filename ".fasl"))

  (define (fasl-file? filename)
    (string=? ($string-lower ($path-extension filename)) ".fasl"))

  (define (strip x) (if (annotation? x) (annotation-stripped x) x))

  (define (read-source-forms filename)
    (call-with-input-file filename
      (lambda (p)
        (let f ()
          (let ((x (read-annotated p)))
            (if (eof-object? x) '() (cons x (f))))))))

  (define (library-form? x)
    (let ((x (strip x)))
      (and (pair? x) (eq? (strip (car x)) 'library))))

  (define (fasl-content? x kind)
    (and (pair? x) (eq? (car x) 'ironscheme-fasl) (pair? (cdr x)) (eqv? (cadr x) 1)
         (pair? (cddr x)) (eq? (caddr x) kind)))

  (define (write-fasl out expr)
    (when (file-exists? out) (delete-file out))
    (compile-library-dll out expr))

  ;; 编译期间从源码加载、且没有不旧于源文件的 .fasl 的库 → 各自源文件旁的 .fasl
  (define (compile-source-libraries! except)
    (for-each
      (lambda (p)
        (let* ((name (car p)) (src (cdr p)) (out (fasl-file-name src)))
          (unless (or (member name except)
                      (fasl-file? src)
                      (and (file-exists? out) (file-newer? out src)))
            (write-fasl out `(list 'ironscheme-fasl 1 'libraries
                                   (list ,(library-content->expr (library-content name compile-core-expr))))))))
      (source-libraries)))

  (define compile-library
    (case-lambda
      [(filename) (compile-library filename (fasl-file-name filename))]
      [(filename out)
        (parameterize ([library-path (local-library-path filename)])
          (let ((forms (read-source-forms filename)))
            (when (null? forms) (assertion-violation 'compile-library "no library in file" filename))
            (let ((names
                   (map (lambda (x)
                          (unless (library-form? x)
                            (assertion-violation 'compile-library "not a library form" (strip x) filename))
                          (let ((name #f))
                            (parameterize ((allow-library-redefinition #t))
                              ((current-library-expander) x filename (lambda (n) (set! name n))))
                            name))
                        forms)))
              (when (compile-imported-libraries) (compile-source-libraries! names))
              (write-fasl out `(list 'ironscheme-fasl 1 'libraries
                                     (list ,@(map (lambda (n) (library-content->expr (library-content n compile-core-expr))) names))))
              out)))]))

  (define compile-program
    (case-lambda
      [(filename) (compile-program filename (fasl-file-name filename))]
      [(filename out)
        (parameterize ([library-path (local-library-path filename)])
          (let-values (((lib* invoke-code macro* export-subst export-env)
                        (top-level-expander (read-source-forms filename))))
            (when (compile-imported-libraries) (compile-source-libraries! '()))
            (write-fasl out `(list 'ironscheme-fasl 1 'program
                                   ',(map (lambda (lib) (let ((s (library-spec lib))) (list (car s) (cadr s)))) lib*)
                                   (lambda () ,(compile-core-expr invoke-code))))
            out))]))

  ;; 文件以 library 形式开头时按库文件编译，否则按顶层程序编译（与 load 对源文件的处理一致）
  ;; (compile-file 文件 [输出] ['target 目标])：目标缺省为本宿主（iron-057；现在只有 clr，未知目标报错）
  (define (compile-file filename . args)
    (let-values (((out opts) (if (and (pair? args) (string? (car args)))
                                 (values (car args) (cdr args))
                                 (values (fasl-file-name filename) args))))
      (let ((target (let loop ((opts opts))
                      (cond
                        ((null? opts) #f)
                        ((and (eq? (car opts) 'target) (pair? (cdr opts))) (cadr opts))
                        (else (assertion-violation 'compile-file "invalid option" (car opts)))))))
        (define (compile)
          (let ((forms (read-source-forms filename)))
            (if (and (pair? forms) (library-form? (car forms)))
                (compile-library filename out)
                (compile-program filename out))))
        (if target
            (parameterize (((symbol-value 'current-compile-target) target)) (compile))
            (compile)))))

  ;; (load "x.fasl")：顶层程序运行；库安装（已安装的同名库被替换）
  (define (load-fasl-file filename)
    (load-fasl-content (load-fasl filename) filename))

  ;; 已加载的编译产物入口返回值 → 安装其中的库或运行程序（宿主运行嵌入的应用时经钩子调用，iron-036）
  (define (load-fasl-content content filename)
      (cond
        ((fasl-content? content 'program)
          (let ((deps (cadddr content)) (thunk (car (cddddr content))))
            (for-each
              (lambda (d)
                (let ((lib (find-library-by-name (cadr d))))
                  (unless (eq? (car (library-spec lib)) (car d))
                    (assertion-violation 'load
                      "compiled program requires a different compilation of a library; recompile the program (and the library)"
                      filename (cadr d)))
                  (invoke-library lib)))
              deps)
            (thunk)))
        ((fasl-content? content 'libraries)
          (for-each
            (lambda (desc)
              (unless (install-library-content (cadr desc) filename desc)
                (assertion-violation 'load "compiled library is out of date with its dependencies; recompile it" filename (cadr desc))))
            (cadddr content)))
        (else (assertion-violation 'load "not an IronScheme compiled file" filename))))

  ;; 整个程序编译到 out-dir（自包含发布，iron-036）：程序展开期间从源码加载的每个库一个 libN.fasl（按安装顺序，
  ;; 依赖在前），程序本身 program.fasl，清单 ironscheme.app 每行 "library 文件" 或 "program 文件"。
  ;; 展开时不使用已有的编译产物（.fasl、库 dll），保证每个库都从源码展开、都能序列化。
  (define (compile-whole-program filename out-dir)
    (define (out name) ($path-combine out-dir name))
    ($directory-create out-dir)
    (parameterize ([library-path (local-library-path filename)]
                   [current-precompiled-library-loader (lambda (libname filename sk) #f)])
      (let-values (((lib* invoke-code macro* export-subst export-env)
                    (top-level-expander (read-source-forms filename))))
        (let loop ((libs (source-libraries)) (i 0) (manifest '()))
          (if (pair? libs)
              (let ((file (string-append "lib" (number->string i) ".fasl")))
                (write-fasl (out file) `(list 'ironscheme-fasl 1 'libraries
                                              (list ,(library-content->expr (library-content (caar libs) compile-core-expr)))))
                (loop (cdr libs) (+ i 1) (cons (string-append "library " file) manifest)))
              (begin
                (write-fasl (out "program.fasl")
                            `(list 'ironscheme-fasl 1 'program
                                   ',(map (lambda (lib) (let ((s (library-spec lib))) (list (car s) (cadr s)))) lib*)
                                   (lambda () ,(compile-core-expr invoke-code))))
                (let ((m (out "ironscheme.app")))
                  (when (file-exists? m) (delete-file m))
                  (call-with-output-file m
                    (lambda (p)
                      (for-each (lambda (line) (display line p) (newline p)) (reverse (cons "program program.fasl" manifest))))))
                out-dir))))))

  (define (install-library-content name filename desc)
    (parameterize ((allow-library-redefinition #t)
                   (current-precompiled-library-loader (lambda (libname fn sk) (apply sk desc))))
      (try-load-from-file name filename)))

  ;; 导入库时：源文件旁有不旧于它的 .fasl 就加载它（Chez 的编译产物查找）；依赖不符时返回 #f，退回源码
  (define (load-library-from-fasl libname filename sk)
    (let ((fasl (and (not (fasl-file? filename)) (fasl-file-name filename))))
      (and fasl
           (file-exists? fasl)
           (file-newer? fasl filename)
           (let ((content (host-guard [e (e (display (format "WARNING: compiled library ~a could not load. ~s\n" fasl ($exception-message e))
                                                    (current-error-port))
                                           #f)]
                            (load-fasl fasl))))
             (and (fasl-content? content 'libraries)
                  (let ((desc (find (lambda (d) (equal? (cadr d) libname)) (cadddr content))))
                    (and desc (apply sk desc))))))))
      
  ;;; ———————————————— REPL 的补全与提示（iron-038，designs/07-tooling-and-embedding.md §4）————————————————
  ;;; 宿主的行编辑器经符号 repl-complete、repl-describe 调用。

  (define (string-prefix? p s)
    (and (<= (string-length p) (string-length s))
         (string=? p (substring s 0 (string-length p)))))

  (define (library-name->string name)
    (call-with-string-output-port
      (lambda (p)
        (put-char p #\()
        (let f ((xs name) (first? #t))
          (unless (null? xs)
            (unless first? (put-char p #\space))
            (display (car xs) p)
            (f (cdr xs) #f)))
        (put-char p #\)))))

  ;; 库搜索路径下的 .sls 文件 → 库名（按 library-name->file-name 的规则反解：目录分隔为空格，%XX 解码）
  (define library-file-names #f)

  (define (decode-component s)
    (let ((n (string-length s)))
      (let f ((i 0) (acc '()))
        (cond
          ((= i n) (list->string (reverse acc)))
          ((and (char=? (string-ref s i) #\%) (< (+ i 2) n))
           (let ((code (string->number (substring s (+ i 1) (+ i 3)) 16)))
             (if code
                 (f (+ i 3) (cons (integer->char code) acc))
                 (f (+ i 1) (cons #\% acc)))))
          (else (f (+ i 1) (cons (string-ref s i) acc)))))))

  (define (scan-library-files)
    (unless library-file-names
      (set! library-file-names
        (apply append
          (map
            (lambda (dir)
              (if ($directory-exists? dir)
                  (let ((full ($path-trim-separator ($path-full dir))))
                    (map
                      (lambda (file)
                        (let* ((rel (substring file (+ (string-length full) 1) (string-length file)))
                               (rel ($string-replace rel "\\" "/"))
                               (base (cond
                                       ((string-suffix? ".ironscheme.sls" rel) (substring rel 0 (- (string-length rel) 15)))
                                       (else (substring rel 0 (- (string-length rel) 4))))))
                          (library-name->string
                            (map (lambda (c) (string->symbol (decode-component c)))
                                 (split-path base)))))
                      ($directory-files full "*.sls")))
                  '()))
            (library-path)))))
    library-file-names)

  (define (string-suffix? x s)
    (and (<= (string-length x) (string-length s))
         (string=? x (substring s (- (string-length s) (string-length x)) (string-length s)))))

  (define (split-path s)
    (let f ((i 0) (start 0) (acc '()))
      (cond
        ((= i (string-length s)) (reverse (cons (substring s start i) acc)))
        ((char=? (string-ref s i) #\/) (f (+ i 1) (+ i 1) (cons (substring s start i) acc)))
        (else (f (+ i 1) start acc)))))

  ;; prefix 开头的名字（交互环境中的绑定）；library? 为真时是写成 "(a b c)" 的库名
  (define (repl-complete prefix library?)
    (if library?
        (filter (lambda (s) (string-prefix? prefix s))
                (append (map library-name->string (installed-libraries #t)) (scan-library-files)))
        (filter (lambda (s) (string-prefix? prefix s))
                (map symbol->string (interaction-environment-symbols)))))

  ;; 名字的提示：过程 → 各子句的形参（编译器生成的 Form，或按参数个数）；语法关键字与其他值为 #f
  (define (repl-describe name)
    (let ((sym (string->symbol name)))
      (and (memq sym (interaction-environment-symbols))
           (call/cc
             (lambda (k)
               (with-exception-handler
                 (lambda (e) (k #f))
                 (lambda ()
                   (let ((v (eval sym (interaction-environment))))
                     (and (procedure? v) (procedure-hint sym v))))))))))

  (define (procedure-hint sym p)
    (define (show x) (call-with-string-output-port (lambda (o) (display x o))))
    (define (placeholders n rest?)
      (let f ((i 0))
        (cond
          ((< i n) (cons '_ (f (+ i 1))))
          (rest? '_)
          (else '()))))
    (let ((form ($procedure-form p)))
      (if (pair? form)
          (join-hints (map (lambda (f) (show (cons sym (cdr f)))) form))
          (let ((arity ($procedure-arity p)))
            (define (one a)
              (cond
                ((and (integer? a) (exact? a)) (show (cons sym (placeholders a #f))))
                ((real? a) (show (cons sym (placeholders (exact (floor a)) #t))))
                (else #f)))
            (cond
              ((pair? arity) (join-hints (filter values (map one arity))))
              ((null? arity) #f)
              (else (one arity)))))))

  ;; 多个子句以 " | " 连接；超过两个时只显示首尾两个，中间写 "…"
  (define (join-hints xs)
    (cond
      ((null? xs) #f)
      ((null? (cdr xs)) (car xs))
      ((null? (cddr xs)) (string-append (car xs) " | " (cadr xs)))
      (else (string-append (car xs) " | … | " (let f ((l xs)) (if (null? (cdr l)) (car l) (f (cdr l))))))))

  (current-precompiled-library-loader load-library-from-dll)
  
  (initialize-default-printers)
  
  ; hacks to get compiled libraries playing nicely
  (set-symbol-value! 'list list)
  (set-symbol-value! 'values values)    
  
  (set-symbol-value! 'load load)
  (set-symbol-value! 'compile compile)
  (set-symbol-value! 'compile->closure compile->closure)
  (set-symbol-value! 'eval-r6rs eval-top-level)
  (set-symbol-value! 'eval-embedded eval-embedded)
  (set-symbol-value! 'int-env-syms interaction-environment-symbols)
  (set-symbol-value! 'repl-complete repl-complete)
  (set-symbol-value! 'load-fasl-content load-fasl-content)
  (set-symbol-value! 'repl-describe repl-describe)
  (set-symbol-value! 'expanded2core expanded->core)
  
  (set-symbol-value! 'trace-printer trace-printer)
  ;; 构建脚本（ironscheme-buildscript-nano.sps）在新系统上运行时经它取运行中系统的原语位置表（iron-030）
  (set-symbol-value! 'current-primitive-locations current-primitive-locations)
  (set-symbol-value! 'pretty-print pretty-print)
  (set-symbol-value! 'assertion-violation assertion-violation)
  (set-symbol-value! 'raise raise)
  (set-symbol-value! 'raise-continuable raise-continuable)
  (set-symbol-value! 'with-exception-handler with-exception-handler)
  (set-symbol-value! 'emacs-mode? emacs-mode?)
  (set-symbol-value! 'stacktrace-enable? display-stacktrace)
  
  (file-options-constructor (enum-set-constructor fo))

  (compile-to-current-directory? #t)
  (library-path (get-library-paths))
  
  (library-extensions (cons ".ironscheme.sls" (library-extensions)))

  (constant-fold)
  
  (interaction-environment (new-interaction-environment))
  
  (set-symbol-value! 'r6rs-loaded #t))