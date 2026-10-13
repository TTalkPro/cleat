;;; 快照测试（iron-022 ~ iron-026）：手写 Lcore 经各 pass 后的 L0、L2、L3 与 Lcil 文本，与 tests/scheme/compiler/snapshots/ 下的文件比较（在 src/scheme 目录下运行）。
;;; 文件不存在时生成；不一致时写出 .actual 文件并失败。更新快照：删除对应文件后重跑。
(import (rnrs) (cleat scheme) (nanopass)
        (cleat scheme compiler languages) (cleat scheme compiler parse) (cleat scheme compiler front)
        (cleat scheme compiler closures) (cleat scheme compiler) (cleat scheme compiler loops)
        (only (cleat scheme compiler lcil) lcil->string))

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
    ;; iron-069：查表驱动的常量折叠与丢弃——boot 过程（$%b 位置）的常量调用折叠（fx+、bitwise-and、char-upcase、
    ;; vector-ref、car、vector-length、=），C# 原语折叠（$fx+、not、null?），变参 + 的常量部分先合并，
    ;; fx 溢出不折叠，除零不折叠，用户自己的 fx+（位置不是 boot 的）不折叠，begin 中 cons / pair? 的调用删掉而 car 的保留
    (cp0-fold
     (case-lambda
       ((x y)
        (begin
          (g$cons$1$%b x '1)
          (g$pair?$2$%b x)
          (g$car$3$%b x)
          (if (g$=$4$%b '1 '1.0)
              (g$+$5$%b '1 '2 x '3)
              (g$fx+$6$%b '2147483647 '1))
          (g$fxdiv$7$%b '7 '0)
          (g$fx+$8$Cuser '1 '2)
          (vector
            (g$fx+$9$%b '1 '2)
            (g$bitwise-and$10$%b '6 '3)
            (g$char-upcase$11$%b '#\a)
            (g$vector-ref$12$%b '#(a b) '1)
            (g$car$13$%b '(1 2))
            (g$vector-length$14$%b '#(1 2 3))
            ($fx+ '1 '2)
            (not '#f)
            (null? '())
            (g$expt$15$%b '2 '10)
            (g$string-ref$16$%b '"abc" '1))))))
    ;; iron-069：签名给类型推断——n 由 vector-length 定为 int32，循环里 (fx<? i n) 不再检查 n
    (sig-typed-loop
     (case-lambda
       ((v)
        ((letrec ((loop (case-lambda
                          ((i n acc)
                           (if (g$fx<?$1$%b i n)
                               (loop (g$fx+$2$%b i '1) n (g$+$3$%b acc (g$char->integer$4$%b (g$string-ref$5$%b v i))))
                               acc)))))
           loop)
         '0 (g$string-length$6$%b v) '0))))
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
    ;; iron-066：通用算符的 flonum 路径与类型推断——x 与 acc 由 flonum 常量和通用 + * 定为 double，循环内没有类型检查；
    ;; (< x n) 的 x 已知是 flonum，不生成 fixnum 路径，只检查 n；(/ n '2) 没有 fixnum 路径（结果是分数），只有 flonum 路径与慢路径
    (generic-fl-loop
     (case-lambda
       ((n)
        ((letrec ((loop (case-lambda
                          ((x acc)
                           (if (g$<$1$%b x n)
                               (loop (g$+$2$%b x '1.0) (g$+$3$%b acc (g$*$4$%b x '0.5)))
                               (cons acc (g$/$5$%b n '2)))))))
           loop)
         '0.0 '0.0))))
    ;; iron-068：k 只在空栈位置被直接调用的 call-with-return 变成 escape-frame，(k v) 是"存值、br 到出口"，
    ;; 不建逃逸续延对象、没有 try 区域；循环先降级为 label，k 的调用在 label 体的语句位置
    (local-escape
     (case-lambda
       ((xs)
        (call-with-return
          (case-lambda
            ((k)
             ((letrec ((loop (case-lambda
                               ((l) (if (pair? l)
                                        (begin (if (null? (car l)) (k l) '#f) (loop (cdr l)))
                                        '#f)))))
                loop)
              xs)))))))
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
