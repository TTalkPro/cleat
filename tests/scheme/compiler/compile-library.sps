;;; iron-027 验收：用新编译器编译文本种子中的库 (boot lists)：Lcore → Lcil → 发射器 → dll，
;;; 在当前进程（stage0）中加载执行，之后经 eval 调用的列表过程即为编译后的版本。
(import (rnrs) (rnrs eval) (cleat scheme) (cleat scheme clr) (cleat scheme compiler))

(define (seed-form name)
  (call-with-input-file "boot/boot-seed.core"
    (lambda (p)
      (let loop ()
        (let ((x (read p)))
          (cond
            ((eof-object? x) (error 'seed-form "library not found" name))
            ((and (pair? x) (eq? (car x) 'library-letrec*) (equal? (cadr x) name)) x)
            (else (loop))))))))

(define out-dir
  (let ((d (string-append (clr-static-call System.IO.Path GetTempPath) "cleat-compile-library")))
    (clr-static-call System.IO.Directory CreateDirectory d)
    d))

(define form (seed-form '(boot lists)))
(define t0 (clr-static-prop-get System.Environment TickCount))
(define text (compile->lcil-text (list form) "cleat.scheme.lists" "#Module"))
(define t1 (clr-static-prop-get System.Environment TickCount))
(define lcil-path (string-append out-dir "/lists.lcil"))
(define dll-path (string-append out-dir "/cleat.scheme.lists.dll"))
(when (file-exists? lcil-path) (delete-file lcil-path))
(call-with-output-file lcil-path (lambda (p) (put-string p text)))
(clr-static-call Cleat.Emit.LcilEmitter (Save String String String Boolean) text lcil-path dll-path #f)
(define t2 (clr-static-prop-get System.Environment TickCount))

(display "compiled (boot lists): Lcil ")
(display (string-length text)) (display " chars in ") (display (- t1 t0)) (display " ms, emitted in ")
(display (- t2 t1)) (display " ms -> ") (display dll-path) (newline)

;; 加载执行：入口先预链接，把库中的过程写入各自的位置
(clr-static-call Cleat.Scheme.ModuleLoader (LoadModule String Boolean) dll-path #f)

(define env (environment '(rnrs) '(cleat scheme)))
(define failures 0)
(define (check name expr expected)
  (let ((got (eval expr env)))
    (unless (equal? got expected)
      (set! failures (+ failures 1))
      (display "FAIL: ") (display name) (display " got ") (write got) (newline))))

(define (compiled? name)
  (let ((t (clr-call System.Object GetType (eval name env))))
    (string=? (clr-prop-get System.Reflection.AssemblyName Name (clr-call System.Reflection.Assembly GetName (clr-prop-get System.Type Assembly t)))
              "cleat.scheme.lists")))

(for-each
  (lambda (name)
    (unless (compiled? name)
      (set! failures (+ failures 1))
      (display "NOT COMPILED: ") (display name) (newline)))
  '(filter partition fold-left fold-right remp remove remv remq memp member assp assoc find for-all exists))

(check "filter" '(filter odd? '(1 2 3 4 5)) '(1 3 5))
(check "partition" '(call-with-values (lambda () (partition odd? '(1 2 3 4))) list) '((1 3) (2 4)))
(check "fold-left" '(fold-left cons '() '(1 2 3)) '(((() . 1) . 2) . 3))
(check "fold-right" '(fold-right cons '() '(1 2 3)) '(1 2 3))
(check "fold-left multi" '(fold-left (lambda (a x y) (+ a (* x y))) 0 '(1 2 3) '(4 5 6)) 32)
(check "remove" '(remove 1 '(1 2 1 3)) '(2 3))
(check "member" '(member '(b) '(a (b) c)) '((b) c))
(check "assoc" '(assoc "b" '(("a" 1) ("b" 2))) '("b" 2))
(check "find" '(find even? '(1 3 4 5)) 4)
(check "for-all" '(for-all < '(1 2) '(2 3)) #t)
(check "exists" '(exists (lambda (x) (and (> x 2) x)) '(1 2 3 4)) 3)
(check "long list" '(length (filter even? (let loop ((i 0) (acc '())) (if (= i 100000) acc (loop (+ i 1) (cons i acc)))))) 50000)
(check "errors" '(guard (e ((assertion-violation? e) 'caught)) (filter odd? 5)) 'caught)

;; 驱动入口 compile-library-fasl：表达式求值得到库描述列表，生成库 fasl（程序集），入口返回该列表（load-library-fasl 读回）
(define lib-dll (string-append out-dir "/mini.lib.fasl"))
(compile-library-fasl/asm lib-dll '(list 'g$id (quote (mini lib)) '() '() '() '() '((f . g$f)) '()
                                (case-lambda (() '#t)) (case-lambda (() '#t)) (case-lambda (() '#t)) '() '#t)
                          #t)
(let ((content (clr-static-call Cleat.Scheme.ModuleLoader (LoadLibraryDll Object) lib-dll)))
  (unless (and (= (length content) 13) (equal? (cadr content) '(mini lib)) ((list-ref content 9)))
    (set! failures (+ failures 1))
    (display "FAIL: compile-library-fasl content ") (write content) (newline)))

;; 驱动入口 compile-bootfile：核心形式列表 → dll，入口依次执行
(define boot-dll (string-append out-dir "/mini.boot.dll"))
(compile-bootfile '((set! g$mini-boot-a '1) ($fx+ g$mini-boot-a '41)) boot-dll)
(let ((r (clr-static-call Cleat.Scheme.ModuleLoader (LoadModule String Boolean) boot-dll #f)))
  (unless (eqv? r 42)
    (set! failures (+ failures 1))
    (display "FAIL: compile-bootfile result ") (write r) (newline)))

(display "compile-library: ")
(display (if (= failures 0) "all checks passed" "FAILED"))
(newline)
(unless (= failures 0) (exit 1))
