#| License
Copyright (c) 2026 the IronScheme contributors
All rights reserved.
This source code is subject to terms and conditions of the BSD License.
See docs/license.txt. |#

;;; 前中端 pass（designs/03-compiler.md §4 第 2 ~ 5 步，iron-023）。
;;;
;;;   analyze            L0 上的一遍分析：每个 letrec 右边引用了哪些同组变量、哪些词法变量被赋值、
;;;                      哪些被 lambda 捕获（出现在某个 lambda 的自由变量中）
;;;   simplify-letrec    L0 → L1：recognize-let（((lambda (x …) body) e …) → let）与 letrec 拆分：
;;;                      连续的、未被赋值的 lambda 绑定成为一组 letrec；其余绑定成为 let；
;;;                      右边引用了"之后才绑定"的变量时，这些变量先绑定为未初始化，再按顺序 set!
;;;   cp0-lite           L1 → L1：if 的常量测试、begin 中可丢弃的调用、原语与 boot 过程的常量折叠（查表，iron-069）
;;;   simplify-if        L1 → L1：if 的测试规范化、外层已知真假的测试在分支里折叠、常量分支（iron-073）
;;;   convert-assignments L1 → L2：既被赋值又被捕获的词法变量改为 Box
(library (cleat scheme compiler front)
  (export analyze simplify-letrec check-forward cp0-lite simplify-if convert-assignments
          analysis-assigned analysis-captured analysis-massigned)
  (import
    (rnrs)
    (only (cleat scheme) void)
    (only (cleat scheme unsafe) $get-environment-variable)
    (nanopass)
    (cleat scheme compiler languages)
    (cleat scheme compiler primitives)
    (cleat scheme compiler primdata))

  ;; 测量开关的值里是否含 sub（如 CLEAT_NO_070=letrec,call）
  (define (string-contains? s sub)
    (let ((n (string-length s)) (m (string-length sub)))
      (let loop ((i 0)) (and (<= (+ i m) n) (or (string=? (substring s i (+ i m)) sub) (loop (+ i 1)))))))

  ;; —— 小集合（无重复的列表）——
  (define (set-add x s) (if (memq x s) s (cons x s)))
  (define (set-union a b)
    (cond
      ((null? a) b)
      ((null? b) a)
      (else (fold-left (lambda (s x) (set-add x s)) b a))))
  (define (set-minus s xs) (filter (lambda (x) (not (memq x xs))) s))
  (define (union* ss) (fold-left set-union '() ss))

  (define-record-type analysis
    (fields assigned      ; eq 哈希表：被 set! 的词法变量
            captured      ; eq 哈希表：出现在某个 lambda 自由变量中的词法变量
            letrec-refs   ; eq 哈希表：letrec 右边（L0 记录）→ 它引用的同组变量
            massigned     ; eq 哈希表：被 mset! 的库变量（除初始化外还被赋值，不能当作已知过程直接调用）
            forward       ; eq 哈希表：simplify-letrec 引入的前向变量（读取时要检查是否已初始化）
            fields))      ; eq 哈希表：deftype* 的字段变量（cleat-051；被 set! 也不装箱）

  (define (analyze ir)
    (let ((assigned (make-eq-hashtable))
          (captured (make-eq-hashtable))
          (letrec-refs (make-eq-hashtable))
          (massigned (make-eq-hashtable))
          (fields (make-eq-hashtable)))
      ;; 返回 e 中自由出现的词法变量
      (define (Expr e)
        (nanopass-case (L0 Expr) e
          [,x (list x)]
          [(quote ,d) '()]
          [(if ,e0 ,e1 ,e2) (union* (list (Expr e0) (Expr e1) (Expr e2)))]
          [(begin ,e* ... ,e) (union* (map Expr (cons e e*)))]
          [(set! ,x ,e) (hashtable-set! assigned x #t) (set-add x (Expr e))]
          [(gref ,s) '()]
          [(gset! ,s ,e) (Expr e)]
          [(mref ,x) '()]
          [(mset! ,x ,e) (hashtable-set! massigned x #t) (Expr e)]
          [(prim ,pr) '()]
          [(primcall ,pr ,e* ...) (union* (map Expr e*))]
          [(lambda ,nm ,cl* ...)
           (let ((fv (union* (map Clause cl*))))
             (for-each (lambda (x) (hashtable-set! captured x #t)) fv)
             fv)]
          [(letrec ([,x* ,e*] ...) ,body)
           (let ((fvs (map Expr e*)))
             (for-each (lambda (rhs fv) (hashtable-set! letrec-refs rhs (filter (lambda (x) (memq x x*)) fv))) e* fvs)
             (set-minus (union* (cons (Expr body) fvs)) x*))]
          [(let ([,x* ,e*] ...) ,body) (set-union (union* (map Expr e*)) (set-minus (Expr body) x*))]
          ;; 局部循环（lower-loops 的输出）：label 绑定 x 与 x*
          [(label ,x (,x* ...) ,body0 ,body1) (set-minus (set-union (Expr body0) (Expr body1)) (cons x x*))]
          [(jump ,x ,e* ...) (set-add x (union* (map Expr e*)))]
          [(library ,d ([,x* ,s* ,e*] ...) ,body) (union* (cons (Expr body) (map Expr e*)))]
          [(call ,e ,e* ...) (union* (map Expr (cons e e*)))]
          [(and ,e* ...) (union* (map Expr e*))]
          [(or ,e* ...) (union* (map Expr e*))]
          [(throw ,e) (Expr e)]
          [(break) '()]
          [(try-finally ,e0 ,e1) (set-union (Expr e0) (Expr e1))]
          [(try-catch ,e (,d* ,e*) ...) (union* (cons (Expr e) (map Expr e*)))]
          ;; escape-frame 绑定 x；try-handle 在 body1 中绑定 x（cleat-076）
          [(escape-frame ,x ,body) (set-minus (Expr body) (list x))]
          [(try-handle ,x ,body0 ,body1) (set-union (Expr body0) (set-minus (Expr body1) (list x)))]
          [(host-call ,d0 ,d1 ,st ,e* ...) (union* (map Expr e*))]
          [(host-field-get ,d0 ,d1 ,st ,e* ...) (union* (map Expr e*))]
          [(host-field-set ,d0 ,d1 ,st ,e* ...) (union* (map Expr e*))]
          [(host-new ,d ,e* ...) (union* (map Expr e*))]
          [(host-new-array ,d ,e) (Expr e)]
          [(host-cast ,d ,e) (Expr e)]
          [(host-is ,d ,e) (Expr e)]
          [(host-typeof ,d) '()]
          [(host-using ,d) '()]
          [(host-reference ,d) '()]
          [(host-namespaces) '()]
          [(unsupported ,d ,e* ...) (union* (map Expr e*))]
          [(clj-var-ref ,d ,st) '()]
          [(clj-var-set! ,d ,e) (Expr e)]
          [(clj-def ,d ,st0 ,st1 ,e0 ,e1) (set-union (Expr e0) (Expr e1))]
          [(clj-invoke ,e ,e* ...) (union* (map Expr (cons e e*)))]
          [(clj-keyword-invoke ,d ,e ,e* ...) (union* (map Expr (cons e e*)))]
          [(clj-call ,d0 ,d1 ,e* ...) (union* (map Expr e*))]
          [(clj-static-call ,d0 ,d1 ,e* ...) (union* (map Expr e*))]
          [(clj-case ,d ,e0 ,e1 ,e* ...) (union* (map Expr (cons* e0 e1 e*)))]
          [(clj-deftype ,d (,x* ...) ,le* ...)
           ;; 字段是对象的字段：方法里的 set! 直接写字段，不装箱
           (for-each (lambda (x) (hashtable-set! fields x #t)) x*)
           (set-minus (union* (map Expr le*)) x*)]))
      (define (Clause cl)
        (nanopass-case (L0 Clause) cl
          [(clause (,x* ...) ,x ,body)
           (set-minus (Expr body) (if x (cons x x*) x*))]))
      (Expr ir)
      (make-analysis assigned captured letrec-refs massigned (make-eq-hashtable) fields)))

  (define (lambda-form? e)
    (nanopass-case (L0 Expr) e
      [(lambda ,nm ,cl* ...) #t]
      [else #f]))

;; —— simplify-letrec（含 recognize-let）：L0 → L1 ——
  ;;
  ;; letrec 的绑定 i 的右边引用了变量 v（v 在 i 处或之后绑定）时：若从 i 到 v 的绑定都是 lambda 且变量都未被赋值，
  ;; 它们会落在同一组 letrec 中，引用合法；否则 v 是"前向变量"：在整个结构外先绑定为未指定值，到它的位置再 set!。
  (define-pass simplify-letrec : L0 (ir an) -> L1 ()
    (definitions
      (define assigned (analysis-assigned an))
      (define (assigned? x) (hashtable-ref assigned x #f))
      (define refs (analysis-letrec-refs an))
      (define (groupable? x e) (and (lambda-form? e) (not (assigned? x))))
      ;; 常量绑定（右边是 quote）也并入 lambda 组，生成时提到该组的 letrec 之前用 let 绑定（iron-070，仿 Chez cpletrec）：
      ;; 组内全是 lambda，绑定时不执行代码，常量提前初始化观察不到。变量被 set! 也可以（仍是普通可变变量），
      ;; 只要它本身不是前向变量。否则 (define n 0) 这类夹在过程定义之间的常量会把过程切成几段，
      ;; 跨段调用的过程都成了前向变量：装箱、每次调用检查是否已初始化、按未知过程调用（sboyer 的 rewrite 等）
      (define const-in-run?
        (let ((v ($get-environment-variable "CLEAT_NO_070")))
          (not (and (string? v) (or (string=? v "1") (string-contains? v "letrec"))))))
      (define fwd (make-eq-hashtable))
      (define (const-binding? x e)
        (and const-in-run? (not (hashtable-ref fwd x #f))
             (nanopass-case (L0 Expr) e [(quote ,d) #t] [else #f])))
      (define (run-member? x e) (or (groupable? x e) (const-binding? x e)))
      ;; 从 x 到 v（含两端）的绑定都可并入同一组
      (define (same-run? x v x* e*)
        (let loop ((xs x*) (es e*) (started #f))
          (cond
            ((null? xs) #f)
            ((and (not started) (not (eq? (car xs) x))) (loop (cdr xs) (cdr es) #f))
            ((not (run-member? (car xs) (car es))) #f)
            ((eq? (car xs) v) #t)
            (else (loop (cdr xs) (cdr es) #t)))))
      (define (forward-vars x* e*)
        (let loop ((xs x*) (es e*) (acc '()))
          (if (null? xs)
              acc
              (let* ((r (hashtable-ref refs (car es) '()))
                     (bad (filter (lambda (v) (and (memq v xs) (not (same-run? (car xs) v x* e*)))) r)))
                (loop (cdr xs) (cdr es) (fold-left (lambda (s v) (set-add v s)) acc bad)))))))
    (Expr : Expr (ir) -> Expr ()
      [(call ,e ,[e*] ...)
       ;; recognize-let：((lambda (x …) body) e …) → (let ((x e) …) body)
       (or (nanopass-case (L0 Expr) e
             [(lambda ,nm ,cl)
              (nanopass-case (L0 Clause) cl
                [(clause (,x* ...) ,x ,body)
                 (and (not x) (= (length x*) (length e*))
                      `(let ([,x* ,e*] ...) ,(Expr body)))])]
             [else #f])
           `(call ,(Expr e) ,e* ...))]
      [(letrec ([,x* ,e*] ...) ,[body])
       ;; 前向变量会被标为已赋值而不能并组，可能把原本连续的 lambda 段拆开、产生新的前向引用：反复计算到不再变化
       (let ((forward
              (let loop ((acc '()))
                (let ((new (filter (lambda (v) (not (memq v acc))) (forward-vars x* e*))))
                  (if (null? new)
                      acc
                      (begin
                        (for-each (lambda (v) (hashtable-set! assigned v #t) (hashtable-set! fwd v #t)) new)
                        (loop (append acc new))))))))
         (for-each (lambda (v) (hashtable-set! assigned v #t) (hashtable-set! (analysis-forward an) v #t)) forward)
         (let ((inner
                (let loop ((xs x*) (es e*))
                  (cond
                    ((null? xs) body)
                    ((run-member? (car xs) (car es))
                     ;; 一组：常量 cx / ce 提到前面用 let 绑定，lambda gx / ge 进 letrec
                     (let group ((xs xs) (es es) (gx '()) (ge '()) (cx '()) (ce '()))
                       (cond
                         ((and (pair? xs) (groupable? (car xs) (car es)))
                          (group (cdr xs) (cdr es) (cons (car xs) gx) (cons (LambdaOf (car es)) ge) cx ce))
                         ((and (pair? xs) (const-binding? (car xs) (car es)))
                          (group (cdr xs) (cdr es) gx ge (cons (car xs) cx) (cons (Expr (car es)) ce)))
                         (else
                          (let ((rest (if (null? gx)
                                          (loop xs es)
                                          `(letrec ([,(reverse gx) ,(reverse ge)] ...) ,(loop xs es)))))
                            (if (null? cx) rest `(let ([,(reverse cx) ,(reverse ce)] ...) ,rest)))))))
                    ((memq (car xs) forward)
                     `(begin (set! ,(car xs) ,(Expr (car es))) ,(loop (cdr xs) (cdr es))))
                    (else
                     `(let ([,(car xs) ,(Expr (car es))]) ,(loop (cdr xs) (cdr es))))))))
           (if (null? forward)
               inner
               `(let ([,forward ,(map (lambda (v) `(primcall $uninitialized)) forward)] ...) ,inner))))])
    (LambdaOf : LambdaExpr (ir) -> LambdaExpr ())
    (Expr ir))

;; —— check-forward：L1 → L1 ——
  ;; 前向变量在赋值之前可能被读到（R6RS letrec 限制要求此时报错）：对它们的引用包上 $check-initialized
  (define-pass check-forward : L1 (ir an) -> L1 ()
    (definitions
      (define (forward? x) (hashtable-ref (analysis-forward an) x #f)))
    (Expr : Expr (ir) -> Expr ()
      [,x (if (forward? x) `(primcall $check-initialized ,x (quote ,x)) x)]))

  ;; —— cp0-lite：L1 → L1 ——
  ;; 常量折叠查表驱动（iron-069）：C# 原语按 primitives.ss 的 mifoldable 标志 + primdata 的折叠实现；
  ;; boot 导出的过程（+ fx+ vector-ref car …在用户代码里是 boot 位置的全局调用）按位置符号名字查 primdata。
  ;; effect 上下文（begin 的中间项）删掉标 discard 且 unrestricted、参数都简单的调用。
  ;; 测量开关：CLEAT_NO_069 含 fold / discard 时分别关掉，为 1 时全关。
  (define no-069 (or ($get-environment-variable "CLEAT_NO_069") ""))
  (define (opt-069-off? name)
    (or (string=? no-069 "1")
        (let ((n (string-length name)) (m (string-length no-069)))
          (let loop ((i 0)) (and (<= (+ i n) m) (or (string=? (substring no-069 i (+ i n)) name) (loop (+ i 1))))))))
  (define fold-on? (not (opt-069-off? "fold")))
  (define discard-on? (not (opt-069-off? "discard")))
  ;; (list e ...) 改嵌套 cons（iron-070）：CLEAT_NO_069 含 fold，或 CLEAT_NO_070 为 1 / 含 list 时关
  (define list-cons?
    (and fold-on?
         (let ((v ($get-environment-variable "CLEAT_NO_070")))
           (not (and (string? v) (or (string=? v "1") (string-contains? v "list")))))))

  (define-pass cp0-lite : L1 (ir) -> L1 ()
    (definitions
      (define (const? e) (nanopass-case (L1 Expr) e [(quote ,d) #t] [else #f]))
      (define (const-value e) (nanopass-case (L1 Expr) e [(quote ,d) d] [else #f]))
      ;; 简单表达式：求值没有副作用也不会报错
      (define (simple? e) (nanopass-case (L1 Expr) e [(quote ,d) #t] [,x #t] [else #f]))
      ;; boot 过程的调用：(gref 位置) 的位置是 boot 的（$%b）且名字在 primdata 里 → 条目，否则 #f
      (define (boot-primdata e0)
        (nanopass-case (L1 Expr) e0
          [(gref ,s) (let ((name (boot-location-name s))) (and name (primdata-lookup name)))]
          [else #f]))
      ;; (list 结果) 或 #f
      (define (fold-primcall pr args)
        (and fold-on? (for-all const? args) (primcall-fold pr (map const-value args))))
      (define (fold-call p args)
        (and fold-on? (primdata-arity-ok? p (length args))
             (for-all const? args) (primdata-fold p (map const-value args))))
      ;; 变参算术的常量参数先合并（partial-folder，Chez 的 cp02 / cp03 之一）：精确实数常量不少于两个时合并成
      ;; 一个放在最前面——精确算术可交换结合，非常量参数的相对顺序不变；不精确常量不动（舍入与求值顺序有关）
      (define (exact-real-const? e)
        (let ((d (const-value e))) (and (const? e) (number? d) (real? d) (exact? d))))
      (define (partial-fold p args)
        (and fold-on? (primdata-flag? p 'partial-folder)
             (let ((cs (filter exact-real-const? args)))
               (and (>= (length cs) 2)
                    (let ((r (primdata-fold p (map const-value cs))))
                      (and r
                           (with-output-language (L1 Expr)
                             (cons `(quote ,(car r)) (filter (lambda (e) (not (exact-real-const? e))) args)))))))))
      (define (boot-call-name? e0 name)
        (nanopass-case (L1 Expr) e0 [(gref ,s) (eq? (boot-location-name s) name)] [else #f]))
      ;; eqv? 改 eq?：一边是常量、且常量是"相等即同一对象"的类型——符号（按名字驻留）、布尔、空表。
      ;; 数（flonum、bignum，连 fixnum 也只有小的有缓存）与字符都装箱，相等的值可能是不同对象，不改
      (define (eq-safe-const? e)
        (and (const? e) (let ((d (const-value e))) (or (symbol? d) (boolean? d) (null? d)))))
      (define (eqv-test a b)
        (with-output-language (L1 Expr)
          (if (or (eq-safe-const? a) (eq-safe-const? b)) `(primcall eq? ,a ,b) `(primcall eqv? ,a ,b))))
      ;; 测试位置的 (memv x '(d ...))（case 的多数据子句）：x 简单、表不长时展开成逐个比较的 if 链，
      ;; 每项按上面的规则选 eq? / eqv?。只看真假，所以不必返回表尾
      (define memv-chain-limit 8)
      (define (memv-test e)
        (and fold-on?
             (nanopass-case (L1 Expr) e
               [(call ,e0 ,e1 ,e2)
                (and (nanopass-case (L1 Expr) e0 [(gref ,s) (eq? (boot-location-name s) 'memv)] [else #f])
                     (simple? e1) (const? e2)
                     (let ((ds (const-value e2)))
                       (and (list? ds) (pair? ds) (<= (length ds) memv-chain-limit)
                            (with-output-language (L1 Expr)
                              (let loop ((ds ds))
                                (let ((t (eqv-test e1 `(quote ,(car ds)))))
                                  (if (null? (cdr ds)) t `(if ,t (quote #t) ,(loop (cdr ds))))))))))]
               [else #f])))
      ;; effect 上下文可删的调用
      ;; 常量总是删（iron-069 之前就如此）；开关只管按标志删调用
      (define (discardable? e)
        (or (const? e)
            (and discard-on?
             (nanopass-case (L1 Expr) e
               [(primcall ,pr ,e* ...)
                (and (for-all simple? e*)
                     (let ((p (primitive-info pr)))
                       (and p (let ((o (primitive-overload p (length e*))))
                                (and o (overload-flag? o 'discard) (overload-flag? o 'unrestricted))))))]
               [(call ,e0 ,e* ...)
                (and (for-all simple? e*)
                     (let ((p (boot-primdata e0)))
                       (and p (primdata-flag? p 'discard) (primdata-flag? p 'unrestricted)
                            (primdata-arity-ok? p (length e*)))))]
               [else #f])))))
    (Expr : Expr (ir) -> Expr ()
      [(if ,[e0] ,[e1] ,[e2])
       (cond
         ((const? e0) (if (const-value e0) e1 e2))
         ((memv-test e0) => (lambda (t) `(if ,t ,e1 ,e2)))
         (else `(if ,e0 ,e1 ,e2)))]
      [(primcall ,pr ,[e*] ...)
       (let ((r (fold-primcall pr e*)))
         (cond
           (r `(quote ,(car r)))
           ((and fold-on? (eq? pr 'eqv?) (= (length e*) 2)) (eqv-test (car e*) (cadr e*)))
           (else `(primcall ,pr ,e* ...))))]
      [(call ,[e0] ,[e*] ...)
       (let ((p (boot-primdata e0)))
         (cond
           ;; boot 的 list 是 (lambda x x)：调用要先建参数数组再转成表。改成嵌套的 cons（iron-070，同 Chez cp0），
           ;; 参数仍从左到右求值（operands! 先算 car 再算 cdr）
           ((and list-cons? (boot-call-name? e0 'list))
            (fold-right (lambda (e acc) `(primcall cons ,e ,acc)) `(quote ()) e*))
           ((not p) `(call ,e0 ,e* ...))
           ((fold-call p e*) => (lambda (r) `(quote ,(car r))))
           ((partial-fold p e*) => (lambda (args) `(call ,e0 ,args ...)))
           (else `(call ,e0 ,e* ...))))]
      [(begin ,[e*] ... ,[e])
       (let ((e* (filter (lambda (x) (not (discardable? x))) e*)))
         (if (null? e*) e `(begin ,e* ... ,e)))]))

;; —— simplify-if：L1 → L1（iron-073，照 Chez cpnanopass 的 np-simplify-if 裁剪）——
  ;; 内联（iron-070）把 and / or / not 与谓词复制进调用处后，常见嵌套的 if、重复的测试与只差真假的常量分支。
  ;; 这里只做不复制代码、不改变求值的化简：
  ;; 1. 测试规范化：(if (not t) a b) → (if t b a)；测试是 (if t #f #t) 同理；(if t K #f)（K 为真值常量）→ t。
  ;; 2. 已知的测试：外层 (if t …) 的真分支里 t 为真、假分支里为假；(and t1 t2) 为真时两项都真，(or t1 t2) 为假时两项都假。
  ;;    内层的同一测试直接选分支。只记"可比较"的测试——无副作用、结果只取决于不被 set! 的变量与不可变常量：
  ;;    变量本身、类型谓词、eq? / eqv?、数值与字符比较（对同样的参数总得同样的结果；外层求值没有报错，内层也不会）。
  ;; 3. 常量分支：(if t #t #f) 且 t 的结果必是布尔 → t；两支是同一个常量或同一个变量 → (begin t 那一支)，
  ;;    t 可比较（无副作用）时只留那一支。
  ;; 测量开关：CLEAT_NO_073 为 1 或含 if 时关。
  (define no-073 (or ($get-environment-variable "CLEAT_NO_073") ""))
  (define simplify-if-on? (not (or (string=? no-073 "1") (string-contains? no-073 "if"))))

  ;; 结果只取决于参数、不读可变状态的测试（C# 原语名 / boot 过程名）
  (define pure-test-prims '(pair? null? nil? eq? $fixnum? $flonum?))
  (define pure-test-procs
    '(pair? null? vector? string? symbol? boolean? char? procedure? bytevector? number? complex? real? rational?
      integer? exact? inexact? fixnum? flonum? exact-integer? zero? positive? negative? odd? even? nan? finite? infinite?
      eq? eqv? = < > <= >= fx=? fx<? fx>? fx<=? fx>=? fxzero? fxpositive? fxnegative? fxodd? fxeven?
      fl=? fl<? fl>? fl<=? fl>=? flzero? flpositive? flnegative? char=? char<? char>? char<=? char>=?))
  ;; 结果必是布尔的原语
  (define bool-prims '(pair? null? nil? eq? eqv? not $fixnum? $flonum?))

  ;; 收集被 set! 的变量（内联复制出的新变量不在 analyze 的表里，所以自己走一遍）
  (define (assigned-vars ir)
    (let ((t (make-eq-hashtable)))
      (define (walk* es) (for-each walk es))
      (define (walk-le le)
        (nanopass-case (L1 LambdaExpr) le
          [(lambda ,nm ,cl* ...) (for-each walk-cl cl*)]))
      (define (walk-cl cl)
        (nanopass-case (L1 Clause) cl [(clause (,x* ...) ,x ,body) (walk body)]))
      (define (walk e)
        (nanopass-case (L1 Expr) e
          [(set! ,x ,e) (hashtable-set! t x #t) (walk e)]
          [(if ,e0 ,e1 ,e2) (walk e0) (walk e1) (walk e2)]
          [(begin ,e* ... ,e) (walk* e*) (walk e)]
          [(gset! ,s ,e) (walk e)]
          [(mset! ,x ,e) (walk e)]
          [(primcall ,pr ,e* ...) (walk* e*)]
          [(lambda ,nm ,cl* ...) (for-each walk-cl cl*)]
          [(letrec ([,x* ,le*] ...) ,body) (for-each walk-le le*) (walk body)]
          [(let ([,x* ,e*] ...) ,body) (walk* e*) (walk body)]
          [(library ,d ([,x* ,s* ,e*] ...) ,body) (walk* e*) (walk body)]
          [(call ,e ,e* ...) (walk e) (walk* e*)]
          [(and ,e* ...) (walk* e*)]
          [(or ,e* ...) (walk* e*)]
          [(throw ,e) (walk e)]
          [(try-finally ,e0 ,e1) (walk e0) (walk e1)]
          [(try-catch ,e (,d* ,e*) ...) (walk e) (walk* e*)]
          [(label ,x (,x* ...) ,body0 ,body1) (walk body0) (walk body1)]
          [(jump ,x ,e* ...) (walk* e*)]
          [(escape-frame ,x ,body) (walk body)]
          [(try-handle ,x ,body0 ,body1) (walk body0) (walk body1)]
          ;; cleat 的 Clojure 节点（漏掉其中的 set! 会让被赋值的变量当作不变的测试折叠）
          [(clj-var-set! ,d ,e) (walk e)]
          [(clj-def ,d ,st0 ,st1 ,e0 ,e1) (walk e0) (walk e1)]
          [(clj-invoke ,e ,e* ...) (walk e) (walk* e*)]
          [(clj-keyword-invoke ,d ,e ,e* ...) (walk e) (walk* e*)]
          [(clj-call ,d0 ,d1 ,e* ...) (walk* e*)]
          [(clj-static-call ,d0 ,d1 ,e* ...) (walk* e*)]
          [(clj-case ,d ,e0 ,e1 ,e* ...) (walk e0) (walk e1) (walk* e*)]
          [(clj-deftype ,d (,x* ...) ,le* ...) (for-each walk-le le*)]
          [(host-call ,d0 ,d1 ,st ,e* ...) (walk* e*)]
          [(host-field-get ,d0 ,d1 ,st ,e* ...) (walk* e*)]
          [(host-field-set ,d0 ,d1 ,st ,e* ...) (walk* e*)]
          [(host-new ,d ,e* ...) (walk* e*)]
          [(host-new-array ,d ,e) (walk e)]
          [(host-cast ,d ,e) (walk e)]
          [(host-is ,d ,e) (walk e)]
          [(unsupported ,d ,e* ...) (walk* e*)]
          [else (void)]))
      (walk ir)
      t))

  (define (simplify-if ir) (if simplify-if-on? (simplify-if-pass ir) ir))

  (define-pass simplify-if-pass : L1 (ir) -> L1 ()
    (definitions
      (define assigned (assigned-vars ir))
      (define (const? e) (nanopass-case (L1 Expr) e [(quote ,d) #t] [else #f]))
      (define (const-value e) (nanopass-case (L1 Expr) e [(quote ,d) d] [else #f]))
      (define (false-const? e) (and (const? e) (not (const-value e))))
      (define (true-const? e) (and (const? e) (const-value e) #t))
      ;; 相等即同一对象的常量（键里用 equal? 比较也就是 eqv?）
      (define (stable-const? d) (or (symbol? d) (boolean? d) (null? d) (char? d) (and (fixnum? d) (exact? d))))
      (define (boot-name e0)
        (nanopass-case (L1 Expr) e0 [(gref ,s) (boot-location-name s)] [else #f]))
      (define (arg-key e)
        (nanopass-case (L1 Expr) e
          [,x (and (not (hashtable-ref assigned x #f)) (list 'v x))]
          [(quote ,d) (and (stable-const? d) (list 'q d))]
          [else #f]))
      (define (args-key e*)
        (let loop ((es e*) (acc '()))
          (cond ((null? es) (reverse acc))
                ((arg-key (car es)) => (lambda (k) (loop (cdr es) (cons k acc))))
                (else #f))))
      ;; 可比较的测试的键，否则 #f
      (define (test-key e)
        (nanopass-case (L1 Expr) e
          [,x (arg-key e)]
          [(primcall ,pr ,e* ...)
           (and (memq pr pure-test-prims) (let ((k (args-key e*))) (and k (cons pr k))))]
          [(call ,e0 ,e* ...)
           (let ((name (boot-name e0)))
             (and name (memq name pure-test-procs) (let ((k (args-key e*))) (and k (cons name k)))))]
          [else #f]))
      (define (bool-valued? e)
        (nanopass-case (L1 Expr) e
          [(quote ,d) (boolean? d)]
          [(primcall ,pr ,e* ...) (and (memq pr bool-prims) #t)]
          [(call ,e0 ,e* ...)
           (let ((p (let ((name (boot-name e0))) (and name (primdata-lookup name)))))
             (and p (primdata-arity-ok? p (length e*)) (eq? (primdata-result-type p) 'boolean)))]
          [(if ,e0 ,e1 ,e2) (and (bool-valued? e1) (bool-valued? e2))]
          [else #f]))
      ;; 测试规范化：返回 (values 测试 交换两支?)
      (define (norm-test e)
        (let loop ((e e) (swap? #f))
          (nanopass-case (L1 Expr) e
            [(primcall ,pr ,e* ...)
             (if (and (eq? pr 'not) (= (length e*) 1)) (loop (car e*) (not swap?)) (values e swap?))]
            [(if ,e0 ,e1 ,e2)
             (cond ((and (false-const? e1) (true-const? e2)) (loop e0 (not swap?)))
                   ((and (true-const? e1) (false-const? e2)) (loop e0 swap?))
                   (else (values e swap?)))]
            [else (values e swap?)])))
      (define (known-value t known)
        (let ((k (test-key t)))
          (let ((p (and k (assoc k known)))) (if p (cdr p) 'unknown))))
      (define (add t v known)
        (let ((k (test-key t))) (if k (cons (cons k v) known) known)))
      ;; 测试为真 / 为假时成立的事实
      (define (true-facts t known)
        (let ((known (add t #t known)))
          (nanopass-case (L1 Expr) t
            [(if ,e0 ,e1 ,e2) (if (false-const? e2) (true-facts e1 (true-facts e0 known)) known)]      ; (and e0 e1)
            [else known])))
      (define (false-facts t known)
        (let ((known (add t #f known)))
          (nanopass-case (L1 Expr) t
            [(if ,e0 ,e1 ,e2) (if (true-const? e1) (false-facts e2 (false-facts e0 known)) known)]     ; (or e0 e2)
            [else known])))
      (define (same-leaf? a b)
        (or (and (const? a) (const? b) (eq? (const-value a) (const-value b)))
            (nanopass-case (L1 Expr) a
              [,x (nanopass-case (L1 Expr) b [,x1 (eq? x x1)] [else #f])]
              [else #f]))))
    (Expr : Expr (ir known) -> Expr ()
      [(if ,[e0] ,e1 ,e2)
       (let-values (((t swap?) (norm-test e0)))
         (let ((a (if swap? e2 e1)) (b (if swap? e1 e2)))
           (case (known-value t known)
             ((#t) (Expr a known))
             ((#f) (Expr b known))
             (else
              (let ((a (Expr a (true-facts t known))) (b (Expr b (false-facts t known))))
                (cond
                  ((and (true-const? a) (eq? (const-value a) #t) (false-const? b) (bool-valued? t)) t)
                  ((same-leaf? a b) (if (test-key t) a `(begin ,t ,a)))
                  (else `(if ,t ,a ,b))))))))])
    ;; 声明出来，known 才会传进 lambda 体（自动生成的处理器不带额外参数）
    (LambdaExpr : LambdaExpr (ir known) -> LambdaExpr ())
    (Clause : Clause (ir known) -> Clause ())
    (Expr ir '()))

  ;; —— convert-assignments：L1 → L2 ——
  (define-pass convert-assignments : L1 (ir an) -> L2 ()
    (definitions
      (define (boxed? x)
        (and (hashtable-ref (analysis-assigned an) x #f)
             (hashtable-ref (analysis-captured an) x #f)
             (not (hashtable-ref (analysis-fields an) x #f))))
      ;; label 的形参表：jump 给被装箱的形参传新 Box
      (define label-params (make-eq-hashtable)))
    (Expr : Expr (ir) -> Expr ()
      [,x (if (boxed? x) `(unbox ,x) x)]
      [(set! ,x ,[e]) (if (boxed? x) `(set-box! ,x ,e) `(set! ,x ,e))]
      [(let ([,x* ,[e*]] ...) ,[body])
       `(let ([,x* ,(map (lambda (x e) (if (boxed? x) `(box ,e) e)) x* e*)] ...) ,body)]
      [(label ,x (,x* ...) ,body0 ,body1)
       (hashtable-set! label-params x x*)
       (let* ((b0 (Expr body0)) (b1 (Expr body1)))
         `(label ,x (,x* ...) ,b0 ,b1))]
      [(jump ,x ,[e*] ...)
       (let ((p* (hashtable-ref label-params x '())))
         `(jump ,x ,(map (lambda (p e) (if (boxed? p) `(box ,e) e)) p* e*) ...))]
      ;; try-handle 的异常变量被闭包捕获又赋值时，进入处理体先换成 Box（escape-frame 的 k 只被直接调用，不会装箱）
      [(try-handle ,x ,[body0] ,[body1])
       (if (boxed? x)
           `(try-handle ,x ,body0 (begin (set! ,x (box ,x)) ,body1))
           `(try-handle ,x ,body0 ,body1))])
    (Clause : Clause (ir) -> Clause ()
      [(clause (,x* ...) ,x ,[body])
       ;; 被装箱的形参：进入时把形参换成 Box
       (let ((bx (filter boxed? (if x (cons x x*) x*))))
         `(clause (,x* ...) ,x
            ,(if (null? bx)
                 body
                 `(begin ,(map (lambda (v) `(set! ,v (box ,v))) bx) ... ,body))))]))
  )
