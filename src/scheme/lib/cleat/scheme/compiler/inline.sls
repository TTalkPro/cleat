#| License
Copyright (c) 2026 the Skeg contributors
All rights reserved.
This source code is subject to terms and conditions of the BSD License.
See docs/license.txt. |#

;;; 过程内联与 let 传播（skeg iron-070，cleat-078 移植；照 Chez cp0 的思路裁剪）。
;;;
;;;   (cp0-inline L1 analysis) → L1     调用点内联
;;;   (simplify-lets L1 analysis) → L1  常量 / 变量传播、删除不再引用的绑定
;;;
;;; 候选过程：let / letrec 绑定的 lambda（变量未被 set!），库绑定（library 形式）的 lambda（变量未被 mset!）。
;;; 调用 (call f e …) 或 (call (mref f) e …) 满足下列条件时换成 (let ([x′ e] …) body′)，body′ 是 f 的子句体的副本，
;;; 其中所有绑定改成新名字（L1 的变量名在单元内唯一，后面的 pass 依赖这一点）：
;;;   - 参数个数与 f 的某个无剩余参数的子句相符；
;;;   - 子句体（已经内联过的版本）不超过 score-limit 个节点（CLEAT_INLINE_LIMIT，默认 24），不含 set! / library /
;;;     Clojure 的 fn* / deftype* / def；
;;;   - f 不是递归的：优化后的体里不再引用 f（相当于 Chez 的 outer-unroll-limit 0）；
;;;   - 调用点在某个 lambda 体里（顶层初始化代码只执行一次，不内联；这也保证库变量在调用时已初始化），
;;;     不在 Clojure 的 fn* 里，不在超过 2000 个节点的过程体里（与 codegen 的大过程规则相同）。
;;; 被调过程的体先递归地做内联（结果按变量缓存，整趟复用；正在处理的过程不再展开，防止无限展开），
;;; 所以小过程会层层内联，每层都受 score-limit 约束。
;;;
;;; 内联只改变调用方式，不改变求值顺序以外的语义：参数个数相符时原调用不会报错；let 的初始化与 call 的参数
;;; 同样从左到右求值（codegen）。内联后错误信息里的过程名、调用栈会少一层。
;;;
;;; 测量开关：CLEAT_NO_070=1 关掉内联与 let 传播；CLEAT_INLINE_LIMIT=n 调节点预算。
;;;
;;; cleat 与 skeg 的差别（cleat-078）：元层没有 async（没有 async-lambda / await，escape-frame / try-handle 是 cleat-076 的非 async 节点，
;;; try-catch 是 $try/catch 的多子句形式）；L1 多出 Clojure 节点（clj-…）。内联只用于 Scheme 代码：Clojure 的 fn*（名字是
;;; clj-fn-name 记录的 lambda）不作候选，也不在它的体里内联，含 Clojure fn*、deftype* / reify*、def 的体不复制——复制会重复
;;; 生成的类名，原始签名（^long 等）的 fn 内联后还会少掉参数的转换。各遍历都走进 Clojure 节点的子表达式（引用计数漏掉
;;; clj-invoke 等里的引用会让 simplify-lets 删掉还在用的绑定）。
(library (cleat scheme compiler inline)
  (export cp0-inline simplify-lets inline-enabled?)
  (import
    (rnrs)
    (only (cleat scheme) void)
    (only (cleat scheme unsafe) $get-environment-variable)
    (nanopass)
    (cleat scheme compiler languages)
    (only (cleat scheme compiler front) analysis-assigned analysis-massigned)
    (only (cleat scheme compiler loops) fresh-name))

  (define inline-enabled?
    (let ((v ($get-environment-variable "CLEAT_NO_070")))
      (not (and (string? v) (string=? v "1")))))

  (define score-limit
    (let ((v ($get-environment-variable "CLEAT_INLINE_LIMIT")))
      (or (and (string? v) (string->number v)) 24)))

  (define big-body-nodes 2000)

  ;; g$名字$… → 名字；其他原样
  (define (base-name x)
    (let* ((s (symbol->string x)) (n (string-length s)))
      (if (and (> n 2) (char=? (string-ref s 0) #\g) (char=? (string-ref s 1) #\$))
          (let loop ((i 2))
            (cond ((= i n) s)
                  ((char=? (string-ref s i) #\$) (substring s 2 i))
                  (else (loop (+ i 1)))))
          (let loop ((i 0))                         ; 已经是 名字%N 的，去掉编号
            (cond ((= i n) s)
                  ((char=? (string-ref s i) #\%) (substring s 0 i))
                  (else (loop (+ i 1))))))))

  ;; —— 节点数（不超过 limit 时给出个数，超过给 #f）与可复制性 ——
  ;; 含 set! / library、Clojure 的 fn* / deftype* / def 的体不复制：给 #f
  (define-pass measure : L1 (ir limit) -> * (v)
    (definitions
      (define count 0)
      (define bad #f)
      (define (more!) (set! count (+ count 1)) (when (> count limit) (set! bad #t)) (not bad))
      (define (Clause* cl*) (for-each Clause cl*))
      (define (Clause cl)
        (nanopass-case (L1 Clause) cl
          [(clause (,x* ...) ,x ,body) (Expr body)])))
    (Expr : Expr (ir) -> * (v)
      [(lambda ,nm ,cl* ...) (if (clj-fn-name? nm) (set! bad #t) (when (more!) (Clause* cl*)))]
      [(set! ,x ,e) (set! bad #t)]
      [(library ,d ([,x* ,s* ,e*] ...) ,body) (set! bad #t)]
      [(clj-deftype ,d (,x* ...) ,le* ...) (set! bad #t)]
      [(clj-def ,d ,st0 ,st1 ,e0 ,e1) (set! bad #t)]
      [(if ,e0 ,e1 ,e2) (when (more!) (Expr e0) (Expr e1) (Expr e2))]
      [(begin ,e* ... ,e) (when (more!) (for-each Expr e*) (Expr e))]
      [(gset! ,s ,e) (when (more!) (Expr e))]
      [(mset! ,x ,e) (when (more!) (Expr e))]
      [(primcall ,pr ,e* ...) (when (more!) (for-each Expr e*))]
      [(letrec ([,x* ,le*] ...) ,body) (when (more!) (for-each Expr le*) (Expr body))]
      [(let ([,x* ,e*] ...) ,body) (when (more!) (for-each Expr e*) (Expr body))]
      [(call ,e ,e* ...) (when (more!) (Expr e) (for-each Expr e*))]
      [(and ,e* ...) (when (more!) (for-each Expr e*))]
      [(or ,e* ...) (when (more!) (for-each Expr e*))]
      [(throw ,e) (when (more!) (Expr e))]
      [(try-finally ,e0 ,e1) (when (more!) (Expr e0) (Expr e1))]
      [(label ,x (,x* ...) ,body0 ,body1) (when (more!) (Expr body0) (Expr body1))]
      [(jump ,x ,e* ...) (when (more!) (for-each Expr e*))]
      [(escape-frame ,x ,body) (when (more!) (Expr body))]
      [(try-handle ,x ,body0 ,body1) (when (more!) (Expr body0) (Expr body1))]
      [(try-catch ,e (,d* ,e*) ...) (when (more!) (Expr e) (for-each Expr e*))]
      [(clj-var-set! ,d ,e) (when (more!) (Expr e))]
      [(clj-invoke ,e ,e* ...) (when (more!) (Expr e) (for-each Expr e*))]
      [(clj-keyword-invoke ,d ,e ,e* ...) (when (more!) (Expr e) (for-each Expr e*))]
      [(clj-call ,d0 ,d1 ,e* ...) (when (more!) (for-each Expr e*))]
      [(clj-static-call ,d0 ,d1 ,e* ...) (when (more!) (for-each Expr e*))]
      [(clj-case ,d ,e0 ,e1 ,e* ...) (when (more!) (Expr e0) (Expr e1) (for-each Expr e*))]
      [(host-call ,d0 ,d1 ,st ,e* ...) (when (more!) (for-each Expr e*))]
      [(host-field-get ,d0 ,d1 ,st ,e* ...) (when (more!) (for-each Expr e*))]
      [(host-field-set ,d0 ,d1 ,st ,e* ...) (when (more!) (for-each Expr e*))]
      [(host-new ,d ,e* ...) (when (more!) (for-each Expr e*))]
      [(host-new-array ,d ,e) (when (more!) (Expr e))]
      [(host-cast ,d ,e) (when (more!) (Expr e))]
      [(host-is ,d ,e) (when (more!) (Expr e))]
      [(unsupported ,d ,e* ...) (when (more!) (for-each Expr e*))]
      [else (more!)])
    (Expr ir)
    (and (not bad) count))

  ;; —— 变量引用计数：词法变量与库变量（mref）各自计数，写进 counts ——
  (define-pass count-refs! : L1 (ir counts) -> * (v)
    (definitions
      (define (bump! x) (hashtable-set! counts x (+ 1 (hashtable-ref counts x 0))))
      (define (Clause cl)
        (nanopass-case (L1 Clause) cl
          [(clause (,x* ...) ,x ,body) (Expr body)])))
    (Expr : Expr (ir) -> * (v)
      [,x (bump! x)]
      [(mref ,x) (bump! x)]
      [(lambda ,nm ,cl* ...) (for-each Clause cl*)]
      [(set! ,x ,e) (Expr e)]
      [(mset! ,x ,e) (Expr e)]
      [(if ,e0 ,e1 ,e2) (Expr e0) (Expr e1) (Expr e2)]
      [(begin ,e* ... ,e) (for-each Expr e*) (Expr e)]
      [(gset! ,s ,e) (Expr e)]
      [(primcall ,pr ,e* ...) (for-each Expr e*)]
      [(letrec ([,x* ,le*] ...) ,body) (for-each Expr le*) (Expr body)]
      [(let ([,x* ,e*] ...) ,body) (for-each Expr e*) (Expr body)]
      [(library ,d ([,x* ,s* ,e*] ...) ,body) (for-each Expr e*) (Expr body)]
      [(call ,e ,e* ...) (Expr e) (for-each Expr e*)]
      [(and ,e* ...) (for-each Expr e*)]
      [(or ,e* ...) (for-each Expr e*)]
      [(throw ,e) (Expr e)]
      [(try-finally ,e0 ,e1) (Expr e0) (Expr e1)]
      [(label ,x (,x* ...) ,body0 ,body1) (Expr body0) (Expr body1)]
      [(jump ,x ,e* ...) (for-each Expr e*)]
      [(escape-frame ,x ,body) (Expr body)]
      [(try-handle ,x ,body0 ,body1) (Expr body0) (Expr body1)]
      [(try-catch ,e (,d* ,e*) ...) (Expr e) (for-each Expr e*)]
      [(clj-var-set! ,d ,e) (Expr e)]
      [(clj-def ,d ,st0 ,st1 ,e0 ,e1) (Expr e0) (Expr e1)]
      [(clj-invoke ,e ,e* ...) (Expr e) (for-each Expr e*)]
      [(clj-keyword-invoke ,d ,e ,e* ...) (Expr e) (for-each Expr e*)]
      [(clj-call ,d0 ,d1 ,e* ...) (for-each Expr e*)]
      [(clj-static-call ,d0 ,d1 ,e* ...) (for-each Expr e*)]
      [(clj-case ,d ,e0 ,e1 ,e* ...) (Expr e0) (Expr e1) (for-each Expr e*)]
      [(clj-deftype ,d (,x* ...) ,le* ...) (for-each Expr le*)]
      [(host-call ,d0 ,d1 ,st ,e* ...) (for-each Expr e*)]
      [(host-field-get ,d0 ,d1 ,st ,e* ...) (for-each Expr e*)]
      [(host-field-set ,d0 ,d1 ,st ,e* ...) (for-each Expr e*)]
      [(host-new ,d ,e* ...) (for-each Expr e*)]
      [(host-new-array ,d ,e) (Expr e)]
      [(host-cast ,d ,e) (Expr e)]
      [(host-is ,d ,e) (Expr e)]
      [(unsupported ,d ,e* ...) (for-each Expr e*)]
      [else (void)])
    (Expr ir))

  ;; —— 复制并改名：体中所有绑定换成新名字；env 预先放好形参的新名字 ——
  (define-pass rename-copy : L1 (ir env) -> L1 ()
    (definitions
      (define (bind! x) (let ((y (fresh-name (base-name x)))) (hashtable-set! env x y) y))
      (define (ren x) (hashtable-ref env x x)))
    (Expr : Expr (ir) -> Expr ()
      [,x (ren x)]
      [(set! ,x ,[e]) `(set! ,(ren x) ,e)]
      [(let ([,x* ,[e*]] ...) ,body)
       (let ((y* (map bind! x*)))
         `(let ([,y* ,e*] ...) ,(Expr body)))]
      [(letrec ([,x* ,le*] ...) ,body)
       (let ((y* (map bind! x*)))
         `(letrec ([,y* ,(map LambdaExpr le*)] ...) ,(Expr body)))]
      [(label ,x (,x* ...) ,body0 ,body1)
       (let* ((y (bind! x)) (y* (map bind! x*)))
         `(label ,y (,y* ...) ,(Expr body0) ,(Expr body1)))]
      [(jump ,x ,[e*] ...) `(jump ,(ren x) ,e* ...)]
      [(escape-frame ,x ,body)
       (let ((y (bind! x))) `(escape-frame ,y ,(Expr body)))]
      [(try-handle ,x ,[body0] ,body1)
       (let ((y (bind! x))) `(try-handle ,y ,body0 ,(Expr body1)))])
    (LambdaExpr : LambdaExpr (ir) -> LambdaExpr ())
    (Clause : Clause (ir) -> Clause ()
      [(clause (,x* ...) ,x ,body)
       (let* ((y* (map bind! x*)) (y (and x (bind! x))))
         `(clause (,y* ...) ,y ,(Expr body)))]))

  ;; —— 调用点内联 ——
  (define-record-type cand
    (fields lambda (mutable done) (mutable busy) (mutable recursive) (mutable sizes) mref? (mutable defined)))

  (define-pass cp0-inline : L1 (ir an) -> L1 ()
    (definitions
      (define assigned (analysis-assigned an))
      (define massigned (analysis-massigned an))
      (define cands (make-eq-hashtable))         ; 变量 → cand
      ;; definitions 里看不到处理器：主体开始时填进来
      (define Expr* #f)
      (define LambdaExpr* #f)
      (define in-lambda? #f)
      (define in-async? #f)
      (define big? #f)
      ;; Clojure 的 fn* 不作候选（见文件头）
      (define (lambda-le? le)
        (nanopass-case (L1 Expr) le [(lambda ,nm ,cl* ...) (not (clj-fn-name? nm))] [else #f]))
      (define (register! x le mref?)
        (when (and (lambda-le? le)
                   (not (hashtable-ref (if mref? massigned assigned) x #f)))
          (hashtable-set! cands x (make-cand le #f #f #f '() mref? (not mref?)))))
      ;; 进入一个 lambda：设置上下文，处理完恢复
      (define (with-lambda le async? thunk)
        (let ((l in-lambda?) (a in-async?) (b big?))
          (set! in-lambda? #t)
          (set! in-async? (or a async?))
          (set! big? (not (measure le big-body-nodes)))
          (let ((r (thunk)))
            (set! in-lambda? l) (set! in-async? a) (set! big? b)
            r)))
      ;; 候选的优化后 lambda（处理一次，缓存）；正在处理时 #f
      (define (template x c)
        (cond
          ((cand-done c) => (lambda (le) le))
          ((cand-busy c) #f)
          (else
           (cand-busy-set! c #t)
           ;; 过程体在自己的上下文里优化（不继承调用点的 async / 大过程标志）
           (let ((le (let ((l in-lambda?) (a in-async?) (b big?))
                       (set! in-async? #f) (set! big? #f)
                       (let ((r (LambdaExpr* (cand-lambda c))))
                         (set! in-lambda? l) (set! in-async? a) (set! big? b)
                         r))))
             (cand-busy-set! c #f)
             (cand-done-set! c le)
             ;; 优化后仍引用自己的是递归过程：不内联（outer-unroll-limit 0）
             (let ((counts (make-eq-hashtable)))
               (count-refs! le counts)
               (cand-recursive-set! c (> (hashtable-ref counts x 0) 0)))
             le))))
      ;; 子句：参数个数相符、无剩余参数的那个 → (形参表 . 体)，否则 #f
      (define (matching-clause le argc)
        (nanopass-case (L1 Expr) le
          [(lambda ,nm ,cl* ...)
           (let loop ((cl* cl*))
             (and (pair? cl*)
                  (nanopass-case (L1 Clause) (car cl*)
                    [(clause (,x* ...) ,x ,body)
                     (if (and (not x) (= (length x*) argc))
                         (cons x* body)
                         (loop (cdr cl*)))])))]
          [else #f]))
      ;; 子句体的大小（按参数个数缓存）
      (define (body-size c argc body)
        (let ((hit (assv argc (cand-sizes c))))
          (if hit
              (cdr hit)
              (let ((n (measure body score-limit)))
                (cand-sizes-set! c (cons (cons argc n) (cand-sizes c)))
                n))))
      (define (try-inline e0 e*)
        ;; 只在过程体里内联：顶层初始化代码只执行一次，内联只增加代码（与 codegen 的 inline-ops 同一原则）
        (and in-lambda? (not in-async?) (not big?)
             (let-values (((x mref?) (nanopass-case (L1 Expr) e0
                                       [,x (values x #f)]
                                       [(mref ,x) (values x #t)]
                                       [else (values #f #f)])))
               (let ((c (and x (hashtable-ref cands x #f))))
                 (and c (eq? mref? (cand-mref? c))
                      (or in-lambda? (cand-defined c))
                      (let ((le (template x c)))
                        (and le (not (cand-recursive c))
                             (let ((m (matching-clause le (length e*))))
                               (and m (body-size c (length e*) (cdr m))
                                    (let* ((env (make-eq-hashtable))
                                           (y* (map (lambda (p)
                                                      (let ((y (fresh-name (base-name p))))
                                                        (hashtable-set! env p y)
                                                        y))
                                                    (car m)))
                                           (body (rename-copy (cdr m) env)))
                                      (if (null? y*)
                                          body
                                          (with-output-language (L1 Expr)
                                            `(let ([,y* ,e*] ...) ,body)))))))))))))
      ;; 绑定右边：候选用缓存的优化结果
      (define (rhs x e)
        (let ((c (hashtable-ref cands x #f)))
          (if c (template x c) (Expr* e)))))
    (Expr : Expr (ir) -> Expr ()
      [(call ,e0 ,[e*] ...)
       (or (try-inline e0 e*)
           `(call ,(Expr e0) ,e* ...))]
      [(let ([,x* ,e*] ...) ,body)
       (for-each (lambda (x e) (register! x e #f)) x* e*)
       (let ((e* (map rhs x* e*)))
         `(let ([,x* ,e*] ...) ,(Expr body)))]
      [(letrec ([,x* ,le*] ...) ,body)
       (for-each (lambda (x le) (register! x le #f)) x* le*)
       (let ((le* (map rhs x* le*)))
         `(letrec ([,x* ,le*] ...) ,(Expr body)))]
      [(library ,d ([,x* ,s* ,e*] ...) ,body)
       (for-each (lambda (x e) (register! x e #t)) x* e*)
       (let ((e* (map (lambda (x e)
                        (let ((r (rhs x e)))
                          (let ((c (hashtable-ref cands x #f))) (when c (cand-defined-set! c #t)))
                          r))
                      x* e*)))
         `(library ,d ([,x* ,s* ,e*] ...) ,(Expr body)))])
    ;; Clojure 的 fn* 的 measure 为 #f（不复制），with-lambda 随之把它当大过程：体里不内联
    (LambdaExpr : LambdaExpr (ir) -> LambdaExpr ()
      [(lambda ,nm ,cl* ...)
       (with-lambda ir #f (lambda () `(lambda ,nm ,(map Clause cl*) ...)))])
    (Clause : Clause (ir) -> Clause ())
    (set! Expr* Expr)
    (set! LambdaExpr* LambdaExpr)
    (Expr ir))

  ;; —— let 传播与死绑定 ——
  ;; (let ([x e] …) body)：x 未被 set! 且 e 是简单常量（数、布尔、字符、符号、空表）或未被 set! 的词法变量时，
  ;; 把 x 换成 e、去掉这个绑定；x 不再被引用且 e 是常量、变量或 lambda 时去掉绑定。
  ;; (letrec ([x le] …) body)：x 不再被引用（含被兄弟引用）时去掉绑定（lambda 没有副作用，省一次闭包分配）。
  (define (simple-datum? d)
    (or (fixnum? d) (flonum? d) (boolean? d) (char? d) (symbol? d) (null? d)))

  (define-pass simplify-lets : L1 (ir an) -> L1 ()
    (definitions
      (define assigned (analysis-assigned an))
      (define counts (make-eq-hashtable))
      (define subst (make-eq-hashtable))
      (define (refs x) (hashtable-ref counts x 0))
      (define (propagatable? x e)
        (and (not (hashtable-ref assigned x #f))
             (nanopass-case (L1 Expr) e
               [(quote ,d) (simple-datum? d)]
               [,x (not (hashtable-ref assigned x #f))]
               [else #f])))
      (define (droppable-init? e)
        (nanopass-case (L1 Expr) e
          [(quote ,d) #t]
          [,x #t]
          [(lambda ,nm ,cl* ...) #t]
          [else #f])))
    (Expr : Expr (ir) -> Expr ()
      [,x (let ((r (hashtable-ref subst x #f))) (or r x))]
      [(let ([,x* ,[e*]] ...) ,body)
       (let loop ((xs x*) (es e*) (kx '()) (ke '()))
         (if (null? xs)
             (let ((body (Expr body)))
               (if (null? kx) body `(let ([,(reverse kx) ,(reverse ke)] ...) ,body)))
             (let ((x (car xs)) (e (car es)))
               (cond
                 ((propagatable? x e)
                  (hashtable-set! subst x e)
                  (loop (cdr xs) (cdr es) kx ke))
                 ((and (= (refs x) 0) (not (hashtable-ref assigned x #f)) (droppable-init? e))
                  (loop (cdr xs) (cdr es) kx ke))
                 (else (loop (cdr xs) (cdr es) (cons x kx) (cons e ke)))))))]
      [(letrec ([,x* ,le*] ...) ,body)
       (let loop ((xs x*) (ls le*) (kx '()) (kl '()))
         (if (null? xs)
             (let ((body (Expr body)))
               (if (null? kx) body `(letrec ([,(reverse kx) ,(map LambdaExpr (reverse kl))] ...) ,body)))
             (if (= (refs (car xs)) 0)
                 (loop (cdr xs) (cdr ls) kx kl)
                 (loop (cdr xs) (cdr ls) (cons (car xs) kx) (cons (car ls) kl)))))])
    (LambdaExpr : LambdaExpr (ir) -> LambdaExpr ())
    (Clause : Clause (ir) -> Clause ())
    (count-refs! ir counts)
    (Expr ir)))
