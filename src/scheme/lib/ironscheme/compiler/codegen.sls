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
  (define-target-type frame-type async-frame)
  (define-target-type task-type task)
  (define-target-type escape-type escape-continuation)
  (define-target-type cont-exn-type continuation-exception)
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
            const-index          ; eq 哈希表：常量 → 下标
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
            shared))              ; 034d：eqv 哈希表 持有类下标 → #(字段 方法 初始化代码)（各逆序）
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

  (define (constant-index! u d)
    (or (and (not (signed-zero-constant? d)) (hashtable-ref (unit-const-index u) d #f))
        (let ((k (unit-nconsts u)))
          (unless (signed-zero-constant? d) (hashtable-set! (unit-const-index u) d k))
          (unit-consts-set! u (cons d (unit-consts u)))
          (unit-nconsts-set! u (+ k 1))
          k)))

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
            vars                 ; eq 哈希表：变量 → (arg . i) | (local . i) | (field . 名字) | (self)
            (mutable locals)     ; 局部变量类型（逆序）
            (mutable nlocals)
            (mutable buffer)     ; 指令（逆序）
            (mutable nlabels)
            (mutable try-depth)
            loop-label           ; 自尾调用跳回的标签，#f 表示不做
            loop-arity           ; 当前子句的固定参数个数（有剩余参数时为 #f）
            self-mref            ; 已知过程自己的库变量（自尾调用识别），或 #f
            arg-base             ; 第一个形参的 ldarg 下标（实例方法为 1）
            (mutable async)      ; async-lambda 的 MoveNext：aframe 记录；否则 #f
            labels               ; eq 哈希表：label 的名字 → (IL 标签 . 形参表)（局部循环，iron-034b）
            (mutable big?)))     ; 方法体很大（只执行一次的测试过程之类）：不内联算术（iron-034f）

  (define (make-method-ctx u class static? loop-label loop-arity self-mref)
    (make-mctx u class static? (make-eq-hashtable) '() 0 '() 0 0 loop-label loop-arity self-mref (if static? 0 1) #f (make-eq-hashtable) #f))

  ;; code（指令树）里有没有 (br L)
  (define (branches-to? code l)
    (exists (lambda (i) (and (pair? i) (or (and (eq? (car i) 'br) (pair? (cdr i)) (eq? (cadr i) l)) (branches-to? i l)))) code))

  ;; ———————————————— async 方法上下文（designs/10-async.md §3.3 D12 ~ D13）————————————————
  ;;
  ;; async-lambda 的体编译成帧类（AsyncFrame 的子类）的 MoveNext：形参、局部变量、临时变量一律是帧的字段
  ;; （vars 表中为 (field . 名字)），挂起后不丢；恢复点 n 在方法开头按 state 分派（beq 链），
  ;; 恢复点在 try 区域里时外层分派到 try 入口前的标签、进入 try 后再分派一次（IL 不许从外面跳进 try）。
  (define-record-type aframe
    (fields class                ; 帧类名
            (mutable fields)     ; 字段名（逆序）
            (mutable nfields)
            (mutable regions)    ; 恢复点区域栈：每项是 ((n . 标签) …)，栈顶是当前最内层 try 区域
            (mutable nresume)
            end-label            ; MoveNext 末尾：挂起与完成都 leave 到这里
            (mutable scratch)    ; stfld 用的临时 IL 局部变量下标，按需分配
            unused-labels))      ; 未用（label 表在 mctx-labels）

  (define (new-field! mc)
    (let* ((af (mctx-async mc)) (n (aframe-nfields af)) (f (string-append "v" (number->string n))))
      (aframe-fields-set! af (cons f (aframe-fields af)))
      (aframe-nfields-set! af (+ n 1))
      f))

  (define (scratch! mc)
    (let ((af (mctx-async mc)))
      (or (aframe-scratch af)
          (let ((t (new-local! mc obj)))
            (aframe-scratch-set! af t)
            t))))

  ;; 在新的恢复点区域里生成代码，返回区域内登记的恢复点 ((n . 标签) …)
  (define (with-region mc thunk)
    (let ((af (mctx-async mc)))
      (aframe-regions-set! af (cons '() (aframe-regions af)))
      (thunk)
      (let ((entries (reverse (car (aframe-regions af)))))
        (aframe-regions-set! af (cdr (aframe-regions af)))
        entries)))

  (define (region-add! mc n label)
    (let ((af (mctx-async mc)))
      (aframe-regions-set! af (cons (cons (cons n label) (car (aframe-regions af))) (cdr (aframe-regions af))))))

  ;; 内层区域的恢复点在外层都分派到 pre（try 入口前）
  (define (propagate! mc entries pre)
    (for-each (lambda (e) (region-add! mc (car e) pre)) entries))

  (define (dispatch-code entries)
    (apply append
      (map (lambda (e) `((ldarg 0) (ldfld ,frame-type "state") (ldc.i4 ,(car e)) (beq ,(cdr e)))) entries)))

  ;; finally 守卫：挂起（state >= 0）时跳过用户的 finally 体
  (define (guarded-finally mc code)
    (let ((skip (new-label! mc)))
      `((ldarg 0) (ldfld ,frame-type "state") (ldc.i4 0) (bge ,skip) ,@code (label ,skip))))

  ;; 栈上的值作为结果完成
  (define (complete! mc)
    (let ((t (scratch! mc)))
      (emit! mc `(stloc ,t) '(ldarg 0) `(ldloc ,t)
             `(call ,frame-type "Complete" (,obj) ,void-type)
             `(leave ,(aframe-end-label (mctx-async mc))))))

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

  (define (contains-try? code) (exists (lambda (i) (eq? (car i) 'try)) code))

  ;; 依次生成多个操作数（每个 gen 产生一个值）。排在后面的操作数含 try 时，先全部存入临时变量（lift-try）。
  (define (operands! mc gens)
    (let ((codes (map (lambda (g) (with-buffer mc g)) gens)))
      (if (and (pair? codes) (exists contains-try? (cdr codes)))
          (let ((temps (map (lambda (c) (let ((t (new-local! mc obj))) (emit-code! mc c) (emit! mc `(stloc ,t)) t)) codes)))
            (for-each (lambda (t) (emit! mc `(ldloc ,t))) temps))
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
      ((tail) (if (mctx-async mc) (complete! mc) (emit! mc '(ret))))))

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

  ;; async 体内没有尾调用（10 §3.3 D13）：结果总是经 Complete 交出
  (define (tail-ok? mc ctx)
    (and (eq? ctx 'tail) (= (mctx-try-depth mc) 0) (not (mctx-async mc)) (target-tail-calls? (current-target))))

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
  ;; 候选：普通方法里 let 绑定的变量与 label 的形参（不在 async 体内、不被 set!、不被任何 lambda 捕获）。
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
    ;; 是否在 async-lambda 体内：可变变量，进出 lambda 时保存恢复；处理器只取一个参数，for-each 直接传处理器，
    ;; 不为每个节点新建闭包
    (definitions (define async? #f))
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
       (let ((saved async?)) (set! async? #f) (for-each Body cl*) (set! async? saved))]
      [(async-lambda ,nm (,x* ...) ,cl)
       (for-each exclude! x*)
       (let ((saved async?)) (set! async? #t) (Body cl) (set! async? saved))]
      [(letrec ([,x* ,le*] ...) ,body) (for-each Expr le*) (Expr body)]
      [(let ([,x* ,e*] ...) ,body)
       (unless async? (for-each (lambda (x e) (when (MaybeTyped e) (add-def! x e))) x* e*))
       (for-each Raw e*)
       (Expr body)]
      [(library ,d ([,x* ,s* ,e*] ...) ,body) (for-each Expr e*) (Expr body)]
      [(call ,e ,e* ...) (Expr e) (for-each (if (inline-op e (length e*)) Raw Expr) e*)]
      [(and ,e* ...) (for-each Expr e*)]
      [(or ,e* ...) (for-each Expr e*)]
      [(throw ,e) (Expr e)]
      [(break) (void)]
      [(try-finally ,e0 ,e1) (Expr e0) (Expr e1)]
      [(await ,e) (Expr e)]
      [(label ,x (,x* ...) ,body0 ,body1)
       (unless async?
         (hashtable-set! labels x x*)
         (for-each (lambda (p) (add-def! p #f)) x*))
       (Expr body0) (Expr body1)]
      [(jump ,x ,e* ...)
       (let ((ps (hashtable-ref labels x #f)))
         (when ps (for-each add-def! ps e*)))
       (for-each Raw e*)]
      [(escape-frame ,x ,body) (Expr body)]
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
      [else (void)])
    (Expr ir))

  ;; 过程体的 L3 节点数是否超过 n（数到 n 就停）；嵌套的 lambda 是别的方法，不数
  (define-pass count-nodes : L3 (ir n) -> * (v)
    (definitions
      (define count 0)
      (define (more?) (set! count (+ count 1)) (<= count n)))
    (Expr : Expr (ir) -> * (v)
      [(lambda ,nm (,x* ...) ,cl* ...) (void)]
      [(async-lambda ,nm (,x* ...) ,cl) (void)]
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
      [(await ,e) (when (more?) (Expr e))]
      [(label ,x (,x* ...) ,body0 ,body1) (when (more?) (Expr body0) (Expr body1))]
      [(jump ,x ,e* ...) (when (more?) (for-each Expr e*))]
      [(escape-frame ,x ,body) (when (more?) (Expr body))]
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
        ((field) (emit! mc '(ldarg 0) `(ldfld ,(mctx-class mc) ,(cdr loc))))
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
        ((field)
         ;; 帧字段（async）：值先存临时变量，再 ldarg 0 / stfld
         (if (mctx-async mc)
             (let ((t (scratch! mc)))
               (emit! mc `(stloc ,t) '(ldarg 0) `(ldloc ,t) `(stfld ,(mctx-class mc) ,(cdr loc))))
             (error 'codegen "cannot assign captured variable (should be boxed)" x)))
        (else (error 'codegen "cannot assign captured variable (should be boxed)" x)))))

  ;; 为词法变量分配存储：IL 局部变量；async 体内是帧字段。返回下标或字段名
  (define (bind-local! mc x)
    (cond
      ((and (not (mctx-async mc)) (not (mctx-big? mc)) (hashtable-ref var-types x #f))
       => (lambda (t)
            (let ((i (new-local! mc (type-clr t))))
              (hashtable-set! (mctx-vars mc) x (cons 'tlocal (cons i t)))
              i)))
      (else (bind-local-object! mc x))))

  (define (bind-local-object! mc x)
    (if (mctx-async mc)
        (let ((f (new-field! mc)))
          (hashtable-set! (mctx-vars mc) x (cons 'field f))
          f)
        (let ((i (new-local! mc obj)))
          (hashtable-set! (mctx-vars mc) x (cons 'local i))
          i)))

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

  ;; 异步节点只能出现在 async-lambda 的体里（lower-async 保证）
  (define (async-unsupported e)
    (syntax-violation 'codegen "async node outside an async-lambda body" (unparse-L3 e)))

  (define (cg e mc ctx)
    (nanopass-case (L3 Expr) e
      [,x (unless (eq? ctx 'effect) (load-var! mc x) (finish-value! mc ctx))]
      [(async-lambda ,nm (,x* ...) ,cl)
       (unless (eq? ctx 'effect)
         (let ((c (lambda-class! e mc #f #f)))
           (make-closure! mc c x* #f)
           (finish-value! mc ctx)))]
      [(await ,e)
       ;; <e>; Suspend(task, n) 为真则直接继续（快路径）；否则 leave 出去，恢复时按 state 分派回 L_n 再 Resume
       (unless (mctx-async mc) (async-unsupported e))
       (cg e mc 'value)
       (let* ((af (mctx-async mc)) (n (aframe-nresume af)) (l (new-label! mc)) (t (scratch! mc)))
         (aframe-nresume-set! af (+ n 1))
         (region-add! mc n l)
         (emit! mc `(stloc ,t) '(ldarg 0) `(ldloc ,t) `(ldc.i4 ,n)
                `(call ,frame-type "Suspend" (,obj ,int32-type) ,bool-type)
                `(brtrue ,l) `(leave ,(aframe-end-label af))
                `(label ,l) '(ldarg 0) `(call ,frame-type "Resume" () ,obj))
         (finish-value! mc ctx))]
      [(label ,x (,x* ...) ,body0 ,body1)
       ;; 局部循环（10 §3.2 D9；iron-034b 起普通代码也有）：形参是 IL 局部变量（async 下是帧字段），
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
      [(escape-frame ,x ,body)
       ;; 内联的 call/cc（10 §3.2 D10）：k = new EscapeContinuation；try { 值 = body } catch (ContinuationException e)
       ;; { e.Continuation == k ? 值 = e.Value : rethrow } finally { 未挂起时 k.Invalidate() }
       (unless (mctx-async mc) (async-unsupported e))
       (bind-local! mc x)
       (emit! mc `(newobj ,escape-type ()))
       (store-var! mc x)
       (let* ((tmp (new-local! mc obj))
              (exc (new-local! mc obj))
              (pre (new-label! mc)) (after (new-label! mc)) (match (new-label! mc))
              (entries #f)
              (b (begin
                   (mctx-try-depth-set! mc (+ (mctx-try-depth mc) 1))
                   (let ((code (with-buffer mc (lambda ()
                                                 (set! entries (with-region mc (lambda () (cg body mc 'value))))
                                                 (emit! mc `(stloc ,tmp) `(leave ,after))))))
                     (mctx-try-depth-set! mc (- (mctx-try-depth mc) 1))
                     code)))
              (handler
               `((stloc ,exc)
                 (ldloc ,exc) (castclass ,cont-exn-type) (callvirt ,cont-exn-type "get_Continuation" () ,escape-type)
                 (ldarg 0) (ldfld ,(mctx-class mc) ,(cdr (var-location mc x)))
                 (beq ,match)
                 (rethrow)
                 (label ,match)
                 (ldloc ,exc) (castclass ,cont-exn-type) (callvirt ,cont-exn-type "get_Value" () ,obj)
                 (stloc ,tmp)
                 (leave ,after)))
              (cleanup
               `((ldarg 0) (ldfld ,(mctx-class mc) ,(cdr (var-location mc x))) (castclass ,escape-type)
                 (callvirt ,escape-type "Invalidate" () ,void-type))))
         (unless (null? entries) (propagate! mc entries pre) (emit! mc `(label ,pre)))
         (emit! mc `(try (body ,@(dispatch-code entries) ,@b)
                         (catch ,cont-exn-type ,@handler)
                         (finally ,@(if (null? entries) cleanup (guarded-finally mc cleanup))))
                `(label ,after) `(ldloc ,tmp))
         (finish-value! mc ctx))]
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
                  (fill-fields! mc c (remq x x1*) (lambda () (load-var! mc x)))]
                 [(async-lambda ,nm (,x1* ...) ,cl)
                  (fill-fields! mc c (remq x x1*) (lambda () (load-var! mc x)))])))
           x* classes le*)
         (cg body mc ctx))]
      [(let ([,x* ,e*] ...) ,body)
       ;; 右边依次求值并存入新变量（IL 局部变量；async 体内是帧字段；有类型的是 int32 / double 局部变量）；
       ;; 右边看不到同一 let 绑定的变量
       (let ((codes (map (lambda (x e)
                           (with-buffer mc (lambda ()
                                             (let ((t (and (not (mctx-async mc)) (not (mctx-big? mc)) (hashtable-ref var-types x #f))))
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
       (let* ((tmp (and (not (eq? ctx 'effect)) (new-local! mc obj)))
              (entries '())
              (body (begin
                      (mctx-try-depth-set! mc (+ (mctx-try-depth mc) 1))
                      (let ((b (with-buffer mc (lambda ()
                                                 (let ((gen (lambda () (cg e0 mc (if tmp 'value 'effect)) (when tmp (emit! mc `(stloc ,tmp))))))
                                                   (if (mctx-async mc) (set! entries (with-region mc gen)) (gen))))))
                            (f (with-buffer mc (lambda () (cg e1 mc 'effect)))))
                        (mctx-try-depth-set! mc (- (mctx-try-depth mc) 1))
                        (cons b f)))))
         (if (null? entries)
             (emit! mc `(try (body ,@(car body)) (finally ,@(cdr body))))
             ;; 体内有恢复点（async）：外层分派到 try 入口前，进入 try 后再分派；finally 加 state 守卫
             (let ((pre (new-label! mc)))
               (propagate! mc entries pre)
               (emit! mc `(label ,pre)
                      `(try (body ,@(dispatch-code entries) ,@(car body))
                            (finally ,@(guarded-finally mc (cdr body)))))))
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
      [(lambda ,nm (,x1* ...) ,cl* ...) (procedure-class! mc nm x1* cl* #f self self-mref preset)]
      [(async-lambda ,nm (,x1* ...) ,cl) (procedure-class! mc nm x1* (list cl) #t self self-mref preset)]))

  ;; async? 为真时（10 §3.3 D12）子句方法 C0 新建帧、复制自由变量与实参并 Start，帧类另外生成
  ;; preset：已知过程的 known 记录（类名 / 持有类与前缀已在 collect-known! 中确定）
  (define (procedure-class! mc nm x1* cl* async? self self-mref preset)
    (let ((fields (if self (remq self x1*) x1*)))
      (if (and opt-shared? (null? fields) (not async?))
          (shared-procedure! mc nm cl* self self-mref (and (pair? preset) (car preset)))
          (procedure-class1! mc nm x1* cl* async? self self-mref preset))))

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

  (define (procedure-class1! mc nm x1* cl* async? self self-mref preset)
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
             (add-method! (if async?
                              (async-clause-method u name static? (car cl*) fields field-names self x1*)
                              (clause-method u name static? (car cl*) i fields field-names self self-mref)))
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

  ;; async-lambda 的子句方法（10 §3.3 D12 ~ D13）：
  ;;   帧类 名字$Frame : AsyncFrame，字段 = 自由变量（含 letrec 中的自己）+ 形参 + 体内的变量，override MoveNext；
  ;;   C0(args) = newobj 帧；复制自由变量与实参到字段；call Start；ret（Task<object> 当 object 返回）。
  ;;   MoveNext：prev = Enter(this); try { 分派; 体（尾上下文 → Complete; leave END） } catch (Exception e) { Fail(e) }
  ;;             finally { Exit(prev) } END: ret
  (define (async-clause-method u class static? cl fields field-names self x1*)
    (nanopass-case (L3 Clause) cl
      [(clause (,x* ...) ,x ,body)
       (let* ((frame (string-append class "$Frame"))
              (params (if x (append x* (list x)) x*))
              (end (string->symbol "END"))
              (af (make-aframe frame '() 0 (list '()) 0 end #f (make-eq-hashtable)))
              (mc (make-method-ctx u frame #f #f #f #f)))
         (mctx-async-set! mc af)
         (let* ((fv-names (map (lambda (v) (bind-local! mc v)) x1*))
                (p-names (map (lambda (p) (bind-local! mc p)) params))
                (t (scratch! mc))
                (prev (new-local! mc obj)))
           (cg body mc 'tail)
           (let ((entries (reverse (car (aframe-regions af))))
                 (code (reverse (mctx-buffer mc))))
             (add-class! u
               `(type ,frame (base ,frame-type) (attrs sealed)
                  ,@(map (lambda (f) `(field ,f ,obj)) (reverse (aframe-fields af)))
                  (method ".ctor" (attrs ctor) (params)
                    (body (ldarg 0) (call ,frame-type ".ctor" () ,void-type) (ret)))
                  (method "MoveNext" (attrs override) (params) (ret ,void-type)
                    (locals ,@(reverse (mctx-locals mc)))
                    (,(interop 'namespaces))
                    (body
                      (ldarg 0) (call ,frame-type "Enter" (,frame-type) ,frame-type) (stloc ,prev)
                      (try (body ,@(dispatch-code entries) ,@code)
                           (catch ,exception-type
                             (stloc ,t) (ldarg 0) (ldloc ,t) (castclass ,exception-type)
                             (call ,frame-type "Fail" (,exception-type) ,void-type))
                           (finally (ldloc ,prev) (castclass ,frame-type) (call ,frame-type "Exit" (,frame-type) ,void-type)))
                      (label ,end)
                      (ret)))))
             ;; C0：在过程类的上下文里建帧
             (let ((arg-base (if static? 0 1)))
               `(method "C0" (attrs ,@(if static? '(static) '())) (params ,@(objs (length params))) (ret ,obj)
                  (body
                    (newobj ,frame ())
                    ,@(apply append
                        (map (lambda (v f)
                               `((dup)
                                 ,@(cond
                                     ((eq? v self) (if static? `((ldsfld ,class "Instance")) '((ldarg 0))))
                                     (else
                                      (let loop ((fs fields) (ns field-names))
                                        (cond
                                          ((null? fs) (error 'codegen "free variable not in procedure fields" v))
                                          ((eq? (car fs) v) `((ldarg 0) (ldfld ,class ,(car ns))))
                                          (else (loop (cdr fs) (cdr ns)))))))
                                 (stfld ,frame ,f)))
                             x1* fv-names))
                    ,@(apply append
                        (let loop ((ps p-names) (j arg-base))
                          (if (null? ps) '() (cons `((dup) (ldarg ,j) (stfld ,frame ,(car ps))) (loop (cdr ps) (+ j 1))))))
                    (call ,frame-type "Start" () ,task-type)
                    (ret)))))))]))

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
                        0 (make-eqv-hashtable))))
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
             [(async-lambda ,nm (,x1* ...) ,cl)
              (when (and (null? x1*) (not (hashtable-ref (unit-massigned u) x #f)))
                (let* ((name (class-name u nm))
                       (k (make-known name (list (clause-shape cl)) name "")))
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
