#| License
Copyright (c) 2026 the IronScheme contributors
All rights reserved.
This source code is subject to terms and conditions of the BSD License.
See docs/license.txt. |#

;;; lower-loops：L0 → L0，接在 parse-core 之后（IronScheme iron-034b）。
;;; 从 IronScheme 的 (cleat scheme compiler async) 拆出（cleat-046）：那个库的 lower-async（async-lambda 体的状态机整理）
;;; 随 async 一起不纳入；这里只保留它的通用工具（kids、check-refs、jumpify、fresh）与 lower-loops 本身，一字未改。
;;;
;;;   D9  单绑定 letrec 的 lambda，全部引用都是参数个数相符的尾调用 ⇒ (label f (x …) body fbody)，调用 ⇒ (jump f e …)
;;;   let 绑定、只在尾位置调用的 lambda 也变成 label；消费者是字面 lambda 的 call-with-values 内联（D6）
(library (cleat scheme compiler loops)
  (export lower-loops reset-fresh-names! loops-made)
  (import (rnrs) (only (cleat scheme unsafe) $get-environment-variable) (nanopass) (cleat scheme compiler languages))

  ;; —— 通用工具 ——

  (define (clause-body cl)
    (nanopass-case (L0 Clause) cl [(clause (,x* ...) ,x ,body) body]))

  ;; 子表达式（不含绑定信息）
  (define (kids e)
    (nanopass-case (L0 Expr) e
      [,x '()]
      [(quote ,d) '()]
      [(if ,e0 ,e1 ,e2) (list e0 e1 e2)]
      [(begin ,e* ... ,e) (append e* (list e))]
      [(set! ,x ,e) (list e)]
      [(gref ,s) '()]
      [(gset! ,s ,e) (list e)]
      [(mref ,x) '()]
      [(mset! ,x ,e) (list e)]
      [(prim ,pr) '()]
      [(primcall ,pr ,e* ...) e*]
      [(lambda ,nm ,cl* ...) (map clause-body cl*)]
      [(letrec ([,x* ,e*] ...) ,body) (append e* (list body))]
      [(let ([,x* ,e*] ...) ,body) (append e* (list body))]
      [(library ,d ([,x* ,s* ,e*] ...) ,body) (append e* (list body))]
      [(call ,e ,e* ...) (cons e e*)]
      [(and ,e* ...) e*]
      [(or ,e* ...) e*]
      [(throw ,e) (list e)]
      [(break) '()]
      [(try-finally ,e0 ,e1) (list e0 e1)]
      [(try-catch ,e (,d* ,e*) ...) (cons e e*)]
      [(label ,x (,x* ...) ,body0 ,body1) (list body0 body1)]
      [(jump ,x ,e* ...) e*]
      [(host-call ,d0 ,d1 ,st ,e* ...) e*]
      [(host-field-get ,d0 ,d1 ,st ,e* ...) e*]
      [(host-field-set ,d0 ,d1 ,st ,e* ...) e*]
      [(host-new ,d ,e* ...) e*]
      [(host-new-array ,d ,e) (list e)]
      [(host-cast ,d ,e) (list e)]
      [(host-is ,d ,e) (list e)]
      [(host-typeof ,d) '()]
      [(host-using ,d) '()]
      [(host-reference ,d) '()]
      [(host-namespaces) '()]
      [(unsupported ,d ,e* ...) e*]
      [(clj-var-ref ,d ,st) '()]
      [(clj-var-set! ,d ,e) (list e)]
      [(clj-def ,d ,st0 ,st1 ,e0 ,e1) (list e0 e1)]
      [(clj-invoke ,e ,e* ...) (cons e e*)]
      [(clj-keyword-invoke ,d ,e ,e* ...) (cons e e*)]
      [(clj-call ,d0 ,d1 ,e* ...) e*]
      [(clj-static-call ,d0 ,d1 ,e* ...) e*]
      [(clj-case ,d ,e0 ,e1 ,e* ...) (cons* e0 e1 e*)]
      [(clj-deftype ,d (,x* ...) ,le* ...) le*]))


  (define (var-ref? e v)
    (nanopass-case (L0 Expr) e [,x (eq? x v)] [else #f]))

  ;; 单子句、无剩余参数的 lambda：返回 (values 形参表 体)，否则 (values #f #f)
  (define (simple-lambda e)
    (nanopass-case (L0 Expr) e
      [(lambda ,nm ,cl)
       (nanopass-case (L0 Clause) cl
         [(clause (,x* ...) ,x ,body) (if x (values #f #f) (values x* body))])]
      [else (values #f #f)]))


  (define counter 0)
  ;; 临时变量名按编译单元从头编号（驱动在每个单元开始时调用），输出确定
  (define (reset-fresh-names!) (set! counter 0))
  (define (fresh name)
    (set! counter (+ counter 1))
    (string->symbol (string-append name "%" (number->string counter))))

  ;; v 在 e 中的引用是否都是参数个数为 n 的尾调用。返回 #f（合格）或原因符号：
  ;; value（当值使用）、arity（参数个数不符）、non-tail（非尾位置调用）、closure（在嵌套 lambda 里引用）、assigned
  (define (check-refs v n e tail? in-lambda?)
    (define (r e tail?) (check-refs v n e tail? in-lambda?))
    (define (r* es) (exists (lambda (e) (r e #f)) es))
    (nanopass-case (L0 Expr) e
      [,x (and (eq? x v) (if in-lambda? 'closure 'value))]
      [(set! ,x ,e) (if (eq? x v) 'assigned (r e #f))]
      [(call ,e ,e* ...)
       (cond
         ((var-ref? e v)
          (cond
            (in-lambda? 'closure)
            ((not (= (length e*) n)) 'arity)
            ((not tail?) 'non-tail)
            (else (r* e*))))
         (else
          ;; 立即调用的 lambda：体在尾位置
          (let-values (((x* body) (simple-lambda e)))
            (if (and x* (= (length x*) (length e*)))
                (or (r* e*) (r body tail?))
                (or (r e #f) (r* e*))))))]
      [(if ,e0 ,e1 ,e2) (or (r e0 #f) (r e1 tail?) (r e2 tail?))]
      [(begin ,e* ... ,e) (or (r* e*) (r e tail?))]
      ;; case* 的分支与 default 在尾位置（与 if 相同）
      [(clj-case ,d ,e0 ,e1 ,e* ...) (or (r e0 #f) (r e1 tail?) (exists (lambda (b) (r b tail?)) e*))]
      [(let ([,x* ,e*] ...) ,body) (or (r* e*) (r body tail?))]
      [(letrec ([,x* ,e*] ...) ,body) (or (r* e*) (r body tail?))]
      [(lambda ,nm ,cl* ...)
       (exists (lambda (cl) (check-refs v n (clause-body cl) #f #t)) cl*)]
      [else (r* (kids e))]))

  ;; 把 e 的尾位置上对 v 的调用改成 jump
  (define (jumpify v node)
    (with-output-language (L0 Expr)
      (nanopass-case (L0 Expr) node
        [(call ,e ,e* ...)
         (cond
           ((var-ref? e v) `(jump ,v ,e* ...))
           (else
            (let-values (((x* body) (simple-lambda e)))
              (if (and x* (= (length x*) (length e*)))
                  `(call (lambda #f (clause (,x* ...) #f ,(jumpify v body))) ,e* ...)
                  node))))]
        [(if ,e0 ,e1 ,e2) `(if ,e0 ,(jumpify v e1) ,(jumpify v e2))]
        [(begin ,e* ... ,e) `(begin ,e* ... ,(jumpify v e))]
        [(clj-case ,d ,e0 ,e1 ,e* ...) `(clj-case ,d ,e0 ,(jumpify v e1) ,(map (lambda (b) (jumpify v b)) e*) ...)]
        [(let ([,x* ,e*] ...) ,body) `(let ([,x* ,e*] ...) ,(jumpify v body))]
        [(letrec ([,x* ,e*] ...) ,body) `(letrec ([,x* ,e*] ...) ,(jumpify v body))]
        [else node])))

  ;; —— lower-loops（iron-034b）：D9 推广到全部代码 ——
  ;; 单绑定 letrec 的 lambda，全部引用都是参数个数相符的尾调用（相对于 letrec 的体或 lambda 自己的体）时，
  ;; 改为 (label f (x …) body fbody) 与 (jump f e …)：命名 let 与 do 不再各生成一个过程类，循环变量是局部变量。
  ;; 只做 D9，不做 D10 / D11；跨 try、跨嵌套 lambda 的调用不算尾调用（check-refs），jump 不会跳出 try 区域。
  ;; ((letrec ((f le)) f) e …)（命名 let 的展开形状）先归一成 (letrec ((f le)) (f e …))。
  ;; 至今生成的 label 个数：编译驱动据此只对含循环的形式做类型推断（codegen 的 loop-forms）
  (define loops-count 0)
  ;; 测量开关：CLEAT_SCHEME_NO_034 含 letlab 时不做 let 的降级
  (define let-labels? (let ((v ($get-environment-variable "CLEAT_SCHEME_NO_034")))
                        (not (and (string? v) (let loop ((i 0)) (and (<= (+ i 6) (string-length v))
                                                                      (or (string=? (substring v i (+ i 6)) "letlab") (loop (+ i 1)))))))))
  (define (loops-made) loops-count)

  (define-pass lower-loops : L0 (ir) -> L0 ()
    (definitions
      ;; (lambda () body) → body，否则 #f
      (define (thunk-literal? e)
        (nanopass-case (L0 Expr) e
          [(lambda ,nm ,cl)
           (nanopass-case (L0 Clause) cl [(clause (,x* ...) ,x ,body) (and (null? x*) (not x) body)])]
          [else #f]))
      (define (consumer-literal? e)
        (nanopass-case (L0 Expr) e [(lambda ,nm ,cl) #t] [else #f]))
      (define (consumer-parts e)
        (nanopass-case (L0 Expr) e
          [(lambda ,nm ,cl) (nanopass-case (L0 Clause) cl [(clause (,x* ...) ,x ,body) (values nm x* x body)])]))
      ;; let 绑定的单子句 lambda，体里只在尾位置按相符的参数个数调用（不作为值、不在嵌套 lambda 里）：
      ;; 变成 label（nanopass 生成的处理器每个子句一个 rhs / else-th 闭包，每次调用都分配；变成 label 后不再分配）。
      ;; let 的右边看不到同一 let 绑定的名字，所以 lambda 体里不会跳到兄弟 label；label 依次嵌套在体外，
      ;; 其余绑定照旧放在最外层的 let 里（先求值，与原来顺序相同；lambda 的创建没有副作用）
      (define (lower-let x* e* body)
        (let loop ((xs x*) (es e*) (keep-x '()) (keep-e '()) (labels '()))
          (if (pair? xs)
              (let-values (((p* fbody) (simple-lambda (car es))))
                (if (and let-labels? p* (not (check-refs (car xs) (length p*) body #t #f)))
                    (loop (cdr xs) (cdr es) keep-x keep-e (cons (list (car xs) p* fbody) labels))
                    (loop (cdr xs) (cdr es) (cons (car xs) keep-x) (cons (car es) keep-e) labels)))
              (with-output-language (L0 Expr)
                (if (null? labels)
                    `(let ([,x* ,(map Expr e*)] ...) ,(Expr body))
                    (let* ((jbody (fold-left (lambda (b l) (jumpify (car l) b)) body labels))
                           (inner (fold-left (lambda (b l)
                                               (set! loops-count (+ loops-count 1))
                                               `(label ,(car l) (,(cadr l) ...) ,b ,(Expr (caddr l))))
                                             (Expr jbody)
                                             labels)))
                      (if (null? keep-x)
                          inner
                          `(let ([,(reverse keep-x) ,(map Expr (reverse keep-e))] ...) ,inner))))))))
      (define (named-letrec? e)
        (nanopass-case (L0 Expr) e
          [(letrec ([,x* ,e*] ...) ,body)
           (and (= (length x*) 1) (var-ref? body (car x*)) (cons (car x*) (car e*)))]
          [else #f])))
    (Expr : Expr (ir) -> Expr ()
      [(call ,e ,e* ...)
       (cond
         ((named-letrec? e)
          => (lambda (f.le)
               (Expr `(letrec ([,(car f.le) ,(cdr f.le)]) (call ,(car f.le) ,e* ...)))))
         (else
          (let-values (((p* b) (simple-lambda e)))
            (if (and p* (= (length p*) (length e*)))
                (lower-let p* e* b)                  ; 立即调用的 lambda 就是 let
                `(call ,(Expr e) ,(map Expr e*) ...)))))]
      ;; D6：(call-with-values (lambda () p) (lambda (x … [. r]) b)) →
      ;;   (let ((t p)) (if (个数相符 t) (let ((x ($values-ref t i)) … [(r ($values-tail t n))]) b) 原来的形式报错))
      ;; 错误分支用同名、同形参的空体 lambda 当消费者，由它报与原来相同的参数个数错误
      [(primcall ,pr ,e* ...)
       (guard (and (eq? pr 'call-with-values) (= (length e*) 2) (thunk-literal? (car e*)) (consumer-literal? (cadr e*))))
       (let-values (((nm x* rest cbody) (consumer-parts (cadr e*))))
         (let* ((t (fresh "values"))
                (n (length x*))
                (count `(primcall $values-count ,t))
                (test (if rest `(primcall $fx>=? ,count (quote ,n)) `(primcall $fx=? ,count (quote ,n))))
                (refs (let loop ((i 0) (xs x*))
                        (if (null? xs) '() (cons `(primcall $values-ref ,t (quote ,i)) (loop (+ i 1) (cdr xs))))))
                (bind-x* (if rest (append x* (list rest)) x*))
                (bind-e* (if rest (append refs (list `(primcall $values-tail ,t (quote ,n)))) refs)))
           `(let ([,t ,(Expr (thunk-literal? (car e*)))])
              (if ,test
                  (let ([,bind-x* ,bind-e*] ...) ,(Expr cbody))
                  (primcall call-with-values
                            (lambda #f (clause () #f ,t))
                            (lambda ,nm (clause (,(map (lambda (x) (fresh "arg")) x*) ...) ,(and rest (fresh "rest")) (quote #f))))))))]
      ;; let（也包括立即调用的 lambda，即展开器的 let）：见 definitions 中的 lower-let
      [(let ([,x* ,e*] ...) ,body) (lower-let x* e* body)]
      [(letrec ([,x* ,e*] ...) ,body)
       (let-values (((p* fbody) (if (= (length x*) 1) (simple-lambda (car e*)) (values #f #f))))
         (if (and p*
                  (let ((f (car x*)) (n (length p*)))
                    (not (or (check-refs f n body #t #f) (check-refs f n fbody #t #f)))))
             (let ((f (car x*)))
               (set! loops-count (+ loops-count 1))
               `(label ,f (,p* ...) ,(Expr (jumpify f body)) ,(Expr (jumpify f fbody))))
             `(letrec ([,x* ,(map Expr e*)] ...) ,(Expr body))))])))
