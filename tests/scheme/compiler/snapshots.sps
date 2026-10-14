;;; 快照测试（iron-022 ~ iron-026）：手写 Lcore 经各 pass 后的 L0、L2、L3 与 Lcil 文本，与 tests/scheme/compiler/snapshots/ 下的文件比较（在 src/scheme 目录下运行）。
;;; 文件不存在时生成；不一致时写出 .actual 文件并失败。更新快照：删除对应文件后重跑。
(import (rnrs) (cleat scheme) (nanopass)
        (cleat scheme compiler languages) (cleat scheme compiler parse) (cleat scheme compiler front)
        (cleat scheme compiler closures) (cleat scheme compiler) (cleat scheme compiler loops) (cleat scheme compiler inline)
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
    ;; iron-070：夹在过程定义之间的常量绑定（n 还被 set!）并入同一组、提到 letrec 之前，f / g 不再是前向变量
    ;; （不装箱、不检查是否已初始化）；组内的调用直接调子句方法（f 是闭包：castclass 后调实例方法）
    (letrec-const-run
     (case-lambda
       ((xs)
        (letrec* ((f (case-lambda ((y) (if (null? y) n (g (cdr y))))))
                  (n '0)
                  (g (case-lambda ((z) (begin (set! n ($fx+ n '1)) (f z))))))
          (f xs)))))
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
    ;; eqv? 改 eq?：一边是符号 / 布尔 / 空表常量时改 eq?，flonum、fixnum、字符常量保持 eqv?（装箱，相等不一定同一对象）；
    ;; 测试位置的 memv 常量表展开成比较链（符号用 eq?，混入的 flonum、字符用 eqv?），非测试位置的 memv 不动
    (eqv-to-eq
     (case-lambda
       ((x)
        (vector
          (eqv? x 'a) (eqv? '#t x) (eqv? x '())
          (eqv? x '1.5) (eqv? x '7) (eqv? x '#\a)
          (if (g$memv$1$%b x '(less more)) '1 '2)
          (if (g$memv$2$%b x '(a 1.5 #\c)) '3 '4)
          (g$memv$3$%b x '(a b))))))
    ;; iron-070：过程内联——sq 内联后参数 3 传播进体，(* 3 3) 折叠成 9；sq 不再被引用，绑定删掉
    (inline-fold
     (case-lambda
       ((a)
        (letrec ((sq (case-lambda ((x) (g$*$1$%b x x)))))
          (g$+$2$%b (sq '3) (sq a))))))
    ;; iron-070：超出节点预算的过程不内联（big 的体远大于 24 个节点），小的 small 照常内联
    (inline-budget
     (case-lambda
       ((a)
        (letrec ((big (case-lambda
                        ((x) (vector (g$+$1$%b x '1) (g$+$2$%b x '2) (g$+$3$%b x '3) (g$+$4$%b x '4)
                                     (g$+$5$%b x '5) (g$+$6$%b x '6) (g$+$7$%b x '7) (g$+$8$%b x '8)))))
                 (small (case-lambda ((x) (cons x x)))))
          (cons (big a) (small a))))))
    ;; iron-070：递归过程不展开（fact 的体引用自己），参数被 set! 的变量绑定的过程不内联（h 被赋值）
    (inline-recursive-assigned
     (case-lambda
       ((n)
        (letrec ((fact (case-lambda ((k) (if (g$=$1$%b k '0) '1 (g$*$2$%b k (fact (g$-$3$%b k '1))))))))
          ((case-lambda
             ((h)
              (begin
                (set! h (case-lambda ((y) y)))
                (cons (fact n) (h n)))))
           (case-lambda ((z) (cons z z))))))))
    ;; iron-070：检查 + 退回——vector-set! 常量下标不检查下标类型，vector-ref 未知下标检查 isinst int32，都有界检查与慢路径；
    ;; car 是 isinst Cons 后直接取字段，否则调 Builtins.Car
    (vector-ops
     (case-lambda
       ((v i x)
        (begin
          (g$vector-set!$1$%b v '0 x)
          (cons (g$vector-ref$2$%b v i) (car x))))))
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
    ;; iron-071：拆箱循环——i、s 的回边实参是通用 - / +，推测为 fixnum：入口检查 n 是 fixnum 进快循环（int32 局部变量，
    ;; 回边按 int64 算、检查范围），否则 / 溢出时装箱进慢循环；ys 的回边是 cdr，不推测
    (spec-loop
     (case-lambda
       ((n xs)
        ((letrec ((loop (case-lambda
                          ((i s ys)
                           (if (g$<$1$%b i '0)
                               (cons s ys)
                               (loop (g$-$2$%b i '1) (g$+$3$%b i s) (g$cdr$4$%b ys)))))))
           loop)
         n '0 xs))))
    ;; iron-071：谓词收窄——(fixnum? x) 为真的分支里 x 上的 + 与 < 直接拆箱、不检查类型；y 不收窄；
    ;; (and (flonum? a) (flonum? b)) 两个都收窄；分支里给 z 赋值时 z 不收窄
    (narrow-predicate
     (case-lambda
       ((x y a b z)
        (begin
          (if (g$fixnum?$1$%b x) (g$+$2$%b x y) '#f)
          (if (if (g$flonum?$3$%b a) (g$flonum?$3$%b b) '#f) (g$*$4$%b a b) '#f)
          (if (g$fixnum?$1$%b z) (begin (set! z y) (g$<$5$%b z '0)) '#f)))))
    ;; iron-073：if 化简——(not t) 交换两支；外层 (pair? x) 为假的分支里内层同一测试直接选支；(and a b) 为真时 a、b 都真；
    ;; (if (null? y) #t #f) 结果必是布尔 → (null? y)；两支是同一常量 → 只留常量（测试无副作用）；被 set! 的 z 不参与
    (simplify-if
     (case-lambda
       ((x y a b z)
        (begin
          (set! z a)
          (cons (if (not (pair? x)) (if (pair? x) (car x) '2) '1)
                (cons (if (if a b '#f) (if a (if b '3 '4) '5) '6)
                      (cons (if (if (null? y) '#t '#f) '7 '8)
                            (cons (if (null? y) '#t '#f)
                                  (cons (if (eq? x y) '9 '9)
                                        (if z (if z '10 '11) '12))))))))))
    ;; iron-073：commonize——只差局部变量名的两个闭合 lambda 共用一个过程字段；体不同或有自由变量（f）的不共享
    (commonize
     (case-lambda
       ((f)
        (cons (case-lambda ((a) ($fx+ a '1)))
              (cons (case-lambda ((b) ($fx+ b '1)))
                    (cons (case-lambda ((c) ($fx+ c '2)))
                          (case-lambda ((d) ($fx+ d f)))))))))
    ;; iron-071：类型格——cond 收窄：(list? l) 为真时 l 是 list，再经 (null? l) 的假分支成 pair，car 不检查；
    ;; (pair? m) 的假分支不收窄，真分支里 (pair? m) 直接是 #t；被 set! 的 w 不收窄
    (narrow-cond
     (case-lambda
       ((l m w)
        (begin
          (if (g$list?$1$%b l) (if (null? l) '0 (car l)) '#f)
          (if (pair? m) (if (pair? m) (cdr m) '#f) (g$vector?$2$%b m))
          (if (pair? w) (begin (set! w (cdr w)) (car w)) '#f)))))
    ;; iron-071：已执行的操作收窄——(car q) 之后 q 是 pair，(cdr q) 不检查、(pair? q) 是 #t；(vector-set! v i a) 之后
    ;; v 是 vector（vector-ref 不检查向量）、i 是 fixnum（不检查下标类型）
    (narrow-effect
     (case-lambda
       ((q v i)
        ((case-lambda
           ((a b)
            (begin
              (g$vector-set!$1$%b v i a)
              (cons (g$vector-ref$2$%b v i) (if (pair? q) b '#f)))))
         (car q) (cdr q)))))
    ;; iron-071：签名传播与 label 合流——n 由 vector-length 定为 int32；p 由 cons 定为 pair，car 不检查；
    ;; acc 的定义是 '0 与 (+ acc (vector-length v))，合流为精确整数：+ 只有 fixnum 路径，没有 flonum 路径
    (sig-lattice
     (case-lambda
       ((v)
        ((case-lambda
           ((n p)
            (cons (car p)
                  ((letrec ((loop (case-lambda
                                    ((i acc)
                                     (if ($fx<? i n)
                                         (loop ($fx+ i '1) (g$+$2$%b acc (g$vector-length$1$%b v)))
                                         acc)))))
                     loop)
                   '0 '0))))
         (g$vector-length$1$%b v) (cons v '())))))
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
           (p0 (begin (reset-fresh-names!) (parse-core lcore)))
           (l0 (lower-loops p0))
           (an (analyze l0))
           (l1 (cp0-lite (check-forward (simplify-letrec l0 an) an)))
           (l1 (if inline-enabled? (cp0-lite (simplify-lets (cp0-inline l1 an) an)) l1))   ; 与驱动相同（iron-070）
           (l1 (simplify-if l1))                                                              ; iron-073
           (l2 (convert-assignments l1 an))
           (l3 (uncover-free l2)))
      ;; p0（parse-core 的输出）与 l1（convert-assignments 的输入）：Clojure 编译器逐 pass 对照用（cleat-084）
      (check! name "p0" (pp->string (unparse-L0 p0)))
      (check! name "l0" (pp->string (unparse-L0 l0)))
      (check! name "l1" (pp->string (unparse-L1 l1)))
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

(display "snapshots: ") (display (* 6 (length cases))) (display " compared, ")
(display created) (display " created, ") (display failures) (display " failed") (newline)
(unless (= failures 0) (exit 1))
