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


;;; div / mod：被除数为 -0.0 时商的零符号（原来取绝对值再定符号，(div -0.0 2) 得 0.0、(mod -0.0 2) 得 -0.0）
(check (negative-zero? (div -0.0 2)) => #t)
(check (eqv? (mod -0.0 2) 0.0) => #t)
(check (call-with-values (lambda () (div-and-mod -0.0 -2)) list) => '(0.0 0.0))
(check (eqv? (div 7.5 -2) -3.0) => #t)   ; 商 -3，mod 1.5
(check (raises? (div 1 0.0)) => #t)        ; x2 必须非零（R6RS），Chez 返回 +inf.0 是其扩展
(check (raises? (div +inf.0 2)) => #t)     ; x1 不能是无穷或 NaN

;;; remainder / modulo：整除时得 0.0 而不是 -0.0（按定义 n1 - n2 * q）
(check (eqv? (remainder -4.0 2.0) 0.0) => #t)
(check (eqv? (modulo 4.0 -2.0) 0.0) => #t)
(check (eqv? (modulo 7.0 -1) 0.0) => #t)
(check (remainder -7 2) => -1)
(check (modulo -7 2) => 1)

;;; fxmod / fxmod0：除数为 -1 时余数为 0，不随商溢出
(check (fxmod (least-fixnum) -1) => 0)
(check (fxmod0 (least-fixnum) -1) => 0)
(check (raises? (fxdiv (least-fixnum) -1)) => #t)
(check (raises? (fxrotate-bit-field 0 0 0 -1)) => #t)

;;; eqv?：字符串按引用、复数逐分量区分 ±0.0
(check (eqv? "hi there" (string-append "hi " "there")) => #f)
(check (let ((s "x")) (eqv? s s)) => #t)
(check (eqv? 3.0+0.0i 3.0-0.0i) => #f)
(check (eqv? 3.0+4.0i 3.0+4.0i) => #t)

;;; 多参数比较与谓词：短路为 #f 后仍检查余下参数
(check (raises? (= 4 3 'a)) => #t)
(check (raises? (< 4 3 'a)) => #t)
(check (raises? (> 2 3 3+i)) => #t)
(check (raises? (char=? #\a #\c 'a #\b)) => #t)
(check (raises? (char-ci<? #\a #\c 'a)) => #t)
(check (raises? (string=? "hi" "ho" 'a "he")) => #t)
(check (raises? (string-ci>? "hi" "ho" 'a)) => #t)
(check (raises? (fl=? 3.0 3.1 3)) => #t)
(check (raises? (fl<? 3.5 3.5 7/2 4.5)) => #t)
(check (fl<? 1.0 2.0 3.0) => #t)
(check (raises? (fx<? 3 3 'a)) => #t)
(check (raises? (boolean=? #t #f 3)) => #t)
(check (raises? (symbol=? 't 'f 3)) => #t)
(check (char<? #\a #\b #\c) => #t)
(check (string<? "a" "b" "c") => #t)

;;; nan? / finite? / infinite? 只收实数
(check (raises? (nan? 3+4i)) => #t)
(check (nan? +nan.0) => #t)
(check (finite? 3/4) => #t)

;;; expt / lcm / bitwise 边界
(check (raises? (expt 0 -1)) => #t)
(check (expt 0 0) => 1)
(check (expt 0.0 -1) => +inf.0)
(check (lcm 10.0 0.0) => 0.0)
(check (lcm 10 0) => 0)
(check (bitwise-arithmetic-shift 0 (expt 2 100)) => 0)
(check (bitwise-bit-field 35 (* (greatest-fixnum) 2) (+ (* (greatest-fixnum) 2) 10)) => 0)
(check (bitwise-bit-field -35 (* (greatest-fixnum) 2) (+ (* (greatest-fixnum) 2) 10)) => 1023)
(check (raises? (bitwise-rotate-bit-field 0 0.0 0 0)) => #t)

;;; string->number / number->string 参数检查与 1e23 的写法
(check (raises? (string->number "a" 37)) => #t)
(check (string->number "ff" 16) => 255)
(check (number->string 1e23) => "1e23")
(check (number->string 1.5e23) => "1.5e23")
(check (raises? (number->string 0.0 10 0)) => #t)
(check (raises? (number->string 1 10 24)) => #t)
(check (number->string 0.5 10 24) => "0.5|53")   ; 带精度时写出尾数宽度，见下

;;; Unicode 属性：Alphabetic / Uppercase / Lowercase（不只是 L* / Lu / Ll）
(check (char-lower-case? #\xAA) => #t)        ; ª：Lo + Other_Lowercase
(check (char-alphabetic? #\x2160) => #t)      ; Ⅰ：Nl
(check (char-upper-case? #\x2160) => #t)      ; Ⅰ：Other_Uppercase
(check (char-alphabetic? #\x0345) => #t)      ; 组合字符，Other_Alphabetic
(check (char-alphabetic? #\1) => #f)
(check (char-upper-case? #\a) => #f)

;;; string-titlecase：词内第一个有大小写的字符
(check (string-titlecase "ciao123 futzmo. 33ar") => "Ciao123 Futzmo. 33Ar")
(check (string-titlecase "who's there?") => "Who's There?")
(check (string-titlecase "r6rs") => "R6rs")
(check (string-titlecase "kNock KNoCK") => "Knock Knock")

;;; 枚举：空全集可用，不同类型的集合运算报错
(check (enum-set->list (make-enumeration '())) => '())
(check (let () (define-enumeration foo () bar) #t) => #t)
(check (raises? (enum-set-union (make-enumeration '(a b)) (make-enumeration '(c d)))) => #t)

;;; bytevector：endianness、native 对齐、u8 / s8 范围
(check (raises? (bytevector-u16-ref #vu8(1 2 3) 0 'bigger)) => #t)
(check (raises? (bytevector-sint-ref #vu8(1 2 3) 0 'bonkers 2)) => #t)
(check (raises? (bytevector-u16-native-ref #vu8(1 2 3 4) 1)) => #t)
(check (raises? (bytevector-u32-native-set! (make-bytevector 8) 2 0)) => #t)
(check (bytevector-u16-native-ref #vu8(1 2 3 4) 2) => (if (eq? (native-endianness) 'little) #x0403 #x0304))
(check (raises? (u8-list->bytevector '(1 -1 3))) => #t)
(check (raises? (sint-list->bytevector '(0 128 0) 'little 1)) => #t)
(check (sint-list->bytevector '(0 -128 127) 'little 1) => #vu8(0 128 127))
(check (let ((bv (make-bytevector 2 0))) (bytevector-s8-set! bv 0 -1) (bytevector-u8-ref bv 0)) => 255)
(check (raises? (let ((bv (make-bytevector 2 0))) (bytevector-u8-set! bv 0 -1))) => #t)

;;; bignum 与整数值 flonum 混合的 div / mod 系：精确计算后取不精确，不再经 +inf.0 得 +nan.0
(check (remainder #e1e1000 3.0) => 1.0)
(check (modulo #e-1e1000 3.0) => 2.0)
(check (call-with-values (lambda () (div-and-mod #e1e1000 3.0)) (lambda (d m) (list (infinite? d) m))) => '(#t 1.0))
(check (mod0 #e2e1000 3.0) => -1.0)
(check (gcd #e3e999 1.0) => 1.0)
(check (lcm #e3e999 1.0) => +inf.0)
(check (div 7 2.0) => 3.0)

;;; expt：bignum 指数只对 0、±1 有值；负 flonum 底数的奇数大指数保留符号
(check (expt -1 (expt 2 100)) => 1)
(check (expt -1 (+ 1 (expt 2 100))) => -1)
(check (expt 1 (expt 2 100)) => 1)
(check (raises? (expt 2 (expt 2 100))) => #t)
(check (expt -inf.0 (+ 1 (expt 2 100))) => -inf.0)
(check (expt -inf.0 (expt 2 100)) => +inf.0)
(check (eqv? (expt -0.0 (+ 1 (expt 2 55))) -0.0) => #t)
(check (expt -0.0 (- (+ 1 (expt 2 55)))) => -inf.0)
(check (expt -2.0 3) => -8.0)

;;; 尾数宽度与精度（R6RS 4.2.8、11.7.4.4）
(check (string->number "1|21") => 1.0)
(check (string->number "0.1|10") => 0.0999755859375)
(check (string->number "1.5|200") => 1.5)
(check (number->string 100.5 10 53) => "100.5|53")
(check (number->string 100.5 10 10) => "100.5|53")
(check (number->string 100.5 10 60) => "100.5|60")
(check (number->string +inf.0 10 53) => "+inf.0")
(check (number->string 1.5+2.5i 10 53) => "1.5|53+2.5|53i")

;;; filter / partition / remp 的 proc 参数在空表上也要检查
(check (raises? (filter 'q '())) => #t)
(check (raises? (remp 'q '())) => #t)
(check (raises? (partition 'q '())) => #t)
(check (filter odd? '(1 2 3)) => '(1 3))

;;; bytevector-uint-ref 系的 size 须为正
(check (raises? (bytevector-uint-ref (make-bytevector 4 0) 0 'little 0)) => #t)
(check (raises? (bytevector-sint-set! (make-bytevector 4 0) 0 0 'little 0)) => #t)

;;; div / mod 的 fixnum 快路径（cleat-077）：与按定义 x1 = q·x2 + m、0 ≤ m < |x2| 的结果一致，含最小 / 最大 fixnum 与 ±1、±2
(define fx-samples (list 0 1 -1 2 -2 3 -3 7 -7 10 -10 1000003 -1000003 (greatest-fixnum) (least-fixnum)
                         (- (greatest-fixnum) 1) (+ (least-fixnum) 1)))
(define (div-mod-ok? a b)
  (let ((q (div a b)) (m (mod a b)))
    (and (= a (+ (* q b) m)) (<= 0 m) (< m (abs b))
         (call-with-values (lambda () (div-and-mod a b)) (lambda (q2 m2) (and (= q q2) (= m m2)))))))
(check (for-all (lambda (a) (for-all (lambda (b) (or (= b 0) (div-mod-ok? a b))) fx-samples)) fx-samples) => #t)
(check (div (least-fixnum) -1) => (- (least-fixnum)))   ; 溢出成 bignum，走原实现
(check (mod (least-fixnum) -1) => 0)
(check (raises? (div 1 0)) => #t)
(check (raises? (mod 1 0)) => #t)

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
