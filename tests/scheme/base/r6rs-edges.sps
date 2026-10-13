;;; R6RS 边界语义回归（照 skeg 的 tests/r6rs-edges.sps，iron-072 起；cleat-075 起纳入）：对拍 Chez 时发现并修复的项，每组注明出处。
(import (rnrs) (cleat scheme))

(define failures 0)
(define count 0)
(define-syntax check
  (syntax-rules (=>)
    ((_ expr => expected)
     (let ((v (guard (e (#t (list 'raised (if (message-condition? e) (condition-message e) e)))) expr)))
       (set! count (+ count 1))
       (unless (equal? v expected)
         (set! failures (+ failures 1))
         (display "FAIL: ") (write 'expr) (display " => ") (write v)
         (display ", expected ") (write expected) (newline))))))
(define-syntax raises?
  (syntax-rules ()
    ((_ expr) (guard (e (#t #t)) expr #f))))
(define (negative-zero? x) (and (flonum? x) (eqv? x -0.0)))


;;; 文本端口的行尾（iron-074，R6RS 8.2.4 / 8.2.9；cleat 汇报）：get-line 只认换行符；字符串端口与 eol-style none 不转换；
;;; 其余 eol-style 的转码端口在字符层把 CR、CRLF、NEL、CRNEL、LS 转成换行符
(define io-tmp "r6rs-edges-io.tmp")
(define (write-raw! str)   ; 按 eol-style none 原样写出
  (when (file-exists? io-tmp) (delete-file io-tmp))
  (let ((o (open-file-output-port io-tmp (file-options) 'block (make-transcoder (utf-8-codec) 'none))))
    (put-string o str)
    (close-port o)))
(define (with-input eol proc)
  (let* ((p (open-file-input-port io-tmp (file-options) 'block (make-transcoder (utf-8-codec) eol)))
         (v (proc p)))
    (close-port p)
    v))
(define (all-chars p) (let loop ((acc '())) (let ((c (get-char p))) (if (eof-object? c) (reverse acc) (loop (cons c acc))))))
(define (lines p) (let loop ((acc '())) (let ((l (get-line p))) (if (eof-object? l) (reverse acc) (loop (cons l acc))))))
(define nel (string (integer->char #x85)))
(define ls (string (integer->char #x2028)))

(check (lines (open-string-input-port "a\r\nb\rc\nd")) => '("a\r" "b\rc" "d"))
(check (lines (open-string-input-port "a\n\nb\n")) => '("a" "" "b"))
(write-raw! (string-append "x\r\ny\rz" nel "w" ls "v\r" nel "u"))
(check (with-input 'none get-line) => "x\r")
(check (with-input 'none (lambda (p) (length (all-chars p)))) => 13)
(check (with-input 'crlf all-chars) => (string->list "x\ny\nz\nw\nv\nu"))
(check (with-input 'crlf get-string-all) => "x\ny\nz\nw\nv\nu")
(check (with-input 'lf lines) => '("x" "y" "z" "w" "v" "u"))
;; lookahead-char 看到的也是转换后的字符，CR 不被多读
(check (with-input 'lf (lambda (p) (get-char p) (list (lookahead-char p) (get-char p) (get-char p)))) => '(#\newline #\newline #\y))
(write-raw! "a\r\r\nb\r")
(check (with-input 'lf lines) => '("a" "" "b"))
;; 转码端口自己按块（4096 个字符）缓冲：CR 恰在块尾、LF 在下一块；跨多块的长行
(define long-a (make-string 4095 #\a))
(write-raw! (string-append long-a "\r\nb\r\nc"))
(check (with-input 'lf lines) => (list long-a "b" "c"))
(check (with-input 'none lines) => (list (string-append long-a "\r") "b\r" "c"))
(define long-z (make-string 10000 #\z))
(write-raw! (string-append long-z "\n" long-z "\rx" nel "\ny"))
(check (with-input 'lf lines) => (list long-z long-z "x" "" "y"))
;; get-line 与 get-char / lookahead-char / get-string-all 交替
(write-raw! "ab\r\ncd\ref")
(check (with-input 'crlf (lambda (p) (let* ((c1 (get-char p)) (l1 (get-line p)) (c2 (lookahead-char p)) (l2 (get-line p)))
                                       (list c1 l1 c2 l2 (get-string-all p)))))
       => '(#\a "b" #\c "cd" "ef"))
(write-raw! "a\r\r\nb\r")
;; 关闭后的转码端口：缓冲里还有字符也不能再读
(check (let ((p (open-file-input-port io-tmp (file-options) 'block (native-transcoder)))) (get-char p) (close-port p) (raises? (get-line p))) => #t)
;; 文本输入文件（原生转码器，eol-style lf）
(check (let* ((p (open-input-file io-tmp)) (v (get-string-all p))) (close-port p) v) => "a\n\nb\n")
;; 输出方向：crlf 转码器把换行符写成 CR LF
(check (let-values (((o g) (open-bytevector-output-port (make-transcoder (utf-8-codec) 'crlf)))) (put-string o "a\nb") (g))
       => #vu8(97 13 10 98))
;; 行尾字符写成 \x…; 才能原样读回
(check (let-values (((o g) (open-string-output-port))) (write (string #\a (integer->char #x85) (integer->char 1)) o) (g))
       => "\"a\\x85;\\x1;\"")
(check (let-values (((o g) (open-string-output-port))) (write (integer->char #x2028) o) (g)) => "#\\x2028")

;;; output-port-buffer-mode 返回打开时给的模式（iron-074；原来按底层流猜，文本端口总是 line）
(define (mode-of open)
  (when (file-exists? io-tmp) (delete-file io-tmp))
  (let* ((p (open)) (m (output-port-buffer-mode p))) (close-port p) m))
(for-each
  (lambda (m)
    (check (mode-of (lambda () (open-file-output-port io-tmp (file-options) m))) => m)
    (check (mode-of (lambda () (open-file-output-port io-tmp (file-options) m (native-transcoder)))) => m)
    (check (mode-of (lambda () (open-file-input/output-port io-tmp (file-options) m))) => m)
    (check (mode-of (lambda () (transcoded-port (open-file-output-port io-tmp (file-options) m) (native-transcoder)))) => m))
  '(none line block))
(check (mode-of (lambda () (open-file-output-port io-tmp))) => 'block)   ; 省略时为 block
(check (raises? (open-file-output-port io-tmp (file-options no-fail) 17)) => #t)   ; 不是缓冲模式（Chez io.ms）
(check (raises? (open-file-output-port io-tmp (file-options no-fail) 'block 'wow)) => #t)   ; 不是转码器
(check (let-values (((p g) (open-string-output-port))) (output-port-buffer-mode p)) => 'block)
(check (let-values (((p g) (open-bytevector-output-port))) (output-port-buffer-mode p)) => 'block)
;;; 关闭后的自定义端口不能再读写（iron-074，Chez io.ms custom-*-ports）；重复关闭只调一次 close 过程
(define closes 0)
(define (closed port) (close-port port) port)
(check (raises? (put-u8 (closed (make-custom-binary-output-port "o" (lambda (bv s c) c) #f #f #f)) 0)) => #t)
(check (raises? (put-char (closed (make-custom-textual-output-port "o" (lambda (s st c) c) #f #f #f)) #\a)) => #t)
(check (raises? (get-u8 (closed (make-custom-binary-input-port "i" (lambda (bv s c) 0) #f #f #f)))) => #t)
(check (raises? (get-char (closed (make-custom-textual-input-port "i" (lambda (s st c) 0) #f #f #f)))) => #t)
(check (let ((p (make-custom-binary-input-port "i" (lambda (bv s c) 0) #f #f (lambda () (set! closes (+ closes 1))))))
         (close-port p) (close-port p) closes)
       => 1)

(when (file-exists? io-tmp) (delete-file io-tmp))

(display (if (= failures 0) "r6rs-edges: all passed" "r6rs-edges: failed"))
(display " (") (display count) (display " checks)")
(newline)
(exit (if (= failures 0) 0 1))
