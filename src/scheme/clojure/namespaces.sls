;;; Clojure 的命名空间模型（cleat-049，designs/14 §6、designs/13 §3）。按需加载，不进 Scheme boot 镜像。
;;;
;;; 一、Clojure 数据 ↔ psyntax 数据（clj->scheme / scheme->clj / syntax->clj）
;;;   wrap 前把 reader 给出的 Clojure 数据转成 psyntax 认识的形状：
;;;   - clojure.lang.Symbol → 驻留的 Scheme 符号，名字为 Symbol.ToString()（`name` 或 `ns/name`），
;;;     包成注解 (make-annotation Scheme符号 源位置 原Clojure符号)：标识符认 expression（boot 的 id?），
;;;     syntax->datum 得回 stripped（原符号，带元数据）。符号自己没有位置，用外层列表的位置（与 C# 展开器报错的位置相同）。
;;;   - 非空 Clojure 列表 / seq → Scheme 列表（psyntax 的应用、宏、syntax-case 都按序对工作），注解的 stripped 为原对象；
;;;     空列表 () 保持原对象（它与 nil 不同；Scheme 的 '() 就是 CLR null，即 Clojure 的 nil）。
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
;;;     clj-var      值为 Var：引用 → 读根（getRawRoot；动态 Var 为 get），set! → Var.set
;;;     clj-macro    值为转换器（defmacro 垫片：Clojure 数据进、Clojure 数据出、使用处上下文）；macroexpand-1 展开它
;;;     clj-type     值为 Type：引用 → 常量
;;;     clj-special  值为转换器；按未限定名字登记（register-clj-special!），先于命名空间映射；macroexpand-1 不展开它
;;;
;;; 三、错误：clojure-eval 等入口在 syntax-error-hook 下运行，语法错误改抛 Cleat.Core.Expand.ExpanderException
;;;   （"Syntax error (file:line:col) message"）；"unbound identifier" 的文本由 resolve-in 给出具体原因。

(library (clojure namespaces)
  (export clj->scheme scheme->clj syntax->clj clj-datum->syntax*
          clj-symbol clj-symbol? scheme-symbol->clj
          current-ns find-ns ns-name* in-ns! with-ns-binding refer! alias! import!
          resolve-in clj-ns-resolve clj-resolve
          namespace-environment clojure-eval clojure-expand clojure-macroexpand-1
          symbol-label register-clj-special! var-label
          clojure-error-hook expander-exception)
  (import
    (rnrs)
    (rnrs eval)
    (only (ironscheme) gensym set-symbol-value! expand->core with-clr-exception-handler parameterize)
    (only (ironscheme core) stx? stx-expr)
    (only (ironscheme reader) make-annotation annotation? annotation-source)
    (only (ironscheme unsafe) $throw)
    (ironscheme syntax-ext)
    (ironscheme clr)
    (clojure syntax-kinds))

  ;;; —— 互操作小工具 ——

  (define (clj-symbol name) (clr-static-call clojure.lang.Symbol (intern System.String) name))
  (define (clj-symbol? x) (clr-is clojure.lang.Symbol x))
  (define (sym-ns s) (let ((n (clr-call clojure.lang.Symbol get_Namespace s))) (if (null? n) #f n)))
  (define (sym-name s) (clr-call clojure.lang.Symbol get_Name s))
  (define (->string x) (clr-call System.Object ToString x))
  (define (->csym x) (cond ((clj-symbol? x) x) ((symbol? x) (clj-symbol (symbol->string x))) (else (clj-symbol x))))
  (define (var? x) (clr-is clojure.lang.Var x))
  (define (type? x) (clr-is System.Type x))
  (define (var-ns v) (clr-call clojure.lang.Var get_Namespace v))
  (define (var-public? v) (clr-call clojure.lang.Var get_isPublic v))
  (define (var-macro? v) (clr-call clojure.lang.Var get_IsMacro v))
  (define (var-dynamic? v) (clr-call clojure.lang.Var isDynamic v))
  (define (meta-of x) (if (clr-is clojure.lang.IMeta x) (clr-call clojure.lang.IMeta meta x) '()))
  (define (nil->f x) (if (null? x) #f x))
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
                   (make-annotation
                     (if (eq? (syntax-extension-kind e) 'clj-list) new ((syntax-extension-rebuild e) x new))
                     own x))))))
        (else x))))

  ;;; strip 之后的数据 → Clojure 数据：Scheme 符号 → Clojure 符号，序对 → PersistentList，Clojure 集合按子项重建
  (define (scheme->clj x)
    (cond
      ((symbol? x) (scheme-symbol->clj x))
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

  ;;; Clojure 的 ns-resolve / resolve：解析不到为 nil（'()），私有 Var 抛 IllegalStateException
  (define (clj-ns-resolve ns sym)
    (let-values (((o msg private?) (resolve-in ns (->csym sym))))
      (cond
        (o o)
        (private? ($throw (clr-new System.InvalidOperationException msg)))
        (else '()))))
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

  ;;; 特殊形式：未限定名字（Scheme 符号）→ 标签
  (define specials (make-eq-hashtable))
  (define (register-clj-special! name transformer)
    (hashtable-set! specials (if (symbol? name) name (string->symbol (->string name)))
      (make-label (cons 'clj-special transformer))))

  ;;; 查找 rib 的过程：特殊形式 → 命名空间（标记不参与：Clojure 按名字解析）
  (define (ns-lookup ns)
    (lambda (sym mark*)
      (or (hashtable-ref specials sym #f)
          (let-values (((o msg private?) (resolve-in ns (scheme-symbol->clj sym))))
            (cond
              ((not o) #f)
              ((var? o) (var-label o))
              ((type? o) (type-label o))
              (else #f))))))

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

  (define (var-reference v)
    (with-syntax ((var (const v)))
      (if (var-dynamic? v)
          #'(clr-call clojure.lang.Var get (quote var))
          #'(clr-call clojure.lang.Var getRawRoot (quote var)))))

  ;;; 当前展开中的标识符 e（语法对象）对应的 Clojure 符号
  (define (form-symbol e) (syntax->clj e))

  (define (unresolvable e)
    (syntax-violation #f (string-append "Unable to resolve symbol: " (->string (form-symbol e)) " in this context") e))

  (define (register-kinds!)
    (register-binding-extension!
      (make-binding-extension 'clj-var
        (lambda (e v expand) (expand (var-reference v)))
        (lambda (e v expand)
          (syntax-case e ()
            ((_ id val)
             (with-syntax ((var (const v)))
               (expand #'(clr-call clojure.lang.Var set (quote var) val))))))
        #f #f))
    (register-binding-extension!
      (make-binding-extension 'clj-macro
        (lambda (e t expand)
          (let-values (((o msg p?) (resolve-in (current-ns) (form-symbol e))))
            (syntax-violation #f (string-append "Can't take the value of a macro: " (->string o)) e)))
        #f #t #t))
    (register-binding-extension!
      (make-binding-extension 'clj-type
        (lambda (e t expand) (expand (with-syntax ((type (const t))) #'(quote type))))
        #f #f #f))
    (register-binding-extension!
      (make-binding-extension 'clj-special
        (lambda (e t expand) (unresolvable e))
        #f #t #f)))

  ;;; —— defmacro 垫片（最小：&env 为 nil；完整的在 cleat-050）——

  (define (apply-macro v form)
    (let ((args (clr-static-call clojure.lang.RT (cons System.Object System.Object) form
                  (clr-static-call clojure.lang.RT (cons System.Object System.Object) '()
                    (clr-static-call clojure.lang.RT (next System.Object) form)))))
      (clr-call clojure.lang.IFn applyTo v args)))

  (define (macro-transformer v)
    (lambda (stx)
      (syntax-case stx ()
        ((head . _)
         (let ((form (syntax->clj stx)))
           (clj-datum->syntax* #'head
             (with-clr-exception-handler
               (lambda (ex)
                 (if (clr-is Cleat.Core.Expand.ExpanderException ex)
                     ($throw ex)
                     ($throw (expander-exception (syntax-position stx)
                               (if (or (clr-is System.ArgumentException ex) (clr-is System.InvalidOperationException ex)
                                       (clr-is clojure.lang.IExceptionInfo ex))
                                   'macro-syntax-check 'macroexpand)
                               (clr-call System.Exception get_Message ex) ex))))
               (lambda () (apply-macro v form)))))))))

  ;;; —— def（特殊形式；按 C# Expander.ParseDef / LookupVar(sym, true)）——

  (define kw-line (clj-keyword "line"))
  (define kw-column (clj-keyword "column"))
  (define kw-file (clj-keyword "file"))
  (define kw-doc (clj-keyword "doc"))
  (define kw-dynamic (clj-keyword "dynamic"))

  (define (assoc* m k v) (clr-static-call clojure.lang.RT (assoc System.Object System.Object System.Object) m k v))
  (define (meta-get m k) (nil->f (clr-static-call clojure.lang.RT (get System.Object System.Object) m k)))

  (define (def-var e ns sym)
    (define (err msg) (syntax-violation #f msg e))
    (let ((nsname (sym-ns sym)) (name (clj-symbol (sym-name sym))))
      (if nsname
          (let ((target (namespace-for ns nsname)))
            (cond
              ((not target) (err "Can't refer to qualified var that doesn't exist"))
              ((eq? target ns) (clr-call clojure.lang.Namespace intern ns name))
              ((nil->f (clr-call clojure.lang.Namespace FindInternedVar target name))
               (err "Can't create defs outside of current namespace"))
              (else (err "Can't refer to qualified var that doesn't exist"))))
          (let ((o (nil->f (clr-call clojure.lang.Namespace GetMapping ns name))))
            (cond
              ((not o) (clr-call clojure.lang.Namespace intern ns name))
              ((and (var? o) (eq? (var-ns o) ns)) o)
              ((var? o) (clr-call clojure.lang.Namespace intern ns name))   ; 替换 refer 来的 Var（Namespace 打印警告）
              (else (err (string-append "Expecting var, but " (->string sym) " is mapped to " (->string o)))))))))

  (define (def-transformer stx)
    (define (build name doc init)
      (let ((sym (syntax->clj name)))
        (unless (clj-symbol? sym)
          (syntax-violation #f "First argument to def must be a Symbol" stx))
        (let* ((ns (current-ns))
               (v (def-var stx ns sym))
               (m (let* ((m (meta-of sym))
                         (pos (syntax-position stx))
                         (m (if pos (assoc* (assoc* m kw-line (car pos)) kw-column (cadr pos)) m))
                         (m (assoc* m kw-file (or (and pos (caddr pos)) (source-path))))
                         (m (if doc (assoc* m kw-doc doc) m)))
                    m)))
          (when (meta-get m kw-dynamic) (clr-call clojure.lang.Var setDynamic v))
          (with-syntax ((var (const v)) (meta (const m)))
            (if init
                (with-syntax ((init init))
                  #'(begin
                      (clr-call clojure.lang.Var setMeta (quote var) (quote meta))
                      (clr-call clojure.lang.Var bindRoot (quote var) init)
                      (quote var)))
                #'(begin
                    (clr-call clojure.lang.Var setMeta (quote var) (quote meta))
                    (quote var)))))))
    (syntax-case stx ()
      ((_ name) (build #'name #f #f))
      ((_ name init) (build #'name #f #'init))
      ((_ name doc init) (string? (syntax->datum #'doc)) (build #'name (syntax->datum #'doc) #'init))
      ((_ name x y z . rest) (syntax-violation #f "Too many arguments to def" stx))
      ((_ name x y) (syntax-violation #f "Too many arguments to def" stx))
      (_ (syntax-violation #f "Too few arguments to def" stx))))

  ;;; —— 当前命名空间与运行时的命名空间操作（Clojure 的 in-ns / refer / alias / import 的最小形式）——

  (define (in-ns! name)
    (let ((ns (clr-static-call clojure.lang.Namespace findOrCreate (->csym name))))
      (clr-call clojure.lang.Var set current-ns-var ns)
      ns))

  ;;; 在 *ns* 的线程绑定下运行 thunk（in-ns! 要求 *ns* 有线程绑定，与 Clojure 的 REPL / load 相同）
  (define (with-ns-binding ns thunk)
    (clr-static-call clojure.lang.Var pushThreadBindings
      (clj-map current-ns-var ns))
    (dynamic-wind
      (lambda () #f)
      thunk
      (lambda () (clr-static-call clojure.lang.Var popThreadBindings))))

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

  ;;; 语法对象的源位置 → (行 列 文件或#f)，或 #f
  (define (syntax-position x)
    (let ((src (and (stx? x)
                    (let ((e (stx-expr x)))
                      (and (annotation? e) (annotation-source e))))))
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

  (define (expander-exception pos phase msg cause)
    (clr-new Cleat.Core.Expand.ExpanderException
      (or (and pos (caddr pos)) (source-path))
      (if pos (car pos) 0) (if pos (cadr pos) 0)
      (phase-keyword phase) msg cause))

  ;;; syntax-error-hook：psyntax 的语法错误改抛 ExpanderException。"unbound identifier" 由 resolve-in 给出原因
  (define (clojure-error-hook who msg form c)
    (let ((text (if (and (equal? msg "unbound identifier") (stx? form))
                    (let ((d (syntax->clj form)))
                      (if (clj-symbol? d)
                          (let-values (((o why p?) (resolve-in (current-ns) d))) (or why msg))
                          msg))
                    msg)))
      ($throw (expander-exception (syntax-position form) 'compile-syntax-check text '()))))

  ;;; —— 入口：在 *ns* 里展开 / 求值 Clojure 形式 ——

  (define (in-clojure thunk)
    (parameterize ((syntax-error-hook clojure-error-hook))
      (thunk)))

  (define (clojure-eval form)
    (in-clojure (lambda () (eval (clj->scheme form) (namespace-environment (current-ns))))))

  ;;; 展开为 Lcore（不求值）
  (define (clojure-expand form)
    (in-clojure (lambda () (expand->core (clj->scheme form) (namespace-environment (current-ns))))))

  ;;; Clojure 的 macroexpand-1：返回 Clojure 数据；不是宏调用（含特殊形式）则返回 form 本身
  (define (clojure-macroexpand-1 form)
    (in-clojure
      (lambda ()
        (let* ((x (clj->scheme form))
               (y (environment-macroexpand-1 x (namespace-environment (current-ns)))))
          (if (eq? y x) form (scheme->clj y))))))

  (register-kinds!)
  (register-clj-special! 'def def-transformer))
