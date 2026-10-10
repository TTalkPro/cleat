;;; 快照测试（iron-022 ~ iron-026）：手写 Lcore 经各 pass 后的 L0、L2、L3 与 Lcil 文本，与 tests/scheme/compiler/snapshots/ 下的文件比较（在 src/scheme 目录下运行）。
;;; 文件不存在时生成；不一致时写出 .actual 文件并失败。更新快照：删除对应文件后重跑。
(import (rnrs) (ironscheme) (nanopass)
        (ironscheme compiler languages) (ironscheme compiler parse) (ironscheme compiler front)
        (ironscheme compiler closures) (ironscheme compiler) (ironscheme compiler loops)
        (only (ironscheme compiler lcil) lcil->string))

(define cases
  '((closure
     (case-lambda ((x) (case-lambda ((y) ($fx+ x y))))))
    (named-let-loop
     ((letrec ((loop (case-lambda ((i acc) (if ($fx=? i '10) acc (loop ($fx+ i '1) (cons i acc)))))))
        loop)
      '0 '()))
    (letrec-forward
     (case-lambda
       ((n)
        (letrec* ((f (case-lambda ((x) (g x))))
                  (g (case-lambda ((y) (if (null? y) total (f (cdr y))))))
                  (total ($fx+ n '1))
                  (bump (case-lambda (() (set! n ($fx+ n '1)) n))))
          (begin (bump) (f '(1 2)))))))
    (case-lambda-rest
     (case-lambda ((a) a) ((a b) (cons a b)) ((a b c d e) (vector a b c d e)) ((a . r) r)))
    ;; iron-034f：boot 的通用算术在调用处内联（常量操作数、测试上下文、尾位置的慢路径、
    ;; 三参数不展开、用户自己定义的 +（位置不是 boot 的）不展开、fx 的溢出检查、fl 的常量）
    (inline-arith
     (case-lambda
       ((a b)
        (if (g$<$1$%b a '10)
            (g$+$2$%b a b)
            (begin
              (g$fx+$3$%b a '1)
              (g$fl*$4$%b b '2.5)
              (g$+$5$Cuser a b)
              (g$-$6$%b a b '1))))))
    ;; iron-034c：循环变量拆箱——i 由 $fx+ 定为 int32，acc 由 fl+ 定为 double，xs 不定类型；
    ;; 被闭包捕获的 j 不拆箱
    (typed-loop
     (case-lambda
       ((n xs)
        ((letrec ((loop (case-lambda
                          ((i acc ys)
                           (if ($fx=? i n)
                               (cons acc ys)
                               (loop ($fx+ i '1) (g$fl+$1$%b acc '1.5) (cons i ys)))))))
           loop)
         '0 '0.0 xs)
        ((case-lambda ((j) (case-lambda (() j)))) ($fx+ n '1)))))
    ;; iron-034（D6）：消费者是字面 lambda 的 call-with-values 内联；个数不符时走原来的形式报错
    (inline-values
     (case-lambda
       ((p)
        (call-with-values (case-lambda (() (p))) (case-lambda ((a b . r) (cons a (cons b r))))))))
;; iron-034：let 绑定、只在尾位置调用的 lambda 变成 label（nanopass 处理器的 rhs / else-th 形状），不生成闭包类
    (let-lambda-labels
     (case-lambda
       ((x)
        ((case-lambda ((rhs else-th) (if (pair? x) (rhs (car x)) (else-th))))
         (case-lambda ((d) (cons d d)))
         (case-lambda (() '#f))))))
    ;; iron-034（D6）：typed-lambda 的 Int32 / Double 形参用 int32 / double 局部变量
    (typed-lambda-args
     (case-lambda
       ((a b)
        ((typed-case-lambda ((x y) ((Int32 Double) Object) (cons ($fx* x x) ($fl* y y)))) a b))))
    ;; iron-034（D6）：define-record-type 的访问器展开为调用 $record-field-ref 的 lambda，库中是已知过程，调用点直接调用
    (record-accessor
     (library-letrec* (snapshot rec)
       ((rtd g$rtd (make-record-type-descriptor 'p '#f '#f '#f '#f '#((immutable x))))
        (px g$px (case-lambda ((r) ($record-field-ref rtd '0 r))))
        (p? g$p? (case-lambda ((o) ($record-instance? rtd o)))))
       (begin (p? '1) (px '1))))
    ;; cleat-047：$try/catch → try-catch → Lcil 的 try/catch（类型 #f 即 SchemeException，处理器收到其 Condition）；
    ;; 处理器先求值；try 体内的调用不发 tail.
    (try-catch
     (case-lambda
       ((th h)
        ($try/catch (th) '#f (case-lambda ((c) (h c))) '"System.InvalidOperationException" (case-lambda ((e) e))))))
    (library
     (library-letrec* (snapshot lib)
       ((u g$u (clr-using-internal 'System.Text))
        (len g$len (case-lambda ((l) (if (null? l) '0 ($fx+ '1 (len (cdr l)))))))
        (sum g$sum (case-lambda ((l acc) (if (null? l) acc (sum (cdr l) ($fx+ acc (car l)))))))
        (sb g$sb (case-lambda ((s) (clr-call-internal 'StringBuilder 'ToString (clr-new-internal 'StringBuilder s)))))
        (safe g$safe (case-lambda ((th) ($try/finally (th) (g$cleanup)))))
        (table g$table (cons 'a '#(1 "x" 2.5))))
       (begin (len '(1 2 3)) (sum '(1 2) '0))))))

(define (pp->string x) (call-with-string-output-port (lambda (p) (pretty-print x p))))

(define failures 0)
(define created 0)

(define (check! name stage text)
  (let ((file (string-append "../../tests/scheme/compiler/snapshots/" (symbol->string name) "." stage)))
    (cond
      ((not (file-exists? file))
       (call-with-output-file file (lambda (p) (put-string p text)))
       (set! created (+ created 1)))
      ((not (string=? text (call-with-input-file file get-string-all)))
       (let ((actual (string-append file ".actual")))
         (when (file-exists? actual) (delete-file actual))
         (call-with-output-file actual (lambda (p) (put-string p text)))
         (set! failures (+ failures 1))
         (display "MISMATCH: ") (display file) (newline))))))

(for-each
  (lambda (c)
    (let* ((name (car c)) (lcore (cadr c))
           (l0 (begin (reset-fresh-names!) (lower-loops (parse-core lcore))))
           (an (analyze l0))
           (l2 (convert-assignments (cp0-lite (check-forward (simplify-letrec l0 an) an)) an))
           (l3 (uncover-free l2)))
      (check! name "l0" (pp->string (unparse-L0 l0)))
      (check! name "l2" (pp->string (unparse-L2 l2)))
      (check! name "l3" (pp->string (unparse-L3 l3)))
      (check! name "lcil" (compile->lcil-text (list lcore) (symbol->string name) "#Module"))))
  cases)

;; 确定性：同一输入编译两次，Lcil 逐字相同
(for-each
  (lambda (c)
    (unless (string=? (compile->lcil-text (list (cadr c)) "d" "#Module") (compile->lcil-text (list (cadr c)) "d" "#Module"))
      (set! failures (+ failures 1))
      (display "NONDETERMINISTIC: ") (display (car c)) (newline)))
  cases)

(display "snapshots: ") (display (* 4 (length cases))) (display " compared, ")
(display created) (display " created, ") (display failures) (display " failed") (newline)
(unless (= failures 0) (exit 1))
