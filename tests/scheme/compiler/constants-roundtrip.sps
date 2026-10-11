;;; 常量往返（iron-020）：在 stage0 上用 (cleat scheme compiler constants) 编码各类常量，写成 Lcil 常量表，
;;; 经发射器（内存模式）生成程序集，再从 $Constants.c 读回，逐项核对。
;;; 运行（在 src/scheme 目录下）：
;;;   dotnet ../Cleat.Stage0/bin/Release/net10.0/stage0.dll -I lib -I nanopass boot/boot-seed.core ../../tests/scheme/compiler/constants-roundtrip.sps
(import (rnrs) (rnrs eval) (rnrs mutable-pairs) (rnrs mutable-strings) (cleat scheme) (cleat scheme clr)
        (cleat scheme compiler constants))

(clr-reference cleat-emit)

(define-record-type point (fields x (mutable y)) (nongenerative point-uid-iron020))
(define-record-type gpoint (fields a))

(define shared (list 1 2 3))
(define cyc (list 'a 'b))
(set-cdr! (cdr cyc) cyc)
(define selfvec (vector 1 2))
(vector-set! selfvec 1 selfvec)
(define stx #'(lambda (x) (car x)))
(define long-list (let loop ((i 0) (acc '())) (if (= i 50000) acc (loop (+ i 1) (cons i acc)))))

(define atoms
  (list 1 -7 (expt 2 100) 2/3 1.5 -0.0 +inf.0 1+2i 1.5+2.5i #\a #\x1F600 "str\x3bb;\"\\" 'sym
        (string->symbol "with space") (gensym 'g) '() #t #f (if #f #f) (eof-object) #vu8(1 2 255) (vector 'x "y")))

(define objs
  (list atoms                                   ; 0
        (cons shared shared)                    ; 1
        cyc                                     ; 2
        selfvec                                 ; 3
        (make-point 1 (make-point 2 3))         ; 4
        (make-gpoint 'g)                        ; 5
        (make-gpoint 'h)                        ; 6
        stx                                     ; 7
        (make-string 3 #\m)                     ; 8
        (condition (make-message-condition "msg") (make-irritants-condition '(1 2)))   ; 9
        long-list                               ; 10
        +nan.0                                  ; 11
        shared))                                ; 12：与 1 共享

(define (lcil-text encs)
  (call-with-string-output-port
    (lambda (p)
      (put-string p "(lcil 1 (assembly \"roundtrip\" (references \"Cleat.Scheme\") (runtime scheme)) (constants")
      (let loop ((i 0) (encs encs))
        (unless (null? encs)
          (put-string p " (")
          (put-string p (number->string i))
          (put-char p #\space)
          (write-lcil-datum (car encs) p)
          (put-char p #\))
          (loop (+ i 1) (cdr encs))))
      (put-string p ") (type \"C\" (attrs abstract sealed) (method \"Get\" (attrs static) (params \"System.Int32\") (ret \"System.Object\")")
      (put-string p " (body (ldsfld \"$Constants\" \"c\") (ldarg 0) (ldelem.ref) (ret)))))"))))

(define encs (encode-constants objs))
(define asm (clr-static-call Cleat.Emit.LcilEmitter (EmitInMemory String String) (lcil-text encs) "roundtrip"))
(define get-method (clr-call System.Type GetMethod (clr-call System.Reflection.Assembly GetType asm "C") "Get"))
(define (get k) (clr-call System.Reflection.MethodBase Invoke get-method '() (vector k)))

(define (iota n) (let loop ((i (- n 1)) (acc '())) (if (< i 0) acc (loop (- i 1) (cons i acc)))))

(define failures 0)
(define checks 0)
(define-syntax check
  (syntax-rules ()
    ((_ name expr)
     (begin
       (set! checks (+ checks 1))
       (unless expr
         (set! failures (+ failures 1))
         (display "FAIL: ") (display name) (newline))))))

(let ((d (get 0)))
  (check "atoms length" (= (length d) (length atoms)))
  (for-each
    (lambda (a b i)
      (check (string-append "atom " (number->string i))
             (cond
               ((and (flonum? a) (flzero? a)) (and (flzero? b) (eqv? (fl/ 1.0 a) (fl/ 1.0 b))))
               ((unspecified? a) (unspecified? b))
               (else (equal? a b)))))
    atoms d (iota (length atoms)))
  (check "symbol interned" (eq? (list-ref d 12) 'sym))
  (check "gensym identity" (eq? (list-ref d 14) (list-ref atoms 14))))
(let ((d (get 1))) (check "shared pair" (eq? (car d) (cdr d))) (check "shared across constants" (eq? (car d) (get 12))))
(let ((d (get 2))) (check "cycle" (eq? (cddr d) d)))
(let ((d (get 3))) (check "self vector" (eq? (vector-ref d 1) d)))
(let ((d (get 4)))
  (check "nongenerative record type" (point? d))
  (check "record fields" (and (= (point-x d) 1) (= (point-y (point-y d)) 3)))
  (point-y-set! d 9)
  (check "mutable field" (= (point-y d) 9)))
(let ((g (get 5)) (h (get 6)))
  (check "generative rtd shared in table" (eq? (record-rtd g) (record-rtd h)))
  (check "generative rtd is new" (not (gpoint? g)))
  (check "generative fields" (eq? ((record-accessor (record-rtd h) 0) h) 'h)))
(let ((d (get 7)))
  (check "syntax datum" (equal? (syntax->datum d) '(lambda (x) (car x))))
  (syntax-case d ()
    ((lam (x) (f y))
     (begin
       (check "free-identifier=? lambda" (free-identifier=? #'lam #'lambda))
       (check "free-identifier=? car" (free-identifier=? #'f #'car))
       (check "bound-identifier=? x" (bound-identifier=? #'x #'y))
       (check "eval decoded syntax" (equal? ((eval (syntax->datum d) (environment '(rnrs))) '(5 6)) 5))))))
(let ((d (get 8)))
  (check "mutable string" (equal? d "mmm"))
  (string-set! d 0 #\x)
  (check "mutable string set" (equal? d "xmm")))
(let ((d (get 9)))
  (check "condition" (and (message-condition? d) (equal? (condition-message d) "msg")))
  (check "compound condition" (equal? (condition-irritants d) '(1 2))))
(let ((d (get 10))) (check "long list" (and (= (length d) 50000) (= (car d) 49999))))
(let ((d (get 11))) (check "nan" (nan? d)))

(display "constants round-trip: ")
(display (- checks failures))
(display " of ")
(display checks)
(display " checks passed")
(newline)
(unless (= failures 0) (exit 1))
