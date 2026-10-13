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
(library (cleat scheme compiler codegen)
  (export generate-unit canonical-gensyms current-target unit-interface loop-forms)
  (import
    (rnrs)
    (only (cleat scheme) void unspecified? make-parameter parameterize gensym symbol-bound? symbol-value)
    (nanopass)
    (cleat scheme compiler languages)
    (cleat scheme compiler primitives)
    (cleat scheme compiler primdata)
    (cleat scheme compiler constants)
    (only (cleat scheme unsafe) $immutable-string? $get-environment-variable)
    (only (cleat scheme clr) clr-is clr-prop-get clr-call clr-static-call)
    (cleat scheme compiler targets))

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
  (define-target-type int64-type int64)
  (define-target-type double-type double)
  (define-target-type bool-type boolean)
  (define-target-type char-type char)
  (define-target-type string-type string)
  (define-target-type boot-interface-type boot-interface)
  (define-target-type void-type void)
  (define-target-type exception-type exception)
  (define-target-type unspecified-type unspecified)
  (define-target-type empty-list-type empty-list)
  (define-target-type proc-type procedure)
  (define-target-type variadic-type variadic-procedure)
  (define-target-type cons-type cons)
  (define-target-type box-type box)
  (define-target-type symbol-type symbol)
  (define-target-type rt-type rt)
  (define-target-type calls-type calls)
  (define-target-type arith-type inline-arith)
  (define-target-type scheme-exn-type scheme-exception)
  (define-target-type continuation-exn-type continuation-exception)
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
            type-names                      ; cleat-051：按名字生成的类型（Clojure 的函数类、deftype）已用的名字（字符串哈希表）
            local-known))         ; iron-070：eq 哈希表 letrec 绑定的词法变量 → (static | instance . known 记录)
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
  ;; CLEAT_SCHEME_NO_034 含 shared 时关（opt-shared? 定义在 opt-off? 之后）
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
            vars                 ; eq 哈希表：变量 → (arg . i) | (targ i . 类型) | (local . i) | (tlocal i . 类型) | (field . 名字) | (self)
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
            (mutable big?)       ; 方法体很大（只执行一次的测试过程之类）：不内联算术（iron-034f）
            (mutable ret-type)    ; 原始签名的方法（cleat-042）：返回 l64 / fl，尾位置的值转换成原始值再 ret；否则 #f
            uses                 ; 局部变量清除（cleat-042 第 5 项）：变量 → 还没生成的引用个数（只登记做清除的方法）
            pinned))             ; 变量 → 所在循环层数：循环体外绑定、体内引用的变量在循环里不清除

  (define (make-method-ctx u class static? loop-label loop-arity self-mref)
    (make-mctx u class static? (make-eq-hashtable) '() 0 '() 0 0 loop-label loop-arity self-mref (if static? 0 1) (make-eq-hashtable) #f #f
               (make-eq-hashtable) (make-eq-hashtable)))

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
  ;; 暂存的局部变量按值的类型声明：被调者已转成 Procedure（Calls.Proc）、原始 int32 / double 存进 object 变量
  ;; 过不了 IL 校验（iron-070 内联后出现了参数里带 try 的未知过程调用）。按代码最后一条指令推断，推断不出的是 object。
  (define (code-result-type code)
    (if (null? code)
        obj
        (let ((i (list-ref code (- (length code) 1))))
          (case (car i)
            ((call callvirt)
             (let ((r (list-ref i (- (length i) 1))))
               (if (and (string? r) (member r (list proc-type int32-type double-type))) r obj)))
            ((unbox.any) (let ((t (cadr i))) (if (member t (list int32-type double-type)) t obj)))
            ((ldc.i4) int32-type)
            ((ldc.r8) double-type)
            (else obj)))))

  (define (operands! mc gens)
    (let ((codes (map (lambda (g) (with-buffer mc g)) gens)))
      (if (and (pair? codes) (exists contains-try? (cdr codes)))
          (let ((temps (map (lambda (c)
                              (let ((t (new-local! mc (code-result-type c)))) (emit-code! mc c) (emit! mc `(stloc ,t)) (cons t c)))
                            codes)))
            ;; 临时变量按值的类型声明（iron-070）；推断不出是 object 时，操作数原来以 castclass（IFn、Var …）结尾的取回后再转换一次，
            ;; 否则栈上的类型与随后的 callvirt 不符（ILVerify StackUnexpected；cleat-053 在 Clojure 镜像里发现）
            (for-each (lambda (tc)
                        (emit! mc `(ldloc ,(car tc)))
                        (when (equal? (code-result-type (cdr tc)) obj)
                          (for-each (lambda (i) (emit! mc i)) (reload-cast (cdr tc)))))
                      temps))
          (for-each (lambda (c) (emit-code! mc c)) codes))))

  ;; 把栈上 n 个值（已按顺序压栈）收进 object[]：先存临时变量再建数组
  (define (pack-array! mc n)
    (let ((temps (let loop ((i 0) (acc '())) (if (= i n) acc (loop (+ i 1) (cons (new-local! mc obj) acc))))))
      ;; temps 为逆序：最后压栈的值先出栈
      (for-each (lambda (t) (emit! mc `(stloc ,t))) temps)
      (emit! mc `(ldc.i4 ,n) `(newarr ,obj))
      (let loop ((ts (reverse temps)) (i 0))
        (unless (null? ts)
          (emit! mc '(dup) `(ldc.i4 ,i))
          (ldloc-clear! mc (car ts))
          (emit! mc '(stelem.ref))
          (loop (cdr ts) (+ i 1))))))

  ;; ———————————————— 结果按上下文收尾 ————————————————

  ;; 栈上是一个 object
  (define (finish-value! mc ctx)
    (case ctx
      ((value) (void))
      ((effect) (emit! mc '(pop)))
      ((test) (emit! mc `(call ,rt-type "IsTrue" (,obj) ,bool-type)))
      ((tail)
       ;; 原始签名的方法：object 结果按 JVM 的规则转换（Number.longValue / doubleValue，ClojureStaticCalls.PrimRet…）
       (case (mctx-ret-type mc)
         ((l64) (emit! mc `(call ,static-calls-type "PrimRetLong" (,obj) ,int64-type)))
         ((fl) (emit! mc `(call ,static-calls-type "PrimRetDouble" (,obj) ,double-type)))
         (else (void)))
       (emit! mc '(ret)))))

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
    (and (eq? ctx 'tail) (not (mctx-ret-type mc)) (= (mctx-try-depth mc) 0) (target-tail-calls? (current-target))))

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
      ((null? d) (emit! mc `(ldsfld ,empty-list-type "Instance")))   ; '()
      ((nil? d) (emit! mc '(ldnull)))                                   ; #nil
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
  ;; 推断收敛后每个候选变量的类型（不论是否拆箱）：宿主调用按它选重载（cleat-042）——与推断时 expr-type 给出的
  ;; 返回类型一致；var-types 只含决定拆箱的变量
  (define sound-types (make-eq-hashtable))
  ;; 原始签名子句的 L / D 形参（cleat-042）：类型固定（不是推断候选），推断与代码生成都当作已知
  (define fixed-types (make-eq-hashtable))
  ;; 变量的已知类型（iron-071，类型格的元素），不改变存储：推断出的 let / label 变量（拆箱的除外），与生成某段代码期间
  ;; 由谓词或已执行的操作收窄出的事实（进出该段时加上、恢复）
  (define var-facts (make-eq-hashtable))
  ;; 被 set! 赋值的局部变量：不参与收窄（每个编译单元重算）
  (define assigned-vars (make-eq-hashtable))

  ;; 测量用（iron-034）：环境变量 CLEAT_SCHEME_NO_034 设置时关掉 034 的生成优化（内联算术、拆箱、单元内直接调用），
  ;; 同一个 boot 镜像里对照编译时间与运行时间
  (define no-034 (or ($get-environment-variable "CLEAT_SCHEME_NO_034") ""))
  (define (opt-off? name)
    (or (string=? no-034 "1")
        (let ((n (string-length name)) (m (string-length no-034)))
          (let loop ((i 0)) (and (<= (+ i n) m) (or (string=? (substring no-034 i (+ i n)) name) (loop (+ i 1))))))))
  (define opt-034? (not (opt-off? "known")))
  (define opt-inline? (not (opt-off? "inline")))
  (define opt-infer? (not (opt-off? "infer")))
  (define opt-shared? (not (opt-off? "shared")))
  ;; 测量开关的值里是否含 sub（如 CLEAT_NO_070=letrec,call）
  (define (string-contains? s sub)
    (let ((n (string-length s)) (m (string-length sub)))
      (let loop ((i 0)) (and (<= (+ i m) n) (or (string=? (substring s i (+ i m)) sub) (loop (+ i 1)))))))
  ;; iron-070：letrec 绑定的 lambda 在组内（含自身递归）直接调用子句方法；CLEAT_NO_070 为 1 或 call 时关
  (define opt-local-call?
    (let ((v ($get-environment-variable "CLEAT_NO_070")))
      (not (and (string? v) (or (string=? v "1") (string-contains? v "call"))))))

  ;; ———————————————— boot 镜像的接口（iron-034e，D5）————————————————
  ;; nano 构建脚本在镜像旁写出 cleat.scheme.boot.ifc：
  ;;   (cleat-scheme-interface 1 "镜像 id" ("位置符号名" "类名" ((固定参数个数 . 剩余?) …)) …)
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
                    (and (pair? x) (eq? (car x) 'cleat-scheme-interface)
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

  ;; 用户库的接口文件（iron-034e）：compile-library-fasl 在 .fasl 旁写出 名字.ifc，
  ;;   (cleat-scheme-interface 1 "程序集名" ("位置符号名" "类名" 子句形状) …)
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
      (when (and (pair? x) (eq? (car x) 'cleat-scheme-interface)
                 (pair? (cdr x)) (eqv? (cadr x) 1) (pair? (cddr x)) (string? (caddr x)))
        (add-interface-entries! library-ifc-table (caddr x) (cdddr x)))))

  (define (add-interface-entries! table asm entries)
    (for-each
      (lambda (entry)
        (let ((class (string-append (cadr entry) ", " asm)))
          (hashtable-set! table (car entry)
            (make-known class (caddr entry) class (if (pair? (cdddr entry)) (cadddr entry) "")))))
      entries))

;; 上一个单元的接口条目：(("位置符号名" "类名" 子句形状) …)，供构建脚本写 boot 的 .ifc、compile-library-fasl 写库的 .ifc。
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

  ;; ———————————————— 类型格（iron-071，照 Chez cptypes-lattice.ss 裁剪）————————————————
  ;; 元素：fx fl int（精确整数，可能是大整数）real num bool char symbol pair null list vector。
  ;; 推断里另有 top（还没定，乐观不动点的起点）与 #f（什么都可能）。父子关系只有下面几条，其余互不相交
  (define type-parents '((fx . int) (int . real) (fl . real) (real . num) (pair . list) (null . list)))
  (define (type-ancestors t)
    (if t (cons t (let ((p (assq t type-parents))) (if p (type-ancestors (cdr p)) '()))) '()))
  (define (type<=? a b) (and a b (memq b (type-ancestors a)) #t))
  (define (type-disjoint? a b) (and a b (not (type<=? a b)) (not (type<=? b a))))
  ;; 合流（if 两支、label 各 jump 的实参、变量的各定义）：最小公共上界
  (define (type-join a b)
    (cond ((eq? a 'top) b)
          ((eq? b 'top) a)
          ((not (and a b)) #f)
          (else (let ((bs (type-ancestors b))) (find (lambda (t) (memq t bs)) (type-ancestors a))))))
  ;; 收窄：已知是 a、又知是 b → 较具体的一个（不相容时分支走不到，取 b）
  (define (type-meet a b)
    (cond ((not a) b) ((not b) a) ((type<=? a b) a) (else b)))
  ;; 已知是 a、又知不是 b
  (define (type-minus a b)
    (cond ((and (eq? a 'list) (eq? b 'null)) 'pair)
          ((and (eq? a 'list) (eq? b 'pair)) 'null)
          (else a)))
  ;; 只用于数值推断的旧名字：fx 与 fl 合流为 real，拆箱只认 fx / fl，效果与原来的 #f 相同
  (define (meet-type a b) (type-join a b))

  ;; 常量的类型
  (define (datum-type d)
    (cond ((and (fixnum? d) (exact? d)) 'fx)
          ((flonum? d) 'fl)
          ((and (integer? d) (exact? d)) 'int)
          ((and (number? d) (real? d)) 'real)
          ((number? d) 'num)
          ((boolean? d) 'bool)
          ((char? d) 'char)
          ((symbol? d) 'symbol)
          ((null? d) 'null)
          ((pair? d) 'pair)
          ((vector? d) 'vector)
          (else #f)))

  (define fx-result-prims '($fx+ $fx- $fx* $fxand $fxior $fxxor $fxnot $as-fixnum))
  (define fx-compare-prims '($fx=? $fx<? $fx>? $fx<=? $fx>=? $fxzero? $fxnegative? $fxpositive?))
  (define fl-compare-prims '($fl=? $fl<? $fl>? $fl<=? $fl>=?))
  (define fl-result-prims '($fl+ $fl- $fl* $fl/ $as-flonum))
  (define bool-result-prims '(eq? not null? nil? pair? $fixnum? $flonum?))

  ;; (quote d) 的 d，否则 (void)（L3 引用字面量的数据）
  (define (quoted-datum e)
    (nanopass-case (L3 Expr) e
      [(quote ,d) d]
      [else (void)]))

  ;; Numbers 的 int64 算术（cleat-042）：clojure.core 的 :inline 把 (+ a b) 等展开为
  ;;   ($clj-call "CallStatic" 3 '<System.Type clojure.lang.Numbers> '"add" '() a b)
  ;; 第三个实参是类型对象（不是字符串），泛型实参表是 '()。实参全为 l64 时改为直接调用 Numbers 的 (long …) 重载
  ;; （add / inc 等带溢出检查，与 object 重载的结果完全相同，JIT 内联），省掉每次运算的 Int64 装箱
  (define numbers-long-ops '("add" "minus" "multiply" "inc" "dec"))        ; (long …) → long
  (define numbers-bool-ops '("lt" "lte" "gt" "gte" "equiv"))              ; (long long) → bool

  (define (numbers-type? tp)
    (or (and (string? tp) (string=? tp "clojure.lang.Numbers"))
        (and (clr-is System.Type tp) (string=? (clr-prop-get System.Type FullName tp) "clojure.lang.Numbers"))))

  ;; 是 Numbers 的可拆箱算术形状时返回运算名，否则 #f
  (define (numbers-arith-op d0 d1 e*)
    (and (equal? d0 "CallStatic") (eqv? d1 3) (<= 4 (length e*) 5)
         (numbers-type? (quoted-datum (car e*)))
         (let ((op (quoted-datum (cadr e*))) (gen (quoted-datum (caddr e*))) (argc (- (length e*) 3)))
           (and (string? op) (or (null? gen) (not gen))
                (cond ((member op '("inc" "dec")) (= argc 1))
                      ((string=? op "minus") #t)
                      ((member op numbers-long-ops) (= argc 2))
                      ((member op numbers-bool-ops) (= argc 2))
                      (else #f))
                op))))

  (define (numbers-arith-shape? d0 d1 e*) (and (numbers-arith-op d0 d1 e*) #t))

  ;; 运算名与实参（实参全为 l64 时），否则 #f
  (define (numbers-arith-args e env)
    (nanopass-case (L3 Expr) e
      [(clj-call ,d0 ,d1 ,e* ...)
       (let ((op (numbers-arith-op d0 d1 e*)))
         (and op
              (for-all (lambda (x) (eq? (expr-type x env) 'l64)) (list-tail e* 3))
              (cons op (list-tail e* 3))))]
      [else #f]))

  ;; 表达式的类型（上面的格元素，另有 cleat-042 的 l64：Clojure 的 long，与格里其余元素都不相交）或 #f；env：变量 → 类型 |
  ;; top（还没定）。不在 env 里的变量查收窄与推断记下的事实（var-facts）
  (define (expr-type e env)
    (nanopass-case (L3 Expr) e
      [(quote ,d) (if (clr-is System.Int64 d) 'l64 (datum-type d))]
      [(clj-call ,d0 ,d1 ,e* ...)
       (guard (equal? d0 "PrimInvoke"))
       (sig-char-raw (sig-ret (quoted-datum (car e*))))]
      [(clj-call ,d0 ,d1 ,e* ...)
       ;; Numbers 的 long 运算：实参还没定（top）时暂为 top，不动点继续；
       ;; 其余（含 double 实参的 Numbers 运算）按静态决议出的宿主调用的返回类型：long → l64，double → fl
       (let* ((op (numbers-arith-op d0 d1 e*))
              (ts (and op (member op numbers-long-ops) (map (lambda (x) (expr-type x env)) (list-tail e* 3)))))
         (cond ((and ts (for-all (lambda (t) (eq? t 'l64)) ts)) 'l64)
               ((and ts (for-all (lambda (t) (memq t '(l64 top))) ts)) 'top)
               (else (host-call-type d0 d1 e* env))))]
      [,x (if (hashtable-contains? env x) (hashtable-ref env x #f) (hashtable-ref var-facts x #f))]
      [(primcall ,pr ,e* ...)
       (cond ((memq pr fx-result-prims) 'fx)
             ((memq pr fl-result-prims) 'fl)
             ((or (memq pr bool-result-prims) (memq pr fx-compare-prims) (memq pr fl-compare-prims)) 'bool)
             ((eq? pr 'cons) 'pair)
             (else #f))]
      [(call ,e0 ,e* ...)
       (let ((op (inline-op e0 (length e*))))
         (cond
           (op
            (case (car op)
              ((add sub mul div)
               (case (cdr op)
                 ((fx fl) (cdr op))
                 ;; 通用算符（iron-066）：操作数都是 flonum 时结果是 flonum；都是精确整数时是精确整数（fixnum 的和可能溢出成
                 ;; 大整数，不算 fx）
                 (else (let ((ts (map (lambda (a) (expr-type a env)) e*)))
                         (cond ((for-all (lambda (t) (memq t '(fl top))) ts) (fold-left type-join 'top ts))
                               ((and (not (eq? (car op) 'div)) (for-all (lambda (t) (memq t '(fx int top))) ts)) 'int)
                               (else #f))))))
              ((eq lt le gt ge) 'bool)
              (else #f)))
           ((unary-inline-op e0 (length e*)) 'bool)
           ;; 其余 boot 过程按 primdata 的签名（iron-069）：vector-length / char->integer / fxand … 正常返回时必是 fixnum，
           ;; flsqrt / fixnum->flonum … 必是 flonum，cons 是 pair（参数不对时报错，不影响返回值的类型）
           (else (boot-call-result-type e0 (length e*)))))]
      [(if ,e0 ,e1 ,e2) (type-join (expr-type e1 env) (expr-type e2 env))]
      [(begin ,e* ... ,e) (expr-type e env)]
      [(let ([,x* ,e*] ...) ,body) (expr-type body env)]
      [(jump ,x ,e* ...) 'top]                 ; 不返回：不影响 if 另一支的类型
      [(throw ,e) 'top]
      [else #f]))

  ;; primdata 签名的结果类型 → 格元素
  (define sig-types
    '((fixnum . fx) (flonum . fl) (integer . int) (number . num) (boolean . bool) (char . char) (symbol . symbol)
      (pair . pair) (list . list) (vector . vector)))

  ;; (gref boot 位置) 的调用按签名的结果类型给格元素，其余 #f；按位置符号缓存
  (define boot-result-cache (make-eq-hashtable))
  (define (boot-call-result-type e0 argc)
    (let ((p (boot-primdata e0)))
      (and p (primdata-arity-ok? p argc)
           (let ((r (assq (primdata-result-type p) sig-types))) (and r (cdr r))))))

  (define (boot-primdata e0)
    (nanopass-case (L3 Expr) e0
      [(gref ,s)
       (let ((r (hashtable-ref boot-result-cache s 'unknown)))
         (if (eq? r 'unknown)
             (let ((r (let ((name (boot-location-name s))) (and name (primdata-lookup name)))))
               (hashtable-set! boot-result-cache s r)
               r)
             r))]
      [else #f]))

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
      [(quote ,d) (or (clr-is System.Int64 d) (and (datum-type d) #t))]
      [,x (candidate? x)]
      [(clj-call ,d0 ,d1 ,e* ...) (or (equal? d0 "PrimInvoke") (and (host-call-layout d0 d1 e*) #t))]
      [(primcall ,pr ,e* ...) (and (or (memq pr fx-result-prims) (memq pr fl-result-prims) (eq? pr 'cons)) #t)]
      [(call ,e0 ,e* ...) (and (or (inline-op e0 (length e*)) (boot-call-result-type e0 (length e*))) #t)]
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
       (for-each (lambda (cl) (fix-clause-params! nm cl)) cl*)
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
      [(escape-frame ,x ,body) (Expr body)]
      [(try-handle ,x ,body0 ,body1) (Expr body0) (Expr body1)]
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
      [(clj-call ,d0 ,d1 ,e* ...)
       (cond
         ((numbers-arith-shape? d0 d1 e*) (for-each Raw (list-tail e* 3)))
         ((equal? d0 "PrimInvoke")
          (Expr (cadr e*))
          (for-each (lambda (a c) (if (sig-char-raw c) (Raw a) (Expr a))) (cddr e*) (sig-params (quoted-datum (car e*)))))
         ;; 宿主调用的实参（cleat-042 第 4 项）：静态决议时通常选到原始类型的重载（Numbers.isPos(long) …），算原始用途；
         ;; 实例算装箱用途
         ((host-call-layout d0 d1 e*)
          => (lambda (l) (when (cadr l) (Expr (cadr l))) (for-each Raw (caddr l))))
         (else (for-each Expr e*)))]
      [(clj-static-call ,d0 ,d1 ,e* ...) (for-each Expr e*)]
      [(clj-case ,d ,e0 ,e1 ,e* ...) (Expr e0) (Expr e1) (for-each Expr e*)]
      [(clj-deftype ,d (,x* ...) ,le* ...) (for-each exclude! x*) (for-each Expr le*)]
      [else (void)])
    (Expr ir))

  (define (inlined-binding? x)
    (let* ((s (symbol->string x)) (n (string-length s)))
      (and (> n 4) (string=? (substring s (- n 4) n) "$inl"))))

  ;; 原始签名子句的 L / D 形参登记为固定类型（cleat-042）
  (define (fix-clause-params! nm cl)
    (let ((sig (clause-sig nm cl)))
      (when sig
        (nanopass-case (L3 Clause) cl
          [(clause (,x* ...) ,x ,body)
           (for-each (lambda (p c) (let ((r (sig-char-raw c))) (when r (hashtable-set! fixed-types p r))))
                     x* (sig-params sig))]))))

  ;; 记下被 set! 赋值的变量（iron-071：收窄只对不被赋值的变量做）。嵌套的 lambda 也要走：被捕获又被赋值的变量已经装箱
  ;; （mref），这里只剩方法内的局部变量。cleat 的 L3 的每个复合节点都要走到（漏掉 Clojure 节点里的 set! 会让被赋值的变量被收窄）
  (define-pass collect-assigned! : L3 (ir) -> * (v)
    (Body : Clause (ir) -> * (v)
      [(clause (,x* ...) ,x ,body) (Expr body)])
    (Expr : Expr (ir) -> * (v)
      [(set! ,x ,e) (hashtable-set! assigned-vars x #t) (Expr e)]
      [(if ,e0 ,e1 ,e2) (Expr e0) (Expr e1) (Expr e2)]
      [(begin ,e* ... ,e) (for-each Expr e*) (Expr e)]
      [(gset! ,s ,e) (Expr e)]
      [(mset! ,x ,e) (Expr e)]
      [(primcall ,pr ,e* ...) (for-each Expr e*)]
      [(lambda ,nm (,x* ...) ,cl* ...) (for-each Body cl*)]
      [(letrec ([,x* ,le*] ...) ,body) (for-each Expr le*) (Expr body)]
      [(let ([,x* ,e*] ...) ,body) (for-each Expr e*) (Expr body)]
      [(library ,d ([,x* ,s* ,e*] ...) ,body) (for-each Expr e*) (Expr body)]
      [(call ,e ,e* ...) (Expr e) (for-each Expr e*)]
      [(and ,e* ...) (for-each Expr e*)]
      [(or ,e* ...) (for-each Expr e*)]
      [(throw ,e) (Expr e)]
      [(try-finally ,e0 ,e1) (Expr e0) (Expr e1)]
      [(try-catch ,e (,d* ,e*) ...) (Expr e) (for-each Expr e*)]
      [(label ,x (,x* ...) ,body0 ,body1) (Expr body0) (Expr body1)]
      [(jump ,x ,e* ...) (for-each Expr e*)]
      [(escape-frame ,x ,body) (Expr body)]
      [(try-handle ,x ,body0 ,body1) (Expr body0) (Expr body1)]
      [(host-call ,d0 ,d1 ,st ,e* ...) (for-each Expr e*)]
      [(host-field-get ,d0 ,d1 ,st ,e* ...) (for-each Expr e*)]
      [(host-field-set ,d0 ,d1 ,st ,e* ...) (for-each Expr e*)]
      [(host-new ,d ,e* ...) (for-each Expr e*)]
      [(host-new-array ,d ,e) (Expr e)]
      [(host-cast ,d ,e) (Expr e)]
      [(host-is ,d ,e) (Expr e)]
      [(unsupported ,d ,e* ...) (for-each Expr e*)]
      [(box ,e) (Expr e)]
      [(set-box! ,x ,e) (hashtable-set! assigned-vars x #t) (Expr e)]
      [(clj-var-set! ,d ,e) (Expr e)]
      [(clj-def ,d ,st0 ,st1 ,e0 ,e1) (Expr e0) (Expr e1)]
      [(clj-invoke ,e ,e* ...) (Expr e) (for-each Expr e*)]
      [(clj-keyword-invoke ,d ,e ,e* ...) (Expr e) (for-each Expr e*)]
      [(clj-call ,d0 ,d1 ,e* ...) (for-each Expr e*)]
      [(clj-static-call ,d0 ,d1 ,e* ...) (for-each Expr e*)]
      [(clj-case ,d ,e0 ,e1 ,e* ...) (Expr e0) (Expr e1) (for-each Expr e*)]
      [(clj-deftype ,d (,x* ...) ,le* ...) (for-each Expr le*)]
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
      [(escape-frame ,x ,body) (when (more?) (Expr body))]
      [(try-handle ,x ,body0 ,body1) (when (more?) (Expr body0) (Expr body1))]
      [(label ,x (,x* ...) ,body0 ,body1) (when (more?) (Expr body0) (Expr body1))]
      [(jump ,x ,e* ...) (when (more?) (for-each Expr e*))]
      [(host-call ,d0 ,d1 ,st ,e* ...) (when (more?) (for-each Expr e*))]
      [(host-new ,d ,e* ...) (when (more?) (for-each Expr e*))]
      [else (more?)])
    (Expr ir)
    (> count n))

  (define (infer-var-types! form) (infer-var-types-into! form #f var-types))

  ;; seeds：#f，或 变量 → fx / fl 的表——这些变量的类型定死（不按定义推断），供拆箱循环（iron-071）在推测的形参类型下
  ;; 重算循环体里的变量；结果写进 out
  (define (infer-var-types-into! form seeds out)
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
            (cands (filter (lambda (x) (and (not (hashtable-ref excluded x #f))
                                            (not (and seeds (hashtable-contains? seeds x)))))
                           (vector->list (hashtable-keys defs)))))
        (define (deps e)                     ; expr-type 会查的变量
          (nanopass-case (L3 Expr) e
            [,x (list x)]
            [(clj-call ,d0 ,d1 ,e* ...)
             (let ((layout (host-call-layout d0 d1 e*)))
               (if layout (apply append (map deps (caddr layout))) '()))]
            [(if ,e0 ,e1 ,e2) (append (deps e1) (deps e2))]
            [(begin ,e* ... ,e) (deps e)]
            [(let ([,x* ,e*] ...) ,body) (deps body)]
            [(call ,e0 ,e* ...)              ; 通用算符的类型取决于操作数
             (let ((op (inline-op e0 (length e*))))
               (if (and op (eq? (cdr op) 'generic)) (apply append (map deps e*)) '()))]
            [else '()]))
        (define (meet-defs x)                ; x 的定义列表逐个合流类型
          (let ((t (let f ((es (hashtable-ref defs x '())) (t 'top))
                     (if (or (null? es) (not t)) t (f (cdr es) (meet-type t (expr-type (car es) env)))))))
            ;; 内联子句的形参（$inl，见 (clojure syntax) 的 inline-call）不推断为 double（cleat-042）
            (if (and (eq? t 'fl) (inlined-binding? x)) #f t)))
        (define (user-entries! x e)          ; 把候选 x 记进其定义 e 引用到的变量的 users
          (for-each (lambda (v)
                      (hashtable-set! users v (cons x (hashtable-ref users v '()))))
                    (deps e)))
        (define (propagate! work)            ; 不动点迭代：类型变化者的影响者重新入队
          (unless (null? work)
            (let* ((x (car work))
                   (old (hashtable-ref env x #f))
                   (new (and old (meet-defs x))))
              (if (and old (not (eq? old new)))
                  (begin
                    (hashtable-set! env x new)
                    (propagate! (append (hashtable-ref users x '()) (cdr work))))
                  (propagate! (cdr work))))))
        (define (decide! x)                  ; 原始用法不少于装箱用法的变量记入 var-types（拆箱），其余有类型的记入 var-facts
          (let ((t (hashtable-ref env x #f)))
            ;; 宿主调用按推断收敛后的类型选重载（cleat-042），不论是否拆箱；推测（seeds）下的不算
            (when (and (memq t '(fx fl l64)) (not seeds)) (hashtable-set! sound-types x t))
            (cond ((and (memq t '(fx fl l64))
                        (<= (hashtable-ref boxed-uses x 0) (hashtable-ref raw-uses x 0)))
                   (hashtable-set! out x t))
                  ;; 推测（seeds）下算出的类型只在快循环里成立，不记；l64（Clojure 的 long）只在拆箱时才当作已知（同 cleat-042）
                  ((and t (not (memq t '(top l64))) (not seeds))
                   (hashtable-set! var-facts x t)))))
        ;; 原始签名子句的 L / D 形参（cleat-042）：类型固定，不是推断候选
        (let-values (((ks vs) (hashtable-entries fixed-types)))
          (vector-for-each (lambda (k v)
                             (hashtable-set! env k v)
                             (unless seeds (hashtable-set! var-types k v) (hashtable-set! sound-types k v)))
                           ks vs))
        (when seeds
          (let-values (((ks ts) (hashtable-entries seeds)))
            (vector-for-each (lambda (k t) (hashtable-set! env k t)) ks ts)))
        (for-each (lambda (x) (hashtable-set! env x 'top)) cands)
        (for-each (lambda (x)
                    (for-each (lambda (e) (user-entries! x e))
                              (hashtable-ref defs x '())))
                  cands)
        (propagate! cands)
        (for-each decide! cands))))

  ;; ———————————————— 拆箱循环（iron-071）————————————————
  ;; 最内层的 label 循环里，形参的回边实参都是"fixnum 形"（fixnum 常量、已知 fixnum、本组形参、或它们的通用 + - *）时，
  ;; 推测这些形参是 fixnum：循环体生成两份。快的一份形参是 int32 局部变量，回边上的通用算术按 int64 算，结果都在 fixnum
  ;; 范围内就直接回快循环（不装箱）；入口实参不是 fixnum、或算术溢出时，把当前值装箱转到慢的一份（与原来的生成相同）。
  ;; Chez 的 fixnum 不用分配，这里每轮 + - 的结果都要装箱（超出缓存时分配），sum 一类计数循环的大半时间在这上面。
  (define no-071 (or ($get-environment-variable "CLEAT_NO_071") ""))
  (define opt-spec? (not (or (string=? no-071 "1") (string-contains? no-071 "spec"))))
  (define spec-body-limit 600)             ; 循环体超过这么多 L3 节点不复制

  ;; (call (gref +|-|*) a b) 的通用算符（不是 fx+ 等）时返回 add / sub / mul
  (define (generic-arith-op e)
    (nanopass-case (L3 Expr) e
      [(call ,e0 ,e* ...)
       (let ((op (inline-op e0 (length e*))))
         (and op (eq? (cdr op) 'generic) (memq (car op) '(add sub mul)) (car op)))]
      [else #f]))

  (define (arith-operands e)
    (nanopass-case (L3 Expr) e [(call ,e0 ,e* ...) e*] [else '()]))

  ;; 推测的形参集合 S，或 #f
  (define (loop-spec-params mc x x* body0 body1)
    ;; 局部变量清除（cleat-042）生效的方法里不做：循环体生成两份，引用计数按一份算。cleat 的元层没有 async 帧
    ;; （Clojure 的 ^:async fn 不会有推测所需的 Scheme 通用算术）
    (and opt-infer? opt-spec? (not (mctx-big? mc)) (not (clearing? mc))
         (exists (lambda (p) (not (hashtable-ref var-types p #f))) x*)
         (not (bigger-than? body1 spec-body-limit))
         (let ((labels (make-eq-hashtable)) (back (make-eq-hashtable)) (entry (make-eq-hashtable))
               (excluded (make-eq-hashtable)) (raw (make-eq-hashtable)) (boxed (make-eq-hashtable)))
           (define (walk e defs)
             (infer-walk e
                         (lambda (v e) (when (and e (memq v x*)) (hashtable-set! defs v (cons e (hashtable-ref defs v '())))))
                         (lambda (v) (hashtable-set! excluded v #t))
                         labels
                         (lambda (v) #f)
                         (lambda (v raw?)
                           (when (memq v x*)
                             (let ((t (if raw? raw boxed))) (hashtable-set! t v (+ 1 (hashtable-ref t v 0))))))))
           (define (var-in? e s) (nanopass-case (L3 Expr) e [,x (and (memq x s) #t)] [else #f]))
           (define (fx-simple? e s) (or (eq? (static-type e) 'fx) (var-in? e s)))
           (define (fx-like? e s)
             (or (fx-simple? e s)
                 (and (generic-arith-op e) (for-all (lambda (a) (fx-simple? a s)) (arith-operands e)))))
           (hashtable-set! labels x x*)
           (walk body1 back)
           ;; 循环体里还有 label：不是最内层，不复制（嵌套复制会指数增长）
           (and (= (hashtable-size labels) 1)
                (begin
                  (walk body0 entry)
                  (let* ((defs-of (lambda (t p) (hashtable-ref t p '())))
                         (s0 (filter
                               (lambda (p)
                                 (and (not (hashtable-ref var-types p #f))
                                      (not (hashtable-ref excluded p #f))
                                      (pair? (defs-of back p))
                                      ;; 有 fixnum 的迹象：某个实参是 fixnum 常量或通用算术
                                      (exists (lambda (e) (or (fixnum-constant e) (generic-arith-op e)))
                                              (append (defs-of entry p) (defs-of back p)))
                                      (not (exists (lambda (e) (eq? (static-type e) 'fl)) (defs-of entry p)))
                                      (<= (hashtable-ref boxed p 0) (hashtable-ref raw p 0))))
                               x*)))
                    ;; 不动点：回边实参不是 fixnum 形的形参去掉，直到不变
                    (let loop ((s s0))
                      (let ((s1 (filter (lambda (p) (for-all (lambda (e) (fx-like? e s)) (defs-of back p))) s)))
                        (cond ((null? s1) #f)
                              ((= (length s1) (length s)) s1)
                              (else (loop s1)))))))))))

  ;; 推测 S 为 fixnum 时循环体里的变量类型：在 var-types 的副本上按 S 重算 body1
  (define (loop-spec-types s body1)
    (let ((spec (hashtable-copy var-types #t)) (seeds (hashtable-copy var-types #t)))
      (for-each (lambda (p) (hashtable-set! seeds p 'fx)) s)
      (infer-var-types-into! body1 seeds spec)
      (for-each (lambda (p) (hashtable-set! spec p 'fx)) s)
      spec))

  (define (loc-raw-type loc) (and (eq? (car loc) 'tlocal) (cddr loc)))
  (define (loc-index loc) (if (eq? (car loc) 'tlocal) (cadr loc) (cdr loc)))

  ;; (label x (x* ...) body0 body1) 的拆箱生成：body0（入口）；快的一份；慢的一份
  (define (cg-spec-label! x x* body0 body1 s mc ctx)
    (let* ((base var-types)
           (spec (loop-spec-types s body1))
           (fast-locs (begin (set! var-types spec)
                             (let ((l (map (lambda (p) (bind-local! mc p) (hashtable-ref (mctx-vars mc) p #f)) x*)))
                               (set! var-types base)
                               l)))
           (slow-locs (map (lambda (p) (bind-local! mc p) (hashtable-ref (mctx-vars mc) p #f)) x*))
           (fast-l (new-label! mc)) (slow-l (new-label! mc)) (end-l (new-label! mc)))
      (define (entry mode) (vector 'spec mode fast-l slow-l fast-locs slow-locs))
      (define (bind-copy! locs) (for-each (lambda (p l) (hashtable-set! (mctx-vars mc) p l)) x* locs))
      (hashtable-set! (mctx-labels mc) x (entry 'dispatch))
      (let ((code (with-buffer mc (lambda () (cg body0 mc ctx)))))
        (emit-code! mc code)
        (when (or (branches-to? code fast-l) (branches-to? code slow-l))
          (let ((fast (with-buffer mc
                        (lambda ()
                          (bind-copy! fast-locs)
                          (set! var-types spec)
                          (cg body1 mc ctx)
                          (set! var-types base)))))
            (unless (eq? ctx 'tail) (emit! mc `(br ,end-l)))
            (when (branches-to? code fast-l)
              (emit! mc `(label ,fast-l))
              (emit-code! mc fast)
              (unless (eq? ctx 'tail) (emit! mc `(br ,end-l))))
            (when (or (branches-to? code slow-l) (branches-to? fast slow-l))
              (emit! mc `(label ,slow-l))
              (hashtable-set! (mctx-labels mc) x (entry 'slow))
              (bind-copy! slow-locs)
              (cg body1 mc ctx))
            (unless (eq? ctx 'tail) (emit! mc `(label ,end-l))))))))

  ;; 跳到拆箱循环。慢的一份里跳回慢循环；入口与快的一份里：实参能定为 fixnum 就进快循环，否则装箱后进慢循环
  (define (cg-spec-jump! entry e* mc ctx)
    (let ((mode (vector-ref entry 1)) (fast-l (vector-ref entry 2)) (slow-l (vector-ref entry 3))
          (fast-locs (vector-ref entry 4)) (slow-locs (vector-ref entry 5)))
      (define (stloc! loc) (emit! mc `(stloc ,(loc-index loc))))
      (if (eq? mode 'slow)
          (let ((temps (map (lambda (loc a)
                              (let ((rt (loc-raw-type loc)))
                                (if rt (cg-raw a mc rt) (cg a mc 'value))
                                (let ((t (new-local! mc (if rt (type-clr rt) obj)))) (emit! mc `(stloc ,t)) t)))
                            slow-locs e*)))
            (for-each (lambda (loc t) (emit! mc `(ldloc ,t)) (stloc! loc)) slow-locs temps)
            (emit! mc `(br ,slow-l)))
          ;; 每个实参存进临时变量：(种类 . 下标)，种类 i32 / f64 / i64（通用算术按 int64 算）/ obj / obj-fx / obj-fl（要检查）
          (let* ((temps
                  (map (lambda (loc a)
                         (define (temp! kind type) (let ((t (new-local! mc type))) (emit! mc `(stloc ,t)) (cons kind t)))
                         (case (loc-raw-type loc)
                           ((fx)
                            (cond
                              ((eq? (static-type a) 'fx) (cg-raw a mc 'fx) (temp! 'i32 int32-type))
                              ((and (generic-arith-op a)
                                    (for-all (lambda (o) (eq? (static-type o) 'fx)) (arith-operands a)))
                               (operands! mc (map (lambda (o) (lambda () (cg-raw o mc 'fx) (emit! mc '(conv.i8))))
                                                  (arith-operands a)))
                               (emit! mc (case (generic-arith-op a) ((add) '(add)) ((sub) '(sub)) (else '(mul))))
                               (temp! 'i64 int64-type))
                              (else (cg a mc 'value) (temp! 'obj-fx obj))))
                           ((fl)
                            (if (eq? (static-type a) 'fl)
                                (begin (cg-raw a mc 'fl) (temp! 'f64 double-type))
                                (begin (cg a mc 'value) (temp! 'obj-fl obj))))
                           (else (cg a mc 'value) (temp! 'obj obj))))
                       fast-locs e*))
                 (fail (new-label! mc))
                 (checked? #f))
            (for-each (lambda (t)
                        (case (car t)
                          ((i64) (set! checked? #t)
                           (emit! mc `(ldloc ,(cdr t)) '(conv.i4) '(conv.i8) `(ldloc ,(cdr t)) `(bne.un ,fail)))
                          ((obj-fx obj-fl) (set! checked? #t)
                           (emit! mc `(ldloc ,(cdr t)) `(isinst ,(if (eq? (car t) 'obj-fx) int32-type double-type))
                                  `(brfalse ,fail)))))
                      temps)
            (for-each (lambda (loc t)
                        (emit! mc `(ldloc ,(cdr t)))
                        (case (car t)
                          ((i64) (emit! mc '(conv.i4)))
                          ((obj-fx) (emit! mc `(unbox.any ,int32-type)))
                          ((obj-fl) (emit! mc `(unbox.any ,double-type))))
                        (stloc! loc))
                      fast-locs temps)
            (emit! mc `(br ,fast-l))
            (when checked?
              (emit! mc `(label ,fail))
              (for-each (lambda (loc t)
                          (emit! mc `(ldloc ,(cdr t)))
                          (let ((st (loc-raw-type loc)))
                            (case (car t)
                              ((i32) (unless st (box-raw! mc 'fx)))
                              ((f64) (unless st (box-raw! mc 'fl)))
                              ((i64) (if st
                                         (emit! mc '(conv.i4))
                                         (emit! mc `(call ,arith-type "Result" (,int64-type) ,obj))))
                              (else (when st (emit! mc `(unbox.any ,(type-clr st)))))))
                          (stloc! loc))
                        slow-locs temps)
              (emit! mc `(br ,slow-l)))))
      ;; 之后的代码不可达；补一个值保持栈形状一致（同 throw）
      (case ctx
        ((value) (emit! mc '(ldnull)))
        ((test) (emit! mc '(ldc.i4 0)))
        (else (void)))))

  ;; ———————————————— 收窄（iron-071）————————————————
  ;; 生成某段代码期间，变量的类型比推断的更具体时记进 var-facts（进入时加上、离开时恢复）。来源两种：
  ;; 1. 谓词：(if (pair? x) e1 e2) 的 e1 里 x 是 pair；e2 里 x 不是 pair（x 已知是 list 时就是 null）。not、
  ;;    (and …)（展开成 (if t1 t2 #f)）、(or …) 照逻辑组合；cond 的各子句是嵌套的 if，前面子句的假分支事实自然带进后面。
  ;; 2. 已执行的操作：let 的右边、begin 的前几项里无条件执行的 (car x) / (cdr x) 正常返回后 x 是 pair，
  ;;    (vector-ref v i) / (vector-set! v i y) 之后 v 是 vector、i 是 fixnum，fx+ 等之后操作数是 fixnum（否则已经报错）。
  ;; 只收窄方法内的局部变量与参数中不被 set! 的（jump 给 label 形参赋值后回到 label 头，离开了收窄的范围）。
  ;; 消费：fixnum / flonum 的内联算术与比较直接拆箱；car / cdr 不检查类型；vector-ref / vector-set! 不检查向量；
  ;; 结果已知的类型谓词直接是常量；精确整数的通用算术不生成 flonum 路径
  (define opt-narrow? (not (or (string=? no-071 "1") (string-contains? no-071 "narrow"))))

  (define (append-map f l) (apply append (map f l)))

  (define (narrowable? mc x)
    (let ((loc (hashtable-ref (mctx-vars mc) x #f)))
      (and loc (memq (car loc) '(arg local)) (not (hashtable-ref assigned-vars x #f)))))

  ;; 类型谓词 → 格元素：C# 原语，与 boot 过程（按名字）
  (define pred-prim-types '((pair? . pair) (null? . null) ($fixnum? . fx) ($flonum? . fl)))
  (define pred-boot-types
    '((pair? . pair) (null? . null) (list? . list) (vector? . vector) (fixnum? . fx) (flonum? . fl)
      (exact-integer? . int) (real? . real) (number? . num) (boolean? . bool) (char? . char) (symbol? . symbol)))

  ;; (call e0 a) 是类型谓词时给出类型
  (define (call-pred-type e0 argc)
    (and (= argc 1)
         (let ((op (unary-inline-op e0 argc)))
           (case op
             ((fixnum?) 'fx)
             ((flonum?) 'fl)
             (else (nanopass-case (L3 Expr) e0
                     [(gref ,s) (let ((name (boot-location-name s)))
                                  (and name (let ((r (assq name pred-boot-types))) (and r (cdr r)))))]
                     [else #f]))))))

  (define (fact x kind t) (cons x (cons kind t)))

  ;; 测试 e0 为真 / 为假时成立的事实：(values 真 假)，事实是 (x pos . 类型) 或 (x neg . 类型)
  (define (test-facts e0 mc)
    (define (none) (values '() '()))
    (define (on-var e t)
      (nanopass-case (L3 Expr) e
        [,x (if (narrowable? mc x) (values (list (fact x 'pos t)) (list (fact x 'neg t))) (none))]
        [else (none)]))
    (define (const? e pred) (nanopass-case (L3 Expr) e [(quote ,d) (pred d)] [else #f]))
    (define (true-facts e) (let-values (((t f) (test-facts e mc))) t))
    (define (false-facts e) (let-values (((t f) (test-facts e mc))) f))
    (if (not opt-narrow?)
        (none)
        (nanopass-case (L3 Expr) e0
          [(primcall ,pr ,e* ...)
           (cond ((and (eq? pr 'not) (= (length e*) 1))
                  (let-values (((t f) (test-facts (car e*) mc))) (values f t)))
                 ((and (= (length e*) 1) (assq pr pred-prim-types)) => (lambda (r) (on-var (car e*) (cdr r))))
                 ;; (eq? x '())
                 ((and (eq? pr 'eq?) (= (length e*) 2) (const? (cadr e*) null?)) (on-var (car e*) 'null))
                 (else (none)))]
          [(call ,e ,e* ...)
           (let ((t (call-pred-type e (length e*))))
             (if t (on-var (car e*) t) (none)))]
          [(and ,e* ...) (values (append-map true-facts e*) '())]
          [(or ,e* ...) (values '() (append-map false-facts e*))]
          [(if ,e1 ,e2 ,e3)
           (cond ((const? e3 not) (values (append (true-facts e1) (true-facts e2)) '()))                 ; (and e1 e2)
                 ((and (const? e2 not) (const? e3 (lambda (d) (eq? d #t)))) (let-values (((t f) (test-facts e1 mc))) (values f t)))
                 ((const? e2 (lambda (d) d)) (values '() (append (false-facts e1) (false-facts e3))))    ; (or e1 e3)
                 (else (none)))]
          [else (none)])))

  ;; e 正常求值完之后成立的事实（只看无条件求值的部分：调用与原语的操作数，不进 if / lambda）
  (define (effect-facts e mc)
    (define (var e) (nanopass-case (L3 Expr) e [,x (and (narrowable? mc x) x)] [else #f]))
    (define (pos e t) (let ((x (var e))) (if x (list (fact x 'pos t)) '())))
    (if (not opt-narrow?)
        '()
        (nanopass-case (L3 Expr) e
          [(primcall ,pr ,e* ...)
           (append (append-map (lambda (a) (effect-facts a mc)) e*)
                   (if (and (memq pr '(car cdr $car $cdr)) (= (length e*) 1)) (pos (car e*) 'pair) '()))]
          [(call ,e0 ,e* ...)
           (let ((argc (length e*)))
             (append (append-map (lambda (a) (effect-facts a mc)) e*)
                     (cond ((vector-op e0 argc) (append (pos (car e*) 'vector) (pos (cadr e*) 'fx)))
                           ((let ((op (inline-op e0 argc))) (and op (eq? (cdr op) 'fx)))
                            (append-map (lambda (a) (pos a 'fx)) e*))
                           (else '()))))]
          [else '()])))

  (define (fact-of x) (or (hashtable-ref var-types x #f) (hashtable-ref var-facts x #f)))

  ;; 加上事实，返回恢复用的表；拆箱的变量（var-types）不动
  (define (push-facts! facts)
    (let loop ((fs facts) (undo '()))
      (if (null? fs)
          undo
          (let* ((x (caar fs)) (kind (cadar fs)) (t (cddar fs)) (cur (fact-of x))
                 (new (if (eq? kind 'pos) (type-meet cur t) (and cur (type-minus cur t)))))
            (if (and new (not (eq? new cur)) (not (hashtable-contains? var-types x)))
                (let ((old (hashtable-ref var-facts x #f)))
                  (hashtable-set! var-facts x new)
                  (loop (cdr fs) (cons (cons x old) undo)))
                (loop (cdr fs) undo))))))

  (define (pop-facts! undo)
    (for-each (lambda (u)
                (if (cdr u) (hashtable-set! var-facts (car u) (cdr u)) (hashtable-delete! var-facts (car u))))
              undo))

  ;; (list #t / #f) 或 #f（不知道）
  (define (known-pred e t)
    (let ((r (pred-result e t))) (and (boolean? r) (list r))))

  ;; 测试 e0 是类型谓词（或其 not）且结果已知：(list #t / #f)，否则 #f
  (define (known-test e0)
    (nanopass-case (L3 Expr) e0
      [(primcall ,pr ,e* ...)
       (cond ((and (eq? pr 'not) (= (length e*) 1))
              (let ((k (known-test (car e*)))) (and k (list (not (car k))))))
             ((and (= (length e*) 1) (assq pr pred-prim-types)) => (lambda (r) (known-pred (car e*) (cdr r))))
             (else #f))]
      [(call ,e ,e* ...)
       (let ((t (call-pred-type e (length e*)))) (and t (known-pred (car e*) t)))]
      [else #f]))

  ;; 操作数是变量，不必求值
  (define (cg-known-bool! mc b ctx)
    (case ctx
      ((effect) (void))
      ((test) (emit! mc `(ldc.i4 ,(if b 1 0))))
      (else (load-constant! mc b) (finish-value! mc ctx))))

  (define (with-facts facts thunk)
    (let ((undo (push-facts! facts)))
      (thunk)
      (pop-facts! undo)))

  ;; 类型谓词作用在类型已知的变量上：#t / #f；不知道时 'unknown
  (define (pred-result e t)
    (let ((vt (nanopass-case (L3 Expr) e [,x (fact-of x)] [else #f])))
      (cond ((not (and opt-narrow? vt t)) 'unknown)
            ((type<=? vt t) #t)
            ((type-disjoint? vt t) #f)
            (else 'unknown))))

  (define (type-clr t)
    (case t
      ((fl) double-type)
      ((l64) int64-type)
      (else int32-type)))

  ;; 把 e 的值以 t（fx / fl）的原始形式压栈；e 的类型必须已知是 t（expr-type 保证），否则先求值再 unbox.any
  ;; l64 上下文的原始算术（cleat-042）：clj-call 是 Numbers 的 long 运算时直接调用 (long …) 重载
  (define (cg-raw e mc t)
    (let ((oa (and (eq? t 'l64) (numbers-arith-args e var-types))))
      (cond ((and oa (member (car oa) numbers-long-ops)) (numbers-raw-emit-args! (cdr oa) (car oa) mc))
            ((raw-prim-invoke e t) => (lambda (e*) (cg-prim-invoke! e* mc 'raw)))
            ((raw-host-call e t) => (lambda (sl) (cg-static-host-call! sl mc 'raw)))
            (else (cg-raw/default e mc t)))))

  ;; 返回 t 的原始签名调用（PrimInvoke）的 e*，否则 #f
  (define (raw-prim-invoke e t)
    (and (memq t '(l64 fl))
         (nanopass-case (L3 Expr) e
           [(clj-call ,d0 ,d1 ,e* ...)
            (and (equal? d0 "PrimInvoke") (eq? (sig-char-raw (sig-ret (quoted-datum (car e*)))) t) e*)]
           [else #f])))

  ;; ($clj-call "PrimInvoke" #f '签名 f 实参 …)（cleat-042，JVM 的 IFn$LL 调用）：f 转换为 primifs.<签名>、callvirt invokePrim；
  ;; L / D 实参已知原始类型时原始求值，否则求值后 RT.longCast / doubleCast。ctx 为 raw 时结果原样留在栈上
  (define (cg-prim-invoke! e* mc ctx)
    (let* ((sig (quoted-datum (car e*))) (f (cadr e*)) (args (cddr e*))
           (cs (sig-params sig)) (rc (sig-ret sig))
           (iface (string-append primifs-prefix sig))
           (raws (map (lambda (a c) (let ((r (sig-char-raw c))) (and r (eq? (expr-type a var-types) r) r))) args cs))
           (codes (map (lambda (a r) (with-buffer mc (lambda () (if r (cg-raw a mc r) (cg a mc 'value)))))
                       (cons f args) (cons #f raws))))
      (define (conv! c r) (unless r (unbox-to-sig! mc c)))
      ;; 后面的操作数含 try 时先全部存临时变量（同 operands!）
      (if (exists contains-try? (cdr codes))
          (let ((temps (map (lambda (c r) (let ((t (new-local! mc (if r (type-clr r) obj)))) (emit-code! mc c) (emit! mc `(stloc ,t)) t))
                            codes (cons #f raws))))
            (emit! mc `(ldloc ,(car temps)) `(castclass ,iface))
            (for-each (lambda (t c r) (emit! mc `(ldloc ,t)) (conv! c r)) (cdr temps) cs raws))
          (begin
            (emit-code! mc (car codes))
            (emit! mc `(castclass ,iface))
            (for-each (lambda (code c r) (emit-code! mc code) (conv! c r)) (cdr codes) cs raws)))
      (emit! mc `(callvirt ,iface "invokePrim" ,(sig-param-types sig) ,(sig-char-type rc)))
      (unless (eq? ctx 'raw)
        (box-from-sig! mc rc)
        (finish-value! mc ctx))))

  ;; 静态决议、返回 long（l64）/ double（fl）的宿主调用：结果直接以原始值留在栈上（RT.longCast、Numbers 的 double 运算 …）
  (define (raw-host-call e t)
    (and (memq t '(l64 fl))
         (nanopass-case (L3 Expr) e
           [(clj-call ,d0 ,d1 ,e* ...)
            (let ((sl (host-call-signature d0 d1 e*)))
              (and (pair? sl)
                   (string=? (vector-ref (car sl) 3) (if (eq? t 'l64) "System.Int64" "System.Double"))
                   sl))]
           [else #f])))

  (define (cg-raw/default e mc t)
    (define (fallback) (cg e mc 'value) (emit! mc `(unbox.any ,(type-clr t))))
    (nanopass-case (L3 Expr) e
      [(quote ,d)
       (cond ((and (eq? t 'fx) (fixnum? d) (exact? d)) (emit! mc `(ldc.i4 ,d)))
             ((and (eq? t 'fl) (flonum-constant e)) (emit! mc `(ldc.r8 ,d)))
             ;; Int64 常量（Clojure 的整数字面量）：Lcil 文本里写成十进制整数
             ((and (eq? t 'l64) (clr-is System.Int64 d))
              (emit! mc `(ldc.i8 ,(string->number (clr-call System.Object ToString d)))))
             (else (fallback)))]
      [,x (let ((loc (hashtable-ref (mctx-vars mc) x #f)))
            (if (and loc (memq (car loc) '(tlocal targ)) (eq? (cddr loc) t))
                (begin (ref-var! mc x #f) (emit! mc (if (eq? (car loc) 'targ) `(ldarg ,(cadr loc)) `(ldloc ,(cadr loc)))))
                (fallback)))]
      [(primcall ,pr ,e* ...)
       (if (and (eq? (expr-type e var-types) t) (raw-prim! pr e* mc))
           (void)
           (fallback))]
      [(call ,e0 ,e* ...)
       (let ((op (inline-op e0 (length e*))))
         (if (and op (memq (cdr op) '(fl generic)) (eq? t 'fl) (memq (car op) '(add sub mul div))
                  (for-all (lambda (a) (eq? (expr-type a var-types) 'fl)) e*))
             (begin
               (for-each (lambda (a) (cg-raw a mc 'fl)) e*)
               (emit! mc (case (car op) ((add) '(add)) ((sub) '(sub)) ((mul) '(mul)) (else '(div)))))
             (fallback)))]
      [else (fallback)]))

  ;; Numbers 的 long 运算（cleat-042）：实参全部已知 l64 时以原始 int64 求值实参、调用 (long …) 重载，
  ;; 栈上留 int64（long 运算）或 CLR bool（比较）；返回 'l64 / 'bool。不适用时返回 #f 且不生成代码
  (define (numbers-raw-emit! e mc)
    (let ((oa (numbers-arith-args e var-types)))
      (and oa
           (begin (numbers-raw-emit-args! (cdr oa) (car oa) mc)
                  (if (member (car oa) numbers-bool-ops) 'bool 'l64)))))

  (define (numbers-raw-emit-args! args op mc)
    (for-each (lambda (x) (cg-raw x mc 'l64)) args)
    (emit! mc `(call "clojure.lang.Numbers" ,op ,(map (lambda (x) int64-type) args)
                     ,(if (member op numbers-bool-ops) bool-type int64-type))))

  ;; 宿主方法调用的编译期静态决议（cleat-042 第 3 项）：CallStatic / CallInstance 的类型在 Cleat.Core 中、重载能静态选定
  ;; （ClojureStaticCalls.Resolve，JVM Clojure 的规则）时直接 call / callvirt，不经运行时的 Reflector。
  ;; 返回 #(声明类型 成员名 #(形参类型 …) 返回类型 虚调用? 种类) 或 #f；种类 "method" / "field" / "volatile-field"（无实参的字段读）
  (define static-calls-type "Cleat.Scheme.Clojure.ClojureStaticCalls")
  ;; e* 的布局：CallStatic (t 名 gen 实参 …)、CallInstance (t 名 实例 gen 实参 …)、GetStaticField (t 名)、
  ;; GetInstanceField (t 名 实例)。返回 (static? 实例或 #f 实参列表)，不是这四种时 #f
  (define (host-call-layout d0 d1 e*)
    (cond ((and (equal? d0 "CallStatic") (eqv? d1 3) (>= (length e*) 3)) (list #t #f (list-tail e* 3)))
          ((and (equal? d0 "CallInstance") (eqv? d1 4) (>= (length e*) 4)) (list #f (list-ref e* 2) (list-tail e* 4)))
          ((and (equal? d0 "GetStaticField") (not d1) (= (length e*) 2)) (list #t #f '()))
          ((and (equal? d0 "GetInstanceField") (not d1) (= (length e*) 3)) (list #f (list-ref e* 2) '()))
          (else #f)))

  ;; env：推断中为不动点的类型表（实参类型还没定时返回 'pending），代码生成时为 var-types——改用 sound-types 选重载
  (define (host-call-signature d0 d1 e* . env)
    (let ((layout (host-call-layout d0 d1 e*))
          (env (if (or (null? env) (eq? (car env) var-types)) sound-types (car env))))
      (and layout
           (let ((t (quoted-datum (car e*)))
                 (name (quoted-datum (cadr e*)))
                 (gen (if (member d0 '("CallStatic" "CallInstance")) (quoted-datum (list-ref e* (if (car layout) 2 3))) '())))
             (and (clr-is System.Type t) (string? name) (or (null? gen) (not gen))
                  (let ((ats (map (lambda (a) (static-arg-type a env)) (caddr layout))))
                    (if (memq 'pending ats)
                        'pending
                        (let ((sig (if (member d0 '("CallStatic" "CallInstance"))
                                       (clr-static-call Cleat.Scheme.Clojure.ClojureStaticCalls Resolve
                                                        t name (list->vector ats) (car layout))
                                       (clr-static-call Cleat.Scheme.Clojure.ClojureStaticCalls ResolveField t name (car layout)))))
                          (and (> (vector-length sig) 0) (cons sig layout))))))))))

  ;; 静态决议的宿主调用的结果类型：l64 / fl / top（实参类型未定）/ #f
  (define (host-call-type d0 d1 e* env)
    (let ((sl (host-call-signature d0 d1 e* env)))
      (cond ((eq? sl 'pending) 'top)
            ((not sl) #f)
            (else (let ((ret (vector-ref (car sl) 3)))
                    (cond ((string=? ret "System.Int64") 'l64)
                          ((string=? ret "System.Double") 'fl)
                          (else #f)))))))

  ;; 实参的静态类型名（JVM Clojure 的 getJavaClass：数字与字符串字面量、推断为 long / double 的变量与表达式），
  ;; 未知为 #f，推断中还没定为 'pending
  (define (static-arg-type e env)
    (nanopass-case (L3 Expr) e
      [(quote ,d) (cond ((clr-is System.Int64 d) "System.Int64")
                        ((clr-is System.Int32 d) "System.Int32")   ; (int 1) 折叠成的常量（cleat-043）
                        ((flonum? d) "System.Double")
                        ((string? d) "System.String")
                        (else #f))]
      ;; 静态决议的宿主调用：返回 int / bool / char / String 时实参的类型就是它（(int x) 即 RT.intCast，cleat-043：
      ;; 选 gen-class 的 foo(Object, Int32) 这类重载，同 JVM 的 getJavaClass）
      [(clj-call ,d0 ,d1 ,e* ...)
       (guard (equal? d0 "CallStatic"))
       (let ((sl (host-call-signature d0 d1 e* env)))
         (cond ((eq? sl 'pending) 'pending)
               ((and sl (member (vector-ref (car sl) 3) '("System.Int32" "System.Boolean" "System.Char" "System.String")))
                (vector-ref (car sl) 3))
               (else (case (expr-type e env) ((l64) "System.Int64") ((fl) "System.Double") ((top) 'pending) (else #f)))))]
      [else (case (expr-type e env)
              ((l64) "System.Int64")
              ((fl) "System.Double")
              ((top) 'pending)
              (else #f))]))

  ;; sl：host-call-signature 的结果 (签名 static? 实例 实参列表)
  (define (cg-static-host-call! sl mc ctx)
    (let* ((sig (car sl)) (static? (cadr sl)) (inst (caddr sl)) (args (cadddr sl))
           (decl (vector-ref sig 0)) (name (vector-ref sig 1))
           (pts (vector->list (vector-ref sig 2))) (ret (vector-ref sig 3)) (virt? (vector-ref sig 4)))
      ;; 栈顶 object → 形参类型 pt
      (define (convert! pt)
        (cond ((string=? pt "System.Object") (void))
              ((assoc pt '(("System.Int32" . "ArgInt32") ("System.Int64" . "ArgInt64") ("System.Double" . "ArgDouble")
                           ("System.Boolean" . "ArgBoolean") ("System.Char" . "ArgChar")))
               => (lambda (c) (emit! mc `(call ,static-calls-type ,(cdr c) (,obj) ,pt))))
              (else (emit! mc `(castclass ,pt)))))
      ;; 操作数先各自求出 object；后面的操作数含 try 时（binding 等）全部先存 object 临时变量（同 operands!），
      ;; 再逐个取回并转换——转换后的原始值不能进 object 临时变量
      ;; 已知 l64 的实参传给 long / int 形参、已知 fl 的传给 double 形参：原始求值（int 经 RT.intCast，与 Reflector 的转换
      ;; 相同），不装箱。返回 l64 / fl 或 #f
      (define (raw-long? x pt)
        (let ((t (expr-type x var-types)))
          (cond ((and (eq? t 'l64) (member pt '("System.Int64" "System.Int32"))) 'l64)
                ((and (eq? t 'fl) (string=? pt "System.Double")) 'fl)
                (else #f))))
      (let* ((opnds (append (if static? '() (list inst)) args))
             (types (append (if static? '() (list 'instance)) pts))
             (raws (map (lambda (x pt) (and (not (eq? pt 'instance)) (raw-long? x pt))) opnds types))
             (codes (map (lambda (x raw?) (with-buffer mc (lambda () (if raw? (cg-raw x mc raw?) (cg x mc 'value))))) opnds raws))
             (conv! (lambda (pt raw?)
                      (cond ((eq? pt 'instance)
                             (emit! mc `(ldstr ,name) `(call ,static-calls-type "Instance" (,obj ,obj) ,obj) `(castclass ,decl)))
                            ((and raw? (string=? pt "System.Int32"))
                             (emit! mc `(call "clojure.lang.RT" "intCast" (,int64-type) ,int32-type)))
                            (raw? (void))
                            (else (convert! pt))))))
        (if (and (pair? codes) (exists contains-try? (cdr codes)))
            (let ((temps (map (lambda (c raw?) (let ((t (new-local! mc (if raw? (type-clr raw?) obj)))) (emit-code! mc c) (emit! mc `(stloc ,t)) t))
                              codes raws)))
              (for-each (lambda (t pt raw?) (emit! mc `(ldloc ,t)) (conv! pt raw?)) temps types raws))
            (for-each (lambda (c pt raw?) (emit-code! mc c) (conv! pt raw?)) codes types raws)))
      (let ((kind (vector-ref sig 5)))
        (if (string=? kind "method")
            (emit! mc `(,(if virt? 'callvirt 'call) ,decl ,name ,pts ,ret))
            (begin
              (when (string=? kind "volatile-field") (emit! mc '(volatile.)))
              (emit! mc (if static? `(ldsfld ,decl ,name) `(ldfld ,decl ,name))))))
      (cond
        ((eq? ctx 'raw) (void))           ; cg-raw：long / double 结果原样留在栈上
        ((string=? ret "System.Boolean") (finish-bool! mc ctx))
        (else
            (cond ((string=? ret "System.Void") (emit! mc '(ldnull)))
                  ((string=? ret "System.Object") (emit! mc `(call ,static-calls-type "RetObject" (,obj) ,obj)))
                  ((string=? ret "System.Int32") (emit! mc `(call ,static-calls-type "RetInt32" (,int32-type) ,obj)))
                  ((member ret '("System.Int64" "System.Double" "System.Char")) (emit! mc `(box ,ret)))
                  (else (void)))
            (finish-value! mc ctx)))))

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
    (case t
      ((fl) (emit! mc `(box ,double-type)))
      ((l64) (emit! mc `(box ,int64-type)))
      (else (emit! mc `(call ,rt-type "Box" (,int32-type) ,obj)))))

  ;; ———————————————— 变量 ————————————————

  (define (var-location mc x)
    (or (hashtable-ref (mctx-vars mc) x #f)
        (error 'codegen "unbound variable" x)))

  ;; ———————————————— 局部变量清除（cleat-042 第 5 项，JVM 的 locals clearing）————————————————
  ;; CLR 的 tier-0 代码把方法的全部局部变量与参数都报告为活的（优化后的代码才按活跃区间），只调用一次的长调用
  ;; （reduce → coll-reduce → seq-reduce …）一直停在 tier-0：各帧的 coll 参数、循环变量抓着惰性序列的头，整条序列无法回收。
  ;; 做法：方法体里按生成顺序数每个变量的引用，生成最后一个引用时读出后把槽置 null（ldnull stloc / starg）。
  ;; if 的两支按生成顺序数，前一支里不清（保守）；循环体外绑定、体内引用的变量在循环里不清（下一轮还要用）；
  ;; try 的处理器 / finally 在体之后生成，引用它们的变量不会在体里被清。原始值的局部变量不必清，只计数
  (define (init-clearing! mc body)
    (let ((uses (mctx-uses mc)))
      (walk-var-refs body (lambda (x) (hashtable-update! uses x (lambda (n) (+ n 1)) 0)) (lambda (x) (void)))))

  ;; 引用变量 x：计数减一；load? 为真时压栈；是最后一个引用、不在钉住的循环里、是 object 局部变量或参数时清掉槽
  (define (ref-var! mc x load?)
    (let* ((uses (mctx-uses mc))
           (n (hashtable-ref uses x #f))
           (last? (and n (begin (hashtable-set! uses x (- n 1)) (= n 1))
                       (= 0 (hashtable-ref (mctx-pinned mc) x 0)))))
      (let ((loc (and last? (hashtable-ref (mctx-vars mc) x #f))))
        (cond
          ;; 最后一次读：经地址取出并清空（Locals.Take），不留求值栈上的副本给 tier-0 溢出成活着的临时变量
          ((and load? loc (eq? (car loc) 'local)) (emit! mc `(ldloca ,(cdr loc))) (take-slot! mc))
          ((and load? loc (eq? (car loc) 'arg) (clear-args-by-ref)) (emit! mc `(ldarga ,(cdr loc))) (take-slot! mc))
          (else
           (when load? (load-var! mc x))
           (when last? (clear-var! mc x)))))))

  ;; 参数经地址取出只对 object 形参（Clojure fn 的方法）；deftype 方法的形参有类型，退回读后置 null
  (define clear-args-by-ref (make-parameter #t))
  ;; 做局部变量清除的方法（Clojure fn / deftype 方法）才清临时变量；Scheme 代码的 jump / 打包临时变量照旧
  (define (clearing? mc) (> (hashtable-size (mctx-uses mc)) 0))

  (define (take-slot! mc)
    (emit! mc `(call "Cleat.Scheme.Locals" "Take" (,(string-append obj "&")) ,obj)))

  (define (clear-var! mc x)
    (let ((loc (hashtable-ref (mctx-vars mc) x #f)))
      (when loc
        (case (car loc)
          ((local) (emit! mc '(ldnull) `(stloc ,(cdr loc))))
          ((arg) (emit! mc '(ldnull) `(starg ,(cdr loc))))
          (else (void))))))

  ;; 分支 e 里各（做清除的）变量的引用数：((变量 . 个数) …)
  (define (branch-ref-counts mc e)
    (let ((uses (mctx-uses mc)) (t (make-eq-hashtable)))
      (if (= (hashtable-size uses) 0)
          '()
          (begin
            (walk-var-refs e (lambda (x) (when (hashtable-contains? uses x) (hashtable-update! t x (lambda (n) (+ n 1)) 0)))
                           (lambda (x) (void)))
            (let-values (((ks vs) (hashtable-entries t)))
              (map cons (vector->list ks) (vector->list vs)))))))

  (define (adjust-uses! mc counts op)
    (let ((uses (mctx-uses mc)))
      (for-each (lambda (p) (hashtable-update! uses (car p) (lambda (n) (op n (cdr p))) 0)) counts)))

  ;; 只读一次的 object 临时变量：读出后置 null
  (define (ldloc-clear! mc t)
    (if (clearing? mc)
        (begin (emit! mc `(ldloca ,t)) (take-slot! mc))
        (emit! mc `(ldloc ,t))))

  ;; 遍历 L3 表达式的变量引用（on-ref）与约束（on-bind）。不进入嵌套 lambda 的体（另一个方法），
  ;; 但闭包创建时读的自由变量算引用（lambda 全部自由变量；letrec 的 lambda 除去自己）
  (define (walk-var-refs e on-ref on-bind)
    (define (free-of le self)
      (nanopass-case (L3 LambdaExpr) le
        [(lambda ,nm (,x* ...) ,cl* ...) (for-each on-ref (if self (remq self x*) x*))]))
    (let walk ((e e))
      (nanopass-case (L3 Expr) e
        [,x (on-ref x)]
        [(if ,e0 ,e1 ,e2) (walk e0) (walk e1) (walk e2)]
        [(begin ,e* ... ,e) (for-each walk e*) (walk e)]
        [(set! ,x ,e) (walk e)]
        [(gset! ,s ,e) (walk e)]
        [(mset! ,x ,e) (walk e)]
        [(primcall ,pr ,e* ...) (for-each walk e*)]
        [(lambda ,nm (,x* ...) ,cl* ...) (for-each on-ref x*)]
        [(letrec ([,x* ,le*] ...) ,body)
         (for-each on-bind x*)
         (for-each (lambda (x le) (free-of le x)) x* le*)
         (walk body)]
        [(let ([,x* ,e*] ...) ,body) (for-each walk e*) (for-each on-bind x*) (walk body)]
        [(library ,d ([,x* ,s* ,e*] ...) ,body) (for-each walk e*) (walk body)]
        [(call ,e ,e* ...) (walk e) (for-each walk e*)]
        [(and ,e* ...) (for-each walk e*)]
        [(or ,e* ...) (for-each walk e*)]
        [(throw ,e) (walk e)]
        [(try-finally ,e0 ,e1) (walk e0) (walk e1)]
        [(try-catch ,e (,d* ,e*) ...) (walk e) (for-each walk e*)]
        [(escape-frame ,x ,body) (on-bind x) (walk body)]
        [(try-handle ,x ,body0 ,body1) (walk body0) (on-bind x) (walk body1)]
        [(label ,x (,x* ...) ,body0 ,body1) (for-each on-bind x*) (walk body0) (walk body1)]
        [(jump ,x ,e* ...) (for-each walk e*)]
        [(host-call ,d0 ,d1 ,st ,e* ...) (for-each walk e*)]
        [(host-field-get ,d0 ,d1 ,st ,e* ...) (for-each walk e*)]
        [(host-field-set ,d0 ,d1 ,st ,e* ...) (for-each walk e*)]
        [(host-new ,d ,e* ...) (for-each walk e*)]
        [(host-new-array ,d ,e) (walk e)]
        [(host-cast ,d ,e) (walk e)]
        [(host-is ,d ,e) (walk e)]
        [(unsupported ,d ,e* ...) (for-each walk e*)]
        [(box ,e) (walk e)]
        [(unbox ,x) (on-ref x)]
        [(set-box! ,x ,e) (on-ref x) (walk e)]
        [(clj-var-set! ,d ,e) (walk e)]
        [(clj-def ,d ,st0 ,st1 ,e0 ,e1) (walk e0) (walk e1)]
        [(clj-invoke ,e ,e* ...) (walk e) (for-each walk e*)]
        [(clj-keyword-invoke ,d ,e ,e* ...) (walk e) (for-each walk e*)]
        [(clj-call ,d0 ,d1 ,e* ...) (for-each walk e*)]
        [(clj-static-call ,d0 ,d1 ,e* ...) (for-each walk e*)]
        [(clj-case ,d ,e0 ,e1 ,e* ...) (walk e0) (walk e1) (for-each walk e*)]
        [(clj-deftype ,d (,x* ...) ,le* ...)
         (when (deftype-reify? d) (for-each on-ref (deftype-caps x* le*)))]
        [else (void)])))

  ;; 循环体 body 里引用、在体外绑定的变量（params 是这个循环自己的形参，每轮重新赋值，不算）
  (define (loop-outer-refs body params)
    (let ((refs (make-eq-hashtable)) (bound (make-eq-hashtable)))
      (for-each (lambda (p) (hashtable-set! bound p #t)) params)
      (walk-var-refs body (lambda (x) (hashtable-set! refs x #t)) (lambda (x) (hashtable-set! bound x #t)))
      (filter (lambda (x) (not (hashtable-ref bound x #f))) (vector->list (hashtable-keys refs)))))

  (define (load-var! mc x)
    (let ((loc (var-location mc x)))
      (case (car loc)
        ((arg) (emit! mc `(ldarg ,(cdr loc))))
        ((local) (emit! mc `(ldloc ,(cdr loc))))
        ((tlocal) (emit! mc `(ldloc ,(cadr loc))) (box-raw! mc (cddr loc)))
        ((targ) (emit! mc `(ldarg ,(cadr loc))) (box-raw! mc (cddr loc)))
        ((field dfield) (emit! mc '(ldarg 0) `(ldfld ,(mctx-class mc) ,(cdr loc))))
        ((escape) (error 'codegen "local escape continuation used as a value" x))
        ((self) (cond
                  ((pair? (cdr loc)) (emit! mc `(ldsfld ,(cadr loc) ,(cddr loc))))   ; 034d：共享持有类中的过程
                  ((mctx-static? mc) (emit! mc `(ldsfld ,(mctx-class mc) "Instance")))
                  (else (emit! mc '(ldarg 0)))))
        (else (error 'codegen "unbound variable location" x)))))

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
      (and loc (memq (car loc) '(tlocal targ)) (cddr loc))))

  ;; 实参都是 (host-cast T e) 时返回类型名列表（Lcil 的实参类型提示，发射器据此静态决议重载，iron-033）；否则 #f
  (define (cast-types e*)
    (and (pair? e*)
         (let loop ((e* e*) (acc '()))
           (if (null? e*)
               (reverse acc)
               (let ((d (nanopass-case (L3 Expr) (car e*) [(host-cast ,d ,e) d] [else #f])))
                 (and d (loop (cdr e*) (cons d acc))))))))

  ;; let 单个绑定的求值代码：拆箱的变量（且不是大过程）用原始形式（iron-071 的 let 逐个绑定、带收窄事实时用）
  (define (binding-code mc x e)
    (with-buffer mc
      (lambda ()
        (let ((t (and (not (mctx-big? mc)) (hashtable-ref var-types x #f))))
          (if t (cg-raw e mc t) (cg e mc 'value))))))

  ;; ———————————————— 表达式 ————————————————

;; 局部逃逸（cleat-076，照 skeg iron-068）：k 不是对象，只是"结果变量 + 出口标签"。体在值上下文里求值后存入结果变量；
  ;; (k v) 在 cg-call 里编译成"v 存入结果变量，br 到出口"，中间隔着 try 区域时用 leave（运行 finally）。
  ;; lower-loops 保证 k 只出现在空栈位置（或体内有 try 时整个帧从空栈开始），所以出口处的栈深度一致
  (define (cg-local-escape! x body mc ctx)
    (let ((tmp (new-local! mc obj)) (after (new-label! mc)))
      (hashtable-set! (mctx-vars mc) x (list 'escape tmp after (mctx-try-depth mc)))
      (cg body mc 'value)
      (emit! mc `(stloc ,tmp) `(label ,after) `(ldloc ,tmp))
      (finish-value! mc ctx)))

  ;; (k v) 的 k 是局部逃逸时返回它的 (escape 结果变量 出口 try 深度)
  (define (local-escape-target e0 mc argc)
    (and (= argc 1)
         (nanopass-case (L3 Expr) e0
           [,x (let ((loc (hashtable-ref (mctx-vars mc) x #f))) (and loc (eq? (car loc) 'escape) loc))]
           [else #f])))

  ;; 原始签名的方法里尾位置的叶子表达式（不是 if / let / label … 这类把上下文传给子表达式的）类型已经是返回的原始类型时
  ;; 直接以原始值返回（cleat-042）；其余照常，finish-value! 在尾位置转换
  (define (cg e mc ctx)
    (let ((rt (and (eq? ctx 'tail) (mctx-ret-type mc))))
      (if (and rt (raw-tail-leaf? e) (eq? (expr-type e var-types) rt))
          (begin (cg-raw e mc rt) (emit! mc '(ret)))
          (cg* e mc ctx))))

  ;; Clojure 的 if 的测试是 (let ((t X)) (if (null? t) '#f t))（nil 与 false 都为假）。X 是返回 CLR bool 的调用
  ;; （静态决议的宿主调用、可 isinst 的 instance?）时它不会是 nil：直接返回 X，在 test 上下文求值，不装箱再判断（cleat-042）
  (define (truthy-bool-call e)
    (define (same? a v) (nanopass-case (L3 Expr) a [,x (eq? x v)] [else #f]))
    (define (null-test? a v)
      (nanopass-case (L3 Expr) a
        [(primcall ,pr ,e* ...) (and (eq? pr 'null?) (= (length e*) 1) (same? (car e*) v))]
        [else #f]))
    (nanopass-case (L3 Expr) e
      [(let ([,x* ,e*] ...) ,body)
       (and (= (length x*) 1)
            (let ((v (car x*)) (init (car e*)))
              (nanopass-case (L3 Expr) body
                [(if ,e0 ,e1 ,e2)
                 (and (null-test? e0 v)
                      (eq? (quoted-datum e1) #f)
                      (same? e2 v)
                      (bool-call? init)
                      init)]
                [else #f])))]
      [else #f]))

  (define (bool-call? e)
    (nanopass-case (L3 Expr) e
      [(clj-call ,d0 ,d1 ,e* ...)
       (or (let ((sl (host-call-signature d0 d1 e*)))
             (and (pair? sl) (string=? (vector-ref (car sl) 3) "System.Boolean")))
           (and (equal? d0 "IsInstanceOf") (= (length e*) 2)
                (let ((t (quoted-datum (car e*))))
                  (and (clr-is System.Type t)
                       (> (string-length (clr-static-call Cleat.Scheme.Clojure.ClojureStaticCalls IsinstName t)) 0)))))]
      [else #f]))

  (define (raw-tail-leaf? e)
    (nanopass-case (L3 Expr) e
      [(if ,e0 ,e1 ,e2) #f]
      [(begin ,e* ... ,e) #f]
      [(let ([,x* ,e*] ...) ,body) #f]
      [(letrec ([,x* ,le*] ...) ,body) #f]
      [(label ,x (,x* ...) ,body0 ,body1) #f]
      [(jump ,x ,e* ...) #f]
      [(throw ,e) #f]
      [(try-finally ,e0 ,e1) #f]
      [(try-catch ,e (,d* ,e*) ...) #f]
      [(escape-frame ,x ,body) #f]
      [(try-handle ,x ,body0 ,body1) #f]
      [else #t]))

  (define (cg* e mc ctx)
    (nanopass-case (L3 Expr) e
      [,x (ref-var! mc x (not (eq? ctx 'effect))) (unless (eq? ctx 'effect) (finish-value! mc ctx))]
      [(label ,x (,x* ...) ,body0 ,body1)
       ;; 局部循环（iron-034b，lower-loops 产生）：形参是 IL 局部变量，
       ;; jump 给它们赋值后 br 到循环体。body0 一次都没跳到循环体时循环体不可达，不生成
       ;; （否则无条件分支之后的标签处栈深无法单遍推导，ECMA-335 III.1.7.5）
       (let ((s (loop-spec-params mc x x* body0 body1)))
        (if s
         (cg-spec-label! x x* body0 body1 s mc ctx)
         (begin
       (for-each (lambda (p) (bind-local! mc p)) x*)
       (let ((lf (new-label! mc)) (end-l (new-label! mc)))
         (hashtable-set! (mctx-labels mc) x (cons lf x*))
         (let ((code (with-buffer mc (lambda () (cg body0 mc ctx)))))
           (emit-code! mc code)
           (when (branches-to? code lf)
             (unless (eq? ctx 'tail) (emit! mc `(br ,end-l)))
             (emit! mc `(label ,lf))
             ;; 体外绑定、体内引用的变量在循环里钉住（不清除）
             (let ((outer (loop-outer-refs body1 x*)) (pinned (mctx-pinned mc)))
               (for-each (lambda (v) (hashtable-update! pinned v (lambda (n) (+ n 1)) 0)) outer)
               (cg body1 mc ctx)
               (for-each (lambda (v) (hashtable-update! pinned v (lambda (n) (- n 1)) 0)) outer))
             (unless (eq? ctx 'tail) (emit! mc `(label ,end-l)))))))))]
      [(jump ,x ,e* ...)
       (if (vector? (hashtable-ref (mctx-labels mc) x #f))
           (cg-spec-jump! (hashtable-ref (mctx-labels mc) x #f) e* mc ctx)
       (let* ((entry (or (hashtable-ref (mctx-labels mc) x #f) (error 'codegen "jump to unknown label" x)))
              ;; 实参先全部求值到临时变量再写入形参（并行赋值）；有类型的形参用原始值的临时变量
              (temps (map (lambda (p a)
                            (let ((rt (var-raw-type mc p)))
                              (if rt (cg-raw a mc rt) (cg a mc 'value))
                              (let ((t (new-local! mc (if rt (type-clr rt) obj)))) (emit! mc `(stloc ,t)) t)))
                          (cdr entry) e*)))
         (for-each (lambda (p t) (if (var-raw-type mc p) (emit! mc `(ldloc ,t)) (ldloc-clear! mc t)) (store-var! mc p)) (cdr entry) temps)
         (emit! mc `(br ,(car entry)))
         ;; 之后的代码不可达；补一个值保持栈形状一致（同 throw）
         (case ctx
           ((value) (emit! mc '(ldnull)))
           ((test) (emit! mc '(ldc.i4 0)))
           (else (void)))))]
      [(escape-frame ,x ,body)
       ;; 局部逃逸的 call-with-return（cleat-076，照 skeg iron-068；lower-loops 产生）：k 只在空栈位置被直接调用，
       ;; 不创建 EscapeContinuation，(k v) 是存值后 br / leave 到帧的出口，见 cg-local-escape!
       (cg-local-escape! x body mc ctx)]
      [(try-handle ,x ,body0 ,body1)
       ;; 内联的 with-clr-exception-handler（cleat-076，照 skeg iron-067；lower-loops 产生，处理器与体都是字面 lambda）：
       ;; try { 值 = body0 } catch (Exception e) { e 是 ContinuationException ? rethrow : x = e; leave CAUGHT }；
       ;; CAUGHT: 值 = body1（在 try 区域之外求值，与原语的结果相同）。不为处理器和体分配闭包
        (bind-local! mc x)
        (let* ((tmp (new-local! mc obj))
               (exc (new-local! mc obj))
               (after (new-label! mc)) (caught (new-label! mc)) (own (new-label! mc)))
          (let ((b (in-try mc (lambda () (cg body0 mc 'value) (emit! mc `(stloc ,tmp) `(leave ,after)))))
                (handler
                  (with-buffer mc
                    (lambda ()
                      (emit! mc `(stloc ,exc)
                                `(ldloc ,exc) `(isinst ,continuation-exn-type) `(brfalse ,own)
                                '(rethrow)
                                `(label ,own)
                                `(ldloc ,exc))
                      (store-var! mc x)
                      (emit! mc `(leave ,caught))))))
            (emit! mc `(try (body ,@b)
                            (catch ,exception-type ,@handler))
                   `(label ,caught))
            (cg body1 mc 'value)
            (emit! mc `(stloc ,tmp) `(label ,after) `(ldloc ,tmp))
            (finish-value! mc ctx)))]
      [(quote ,d)
       (case ctx
         ((effect) (void))
         ((test) (emit! mc `(ldc.i4 ,(if d 1 0))))
         (else (load-constant! mc d) (finish-value! mc ctx)))]
      [(if ,e0 ,e1 ,e2)
       (let ((k (known-test e0)))
         (if k
             ;; 测试的结果已知（iron-071：类型谓词作用在类型已知的变量上），只生成那一支（没生成的一支里的引用不再扣减，
             ;; 局部变量清除只会更保守）
             (cg (if (car k) e1 e2) mc ctx)
             (let ((else-l (new-label! mc)) (end-l (new-label! mc)))
               (cg (or (truthy-bool-call e0) e0) mc 'test)
               (emit! mc `(brfalse ,else-l))
               (let-values (((t-facts f-facts) (test-facts e0 mc)))
                 ;; 局部变量清除按分支：生成 then 支时先扣掉 else 支的引用数，then 支里的最后一个引用在 if 之后没有引用时就清；
                 ;; 生成 else 支前加回（if 之后的计数与不分支时相同）
                 (let ((c2 (branch-ref-counts mc e2)))
                   (adjust-uses! mc c2 -)
                   (with-facts t-facts (lambda () (cg e1 mc ctx)))
                   (adjust-uses! mc c2 +))
                 (unless (eq? ctx 'tail) (emit! mc `(br ,end-l)))
                 (emit! mc `(label ,else-l))
                 (with-facts f-facts (lambda () (cg e2 mc ctx))))
               (unless (eq? ctx 'tail) (emit! mc `(label ,end-l))))))]
      [(begin ,e* ... ,e)
       ;; 前几项无条件执行的操作带出的事实（iron-071）在后面成立
       (let ((undo (fold-left (lambda (undo x) (cg x mc 'effect) (append (push-facts! (effect-facts x mc)) undo)) '() e*)))
         (cg e mc ctx)
         (pop-facts! undo))]
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
       ;; iron-070：生成各类之前先给组内每个同步 lambda 定下类名 / 共享槽并登记，组内的调用（含自身递归）
       ;; 生成时就能直接调子句方法，不经 Calls.Proc 与虚方法 Invoke（letrec 绑定的变量不会被赋值）
       ;; 两趟：先全组登记，前面的过程体里调后面的过程时也已登记
       (let* ((presets (map (lambda (x le) (local-known-preset! (mctx-unit mc) x le)) x* le*))
              (classes (map (lambda (x le k) (if k (lambda-class! le mc x #f k) (lambda-class! le mc x #f)))
                            x* le* presets)))
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
       ;; 前面右边无条件执行的操作带出的事实（iron-071）在后面的右边与体里成立
       (let loop ((xs x*) (es e*) (codes '()) (undo '()))
         (if (null? xs)
             (begin
               (for-each (lambda (x code) (emit-code! mc code) (bind-value! mc x)) x* (reverse codes))
               (cg body mc ctx)
               (pop-facts! undo))
             (let ((code (binding-code mc (car xs) (car es))))
               (loop (cdr xs) (cdr es) (cons code codes) (append (push-facts! (effect-facts (car es) mc)) undo)))))]
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
         ;; 逃逸续延（call-with-return，cleat-058）的控制流异常一律放行：按 CLR 类型的子句（Clojure 的 catch Exception 等）
         ;; 前面先接住 ContinuationException 原样 rethrow
         (emit! mc `(try (body ,@body)
                         ,@(if (exists (lambda (d) d) d*) `((catch ,continuation-exn-type (rethrow))) '())
                         ,@handlers))
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
       ;; ClojureOps 的静态方法：参数与返回都是 object；d1 为固定参数个数时其余实参打包成 object[]。
       ;; Numbers 的 int64 算术（cleat-042）在实参类型全知时直接发 int64 原始运算
       (cond
         ((equal? d0 "PrimInvoke") (cg-prim-invoke! e* mc ctx))
         ;; (await* t) 在运行时 async 的方法里（cleat-043）：任意 Task / ValueTask → Task，AsyncHelpers.Await 挂起到它完成，再取结果
         ((and (equal? d0 "AwaitAsync") (= (length e*) 1))
          (let ((task "System.Threading.Tasks.Task") (support "Cleat.Scheme.Clojure.AsyncSupport"))
            (cg (car e*) mc 'value)
            (emit! mc `(call ,support "ToTask" (,obj) ,task) '(dup)
                   `(call "System.Runtime.CompilerServices.AsyncHelpers" "Await" (,task) ,void-type)
                   `(call ,support "ResultOf" (,task) ,obj))
            (finish-value! mc ctx)))
         ;; (instance? T x)，T 是常量且能按名字引用：isinst（cleat-042；内联的守卫、用户代码的 instance?）
         ((and (equal? d0 "IsInstanceOf") (= (length e*) 2)
               (let ((t (quoted-datum (car e*))))
                 (and (clr-is System.Type t)
                      (let ((n (clr-static-call Cleat.Scheme.Clojure.ClojureStaticCalls IsinstName t)))
                        (and (> (string-length n) 0) n)))))
          => (lambda (n)
               (cg (cadr e*) mc 'value)
               (emit! mc `(isinst ,n) '(ldnull) '(cgt.un))
               (finish-bool! mc ctx)))
         (else
       (case (numbers-raw-emit! e mc)
         ((bool) (finish-bool! mc ctx))
         ((l64) (box-raw! mc 'l64) (finish-value! mc ctx))
         (else
           (let ((sig (host-call-signature d0 d1 e*)))
            (if sig
             (cg-static-host-call! sig mc ctx)
             (let ((argc (length e*)))
               (operands! mc (map (lambda (x) (lambda () (cg x mc 'value))) e*))
               (when d1 (pack-array! mc (- argc d1)))
               (emit! mc `(call ,clojure-ops-type ,d0 ,(if d1 (append (objs d1) (list obj-array)) (objs argc)) ,obj))
               (finish-value! mc ctx))))))))]
      [(clj-static-call ,d0 ,d1 ,e* ...)
       ;; 受保护的直接链接（cleat-033，designs/04 §6）：e0 取 Var 的当前根。实参先存入临时变量，根还是函数类 d0 时
       ;; 以根作 self 直接 call d0::invokeStatic；被 with-redefs / 重定义换掉的根（别的类）走普通
       ;; IFn.invoke（d1 为 Clojure 层的参数个数，fast 慢两条路的返回值在终点汇合）
       (let* ((argc (length e*))                       ; 实参个数：e0 是 Var 读，e1 起才是实参
              (n (- argc 1))
              (root (new-local! mc obj))
              (defs (map (lambda (x) (new-local! mc obj)) (cdr e*)))
              (slow (new-label! mc))
              (end (new-label! mc)))
         (cg (car e*) mc 'value)
         (emit! mc `(stloc ,root))
         (for-each (lambda (x l) (cg x mc 'value) (emit! mc `(stloc ,l))) (cdr e*) defs)
         (emit! mc `(ldloc ,root) `(isinst ,d0) `(brfalse ,slow))
         ;; self 就是 Var 的根（已确认是 d0 的实例；普通 invoke 的 this 也是它）——不再每次调用 newobj（cleat-042）。
         ;; 临时变量在两条路上各读一次，读后置 null（局部变量清除）
         (ldloc-clear! mc root)
         (for-each (lambda (l) (ldloc-clear! mc l)) defs)
         (emit! mc `(call ,d0 "invokeStatic" ,(objs (+ n 1)) ,obj) `(br ,end))
         (emit! mc `(label ,slow))
         (ldloc-clear! mc root)
         (emit! mc `(castclass ,ifn-type))
         (for-each (lambda (l) (ldloc-clear! mc l)) defs)
         (emit! mc `(callvirt ,ifn-type "invoke" ,(objs n) ,obj))
         (emit! mc `(label ,end))
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
       (let ((c (cond ((deftype-interface? d) (interface-class! mc d))
                      ((deftype-proxy? d) (proxy-class! mc d))
                      ((deftype-genclass? d) (genclass-class! mc d))
                      (else (deftype-class! mc d x* le*)))))
         (if (deftype-reify? d)
             (begin
               (for-each (lambda (v) (ref-var! mc v #t)) (cdr c))
               (emit! mc '(ldnull) `(newobj ,(car c) ,(append (objs (length (cdr c))) (list obj)))))
             (emit! mc `(ldtoken ,(car c)) `(call ,type-type "GetTypeFromHandle" (,type-handle-type) ,type-type)
                    `(call ,clojure-ops-type "DefineType" (,obj) ,obj)))
         (finish-value! mc ctx))]
      [(box ,e)
       (cg e mc 'value)
       (emit! mc `(newobj ,box-type (,obj)))
       (finish-value! mc ctx)]
      [(unbox ,x)
       (if (eq? ctx 'effect)
         (ref-var! mc x #f)
         (begin
         (ref-var! mc x #t)
         (emit! mc `(castclass ,box-type) `(ldfld ,box-type "Value"))
         (finish-value! mc ctx)))]
      [(set-box! ,x ,e)
       (operands! mc (list (lambda () (ref-var! mc x #t) (emit! mc `(castclass ,box-type)))
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
      (and k (known-clause k argc))))

  (define (known-clause k argc)
    (let loop ((cls (known-clauses k)) (i 0))
      (cond
        ((null? cls) #f)
        ((cdar cls) #f)   ; 先匹配到剩余参数子句：走一般调用
        ((= (caar cls) argc) (cons k i))
        (else (loop (cdr cls) (+ i 1))))))

  ;; iron-070：letrec 绑定的同步 lambda 预先定下类名（有自由变量：实例方法）或共享槽 / 静态类（无自由变量：静态方法），
  ;; 登记到单元的 local-known 表。返回作为 lambda-class! 预设的 known 记录，或 #f（不登记，照旧生成）
  (define (local-known-preset! u x le)
    (and opt-local-call?
         (nanopass-case (L3 LambdaExpr) le
           ;; Clojure 的 fn* 生成 AFunction 的子类（invoke 方法，clj-fn-class!），没有 "C<i>" 子句方法：不直接调用（cleat-078）
           [(lambda ,nm (,x1* ...) ,cl* ...)
            (guard (not (clj-fn-name? nm)))
            (let* ((static? (null? (remq x x1*)))
                   (k (if (and static? opt-shared?)
                          (known-for-export u nm cl*)
                          (let ((name (class-name u nm)))
                            (make-known name (map clause-shape cl*) name "")))))
              (hashtable-set! (unit-local-known u) x (cons (if static? 'static 'instance) k))
              k)]
           [else #f])))

  ;; (static | instance known . 子句下标) 或 #f
  (define (local-known-target e0 mc argc)
    (nanopass-case (L3 Expr) e0
      [,x (let ((e (hashtable-ref (unit-local-known (mctx-unit mc)) x #f)))
            (and e (let ((t (known-clause (cdr e) argc))) (and t (cons (car e) t)))))]
      [else #f]))

  ;; ———————————————— 通用算术与比较在调用处内联（iron-034f，D8）————————————————
  ;;
  ;; 用户代码里的 + = fx+ fl< … 是 boot 导出的全局位置（rewrite 把 (primitive +) 换成了 g$+$N$%b），
  ;; 不是原语。按位置符号识别：去掉 gensym 后缀的名字在表里，且后缀是 $%b（boot 镜像规范改名后的后缀，
  ;; 05 §7；用户库自己定义的 + 的位置带进程戳，不会被误认）。两个参数时生成"都是 fixnum（或 flonum）→ 直接算，
  ;; 否则调原过程"，原过程负责其余类型与报错。
  (define inline-ops
    '(("+" add . generic) ("-" sub . generic) ("*" mul . generic) ("/" div . generic)
      ("quotient" quot . generic) ("remainder" rem . generic)
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

;; 操作数的静态类型：fx / fl（常量或类型推断得出），否则 #f
  (define (static-type e)
    (cond ((fixnum-constant e) 'fx)
          ((flonum-constant e) 'fl)
          (else (let ((t (expr-type e var-types))) (and (memq t '(fx fl)) t)))))

  ;; 要生成的快路径（按顺序尝试）：fx 算符只有 fx 路径、fl 算符只有 fl 路径；通用算符（iron-066）视操作数而定——
  ;; 有 flonum 操作数就不可能走 fx 路径；fl 路径要求至少一个操作数可能是 flonum（全是 fixnum 时 (/ 3 4) 是 3/4，
  ;; 不能按 double 算），其中的 fixnum 常量与已知 fixnum 转成 double（与通用算术的浮点传染相同：(* 1.5 0) 为 0.0）。
  ;; / 没有 fx 路径（结果是分数），quotient / remainder 没有 fl 路径。
  (define (inline-paths op kind types)
    (case kind
      ((fx) '(fx))
      ((fl) '(fl))
      (else
       (let ((fx? (and (not (eq? op 'div)) (not (memq 'fl types))))
             (fl? (and (not (memq op '(quot rem))) (or (memq 'fl types) (memq #f types)) #t)))
         (append (if fx? '(fx) '()) (if fl? '(fl) '()))))))

  ;; 两个参数的算术与比较。每条快路径检查类型未知的操作数（isinst），都符合就直接算，否则试下一条，最后是慢路径：
  ;; 与一般调用相同（经值槽、callvirt Invoke2），由原过程处理其余类型并报错。操作数都已知类型时没有检查，也没有慢路径。
  (define (cg-inline-op op kind e0 e* mc ctx)
    (let* ((types (map static-type e*))
           ;; fx / fl 算符的操作数已知是另一种类型（cp0 内联后的 (fx+ 1.0 x)、(fl+ 1 x)）时没有快路径，由原过程报错；
           ;; 已知是精确整数（iron-071）的操作数不可能走 flonum 路径
           (paths (if (and (memq kind '(fx fl)) (exists (lambda (t) (and t (not (eq? t kind)))) types))
                      '()
                      (inline-paths op kind (map (lambda (e t) (or t (and (type<=? (expr-type e var-types) 'int) 'int))) e* types))))
           (slow (new-label! mc))
           (end (new-label! mc))
           (slow-used? #f)
           ;; 操作数从左到右求值：常量留在原处；已知类型的（iron-034c）存入原始值的临时变量，不再检查；其余存入 object 临时变量
           (slots (map (lambda (e t)
                         (cond
                           ((or (fixnum-constant e) (flonum-constant e)) (list 'const e))
                           (t (cg-raw e mc t)
                              (let ((l (new-local! mc (type-clr t)))) (emit! mc `(stloc ,l)) (list 'raw l t)))
                           (else
                            (cg e mc 'value)
                            (let ((l (new-local! mc obj))) (emit! mc `(stloc ,l)) (list 'local l)))))
                       e* types)))
      (define (load-boxed s)
        (case (car s)
          ((local) (emit! mc `(ldloc ,(cadr s))))
          ((raw) (emit! mc `(ldloc ,(cadr s))) (box-raw! mc (caddr s)))
          (else (cg (cadr s) mc 'value))))
      ;; 以路径 p 的原始类型压栈
      (define (load-raw s p)
        (case (car s)
          ((local) (emit! mc `(ldloc ,(cadr s)) `(unbox.any ,(type-clr p))))
          ((raw) (emit! mc `(ldloc ,(cadr s))) (when (and (eq? p 'fl) (eq? (caddr s) 'fx)) (emit! mc '(conv.r8))))
          (else (let ((e (cadr s)))
                  (if (eq? p 'fl)
                      (emit! mc `(ldc.r8 ,(or (flonum-constant e) (inexact (fixnum-constant e)))))
                      (emit! mc `(ldc.i4 ,(fixnum-constant e))))))))
      (define (to! l) (set! slow-used? (or slow-used? (eq? l slow))) l)
      (define (emit-path! p next)
        (let ((fl? (eq? p 'fl)))
          (for-each (lambda (s)
                      (when (eq? (car s) 'local)
                        (emit! mc `(ldloc ,(cadr s)) `(isinst ,(type-clr p)) `(brfalse ,(to! next)))))
                    slots)
          ;; quotient / remainder（iron-061）：除数为 0 或 -1 时转慢路径（报错；最小 fixnum ÷ -1 溢出），由原过程处理
          (when (memq op '(quot rem))
            (let ((d (cadr slots)))
              (unless (and (eq? (car d) 'const) (not (memv (fixnum-constant (cadr d)) '(0 -1))))
                (load-raw d p) (emit! mc '(ldc.i4 0) `(beq ,(to! slow)))
                (load-raw d p) (emit! mc '(ldc.i4 -1) `(beq ,(to! slow))))))
          (for-each (lambda (s) (load-raw s p)) slots)
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
               ((memq op '(quot rem))
                (emit! mc (if (eq? op 'quot) '(div) '(rem)) `(call ,rt-type "Box" (,int32-type) ,obj))
                (finish-value! mc ctx))
               ((eq? kind 'generic)
                (emit! mc `(call ,arith-type ,(case op ((add) "Add") ((sub) "Subtract") (else "Multiply")) (,int32-type ,int32-type) ,obj))
                (finish-value! mc ctx))
               (else
                ;; fx+ fx- fx*：溢出时助手返回 null，转去慢路径由原过程报错
                (let ((r (new-local! mc obj)))
                  (emit! mc `(call ,arith-type ,(case op ((add) "FxAdd") ((sub) "FxSubtract") (else "FxMultiply")) (,int32-type ,int32-type) ,obj)
                         `(stloc ,r) `(ldloc ,r) `(brfalse ,(to! slow)) `(ldloc ,r))
                  (finish-value! mc ctx))))))
          (unless (eq? ctx 'tail) (emit! mc `(br ,end)))))
      (let loop ((ps paths))
        (unless (null? ps)
          (let ((next (if (null? (cdr ps)) slow (new-label! mc))))
            (emit-path! (car ps) next)
            (unless (eq? next slow) (emit! mc `(label ,next)))
            (loop (cdr ps)))))
      ;; 慢路径：与一般调用相同（经值槽、callvirt Invoke2），错误信息与 who 由原过程给出
      (when (or slow-used? (null? paths))
        (emit! mc `(label ,slow))
        (cg e0 mc 'value)
        (emit! mc `(call ,calls-type "Proc" (,obj) ,proc-type))
        (for-each load-boxed slots)
        (when (tail-ok? mc ctx) (emit! mc '(tail)))
        (emit! mc (invoke-method 2))
        (finish-value! mc ctx))
      (unless (eq? ctx 'tail) (emit! mc `(label ,end)))))

  ;; 一元数值谓词在调用处内联（iron-064，参照 Chez cpprim.ss 的 zero? / even? 等）：参数是 fixnum 时
  ;; 一条比较，否则调原过程（其余类型与报错由它处理）。fixnum? / flonum? 直接是类型检查。
  (define unary-inline-ops
    '(("zero?" . zero) ("positive?" . pos) ("negative?" . neg) ("even?" . even) ("odd?" . odd)
      ("fixnum?" . fixnum?) ("flonum?" . flonum?)))
  (define unary-op-cache (make-eq-hashtable))
  (define (unary-op-lookup s)
    (let ((r (hashtable-ref unary-op-cache s 'unknown)))
      (if (eq? r 'unknown)
          (let ((r (and (boot-location? s)
                        (let ((r (assoc (pretty s) unary-inline-ops))) (and r (cdr r))))))
            (hashtable-set! unary-op-cache s r)
            r)
          r)))
  (define (unary-inline-op e0 argc)
    (and (= argc 1)
         (nanopass-case (L3 Expr) e0
           [(gref ,s) (unary-op-lookup s)]
           [else #f])))

  (define (cg-unary-op op e0 e mc ctx)
    (case op
      ((fixnum? flonum?)
       (cg e mc 'value)
       (emit! mc `(isinst ,(if (eq? op 'fixnum?) int32-type double-type)) '(ldnull) '(cgt.un))
       (finish-bool! mc ctx))
      (else
       (let ((slow (new-label! mc)) (end (new-label! mc)) (t (new-local! mc obj)))
         (cg e mc 'value)
         (emit! mc `(stloc ,t) `(ldloc ,t) `(isinst ,int32-type) `(brfalse ,slow) `(ldloc ,t) `(unbox.any ,int32-type))
         (case op
           ((zero) (emit! mc '(ldc.i4 0) '(ceq)))
           ((pos) (emit! mc '(ldc.i4 0) '(cgt)))
           ((neg) (emit! mc '(ldc.i4 0) '(clt)))
           ((even) (emit! mc '(ldc.i4 1) '(and) '(ldc.i4 0) '(ceq)))
           (else (emit! mc '(ldc.i4 1) '(and))))
         (finish-bool! mc ctx)
         (unless (eq? ctx 'tail) (emit! mc `(br ,end)))
         (emit! mc `(label ,slow))
         (cg e0 mc 'value)
         (emit! mc `(call ,calls-type "Proc" (,obj) ,proc-type) `(ldloc ,t))
         (when (tail-ok? mc ctx) (emit! mc '(tail)))
         (emit! mc (invoke-method 1))
         (finish-value! mc ctx)
         (unless (eq? ctx 'tail) (emit! mc `(label ,end)))))))

  ;; vector-ref / vector-set! 在调用处内联（iron-070 的"检查 + 退回"）：向量是 object[]（与 vector? 相同）、下标是
  ;; fixnum 且在界内（无符号比较同时排除负数）时直接 ldelem.ref / stelem.ref；否则调原过程（boot 的 vector-ref），
  ;; 错误信息与 who 由它给出。下标已推断为 fixnum（iron-034c / 069 签名）或是常量时不检查下标类型。
  (define vector-op-cache (make-eq-hashtable))
  (define (vector-op e0 argc)
    (nanopass-case (L3 Expr) e0
      [(gref ,s)
       (let ((r (let ((r (hashtable-ref vector-op-cache s 'unknown)))
                  (if (eq? r 'unknown)
                      (let ((r (case (boot-location-name s) ((vector-ref) 'ref) ((vector-set!) 'set) (else #f))))
                        (hashtable-set! vector-op-cache s r)
                        r)
                      r))))
         (and r (= argc (if (eq? r 'ref) 2 3)) r))]
      [else #f]))

  (define (cg-vector-op op e0 e* mc ctx)
    (let* ((v (car e*)) (i (cadr e*))
           (k (fixnum-constant i))
           (raw-i? (or k (eq? (static-type i) 'fx)))
           (slow (new-label! mc)) (end (new-label! mc))
           (lv (new-local! mc obj))
           (arr (new-local! mc obj-array))
           (ii (new-local! mc int32-type))
           (li (and (not raw-i?) (new-local! mc obj)))
           (lx (and (eq? op 'set) (new-local! mc obj))))
      ;; 操作数从左到右求值，各自存入局部变量（栈上不留值，操作数里可以有 try）
      (cg v mc 'value) (emit! mc `(stloc ,lv))
      (cond (k (emit! mc `(ldc.i4 ,k) `(stloc ,ii)))
            (raw-i? (cg-raw i mc 'fx) (emit! mc `(stloc ,ii)))
            (else (cg i mc 'value) (emit! mc `(stloc ,li))))
      (when lx (cg (caddr e*) mc 'value) (emit! mc `(stloc ,lx)))
      ;; 快路径
      (if (eq? (expr-type v var-types) 'vector)            ; 已知是向量（iron-071 的收窄）
          (emit! mc `(ldloc ,lv) `(castclass ,obj-array) `(stloc ,arr))
          (emit! mc `(ldloc ,lv) `(isinst ,obj-array) `(stloc ,arr) `(ldloc ,arr) `(brfalse ,slow)))
      (when li
        (emit! mc `(ldloc ,li) `(isinst ,int32-type) `(brfalse ,slow) `(ldloc ,li) `(unbox.any ,int32-type) `(stloc ,ii)))
      (emit! mc `(ldloc ,ii) `(ldloc ,arr) '(ldlen) '(conv.i4) `(bge.un ,slow))
      (if (eq? op 'ref)
          (begin (emit! mc `(ldloc ,arr) `(ldloc ,ii) '(ldelem.ref)) (finish-value! mc ctx))
          (begin (emit! mc `(ldloc ,arr) `(ldloc ,ii) `(ldloc ,lx) '(stelem.ref)) (finish-none! mc ctx)))
      (unless (eq? ctx 'tail) (emit! mc `(br ,end)))
      ;; 慢路径：与一般调用相同
      (emit! mc `(label ,slow))
      (cg e0 mc 'value)
      (emit! mc `(call ,calls-type "Proc" (,obj) ,proc-type) `(ldloc ,lv))
      (if li (emit! mc `(ldloc ,li)) (begin (emit! mc `(ldloc ,ii)) (box-raw! mc 'fx)))
      (when lx (emit! mc `(ldloc ,lx)))
      (when (tail-ok? mc ctx) (emit! mc '(tail)))
      (emit! mc (invoke-method (length e*)))
      (finish-value! mc ctx)
      (unless (eq? ctx 'tail) (emit! mc `(label ,end)))))

  (define (cg-call e0 e* mc ctx)
    (let ((argc (length e*)))
      (cond
        ((local-escape-target e0 mc argc)
         => (lambda (loc)
              (cg (car e*) mc 'value)
              (emit! mc `(stloc ,(cadr loc))
                     (if (> (mctx-try-depth mc) (cadddr loc)) `(leave ,(caddr loc)) `(br ,(caddr loc))))
              ;; 之后的代码不可达；按上下文补一个占位值，让后续指令的栈形状照常
              (case ctx ((value) (emit! mc '(ldnull))) ((test) (emit! mc '(ldc.i4 0))) (else (void)))))
        ;; 结果已知的类型谓词（iron-071）
        ((let ((t (call-pred-type e0 argc))) (and t (known-pred (car e*) t)))
         => (lambda (b) (cg-known-bool! mc (car b) ctx)))
        ((and opt-inline? (not (top-level-mc? mc)) (unary-inline-op e0 argc))
         => (lambda (op) (cg-unary-op op e0 (car e*) mc ctx)))
        ((and opt-inline? (not (top-level-mc? mc)) (vector-op e0 argc))
         => (lambda (op) (cg-vector-op op e0 e* mc ctx)))
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
        ;; letrec 绑定的过程（iron-070）：无自由变量的直接调静态子句方法；闭包按其类 castclass 后调实例子句方法
        ((local-known-target e0 mc argc)
         => (lambda (t)
              (let* ((instance? (eq? (car t) 'instance)) (k (cadr t)) (i (cddr t)))
                (operands! mc (append
                                (if instance?
                                    (list (lambda () (cg e0 mc 'value) (emit! mc `(castclass ,(known-owner k)))))
                                    '())
                                (map (lambda (x) (lambda () (cg x mc 'value))) e*)))
                (when (tail-ok? mc ctx) (emit! mc '(tail)))
                (emit! mc `(call ,(known-owner k) ,(string-append (known-prefix k) "C" (number->string i)) ,(objs argc) ,obj))
                (finish-value! mc ctx))))
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
        ;; 结果已知的类型谓词（iron-071）
        ((and (= argc 1) (let ((r (assq pr pred-prim-types))) (and r (known-pred (car e*) (cdr r)))))
         => (lambda (b) (cg-known-bool! mc (car b) ctx)))
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
           ;; car / cdr：boot 里不展开（预编译的 dll，JIT 会把 Builtins.Car 内联进来，展开只增大镜像）。
           ;; 用户代码编译进内存程序集，JIT 不把 Skeg.Core 的 Car 内联进去（JitDisasm 里是 call [Builtins:Car]），
           ;; 在调用处展开成"isinst Cons 后取字段，否则调 Builtins.Car 报错"（iron-070，错误与 who 不变）
           ((car cdr)
            (cond
              ;; 已知是 pair（iron-071 的收窄）：直接取字段
              ((and (= argc 1) (eq? (expr-type (car e*) var-types) 'pair))
               (args!)
               (emit! mc `(castclass ,cons-type) `(ldfld ,cons-type ,(if (eq? pr 'car) "car" "cdr")))
               (finish-value! mc ctx))
              ((or (top-level-mc? mc) (string=? (unit-name (mctx-unit mc)) boot-assembly-name))
                (cg-static-call! o e* mc ctx))
              (else
                (let ((t (new-local! mc obj)) (ok (new-label! mc)) (end (new-label! mc)))
                  (args!)
                  (emit! mc `(stloc ,t) `(ldloc ,t) `(isinst ,cons-type) '(dup) `(brtrue ,ok)
                         '(pop) `(ldloc ,t) `(call ,(overload-type o) ,(overload-method o) (,obj) ,obj) `(br ,end)
                         `(label ,ok) `(ldfld ,cons-type ,(if (eq? pr 'car) "car" "cdr"))
                         `(label ,end))
                  (finish-value! mc ctx)))))
           ((cons)
            (args!)
            (emit! mc `(newobj ,cons-type (,obj ,obj)))
            (finish-value! mc ctx))
           ((eq?) (args!) (emit! mc '(ceq)) (finish-bool! mc ctx))
           ((not)
            (args!)
            (emit! mc `(call ,rt-type "IsTrue" (,obj) ,bool-type) '(ldc.i4 0) '(ceq))
            (finish-bool! mc ctx))
           ((null?) (args!) (emit! mc `(ldsfld ,empty-list-type "Instance") '(ceq)) (finish-bool! mc ctx))
           ((nil?) (args!) (emit! mc '(ldnull) '(ceq)) (finish-bool! mc ctx))
           ((pair?) (args!) (emit! mc `(isinst ,cons-type) '(ldnull) '(cgt.un)) (finish-bool! mc ctx))
           (($fixnum?) (args!) (emit! mc `(isinst ,int32-type) '(ldnull) '(cgt.un)) (finish-bool! mc ctx))
           (($flonum?) (args!) (emit! mc `(isinst ,double-type) '(ldnull) '(cgt.un)) (finish-bool! mc ctx))
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
          ;; commonize（iron-073）：本单元里结构相同的闭合 lambda 共用一个过程（同一持有类字段）
          (let ((key (and opt-commonize? (not self) (null? preset) (lambda-key nm cl*))))
            (or (and key (hashtable-ref common-lambdas key #f))
                (let ((c (shared-procedure! mc nm cl* self self-mref (and (pair? preset) (car preset)))))
                  (when key (hashtable-set! common-lambdas key c))
                  c)))
          (procedure-class1! mc nm x1* cl* self self-mref preset)))))

  ;; ———————————————— commonize（iron-073，照 Chez cpcommonize.ss 裁剪）————————————————
  ;; 内联（iron-070）把过程体复制到各调用处，体里的字面 lambda 也随之复制；没有自由变量的（034d 的共享过程）每份都生成
  ;; 一组子句方法与一个静态字段。这里按结构比较：名字相同、子句的 L3 形式在局部变量按出现顺序改名后相同的闭合 lambda，
  ;; 共用第一次生成的那个过程（持有类与字段）。闭合的过程没有状态，共用只让本来不同的两个过程对象变成 eq?——
  ;; R6RS 不规定两次求值同一个 lambda 是否 eq?（Chez 的 commonize 同样如此）。有自由变量的 lambda 不共享（Chez
  ;; commonization-level 更高时把它们提成 helper，这里不做）。节点数超过上限的不比较（键的计算与体积成正比）。
  ;; 测量开关：CLEAT_NO_073 为 1 或含 commonize 时关。
  (define opt-commonize?
    (let ((v ($get-environment-variable "CLEAT_NO_073")))
      (not (and (string? v) (or (string=? v "1") (string-contains? v "commonize"))))))
  (define commonize-node-limit 400)
  (define common-lambdas (make-hashtable equal-hash equal?))   ; 键 → shared-procedure! 的结果；每个单元清空

  (define (lambda-key nm cl*)
    (and (for-all (lambda (cl)
                    (nanopass-case (L3 Clause) cl
                      [(clause (,x* ...) ,x ,body) (not (count-nodes body commonize-node-limit))]))
                  cl*)
         (let ((forms (alpha-normalize (map unparse-L3 cl*))))
           (and forms (cons nm forms)))))

  ;; 局部变量（子句形参、let / letrec / label / escape-frame / try-handle 绑定的名字）按首次出现的顺序改成 0、1、2 …；
  ;; 其余符号（全局位置、模块变量、原语名、关键字）与 quote 里的数据不动。quote 了表或向量时为 #f：
  ;; 常量可能有环，equal-hash 不一定停得下来
  (define (alpha-normalize forms)
    (let ((binders (make-eq-hashtable)) (names (make-eq-hashtable)) (n 0) (ok? #t))
      (define (bind! x) (when (symbol? x) (hashtable-set! binders x #t)))
      (define (collect e)
        (when (pair? e)
          (case (car e)
            ((quote) (when (and (pair? (cdr e)) (or (pair? (cadr e)) (vector? (cadr e)))) (set! ok? #f)))
            ((clause) (for-each bind! (cadr e)) (bind! (caddr e)) (collect (cadddr e)))
            ((let letrec) (for-each (lambda (b) (bind! (car b)) (collect (cadr b))) (cadr e)) (collect (caddr e)))
            ((label) (bind! (cadr e)) (for-each bind! (caddr e)) (for-each collect (cdddr e)))
            ((escape-frame try-handle) (bind! (cadr e)) (for-each collect (cddr e)))
            ;; cleat：含 Clojure 节点、或嵌套的 lambda 名字是记录（Clojure 的 fn*）的不合并
            ((lambda) (unless (or (symbol? (cadr e)) (string? (cadr e)) (not (cadr e))) (set! ok? #f)) (for-each collect (cddr e)))
            (else
             (when (and (symbol? (car e))
                        (let ((n (symbol->string (car e)))) (and (> (string-length n) 4) (string=? (substring n 0 4) "clj-"))))
               (set! ok? #f))
             (for-each collect e)))))
      (define (rename e)
        (cond
          ((symbol? e)
           (if (hashtable-contains? binders e)
               (or (hashtable-ref names e #f)
                   (let ((k n)) (set! n (+ n 1)) (hashtable-set! names e k) k))
               e))
          ((and (pair? e) (eq? (car e) 'quote)) e)
          ((pair? e) (let ((a (rename (car e)))) (cons a (rename (cdr e)))))
          (else e)))
      (for-each collect forms)
      (and ok? (map rename forms))))


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
        ((null? arity) `((ldsfld ,empty-list-type "Instance")))
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
                          ((null? arity) `((ldsfld ,empty-list-type "Instance")))
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
  ;; 与 Procedure 子类同构：自由变量为实例字段 c0 …（没有自由变量的也每次 new，cleat-032），
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
           ;; cleat-032：Clojure 的 fn 每次求值都是新对象（JVM 的 FnExpr 总是 new；(identical? (mk) (mk)) 为 false，
           ;; 两次求值得到的 fn 是 set / map 里的两个键），没有自由变量的也不做单例
           (static? #f)
           (name (unique-type-name! u (clj-fn-name-class nm)))
           (variadic (clj-fn-name-variadic nm))
           (n (length cl*))
           (base (if variadic restfn-type afunction-type))
           (field-names (let loop ((fs fields) (i 0))
                          (if (null? fs) '() (cons (string-append "c" (number->string i)) (loop (cdr fs) (+ i 1))))))
           (arities (let loop ((cl* cl*) (i 0))
                      (if (null? cl*) '() (cons (car (clause-shape (car cl*))) (loop (cdr cl*) (+ i 1))))))
           (fixed (if variadic (list-head arities (- n 1)) arities))
           (async? (clj-fn-name-async? nm))
           (methods '()))
      (define (add-method! m) (set! methods (cons m methods)))
      ;; invokeStatic（cleat-033，designs/04 §4/§6）：没有自由字段的 fn 把固定子句的体编成静态方法，供接口文件
      ;; 登记（HasStatic）与跨单元直接链接的调用点（$clj-static-call → newobj + call 类::invokeStatic）使用。
      ;; 体只编这一份，实例 invoke 转发到它并传 this 作 self（(fn* me [] me) 的 self 就是实例本身）；体里的 self
      ;; 是第 0 个参数。直接链接的调用点没有实例：现场 newobj 一个（fn 每次求值本就是新对象，cleat-032）。
      ;; 变长子句保留实例体（RestFn 的 doInvoke 语义）
      ;; 原始签名的固定子句（cleat-042，JVM 的 IFn$LL）：体编成有类型的方法——没有自由字段时是静态 invokeStaticPrim
      ;; （另有 object 签名的 invokeStatic 转换后转发给它，直接链接照旧），有字段时是实例 invokePrim；类实现
      ;; clojure.lang.primifs.<签名>，invokePrim 供按 Var 元数据生成的原始调用点使用
      (when (and (null? fields) (not async?))
        (for-each (lambda (cl)
                    (let ((sig (clause-sig nm cl)))
                      (add-method! (clj-static-clause-method u name cl self sig))
                      (when sig
                        (add-method! (clj-prim-bridge u name cl sig 'static))
                        (add-method! (clj-prim-bridge u name cl sig 'prim)))))
                  (if variadic (list-head cl* (- n 1)) cl*)))
      (let loop ((cls cl*) (i 0))
        (unless (null? cls)
          (let ((last-variadic? (and variadic (= i (- n 1)))))
            (cond
              ;; ^:async 的体 thunk（cleat-043）：体编进返回 Task<object> 的 async 方法，invoke 转发
              (async?
               (add-method! (clj-async-clause-method u name (car cls) fields field-names self))
               (add-method! `(method "invoke" (attrs override) (params) (ret ,obj)
                               (body (ldarg 0) (call ,name "invokeAsync" () ,(task-of-object-type)) (ret)))))
              ((and (null? fields) (not last-variadic?))
               (add-method! (clj-forward-method u name (car cls))))
              ((and (not last-variadic?) (clause-sig nm (car cls)))
               => (lambda (sig)
                    (add-method! (clj-clause-method u name (car cls) fields field-names self "invokePrim" sig))
                    (add-method! (clj-prim-bridge u name (car cls) sig 'invoke))))
              (else
               (add-method! (clj-clause-method u name (car cls) fields field-names self
                                               (if last-variadic? "doInvoke" "invoke"))))))
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
           ,@(let ((sigs (let loop ((cl* cl*) (acc '()))
                           (if (null? cl*) (reverse acc)
                               (let ((sig (clause-sig nm (car cl*))))
                                 (loop (cdr cl*) (if (and sig (not (member sig acc))) (cons sig acc) acc)))))))
               (if (null? sigs) '() `((interfaces ,@(map (lambda (sig) (string-append primifs-prefix sig)) sigs)))))
           ,@(if static? `((field "Instance" ,name static)) (map (lambda (f) `(field ,f ,obj)) field-names))
           ,@(reverse methods)))
      (list static? name "Instance")))

  (define (find-index pred ls)
    (let loop ((ls ls) (i 0)) (cond ((null? ls) #f) ((pred (car ls)) i) (else (loop (cdr ls) (+ i 1))))))

  (define (list-head ls n) (if (= n 0) '() (cons (car ls) (list-head (cdr ls) (- n 1)))))

  ;; Clojure 函数类的一个子句：实例方法 mname（invoke / doInvoke 的覆盖），形参都是 object
  (define (clj-clause-method u class cl fields field-names self mname . sig)
    (nanopass-case (L3 Clause) cl
      [(clause (,x* ...) ,x ,body)
       (when x (error 'codegen "Clojure fn clauses have fixed parameters" x))
       (let ((mc (make-method-ctx u class #f #f #f #f))
             (sig (and (pair? sig) (car sig))))
         (bind-clause-params! mc x* 1 sig)
         (for-each (lambda (f n) (hashtable-set! (mctx-vars mc) f (cons 'field n))) fields field-names)
         (when self (hashtable-set! (mctx-vars mc) self '(self)))
         (mctx-big?-set! mc (bigger-than? body big-body-nodes))
         (init-clearing! mc body)
         (cg body mc 'tail)
         (if sig
             (append (finish-method mc mname '(optimize) (sig-param-types sig) (sig-char-type (sig-ret sig)))
                     `((implements ,(string-append primifs-prefix sig) "invokePrim")))
             (finish-method mc mname '(override optimize) (objs (length x*)))))]))

  ;; ^:async fn 的体 thunk（无参数，cleat-043）：运行时 async（.NET 11 起缺省；.NET 10 要 DOTNET_RuntimeAsync=1）——方法声明返回
  ;; Task<object> 并带 async 实现标志，体照常返回 object，由运行时包成 Task；await* 在方法里直接调 AsyncHelpers.Await，
  ;; 挂起与恢复由运行时完成（ClojureCLR 的做法）。方法内不发 tail.（try-depth 从 1 起），不做局部变量清除（取地址）
  (define (task-of-object-type) (clr-static-call Cleat.Scheme.Clojure.AsyncSupport get_TaskOfObjectName))
  (define (clj-async-clause-method u class cl fields field-names self)
    (nanopass-case (L3 Clause) cl
      [(clause (,x* ...) ,x ,body)
       (unless (and (null? x*) (not x)) (error 'codegen "async body thunks take no parameters" x*))
       (let ((mc (make-method-ctx u class #f #f #f #f)))
         (for-each (lambda (f n) (hashtable-set! (mctx-vars mc) f (cons 'field n))) fields field-names)
         (when self (hashtable-set! (mctx-vars mc) self '(self)))
         (mctx-big?-set! mc (bigger-than? body big-body-nodes))
         (mctx-try-depth-set! mc 1)
         (cg body mc 'tail)
         (finish-method mc "invokeAsync" '(async) '() (task-of-object-type)))]))

  ;; 子句形参的位置：从下标 j0 起的参数；有原始签名时 L / D 形参是原始值的参数（targ），返回类型记入 mctx
  (define (bind-clause-params! mc x* j0 sig)
    (let loop ((ps x*) (j j0) (cs (if sig (sig-params sig) '())))
      (unless (null? ps)
        (let ((raw (and (pair? cs) (sig-char-raw (car cs)))))
          (hashtable-set! (mctx-vars mc) (car ps) (if raw (cons 'targ (cons j raw)) (cons 'arg j))))
        (loop (cdr ps) (+ j 1) (if (pair? cs) (cdr cs) '()))))
    (when sig (mctx-ret-type-set! mc (sig-char-raw (sig-ret sig)))))

  ;; Clojure 函数类的固定子句的静态变体（cleat-033）：第 0 个参数是 self（实例 invoke 转发时传 this，
  ;; 直接链接的调用点传新建实例），形参从 1 开始；尾位置照常发 (tail)
  (define (clj-static-clause-method u class cl self sig)
    (nanopass-case (L3 Clause) cl
      [(clause (,x* ...) ,x ,body)
       (when x (error 'codegen "Clojure fn clauses have fixed parameters" x))
       (let ((mc (make-method-ctx u class #t #f #f #f)))
         (when self (hashtable-set! (mctx-vars mc) self (cons 'arg 0)))
         (bind-clause-params! mc x* 1 sig)
         (mctx-big?-set! mc (bigger-than? body big-body-nodes))
         (init-clearing! mc body)
         (cg body mc 'tail)
         (if sig
             (finish-method mc "invokeStaticPrim" '(static optimize) (cons obj (sig-param-types sig)) (sig-char-type (sig-ret sig)))
             (finish-method mc "invokeStatic" '(static optimize) (objs (+ (length x*) 1)))))]))

  ;; ———— 原始签名（cleat-042）————
  ;; 签名字符串：每个形参一个字母、最后是返回，O = object、L = long、D = double（clojure.lang.primifs 的接口名）
  (define primifs-prefix "clojure.lang.primifs.")
  (define (sig-char-type c) (case c ((#\L) int64-type) ((#\D) double-type) (else obj)))
  (define (sig-char-raw c) (case c ((#\L) 'l64) ((#\D) 'fl) (else #f)))
  (define (sig-params sig) (list-head (string->list sig) (- (string-length sig) 1)))
  (define (sig-ret sig) (string-ref sig (- (string-length sig) 1)))
  (define (sig-param-types sig) (map sig-char-type (sig-params sig)))
  ;; 栈顶 object → 字母 c 的类型（JVM 原始参数的转换：RT.longCast / doubleCast，只收数字）；字母 c 的值 → object
  (define (unbox-to-sig! mc c)
    (case c
      ((#\L) (emit! mc `(call ,static-calls-type "PrimArgLong" (,obj) ,int64-type)))
      ((#\D) (emit! mc `(call ,static-calls-type "PrimArgDouble" (,obj) ,double-type)))
      (else (void))))
  (define (box-from-sig! mc c)
    (case c
      ((#\L) (emit! mc `(box ,int64-type)))
      ((#\D) (emit! mc `(box ,double-type)))
      (else (void))))
  ;; fn 名字记录里固定子句的签名（变长子句没有）
  (define (clause-sig nm cl)
    (and (clj-fn-name? nm)
         (nanopass-case (L3 Clause) cl
           [(clause (,x* ...) ,x ,body)
            (and (not x) (let ((p (assv (length x*) (clj-fn-name-prims nm)))) (and p (cdr p))))])))

  ;; 原始签名子句的转发方法：
  ;;   static：object 签名的静态 invokeStatic(self, a …) → 转换实参、call invokeStaticPrim、装箱结果（直接链接的调用点用）
  ;;   prim  ：实例 invokePrim(T …)（实现 primifs 接口）→ call invokeStaticPrim(this, …)
  ;;   invoke：有字段的 fn 的实例 invoke(a …) → 转换实参、callvirt this.invokePrim、装箱结果
  (define (clj-prim-bridge u class cl sig kind)
    (let* ((mc (make-method-ctx u class (eq? kind 'static) #f #f #f))
           (cs (sig-params sig)) (rc (sig-ret sig))
           (n (length cs)) (ptypes (sig-param-types sig)) (rtype (sig-char-type rc)))
      (case kind
        ((static)
         (emit! mc '(ldarg 0))
         (let loop ((cs cs) (j 1)) (unless (null? cs) (emit! mc `(ldarga ,j)) (take-slot! mc) (unbox-to-sig! mc (car cs)) (loop (cdr cs) (+ j 1))))
         (emit! mc `(call ,class "invokeStaticPrim" ,(cons obj ptypes) ,rtype))
         (box-from-sig! mc rc)
         (emit! mc '(ret))
         (finish-method mc "invokeStatic" '(static optimize) (objs (+ n 1))))
        ((prim)
         (emit! mc '(ldarg 0))
         (let loop ((j 1)) (unless (> j n) (emit! mc `(ldarg ,j)) (loop (+ j 1))))
         (emit! mc `(call ,class "invokeStaticPrim" ,(cons obj ptypes) ,rtype) '(ret))
         (append (finish-method mc "invokePrim" '(optimize) ptypes rtype)
                 `((implements ,(string-append primifs-prefix sig) "invokePrim"))))
        (else
         (emit! mc '(ldarg 0))
         (let loop ((cs cs) (j 1)) (unless (null? cs) (emit! mc `(ldarga ,j)) (take-slot! mc) (unbox-to-sig! mc (car cs)) (loop (cdr cs) (+ j 1))))
         (emit! mc `(call ,class "invokePrim" ,ptypes ,rtype))
         (box-from-sig! mc rc)
         (emit! mc '(ret))
         (finish-method mc "invoke" '(override optimize) (objs n))))))

  ;; 实例 invoke 的转发变体（cleat-033）：this 作 self，形参原样传给静态 invokeStatic，返回它的值
  (define (clj-forward-method u class cl)
    (nanopass-case (L3 Clause) cl
      [(clause (,x* ...) ,x ,body)
       `(method "invoke" (attrs override optimize) (params ,@(objs (length x*))) (ret ,obj)
          ;; 实参读出后把参数槽置 null（局部变量清除）：tier-0 的转发帧在被调用者运行期间不再抓着它们
          (body (ldarg 0)
                ,@(let loop ((j 1)) (if (> j (length x*)) '() (cons* `(ldarga ,j) `(call "Cleat.Scheme.Locals" "Take" (,(string-append obj "&")) ,obj) (loop (+ j 1)))))
                (call ,class "invokeStatic" ,(objs (+ (length x*) 1)) ,obj)
                (ret)))]))

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
      ;; 没有基本字段的记录（(defrecord R [])）也要无参构造函数与 create（cleat-032：原来 keys 为空时整个跳过）
      (if (= drops 0)
          '()
          (append
            (if (= drops 4) (list (alt-ctor nb `((ldnull) (ldnull) ,@zero ,@zero))) '())
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
  ;; reify? 位置：#t reify*、#f deftype*、interface gen-interface、proxy proxy-class*（cleat-043）
  (define (deftype-reify? d) (eq? (deftype-info-reify? d) #t))
  (define (deftype-proxy? d) (eq? (deftype-info-reify? d) 'proxy))
  (define (deftype-genclass? d) (eq? (deftype-info-reify? d) 'genclass))

  (define (interface-class! mc d)
    (let ((u (mctx-unit mc)) (name (deftype-info-class d)))
      (hashtable-set! (unit-type-names u) name #t)
      (add-class! u
        `(type ,name (attrs interface abstract) (interfaces ,@(deftype-info-ifaces d))
           ,@(map (lambda (sp)
                    `(method ,(cadr sp) (attrs abstract virtual) (params ,@(map tname (cadddr sp))) (ret ,(tname (caddr sp)))))
                  (deftype-info-methods d))))
      (cons name '())))

  ;; ———————————————— proxy-class*（cleat-043）————————————————
  ;; 描述：(类名 proxy 基类 (接口 …) (构造函数参数类型 …) (方法 …))；方法为
  ;;   (本类中的名字 返回类型 (参数类型 …) 访问性 种类 (声明类型 原名) 缺省行为 映射键 可分派?)，类型写作 ("名字" . 值类型?)。
  ;; 生成真正的子类：字段 __clojureFnMap（IProxy 的 fn 映射）；每个方法按映射键取 fn，有则以 (this 实参 …) 调用（值类型实参装箱、
  ;; 返回值转换为声明类型），没有则调基类的实现（非虚 call）或抛 NotImplementedException。基类方法另有公开的非虚
  ;; proxy$super$名字，proxy-super 经它直接调基类实现（不必像 JVM 那样临时改映射）。构造函数按基类的原签名转发。
  (define (deftype-proxy-super d) (list-ref d 2))
  (define (deftype-proxy-ifaces d) (list-ref d 3))
  (define (deftype-proxy-ctors d) (list-ref d 4))
  (define (deftype-proxy-methods d) (list-ref d 5))

  ;; 栈上的 object → 声明类型 t 的代码（void 时丢弃）
  (define (convert-object-code t)
    (cond
      ((string=? (tname t) void-type) '((pop)))
      ((string=? (tname t) obj) '())
      ((value-type? t)
       `((ldtoken ,(tname t)) (call ,type-type "GetTypeFromHandle" (,type-handle-type) ,type-type)
         (call ,clojure-ops-type "ConvertReturn" (,obj ,type-type) ,obj) (unbox.any ,(tname t))))
      (else `((castclass ,(tname t))))))

  ;; 第 i 个实参（ldarg i）装成 Clojure 的值：值类型装箱并按 Clojure 的规则转换（int → long）
  (define (boxed-arg-code i t)
    (if (value-type? t)
        `((ldarg ,i) (box ,(tname t)) (call ,clojure-ops-type "BoxArg" (,obj) ,obj))
        `((ldarg ,i))))

  (define (args-code n) (let loop ((i 1)) (if (> i n) '() (cons `(ldarg ,i) (loop (+ i 1))))))

  (define (proxy-class! mc d)
    (let* ((u (mctx-unit mc))
           (name (deftype-info-class d))
           (super (deftype-proxy-super d))
           (fmap "__clojureFnMap")
           (iproxy "clojure.lang.IProxy"))
      (define (throw-code msg)
        `((ldstr ,msg) (newobj "System.NotImplementedException" (,string-type)) (throw)))
      (define (method-def m)
        (let* ((mname (list-ref m 0)) (ret (list-ref m 1)) (ptypes (list-ref m 2)) (access (list-ref m 3))
               (kind (list-ref m 4)) (target (list-ref m 5)) (fallback (list-ref m 6)) (key (list-ref m 7)) (ok? (list-ref m 8))
               (n (length ptypes))
               (call-base `((ldarg 0) ,@(args-code n)
                            (call ,(car target) ,(cadr target) ,(map tname ptypes) ,(tname ret)) (ret)))
               (default (if (string=? fallback "base")
                            call-base
                            (throw-code (string-append (cadr target) " is not implemented by proxy " name))))
               (dispatch
                (if ok?
                    `((ldarg 0) (ldfld ,name ,fmap) (ldstr ,key)
                      (call ,clojure-ops-type "ProxyFn" (,pmap-type ,string-type) ,ifn-type)
                      (dup) (brfalse NOFN)
                      ,@(if (< n 20)
                            `((ldarg 0)
                              ,@(apply append (map (lambda (t i) (boxed-arg-code i t)) ptypes (iota-from 1 n)))
                              (callvirt ,ifn-type "invoke" ,(objs (+ n 1)) ,obj))
                            `((ldc.i4 ,(+ n 1)) (newarr ,obj)
                              (dup) (ldc.i4 0) (ldarg 0) (stelem.ref)
                              ,@(apply append (map (lambda (t i) `((dup) (ldc.i4 ,i) ,@(boxed-arg-code i t) (stelem.ref)))
                                                   ptypes (iota-from 1 n)))
                              (call ,clojure-ops-type "InvokeIFn" (,ifn-type ,obj-array) ,obj)))
                      ,@(convert-object-code ret)
                      (ret)
                      (label NOFN) (pop))
                    '())))
          `(method ,mname
             (attrs ,@(cond ((string=? kind "override") `(override ,@(if (string=? access "protected") '(protected) '())))
                            ((string=? mname (cadr target)) '())
                            (else '(private))))
             (params ,@(map tname ptypes)) (ret ,(tname ret))
             ,@(if (string=? kind "implement") `((implements ,(car target) ,(cadr target))) '())
             (body ,@dispatch ,@default))))
      ;; proxy-super 用：基类实现的公开非虚入口
      (define (super-def m)
        (let ((ret (list-ref m 1)) (ptypes (list-ref m 2)) (target (list-ref m 5)))
          `(method ,(string-append "proxy$super$" (cadr target)) (params ,@(map tname ptypes)) (ret ,(tname ret))
             (body (ldarg 0) ,@(args-code (length ptypes))
                   (call ,(car target) ,(cadr target) ,(map tname ptypes) ,(tname ret)) (ret)))))
      (unless (hashtable-contains? (unit-type-names u) name)
        (hashtable-set! (unit-type-names u) name #t)
        (add-class! u
          `(type ,name (base ,super) (interfaces ,@(deftype-proxy-ifaces d))
             (field ,fmap ,pmap-type)
             ,@(map (lambda (ps)
                      `(method ".ctor" (attrs ctor) (params ,@(map tname ps))
                         (body (ldarg 0) ,@(args-code (length ps)) (call ,super ".ctor" ,(map tname ps) ,void-type) (ret))))
                    (deftype-proxy-ctors d))
             (method "__initClojureFnMappings" (params ,pmap-type) (ret ,void-type) (implements ,iproxy "__initClojureFnMappings")
               (body (ldarg 0) (ldarg 1) (stfld ,name ,fmap) (ret)))
             (method "__updateClojureFnMappings" (params ,pmap-type) (ret ,void-type) (implements ,iproxy "__updateClojureFnMappings")
               (body (ldarg 0) (ldarg 0) (ldfld ,name ,fmap) (ldarg 1)
                     (call ,clojure-ops-type "ProxyUpdate" (,pmap-type ,pmap-type) ,pmap-type) (stfld ,name ,fmap) (ret)))
             (method "__getClojureFnMappings" (params) (ret ,pmap-type) (implements ,iproxy "__getClojureFnMappings")
               (body (ldarg 0) (ldfld ,name ,fmap) (ret)))
             ,@(map method-def (deftype-proxy-methods d))
             ,@(map super-def (filter (lambda (m) (string=? (list-ref m 6) "base")) (deftype-proxy-methods d))))))
      (cons name '())))

  (define (iota-from a n) (if (= n 0) '() (cons a (iota-from (+ a 1) (- n 1)))))

  ;; ———————————————— gen-class*（cleat-043）————————————————
  ;; 描述：(类名 genclass 基类 (接口 …) 实现命名空间 加载实现? ((Var 字段 Var 名) …) (((参数类型 …) (基类参数类型 …)) …)
  ;;        init 字段 post-init 字段 state 字段 (方法 …) factory main 字段 (exposes …) (exposes-methods …))，见 HostSyntax.GenClassLayout。
  ;; 方法经静态 Var 字段找实现（重载时先找 名字-参数类型… 的 Var，再找 名字 的 Var）：已绑定则以 (this 实参 …) 调用，
  ;; 否则调基类实现或抛 NotImplementedException。类型初始化器 intern 这些 Var 并（缺省）加载实现命名空间。
  (define (genclass-class! mc d)
    (let* ((u (mctx-unit mc))
           (name (list-ref d 0)) (super (list-ref d 2)) (ifaces (list-ref d 3)) (impl-ns (list-ref d 4)) (load-impl? (list-ref d 5))
           (var-fields (list-ref d 6)) (ctors (list-ref d 7)) (init (list-ref d 8)) (post-init (list-ref d 9)) (state (list-ref d 10))
           (methods (list-ref d 11)) (factory (list-ref d 12)) (main (list-ref d 13)) (exposes (list-ref d 14)) (exposes-methods (list-ref d 15)))
      (define (get-fn f) `((ldsfld ,name ,f) (call ,clojure-ops-type "GenClassFn" (,clj-var-type) ,ifn-type)))
      (define (not-defined what) `((ldstr ,(string-append what " is not defined for " name))
                                   (newobj "System.NotImplementedException" (,string-type)) (throw)))
      ;; 栈顶是 fn：压 this（可选）与实参 first..first+n-1，调用，结果（object）留在栈上
      (define (invoke-code this? first ptypes)
        (let* ((n (length ptypes)) (k (+ n (if this? 1 0))) (idx (iota-from first n)))
          (if (< k 20)
              `(,@(if this? '((ldarg 0)) '())
                ,@(apply append (map (lambda (t i) (boxed-arg-code i t)) ptypes idx))
                (callvirt ,ifn-type "invoke" ,(objs k) ,obj))
              `((ldc.i4 ,k) (newarr ,obj)
                ,@(if this? '((dup) (ldc.i4 0) (ldarg 0) (stelem.ref)) '())
                ,@(apply append (map (lambda (t i j) `((dup) (ldc.i4 ,j) ,@(boxed-arg-code i t) (stelem.ref)))
                                     ptypes idx (iota-from (if this? 1 0) n)))
                (call ,clojure-ops-type "InvokeIFn" (,ifn-type ,obj-array) ,obj)))))
      (define (method-def m)
        (let* ((mname (list-ref m 0)) (ret (list-ref m 1)) (ptypes (list-ref m 2)) (access (list-ref m 3)) (kind (list-ref m 4))
               (target (list-ref m 5)) (fallback (list-ref m 6)) (vf (list-ref m 7)) (ovf (list-ref m 8)) (ok? (list-ref m 9))
               (static? (string=? kind "static")) (n (length ptypes)))
          `(method ,mname
             (attrs ,@(cond ((string=? kind "override") `(override ,@(if (string=? access "protected") '(protected) '())))
                            ((string=? kind "implement") (if (string=? mname (cadr target)) '() '(private)))
                            ((string=? kind "new") '(virtual))
                            (else '(static))))
             (params ,@(map tname ptypes)) (ret ,(tname ret))
             ,@(if (string=? kind "implement") `((implements ,(car target) ,(cadr target))) '())
             (body
               ,@(if ok?
                     `(,@(if ovf `(,@(get-fn ovf) (dup) (brtrue FOUND) (pop)) '())
                       ,@(get-fn vf) (dup) (brfalse NOFN)
                       (label FOUND)
                       ,@(invoke-code (not static?) (if static? 0 1) ptypes)
                       ,@(convert-object-code ret)
                       (ret)
                       (label NOFN) (pop))
                     '())
               ,@(if (string=? fallback "base")
                     `((ldarg 0) ,@(args-code n) (call ,(car target) ,(cadr target) ,(map tname ptypes) ,(tname ret)) (ret))
                     (not-defined mname))))))
      (define (ctor-def c)
        (let* ((ps (car c)) (sps (cadr c)) (n (length ps))
               (same? (equal? (map tname ps) (map tname sps)))
               (pass-through `((ldarg 0) ,@(args-code n) (call ,super ".ctor" ,(map tname sps) ,void-type)))
               (nth '(call "clojure.lang.RT" "nth" ("System.Object" "System.Int32") "System.Object")))
          `(method ".ctor" (attrs ctor) (params ,@(map tname ps)) (locals ,obj)
             (body
               ,@(if init
                     `(,@(get-fn init) (dup) (brfalse NOINIT)
                       ,@(invoke-code #f 1 ps) (stloc 0)
                       (ldarg 0)
                       ,@(apply append
                           (map (lambda (t j) `((ldloc 0) (ldc.i4 0) ,nth (ldc.i4 ,j) ,nth ,@(convert-object-code t)))
                                sps (iota-from 0 (length sps))))
                       (call ,super ".ctor" ,(map tname sps) ,void-type)
                       ,@(if state `((ldarg 0) (ldloc 0) (ldc.i4 1) ,nth (stfld ,name ,state)) '())
                       (br AFTERINIT)
                       (label NOINIT) (pop)
                       ,@(if same? pass-through (not-defined "init"))
                       (label AFTERINIT))
                     (if same? pass-through (not-defined "init")))
               ,@(if post-init
                     `(,@(get-fn post-init) (dup) (brfalse NOPOST)
                       ,@(invoke-code #t 1 ps) (pop) (br ENDPOST)
                       (label NOPOST) (pop) ,@(not-defined "post-init")
                       (label ENDPOST))
                     '())
               (ret)))))
      (hashtable-set! (unit-type-names u) name #t)
      (add-class! u
        `(type ,name (base ,super) (interfaces ,@ifaces)
           ,@(map (lambda (vf) `(field ,(car vf) ,clj-var-type static)) var-fields)
           ,@(if state `((field ,state ,obj readonly)) '())
           (method ".cctor" (attrs cctor) (params)
             (body ,@(apply append
                       (map (lambda (vf)
                              `((ldstr ,impl-ns) (ldstr ,(cadr vf))
                                (call ,clj-var-type "internPrivate" (,string-type ,string-type) ,clj-var-type)
                                (stsfld ,name ,(car vf))))
                            var-fields))
                   ,@(if load-impl? `((ldstr ,impl-ns) (call ,clojure-ops-type "GenClassLoadImpl" (,string-type) ,void-type)) '())
                   (ret)))
           ,@(map ctor-def ctors)
           ,@(map method-def methods)
           ,@(if factory
                 (map (lambda (c)
                        (let ((ps (car c)))
                          `(method ,factory (attrs static) (params ,@(map tname ps)) (ret ,name)
                             (body ,@(let loop ((i 0)) (if (= i (length ps)) '() (cons `(ldarg ,i) (loop (+ i 1)))))
                                   (newobj ,name ,(map tname ps)) (ret)))))
                      ctors)
                 '())
           ,@(if main
                 `((method "Main" (attrs static) (params "System.String[]") (ret ,void-type)
                     (body (ldsfld ,name ,main) (ldarg 0)
                           (call ,clojure-ops-type "GenClassMain" (,clj-var-type "System.String[]") ,void-type) (ret))))
                 '())
           ,@(apply append
               (map (lambda (x)
                      (let ((decl (list-ref x 0)) (f (list-ref x 1)) (ft (list-ref x 2)) (static? (list-ref x 3))
                            (get (list-ref x 4)) (set (list-ref x 5)))
                        (append
                          (if get
                              `((method ,get ,@(if static? '((attrs static)) '()) (params) (ret ,ft)
                                  (body ,@(if static? `((ldsfld ,decl ,f)) `((ldarg 0) (ldfld ,decl ,f))) (ret))))
                              '())
                          (if set
                              `((method ,set ,@(if static? '((attrs static)) '()) (params ,ft) (ret ,void-type)
                                  (body ,@(if static? `((ldarg 0) (stsfld ,decl ,f)) `((ldarg 0) (ldarg 1) (stfld ,decl ,f))) (ret))))
                              '()))))
                    exposes))
           ,@(map (lambda (x)
                    (let ((pub (list-ref x 0)) (ret (list-ref x 1)) (ptypes (list-ref x 2)) (decl (list-ref x 3)) (sname (list-ref x 4)))
                      `(method ,pub (params ,@ptypes) (ret ,ret)
                         (body (ldarg 0) ,@(args-code (length ptypes)) (call ,decl ,sname ,ptypes ,ret) (ret)))))
                  exposes-methods)))
      (cons name '())))

  (define (tname t) (car t))
  (define (value-type? t) (cdr t))

  ;; 返回 (类名 . 自由变量)
  ;; reify* 的方法闭包捕获的外层变量（按出现顺序，去掉字段 x*）：构造参数，也是局部变量清除要计数的引用
  (define (deftype-caps x* le*)
    (fold-left (lambda (acc le)
                 (nanopass-case (L3 LambdaExpr) le
                   [(lambda ,nm (,x1* ...) ,cl* ...)
                    (fold-left (lambda (a v) (if (or (memq v a) (memq v x*)) a (append a (list v)))) acc x1*)]))
               '() le*))
  (define (deftype-class! mc d x* le*)
    (let* ((u (mctx-unit mc))
           (name (deftype-info-class d))
           (reify? (deftype-info-reify? d))
           (fnames (map car (deftype-info-fields d)))
           (caps (deftype-caps x* le*))
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
            (init-clearing! mc body)
            (parameterize ((clear-args-by-ref #f))
            (cond
              ((string=? (tname ret) void-type) (cg body mc 'effect) (emit! mc '(ret)))
              ((string=? (tname ret) obj) (cg body mc 'tail))
              (else (cg body mc 'value) (convert-return! mc ret) (emit! mc '(ret)))))
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

  (define (finish-method mc name attrs params . ret)
    `(method ,name (attrs ,@attrs) (params ,@params) (ret ,(if (pair? ret) (car ret) obj))
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
        (ref-var! mc (car fs) #t)
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
                        0 (make-eqv-hashtable) (make-hashtable string-hash string=?) (make-eq-hashtable))))
      ;; 1. 已知过程：库变量绑定的、无自由变量且不被再赋值的 lambda
      (for-each (lambda (f) (collect-known! u f)) forms)
      (hashtable-clear! var-types)
      (hashtable-clear! sound-types)
      (hashtable-clear! fixed-types)
      (hashtable-clear! var-facts)
      (hashtable-clear! common-lambdas)
      (hashtable-clear! assigned-vars)
      (for-each collect-assigned! forms)
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

  ;; 已知过程的 known 记录：共享持有类中的一个槽（034d），或单独的类（collect-known! 与 iron-070 的局部直接调用共用）
  (define (known-for-export u nm cl*)
    (if opt-shared?
        (let ((slot (shared-slot! u)))
          (make-known (car slot) (map clause-shape cl*) (car slot) (cdr slot)))
        (let ((name (class-name u nm)))
          (make-known name (map clause-shape cl*) name ""))))

  (define (collect-known! u form)
    (nanopass-case (L3 Expr) form
      [(library ,d ([,x* ,s* ,e*] ...) ,body)
       (for-each
         (lambda (x s e)
           (nanopass-case (L3 Expr) e
             [(lambda ,nm (,x1* ...) ,cl* ...)
              (when (and (null? x1*) (not (hashtable-ref (unit-massigned u) x #f)))
                (let ((k (known-for-export u nm cl*)))
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
