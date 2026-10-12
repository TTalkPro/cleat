;;; Clojure 的命名空间模型（cleat-049，designs/14 §6、designs/13 §3）。按需加载，不进 Scheme boot 镜像。
;;;
;;; 一、Clojure 数据 ↔ psyntax 数据（clj->scheme / scheme->clj / syntax->clj）
;;;   wrap 前把 reader 给出的 Clojure 数据转成 psyntax 认识的形状：
;;;   - clojure.lang.Symbol → 驻留的 Scheme 符号，名字为 Symbol.ToString()（`name` 或 `ns/name`），
;;;     包成注解 (make-annotation Scheme符号 源位置 原Clojure符号)：标识符认 expression（boot 的 id?），
;;;     syntax->datum 得回 stripped（原符号，带元数据）。符号自己没有位置，用外层列表的位置（与 C# 展开器报错的位置相同）。
;;;   - 非空 Clojure 列表 / seq → Scheme 列表（psyntax 的应用、宏、syntax-case 都按序对工作），注解的 stripped 为原对象；
;;;     空列表 () 保持原对象（它与 nil 不同；Clojure 的 nil 是 CLR null，即 Scheme 的 #nil，cleat-058）。
;;;   - 向量 / map / set → 用 048 登记的 rebuild 转换子项，注解的 stripped 为原对象。
;;;   - 其他（数、字符串、关键字、nil）不变。
;;;   于是未被宏改动的部分 syntax->datum 得回原对象（eq?）；模板产物里的 Scheme 符号 / 序对由 scheme->clj 转回 Clojure。
;;;
;;; 二、命名空间 = 环境
;;;   每个 clojure.lang.Namespace 一个 boot 的查找环境（make-lookup-environment）：顶层 rib 不存条目，
;;;   标识符经过程 (ns-lookup ns) 现查 Namespace 的映射与别名——运行时的 refer / alias / import / intern / ns-unmap
;;;   与展开器看到的是同一张表。词法绑定（let / lambda 的 rib）在 subst 里位于它之前，照常遮蔽。
;;;   解析规则 resolve-in 照 C# 展开器的 Resolve 与 Clojure 的 Compiler.maybeResolveIn，展开器、resolve、ns-resolve 与
;;;   错误信息共用它。
;;;   标签：每个 Var 一个 gensym（Var 为宏时用另一个），Var 对象即身份，所以重定义（同一个 Var）标签不变；
;;;   绑定 (kind . value) 放在标签的 symbol-value，即 psyntax 的全局标签表（imported-label->binding），boot 不必改。
;;;   绑定种类（经 048 的绑定扩展表登记）：
;;;     clj-var      值为 Var：引用 → 编译器的 Var 节点 $clj-var-ref（getRawRoot；动态 Var 为 get；:const 为常量），
;;;                  set! → $clj-var-set!（Var.set）（cleat-051）；
;;;                  以它开头的调用交给 set-var-call-handler! 登记的过程（(clojure syntax)：:inline、instance?、IFn.invoke）
;;;     clj-macro    值为转换器（defmacro 垫片：Clojure 数据进、Clojure 数据出、使用处上下文，&env 由
;;;                  set-macro-env-provider! 给出；Var 元数据 :hygienic 为真时是卫生宏：语法对象进、语法对象出，
;;;                  cleat-057）；macroexpand-1 展开它
;;;     clj-type     值为 Type：引用 → 常量
;;;     clj-special  值为转换器；按未限定名字登记（register-clj-special!），先于命名空间映射；macroexpand-1 不展开它
;;;     clj-form     值为过程 (e expand) → 核心形式（register-clj-form!，cleat-050 的特殊形式）；macroexpand-1 不展开它
;;;   特殊形式只在列表头部起作用、不被词法绑定遮蔽（Clojure 的规则）：clj->scheme 把头部是特殊形式名的列表的头部换成
;;;   该特殊形式专用的 gensym（注解的 stripped 仍是原符号），查找过程按 gensym 找到它；不在头部的同名符号照常解析。
;;;   解析不到的符号交给 set-unresolved-lookup! 登记的过程（(clojure syntax) 用它处理 .method、Type.、Type/member 等宿主语法）。
;;;
;;; 三、错误：clojure-eval 等入口在 syntax-error-hook 下运行，语法错误改抛 Cleat.Core.Expand.ExpanderException
;;;   （"Syntax error (file:line:col) message"）；"unbound identifier" 的文本由 resolve-in 给出具体原因。
;;;
;;; 入口 eval-in-ns / expand-in-ns / macroexpand-1-in-ns 只有本库的绑定种类；用户代码用 (clojure syntax) 的
;;; clojure-eval 等（它们引用 (clojure syntax) 的变量，库体才会执行、特殊形式才会登记）。

(library (clojure namespaces)
  (export clj->scheme scheme->clj syntax->clj clj-datum->syntax*
          clj-symbol clj-symbol? scheme-symbol->clj
          current-ns find-ns ns-name* in-ns! with-ns-binding refer! alias! import!
          resolve-in clj-ns-resolve clj-resolve
          namespace-for namespace-environment eval-in-ns expand-in-ns macroexpand-1-in-ns macroexpand-in-ns
          symbol-label register-clj-special! register-clj-form! register-hidden-clj-form! var-label type-label make-label
          set-unresolved-lookup! set-macro-env-provider! set-var-call-handler!
          clojure-expanding? clojure-form? clj-op var-reference macro-transformer direct-link-target make-identity-table
          clojure-error-hook expander-exception syntax-position source-path
          macro-var-head call-macro
          hygienic-var? special-head-key fix-special-head mixed->syntax)
  (import
    (rnrs)
    (rnrs eval)
    (only (cleat scheme) gensym set-symbol-value! expand->core with-clr-exception-handler parameterize make-parameter)
    (only (cleat scheme core) stx? stx-expr stx-mark* stx-subst*)
    (only (cleat scheme reader) make-annotation annotation? annotation-source annotation-stripped annotation-expression)
    (only (cleat scheme unsafe) $throw $try/finally)
    (cleat scheme syntax-ext)
    (cleat scheme clr)
    (clojure syntax-kinds))

  ;;; —— 互操作小工具 ——

  ;;; Clojure 值的运行时操作：C# 过程（Cleat.Scheme.Clojure.ClojureOps），实参与返回值不经 Scheme 互操作的转换（cleat-050）
  (define-syntax clj-op
    (syntax-rules ()
      ((_ name) (clr-static-field-get Cleat.Scheme.Clojure.ClojureOps name))))

  (define (clj-symbol name) (clr-static-call clojure.lang.Symbol (intern System.String) name))
  (define (clj-symbol? x) (clr-is clojure.lang.Symbol x))
  (define (sym-ns s) (let ((n (clr-call clojure.lang.Symbol get_Namespace s))) (if (nil? n) #f n)))
  (define (sym-name s) (clr-call clojure.lang.Symbol get_Name s))
  (define (->string x) (clr-call System.Object ToString x))
  (define (->csym x) (cond ((clj-symbol? x) x) ((symbol? x) (clj-symbol (symbol->string x))) (else (clj-symbol x))))
  (define (var? x) (clr-is clojure.lang.Var x))
  (define (type? x) (clr-is System.Type x))
  (define (var-ns v) (clr-call clojure.lang.Var get_Namespace v))
  (define (var-public? v) (clr-call clojure.lang.Var get_isPublic v))
  (define (var-macro? v) (clr-call clojure.lang.Var get_IsMacro v))
  (define (var-dynamic? v) (clr-call clojure.lang.Var isDynamic v))
  (define (nil->f x) (if (nil? x) #f x))
  (define current-ns-var (clr-static-field-get clojure.lang.RT CurrentNSVar))

  (define (current-ns) (clr-call clojure.lang.Var deref current-ns-var))
  (define (find-ns name) (nil->f (clr-static-call clojure.lang.Namespace find (->csym name))))
  (define (ns-name* ns) (->string (clr-call clojure.lang.Namespace get_Name ns)))

  ;;; Scheme 符号 ↔ Clojure 符号（名字相同；Clojure 符号不驻留，缓存去掉元数据的那个）
  (define symbol-cache (make-eq-hashtable))
  (define (scheme-symbol->clj s)
    (or (hashtable-ref symbol-cache s #f)
        (let ((c (clj-symbol (symbol->string s))))
          (hashtable-set! symbol-cache s c)
          c)))
  (define (clj-symbol->scheme c) (string->symbol (->string c)))

  ;;; —— 一、数据转换 ——

  (define (clj-kind x)
    (let ((e (syntax-extension-of x)))
      (and e (memq e clojure-syntax-kinds) e)))

  ;;; 特殊形式名（未限定，字符串）→ 专用的 gensym（clj->scheme 换掉列表头部用）
  (define special-keys (make-hashtable string-hash string=?))
  ;;; Clojure 的特殊形式表里 import* 是限定的 clojure.core/import*（core.clj 的 import 宏展开成它，cleat-053）
  (define (special-head-key x)
    (and (clj-symbol? x)
         (or (not (sym-ns x))
             (and (string=? (sym-ns x) "clojure.core") (string=? (sym-name x) "import*")))
         (hashtable-ref special-keys (sym-name x) #f)))

  (define (clj->scheme x)
    (let convert ((x x) (pos #f))
      (cond
        ((clj-symbol? x) (make-annotation (clj-symbol->scheme x) pos x))
        ((clj-kind x) =>
         (lambda (e)
           (let ((kids ((syntax-extension-children e) x)))
             (if (null? kids)
                 x
                 (let* ((own (or (clojure-source-of x) pos))
                        (new (map (lambda (k) (convert k own)) kids)))
                   (if (eq? (syntax-extension-kind e) 'clj-list)
                       (let ((key (special-head-key (car kids))))
                         (make-annotation (if key (cons (make-annotation key own (car kids)) (cdr new)) new) own x))
                       (make-annotation ((syntax-extension-rebuild e) x new) own x)))))))
        (else x))))

  ;;; strip 之后的数据 → Clojure 数据：Scheme 符号 → Clojure 符号，序对 → PersistentList，Clojure 集合按子项重建
  (define (scheme->clj x)
    (cond
      ((symbol? x) (scheme-symbol->clj x))
      ;; Scheme 的 '()（EmptyList）是空表，对应 Clojure 的 ()（PersistentList.EMPTY）；Clojure 的 nil 是 #nil（cleat-058）
      ((null? x) (clj-list))
      ((pair? x)
       (let loop ((x x) (acc '()))
         (cond
           ((pair? x) (loop (cdr x) (cons (scheme->clj (car x)) acc)))
           ((null? x) (apply clj-list (reverse acc)))
           (else (assertion-violation 'scheme->clj "improper list in Clojure form" x)))))
      ((clj-kind x) =>
       (lambda (e)
         (let* ((old ((syntax-extension-children e) x))
                (new (map scheme->clj old)))
           (if (for-all eq? old new) x ((syntax-extension-rebuild e) x new)))))
      (else x)))

  (define (syntax->clj stx) (scheme->clj (syntax->datum stx)))

  ;;; Clojure 数据 → 语法对象，取 id 的上下文（defmacro 垫片的"使用处上下文"）
  (define (clj-datum->syntax* id x) (datum->syntax id (clj->scheme x)))

  ;;; —— 二、解析（照 C# Expander.Resolve / Compiler.maybeResolveIn）——
  ;;; (resolve-in ns csym) → (values 对象 #f #f) 或 (values #f 错误文本 private?)

  (define ns-sym (clj-symbol "ns"))
  (define in-ns-sym (clj-symbol "in-ns"))

  (define (string-index s ch)
    (let loop ((i 0))
      (cond ((= i (string-length s)) #f) ((char=? (string-ref s i) ch) i) (else (loop (+ i 1))))))

  (define (class-for-name name) (nil->f (clr-static-call clojure.lang.RT (classForName System.String) name)))

  (define (namespace-for ns nsname)
    (let ((s (clj-symbol nsname)))
      (or (nil->f (clr-call clojure.lang.Namespace LookupAlias ns s))
          (find-ns s))))

  (define (resolve-in ns sym)
    (let ((nsname (sym-ns sym)) (name (sym-name sym)))
      (if nsname
          (let ((target (namespace-for ns nsname)))
            (if (not target)
                (values #f (string-append "No such namespace: " nsname) #f)
                (let ((v (nil->f (clr-call clojure.lang.Namespace FindInternedVar target (clj-symbol name)))))
                  (cond
                    ((not v) (values #f (string-append "No such var: " (->string sym)) #f))
                    ((and (not (eq? (var-ns v) ns)) (not (var-public? v)))
                     (values #f (string-append "var: " (->string sym) " is not public") #t))
                    (else (values v #f #f))))))
          (let ((dot (string-index name #\.)))
            (cond
              ((or (and dot (> dot 0))
                   (char=? (string-ref name (- (string-length name) 1)) #\]))
               (let ((t (class-for-name name)))
                 (if t (values t #f #f) (values #f (string-append "Unable to resolve classname: " name) #f))))
              ((clr-call System.Object Equals sym ns-sym) (values (clr-static-field-get clojure.lang.RT NSVar) #f #f))
              ((clr-call System.Object Equals sym in-ns-sym) (values (clr-static-field-get clojure.lang.RT InNSVar) #f #f))
              (else
               (let ((o (nil->f (clr-call clojure.lang.Namespace GetMapping ns (clj-symbol name)))))
                 (if o
                     (values o #f #f)
                     (values #f (string-append "Unable to resolve symbol: " (->string sym) " in this context") #f)))))))))

  ;;; Clojure 的 ns-resolve / resolve：解析不到为 nil（#nil），私有 Var 抛 IllegalStateException
  (define (clj-ns-resolve ns sym)
    (let-values (((o msg private?) (resolve-in ns (->csym sym))))
      (cond
        (o o)
        (private? ($throw (clr-new System.InvalidOperationException msg)))
        (else #nil))))
  (define (clj-resolve sym) (clj-ns-resolve (current-ns) sym))

  ;;; —— 标签与绑定 ——

  (define (make-label binding)
    (let ((l (gensym)))
      (set-symbol-value! l binding)
      l))

  ;;; Var → (值标签 . 宏标签)；Type → 标签。表不弱：ns-unmap 掉的 Var 留在表里（见结论）
  (define var-labels (make-eq-hashtable))
  (define type-labels (make-eq-hashtable))

  (define (var-label v)
    (let ((p (or (hashtable-ref var-labels v #f)
                 (let ((p (cons (make-label (cons 'clj-var v))
                                (make-label (cons 'clj-macro (macro-transformer v))))))
                   (hashtable-set! var-labels v p)
                   p))))
      (if (var-macro? v) (cdr p) (car p))))

  (define (type-label t)
    (or (hashtable-ref type-labels t #f)
        (let ((l (make-label (cons 'clj-type t))))
          (hashtable-set! type-labels t l)
          l)))

  ;;; 特殊形式：gensym 键 → 标签。名字（未限定）→ 键在 special-keys，clj->scheme 只在列表头部换成键
  (define specials (make-eq-hashtable))
  (define (special-key! name)
    (let ((n (if (symbol? name) (symbol->string name) (->string name))))
      (or (hashtable-ref special-keys n #f)
          (let ((k (gensym n)))
            (hashtable-set! special-keys n k)
            k))))
  (define (register-clj-special! name transformer)
    (hashtable-set! specials (special-key! name) (make-label (cons 'clj-special transformer))))
  ;;; 过程式的特殊形式：(proc e expand) → 核心形式，expand 在当前环境展开一个子形式（cleat-050）
  (define (register-clj-form! name proc)
    (hashtable-set! specials (special-key! name) (make-label (cons 'clj-form proc))))
  ;;; 只能由展开器产生的形式（如 (clojure syntax) 的 %ctx）：返回键，用 (datum->syntax 使用处标识符 键) 引用
  (define (register-hidden-clj-form! name proc)
    (let ((k (gensym name)))
      (hashtable-set! specials k (make-label (cons 'clj-form proc)))
      k))

  ;;; 解析不到的符号的后备：(proc ns 符号) → 标签或 #f
  (define unresolved-lookup #f)
  (define (set-unresolved-lookup! proc) (set! unresolved-lookup proc))

  ;;; 查找 rib 的过程：特殊形式 → 命名空间 → 后备（标记不参与：Clojure 按名字解析）
  (define (ns-lookup ns)
    (lambda (sym mark*)
      (or (hashtable-ref specials sym #f)
          (let ((csym (scheme-symbol->clj sym)))
            (let-values (((o msg private?) (resolve-in ns csym)))
              (cond
                ((var? o) (var-label o))
                ((type? o) (type-label o))
                ((and (not o) (not private?) unresolved-lookup) (unresolved-lookup ns csym))
                (else #f)))))))

  ;;; 符号在 ns（缺省 *ns*）里的标签，或 #f（测试与 &env 用）
  (define symbol-label
    (case-lambda
      ((sym) (symbol-label sym (current-ns)))
      ((sym ns)
       ((ns-lookup ns) (if (symbol? sym) sym (clj-symbol->scheme (->csym sym))) '()))))

  (define environments (make-eq-hashtable))
  (define (namespace-environment ns)
    (or (hashtable-ref environments ns #f)
        (let ((env (make-lookup-environment (ns-lookup ns))))
          (hashtable-set! environments ns env)
          env)))

  ;;; —— 绑定种类的展开 ——

  (define here #'here)
  (define (const x) (datum->syntax here x))

  (define kw-const (clj-keyword "const"))
  (define op-var-get (clj-op VarGet))
  (define op-meta (clj-op Meta))
  (define op-get (clj-op Get))

  ;;; 接口文件里的已知函数类（cleat-033）：Var 的条目有 invokeStatic 且 arity 相符时返回 "类, 程序集"，否则 #f
  (define (direct-link-target v argc)
    (clr-static-call Cleat.Scheme.Clojure.Driver DirectLinkTarget v argc))

  ;;; Var 的读（核心形式）：:const 的 Var 为常量，否则编译器的 Var 节点（cleat-051）：动态 Var 为 Var.get，否则 getRawRoot
  ;;; （与 C# 展开器的 var-ref 相同）。Var 在 Lcore 里是常量：进程内求值为对象本身，保存到 dll 时编码为 (var ns name)，加载时 intern
  (define (var-reference v)
    (if (nil->f (op-get (op-meta v) kw-const))
        (list 'quote (op-var-get v))
        (list '$clj-var-ref (list 'quote v) (list 'quote (and (var-dynamic? v) #t)))))

  ;;; 以 Var 开头的调用：(clojure syntax) 登记（:inline、instance?、IFn.invoke）；没有登记时按 IFn 调用，
  ;;; 但 Var 是接口文件里的已知函数（:static 条目，参数个数恰为某固定 arity）时直接 call 类::invokeStatic
  ;;; （$clj-static-call，跨单元直接链接，cleat-033，designs/04 §6）
  (define var-call-handler #f)
  (define (set-var-call-handler! proc) (set! var-call-handler proc))
  (define (default-var-call e v expand)
    (syntax-case e ()
      ((_ arg ...)
       (let* ((n (length #'(arg ...)))
              (cls (direct-link-target v n)))
         (if cls
             (cons* '$clj-static-call (list 'quote cls) (list 'quote n) (map expand #'(arg ...)))
             (cons* '$clj-invoke (var-reference v) (map expand #'(arg ...))))))))

  ;;; 当前展开中的标识符 e（语法对象）对应的 Clojure 符号
  (define (form-symbol e) (syntax->clj e))

  (define (unresolvable e)
    (syntax-violation #f (string-append "Unable to resolve symbol: " (->string (form-symbol e)) " in this context") e))

  (define (register-kinds!)
    (register-binding-extension!
      (make-binding-extension 'clj-var
        (lambda (e v expand) (var-reference v))
        (lambda (e v expand)
          (syntax-case e ()
            ((_ id val)
             (list '$clj-var-set! (list 'quote v) (expand #'val)))))
        (lambda (e v expand) ((or var-call-handler default-var-call) e v expand))
        #f))
    (register-binding-extension!
      (make-binding-extension 'clj-macro
        (lambda (e t expand)
          (let-values (((o msg p?) (resolve-in (current-ns) (form-symbol e))))
            (syntax-violation #f (string-append "Can't take the value of a macro: " (->string o)) e)))
        #f #t #t))
    (register-binding-extension!
      (make-binding-extension 'clj-type
        ;; deftype* 方法体里的桩类型（cleat-051）：运行时换成真正的类型
        (lambda (e t expand)
          (if (clr-static-call Cleat.Scheme.Clojure.HostSyntax IsStub t)
              (list '$clj-call (list 'quote "RealType") (list 'quote #f) (list 'quote t))
              (list 'quote t)))
        #f #f #f))
    (register-binding-extension!
      (make-binding-extension 'clj-special
        (lambda (e t expand) (unresolvable e))
        #f #t #f))
    (register-binding-extension!
      (make-binding-extension 'clj-form
        (lambda (e t expand) (unresolvable e))
        #f
        (lambda (e proc expand) (proc e expand))
        #f)))

  ;;; —— defmacro 垫片：输入剥成 Clojure 数据（&form），(&form &env 实参…) 调宏函数，输出挂使用处上下文 ——
  ;;; 与 Clojure 完全相同的不卫生语义：宏输出里的符号按使用处解析。&env 由 set-macro-env-provider! 登记的过程给出
  ;;; （(clojure syntax) 给出词法局部的 符号 → LocalBinding map），没有登记时为 nil。

  (define macro-env-provider (lambda () #nil))
  (define (set-macro-env-provider! thunk) (set! macro-env-provider thunk))

  (define op-apply-to (clj-op ApplyTo))

  (define (apply-macro v form env)
    (op-apply-to v
      (clr-static-call clojure.lang.RT (cons System.Object System.Object) form
        (clr-static-call clojure.lang.RT (cons System.Object System.Object) env
          (clr-static-call clojure.lang.RT (next System.Object) form)))))

  (define (munge s) (clr-static-call clojure.lang.Munge munge s))

  ;;; ns$name__1234 → ns$name
  (define (remove-fn-suffix name)
    (let loop ((i 0))
      (cond
        ((>= (+ i 1) (string-length name)) name)
        ((and (char=? (string-ref name i) #\_) (char=? (string-ref name (+ i 1)) #\_)) (substring name 0 i))
        (else (loop (+ i 1))))))

  ;;; 宏函数的 ArityException 隐去 &form / &env 两个参数（Clojure 的 macroexpand1 的规则）
  (define (macro-arity-fix v ex)
    (if (clr-is clojure.lang.ArityException ex)
        (let ((name (clr-call clojure.lang.ArityException get_Name ex)))
          (if (string=? (remove-fn-suffix name)
                        (string-append (munge (ns-name* (var-ns v))) "$" (munge (sym-name (clr-call clojure.lang.Var get_Symbol v)))))
              (clr-new clojure.lang.ArityException (- (clr-call clojure.lang.ArityException get_Actual ex) 2) name)
              ex))
        ex))

  ;;; 宏调用：v 为宏 Var，form 为 Clojure 数据，pos 为错误位置；返回展开结果（Clojure 数据）
  ;;; 宏抛出的异常按 Clojure 1.12 的 Compiler.macroexpand1 分阶段（cleat-055，与 JVM 的输出对照）：
  ;;;   宏函数的 ArityException（隐去 &form / &env 后）→ compile-syntax-check，无符号（"Syntax error (ArityException) compiling at"）；
  ;;;   ArgumentException / InvalidOperationException / IExceptionInfo / 恰为 System.Exception（JVM 的受检异常）→ macro-syntax-check；
  ;;;   其余 → macroexpand（"Unexpected error (类型) macroexpanding m at"）。符号是形式里写的运算符。
  (define (call-macro v form pos)
    (call-macro* v form pos #t (lambda () (apply-macro v form (macro-env-provider)))))

  ;;; arity-fix?：宏函数的 ArityException 是否按 (&form &env …) 减 2（卫生宏的转换器只收一个语法对象，不减）
  (define (call-macro* v form pos arity-fix? thunk)
    (with-clr-exception-handler
      (lambda (ex)
        (if (clr-is Cleat.Core.Expand.ExpanderException ex)
            ($throw ex)
            (let* ((fixed (if arity-fix? (macro-arity-fix v ex) ex))
                   (arity? (not (eq? fixed ex)))
                   (ex fixed)
                   (op (clr-static-call clojure.lang.RT (first System.Object) form))
                   (sym (and (not arity?) (clj-symbol? op) (->string op))))
              ($throw (expander-exception pos
                        (cond
                          (arity? 'compile-syntax-check)
                          ((or (clr-is System.ArgumentException ex) (clr-is System.InvalidOperationException ex)
                               (clr-is clojure.lang.IExceptionInfo ex)
                               (clr-call System.Object Equals (clr-call System.Object GetType ex) (clr-type-of System.Exception)))
                           'macro-syntax-check)
                          (else 'macroexpand))
                        (clr-call System.Exception get_Message ex) ex sym)))))
      thunk))

  ;;; —— 卫生宏（cleat-057，clojure.syntax/defsyntax）：Var 的元数据 :hygienic 为真的宏。根值是转换器（IFn）：
  ;;; 收整个调用形式的语法对象（psyntax 已加反标记），返回语法对象（语法对象 / 含语法对象叶子的序对、Clojure 集合；
  ;;; 裸 Clojure 符号按使用处上下文挂上）；psyntax 的 do-macro-call 再给输出加新标记——模板引入的标识符因此不捕获、
  ;;; 也不被用户的同名局部捕获。模式与模板的运行时引擎在 (clojure syntax)。
  (define kw-hygienic (clj-keyword "hygienic"))
  (define (hygienic-var? v)
    (and (var-macro? v) (nil->f (op-get (op-meta v) kw-hygienic)) #t))

  (define op-invoke (clj-op Invoke))

  (define (hygienic-call v stx head)
    (let ((out (call-macro* v (syntax->clj stx) (syntax-position stx) #f
                 (lambda () (op-invoke (op-var-get v) stx)))))
      (mixed->syntax head out)))

  ;;; 列表头部若是特殊形式名的标识符，换成该特殊形式的 gensym 键（与 clj->scheme 对列表头部的处理相同），上下文照旧
  (define (fix-special-head h)
    (if (identifier? h)
        (let* ((csym (syntax->clj h)) (key (and (clj-symbol? csym) (special-head-key csym))))
          (if key (datum->syntax h (make-annotation key #f csym)) h))
        h))

  ;;; 卫生宏的输出 → 语法结构。返回 (values 语法 纯数据?)：纯数据（不含语法对象）的子树一次 datum->syntax 挂 head 的上下文
  ;;; （与 defmacro 垫片相同）；含语法对象的 Clojure 列表变成序对（头部的特殊形式名换键）、集合按子项重建，不加注解
  ;;; （注解的 stripped 必须是纯数据）
  (define (mixed->syntax head x)
    (define (convert x pos)
      (cond
        ((or (stx? x) (annotation? x)) (values x #f))
        ((pair? x)
         (let-values (((a pa) (convert (car x) pos)) ((d pd) (convert (cdr x) pos)))
           (values (cons a d) #f)))
        ((null? x) (values x #f))
        ((clj-symbol? x) (values x #t))
        ((clj-kind x) =>
         (lambda (e)
           (let ((kids ((syntax-extension-children e) x)))
             (if (null? kids)
                 (values x #t)
                 (let* ((own (or (clojure-source-of x) pos))
                        (rs (map (lambda (k) (let-values (((s p) (convert k own))) (cons s p))) kids)))
                   (if (for-all cdr rs)
                       (values x #t)
                       (let ((new (map (lambda (r k p) (if p (clj-datum->syntax* head k) (car r))) rs kids (map cdr rs))))
                         (if (eq? (syntax-extension-kind e) 'clj-list)
                             (values (cons (fix-special-head (car new)) (cdr new)) #f)
                             (values ((syntax-extension-rebuild e) x new) #f)))))))))
        (else (values x #t))))
    (let-values (((s pure?) (convert x #f)))
      (if pure? (clj-datum->syntax* head s) s)))

  ;;; —— defmacro 垫片的标识符跟踪（cleat-057）——
  ;;; 宏调用形式里 wrap 与头部不同的标识符（卫生宏模板传进来的用户子形式、模板引入的标识符、%ctx 等拼装的形式）按
  ;;; Clojure 符号对象（身份：reader 给每个出现各一个对象，strip 得回原对象）记下其语法对象；宏输出里同一个符号对象
  ;;; 原样放回（保住它自己的标记与 rib），其余符号挂使用处（头部）上下文——Clojure 的不卫生语义不变。
  ;;; 全部标识符都与头部同 wrap（普通的 Clojure 代码）时返回 #f：走原来的快速路径（整个输出一次 datum->syntax）。
  ;;; 同一 wrap：标记与 subst 逐项 eq?（add-mark 给每个叶子各 cons 一份表，同一语法对象放在模板两处后表不再 eq?）
  (define (same-list? a b)
    (or (eq? a b)
        (and (pair? a) (pair? b) (eq? (car a) (car b)) (same-list? (cdr a) (cdr b)))
        (and (null? a) (null? b))))
  (define (same-wrap? a b) (and (same-list? (stx-mark* a) (stx-mark* b)) (same-list? (stx-subst* a) (stx-subst* b))))

  ;;; 按引用相等的表：Clojure 符号的 Equals 按名字，make-eq-hashtable 分不开同名的两个对象
  (define op-identity-table (clj-op IdentityTable))
  (define (make-identity-table) (op-identity-table #f))

  (define (track-identifiers stx head)
    (let ((table (make-identity-table)) (mixed? #f))
      (let walk ((x stx))
        (syntax-case x ()
          ((a . d) (begin (walk #'a) (walk #'d)))
          (() #f)
          (_ (cond
               ((identifier? x)
                (let ((key (syntax->clj x)) (same? (same-wrap? x head)))
                  (unless same? (set! mixed? #t))
                  (cond
                    ((not (hashtable-contains? table key)) (hashtable-set! table key (if same? 'head x)))
                    ((let ((old (hashtable-ref table key #f)))
                       (and old (if (eq? old 'head) same? (and (not same?) (same-wrap? old x)))))
                     #f)
                    (else (hashtable-set! table key #f)))))   ; 同一符号对象带不同 wrap：不跟踪，挂使用处上下文
               ((syntax-ext-kind x) => (lambda (k) (for-each walk (syntax-ext->list x k))))
               (else #f)))))
      (and mixed? table)))

  ;;; 宏输出（Clojure 数据）→ 语法结构：跟踪到的符号对象放回其语法对象，其余同 clj->scheme 并挂 head 的上下文；
  ;;; 列表与集合保留注解（位置、stripped 为输出数据本身，与 strip 的结果一致）
  (define (rewrap-output head out table)
    (define (tracked x)
      (let ((v (hashtable-ref table x #f)))
        (and v (not (eq? v 'head)) v)))
    (let convert ((x out) (pos #f))
      (cond
        ((clj-symbol? x)
         (or (tracked x) (datum->syntax head (make-annotation (clj-symbol->scheme x) pos x))))
        ((clj-kind x) =>
         (lambda (e)
           (let ((kids ((syntax-extension-children e) x)))
             (if (null? kids)
                 (datum->syntax head x)
                 (let* ((own (or (clojure-source-of x) pos))
                        (new (map (lambda (k) (convert k own)) kids)))
                   (if (eq? (syntax-extension-kind e) 'clj-list)
                       (let ((key (special-head-key (car kids))))
                         (make-annotation
                           (if key
                               (cons (datum->syntax (or (tracked (car kids)) head) (make-annotation key own (car kids))) (cdr new))
                               new)
                           own x))
                       (make-annotation ((syntax-extension-rebuild e) x new) own x)))))))
        (else (datum->syntax head x)))))

  (define (shim-call v stx head)
    (let ((table (track-identifiers stx head))
          (form (syntax->clj stx)))
      (let ((out (call-macro v form (syntax-position stx))))
        (if table
            (rewrap-output head out table)
            (clj-datum->syntax* head out)))))

  (define (macro-transformer v)
    (lambda (stx)
      (syntax-case stx ()
        ((head . _)
         (if (hygienic-var? v)
             (hygienic-call v stx #'head)
             (shim-call v stx #'head))))))

  ;;; —— 当前命名空间与运行时的命名空间操作（Clojure 的 in-ns / refer / alias / import 的最小形式）——

  (define (in-ns! name)
    (let ((ns (clr-static-call clojure.lang.Namespace findOrCreate (->csym name))))
      (clr-call clojure.lang.Var set current-ns-var ns)
      ns))

  ;;; 在 *ns* 的线程绑定下运行 thunk（in-ns! 要求 *ns* 有线程绑定，与 Clojure 的 REPL / load 相同）
  (define (with-ns-binding ns thunk)
    (clr-static-call clojure.lang.Var pushThreadBindings
      (clj-map current-ns-var ns))
    ($try/finally (thunk)
      (clr-static-call clojure.lang.Var popThreadBindings)))

  (define (the-ns x)
    (if (clr-is clojure.lang.Namespace x)
        x
        (or (find-ns x) ($throw (clr-new System.Exception (string-append "No namespace: " (->string x) " found"))))))

  ;;; (refer! 'a 'x 'y)：把 a 的公有 Var x、y 映射进 *ns*
  (define (refer! from . names)
    (let ((src (the-ns from)) (ns (current-ns)))
      (for-each
        (lambda (n)
          (let* ((s (->csym n))
                 (v (nil->f (clr-call clojure.lang.Namespace FindInternedVar src s))))
            (cond
              ((not v) ($throw (clr-new System.InvalidOperationException (string-append (->string s) " does not exist"))))
              ((not (var-public? v)) ($throw (clr-new System.InvalidOperationException (string-append (->string s) " is not public"))))
              (else (clr-call clojure.lang.Namespace refer ns s v)))))
        names)))

  (define (alias! alias target)
    (clr-call clojure.lang.Namespace addAlias (current-ns) (->csym alias) (the-ns target)))

  (define (import! type-name)
    (let ((t (if (type? type-name) type-name (class-for-name (if (symbol? type-name) (symbol->string type-name) type-name)))))
      (unless t ($throw (clr-new System.TypeLoadException (string-append "Unable to resolve classname: " (->string type-name)))))
      (clr-call clojure.lang.Namespace (importClass System.Type) (current-ns) t)))

  ;;; —— 三、错误 ——

  (define (source-path)
    (->string (clr-call clojure.lang.Var deref (clr-static-field-get clojure.lang.RT SourcePathVar))))

  ;;; 语法对象（或宏输出里的裸注解，cleat-057）的注解，或 #f
  (define (form-annotation x)
    (let ((e (if (stx? x) (stx-expr x) x)))
      (and (annotation? e) e)))

  ;;; 语法对象的源位置 → (行 列 文件或#f)，或 #f
  (define (syntax-position x)
    (let ((src (let ((a (form-annotation x))) (and a (annotation-source a)))))
      (and (pair? src) (string? (cdr src))
           (let* ((s (cdr src))
                  (comma (string-index s #\,))
                  (close (string-index s #\))))
             (and comma close
                  (list (string->number (substring s 1 comma))
                        (string->number (substring s (+ comma 1) close))
                        (and (> (string-length (car src)) 0) (car src))))))))

  (define (phase-keyword phase)
    (case phase
      ((macro-syntax-check) (clr-static-field-get Cleat.Core.Expand.ExpanderException PhaseSyntax))
      ((macroexpand) (clr-static-field-get Cleat.Core.Expand.ExpanderException PhaseMacroexpand))
      (else (clr-static-field-get Cleat.Core.Expand.ExpanderException PhaseCompile))))

  ;;; sym：出错形式的运算符名（字符串，Clojure 的 CompilerException.symbol），没有时省略
  (define expander-exception
    (case-lambda
      ((pos phase msg cause) (expander-exception pos phase msg cause #f))
      ((pos phase msg cause sym)
       (clr-new Cleat.Core.Expand.ExpanderException
         (or (and pos (caddr pos)) (source-path))
         (if pos (car pos) 0) (if pos (cadr pos) 0)
         (phase-keyword phase) msg cause (or sym #nil)))))

  ;;; 出错形式的特殊形式名（Clojure 只对特殊形式报 "compiling recur at"，普通调用为 "compiling at"），否则 #f
  (define (special-form-op form)
    (and (form-annotation form)
         (let ((d (syntax->clj form)))
           (and (clr-is clojure.lang.ISeq d)
                (let ((h (clr-static-call clojure.lang.RT (first System.Object) d)))
                  (and (special-head-key h) (sym-name h)))))))

  ;;; psyntax 自己的报错文本（Scheme 的措辞）不给 Clojure 用户看：换成 Clojure 的说法（cleat-055 诊断边界的后备；
  ;;; 已知的来源——重复的参数、deftype* 的形状——在 (clojure syntax) 里先处理掉了）
  (define (clojure-wording msg form op)
    (cond
      ((equal? msg "invalid syntax") (if op (string-append "Malformed " op) "Malformed form"))
      ((equal? msg "duplicate binding") "Duplicate binding name")
      ((member msg '("expected a list" "invalid expression" "not an identifier" "invalid context for definition"
                     "incorrect usage of auxiliary keyword" "invalid syntax in quasiquote"))
       (if op (string-append "Malformed " op) (string-append "Malformed form: " (->string (syntax->clj form)))))
      (else msg)))

  ;;; syntax-error-hook：psyntax 的语法错误改抛 ExpanderException。"unbound identifier" 由 resolve-in 给出原因
  (define (clojure-error-hook who msg form c)
    (let* ((op (special-form-op form))
           (text (if (and (equal? msg "unbound identifier") (stx? form))
                     (let ((d (syntax->clj form)))
                       (if (clj-symbol? d)
                           (let-values (((o why p?) (resolve-in (current-ns) d))) (or why msg))
                           (string-append "Unable to resolve symbol: " (->string d) " in this context")))
                     (clojure-wording msg form op))))
      ($throw (expander-exception (syntax-position form) 'compile-syntax-check text #nil op))))

  ;;; —— 入口：在 *ns* 里展开 / 求值 Clojure 形式 ——

  ;;; 正在展开 Clojure 形式（(clojure syntax) 的数据挂钩只在此时处理 Scheme 的 '() 等数据）
  (define clojure-expanding? (make-parameter #f))

  ;;; e 是否来自 Clojure 数据（clj->scheme 产生的列表注解，stripped 为 Clojure 的 seq；宏输出里可以是裸注解，cleat-057）
  (define (clojure-form? e)
    (let ((a (form-annotation e)))
      (and a (clr-is clojure.lang.ISeq (annotation-stripped a)))))

  (define (in-clojure thunk)
    (parameterize ((syntax-error-hook clojure-error-hook) (clojure-expanding? #t))
      (thunk)))

  (define (eval-in-ns form)
    (in-clojure (lambda () (eval (clj->scheme form) (namespace-environment (current-ns))))))

  ;;; 展开为 Lcore（不求值）
  (define (expand-in-ns form)
    (in-clojure (lambda () (expand->core (clj->scheme form) (namespace-environment (current-ns))))))

  ;;; 以宏 Var 对象（不是符号）开头的形式 (#<Var m> …)：只能由宏构造（cleat-052；C# 展开器 IsMacro 的 op is Var 分支）。
  ;;; 是则返回该 Var（别的命名空间的私有宏报 C# 的文本 "Var: #'a/m is not public"），否则 #f。
  ;;; fail 为报错过程 (fail 消息)。
  (define (macro-var-head form fail)
    (and (clr-is clojure.lang.ISeq form)
         (let ((op (clr-static-call clojure.lang.RT (first System.Object) form)))
           (and (clr-is clojure.lang.Var op)
                (var-macro? op)
                (begin
                  (when (and (not (eq? (var-ns op) (current-ns))) (not (var-public? op)))
                    (fail (string-append "Var: " (->string op) " is not public")))
                  op)))))

  ;;; Clojure 的 macroexpand-1：返回 Clojure 数据；不是宏调用（含特殊形式）则返回 form 本身
  (define (macroexpand-1-in-ns form)
    (in-clojure
      (lambda ()
        (cond
          ((macro-var-head form
             (lambda (msg) ($throw (expander-exception #f 'compile-syntax-check msg #nil)))) 
           => (lambda (v) (call-macro v form #f)))
          (else
           (let* ((x (clj->scheme form))
                  (y (environment-macroexpand-1 x (namespace-environment (current-ns)))))
             (if (eq? y x) form (scheme->clj y))))))))

  ;;; 以卫生宏开头的形式（cleat-057）：顶层的数据级预展开到此为止（展开结果是数据，标记会丢；交给语法对象路径）
  (define (hygienic-head? form)
    (and (clr-is clojure.lang.ISeq form)
         (let ((op (clr-static-call clojure.lang.RT (first System.Object) form)))
           (cond
             ((var? op) (hygienic-var? op))
             ((and (clj-symbol? op) (not (special-head-key op)))
              (let-values (((o msg p?) (resolve-in (current-ns) op)))
                (and (var? o) (hygienic-var? o))))
             (else #f)))))

  ;;; 驱动的顶层预展开（Compiler.eval 的 macroexpand）：重复 macroexpand-1 直到不变或遇到卫生宏。
  ;;; Clojure 的 macroexpand 函数自己循环调 macroexpand-1 钩子，不经这里
  (define (macroexpand-in-ns form)
    (if (hygienic-head? form)
        form
        (let ((x (macroexpand-1-in-ns form)))
          (if (eq? x form) form (macroexpand-in-ns x)))))

  (register-kinds!))
