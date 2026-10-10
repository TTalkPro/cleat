#| License
Copyright (c) 2026 the IronScheme contributors
All rights reserved.
This source code is subject to terms and conditions of the BSD License.
See docs/license.txt. |#

;;; 代码生成：L3 → Lcil（designs/03-compiler.md §4 第 7 ~ 15 步，§5 ~ §7；iron-024 ~ iron-026）。
;;;
;;; 第一版把设计中的 convert-closures、lift-to-classes、lower-loops、lower-calls、lower-interop、
;;; normalize-context、lift-try、linearize 合在一遍按上下文生成栈式代码的过程里完成：
;;;
;;; - 每个 lambda 一个 Procedure 子类（有剩余参数子句时为 VariadicProcedure）。子句体是方法 C0、C1 …；
;;;   Invoke0..4 / InvokeN 按 case-lambda 的子句顺序转发（02 §4）。
;;;   没有自由变量的 lambda：子句方法是静态方法，实例是单例（静态字段 Instance）。
;;;   有自由变量的 lambda：自由变量是实例字段 c0、c1 …；letrec 绑定的 lambda 引用自己时用 this。
;;; - 库变量（library-letrec*）是模块类型的静态字段。库变量绑定的、没有自由变量且不被再赋值的 lambda
;;;   是"已知过程"：模块入口先创建全部已知过程并写入字段与位置（与旧系统的静态链接一致，允许跨库前向调用），
;;;   参数个数相符的调用直接 call 静态子句方法。
;;; - 自尾调用（自己是 letrec 变量或已知过程）在尾位置且参数个数与当前子句相同时，重新赋参数并跳回方法开头。
;;; - 其余尾位置调用加 tail. 前缀；try 区域内不加（03 §5）。
;;; - 上下文：value（结果留在栈上）、effect（不留）、test（留 CLR bool）、tail（返回）。
;;;   谓词原语在 test 上下文直接生成比较。
;;; - try 要求进入时求值栈为空：多操作数的形式中，若排在后面的操作数含 try，先把各操作数存入临时变量（lift-try）。
;;; - 常量：fixnum、布尔、'()、字符、字符串直接生成；其余进常量表（iron-020 的编码）。
(library (ironscheme compiler codegen)
  (export generate-unit canonical-gensyms current-target unit-interface loop-forms)
  (import
    (rnrs)
    (only (ironscheme) void unspecified? make-parameter gensym symbol-bound? symbol-value)
    (nanopass)
    (ironscheme compiler languages)
    (ironscheme compiler primitives)
    (ironscheme compiler constants)
    (only (ironscheme unsafe) $immutable-string? $get-environment-variable)
    (ironscheme compiler targets))

  ;; 规范 gensym 名（iron-030，designs/05-bootstrap.md §7 的后备方案）：设为 #t 时，常量表中本进程生成的 gensym
  ;; （名字以本进程的 TICKS 后缀结尾）按首次出现的顺序改名为 g$名字$序号$%b。构建过程本身用非确定性 gensym，
  ;; 名字带进程时间戳，与运行中系统（上一级镜像）里的名字绝不冲突；产物中的名字只取决于输入。
  (define canonical-gensyms (make-parameter #f))

  (define (process-gensym-suffix)
    (let* ((s (symbol->string (gensym)))
           (n (string-length s)))
      (let loop ((i (- n 1)))
        (if (char=? (string-ref s i) #\$) (substring s i n) (loop (- i 1))))))

  ;; 对编码后的常量（中立编码 S 表达式）改名
  (define (canonicalize-encodings encs)
    (let* ((suffix (process-gensym-suffix))
           (k (string-length suffix))
           (renamed (make-hashtable string-hash string=?))
           (next 0))
      (define (rename s)
        (let ((n (string-length s)))
          (if (and (> n (+ k 2)) (char=? (string-ref s 0) #\g) (char=? (string-ref s 1) #\$)
                   (string=? (substring s (- n k) n) suffix))
              (or (hashtable-ref renamed s #f)
                  (let ((new (string-append (substring s 0 (gensym-name-end s)) "$" (number->string next) "$%b")))
                    (set! next (+ next 1))
                    (hashtable-set! renamed s new)
                    new))
              s)))
      (define (walk x)
        (cond
          ((and (pair? x) (memq (car x) '(sym gensym)) (pair? (cdr x)) (string? (cadr x)))
           (list (car x) (rename (cadr x))))
          ((pair? x) (map walk x))
          (else x)))
      (set! last-renamed renamed)
      (map walk encs)))

  ;; "g$名字$序号$后缀" 中 "g$名字" 的结束位置
  (define (gensym-name-end s)
    (let loop ((i 2))
      (cond
        ((= i (string-length s)) i)
        ((char=? (string-ref s i) #\$) i)
        (else (loop (+ i 1))))))

  ;; 目标机器（designs/11-portability.md §2 D6，iron-057）：运行时类型名与 tail. 等都查描述表（targets.sls），
  ;; 由驱动 parameterize；缺省 clr。下面的名字是标识符宏，用法与原来的字符串常量相同
  (define current-target (make-parameter (target-of 'clr)))
  (define-syntax define-target-type
    (syntax-rules ()
      [(_ name key) (define-syntax name (identifier-syntax (target-type (current-target) 'key)))]))
  (define-target-type obj object)
  (define-target-type obj-array object-array)
  (define-target-type int32-type int32)
  (define-target-type double-type double)
  (define-target-type bool-type boolean)
  (define-target-type char-type char)
  (define-target-type string-type string)
  (define-target-type boot-interface-type boot-interface)
  (define-target-type void-type void)
  (define-target-type exception-type exception)
  (define-target-type unspecified-type unspecified)
  (define-target-type proc-type procedure)
  (define-target-type variadic-type variadic-procedure)
  (define-target-type cons-type cons)
  (define-target-type box-type box)
  (define-target-type symbol-type symbol)
  (define-target-type rt-type rt)
  (define-target-type calls-type calls)
  (define-target-type arith-type inline-arith)
  (define-target-type scheme-exn-type scheme-exception)
  ;; Clojure 运行时（cleat-051）
  (define-target-type ifn-type ifn)
  (define-target-type clj-var-type clj-var)
  (define-target-type ilookup-type ilookup)
  (define-target-type clj-util-type clj-util)
  (define-target-type clj-rt-type clj-rt)
  (define-target-type afunction-type afunction)
  (define-target-type restfn-type restfn)
  (define-target-type pmap-type persistent-map)
  (define-target-type iobj-type iobj)
  (define-target-type clojure-ops-type clojure-ops)
  (define-target-type type-type type)
  (define-target-type type-handle-type type-handle)
  ;; host-* 节点对应的发射器指令名（CLR 目标就是 Lcil 的 clr-*）
  (define (interop key) (target-interop (current-target) key))

  (define (objs n) (if (= n 0) '() (cons obj (objs (- n 1)))))

  ;; ———————————————— 编译单元 ————————————————

  (define-record-type unit
    (fields name                 ; 程序集名
            module               ; 模块类型名
            (mutable classes)    ; 已生成的类型（逆序）
            (mutable consts)     ; 常量（逆序）
            (mutable nconsts)
            const-index          ; eq 哈希表（按 Equals）：常量 → (下标 . 常量)，见 constant-index!
            (mutable namespaces)
            (mutable references)
            (mutable mfields)    ; 模块静态字段名（逆序）
            mfield-of            ; eq 哈希表：库变量 → 字段名
            known                ; eq 哈希表：库变量 → known 记录（已知过程）
            (mutable prelink)    ; ((类名 字段名 位置符号或 #f) …)
            (mutable counter)
            massigned
            known-loc            ; eq 哈希表：位置符号 → known 记录（本单元导出的已知过程，iron-034e）
            (mutable boot-ref)    ; 见下行
            (mutable shared-count) ; 034d：共享持有类中已分配的过程个数
            shared                ; 034d：eqv 哈希表 持有类下标 → #(字段 方法 初始化代码)（各逆序）
            type-names))          ; cleat-051：按名字生成的类型（Clojure 的函数类、deftype）已用的名字（字符串哈希表）
  ;; 跨单元引用 boot 的已知过程：off（不可用）| #f（可用、还没用）| #t（用过，入口要核对镜像）

  ;; 已知过程：class 是过程的名字（共享过程的方法前缀 / 单独的类名），子句方法在 owner 类里，名为 prefix + "C<i>"
  (define-record-type known
    (fields class clauses owner prefix))      ; clauses：((固定参数个数 . 剩余?) …)

  ;; 过程对象所在的静态字段：单独的类是 Instance；共享持有类（034d）是前缀去掉末尾的 $（P<id>）
  (define (known-field k)
    (let ((p (known-prefix k)))
      (if (string=? p "") "Instance" (substring p 0 (- (string-length p) 1)))))

  ;; 034d（D3）：无自由变量的同步过程不各生成一个类，子句方法放进共享持有类（每 200 个一个），
  ;; 过程对象是单元的 StaticProcedure 子类的实例（委托），由持有类的静态构造函数创建。
  ;; IRONSCHEME_NO_034 含 shared 时关（opt-shared? 定义在 opt-off? 之后）
  (define shared-per-holder 200)

  ;; 分配一个共享位置：(持有类名 . 前缀 "P<id>$")
  (define (shared-slot! u)
    (let ((id (unit-shared-count u)))
      (unit-shared-count-set! u (+ id 1))
      (cons (string-append (unit-module u) "$P" (number->string (div id shared-per-holder)))
            (string-append "P" (number->string id) "$"))))

  (define (holder-entry u holder)
    (let ((k (string->number (substring holder (+ (string-length (unit-module u)) 2) (string-length holder)))))
      (or (hashtable-ref (unit-shared u) k #f)
          (let ((v (vector '() '() '())))
            (hashtable-set! (unit-shared u) k v)
            v))))

  ;; 每个单元一个 StaticProcedure 的子类：InvokeK / InvokeN 以 tail. callvirt 调委托（C# 写不出 tail. 前缀，
  ;; 不能依赖 JIT 的优化级别），保证经过程对象的尾调用不占栈
  (define (sp-class u) (string-append (unit-module u) "$SP"))
  (define (sp-ctor-params)
    (list "System.String" "System.Object" "System.Object"
          (func-type 0) (func-type 1) (func-type 2) (func-type 3) (func-type 4) func-n-type))

  (define (sp-class-def u)
    (define (lbl s) (string->symbol s))
    (define (invoke-k k)
      `(method ,(string-append "Invoke" (number->string k)) (attrs override) (params ,@(objs k)) (ret ,obj)
         (body (ldarg 0) (ldfld ,static-proc-type ,(string-append "_f" (number->string k))) (dup) (brfalse ,(lbl "NOFIXED"))
               ,@(let a ((j 1)) (if (> j k) '() (cons `(ldarg ,j) (a (+ j 1)))))
               (tail) (callvirt ,(func-type k) "Invoke" ,(objs k) ,obj) (ret)
               (label ,(lbl "NOFIXED")) (pop)
               (ldarg 0) (ldfld ,static-proc-type "_fn") (dup) (brfalse ,(lbl "NOENTRY"))
               (ldc.i4 ,k) (newarr ,obj)
               ,@(let a ((j 0)) (if (= j k) '() (append `((dup) (ldc.i4 ,j) (ldarg ,(+ j 1)) (stelem.ref)) (a (+ j 1)))))
               (tail) (callvirt ,func-n-type "Invoke" (,obj-array) ,obj) (ret)
               (label ,(lbl "NOENTRY")) (pop)
               (ldarg 0) (ldc.i4 ,k) (call ,static-proc-type "ArityFail" (,int32-type) ,obj) (ret))))
    `(type ,(sp-class u) (base ,static-proc-type) (attrs sealed)
       (method ".ctor" (attrs ctor) (params ,@(sp-ctor-params))
         (body ,@(let a ((j 0)) (if (> j 9) '() (cons `(ldarg ,j) (a (+ j 1)))))
               (call ,static-proc-type ".ctor" ,(sp-ctor-params) ,void-type) (ret)))
       ,@(map invoke-k '(0 1 2 3 4))
       (method "InvokeN" (attrs override) (params ,obj-array) (ret ,obj)
         (body (ldarg 0) (ldfld ,static-proc-type "_fn") (dup) (brfalse ,(lbl "NOENTRY"))
               (ldarg 1) (tail) (callvirt ,func-n-type "Invoke" (,obj-array) ,obj) (ret)
               (label ,(lbl "NOENTRY")) (pop)
               (ldarg 0) (ldarg 1) (call ,static-proc-type "DispatchN" (,obj-array) ,obj) (ret)))))

  ;; 单元结束时生成持有类：静态字段、子句方法与静态构造函数（依次创建各过程对象）
  (define (emit-holders! u)
    (let-values (((ks vs) (hashtable-entries (unit-shared u))))
      (when (> (vector-length ks) 0) (add-class! u (sp-class-def u)))
      (for-each
        (lambda (k)
          (let ((v (hashtable-ref (unit-shared u) k #f))
                (holder (string-append (unit-module u) "$P" (number->string k))))
            (add-class! u
              `(type ,holder (attrs abstract sealed)
                 ,@(reverse (vector-ref v 0))
                 ,@(reverse (vector-ref v 1))
                 (method ".cctor" (attrs cctor) (params)
                   (body ,@(apply append (reverse (vector-ref v 2))) (ret)))))))
        (list-sort < (vector->list ks)))))

  ;; 过程对象的描述（lambda-class! 的结果）：(#t 类名)（无自由变量：静态单例）| (#f 类名)（闭包）
  ;; 静态过程的单例压栈
  (define (load-static-proc! mc c)
    (emit! mc `(ldsfld ,(cadr c) ,(caddr c))))

  (define (next-id! u)
    (let ((n (unit-counter u)))
      (unit-counter-set! u (+ n 1))
      n))

  (define (add-class! u c) (unit-classes-set! u (cons c (unit-classes u))))

  ;; eq 哈希表是 .NET Hashtable，按 Equals 比较：0.0 与 -0.0（及含它们的复数）相等，含 -0.0 的常量不去重
  (define (neg-zero? x) (and (flonum? x) (flzero? x) (fl<? (fl/ 1.0 x) 0.0)))

  (define (signed-zero-constant? d)
    (and (number? d) (inexact? d)
         (or (neg-zero? d)
             (and (not (real? d)) (or (neg-zero? (real-part d)) (neg-zero? (imag-part d)))))))

  ;; 按 Equals 去重只对 Scheme 的数据安全。宿主对象（cleat-055：Clojure 的值）的 Equals 可能跨类型、忽略元数据——
  ;; Clojure 的 () 与 []、'(1 2) 与 [1 2] 相等，带元数据的符号与不带的相等——合并会让 Clojure 代码看到错的常量。
  ;; 这些对象只有同一个对象（eq?）才共用下标。表的值为 (下标 . 常量)。
  (define (equals-dedupable? d)
    (or (number? d) (char? d) (string? d) (symbol? d) (boolean? d) (null? d) (pair? d) (vector? d) (bytevector? d)))

  (define (constant-index! u d)
    (let ((hit (and (not (signed-zero-constant? d)) (hashtable-ref (unit-const-index u) d #f))))
      (if (and hit (or (eq? (cdr hit) d) (equals-dedupable? d)))
          (car hit)
          (let ((k (unit-nconsts u)))
            (unless (or (signed-zero-constant? d) hit) (hashtable-set! (unit-const-index u) d (cons k d)))
            (unit-consts-set! u (cons d (unit-consts u)))
            (unit-nconsts-set! u (+ k 1))
            k))))

  (define (add-namespace! u ns)
    (unless (member ns (unit-namespaces u))
      (unit-namespaces-set! u (append (unit-namespaces u) (list ns)))))

  (define (add-reference! u r)
    (unless (member r (unit-references u))
      (unit-references-set! u (append (unit-references u) (list r)))))

  (define (mfield! u x)
    (or (hashtable-ref (unit-mfield-of u) x #f)
        (let ((f (string-append "m" (number->string (length (unit-mfields u))) "$" (sanitize (pretty x)))))
          (hashtable-set! (unit-mfield-of u) x f)
          (unit-mfields-set! u (cons f (unit-mfields u)))
          f)))

  ;; 类型名只用安全字符：反射按名字查类型时 . + , [ ] & * 等有特殊含义
  (define (sanitize s)
    (let ((s (if (symbol? s) (symbol->string s) s)))
      (list->string
        (map (lambda (c)
               (if (or (char-alphabetic? c) (char-numeric? c) (memv c '(#\$ #\_ #\- #\? #\! #\< #\> #\= #\* #\%)))
                   (if (memv c '(#\* #\< #\>)) #\_ c)
                   #\_))
             (string->list s)))))

  (define (pretty x)
    (let* ((s (if (symbol? x) (symbol->string x) x))
           (n (string-length s)))
      (if (and (> n 2) (char=? (string-ref s 0) #\g) (char=? (string-ref s 1) #\$))
          (let loop ((i 2))
            (cond
              ((= i n) s)
              ((char=? (string-ref s i) #\$) (substring s 2 i))
              (else (loop (+ i 1)))))
          s)))

  ;; ———————————————— 方法上下文 ————————————————

  (define-record-type mctx
    (fields unit
            class                ; 所属类型名
            static?
            vars                 ; eq 哈希表：变量 → (arg . i) | (local . i) | (tlocal i . 类型) | (field . 名字) | (self)
            (mutable locals)     ; 局部变量类型（逆序）
            (mutable nlocals)
            (mutable buffer)     ; 指令（逆序）
            (mutable nlabels)
            (mutable try-depth)
            loop-label           ; 自尾调用跳回的标签，#f 表示不做
            loop-arity           ; 当前子句的固定参数个数（有剩余参数时为 #f）
            self-mref            ; 已知过程自己的库变量（自尾调用识别），或 #f
            arg-base             ; 第一个形参的 ldarg 下标（实例方法为 1）
            labels               ; eq 哈希表：label 的名字 → (IL 标签 . 形参表)（局部循环，iron-034b）
            (mutable big?)))     ; 方法体很大（只执行一次的测试过程之类）：不内联算术（iron-034f）

  (define (make-method-ctx u class static? loop-label loop-arity self-mref)
    (make-mctx u class static? (make-eq-hashtable) '() 0 '() 0 0 loop-label loop-arity self-mref (if static? 0 1) (make-eq-hashtable) #f))

  ;; code（指令树）里有没有 (br L)
  (define (branches-to? code l)
    (exists (lambda (i) (and (pair? i) (or (and (eq? (car i) 'br) (pair? (cdr i)) (eq? (cadr i) l)) (branches-to? i l)))) code))

  (define (emit! mc . ins)
    (for-each (lambda (i) (mctx-buffer-set! mc (cons i (mctx-buffer mc)))) ins))

  (define (new-label! mc)
    (let ((n (mctx-nlabels mc)))
      (mctx-nlabels-set! mc (+ n 1))
      (string->symbol (string-append "L" (number->string n)))))

  (define (new-local! mc type)
    (let ((n (mctx-nlocals mc)))
      (mctx-locals-set! mc (cons type (mctx-locals mc)))
      (mctx-nlocals-set! mc (+ n 1))
      n))

  ;; 在单独的缓冲区中生成代码，返回指令列表（正序）
  (define (with-buffer mc thunk)
    (let ((saved (mctx-buffer mc)))
      (mctx-buffer-set! mc '())
      (thunk)
      (let ((code (reverse (mctx-buffer mc))))
        (mctx-buffer-set! mc saved)
        code)))

  (define (emit-code! mc code) (for-each (lambda (i) (emit! mc i)) code))

  ;; 在 try 区域里生成代码（try-depth + 1：区域内不发 tail.），返回指令列表
  (define (in-try mc thunk)
    (mctx-try-depth-set! mc (+ (mctx-try-depth mc) 1))
    (let ((code (with-buffer mc thunk)))
      (mctx-try-depth-set! mc (- (mctx-try-depth mc) 1))
      code))

  (define (contains-try? code) (exists (lambda (i) (eq? (car i) 'try)) code))

  (define (reload-cast code)
    (if (null? code)
        '()
        (let ((last-i (list-ref code (- (length code) 1))))
          (cond
            ((and (pair? last-i) (eq? (car last-i) 'castclass)) (list last-i))
            ((and (pair? last-i) (memq (car last-i) '(call callvirt)) (= (length last-i) 5)
                  (equal? (list-ref last-i 4) proc-type))
             (list `(castclass ,(list-ref last-i 4))))
            (else '())))))

  ;; 依次生成多个操作数（每个 gen 产生一个值）。排在后面的操作数含 try 时，先全部存入临时变量（lift-try）。
  (define (operands! mc gens)
    (let ((codes (map (lambda (g) (with-buffer mc g)) gens)))
      (if (and (pair? codes) (exists contains-try? (cdr codes)))
          (let ((temps (map (lambda (c) (let ((t (new-local! mc obj))) (emit-code! mc c) (emit! mc `(stloc ,t)) t)) codes)))
            ;; 临时变量是 object：操作数原来以 castclass（IFn、Var …）或返回 Procedure 的调用结尾时，取回后再转换一次，
            ;; 否则栈上的类型与随后的 callvirt 不符（ILVerify StackUnexpected；cleat-053 在 Clojure 镜像里发现）
            (for-each (lambda (t c) (emit! mc `(ldloc ,t)) (for-each (lambda (i) (emit! mc i)) (reload-cast c))) temps codes))
          (for-each (lambda (c) (emit-code! mc c)) codes))))

  ;; 把栈上 n 个值（已按顺序压栈）收进 object[]：先存临时变量再建数组
  (define (pack-array! mc n)
    (let ((temps (let loop ((i 0) (acc '())) (if (= i n) acc (loop (+ i 1) (cons (new-local! mc obj) acc))))))
      ;; temps 为逆序：最后压栈的值先出栈
      (for-each (lambda (t) (emit! mc `(stloc ,t))) temps)
      (emit! mc `(ldc.i4 ,n) `(newarr ,obj))
      (let loop ((ts (reverse temps)) (i 0))
        (unless (null? ts)
          (emit! mc '(dup) `(ldc.i4 ,i) `(ldloc ,(car ts)) '(stelem.ref))
          (loop (cdr ts) (+ i 1))))))

  ;; ———————————————— 结果按上下文收尾 ————————————————

  ;; 栈上是一个 object
  (define (finish-value! mc ctx)
    (case ctx
      ((value) (void))
      ((effect) (emit! mc '(pop)))
      ((test) (emit! mc `(call ,rt-type "IsTrue" (,obj) ,bool-type)))
      ((tail) (emit! mc '(ret)))))

  ;; 栈上是一个 CLR bool
  (define (finish-bool! mc ctx)
    (case ctx
      ((test) (void))
      ((effect) (emit! mc '(pop)))
      ((value) (emit! mc `(call ,rt-type "Bool" (,bool-type) ,obj)))
      ((tail) (emit! mc `(call ,rt-type "Bool" (,bool-type) ,obj)) (finish-value! mc 'tail))))

  ;; 没有值（赋值等）
  (define (finish-none! mc ctx)
    (case ctx
      ((effect) (void))
      ((test) (emit! mc '(ldc.i4 1)))
      ((value) (emit! mc `(ldsfld ,unspecified-type "Instance")))
      ((tail) (emit! mc `(ldsfld ,unspecified-type "Instance")) (finish-value! mc 'tail))))

  ;; try 区域（try-finally / try-catch 的体）内不发 tail.：IL 不允许从受保护区域尾调用
  (define (tail-ok? mc ctx)
    (and (eq? ctx 'tail) (= (mctx-try-depth mc) 0) (target-tail-calls? (current-target))))

  ;; ———————————————— 常量 ————————————————

  ;; 落单的代理项无法写成 Lcil 字符串，这样的字符串进常量表
  (define (bmp-safe-string? s)
    (let ((n (string-length s)))
      (let loop ((i 0))
        (or (= i n)
            (let ((k (char->integer (string-ref s i))))
              (cond
                ((and (<= #xD800 k #xDBFF) (< (+ i 1) n) (<= #xDC00 (char->integer (string-ref s (+ i 1))) #xDFFF)) (loop (+ i 2)))
                ((<= #xD800 k #xDFFF) #f)
                (else (loop (+ i 1)))))))))

  (define (load-constant! mc d)
    (cond
      ((and (fixnum? d) (exact? d)) (emit! mc `(ldc.i4 ,d) `(call ,rt-type "Box" (,int32-type) ,obj)))
      ((eq? d #t) (emit! mc `(ldsfld ,rt-type "True")))
      ((eq? d #f) (emit! mc `(ldsfld ,rt-type "False")))
      ((null? d) (emit! mc '(ldnull)))
      ((unspecified? d) (emit! mc `(ldsfld ,unspecified-type "Instance")))
      ((and (char? d) (< (char->integer d) #x10000))
       (emit! mc `(ldc.i4 ,(char->integer d)) `(box ,char-type)))
      ((and (immutable-string? d) (bmp-safe-string? d)) (emit! mc `(ldstr ,d)))
      (else (emit! mc `(ldconst ,(constant-index! (mctx-unit mc) d))))))

  ;; 只有 System.String（不可变、不含代理码元）用 ldstr；SchemeString（可变串、含 BMP 外字符的串，iron-055）
  ;; 与旧的可变串 StringBuilder 进常量表，加载时经 Fasl 还原为正确的表示
  (define (immutable-string? s) ($immutable-string? s))

  (define (load-symbol! mc s) (emit! mc `(ldconst ,(constant-index! (mctx-unit mc) s))))

  ;; ———————————————— 拆箱的局部变量（iron-034c，D4）————————————————
  ;;
  ;; 候选：普通方法里 let 绑定的变量与 label 的形参（不被 set!、不被任何 lambda 捕获）。
  ;; 定义点（let 的右边、各 jump 的实参）的类型全是 fixnum（或全是 flonum）时，变量用 int32（double）的 IL 局部变量，
  ;; 读作 object 时才装箱。表达式的类型：fixnum / flonum 常量、$fx… / $fl… 原语与 fx… / fl… 的内联调用的结果
  ;; （它们要么返回该类型、要么报错）、已定类型的变量、两个分支同类型的 if。通用 + - * 可能溢出成大整数，不算。
  ;; 循环变量靠乐观不动点定类型（定义点引用变量自己）。
  (define var-types (make-eq-hashtable))     ; 变量 → fx | fl；每个编译单元重算

  ;; 测量用（iron-034）：环境变量 IRONSCHEME_NO_034 设置时关掉 034 的生成优化（内联算术、拆箱、单元内直接调用），
  ;; 同一个 boot 镜像里对照编译时间与运行时间
  (define no-034 (or ($get-environment-variable "IRONSCHEME_NO_034") ""))
  (define (opt-off? name)
    (or (string=? no-034 "1")
        (let ((n (string-length name)) (m (string-length no-034)))
          (let loop ((i 0)) (and (<= (+ i n) m) (or (string=? (substring no-034 i (+ i n)) name) (loop (+ i 1))))))))
  (define opt-034? (not (opt-off? "known")))
  (define opt-inline? (not (opt-off? "inline")))
  (define opt-infer? (not (opt-off? "infer")))
  (define opt-shared? (not (opt-off? "shared")))

  ;; ———————————————— boot 镜像的接口（iron-034e，D5）————————————————
  ;; nano 构建脚本在镜像旁写出 cleat.scheme.boot.ifc：
  ;;   (ironscheme-interface 1 "镜像 id" ("位置符号名" "类名" ((固定参数个数 . 剩余?) …)) …)
  ;; 编译别的单元时，接口文件的 id 与正在运行的镜像一致才用；(gref 位置) 若是其中的已知过程，
  ;; 值就是那个类的 Instance，参数个数相符的调用直接 call 类::C<i>（类名带程序集名 cleat.scheme.boot）。
  ;; 用过的单元在入口核对镜像 id（BootInterface.Check），镜像重新构建后旧的编译产物报错 / 退回源码。

  ;; 与 forms 对齐的布尔表：只对含循环（lower-loops 生成了 label）的形式做类型推断（iron-034c）。
  ;; 拆箱的收益几乎都在循环变量上，推断一遍约占编译分配的 7%；#f 时对全部形式做
  (define loop-forms (make-parameter #f))

  (define boot-assembly-name "cleat.scheme.boot")

  ;; 原语经符号取：编译器库也会在旧镜像（没有这些原语）里从源码加载，例如构建新镜像时
  (define (host-prim name)
    (and (symbol-bound? name) (symbol-value name)))

  ;; (id . 字符串哈希表：位置符号名 → known) 或 #f；每个进程读一次
  (define boot-interface-cache 'unread)
  (define (boot-interface)
    (when (eq? boot-interface-cache 'unread)
      (set! boot-interface-cache (if opt-034? (read-boot-interface) #f)))
    boot-interface-cache)

  (define (read-boot-interface)
    (let ((file-of (host-prim '$boot-interface-file)) (id-of (host-prim '$boot-image-id)))
      (and file-of id-of
           (let ((file (file-of)) (id (id-of)))
             (and (string? file) (string? id)
                  (let ((x (guard (e (#t #f)) (call-with-input-file file read))))
                    (and (pair? x) (eq? (car x) 'ironscheme-interface)
                         (pair? (cdr x)) (eqv? (cadr x) 1)
                         (pair? (cddr x)) (equal? (caddr x) id)
                         (let ((table (make-hashtable string-hash string=?)))
                           (for-each
                             (lambda (entry)
                               (let ((class (string-append (cadr entry) ", " boot-assembly-name)))
                                 (hashtable-set! table (car entry)
                                   (make-known class (caddr entry) class (if (pair? (cdddr entry)) (cadddr entry) "")))))
                             (cdddr x))
                           (cons id table)))))))))

  ;; (gref s) 的已知过程：本单元的导出，或 boot 镜像接口里的
  (define (known-loc u s)
    (or (hashtable-ref (unit-known-loc u) s #f)
        (and (not (eq? (unit-boot-ref u) 'off))
             (boot-location? s)
             (let ((k (hashtable-ref (cdr (boot-interface)) (symbol->string s) #f)))
               (when k (unit-boot-ref-set! u #t))
               k))
        (and opt-034?
             (not (string=? (unit-name u) boot-assembly-name))
             (not (boot-location? s))
             (library-known u s))))

  ;; 用户库的接口文件（iron-034e）：compile-library-dll 在 .fasl / 库 dll 旁写出 名字.ifc，
  ;;   (ironscheme-interface 1 "程序集名" ("位置符号名" "类名" 子句形状) …)
  ;; 运行时加载这些产物时登记旁边的接口文件（原语 $library-interface-files，按加载顺序）。
  ;; 只用真正加载过的产物的接口：装入的库实例就是那个产物，它的程序集已加载、模块字段由其入口初始化。
  ;; 同一进程里刚编译、但装入的是源码展开实例的库（compile-library 之后）不用——它的产物程序集没有加载。
  ;; 位置符号是展开时的 gensym，每次展开都不同：库从源码重新展开后旧条目不会命中，所以不需要另外的 id 核对；
  ;; 编译好的程序 / 库对被调库编译实例的依赖由现有的 id 检查保护（requires a different compilation of a library）。
  (define library-ifc-table (make-hashtable string-hash string=?))
  (define library-ifc-seen 0)
  (define (library-known u s)
    (let ((files-of (host-prim '$library-interface-files)))
      (when files-of
        (let* ((files (files-of)) (n (length files)))
          (when (> n library-ifc-seen)
            (for-each read-library-interface! (list-tail files library-ifc-seen))
            (set! library-ifc-seen n)))))
    (hashtable-ref library-ifc-table (symbol->string s) #f))

  (define (read-library-interface! file)
    (let ((x (guard (e (#t #f)) (call-with-input-file file read))))
      (when (and (pair? x) (eq? (car x) 'ironscheme-interface)
                 (pair? (cdr x)) (eqv? (cadr x) 1) (pair? (cddr x)) (string? (caddr x)))
        (add-interface-entries! library-ifc-table (caddr x) (cdddr x)))))

  (define (add-interface-entries! table asm entries)
    (for-each
      (lambda (entry)
        (let ((class (string-append (cadr entry) ", " asm)))
          (hashtable-set! table (car entry)
            (make-known class (caddr entry) class (if (pair? (cdddr entry)) (cadddr entry) "")))))
      entries))

;; 上一个单元的接口条目：(("位置符号名" "类名" 子句形状) …)，供构建脚本写 boot 的 .ifc、compile-library-dll 写库的 .ifc。
  ;; 规范 gensym 名时（boot）位置符号名取改名后的
  (define last-renamed #f)
  (define last-interface '())
  (define last-unit #f)
  (define last-canonical? #f)
  (define (unit-interface)
    (when last-unit
      (set! last-interface (compute-interface last-unit last-canonical?))
      (set! last-unit #f))
    last-interface)

  ;; 每个单元结束时只记下单元本身，条目在有人要（unit-interface）时才算
  (define (record-interface! u)
    (set! last-unit u)
    (set! last-canonical? (and (canonical-gensyms) #t))
    (set! last-interface '()))

  (define (compute-interface u canonical?)
      (if (or (not canonical?) last-renamed)
          (let-values (((ss ks) (hashtable-entries (unit-known-loc u))))
            (list-sort (lambda (a b) (string<? (car a) (car b)))
                       (let loop ((i 0) (acc '()))
                         (if (= i (vector-length ss))
                             acc
                             (let ((name (if canonical?
                                             (hashtable-ref last-renamed (symbol->string (vector-ref ss i)) #f)
                                             (symbol->string (vector-ref ss i))))
                                   (k (vector-ref ks i)))
                               (loop (+ i 1)
                                     (if (and name (string=? (known-owner k) (known-class k)))
                                         ;; 共享持有类中的过程（034d）多一项前缀
                                         (cons (if (string=? (known-prefix k) "")
                                                   (list name (known-class k) (known-clauses k))
                                                   (list name (known-class k) (known-clauses k) (known-prefix k)))
                                               acc)
                                         acc)))))))
          '()))

  (define fx-result-prims '($fx+ $fx- $fx* $fxand $fxior $fxxor $fxnot $as-fixnum))
  (define fx-compare-prims '($fx=? $fx<? $fx>? $fx<=? $fx>=? $fxzero? $fxnegative? $fxpositive?))
  (define fl-compare-prims '($fl=? $fl<? $fl>? $fl<=? $fl>=?))
  (define fl-result-prims '($fl+ $fl- $fl* $fl/ $as-flonum))

  ;; 表达式的类型：fx / fl / #f；env：变量 → fx | fl | top（还没定）| #f
  (define (expr-type e env)
    (nanopass-case (L3 Expr) e
      [(quote ,d) (cond ((and (fixnum? d) (exact? d)) 'fx) ((flonum? d) 'fl) (else #f))]
      [,x (hashtable-ref env x #f)]
      [(primcall ,pr ,e* ...)
       (cond ((memq pr fx-result-prims) 'fx) ((memq pr fl-result-prims) 'fl) (else #f))]
      [(call ,e0 ,e* ...)
       (let ((op (inline-op e0 (length e*))))
         (and op (memq (car op) '(add sub mul div)) (memq (cdr op) '(fx fl)) (cdr op)))]
      [(if ,e0 ,e1 ,e2) (meet-type (expr-type e1 env) (expr-type e2 env))]
      [(begin ,e* ... ,e) (expr-type e env)]
      [(let ([,x* ,e*] ...) ,body) (expr-type body env)]
      [(jump ,x ,e* ...) 'top]                 ; 不返回：不影响 if 另一支的类型
      [(throw ,e) 'top]
      [else #f]))

  (define (meet-type a b)
    (cond ((eq? a 'top) b) ((eq? b 'top) a) ((eq? a b) a) (else #f)))

  ;; 收集候选（iron-034c）。用库层的一个 define-pass 遍历整棵树：nanopass-case 每次调用都现场建一个 pass
  ;; （一组闭包），按节点调用它的遍历每个节点约 1 KB，编译种子时多分配一百多 MB
  ;; 句法上可能有类型的右边（expr-type 能给出 fx / fl 的形状）才登记为定义；其余一定是 #f
  ;; candidate?：变量是否已登记为候选（外层的 let 与 label 先于内层登记，所以引用处已经知道）
  ;; use!：变量 x 的一次引用，raw? 为真表示在能直接用原始值的位置（算术原语与内联算术的操作数、let 的右边、
  ;; jump 的实参），否则读出后要装箱（传参、存进数据结构 …）。装箱用途多于原始用途的变量不拆箱：
  ;; 每次读都装箱反而更慢（double 每次分配）
  (define-pass infer-walk : L3 (ir add-def! exclude! labels candidate? use!) -> * (v)
    ;; 处理器只取一个参数，for-each 直接传处理器，不为每个节点新建闭包
    (MaybeTyped : Expr (ir) -> * (b)
      [(quote ,d) (or (fixnum? d) (flonum? d))]
      [,x (candidate? x)]
      [(primcall ,pr ,e* ...) (and (or (memq pr fx-result-prims) (memq pr fl-result-prims)) #t)]
      [(call ,e0 ,e* ...) (and (inline-op e0 (length e*)) #t)]
      [(if ,e0 ,e1 ,e2) (or (MaybeTyped e1) (MaybeTyped e2))]
      [(begin ,e* ... ,e) (MaybeTyped e)]
      [(let ([,x* ,e*] ...) ,body) (MaybeTyped body)]
      [(jump ,x ,e* ...) #t]
      [(throw ,e) #t]
      [else #f])
    (Body : Clause (ir) -> * (v)
      [(clause (,x* ...) ,x ,body) (Expr body)])
    ;; 原始值位置的操作数：变量记一次原始用途，其余照常遍历
    (Raw : Expr (ir) -> * (v)
      [,x (use! x #t)]
      [else (Expr ir)])
    (Expr : Expr (ir) -> * (v)
      [,x (use! x #f)]
      [(quote ,d) (void)]
      [(if ,e0 ,e1 ,e2) (Expr e0) (Expr e1) (Expr e2)]
      [(begin ,e* ... ,e) (for-each Expr e*) (Expr e)]
      [(set! ,x ,e) (exclude! x) (Expr e)]
      [(gref ,s) (void)]
      [(gset! ,s ,e) (Expr e)]
      [(mref ,x) (void)]
      [(mset! ,x ,e) (Expr e)]
      [(prim ,pr) (void)]
      [(primcall ,pr ,e* ...)
       (if (or (memq pr fx-result-prims) (memq pr fl-result-prims) (memq pr fx-compare-prims) (memq pr fl-compare-prims))
           (for-each Raw e*)
           (for-each Expr e*))]
      [(lambda ,nm (,x* ...) ,cl* ...)
       (for-each exclude! x*)
       (for-each Body cl*)]
      [(letrec ([,x* ,le*] ...) ,body) (for-each Expr le*) (Expr body)]
      [(let ([,x* ,e*] ...) ,body)
       (for-each (lambda (x e) (when (MaybeTyped e) (add-def! x e))) x* e*)
       (for-each Raw e*)
       (Expr body)]
      [(library ,d ([,x* ,s* ,e*] ...) ,body) (for-each Expr e*) (Expr body)]
      [(call ,e ,e* ...) (Expr e) (for-each (if (inline-op e (length e*)) Raw Expr) e*)]
      [(and ,e* ...) (for-each Expr e*)]
      [(or ,e* ...) (for-each Expr e*)]
      [(throw ,e) (Expr e)]
      [(break) (void)]
      [(try-finally ,e0 ,e1) (Expr e0) (Expr e1)]
      [(try-catch ,e (,d* ,e*) ...) (Expr e) (for-each Expr e*)]
      [(label ,x (,x* ...) ,body0 ,body1)
       (hashtable-set! labels x x*)
       (for-each (lambda (p) (add-def! p #f)) x*)
       (Expr body0) (Expr body1)]
      [(jump ,x ,e* ...)
       (let ((ps (hashtable-ref labels x #f)))
         (when ps (for-each add-def! ps e*)))
       (for-each Raw e*)]
      [(host-call ,d0 ,d1 ,st ,e* ...) (for-each Expr e*)]
      [(host-field-get ,d0 ,d1 ,st ,e* ...) (for-each Expr e*)]
      [(host-field-set ,d0 ,d1 ,st ,e* ...) (for-each Expr e*)]
      [(host-new ,d ,e* ...) (for-each Expr e*)]
      [(host-new-array ,d ,e) (Expr e)]
      [(host-cast ,d ,e) (Expr e)]
      [(host-is ,d ,e) (Expr e)]
      [(unsupported ,d ,e* ...) (for-each Expr e*)]
      [(box ,e) (Expr e)]
      [(set-box! ,x ,e) (Expr e)]
      [(clj-var-set! ,d ,e) (Expr e)]
      [(clj-def ,d ,st0 ,st1 ,e0 ,e1) (Expr e0) (Expr e1)]
      [(clj-invoke ,e ,e* ...) (Expr e) (for-each Expr e*)]
      [(clj-keyword-invoke ,d ,e ,e* ...) (Expr e) (for-each Expr e*)]
      [(clj-call ,d0 ,d1 ,e* ...) (for-each Expr e*)]
      [(clj-case ,d ,e0 ,e1 ,e* ...) (Expr e0) (Expr e1) (for-each Expr e*)]
      [(clj-deftype ,d (,x* ...) ,le* ...) (for-each exclude! x*) (for-each Expr le*)]
      [else (void)])
    (Expr ir))

  ;; 过程体的 L3 节点数是否超过 n（数到 n 就停）；嵌套的 lambda 是别的方法，不数
  (define-pass count-nodes : L3 (ir n) -> * (v)
    (definitions
      (define count 0)
      (define (more?) (set! count (+ count 1)) (<= count n)))
    (Expr : Expr (ir) -> * (v)
      [(lambda ,nm (,x* ...) ,cl* ...) (void)]
      [(if ,e0 ,e1 ,e2) (when (more?) (Expr e0) (Expr e1) (Expr e2))]
      [(begin ,e* ... ,e) (when (more?) (for-each Expr e*) (Expr e))]
      [(set! ,x ,e) (when (more?) (Expr e))]
      [(gset! ,s ,e) (when (more?) (Expr e))]
      [(mset! ,x ,e) (when (more?) (Expr e))]
      [(primcall ,pr ,e* ...) (when (more?) (for-each Expr e*))]
      [(letrec ([,x* ,le*] ...) ,body) (when (more?) (Expr body))]
      [(let ([,x* ,e*] ...) ,body) (when (more?) (for-each Expr e*) (Expr body))]
      [(call ,e ,e* ...) (when (more?) (Expr e) (for-each Expr e*))]
      [(and ,e* ...) (when (more?) (for-each Expr e*))]
      [(or ,e* ...) (when (more?) (for-each Expr e*))]
      [(throw ,e) (when (more?) (Expr e))]
      [(try-finally ,e0 ,e1) (when (more?) (Expr e0) (Expr e1))]
      [(try-catch ,e (,d* ,e*) ...) (when (more?) (Expr e) (for-each Expr e*))]
      [(label ,x (,x* ...) ,body0 ,body1) (when (more?) (Expr body0) (Expr body1))]
      [(jump ,x ,e* ...) (when (more?) (for-each Expr e*))]
      [(host-call ,d0 ,d1 ,st ,e* ...) (when (more?) (for-each Expr e*))]
      [(host-new ,d ,e* ...) (when (more?) (for-each Expr e*))]
      [else (more?)])
    (Expr ir)
    (> count n))

  (define (infer-var-types! form)
    (let ((defs (make-eq-hashtable))       ; 候选变量 → 定义表达式列表（#f 是占位：label 的形参先登记）
          (excluded (make-eq-hashtable))
          (labels (make-eq-hashtable)))    ; label 名 → 形参表
      (define raw-uses (make-eq-hashtable))
      (define boxed-uses (make-eq-hashtable))
      (infer-walk form
                  (lambda (x e) (hashtable-set! defs x (let ((l (hashtable-ref defs x '()))) (if e (cons e l) l))))
                  (lambda (v) (hashtable-set! excluded v #t))
                  labels
                  (lambda (x) (hashtable-contains? defs x))
                  (lambda (x raw?)
                    (when (hashtable-contains? defs x)
                      (let ((t (if raw? raw-uses boxed-uses)))
                        (hashtable-set! t x (+ 1 (hashtable-ref t x 0)))))))
      ;; 工作表求不动点：类型只会从 top 往下走（top → fx/fl → #f），某个变量变了只重算依赖它的变量
      (let ((env (make-eq-hashtable))
            (users (make-eq-hashtable))     ; 变量 → 定义里引用了它的候选
            (cands (filter (lambda (x) (not (hashtable-ref excluded x #f)))
                           (vector->list (hashtable-keys defs)))))
        (define (deps e)                     ; expr-type 会查的变量
          (nanopass-case (L3 Expr) e
            [,x (list x)]
            [(if ,e0 ,e1 ,e2) (append (deps e1) (deps e2))]
            [(begin ,e* ... ,e) (deps e)]
            [(let ([,x* ,e*] ...) ,body) (deps body)]
            [else '()]))
        (for-each (lambda (x) (hashtable-set! env x 'top)) cands)
        (for-each (lambda (x)
                    (for-each (lambda (e)
                                (for-each (lambda (v) (hashtable-set! users v (cons x (hashtable-ref users v '()))))
                                          (deps e)))
                              (hashtable-ref defs x '())))
                  cands)
        (let loop ((work cands))
          (unless (null? work)
            (let* ((x (car work))
                   (old (hashtable-ref env x #f))
                   (new (and old
                             (let f ((es (hashtable-ref defs x '())) (t 'top))
                               (if (or (null? es) (not t)) t (f (cdr es) (meet-type t (expr-type (car es) env))))))))
              (if (and old (not (eq? old new)))
                  (begin
                    (hashtable-set! env x new)
                    (loop (append (hashtable-ref users x '()) (cdr work))))
                  (loop (cdr work))))))
        (for-each (lambda (x)
                    (let ((t (hashtable-ref env x #f)))
                      (when (and (memq t '(fx fl))
                                 (<= (hashtable-ref boxed-uses x 0) (hashtable-ref raw-uses x 0)))
                        (hashtable-set! var-types x t))))
                  cands))))

  (define (type-clr t) (if (eq? t 'fl) double-type int32-type))

  ;; 把 e 的值以 t（fx / fl）的原始形式压栈；e 的类型必须已知是 t（expr-type 保证），否则先求值再 unbox.any
  (define (cg-raw e mc t)
    (define (fallback) (cg e mc 'value) (emit! mc `(unbox.any ,(type-clr t))))
    (nanopass-case (L3 Expr) e
      [(quote ,d)
       (cond ((and (eq? t 'fx) (fixnum? d) (exact? d)) (emit! mc `(ldc.i4 ,d)))
             ((and (eq? t 'fl) (flonum-constant e)) (emit! mc `(ldc.r8 ,d)))
             (else (fallback)))]
      [,x (let ((loc (hashtable-ref (mctx-vars mc) x #f)))
            (if (and loc (eq? (car loc) 'tlocal) (eq? (cddr loc) t))
                (emit! mc `(ldloc ,(cadr loc)))
                (fallback)))]
      [(primcall ,pr ,e* ...)
       (if (and (eq? (expr-type e var-types) t) (raw-prim! pr e* mc))
           (void)
           (fallback))]
      [(call ,e0 ,e* ...)
       (let ((op (inline-op e0 (length e*))))
         (if (and op (eq? (cdr op) 'fl) (eq? t 'fl) (memq (car op) '(add sub mul div))
                  (for-all (lambda (a) (eq? (expr-type a var-types) 'fl)) e*))
             (begin
               (for-each (lambda (a) (cg-raw a mc 'fl)) e*)
               (emit! mc (case (car op) ((add) '(add)) ((sub) '(sub)) ((mul) '(mul)) (else '(div)))))
             (fallback)))]
      [else (fallback)]))

  ;; $fx… / $fl… 算术原语的原始结果（不装箱）；不认识时返回 #f 且不生成代码
  (define (raw-prim! pr e* mc)
    (define (args t) (operands! mc (map (lambda (x) (lambda () (cg-raw x mc t))) e*)))
    (let ((argc (length e*)))
      (case pr
        (($fx+ $fx* $fxand $fxior $fxxor)
         (and (= argc 2)
              (begin (args 'fx)
                     (emit! mc (case pr (($fx+) '(add)) (($fx*) '(mul)) (($fxand) '(and)) (($fxior) '(or)) (else '(xor))))
                     #t)))
        (($fx-) (and (<= 1 argc 2) (begin (args 'fx) (emit! mc (if (= argc 1) '(neg) '(sub))) #t)))
        (($fxnot) (and (= argc 1) (begin (args 'fx) (emit! mc '(not)) #t)))
        ;; typed-lambda 的形参（parse 生成，D6）：原始值就是拆箱后的参数
        (($as-fixnum) (and (= argc 1) (begin (args 'fx) #t)))
        (($as-flonum) (and (= argc 1) (begin (args 'fl) #t)))
        (($fl+ $fl* $fl/)
         (and (= argc 2) (begin (args 'fl) (emit! mc (case pr (($fl+) '(add)) (($fl*) '(mul)) (else '(div)))) #t)))
        (($fl-) (and (<= 1 argc 2) (begin (args 'fl) (emit! mc (if (= argc 1) '(neg) '(sub))) #t)))
        (else #f))))

  (define (box-raw! mc t)
    (if (eq? t 'fl)
        (emit! mc `(box ,double-type))
        (emit! mc `(call ,rt-type "Box" (,int32-type) ,obj))))

  ;; ———————————————— 变量 ————————————————

  (define (var-location mc x)
    (or (hashtable-ref (mctx-vars mc) x #f)
        (error 'codegen "unbound variable" x)))

  (define (load-var! mc x)
    (let ((loc (var-location mc x)))
      (case (car loc)
        ((arg) (emit! mc `(ldarg ,(cdr loc))))
        ((local) (emit! mc `(ldloc ,(cdr loc))))
        ((tlocal) (emit! mc `(ldloc ,(cadr loc))) (box-raw! mc (cddr loc)))
        ((field dfield) (emit! mc '(ldarg 0) `(ldfld ,(mctx-class mc) ,(cdr loc))))
        ((self) (cond
                  ((pair? (cdr loc)) (emit! mc `(ldsfld ,(cadr loc) ,(cddr loc))))   ; 034d：共享持有类中的过程
                  ((mctx-static? mc) (emit! mc `(ldsfld ,(mctx-class mc) "Instance")))
                  (else (emit! mc '(ldarg 0))))))))

  (define (store-var! mc x)
    (let ((loc (var-location mc x)))
      (case (car loc)
        ((arg) (emit! mc `(starg ,(cdr loc))))
        ((local) (emit! mc `(stloc ,(cdr loc))))
        ((tlocal) (emit! mc `(stloc ,(cadr loc))))   ; 栈上须是原始值（cg-raw）
        ;; deftype* 的可变字段（cleat-051）
        ((dfield) (let ((t (new-local! mc obj)))
                    (emit! mc `(stloc ,t) '(ldarg 0) `(ldloc ,t) `(stfld ,(mctx-class mc) ,(cdr loc)))))
        (else (error 'codegen "cannot assign captured variable (should be boxed)" x)))))

  ;; 为词法变量分配存储：IL 局部变量（有类型的是 int32 / double 局部变量）。返回下标
  (define (bind-local! mc x)
    (cond
      ((and (not (mctx-big? mc)) (hashtable-ref var-types x #f))
       => (lambda (t)
            (let ((i (new-local! mc (type-clr t))))
              (hashtable-set! (mctx-vars mc) x (cons 'tlocal (cons i t)))
              i)))
      (else (bind-local-object! mc x))))

  (define (bind-local-object! mc x)
    (let ((i (new-local! mc obj)))
      (hashtable-set! (mctx-vars mc) x (cons 'local i))
      i))

  ;; 栈顶的值绑定到新变量 x
  (define (bind-value! mc x)
    (bind-local! mc x)
    (store-var! mc x))

  ;; 变量的原始类型（fx / fl），普通变量为 #f
  (define (var-raw-type mc x)
    (let ((loc (hashtable-ref (mctx-vars mc) x #f)))
      (and loc (eq? (car loc) 'tlocal) (cddr loc))))

  ;; 实参都是 (host-cast T e) 时返回类型名列表（Lcil 的实参类型提示，发射器据此静态决议重载，iron-033）；否则 #f
  (define (cast-types e*)
    (and (pair? e*)
         (let loop ((e* e*) (acc '()))
           (if (null? e*)
               (reverse acc)
               (let ((d (nanopass-case (L3 Expr) (car e*) [(host-cast ,d ,e) d] [else #f])))
                 (and d (loop (cdr e*) (cons d acc))))))))

  ;; ———————————————— 表达式 ————————————————

  (define (cg e mc ctx)
    (nanopass-case (L3 Expr) e
      [,x (unless (eq? ctx 'effect) (load-var! mc x) (finish-value! mc ctx))]
      [(label ,x (,x* ...) ,body0 ,body1)
       ;; 局部循环（iron-034b，lower-loops 产生）：形参是 IL 局部变量，
       ;; jump 给它们赋值后 br 到循环体。body0 一次都没跳到循环体时循环体不可达，不生成
       ;; （否则无条件分支之后的标签处栈深无法单遍推导，ECMA-335 III.1.7.5）
       (for-each (lambda (p) (bind-local! mc p)) x*)
       (let ((lf (new-label! mc)) (end-l (new-label! mc)))
         (hashtable-set! (mctx-labels mc) x (cons lf x*))
         (let ((code (with-buffer mc (lambda () (cg body0 mc ctx)))))
           (emit-code! mc code)
           (when (branches-to? code lf)
             (unless (eq? ctx 'tail) (emit! mc `(br ,end-l)))
             (emit! mc `(label ,lf))
             (cg body1 mc ctx)
             (unless (eq? ctx 'tail) (emit! mc `(label ,end-l))))))]
      [(jump ,x ,e* ...)
       (let* ((entry (or (hashtable-ref (mctx-labels mc) x #f) (error 'codegen "jump to unknown label" x)))
              ;; 实参先全部求值到临时变量再写入形参（并行赋值）；有类型的形参用原始值的临时变量
              (temps (map (lambda (p a)
                            (let ((rt (var-raw-type mc p)))
                              (if rt (cg-raw a mc rt) (cg a mc 'value))
                              (let ((t (new-local! mc (if rt (type-clr rt) obj)))) (emit! mc `(stloc ,t)) t)))
                          (cdr entry) e*)))
         (for-each (lambda (p t) (emit! mc `(ldloc ,t)) (store-var! mc p)) (cdr entry) temps)
         (emit! mc `(br ,(car entry)))
         ;; 之后的代码不可达；补一个值保持栈形状一致（同 throw）
         (case ctx
           ((value) (emit! mc '(ldnull)))
           ((test) (emit! mc '(ldc.i4 0)))
           (else (void))))]
      [(quote ,d)
       (case ctx
         ((effect) (void))
         ((test) (emit! mc `(ldc.i4 ,(if d 1 0))))
         (else (load-constant! mc d) (finish-value! mc ctx)))]
      [(if ,e0 ,e1 ,e2)
       (let ((else-l (new-label! mc)) (end-l (new-label! mc)))
         (cg e0 mc 'test)
         (emit! mc `(brfalse ,else-l))
         (cg e1 mc ctx)
         (unless (eq? ctx 'tail) (emit! mc `(br ,end-l)))
         (emit! mc `(label ,else-l))
         (cg e2 mc ctx)
         (unless (eq? ctx 'tail) (emit! mc `(label ,end-l))))]
      [(begin ,e* ... ,e)
       (for-each (lambda (x) (cg x mc 'effect)) e*)
       (cg e mc ctx)]
      [(set! ,x ,e)
       (cg e mc 'value)
       (store-var! mc x)
       (finish-none! mc ctx)]
      [(gref ,s)
       (unless (eq? ctx 'effect)
         (let ((k (known-loc (mctx-unit mc) s)))
           (if k
               (emit! mc `(ldsfld ,(known-class k) ,(known-field k)))
               (begin
                 (load-symbol! mc s)
                 (emit! mc `(call ,calls-type "Global" (,obj) ,obj)))))
         (finish-value! mc ctx))]
      [(gset! ,s ,e)
       (operands! mc (list (lambda () (load-symbol! mc s)) (lambda () (cg e mc 'value))))
       (emit! mc `(call ,calls-type "SetGlobal" (,obj ,obj) ,void-type))
       (finish-none! mc ctx)]
      [(mref ,x)
       (unless (eq? ctx 'effect)
         (emit! mc `(ldsfld ,(unit-module (mctx-unit mc)) ,(mfield! (mctx-unit mc) x)))
         (finish-value! mc ctx))]
      [(mset! ,x ,e)
       (cg e mc 'value)
       (emit! mc `(stsfld ,(unit-module (mctx-unit mc)) ,(mfield! (mctx-unit mc) x)))
       (finish-none! mc ctx)]
      [(prim ,pr)
       (unless (eq? ctx 'effect)
         (load-symbol! mc pr)
         (emit! mc `(call ,calls-type "Global" (,obj) ,obj))
         (finish-value! mc ctx))]
      [(primcall ,pr ,e* ...) (cg-primcall pr e* mc ctx)]
      [(lambda ,nm (,x* ...) ,cl* ...)
       (unless (eq? ctx 'effect)
         (let ((c (lambda-class! e mc #f #f)))
           (make-closure! mc c x* #f)
           (finish-value! mc ctx)))]
      [(letrec ([,x* ,le*] ...) ,body)
       ;; 先创建全部过程对象，再填自由变量字段：组内互相引用
       (let ((classes (map (lambda (x le) (lambda-class! le mc x #f)) x* le*)))
         (for-each (lambda (x) (bind-local! mc x)) x*)
         (for-each
           (lambda (x c)
             (if (car c)
                 (load-static-proc! mc c)
                 (emit! mc `(newobj ,(cadr c) ())))
             (store-var! mc x))
           x* classes)
         (for-each
           (lambda (x c le)
             (unless (car c)
               (nanopass-case (L3 LambdaExpr) le
                 [(lambda ,nm (,x1* ...) ,cl* ...)
                  (fill-fields! mc c (remq x x1*) (lambda () (load-var! mc x)))])))
           x* classes le*)
         (cg body mc ctx))]
      [(let ([,x* ,e*] ...) ,body)
       ;; 右边依次求值并存入新变量（IL 局部变量；有类型的是 int32 / double 局部变量）；
       ;; 右边看不到同一 let 绑定的变量
       (let ((codes (map (lambda (x e)
                           (with-buffer mc (lambda ()
                                             (let ((t (and (not (mctx-big? mc)) (hashtable-ref var-types x #f))))
                                               (if t (cg-raw e mc t) (cg e mc 'value))))))
                         x* e*)))
         (for-each (lambda (x code) (emit-code! mc code) (bind-value! mc x)) x* codes)
         (cg body mc ctx))]
      [(library ,d ([,x* ,s* ,e*] ...) ,body)
       ;; 非顶层的 library-letrec*（compile-library 把库的 invoke 代码包在 lambda 中）：库变量仍是模块静态字段，
       ;; 按顺序求值并写入；不做已知过程的预链接（已知过程的直接调用不经字段，类的单例由其静态构造函数建立）
       (let ((u (mctx-unit mc)))
         (for-each
           (lambda (x s e)
             (let ((field (mfield! u x))
                   (k (hashtable-ref (unit-known u) x #f)))
               ;; 已知过程（collect-known! 的 nested-libraries）用登记的类名生成
               (if k
                   (load-static-proc! mc (lambda-class! e mc #f x k))
                   (cg e mc 'value))
               (emit! mc `(stsfld ,(unit-module u) ,field))
               (unless (null? d)
                 (emit! mc `(ldconst ,(constant-index! u s)) `(castclass ,symbol-type)
                        `(ldsfld ,(unit-module u) ,field) `(stfld ,symbol-type "Value")))))
           x* s* e*)
         (cg body mc ctx))]
      [(call ,e0 ,e* ...) (cg-call e0 e* mc ctx)]
      [(and ,e* ...)
       (if (null? e*)
           (begin (emit! mc '(ldc.i4 1)) (finish-bool! mc ctx))
           (let ((false-l (new-label! mc)) (end-l (new-label! mc)))
             (for-each (lambda (x) (cg x mc 'test) (emit! mc `(brfalse ,false-l))) e*)
             (emit! mc '(ldc.i4 1) `(br ,end-l) `(label ,false-l) '(ldc.i4 0) `(label ,end-l))
             (finish-bool! mc ctx)))]
      [(or ,e* ...)
       (if (null? e*)
           (begin (emit! mc '(ldc.i4 0)) (finish-bool! mc ctx))
           (let ((true-l (new-label! mc)) (end-l (new-label! mc)))
             (for-each (lambda (x) (cg x mc 'test) (emit! mc `(brtrue ,true-l))) e*)
             (emit! mc '(ldc.i4 0) `(br ,end-l) `(label ,true-l) '(ldc.i4 1) `(label ,end-l))
             (finish-bool! mc ctx)))]
      [(throw ,e)
       (cg e mc 'value)
       (emit! mc `(castclass ,exception-type) '(throw))
       ;; 之后的代码不可达；value / test 上下文补一个值保持栈形状一致
       (case ctx
         ((value) (emit! mc '(ldnull)))
         ((test) (emit! mc '(ldc.i4 0)))
         (else (void)))]
      [(break)
       (emit! mc `(call ,calls-type "Break" () ,obj))
       (finish-value! mc ctx)]
      [(try-finally ,e0 ,e1)
       ;; try { 值 = e0 } finally { e1 }；体在 try 区域内（try-depth + 1：不发 tail.）
       (let* ((tmp (and (not (eq? ctx 'effect)) (new-local! mc obj)))
              (body (in-try mc (lambda () (cg e0 mc (if tmp 'value 'effect)) (when tmp (emit! mc `(stloc ,tmp))))))
              (f (with-buffer mc (lambda () (cg e1 mc 'effect)))))
         (emit! mc `(try (body ,@body) (finally ,@f)))
         (when tmp
           (emit! mc `(ldloc ,tmp))
           (finish-value! mc ctx)))]
      [(try-catch ,e (,d* ,e*) ...)
       ;; ($try/catch body '类型 处理器 …)（designs/15 §3）：处理器先求值存入局部变量；
       ;; try { 值 = body } catch (T_i) { 值 = h_i(异常) } …；类型 #f 即 SchemeException，处理器收到其 Condition。
       ;; catch 块入口栈上是异常对象；块末尾由发射器补 leave。体在 try 区域内，不发 tail.
       (let* ((tmp (and (not (eq? ctx 'effect)) (new-local! mc obj)))
              (hs (map (lambda (h) (cg h mc 'value) (let ((t (new-local! mc obj))) (emit! mc `(stloc ,t)) t)) e*))
              (body (in-try mc (lambda () (cg e mc (if tmp 'value 'effect)) (when tmp (emit! mc `(stloc ,tmp))))))
              (handlers
               (map (lambda (d h)
                      (let ((exc (new-local! mc obj)))
                        `(catch ,(or d scheme-exn-type)
                           (stloc ,exc)
                           (ldloc ,h) (castclass ,proc-type)
                           (ldloc ,exc)
                           ,@(if d '() `((castclass ,scheme-exn-type) (callvirt ,scheme-exn-type "get_Condition" () ,obj)))
                           (callvirt ,proc-type "Invoke1" (,obj) ,obj)
                           ,(if tmp `(stloc ,tmp) '(pop)))))
                    d* hs)))
         (emit! mc `(try (body ,@body) ,@handlers))
         (when tmp
           (emit! mc `(ldloc ,tmp))
           (finish-value! mc ctx)))]
      [(host-call ,d0 ,d1 ,st ,e* ...)
       (operands! mc (map (lambda (x) (lambda () (cg x mc 'value))) e*))
       (let ((hints (and (string? d1) (cast-types (if st e* (cdr e*)))))
             (argc (- (length e*) (if st 0 1))))
         (emit! mc (if hints `(,(interop 'call) ,d0 ,d1 ,st ,argc ,hints) `(,(interop 'call) ,d0 ,d1 ,st ,argc))))
       (finish-value! mc ctx)]
      [(host-field-get ,d0 ,d1 ,st ,e* ...)
       (operands! mc (map (lambda (x) (lambda () (cg x mc 'value))) e*))
       (emit! mc `(,(interop 'field-get) ,d0 ,d1 ,st))
       (finish-value! mc ctx)]
      [(host-field-set ,d0 ,d1 ,st ,e* ...)
       (operands! mc (map (lambda (x) (lambda () (cg x mc 'value))) e*))
       (emit! mc `(,(interop 'field-set) ,d0 ,d1 ,st))
       (finish-value! mc ctx)]
      [(host-new ,d ,e* ...)
       (operands! mc (map (lambda (x) (lambda () (cg x mc 'value))) e*))
       (let ((hints (cast-types e*)))
         (emit! mc (if hints `(,(interop 'new) ,d ,(length e*) ,hints) `(,(interop 'new) ,d ,(length e*)))))
       (finish-value! mc ctx)]
      [(host-new-array ,d ,e) (cg e mc 'value) (emit! mc `(,(interop 'new-array) ,d)) (finish-value! mc ctx)]
      [(host-cast ,d ,e) (cg e mc 'value) (emit! mc `(,(interop 'cast) ,d)) (finish-value! mc ctx)]
      [(host-is ,d ,e) (cg e mc 'value) (emit! mc `(,(interop 'is) ,d)) (finish-value! mc ctx)]
      [(host-typeof ,d) (emit! mc `(,(interop 'typeof) ,d)) (finish-value! mc ctx)]
      [(host-using ,d) (add-namespace! (mctx-unit mc) d) (finish-none! mc ctx)]
      [(host-reference ,d) (add-reference! (mctx-unit mc) d) (finish-none! mc ctx)]
      [(host-namespaces)
       (unless (eq? ctx 'effect)
         (load-constant! mc (unit-namespaces (mctx-unit mc)))
         (finish-value! mc ctx))]
      [(unsupported ,d ,e* ...)
       (for-each (lambda (x) (cg x mc 'effect)) e*)
       (emit! mc `(ldstr ,d) `(call ,calls-type "Unsupported" (,string-type) ,obj))
       (finish-value! mc ctx)]
      ;; —— Clojure 节点（cleat-051）：直接调 clojure.lang 的方法，不经 Scheme 过程与互操作的值转换 ——
      [(clj-var-ref ,d ,st)
       ;; VarExpr：^:dynamic 的 Var 用 get（看线程绑定），否则 getRawRoot
       (unless (eq? ctx 'effect)
         (load-constant! mc d)
         (emit! mc `(castclass ,clj-var-type) `(callvirt ,clj-var-type ,(if st "get" "getRawRoot") () ,obj))
         (finish-value! mc ctx))]
      [(clj-var-set! ,d ,e)
       (operands! mc (list (lambda () (load-constant! mc d) (emit! mc `(castclass ,clj-var-type)))
                           (lambda () (cg e mc 'value))))
       (emit! mc `(callvirt ,clj-var-type "set" (,obj) ,obj))
       (finish-value! mc ctx)]
      [(clj-def ,d ,st0 ,st1 ,e0 ,e1)
       ;; DefExpr：setDynamic、bindRoot（有初值时）、setMeta；值为 Var
       (let ((var! (lambda () (load-constant! mc d) (emit! mc `(castclass ,clj-var-type)))))
         (when st1
           (var!)
           (emit! mc `(callvirt ,clj-var-type "setDynamic" () ,clj-var-type) '(pop)))
         (when st0
           (operands! mc (list var! (lambda () (cg e0 mc 'value))))
           (emit! mc `(callvirt ,clj-var-type "bindRoot" (,obj) ,void-type)))
         (operands! mc (list var! (lambda () (cg e1 mc 'value) (emit! mc `(castclass ,pmap-type)))))
         (emit! mc `(callvirt ,clj-var-type "setMeta" (,pmap-type) ,void-type))
         (unless (eq? ctx 'effect)
           (load-constant! mc d)
           (finish-value! mc ctx)))]
      [(clj-invoke ,e ,e* ...)
       ;; InvokeExpr：callvirt IFn::invoke(object × k)；超过 20 个实参时第 21 个起打包成 params 数组
       (let ((argc (length e*)))
         (operands! mc (cons (lambda () (cg e mc 'value) (emit! mc `(castclass ,ifn-type)))
                             (map (lambda (x) (lambda () (cg x mc 'value))) e*)))
         (when (> argc 20) (pack-array! mc (- argc 20)))
         (when (and (tail-ok? mc ctx) (target-clojure-tail-calls? (current-target))) (emit! mc '(tail)))
         (emit! mc `(callvirt ,ifn-type "invoke" ,(if (> argc 20) (append (objs 20) (list obj-array)) (objs argc)) ,obj))
         (finish-value! mc ctx))]
      [(clj-keyword-invoke ,d ,e ,e* ...)
       ;; KeywordInvokeExpr：目标是 ILookup 时 valAt，否则 RT.get（与 Keyword.invoke 相同）；实参先存入临时变量
       (let ((t (new-local! mc obj))
             (defs (map (lambda (x) (new-local! mc obj)) e*))
             (slow (new-label! mc))
             (end (new-label! mc))
             (rest! (lambda (defs) (for-each (lambda (l) (emit! mc `(ldloc ,l))) defs))))
         (cg e mc 'value)
         (emit! mc `(stloc ,t))
         (for-each (lambda (x l) (cg x mc 'value) (emit! mc `(stloc ,l))) e* defs)
         (emit! mc `(ldloc ,t) `(isinst ,ilookup-type) '(dup) `(brfalse ,slow))
         (load-constant! mc d)
         (rest! defs)
         (emit! mc `(callvirt ,ilookup-type "valAt" ,(objs (+ 1 (length defs))) ,obj) `(br ,end)
                `(label ,slow) '(pop) `(ldloc ,t))
         (load-constant! mc d)
         (rest! defs)
         (emit! mc `(call ,clj-rt-type "get" ,(objs (+ 2 (length defs))) ,obj) `(label ,end))
         (finish-value! mc ctx))]
      [(clj-call ,d0 ,d1 ,e* ...)
       ;; ClojureOps 的静态方法：参数与返回都是 object；d1 为固定参数个数时其余实参打包成 object[]
       (let ((argc (length e*)))
         (operands! mc (map (lambda (x) (lambda () (cg x mc 'value))) e*))
         (when d1 (pack-array! mc (- argc d1)))
         (emit! mc `(call ,clojure-ops-type ,d0 ,(if d1 (append (objs d1) (list obj-array)) (objs argc)) ,obj))
         (finish-value! mc ctx))]
      [(clj-case ,d ,e0 ,e1 ,e* ...)
       ;; CaseExpr（ClojureCLR 的语义）：键 = :int 时 ConvertToInt（非数字直接 default），否则 Util.hash；有 mask 时 (键 >> shift) & mask；
       ;; 按键 switch（键的范围稠密时用 IL switch 表，否则比较链），命中后比较测试值（:hash-identity 用同一性，其余 Util.equiv；
       ;; 哈希碰撞由 case 宏生成的分支跳过比较），不等则 default。分支与 default 按上下文生成（尾位置照常）
       (let* ((shift (list-ref d 0)) (mask (list-ref d 1)) (kind (list-ref d 2)) (entries (list-ref d 3))
              (x (new-local! mc obj)) (k (new-local! mc int32-type))
              (def-l (new-label! mc)) (end-l (new-label! mc))
              (labels (map (lambda (en) (new-label! mc)) entries))
              (hs (map car entries)))
         (cg e0 mc 'value)
         (emit! mc `(stloc ,x))
         (if (= kind 0)
             (emit! mc `(ldloc ,x) `(call ,clj-util-type "IsNonCharNumeric" (,obj) ,bool-type) `(brfalse ,def-l)
                    `(ldloc ,x) `(call ,clj-util-type "ConvertToInt" (,obj) ,int32-type))
             (emit! mc `(ldloc ,x) `(call ,clj-util-type "hash" (,obj) ,int32-type)))
         (unless (= mask 0) (emit! mc `(ldc.i4 ,shift) '(shr) `(ldc.i4 ,mask) '(and)))
         (emit! mc `(stloc ,k))
         (unless (null? hs)
           (let ((lo (apply min hs)) (hi (apply max hs)))
             (if (<= (- hi lo) (+ 8 (* 2 (length hs))))
                 (emit! mc `(ldloc ,k) `(ldc.i4 ,lo) '(sub)
                        `(switch ,(let loop ((h lo))
                                    (if (> h hi)
                                        '()
                                        (cons (let ((i (find-index (lambda (v) (= v h)) hs))) (if i (list-ref labels i) def-l))
                                              (loop (+ h 1)))))))
                 (for-each (lambda (h l) (emit! mc `(ldloc ,k) `(ldc.i4 ,h) `(beq ,l))) hs labels))))
         (emit! mc `(br ,def-l))
         (for-each
           (lambda (en l then)
             (emit! mc `(label ,l))
             (unless (caddr en)
               (emit! mc `(ldloc ,x))
               (load-constant! mc (cadr en))
               (emit! mc (if (= kind 2) '(ceq) `(call ,clj-util-type "equiv" (,obj ,obj) ,bool-type)) `(brfalse ,def-l)))
             (cg then mc ctx)
             (unless (eq? ctx 'tail) (emit! mc `(br ,end-l))))
           entries labels e*)
         (emit! mc `(label ,def-l))
         (cg e1 mc ctx)
         (unless (eq? ctx 'tail) (emit! mc `(label ,end-l))))]
      [(clj-deftype ,d (,x* ...) ,le* ...)
       ;; deftype*：值为生成的类型（运行时登记，classForName 取最新的）；reify*：值为新实例（自由变量与 nil 元数据作构造参数）
       (let ((c (if (deftype-interface? d) (interface-class! mc d) (deftype-class! mc d x* le*))))
         (if (and (deftype-info-reify? d) (not (deftype-interface? d)))
             (begin
               (for-each (lambda (v) (load-var! mc v)) (cdr c))
               (emit! mc '(ldnull) `(newobj ,(car c) ,(append (objs (length (cdr c))) (list obj)))))
             (emit! mc `(ldtoken ,(car c)) `(call ,type-type "GetTypeFromHandle" (,type-handle-type) ,type-type)
                    `(call ,clojure-ops-type "DefineType" (,obj) ,obj)))
         (finish-value! mc ctx))]
      [(box ,e)
       (cg e mc 'value)
       (emit! mc `(newobj ,box-type (,obj)))
       (finish-value! mc ctx)]
      [(unbox ,x)
       (unless (eq? ctx 'effect)
         (load-var! mc x)
         (emit! mc `(castclass ,box-type) `(ldfld ,box-type "Value"))
         (finish-value! mc ctx))]
      [(set-box! ,x ,e)
       (operands! mc (list (lambda () (load-var! mc x) (emit! mc `(castclass ,box-type)))
                           (lambda () (cg e mc 'value))))
       (emit! mc `(stfld ,box-type "Value"))
       (finish-none! mc ctx)]))

  ;; ———————————————— 调用 ————————————————

  (define (invoke-method n)
    (if (<= n 4)
        `(callvirt ,proc-type ,(string-append "Invoke" (number->string n)) ,(objs n) ,obj)
        `(callvirt ,proc-type "InvokeN" (,obj-array) ,obj)))

  (define (self-call? e0 mc)
    (nanopass-case (L3 Expr) e0
      [,x (let ((loc (hashtable-ref (mctx-vars mc) x #f))) (and loc (eq? (car loc) 'self)))]
      [(mref ,x) (eq? x (mctx-self-mref mc))]
      [else #f]))

  ;; 已知过程：按参数个数取第一个匹配的子句；是固定参数子句时返回其下标
  ;; 位置符号 s 是本单元某个库的导出、且绑定的是已知过程时（iron-034e），(gref s) 就是那个过程：
  ;; 已知过程不被再赋值，预链接在单元入口一开始就写好了位置与字段
  (define (known-of e0 mc)
    (nanopass-case (L3 Expr) e0
      [(mref ,x) (hashtable-ref (unit-known (mctx-unit mc)) x #f)]
      [(gref ,s) (known-loc (mctx-unit mc) s)]
      [else #f]))

  (define (known-target e0 mc argc)
    (let ((k (known-of e0 mc)))
      (and k
           (let loop ((cls (known-clauses k)) (i 0))
             (cond
               ((null? cls) #f)
               ((cdar cls) #f)   ; 先匹配到剩余参数子句：走一般调用
               ((= (caar cls) argc) (cons k i))
               (else (loop (cdr cls) (+ i 1))))))))

  ;; ———————————————— 通用算术与比较在调用处内联（iron-034f，D8）————————————————
  ;;
  ;; 用户代码里的 + = fx+ fl< … 是 boot 导出的全局位置（rewrite 把 (primitive +) 换成了 g$+$N$%b），
  ;; 不是原语。按位置符号识别：去掉 gensym 后缀的名字在表里，且后缀是 $%b（boot 镜像规范改名后的后缀，
  ;; 05 §7；用户库自己定义的 + 的位置带进程戳，不会被误认）。两个参数时生成"都是 fixnum（或 flonum）→ 直接算，
  ;; 否则调原过程"，原过程负责其余类型与报错。
  (define inline-ops
    '(("+" add . generic) ("-" sub . generic) ("*" mul . generic)
      ("=" eq . generic) ("<" lt . generic) ("<=" le . generic) (">" gt . generic) (">=" ge . generic)
      ("fx+" add . fx) ("fx-" sub . fx) ("fx*" mul . fx)
      ("fx=?" eq . fx) ("fx<?" lt . fx) ("fx<=?" le . fx) ("fx>?" gt . fx) ("fx>=?" ge . fx)
      ("fl+" add . fl) ("fl-" sub . fl) ("fl*" mul . fl) ("fl/" div . fl)
      ("fl=?" eq . fl) ("fl<?" lt . fl) ("fl<=?" le . fl) ("fl>?" gt . fl) ("fl>=?" ge . fl)))

  (define (boot-location? s)
    (let* ((str (symbol->string s)) (n (string-length str)))
      (and (> n 3) (string=? (substring str (- n 3) n) "$%b"))))

  ;; (op . kind) 或 #f
  (define (top-level-mc? mc) (or (mctx-big? mc) (string=? (mctx-class mc) (unit-module (mctx-unit mc)))))

  ;; 过程体是否很大（R6RS 测试库把上千个测试展开在一个过程里）：这种过程往往只执行一次，
  ;; 内联让 IL 更大、发射与 JIT 更慢，换不回时间
  (define big-body-nodes 2000)
  (define (bigger-than? e n) (count-nodes e n))

  ;; (op . kind) 或 #f。按位置符号缓存：每个调用都要问，名字的字符串处理不能每次做
  (define inline-op-cache (make-eq-hashtable))
  (define (inline-op e0 argc)
    (and (= argc 2)
         (nanopass-case (L3 Expr) e0
           [(gref ,s)
            (let ((r (hashtable-ref inline-op-cache s 'unknown)))
              (if (eq? r 'unknown)
                  (let ((r (and (boot-location? s)
                                (let ((r (assoc (pretty s) inline-ops))) (and r (cdr r))))))
                    (hashtable-set! inline-op-cache s r)
                    r)
                  r))]
           [else #f])))

  (define (flonum-constant e)
    (nanopass-case (L3 Expr) e
      [(quote ,d) (and (flonum? d) (not (nan? d)) (not (infinite? d)) d)]
      [else #f]))

  (define (cg-inline-op op kind e0 e* mc ctx)
    (let* ((fl? (eq? kind 'fl))
           (clr-type (if fl? double-type int32-type))
           (const (lambda (e) (if fl? (flonum-constant e) (fixnum-constant e))))
           (slow (new-label! mc))
           (end (new-label! mc))
           (raw-t (if fl? 'fl 'fx))
           ;; 操作数从左到右求值：常量留在原处；已知类型的（iron-034c）存入原始值的临时变量，不再检查；其余存入 object 临时变量
           (slots (map (lambda (e)
                         (cond
                           ((const e) (cons 'const e))
                           ((eq? (expr-type e var-types) raw-t)
                            (cg-raw e mc raw-t)
                            (let ((t (new-local! mc clr-type))) (emit! mc `(stloc ,t)) (cons 'raw t)))
                           (else
                            (cg e mc 'value)
                            (let ((t (new-local! mc obj))) (emit! mc `(stloc ,t)) (cons 'local t)))))
                       e*)))
      (define (load-boxed s)
        (case (car s)
          ((local) (emit! mc `(ldloc ,(cdr s))))
          ((raw) (emit! mc `(ldloc ,(cdr s))) (box-raw! mc raw-t))
          (else (cg (cdr s) mc 'value))))
      (define (load-raw s)
        (case (car s)
          ((local) (emit! mc `(ldloc ,(cdr s)) `(unbox.any ,clr-type)))
          ((raw) (emit! mc `(ldloc ,(cdr s))))
          (else (let ((k (const (cdr s)))) (emit! mc (if fl? `(ldc.r8 ,k) `(ldc.i4 ,k)))))))
      ;; 类型检查
      (for-each (lambda (s)
                  (when (eq? (car s) 'local)
                    (emit! mc `(ldloc ,(cdr s)) `(isinst ,clr-type) `(brfalse ,slow))))
                slots)
      (for-each load-raw slots)
      (case op
        ((eq lt le gt ge)
         (emit-compare! mc (if fl?
                               (case op ((eq) '$fl=?) ((lt) '$fl<?) ((le) '$fl<=?) ((gt) '$fl>?) (else '$fl>=?))
                               (case op ((eq) '$fx=?) ((lt) '$fx<?) ((le) '$fx<=?) ((gt) '$fx>?) (else '$fx>=?)))
                        fl?)
         (finish-bool! mc ctx))
        (else
         (cond
           (fl?
            (emit! mc (case op ((add) '(add)) ((sub) '(sub)) ((mul) '(mul)) (else '(div))) `(box ,double-type))
            (finish-value! mc ctx))
           ((eq? kind 'generic)
            (emit! mc `(call ,arith-type ,(case op ((add) "Add") ((sub) "Subtract") (else "Multiply")) (,int32-type ,int32-type) ,obj))
            (finish-value! mc ctx))
           (else
            ;; fx+ fx- fx*：溢出时助手返回 null，转去慢路径由原过程报错
            (let ((r (new-local! mc obj)))
              (emit! mc `(call ,arith-type ,(case op ((add) "FxAdd") ((sub) "FxSubtract") (else "FxMultiply")) (,int32-type ,int32-type) ,obj)
                     `(stloc ,r) `(ldloc ,r) `(brfalse ,slow) `(ldloc ,r))
              (finish-value! mc ctx))))))
      (unless (eq? ctx 'tail) (emit! mc `(br ,end)))
      ;; 慢路径：与一般调用相同（经值槽、callvirt Invoke2），错误信息与 who 由原过程给出
      (emit! mc `(label ,slow))
      (cg e0 mc 'value)
      (emit! mc `(call ,calls-type "Proc" (,obj) ,proc-type))
      (for-each load-boxed slots)
      (when (tail-ok? mc ctx) (emit! mc '(tail)))
      (emit! mc (invoke-method 2))
      (finish-value! mc ctx)
      (unless (eq? ctx 'tail) (emit! mc `(label ,end)))))

  (define (cg-call e0 e* mc ctx)
    (let ((argc (length e*)))
      (cond
        ;; 顶层初始化代码（Init 方法）只执行一次，内联的代码量换不回时间（发射与 JIT 更贵），只在过程体里内联
        ((and opt-inline? (not (top-level-mc? mc)) (inline-op e0 argc))
         => (lambda (op.kind) (cg-inline-op (car op.kind) (cdr op.kind) e0 e* mc ctx)))
        ;; 自尾调用 → 循环
        ((and (tail-ok? mc ctx) (mctx-loop-label mc) (eqv? (mctx-loop-arity mc) argc) (self-call? e0 mc))
         (operands! mc (map (lambda (x) (lambda () (cg x mc 'value))) e*))
         (let loop ((i (- argc 1)))
           (when (>= i 0)
             (emit! mc `(starg ,(+ i (mctx-arg-base mc))))
             (loop (- i 1))))
         (emit! mc `(br ,(mctx-loop-label mc))))
        ;; 已知过程 → 直接调用静态子句方法
        ((known-target e0 mc argc)
         => (lambda (t)
              (operands! mc (map (lambda (x) (lambda () (cg x mc 'value))) e*))
              (when (tail-ok? mc ctx) (emit! mc '(tail)))
              (emit! mc `(call ,(known-owner (car t)) ,(string-append (known-prefix (car t)) "C" (number->string (cdr t))) ,(objs argc) ,obj))
              (finish-value! mc ctx)))
        (else
         (operands! mc (cons (lambda () (cg e0 mc 'value) (emit! mc `(call ,calls-type "Proc" (,obj) ,proc-type)))
                             (map (lambda (x) (lambda () (cg x mc 'value))) e*)))
         (when (> argc 4) (pack-array! mc argc))
         (when (tail-ok? mc ctx) (emit! mc '(tail)))
         (emit! mc (invoke-method argc))
         (finish-value! mc ctx)))))

  ;; ———————————————— 原语（03 §6）————————————————

  (define (cg-primcall pr e* mc ctx)
    (let* ((p (primitive-info pr))
           (argc (length e*))
           (o (and p (primitive-overload p argc))))
      (define (args!) (operands! mc (map (lambda (x) (lambda () (cg x mc 'value))) e*)))
      ;; 拆箱的操作数；fixnum 常量直接 ldc.i4，不装箱再拆箱
      (define (unbox-args! type)
        (operands! mc (map (lambda (x)
                             (lambda () (cg-raw x mc (if (string=? type int32-type) 'fx 'fl))))
                           e*)))
      (cond
        ((not o)
         ;; 没有参数个数相符的重载：经值槽调用，运行时报参数个数错误
         (cg-call-prim-value pr e* mc ctx))
        ;; 尾位置的 apply：展开参数后 tail. callvirt InvokeN，保证真尾调用
        ((and (eq? pr 'apply) (>= argc 2) (tail-ok? mc ctx))
         (operands! mc (cons (lambda () (cg (car e*) mc 'value) (emit! mc `(call ,calls-type "Proc" (,obj) ,proc-type)))
                             (map (lambda (x) (lambda () (cg x mc 'value))) (cdr e*))))
         (pack-array! mc (- argc 1))
         (emit! mc `(call ,calls-type "ApplyArgs" (,obj-array) ,obj-array)
                '(tail) `(callvirt ,proc-type "InvokeN" (,obj-array) ,obj) '(ret)))
        (else
         (case (overload-inline o)
           ((void) (args!) (for-each (lambda (x) (emit! mc '(pop))) e*) (finish-none! mc ctx))
           (($car $cdr)
            (args!)
            (emit! mc `(castclass ,cons-type) `(ldfld ,cons-type ,(if (eq? pr '$car) "car" "cdr")))
            (finish-value! mc ctx))
           ((cons)
            (args!)
            (emit! mc `(newobj ,cons-type (,obj ,obj)))
            (finish-value! mc ctx))
           ((eq?) (args!) (emit! mc '(ceq)) (finish-bool! mc ctx))
           ((not)
            (args!)
            (emit! mc `(call ,rt-type "IsTrue" (,obj) ,bool-type) '(ldc.i4 0) '(ceq))
            (finish-bool! mc ctx))
           ((null?) (args!) (emit! mc '(ldnull) '(ceq)) (finish-bool! mc ctx))
           ((pair?) (args!) (emit! mc `(isinst ,cons-type) '(ldnull) '(cgt.un)) (finish-bool! mc ctx))
           (($fx+ $fx* $fxand $fxior $fxxor)
            (unbox-args! int32-type)
            (emit! mc (case pr (($fx+) '(add)) (($fx*) '(mul)) (($fxand) '(and)) (($fxior) '(or)) (else '(xor))))
            (emit! mc `(call ,rt-type "Box" (,int32-type) ,obj))
            (finish-value! mc ctx))
           (($fx-)
            (unbox-args! int32-type)
            (emit! mc (if (= argc 1) '(neg) '(sub)) `(call ,rt-type "Box" (,int32-type) ,obj))
            (finish-value! mc ctx))
           (($fxnot)
            (unbox-args! int32-type)
            (emit! mc '(not) `(call ,rt-type "Box" (,int32-type) ,obj))
            (finish-value! mc ctx))
           (($fx=? $fx<? $fx>? $fx<=? $fx>=?)
            (unbox-args! int32-type)
            (emit-compare! mc pr #f)
            (finish-bool! mc ctx))
           (($fxzero? $fxnegative? $fxpositive?)
            (unbox-args! int32-type)
            (emit! mc '(ldc.i4 0))
            (emit! mc (case pr (($fxzero?) '(ceq)) (($fxnegative?) '(clt)) (else '(cgt))))
            (finish-bool! mc ctx))
           (($fl+ $fl* $fl/)
            (unbox-args! double-type)
            (emit! mc (case pr (($fl+) '(add)) (($fl*) '(mul)) (else '(div))) `(box ,double-type))
            (finish-value! mc ctx))
           (($fl-)
            (unbox-args! double-type)
            (emit! mc (if (= argc 1) '(neg) '(sub)) `(box ,double-type))
            (finish-value! mc ctx))
           (($fl=? $fl<? $fl>? $fl<=? $fl>=?)
            (unbox-args! double-type)
            (emit-compare! mc pr #t)
            (finish-bool! mc ctx))
           (else (cg-static-call! o e* mc ctx)))))))

  ;; 比较：栈上两个同类型数 → CLR bool。浮点的 <= / >= 对 NaN 为假
  (define (emit-compare! mc pr float?)
    (case pr
      (($fx=? $fl=?) (emit! mc '(ceq)))
      (($fx<? $fl<?) (emit! mc '(clt)))
      (($fx>? $fl>?) (emit! mc '(cgt)))
      (($fx<=? $fl<=?) (emit! mc (if float? '(cgt.un) '(cgt)) '(ldc.i4 0) '(ceq)))
      (($fx>=? $fl>=?) (emit! mc (if float? '(clt.un) '(clt)) '(ldc.i4 0) '(ceq)))))

  (define (fixnum-constant e)
    (nanopass-case (L3 Expr) e
      [(quote ,d) (and (fixnum? d) (exact? d) d)]
      [else #f]))

  (define (cg-static-call! o e* mc ctx)
    (let* ((argc (length e*))
           (fixed (overload-fixed o))
           (rest? (overload-rest? o)))
      (operands! mc (map (lambda (x) (lambda () (cg x mc 'value))) e*))
      (when rest? (pack-array! mc (- argc fixed)))
      (let ((tail? (and (tail-ok? mc ctx) (overload-allow-tail-call? o))))
        (when tail? (emit! mc '(tail)))
        (emit! mc `(call ,(overload-type o) ,(overload-method o)
                         ,(append (objs fixed) (if rest? (list obj-array) '()))
                         ,obj))
        (if tail? (emit! mc '(ret)) (finish-value! mc ctx)))))

  (define (cg-call-prim-value pr e* mc ctx)
    (operands! mc (cons (lambda ()
                          (load-symbol! mc pr)
                          (emit! mc `(call ,calls-type "Global" (,obj) ,obj) `(call ,calls-type "Proc" (,obj) ,proc-type)))
                        (map (lambda (x) (lambda () (cg x mc 'value))) e*)))
    (when (> (length e*) 4) (pack-array! mc (length e*)))
    (when (tail-ok? mc ctx) (emit! mc '(tail)))
    (emit! mc (invoke-method (length e*)))
    (finish-value! mc ctx))

  ;; ———————————————— lambda → 类 ————————————————

  ;; 返回 (static? 类名)。self：letrec 中绑定此 lambda 的变量（体内对它的引用即 this），或 #f；
  ;; self-mref：已知过程的库变量，或 #f
  (define (lambda-class! le mc self self-mref . preset)
    (nanopass-case (L3 LambdaExpr) le
      [(lambda ,nm (,x1* ...) ,cl* ...) (procedure-class! mc nm x1* cl* self self-mref preset)]))

  ;; preset：已知过程的 known 记录（类名 / 持有类与前缀已在 collect-known! 中确定）
  (define (procedure-class! mc nm x1* cl* self self-mref preset)
    (let ((fields (if self (remq self x1*) x1*)))
      (if (clj-fn-name? nm)
          (clj-fn-class! mc nm x1* cl* self)
      (if (and opt-shared? (null? fields))
          (shared-procedure! mc nm cl* self self-mref (and (pair? preset) (car preset)))
          (procedure-class1! mc nm x1* cl* self self-mref preset)))))

  ;; 034d：共享持有类中的过程。返回 (#t 持有类 字段)
  (define (shared-procedure! mc nm cl* self self-mref k)
    (let* ((u (mctx-unit mc))
           (slot (if k (cons (known-owner k) (known-prefix k)) (shared-slot! u)))
           (holder (car slot))
           (prefix (cdr slot))
           (field (substring prefix 0 (- (string-length prefix) 1)))
           (entry (holder-entry u holder))
           (clauses (map clause-shape cl*))
           (self-loc (cons* 'self holder field))
           (methods '()))
      (define (add-method! m) (set! methods (cons m methods)))
      (let loop ((cl* cl*) (i 0))
        (unless (null? cl*)
          (add-method! (clause-method u holder #t (car cl*) i '() '() self self-mref prefix self-loc))
          (loop (cdr cl*) (+ i 1))))
      (let ((need-n (exists (lambda (c) (or (cdr c) (> (car c) 4))) clauses)))
        (when need-n (add-method! (shared-invoke-n-method holder prefix field clauses)))
        (vector-set! entry 0 (cons `(field ,field ,proc-type static) (vector-ref entry 0)))
        (vector-set! entry 1 (append methods (vector-ref entry 1)))
        (vector-set! entry 2
          (cons
            `(,@(if nm `((ldstr ,(if (symbol? nm) (symbol->string nm) nm))) '((ldnull)))
              ,@(arity-code u clauses)
              ,@(if (null? cl*) '((ldnull)) `((ldconst ,(constant-index! u (map (lambda (cl) (clause-form nm cl)) cl*)))))
              ,@(let k-loop ((n 0))
                  (if (> n 4)
                      '()
                      (append
                        (let ((t (first-match clauses n)))
                          (if (and t (not (cdr (list-ref clauses t))))
                              `((ldnull) (ldftn ,holder ,(string-append prefix "C" (number->string t)) ,(objs n) ,obj)
                                (newobj ,(func-type n) (,obj "System.IntPtr")))
                              '((ldnull))))
                        (k-loop (+ n 1)))))
              ,@(if need-n
                    `((ldnull) (ldftn ,holder ,(string-append prefix "N") (,obj-array) ,obj)
                      (newobj ,func-n-type (,obj "System.IntPtr")))
                    '((ldnull)))
              (newobj ,(sp-class u)
                      (,string-type ,obj ,obj ,(func-type 0) ,(func-type 1) ,(func-type 2) ,(func-type 3) ,(func-type 4) ,func-n-type))
              (stsfld ,holder ,field))
            (vector-ref entry 2))))
      (list #t holder field)))

  (define (func-type n)
    (case n
      ((0) "System.Func`1[System.Object]")
      ((1) "System.Func`2[System.Object,System.Object]")
      ((2) "System.Func`3[System.Object,System.Object,System.Object]")
      ((3) "System.Func`4[System.Object,System.Object,System.Object,System.Object]")
      (else "System.Func`5[System.Object,System.Object,System.Object,System.Object,System.Object]")))
  (define func-n-type "System.Func`2[System.Object[],System.Object]")
  (define static-proc-type "Cleat.Scheme.StaticProcedure")

  ;; procedure-arity 的值（与 get_Arity 相同）
  (define (arity-code u clauses)
    (let ((arity (let ((as (map (lambda (c) (if (cdr c) (inexact (car c)) (car c))) clauses)))
                   (cond ((null? as) '()) ((null? (cdr as)) (car as)) (else as)))))
      (cond
        ((null? arity) '((ldnull)))
        ((fixnum? arity) `((ldc.i4 ,arity) (call ,rt-type "Box" (,int32-type) ,obj)))
        (else `((ldconst ,(constant-index! u arity)))))))

  ;; 共享持有类中的通用入口（静态方法，参数为实参数组）：按子句顺序分派，没有匹配时由过程对象报参数个数错误
  (define (shared-invoke-n-method holder prefix field clauses)
    (let loop ((cs clauses) (i 0) (code '()))
      (if (null? cs)
          `(method ,(string-append prefix "N") (attrs static) (params ,obj-array) (ret ,obj)
             (body ,@(reverse code)
                   (ldsfld ,holder ,field) (ldarg 0)
                   (call ,static-proc-type "NoMatch" (,obj ,obj-array) ,obj)
                   (ret)))
          (let* ((n (caar cs)) (rest? (cdar cs))
                 (next (string->symbol (string-append "N" (number->string i))))
                 (load-args (let a-loop ((j 0)) (if (= j n) '() (cons* '(ldarg 0) `(ldc.i4 ,j) '(ldelem.ref) (a-loop (+ j 1))))))
                 (block
                  `((ldarg 0) (ldlen) (conv.i4) (ldc.i4 ,n) ,(if rest? `(blt ,next) `(bne.un ,next))
                    ,@load-args
                    ,@(if rest? `((ldarg 0) (ldc.i4 ,n) (call ,static-proc-type "Rest" (,obj-array ,int32-type) ,obj)) '())
                    (call ,holder ,(string-append prefix "C" (number->string i)) ,(objs (+ n (if rest? 1 0))) ,obj)
                    (ret)
                    (label ,next))))
            (loop (cdr cs) (+ i 1) (append (reverse block) code))))))

  (define (procedure-class1! mc nm x1* cl* self self-mref preset)
       (let* ((u (mctx-unit mc))
              (fields (if self (remq self x1*) x1*))
              (static? (null? fields))
              (name (if (pair? preset) (known-class (car preset)) (class-name u nm)))
              (clauses (map clause-shape cl*))
              (variadic? (exists cdr clauses))
              (base (if variadic? variadic-type proc-type))
              (field-names (let loop ((fs fields) (i 0))
                             (if (null? fs) '() (cons (string-append "c" (number->string i)) (loop (cdr fs) (+ i 1))))))
              (methods '()))
         (define (add-method! m) (set! methods (cons m methods)))
         ;; 子句方法 C0、C1 …
         (let loop ((cl* cl*) (i 0))
           (unless (null? cl*)
             (add-method! (clause-method u name static? (car cl*) i fields field-names self self-mref))
             (loop (cdr cl*) (+ i 1))))
         ;; Invoke0..4：转发到第一个匹配的固定参数子句
         (let k-loop ((k 0))
           (when (<= k 4)
             (let ((t (first-match clauses k)))
               (when (and t (not (cdr (list-ref clauses t))))
                 (add-method!
                   `(method ,(string-append "Invoke" (number->string k)) (attrs override) (params ,@(objs k)) (ret ,obj)
                      (body ,@(if static? '() '((ldarg 0)))
                            ,@(let a-loop ((j 1)) (if (> j k) '() (cons `(ldarg ,j) (a-loop (+ j 1)))))
                            (tail)
                            (call ,name ,(string-append "C" (number->string t)) ,(objs (+ k (if static? 0 0))) ,obj)
                            (ret))))))
             (k-loop (+ k 1))))
         ;; InvokeN：有剩余参数或参数多于 4 个的子句时，按子句顺序分派
         (when (exists (lambda (c) (or (cdr c) (> (car c) 4))) clauses)
           (add-method! (invoke-n-method name static? clauses)))
         ;; procedure-arity（与旧系统一致）：固定参数子句为 n，有剩余参数的为 n.0；多个子句为列表，没有子句为 '()
         (add-method!
           (let ((arity (let ((as (map (lambda (c) (if (cdr c) (inexact (car c)) (car c))) clauses)))
                          (cond ((null? as) '()) ((null? (cdr as)) (car as)) (else as)))))
             `(method "get_Arity" (attrs override) (params) (ret ,obj)
                (body ,@(cond
                          ((null? arity) '((ldnull)))
                          ((fixnum? arity) `((ldc.i4 ,arity) (call ,rt-type "Box" (,int32-type) ,obj)))
                          (else `((ldconst ,(constant-index! u arity)))))
                      (ret)))))
         ;; procedure-form：各子句的 (名字 . 形参)，形参取源码中的名字（REPL 的参数提示使用，iron-038）
         (unless (null? cl*)
           (add-method!
             `(method "get_Form" (attrs override) (params) (ret ,obj)
                (body (ldconst ,(constant-index! u (map (lambda (cl) (clause-form nm cl)) cl*)))
                      (ret)))))
         (when nm
           (add-method! `(method "get_Name" (attrs override) (params) (ret ,string-type)
                           (body (ldstr ,(if (symbol? nm) (symbol->string nm) nm)) (ret)))))
         (add-method! `(method ".ctor" (attrs ctor) (params)
                         (body (ldarg 0) (call ,base ".ctor" () ,void-type) (ret))))
         (when static?
           (add-method! `(method ".cctor" (attrs cctor) (params)
                           (body (newobj ,name ()) (stsfld ,name "Instance") (ret)))))
         (add-class! u
           `(type ,name (base ,base) (attrs sealed)
              ,@(if static? `((field "Instance" ,name static)) (map (lambda (f) `(field ,f ,obj)) field-names))
              ,@(reverse methods)))
         (list static? name "Instance")))

;; ———————————————— Clojure 的 fn* → AFunction / RestFn 子类（cleat-051）————————————————
  ;;
  ;; 与 Procedure 子类同构：自由变量为实例字段 c0 …，没有自由变量的是单例（静态字段 Instance，.cctor 创建），
  ;; letrec 的自引用为 this。固定参数子句 = invoke(k) 的覆盖；变长子句（最后一个，形参为"必需参数 + 剩余 seq"）
  ;; = RestFn.doInvoke 的覆盖 + getRequiredArity（RestFn 负责打包剩余参数与 applyTo）；HasArity 按子句。
  ;; 类名是 Clojure 的 ns$name（AFn.WrongArityException 用类型全名，ArityException 的文本因此与 Clojure 相同），单元内重名加 __N

  (define (unique-type-name! u name)
    (let ((names (unit-type-names u)))
      (let loop ((k 1))
        (let ((n (if (= k 1) name (string-append name "__" (number->string k)))))
          (if (hashtable-ref names n #f)
              (loop (+ k 1))
              (begin (hashtable-set! names n #t) n))))))

  (define (clj-fn-class! mc nm x1* cl* self)
    (let* ((u (mctx-unit mc))
           (fields (if self (remq self x1*) x1*))
           (static? (null? fields))
           (name (unique-type-name! u (clj-fn-name-class nm)))
           (variadic (clj-fn-name-variadic nm))
           (n (length cl*))
           (base (if variadic restfn-type afunction-type))
           (field-names (let loop ((fs fields) (i 0))
                          (if (null? fs) '() (cons (string-append "c" (number->string i)) (loop (cdr fs) (+ i 1))))))
           (arities (let loop ((cl* cl*) (i 0))
                      (if (null? cl*) '() (cons (car (clause-shape (car cl*))) (loop (cdr cl*) (+ i 1))))))
           (fixed (if variadic (list-head arities (- n 1)) arities))
           (methods '()))
      (define (add-method! m) (set! methods (cons m methods)))
      (let loop ((cls cl*) (i 0))
        (unless (null? cls)
          (add-method! (clj-clause-method u name (car cls) fields field-names self
                                          (if (and variadic (= i (- n 1))) "doInvoke" "invoke")))
          (loop (cdr cls) (+ i 1))))
      (when variadic
        (add-method! `(method "getRequiredArity" (attrs override) (params) (ret ,int32-type)
                        (body (ldc.i4 ,variadic) (ret)))))
      (add-method!
        `(method "HasArity" (attrs override) (params ,int32-type) (ret ,bool-type)
           (body ,@(apply append (map (lambda (k) `((ldarg 1) (ldc.i4 ,k) (beq YES))) fixed))
                 ,@(if variadic `((ldarg 1) (ldc.i4 ,variadic) (bge YES)) '())
                 (ldc.i4 0) (ret) (label YES) (ldc.i4 1) (ret))))
      (add-method! `(method ".ctor" (attrs ctor) (params)
                      (body (ldarg 0) (call ,base ".ctor" () ,void-type) (ret))))
      (when static?
        (add-method! `(method ".cctor" (attrs cctor) (params)
                        (body (newobj ,name ()) (stsfld ,name "Instance") (ret)))))
      (add-class! u
        `(type ,name (base ,base) (attrs sealed)
           ,@(if static? `((field "Instance" ,name static)) (map (lambda (f) `(field ,f ,obj)) field-names))
           ,@(reverse methods)))
      (list static? name "Instance")))

  (define (find-index pred ls)
    (let loop ((ls ls) (i 0)) (cond ((null? ls) #f) ((pred (car ls)) i) (else (loop (cdr ls) (+ i 1))))))

  (define (list-head ls n) (if (= n 0) '() (cons (car ls) (list-head (cdr ls) (- n 1)))))

  ;; Clojure 函数类的一个子句：实例方法 mname（invoke / doInvoke 的覆盖），形参都是 object
  (define (clj-clause-method u class cl fields field-names self mname)
    (nanopass-case (L3 Clause) cl
      [(clause (,x* ...) ,x ,body)
       (when x (error 'codegen "Clojure fn clauses have fixed parameters" x))
       (let ((mc (make-method-ctx u class #f #f #f #f)))
         (let loop ((ps x*) (j 1))
           (unless (null? ps)
             (hashtable-set! (mctx-vars mc) (car ps) (cons 'arg j))
             (loop (cdr ps) (+ j 1))))
         (for-each (lambda (f n) (hashtable-set! (mctx-vars mc) f (cons 'field n))) fields field-names)
         (when self (hashtable-set! (mctx-vars mc) self '(self)))
         (mctx-big?-set! mc (bigger-than? body big-body-nodes))
         (cg body mc 'tail)
         (finish-method mc mname '(override) (objs (length x*))))]))

  ;; ———————————————— deftype* / reify* → CLR 类型（cleat-051）————————————————
  ;;
  ;; 描述 d（由 (clojure syntax) 在展开期按 C# 展开器的规则算好，编译器不反射）：
  ;;   (类名 reify? 字段 接口 方法 桩 basis)
  ;;   字段：((名字 可变? volatile?) …)，与 x* 对齐；接口：类型名（含继承的接口）；
  ;;   方法：((接口|#f 名字 返回类型 (参数类型 …)) …)，与 le* 对齐；类型写作 ("名字" . 值类型?)；
  ;;   桩：接口中没有被方法实现的方法 ((接口 名字 返回类型 (参数类型 …) 目标方法下标|#f) …)：
  ;;       有同名同参的方法时转调它（返回值按需转换），否则抛 NotImplementedException（CLR 要求实现全部接口方法）；
  ;;   basis：deftype 的 getBasis 返回的向量（常量），reify 为 #f。
  ;; 字段（与 reify 的自由变量）都是 object；方法的原始类型参数在入口按 Clojure 的规则装箱（int → long），
  ;; 返回值按声明类型转换（Reflector.Convert）。reify 另有 __meta 字段与 IObj 的 meta / withMeta。
  (define (deftype-info-class d) (list-ref d 0))
  (define (deftype-info-reify? d) (list-ref d 1))
  (define (deftype-info-fields d) (list-ref d 2))
  (define (deftype-info-ifaces d) (list-ref d 3))
  (define (deftype-info-methods d) (list-ref d 4))
  (define (deftype-info-stubs d) (list-ref d 5))
  (define (deftype-info-basis d) (list-ref d 6))
  ;; defrecord（cleat-053）：基本字段的名字（create 的关键字）与末尾特殊字段（__meta __extmap __hash __hasheq）的个数
  (define (deftype-info-record-keys d) (if (> (length d) 7) (list-ref d 7) '()))
  (define (deftype-info-drops d) (if (> (length d) 8) (list-ref d 8) 0))

  ;; 记录的另外两个构造函数与静态 create（ClojureCLR NewInstanceExpr 的 AltCtorDrops / EmitStatics）：
  ;; (基本字段 …) → 全部字段（__meta、__extmap 为 null，__hash、__hasheq 为 0）；(除 __hash __hasheq 外的字段) → 全部字段；
  ;; create(IPersistentMap m)：按关键字取出基本字段并从 m 去掉，其余作 __extmap（RT.seqOrElse）
  (define (record-members name d)
    (let* ((n (length (deftype-info-fields d)))
           (drops (deftype-info-drops d))
           (keys (deftype-info-record-keys d))
           (nb (- n drops))
           (zero '((ldc.i4 0) (box "System.Int32"))))
      (define (alt-ctor k tail)
        `(method ".ctor" (attrs ctor) (params ,@(objs k))
           (body (ldarg 0)
                 ,@(let loop ((i 1)) (if (> i k) '() (cons `(ldarg ,i) (loop (+ i 1)))))
                 ,@tail
                 (call ,name ".ctor" ,(objs n) ,void-type)
                 (ret))))
      (if (or (= drops 0) (null? keys))
          '()
          (append
            (if (and (> nb 0) (= drops 4)) (list (alt-ctor nb `((ldnull) (ldnull) ,@zero ,@zero))) '())
            (if (and (> (- n 2) 0) (= drops 4)) (list (alt-ctor (- n 2) `(,@zero ,@zero))) '())
            (list
              `(method "create" (attrs static) (params "clojure.lang.IPersistentMap") (ret ,name)
                 (locals "clojure.lang.Keyword" ,@(objs nb))
                 (body
                   ,@(apply append
                       (let loop ((ks keys) (i 1))
                         (if (null? ks)
                             '()
                             (cons `((ldarg 0) (ldstr ,(car ks)) (call "clojure.lang.Keyword" "intern" (,string-type) "clojure.lang.Keyword")
                                     (dup) (stloc 0) (ldnull)
                                     (callvirt "clojure.lang.ILookup" "valAt" (,obj ,obj) ,obj) (stloc ,i)
                                     (ldarg 0) (ldloc 0) (callvirt "clojure.lang.IPersistentMap" "without" (,obj) "clojure.lang.IPersistentMap")
                                     (starg 0))
                                   (loop (cdr ks) (+ i 1))))))
                   ,@(let loop ((i 1)) (if (> i nb) '() (cons `(ldloc ,i) (loop (+ i 1)))))
                   (ldnull) (ldarg 0) (call "clojure.lang.RT" "seqOrElse" (,obj) ,obj)
                   ,@zero ,@zero
                   (newobj ,name ,(objs n))
                   (ret))))))))

  ;; gen-interface（cleat-053）：描述的 reify? 位置为 interface；方法规格 (#f 名字 返回类型 (参数类型 …))，接口为父接口
  (define (deftype-interface? d) (eq? (deftype-info-reify? d) 'interface))

  (define (interface-class! mc d)
    (let ((u (mctx-unit mc)) (name (deftype-info-class d)))
      (hashtable-set! (unit-type-names u) name #t)
      (add-class! u
        `(type ,name (attrs interface abstract) (interfaces ,@(deftype-info-ifaces d))
           ,@(map (lambda (sp)
                    `(method ,(cadr sp) (attrs abstract virtual) (params ,@(map tname (cadddr sp))) (ret ,(tname (caddr sp)))))
                  (deftype-info-methods d))))
      (cons name '())))

  (define (tname t) (car t))
  (define (value-type? t) (cdr t))

  ;; 返回 (类名 . 自由变量)
  (define (deftype-class! mc d x* le*)
    (let* ((u (mctx-unit mc))
           (name (deftype-info-class d))
           (reify? (deftype-info-reify? d))
           (fnames (map car (deftype-info-fields d)))
           (caps (fold-left (lambda (acc le)
                              (nanopass-case (L3 LambdaExpr) le
                                [(lambda ,nm (,x1* ...) ,cl* ...)
                                 (fold-left (lambda (a v) (if (or (memq v a) (memq v x*)) a (append a (list v)))) acc x1*)]))
                            '() le*))
           (cnames (let loop ((cs caps) (i 0))
                     (if (null? cs) '() (cons (string-append "c" (number->string i)) (loop (cdr cs) (+ i 1))))))
           (specs (deftype-info-methods d))
           (mnames (let loop ((ss specs) (used '()) (acc '()))
                     (if (null? ss)
                         (reverse acc)
                         (let* ((sp (car ss))
                                (key (cons (cadr sp) (map tname (cadddr sp))))
                                (n (if (or (not (car sp)) (not (member key used))) (cadr sp) (string-append (car sp) "." (cadr sp)))))
                           (loop (cdr ss) (cons key used) (cons n acc))))))
           (ctor-fields (if reify? (append cnames (list "__meta")) fnames)))
      (when (and (not reify?) (pair? caps))
        (syntax-violation 'deftype* "deftype* methods cannot close over locals" (map pretty caps)))
      (hashtable-set! (unit-type-names u) name #t)
      (add-class! u
        `(type ,name (attrs sealed) (interfaces ,@(deftype-info-ifaces d))
           ,@(if reify?
                 (map (lambda (f) `(field ,f ,obj)) ctor-fields)
                 (map (lambda (f) `(field ,(car f) ,obj ,@(if (caddr f) '(volatile) '()))) (deftype-info-fields d)))
           (method ".ctor" (attrs ctor) (params ,@(objs (length ctor-fields)))
             (body (ldarg 0) (call ,obj ".ctor" () ,void-type)
                   ,@(let loop ((fs ctor-fields) (i 1))
                       (if (null? fs) '() (cons* '(ldarg 0) `(ldarg ,i) `(stfld ,name ,(car fs)) (loop (cdr fs) (+ i 1)))))
                   (ret)))
           ,@(map (lambda (le sp mn) (deftype-method u name le sp mn x* fnames caps cnames)) le* specs mnames)
           ,@(map (lambda (st) (deftype-stub name st specs mnames)) (deftype-info-stubs d))
           ,@(if reify?
                 `((method "meta" (params) (ret ,pmap-type) (implements "clojure.lang.IMeta" "meta")
                     (body (ldarg 0) (ldfld ,name "__meta") (castclass ,pmap-type) (ret)))
                   (method "withMeta" (params ,pmap-type) (ret ,iobj-type) (implements "clojure.lang.IObj" "withMeta")
                     (body ,@(apply append (map (lambda (c) `((ldarg 0) (ldfld ,name ,c))) cnames))
                           (ldarg 1) (newobj ,name ,(objs (length ctor-fields))) (ret))))
                 `((method "getBasis" (attrs static) (params) (ret "clojure.lang.IPersistentVector")
                     (body (ldconst ,(constant-index! u (deftype-info-basis d))) (castclass "clojure.lang.IPersistentVector") (ret)))
                   ,@(record-members name d)))))
      (cons name caps)))

  ;; 方法体：this 为 ldarg 0；原始类型的参数在入口装箱进局部变量；字段读写 this 的字段；返回值转换为声明类型
  (define (deftype-method u class le spec mname fvars fnames caps cnames)
    (nanopass-case (L3 LambdaExpr) le
      [(lambda ,nm (,x1* ...) ,cl)
       (nanopass-case (L3 Clause) cl
         [(clause (,x2* ...) ,x ,body)
          (let* ((mc (make-method-ctx u class #f #f #f #f))
                 (iface (car spec)) (ret (caddr spec)) (ptypes (cadddr spec))
                 (entry (with-buffer mc
                          (lambda ()
                            (hashtable-set! (mctx-vars mc) (car x2*) '(self))
                            (let loop ((ps (cdr x2*)) (ts ptypes) (j 1))
                              (unless (null? ps)
                                (if (value-type? (car ts))
                                    (let ((l (new-local! mc obj)))
                                      (emit! mc `(ldarg ,j) `(box ,(tname (car ts)))
                                             `(call ,clojure-ops-type "BoxArg" (,obj) ,obj) `(stloc ,l))
                                      (hashtable-set! (mctx-vars mc) (car ps) (cons 'local l)))
                                    (hashtable-set! (mctx-vars mc) (car ps) (cons 'arg j)))
                                (loop (cdr ps) (cdr ts) (+ j 1))))
                            (for-each (lambda (f n) (hashtable-set! (mctx-vars mc) f (cons 'dfield n))) fvars fnames)
                            (for-each (lambda (c n) (hashtable-set! (mctx-vars mc) c (cons 'field n))) caps cnames)))))
            (emit-code! mc entry)
            (mctx-big?-set! mc (bigger-than? body big-body-nodes))
            (cond
              ((string=? (tname ret) void-type) (cg body mc 'effect) (emit! mc '(ret)))
              ((string=? (tname ret) obj) (cg body mc 'tail))
              (else (cg body mc 'value) (convert-return! mc ret) (emit! mc '(ret))))
            `(method ,mname (attrs ,@(if iface '() '(override))) (params ,@(map tname ptypes)) (ret ,(tname ret))
               (locals ,@(reverse (mctx-locals mc)))
               (,(interop 'namespaces))
               ,@(if iface `((implements ,iface ,(cadr spec))) '())
               (body ,@(reverse (mctx-buffer mc)))))])]))

  ;; 栈上的 object → 声明类型 t（("名字" . 值类型?)）
  (define (convert-return! mc t)
    (cond
      ((string=? (tname t) obj) (void))
      ((value-type? t)
       (emit! mc `(ldtoken ,(tname t)) `(call ,type-type "GetTypeFromHandle" (,type-handle-type) ,type-type)
              `(call ,clojure-ops-type "ConvertReturn" (,obj ,type-type) ,obj) `(unbox.any ,(tname t))))
      (else (emit! mc `(castclass ,(tname t))))))

  ;; 接口中没有直接实现的方法：转调同名同参的方法，或抛 NotImplementedException
  (define (deftype-stub class st specs mnames)
    (let* ((iface (list-ref st 0)) (name (list-ref st 1)) (ret (list-ref st 2)) (ptypes (list-ref st 3)) (target (list-ref st 4))
           (mname (string-append iface "." name)))
      `(method ,mname (attrs private) (params ,@(map tname ptypes)) (ret ,(tname ret))
         (implements ,iface ,name)
         (body
           ,@(if target
                 (let* ((sp (list-ref specs target)) (tret (caddr sp)))
                   `((ldarg 0)
                     ,@(let loop ((i 1) (ts ptypes)) (if (null? ts) '() (cons `(ldarg ,i) (loop (+ i 1) (cdr ts)))))
                     (call ,class ,(list-ref mnames target) ,(map tname (cadddr sp)) ,(tname tret))
                     ,@(cond
                         ((string=? (tname ret) void-type) (if (string=? (tname tret) void-type) '() '((pop))))
                         ((string=? (tname tret) (tname ret)) '())
                         (else
                          (append
                            (cond ((string=? (tname tret) void-type) '((ldnull)))
                                  ((value-type? tret) `((box ,(tname tret))))
                                  (else '()))
                            (cond ((string=? (tname ret) obj) '())
                                  ((value-type? ret)
                                   `((ldtoken ,(tname ret)) (call ,type-type "GetTypeFromHandle" (,type-handle-type) ,type-type)
                                     (call ,clojure-ops-type "ConvertReturn" (,obj ,type-type) ,obj) (unbox.any ,(tname ret))))
                                  (else `((castclass ,(tname ret))))))))
                     (ret)))
                 `((ldstr ,(string-append iface "." name " is not implemented by " class))
                   (newobj "System.NotImplementedException" (,string-type))
                   (throw)))))))

  (define (class-name u nm)
    (string-append (sanitize (unit-module u)) "$" (number->string (next-id! u))
                   (if nm (string-append "$" (sanitize nm)) "")))

  ;; 子句的 (名字 . 形参)：形参是展开器生成的 gensym（g$名字$序号$后缀），还原为源码中的名字
  (define (clause-form nm cl)
    (define (source-name x)
      (let* ((s (symbol->string x)) (n (string-length s)))
        (if (and (> n 2) (char=? (string-ref s 0) #\g) (char=? (string-ref s 1) #\$))
            (let loop ((i (- n 1)) (dollars 0))
              (cond
                ((< i 2) x)
                ((char=? (string-ref s i) #\$)
                 (if (= dollars 1) (string->symbol (substring s 2 i)) (loop (- i 1) (+ dollars 1))))
                (else (loop (- i 1) dollars))))
            x)))
    (nanopass-case (L3 Clause) cl
      [(clause (,x* ...) ,x ,body)
       (cons (if (symbol? nm) nm (if (string? nm) (string->symbol nm) 'lambda))
             (let loop ((xs x*))
               (if (null? xs)
                   (if x (source-name x) '())
                   (cons (source-name (car xs)) (loop (cdr xs))))))]))

  (define (clause-shape cl)
    (nanopass-case (L3 Clause) cl
      [(clause (,x* ...) ,x ,body) (cons (length x*) (and x #t))]))

  (define (first-match clauses k)
    (let loop ((cs clauses) (i 0))
      (cond
        ((null? cs) #f)
        ((if (cdar cs) (>= k (caar cs)) (= k (caar cs))) i)
        (else (loop (cdr cs) (+ i 1))))))

  (define (invoke-n-method name static? clauses)
    (let loop ((cs clauses) (i 0) (code '()))
      (if (null? cs)
          `(method "InvokeN" (attrs override) (params ,obj-array) (ret ,obj)
             (body ,@(reverse code)
                   (ldarg 0) (ldarg 1) (ldlen) (conv.i4)
                   (call ,proc-type "ArityError" (,int32-type) ,obj)
                   (ret)))
          (let* ((n (caar cs)) (rest? (cdar cs))
                 (next (string->symbol (string-append "N" (number->string i))))
                 (load-args (let a-loop ((j 0)) (if (= j n) '() (cons* '(ldarg 1) `(ldc.i4 ,j) '(ldelem.ref) (a-loop (+ j 1))))))
                 (block
                  `((ldarg 1) (ldlen) (conv.i4) (ldc.i4 ,n) ,(if rest? `(blt ,next) `(bne.un ,next))
                    ,@(if static? '() '((ldarg 0)))
                    ,@load-args
                    ,@(if rest? `((ldarg 1) (ldc.i4 ,n) (call ,variadic-type "RestList" (,obj-array ,int32-type) ,obj)) '())
                    (call ,name ,(string-append "C" (number->string i)) ,(objs (+ n (if rest? 1 0))) ,obj)
                    (ret)
                    (label ,next))))
            (loop (cdr cs) (+ i 1) (append (reverse block) code))))))

  (define (clause-method u class static? cl i fields field-names self self-mref . shared)
    (nanopass-case (L3 Clause) cl
      [(clause (,x* ...) ,x ,body)
       (let* ((start (string->symbol "start"))
              (mc (make-method-ctx u class static? start (and (not x) (length x*)) self-mref))
              (params (if x (append x* (list x)) x*)))
         (let loop ((ps params) (j (mctx-arg-base mc)))
           (unless (null? ps)
             (hashtable-set! (mctx-vars mc) (car ps) (cons 'arg j))
             (loop (cdr ps) (+ j 1))))
         (for-each (lambda (f n) (hashtable-set! (mctx-vars mc) f (cons 'field n))) fields field-names)
         (when self (hashtable-set! (mctx-vars mc) self (if (pair? shared) (cadr shared) '(self))))
         (mctx-big?-set! mc (bigger-than? body big-body-nodes))
         (emit! mc `(label ,start))
         (cg body mc 'tail)
         (finish-method mc (string-append (if (pair? shared) (car shared) "") "C" (number->string i)) (if static? '(static) '()) (objs (length params))))]))

  (define (finish-method mc name attrs params)
    `(method ,name (attrs ,@attrs) (params ,@params) (ret ,obj)
       (locals ,@(reverse (mctx-locals mc)))
       (,(interop 'namespaces))
       (body ,@(reverse (mctx-buffer mc)))))

  ;; 创建闭包：newobj 后逐个写自由变量字段
  (define (make-closure! mc c fv* self)
    (if (car c)
        (load-static-proc! mc c)
        (begin
          (emit! mc `(newobj ,(cadr c) ()))
          (fill-fields! mc c fv* #f))))

  ;; 栈顶（load 为 #f 时）或 load 产生的对象：写入自由变量字段
  (define (fill-fields! mc c fv* load)
    (let loop ((fs fv*) (i 0))
      (unless (null? fs)
        (if load (begin (load) (emit! mc `(castclass ,(cadr c)))) (emit! mc '(dup)))
        (load-var! mc (car fs))
        (emit! mc `(stfld ,(cadr c) ,(string-append "c" (number->string i))))
        (loop (cdr fs) (+ i 1)))))

  ;; ———————————————— 编译单元 ————————————————

  ;; forms：L3 顶层表达式列表。返回 (values Lcil 的 S 表达式 常量列表)。
  ;; live? 为真时（内存模式 eval）常量不编码，Lcil 中写 (live-constants n)，常量对象由调用者交给发射器：
  ;; 语法对象的 mark、rib 靠 eq? 身份工作，编码再解码的副本会破坏它们。
  (define (generate-unit forms assembly-name module-name massigned . live?)
    (let ((u (make-unit assembly-name module-name '() '() 0 (make-eq-hashtable) '() '() '() (make-eq-hashtable)
                        (make-eq-hashtable) '() 0 massigned (make-eq-hashtable)
                        (if (and opt-034? (not (string=? assembly-name boot-assembly-name)) (boot-interface)) #f 'off)
                        0 (make-eqv-hashtable) (make-hashtable string-hash string=?))))
      ;; 1. 已知过程：库变量绑定的、无自由变量且不被再赋值的 lambda
      (for-each (lambda (f) (collect-known! u f)) forms)
      (hashtable-clear! var-types)
      (when opt-infer?
        (let ((flags (loop-forms)))
          (if (and (list? flags) (= (length flags) (length forms)))
              (for-each (lambda (f loop?) (when loop? (infer-var-types! f))) forms flags)
              (for-each infer-var-types! forms))))
      ;; 2. 每个顶层形式一个静态方法 Init<k>
      (let* ((inits
              (let loop ((fs forms) (k 0))
                (if (null? fs)
                    '()
                    (cons (top-level-method u (car fs) k) (loop (cdr fs) (+ k 1))))))
             (holders (emit-holders! u))
             (entry
              `(method "Initialize" (attrs static) (params) (ret ,obj)
                 (body
                   ;; 直接调用了 boot 镜像中的过程：先核对正在运行的镜像就是编译时的那个（iron-034e）
                   ,@(if (eq? (unit-boot-ref u) #t)
                         `((ldstr ,(car (boot-interface))) (call ,boot-interface-type "Check" (,string-type) ,void-type))
                         '())
                   ;; 预链接：先建好全部已知过程并写入字段与位置
                   ,@(apply append
                       (map (lambda (p)
                              (let ((load (car p)) (field (cadr p)) (loc (caddr p)))
                                `(,@load (stsfld ,module-name ,field)
                                  ,@(if loc
                                        `((ldconst ,(constant-index! u loc)) (castclass ,symbol-type)
                                          (ldsfld ,module-name ,field) (stfld ,symbol-type "Value"))
                                        '()))))
                            (reverse (unit-prelink u))))
                   ,@(let loop ((k 0) (n (length inits)))
                       (cond
                         ((= n 0) `((ldsfld ,unspecified-type "Instance") (ret)))
                         ((= k (- n 1)) `((call ,module-name ,(init-name k) () ,obj) (ret)))
                         (else (cons* `(call ,module-name ,(init-name k) () ,obj) '(pop) (loop (+ k 1) n))))))))
             (module
              `(type ,module-name (attrs abstract sealed)
                 ,@(map (lambda (f) `(field ,f ,obj static)) (reverse (unit-mfields u)))
                 ,@inits
                 ,entry))
             (namespaces (unit-namespaces u)))
        (let ((lcil `(lcil 1
           (assembly ,assembly-name
             (references ,(target-core-assembly (current-target)) ,@(unit-references u))
             (runtime scheme)                                   ; cleat 的发射器按它选 Scheme 互操作与常量编码（LCIL.md §2；缺省是 clojure）
             (entry ,module-name "Initialize"))
           ,@(let ((cs (reverse (unit-consts u))))
               (cond
                 ((null? cs) '())
                 ((and (pair? live?) (car live?)) `((live-constants ,(length cs))))
                 (else
                  `((constants ,@(let loop ((encs (let ((e (encode-constants cs)))
                                                     (if (canonical-gensyms) (canonicalize-encodings e) e)))
                                            (i 0))
                                   (if (null? encs) '() (cons (list i (car encs)) (loop (cdr encs) (+ i 1))))))))))
           ,@(map (lambda (t) (fill-namespaces t namespaces)) (cons module (reverse (unit-classes u)))))))
          (record-interface! u)
          (values lcil (reverse (unit-consts u)))))))

  (define (init-name k) (string-append "Init" (number->string k)))

  ;; 把方法中的占位 (命名空间指令 . NAMESPACES) 换成单元的命名空间列表（方法生成时可能还没见到全部 host-using）
  (define (fill-namespaces t namespaces)
    (map (lambda (item)
           (if (and (pair? item) (eq? (car item) 'method))
               (map (lambda (c)
                      (if (and (pair? c) (eq? (car c) (interop 'namespaces))) `(,(car c) ,@namespaces) c))
                    item)
               item))
         t))

  (define (collect-known! u form)
    (nanopass-case (L3 Expr) form
      [(library ,d ([,x* ,s* ,e*] ...) ,body)
       (for-each
         (lambda (x s e)
           (nanopass-case (L3 Expr) e
             [(lambda ,nm (,x1* ...) ,cl* ...)
              (when (and (null? x1*) (not (hashtable-ref (unit-massigned u) x #f)))
                (let ((k (if opt-shared?
                             (let ((slot (shared-slot! u)))
                               (make-known (car slot) (map clause-shape cl*) (car slot) (cdr slot)))
                             (let ((name (class-name u nm)))
                               (make-known name (map clause-shape cl*) name "")))))
                  (hashtable-set! (unit-known u) x k)
                  (unless (or (null? d) (not opt-034?)) (hashtable-set! (unit-known-loc u) s k))))]
             [else (void)]))
         x* s* e*)]
      ;; compile-library 的产物（.fasl / 库 dll）：库形式包在 (list … (lambda () (library …)) …) 里（iron-034e）。
      ;; 其中的已知过程同样登记，类名固定，别的单元可经接口文件直接调用；求值顺序不变（不预链接，见 cg 的 library）
      [else (when opt-034?
              (for-each (lambda (lib) (collect-known! u lib)) (nested-libraries form)))]))

  ;; 只沿调用的实参与"体就是一个 library 的无参 lambda"向下找，不进入别的代码
  (define (nested-libraries form)
    (nanopass-case (L3 Expr) form
      [(call ,e0 ,e* ...) (apply append (map nested-libraries e*))]
      [(lambda ,nm (,x* ...) ,cl* ...)
       (if (not (= (length cl*) 1))
           '()
       (nanopass-case (L3 Clause) (car cl*)
         [(clause (,x1* ...) ,x ,body)
          (if (or (pair? x1*) x)
              '()
              (nanopass-case (L3 Expr) body
                [(library ,d ([,x2* ,s* ,e*] ...) ,body1) (if (null? d) '() (list body))]
                [else '()]))]))]
      [else '()]))

  (define (top-level-method u form k)
    (let ((mc (make-method-ctx u (unit-module u) #t #f #f #f)))
      (nanopass-case (L3 Expr) form
        [(library ,d ([,x* ,s* ,e*] ...) ,body)
         (let ((program? (null? d)))
           (for-each
             (lambda (x s e)
               (let ((field (mfield! u x))
                     (k (hashtable-ref (unit-known u) x #f)))
                 (if k
                     ;; 已知过程：类在这里生成，赋值由入口的预链接完成
                     (let ((c (lambda-class! e mc #f x k)))
                       (unit-prelink-set! u (cons (list (with-buffer mc (lambda () (load-static-proc! mc c))) field (if program? #f s))
                                                  (unit-prelink u))))
                     (begin
                       (cg (named-lambda e) mc 'value)
                       (emit! mc `(stsfld ,(unit-module u) ,field))
                       (unless program?
                         (emit! mc `(ldconst ,(constant-index! u s)) `(castclass ,symbol-type)
                                `(ldsfld ,(unit-module u) ,field) `(stfld ,symbol-type "Value")))))))
             x* s* e*)
           (cg body mc 'tail))]
        [else (cg form mc 'tail)])
      (finish-method mc (init-name k) '(static) '())))

  (define (named-lambda e) e)

  ;; 已知过程的类名在 collect-known! 中预先确定，所以任何位置（包括类生成之前）的直接调用都能引用它。
  )
