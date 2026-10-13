;;; Chez Scheme mats 测试 oracle（skeg iron-072，cleat-077 纳入）。
;;;
;;;   cd src/scheme && dotnet ../Cleat.Stage0/bin/Release/net10.0/stage0.dll -I lib -I nanopass --boot ../boot/cleat.scheme.boot.dll tests/chez-mats/run.sps [-v|-t] 文件.ms …
;;;
;;; 本目录下的 *.ms 是 Chez Scheme mats/ 的原样拷贝（Apache-2.0，见 NOTICE）。运行器不改写文件，而是：
;;;   1. 按括号切出顶层形式（跳过注释、字符串、字符字面量），去掉 Chez 的 #%名字 / #2%名字 / #3%名字 前缀；
;;;   2. 每个形式单独读（读不出的是 Chez 特有语法，整形式跳过）；非 mat 的顶层形式（辅助定义）在交互环境中求值；
;;;   3. (mat 名字 子句 …) 的每个子句单独求值，结果为 #t 才算通过（与 Chez 的 mat-run 相同，其余真值也算失败）；
;;;      (error? e)：e 抛出条件为通过、正常返回为失败。
;;; 跳过：子句用到交互环境里没有的名字（undefined-violation，多是 Chez 的扩展过程）、语法错误（Chez 的扩展形式）、
;;; (mat 名字 (parameters …) …) 形式、warning? 子句，以及 known.ss 中登记的子句（fixnum 宽度等已知差异，带原因）。
;;; 退出码：有失败（不在 known.ss 中）时为 1。
;;;
;;; 进程结构：主进程（监督者）每个文件起一个工作进程（同一脚本加 --worker 文件 形式号 子句号）逐条求值并按行回报；
;;; 子句在后台线程里求值，超过 clause-seconds 秒即记 timeout，工作进程随即退出（退出码 3），监督者从下一条子句
;;; 重新起一个工作进程。不终止的子句（循环表遍历、bignum 下标）无法从线程里杀掉，留在进程里会一直分配内存
;;; ——第一版就是这样把机器内存吃光的；工作进程还带 DOTNET_GCHeapHardLimit（2 GB），失控分配只会让该进程 OOM。
(import (rnrs) (rnrs eval) (cleat scheme) (cleat scheme clr) (only (cleat scheme unsafe) $start-thread))
(clr-using System)
(clr-using System.Diagnostics)

(define verbose? #f)
(define trace? #f)                      ; -t：每个子句运行前打印（找不终止的子句）
(define env (interaction-environment))

;; —— 切分顶层形式 ——
;; 返回 (行号 . 文本) 的列表；文本中已去掉 #% 前缀
(define (split-forms text)
  (let ((n (string-length text)))
    (define (ch i) (string-ref text i))
    (define out '())
    (define line 1)
    (let loop ((i 0) (depth 0) (start #f) (start-line 1) (buf '()) (drop? #f))
      ;; buf：当前形式的字符（逆序）
      (define (emit i buf)
        (unless drop? (set! out (cons (cons start-line (list->string (reverse buf))) out))))
      (cond
        ((>= i n) (when (and start (pair? buf)) (emit i buf)) (reverse out))
        (else
         (let ((c (ch i)))
           (when (char=? c #\newline) (set! line (+ line 1)))
           (cond
             ;; 行注释
             ((char=? c #\;)
              (let skip ((j i))
                (if (or (>= j n) (char=? (ch j) #\newline))
                    (loop j depth start start-line buf drop?)
                    (skip (+ j 1)))))
             ;; 块注释 #| … |#（可嵌套）
             ((and (char=? c #\#) (< (+ i 1) n) (char=? (ch (+ i 1)) #\|))
              (let skip ((j (+ i 2)) (d 1))
                (cond
                  ((>= j n) (loop j depth start start-line buf drop?))
                  ((and (char=? (ch j) #\|) (< (+ j 1) n) (char=? (ch (+ j 1)) #\#))
                   (if (= d 1) (loop (+ j 2) depth start start-line buf drop?) (skip (+ j 2) (- d 1))))
                  ((and (char=? (ch j) #\#) (< (+ j 1) n) (char=? (ch (+ j 1)) #\|)) (skip (+ j 2) (+ d 1)))
                  (else (when (char=? (ch j) #\newline) (set! line (+ line 1))) (skip (+ j 1) d)))))
             ;; 顶层的 #;：丢弃下一个形式
             ((and (= depth 0) (not start) (char=? c #\#) (< (+ i 1) n) (char=? (ch (+ i 1)) #\;))
              (loop (+ i 2) depth start start-line buf #t))
             ;; #% / #2% / #3% 前缀
             ((and (char=? c #\#) (< (+ i 1) n)
                   (or (char=? (ch (+ i 1)) #\%)
                       (and (< (+ i 2) n) (memv (ch (+ i 1)) '(#\2 #\3)) (char=? (ch (+ i 2)) #\%))))
              (loop (+ i (if (char=? (ch (+ i 1)) #\%) 2 3)) depth (or start i) (if start start-line line) buf drop?))
             ;; 字符字面量：#\ 后的一个字符原样带上（括号、引号不影响切分）
             ((and (char=? c #\#) (< (+ i 2) n) (char=? (ch (+ i 1)) #\\))
              (loop (+ i 3) depth (or start i) (if start start-line line) (cons (ch (+ i 2)) (cons #\\ (cons #\# buf))) drop?))
             ;; 字符串与 |符号|
             ((or (char=? c #\") (char=? c #\|))
              (let str ((j (+ i 1)) (b (cons c buf)))
                (cond
                  ((>= j n) (loop j depth start start-line b drop?))
                  ((char=? (ch j) #\\) (if (< (+ j 1) n) (str (+ j 2) (cons (ch (+ j 1)) (cons #\\ b))) (str (+ j 1) b)))
                  ((char=? (ch j) c) (let ((b (cons c b)))
                                       (if (and (= depth 0)) 
                                           (begin (emit (+ j 1) b) (loop (+ j 1) 0 #f line '() #f))
                                           (loop (+ j 1) depth (or start i) (if start start-line line) b drop?))))
                  (else (when (char=? (ch j) #\newline) (set! line (+ line 1))) (str (+ j 1) (cons (ch j) b))))))
             ((memv c '(#\( #\[))
              (loop (+ i 1) (+ depth 1) (or start i) (if start start-line line) (cons c buf) drop?))
             ((memv c '(#\) #\]))
              (let ((b (cons c buf)) (d (- depth 1)))
                (if (<= d 0)
                    (begin (emit (+ i 1) b) (loop (+ i 1) 0 #f line '() #f))
                    (loop (+ i 1) d start start-line b drop?))))
             ((char-whitespace? c)
              (if (and start (= depth 0))
                  ;; 顶层原子（或 '、` 等前缀后的空白）：前缀字符不结束形式
                  (if (memv (car buf) '(#\' #\` #\, #\@))
                      (loop (+ i 1) depth start start-line buf drop?)
                      (begin (emit i buf) (loop (+ i 1) 0 #f line '() #f)))
                  (loop (+ i 1) depth start start-line (if start (cons c buf) buf) drop?)))
             ;; Chez 的 1+ / 1- 不是 R6RS 标识符（cleat 读作坏数字）：独立出现时改名为 $chez-1+ / $chez-1-（垫片定义）
             ((and (char=? c #\1) (< (+ i 1) n) (memv (ch (+ i 1)) '(#\+ #\-))
                   (or (= i 0) (delimiter? (ch (- i 1))))
                   (or (= (+ i 2) n) (delimiter? (ch (+ i 2)))))
              (loop (+ i 2) depth (or start i) (if start start-line line)
                    (append (reverse (string->list (string-append "$chez-1" (string (ch (+ i 1)))))) buf) drop?))
             (else (loop (+ i 1) depth (or start i) (if start start-line line) (cons c buf) drop?)))))))))

(define (prefix? p s) (and (>= (string-length s) (string-length p)) (string=? p (substring s 0 (string-length p)))))

(define (delimiter? c) (or (char-whitespace? c) (memv c '(#\( #\) #\[ #\] #\" #\; #\'))))

;; —— 已知差异 ——
(define known (make-hashtable string-hash string=?))
(define (load-known! path)
  (when (file-exists? path)
    (call-with-input-file path
      (lambda (p)
        (let loop ()
          (let ((x (read p)))
            (unless (eof-object? x)
              ;; (文件 mat名字 子句号 原因) ，子句号为 * 表示整个 mat
              (hashtable-set! known (key (car x) (cadr x) (caddr x)) (cadddr x))
              (loop))))))))
(define (key file name i)
  (string-append file ":" (if (symbol? name) (symbol->string name) (let-values (((p g) (open-string-output-port))) (write name p) (g)))
                 ":" (if (number? i) (number->string i) (symbol->string i))))

;; —— 求值 ——
(define (condition->string c)
  (let-values (((p g) (open-string-output-port)))
    (when (who-condition? c) (write (condition-who c) p) (display ": " p))
    (when (message-condition? c) (display (condition-message c) p))
    (when (irritants-condition? c) (display " " p) (write (condition-irritants c) p))
    (let ((s (g))) (if (> (string-length s) 160) (substring s 0 160) s))))

;; 结果：(pass) / (fail 原因) / (skip 原因)
(define (run-clause clause)
  (define (classify c)
    (cond
      ((undefined-violation? c) (list 'skip (string-append "unbound " (condition->string c))))
      ((syntax-violation? c) (list 'skip (string-append "syntax " (condition->string c))))
      ((and (message-condition? c) (prefix? "cannot locate library" (condition-message c)))
       (list 'skip (string-append "library " (condition->string c))))   ; (import (unicode-data)) 等 Chez 自带库
      (else #f)))
  (cond
    ((and (pair? clause) (eq? (car clause) 'warning?)) (list 'skip "warning?"))
    ((and (pair? clause) (memq (car clause) '(error? mat-error?)) (pair? (cdr clause)) (null? (cddr clause)))
     (guard (c ((undefined-violation? c) (list 'skip (string-append "unbound " (condition->string c))))
               (#t (list 'pass)))
       (eval (cadr clause) env)
       (list 'fail "no error")))
    (else
     (guard (c ((classify c) => (lambda (r) r))
               (#t (list 'fail (string-append "raised " (condition->string c)))))
       (let ((v (eval clause env)))
         (if (eq? v #t) (list 'pass) (list 'fail (if v "bogus (non-#t true value)" "false"))))))))

;; 子句在后台线程中求值，超过 clause-seconds 秒算 timeout：工作进程回报后立即退出（见文件头）
(define clause-seconds 10)
(define (run-clause/timeout clause)
  (let* ((result #f)
         ;; $start-thread：后台线程（不阻止进程退出），已启动
         (t ($start-thread (lambda () (set! result (run-clause clause))))))
    (if (clr-call System.Threading.Thread (Join Int32) t (* clause-seconds 1000))
        result
        (list 'timeout "timeout"))))

;; error? 在子句内部嵌套出现时（如 (and (error? …) …)）：未绑定名字照旧上抛，由外层归为跳过
(define prelude
  '((define-syntax error?
      (syntax-rules ()
        ((_ e) (guard (c ((undefined-violation? c) (raise c)) (#t #t)) e #f))))
    (define-syntax warning? (syntax-rules () ((_ e) (raise (make-undefined-violation)))))))

;; Chez 的常用小过程：交互环境里没有时才定义（只放语义无歧义的；其余用到 Chez 扩展的子句照常跳过）
(define shims
  '((andmap (define (andmap f l . ls)
              (if (null? ls)
                  (let loop ((l l)) (or (null? l) (if (null? (cdr l)) (f (car l)) (and (f (car l)) (loop (cdr l))))))
                  (apply for-all f l ls))))
    (ormap (define (ormap f l . ls) (apply exists f l ls)))
    ($chez-1+ (define ($chez-1+ x) (+ x 1)))
    ($chez-1- (define ($chez-1- x) (- x 1)))
    (add1 (define (add1 x) (+ x 1)))
    (sub1 (define (sub1 x) (- x 1)))
    (make-list (define make-list (case-lambda ((n) (make-list n #f)) ((n x) (let loop ((n n) (r '())) (if (= n 0) r (loop (- n 1) (cons x r))))))))
    (iota (define (iota n) (let loop ((i (- n 1)) (r '())) (if (< i 0) r (loop (- i 1) (cons i r))))))
    (list-head (define (list-head l n) (if (= n 0) '() (cons (car l) (list-head (cdr l) (- n 1))))))
    (most-positive-fixnum (define (most-positive-fixnum) (greatest-fixnum)))
    (most-negative-fixnum (define (most-negative-fixnum) (least-fixnum)))))

(define (bound? name)
  (guard (c (#t #f)) (eval name env) #t))

(define (file-name path)
  (let loop ((i (- (string-length path) 1)))
    (cond ((< i 0) path) ((char=? (string-ref path i) #\/) (substring path (+ i 1) (string-length path))) (else (loop (- i 1))))))

(define (write-string* . xs) (for-each (lambda (x) (if (string? x) (display x) (write x))) xs))

;; —— 工作进程 ——
;; 按 (形式号 . 子句号) 的顺序求值 path 中的子句，只运行 ≥ (from-form . from-clause) 的；每条子句前后各回报一行：
;;   B<TAB>键<TAB>行号<TAB>形式号<TAB>子句号
;;   R<TAB>结果<TAB>键<TAB>行号<TAB>原因          结果 ∈ pass fail skip known timeout
;; 辅助定义等非 mat 形式一律求值（恢复运行时也要重建环境）。timeout 后退出码 3。
(define (worker path from-form from-clause)
  (let ((forms (split-forms (call-with-input-file path get-string-all)))
        (file (file-name path))
        (seen (make-eq-hashtable))
        (out (current-output-port)))
    (define (report . xs)
      (let loop ((xs xs) (first #t))
        (unless (null? xs)
          (unless first (put-char out #\tab))
          (put-string out (let ((x (car xs))) (if (string? x) x (number->string x))))
          (loop (cdr xs) #f)))
      (put-char out #\newline)
      (flush-output-port out))
    (define (clause-text x)
      (let-values (((p g) (open-string-output-port)))
        (write x p)
        (let ((t (one-line (g)))) (if (> (string-length t) 200) (string-append (substring t 0 200) " …") t))))
    (define (one-line s) (list->string (map (lambda (c) (if (or (char=? c #\newline) (char=? c #\tab)) #\space c)) (string->list s))))
    (for-each (lambda (f) (eval f env)) prelude)
    (for-each (lambda (s) (unless (bound? (car s)) (eval (cadr s) env))) shims)
    (let form-loop ((lfs forms) (fi 0))
      (unless (null? lfs)
        (let* ((lf (car lfs)) (line (car lf))
               (x (guard (c (#t (when (>= fi from-form) (report "N" "read-error" line (one-line (condition->string c)))) 'read-error))
                    (read (open-string-input-port (cdr lf))))))
          (cond
            ((eq? x 'read-error) (when (>= fi from-form) (report "R" "skip" (string-append file ":?:" (number->string line)) line "read")))
            ((and (pair? x) (eq? (car x) 'mat) (pair? (cdr x)))
             (let* ((name0 (cadr x))
                    (occ (+ 1 (hashtable-ref seen name0 0)))
                    (name (if (= occ 1) name0 (string->symbol (string-append (if (symbol? name0) (symbol->string name0) "?") "@" (number->string occ))))))
               (hashtable-set! seen name0 occ)
               (let clause-loop ((cls (cddr x)) (i 1))
                 (unless (null? cls)
                   (let ((k (key file name i)))
                     (when (or (> fi from-form) (and (= fi from-form) (>= i from-clause)))
                       (cond
                         ((and (pair? (cddr x)) (pair? (caddr x)) (eq? (car (caddr x)) 'parameters))
                          (report "R" "skip" k line "parameters"))
                         ((or (hashtable-ref known (key file name '*) #f) (hashtable-ref known k #f))
                          => (lambda (why) (report "R" "known" k line why)))
                         (else
                          (report "B" k line fi i)
                          (let ((r (run-clause/timeout (car cls))))
                            (report "R" (symbol->string (car r)) k line (if (null? (cdr r)) "" (one-line (cadr r)))
                                    (if (memq (car r) '(fail timeout)) (clause-text (car cls)) ""))
                            (when (eq? (car r) 'timeout) (exit 3)))))))
                   (clause-loop (cdr cls) (+ i 1))))))
            (else
             (guard (c (#t (report "N" "top-level" line (one-line (condition->string c)))))
               (eval x env)))))
        (form-loop (cdr lfs) (+ fi 1))))
    (exit 0)))

;; —— 监督者 ——
(define host-dll (vector-ref (clr-static-call Environment GetCommandLineArgs) 0))
(define this-script
  (let ((args (clr-static-call Environment GetCommandLineArgs)))
    (let loop ((i 1) (acc '()))
      (let ((a (vector-ref args i)))
        (if (and (>= (string-length a) 7) (string=? (substring a (- (string-length a) 7) (string-length a)) "run.sps"))
            (reverse (cons a acc))
            (loop (+ i 1) (cons a acc)))))))   ; 宿主选项（--boot、-I …）与脚本路径

(define (start-worker path form clause)
  (let ((psi (clr-new ProcessStartInfo "dotnet")))
    (for-each (lambda (a) (clr-call System.Collections.ObjectModel.Collection<String> Add (clr-prop-get ProcessStartInfo ArgumentList psi) a))
              (append (list host-dll) this-script (list "--worker" path (number->string form) (number->string clause))))
    (clr-prop-set! ProcessStartInfo RedirectStandardOutput psi #t)
    (clr-prop-set! ProcessStartInfo UseShellExecute psi #f)
    (clr-static-call Process (Start ProcessStartInfo) psi)))

(define (split-tabs s)
  (let loop ((i 0) (start 0) (acc '()))
    (cond ((= i (string-length s)) (reverse (cons (substring s start i) acc)))
          ((char=? (string-ref s i) #\tab) (loop (+ i 1) (+ i 1) (cons (substring s start i) acc)))
          (else (loop (+ i 1) start acc)))))

(define (run-file path)
  (let ((file (file-name path))
        (pass 0) (fail 0) (skip 0) (known-n 0) (restarts 0) (failures '()) (skips (make-hashtable string-hash string=?)))
    (define (count-skip! why)
      (set! skip (+ skip 1))
      ;; 按原因的前两个词归类计数（如 "unbound attempted"、"syntax lambda:"）
      (let ((k (let ((n (string-length why)))
                 (let loop ((i 0) (spaces 0))
                   (cond ((= i n) why)
                         ((char=? (string-ref why i) #\space) (if (= spaces 1) (substring why 0 i) (loop (+ i 1) 1)))
                         (else (loop (+ i 1) spaces)))))))
        (hashtable-update! skips k (lambda (x) (+ x 1)) 0)))
    (define (fail! k line why . text)
      (set! fail (+ fail 1))
      (set! failures (cons (list k line why (if (null? text) "" (car text))) failures)))
    (let restart ((form 0) (clause 1))
      (let* ((p (start-worker path form clause))
             (in (clr-prop-get Process StandardOutput p)))
        (let loop ((pending #f))             ; pending：已回报 B、尚未回报 R 的子句 (键 行号 形式号 子句号)
          (let ((line (clr-call System.IO.StreamReader ReadLine in)))
            (cond
              ((or (not line) (null? line) (eq? line '#nil) (not (string? line)))
               (clr-call Process WaitForExit p)
               (let ((code (clr-prop-get Process ExitCode p)))
                 (cond
                   ((and (= code 3) (not pending)) (void))   ; 正常的 timeout 退出，R 行已处理
                   (pending                                   ; 进程死在子句中间（崩溃、OOM）
                    (fail! (car pending) (cadr pending) (string-append "worker exited " (number->string code)))
                    (set! restarts (+ restarts 1))
                    (restart (caddr pending) (+ (cadddr pending) 1)))
                   ((not (= code 0))
                    (display "worker exited ") (display code) (display " outside a clause; stopping ") (display file) (newline)
                    (set! fail (+ fail 1))))))
              (else
               (let ((f (split-tabs line)))
                 (cond
                   ((string=? (car f) "B")
                    (when trace? (display "  run ") (display (cadr f)) (newline) (flush-output-port (current-output-port)))
                    (loop (list (cadr f) (string->number (caddr f)) (string->number (cadddr f)) (string->number (car (cddddr f))))))
                   ((string=? (car f) "R")
                    (let ((kind (cadr f)) (k (caddr f)) (ln (string->number (cadddr f))) (why (car (cddddr f)))
                          (text (if (pair? (cdr (cddddr f))) (cadr (cddddr f)) "")))
                      (cond
                        ((string=? kind "pass") (set! pass (+ pass 1)))
                        ((string=? kind "known") (set! known-n (+ known-n 1)))
                        ((string=? kind "skip") (count-skip! why)
                                                (when verbose? (display "  skip ") (display k) (display " ") (display why) (newline)))
                        ((string=? kind "timeout") (fail! k ln "timeout" text)
                                                   (set! restarts (+ restarts 1))
                                                   ;; 工作进程随后退出 3：读到 eof 时 pending 为 #f，再从下一条子句恢复
                                                   (let ((next (list (caddr pending) (+ (cadddr pending) 1))))
                                                     (clr-call System.IO.StreamReader ReadToEnd in)
                                                     (clr-call Process WaitForExit p)
                                                     (restart (car next) (cadr next))))
                        (else (fail! k ln why text)))
                      (unless (string=? kind "timeout") (loop #f))))
                   ((string=? (car f) "N")   ; 读错误 / 辅助定义失败：-v 时报告
                    (when verbose? (display "  ") (display (cadr f)) (display " at line ") (display (caddr f)) (display ": ") (display (cadddr f)) (newline))
                    (loop pending))
                   (else (display line) (newline) (loop pending)))))))))) ; 子句自己的输出原样透传
    (for-each (lambda (f)
                (display "FAIL ") (display (car f)) (display " (line ") (display (cadr f)) (display "): ") (display (caddr f)) (newline)
                (unless (string=? (cadddr f) "") (display "     ") (display (cadddr f)) (newline)))
              (reverse failures))
    (display file) (display ": pass ") (display pass) (display ", fail ") (display fail)
    (display ", known ") (display known-n) (display ", skip ") (display skip)
    (when (> restarts 0) (display ", restarts ") (display restarts))
    (newline)
    (when verbose?
      (let-values (((ks vs) (hashtable-entries skips)))
        (vector-for-each (lambda (k v) (display "  skip ") (display k) (display " ") (display v) (newline)) ks vs)))
    fail))

(define dir "tests/chez-mats/")
(load-known! (string-append dir "known.ss"))
(let* ((args (cdr (command-line))))
  (cond
    ((and (pair? args) (string=? (car args) "--worker"))
     (worker (cadr args) (string->number (caddr args)) (string->number (cadddr args))))
    (else
     ;; 工作进程的堆上限：失控分配只影响该进程（子进程继承环境变量）
     (clr-static-call Environment (SetEnvironmentVariable String String) "DOTNET_GCHeapHardLimit" "0x80000000")
     (let* ((args (if (and (pair? args) (string=? (car args) "-v")) (begin (set! verbose? #t) (cdr args)) args))
            (args (if (and (pair? args) (string=? (car args) "-t")) (begin (set! verbose? #t) (set! trace? #t) (cdr args)) args))
            (files (map (lambda (a) (if (file-exists? a) a (string-append dir a))) args))
            (fails (fold-left + 0 (map run-file files))))
       (exit (if (= fails 0) 0 1))))))
