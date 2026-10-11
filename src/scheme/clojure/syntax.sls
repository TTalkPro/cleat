;;; Clojure 的特殊形式与表达式语法（cleat-050，designs/13 §3、03 §1 ~ §4）。按需加载，不进 Scheme boot 镜像。
;;;
;;; 规格是 C# 展开器（src/Cleat.Core/Expander/Expander.cs 与 ExpanderTests）：规则与错误文本逐条照搬；产物不同——
;;; 这里展开为 Scheme 核心形式 + 运行时过程（Cleat.Scheme.Clojure.ClojureOps），今天就能编译运行：
;;;
;;;   （cleat-051 起：运行时操作全部是编译器的 Clojure 节点 $clj-…，不再是 Scheme 过程常量，见各形式与 designs/04 §6）
;;;   def        Var 在展开期 intern（Namespace.intern 的语义），运行时 $clj-def（setDynamic / bindRoot / setMeta），值为 Var
;;;   if         (if 测试 then else)：Clojure 的 nil（CLR null = Scheme 的 #nil）与 false 在 Scheme 里本来就为假（cleat-058）
;;;   do         begin；let* / loop* → let* / 命名 let（recur = 调用循环标签，只在尾位置，IronScheme 降为循环）
;;;   fn*        $clj-fn：编译为 AFunction / RestFn 的子类（固定子句 invoke(k)，变长子句 doInvoke）；子句体是命名 let，recur 回到子句开头
;;;   letfn*     letrec；quote → 常量；var → Var 常量；throw → $throw；try → $try/catch + $try/finally
;;;   monitor-*  Monitor.Enter / Exit；import* → 运行时导入 *ns*；new / . / set! → Reflector（Clojure 的转换规则）；
;;;              都是 $clj-call（ClojureOps 的静态方法）
;;;   case*      $clj-case：编译器按 CaseExpr 的规则算键、IL switch 分派
;;;   deftype* reify*   $clj-deftype：编译为真实 CLR 类型（字段、接口方法、getBasis；reify 捕获局部、实现 IObj）
;;;   调用一般经 $clj-invoke（IFn.invoke(k)）；接口文件里的已知函数（cleat-033）在参数个数恰为固定 arity 时经
;;;   $clj-static-call 直接 call 类::invokeStatic（namespaces.sls 的 default-var-call 决定）；(:k x) 为 $clj-keyword-invoke；
;;;   字面量：关键字、nil、正则等为常量，
;;;   集合元素全为常量时整个是常量，否则运行时构造（RT.vector / map / set，元数据经 withMeta）。
;;; 模板（转换器式的特殊形式）里的节点经隐藏形式 %node：(%node 头 (数据 …) 子形式 …) → (头 '数据 … 子形式的展开 …)。
;;;
;;; 上下文（尾位置、recur 目标、词法局部、fn 的类名、def 给 fn 的名字）放在参数 current-ctx 里：展开子形式时参数化。
;;; 引入绑定的形式是转换器（经 do-macro-call，卫生标记使模板里的 let / case-lambda 不被用户的同名局部捕获），子形式包在
;;; %ctx 里；recur、quote、var 等不引入绑定的是过程式形式（直接返回核心形式）。recur 的目标标识符以语法形式放在 %ctx 里，
;;; 才能带上外层命名 let 的替换。

(library (clojure syntax)
  (export clojure-eval clojure-expand clojure-macroexpand-1 clojure-macroexpand
          current-ctx clojure-locals-env special-form-names inline-calls?)
  (import
    (rnrs)
    (only (cleat scheme) parameterize make-parameter with-clr-exception-handler format gensym)
    (only (cleat scheme unsafe) $throw $try/catch $try/finally)
    (cleat scheme syntax-ext)
    (cleat scheme clr)
    (clojure syntax-kinds)
    (clojure namespaces))

  ;;; —— 运行时过程（实参与返回值不经 Scheme 互操作的转换）——

  ;;; 展开期用的（Clojure 值的遍历与宏的调用）；运行时操作是编译器的节点，不是这些过程
  (define op-invoke (clj-op Invoke))
  (define op-apply-to (clj-op ApplyTo))
  (define op-meta (clj-op Meta))
  (define op-get (clj-op Get))
  (define op-long (clj-op ToLong))
  (define op-next-id (clj-op NextId))

  ;;; ClojureOps 的静态方法调用节点（核心形式）：(clj-call "方法" 固定参数个数|#f 子形式核心 …)
  (define (clj-call method fixed . args) (cons* '$clj-call (list 'quote method) (list 'quote fixed) args))

  ;;; —— Clojure 数据的小工具 ——

  (define (->string x) (clr-call System.Object ToString x))
  (define (nil->f x) (if (nil? x) #f x))
  (define (sym-ns s) (nil->f (clr-call clojure.lang.Symbol get_Namespace s)))
  (define (sym-name s) (clr-call clojure.lang.Symbol get_Name s))
  (define (var? x) (clr-is clojure.lang.Var x))
  (define (keyword? x) (clr-is clojure.lang.Keyword x))
  (define (clj-list? x) (and (not (nil? x)) (or (clr-is clojure.lang.IPersistentList x) (clr-is clojure.lang.ISeq x))))
  (define (meta-of x) (op-meta x))
  (define (meta-get m k) (nil->f (op-get m k)))
  (define (assoc* m k v) (clr-static-call clojure.lang.RT (assoc System.Object System.Object System.Object) m k v))
  (define (dissoc* m k) (if (nil? m) m (clr-call clojure.lang.IPersistentMap without m k)))
  (define (count* x) (clr-static-call clojure.lang.RT (count System.Object) x))
  (define (with-meta* x m) (clr-call clojure.lang.IObj withMeta x m))
  (define (print-str x) (clr-static-call clojure.lang.RT (printString System.Object) x))
  (define (sym-is? x name) (and (clj-symbol? x) (not (sym-ns x)) (string=? (sym-name x) name)))
  (define (munge s) (clr-static-call Cleat.Scheme.Clojure.HostSyntax Munge s))
  (define (string-prefix? p s) (and (>= (string-length s) (string-length p)) (string=? (substring s 0 (string-length p)) p)))
  (define (string-suffix? p s)
    (let ((n (string-length s)) (k (string-length p)))
      (and (>= n k) (string=? (substring s (- n k) n) p))))
  (define (string-last-index s ch)
    (let loop ((i (- (string-length s) 1)))
      (cond ((< i 0) #f) ((char=? (string-ref s i) ch) i) (else (loop (- i 1))))))
  (define (last ls) (if (null? (cdr ls)) (car ls) (last (cdr ls))))
  (define (all-but-last ls) (if (null? (cdr ls)) '() (cons (car ls) (all-but-last (cdr ls)))))

  (define kw-tag (clj-keyword "tag"))
  (define kw-line (clj-keyword "line"))
  (define kw-rettag (clj-keyword "rettag"))
  (define kw-column (clj-keyword "column"))
  (define kw-file (clj-keyword "file"))
  (define kw-doc (clj-keyword "doc"))
  (define kw-dynamic (clj-keyword "dynamic"))
  (define kw-arglists (clj-keyword "arglists"))
  (define kw-inline (clj-keyword "inline"))
  (define kw-inline-arities (clj-keyword "inline-arities"))
  (define kw-implements (clj-keyword "implements"))
  (define kw-cleat-interface (clj-keyword "cleat/interface"))
  (define kw-volatile-mutable (clj-keyword "volatile-mutable"))
  (define kw-unsync-mutable (clj-keyword "unsynchronized-mutable"))
  (define kw-reify (clj-keyword "reify"))
  (define kw-source-span (clj-keyword "source-span"))
  (define kw-end-line (clj-keyword "end-line"))
  (define kw-end-column (clj-keyword "end-column"))

  ;;; 用户元数据：去掉 reader 的源位置
  (define (user-meta x)
    (let ((m (meta-of x)))
      (if (nil? m)
          m
          (let ((m (fold-left dissoc* m (list kw-line kw-column kw-file kw-source-span kw-end-line kw-end-column))))
            (if (= (count* m) 0) #nil m)))))

  ;;; —— 宿主类型（规则在 C# 的 HostSyntax）——

  (define (tag-of x) (nil->f (clr-static-call Cleat.Scheme.Clojure.HostSyntax TagOf x)))
  (define (tag-type tag) (and tag (nil->f (clr-static-call Cleat.Scheme.Clojure.HostSyntax TagType tag))))
  (define (type-name t) (clr-static-call Cleat.Scheme.Clojure.HostSyntax TypeName t))
  (define (maybe-type form)
    (nil->f (clr-static-call Cleat.Scheme.Clojure.HostSyntax MaybeType form
              (and (clj-symbol? form) (not (sym-ns form)) (find-local form) #t))))
  (define (maybe-array-type form) (nil->f (clr-static-call Cleat.Scheme.Clojure.HostSyntax MaybeArrayType form)))
  (define (pos-digit? s) (clr-static-call Cleat.Scheme.Clojure.HostSyntax IsPosDigit s))
  (define (class-for-name name)
    (with-clr-exception-handler (lambda (e) #f)
      (lambda () (nil->f (clr-static-call clojure.lang.RT (classForName System.String) name)))))

  ;;; —— 上下文 ——
  ;;; loop：#f 或 (循环标签标识符 . 参数个数)；locals：local 记录（新的在前）；fn-name：外层 fn 的类名（ns$f）；hint：def / letfn 给 fn* 的名字
  (define-record-type ctx (fields tail? loop locals fn-name hint))
  (define-record-type local (fields sym tag mutable? kind))   ; sym：Clojure 符号；kind：local / arg / field / this / catch
  (define current-ctx (make-parameter (make-ctx #f #f '() #f #f)))
  (define (cur) (current-ctx))

  (define (ctx-nontail c) (make-ctx #f #f (ctx-locals c) (ctx-fn-name c) #f))
  (define (ctx-clear c) (if (ctx-hint c) (make-ctx (ctx-tail? c) (ctx-loop c) (ctx-locals c) (ctx-fn-name c) #f) c))
  (define (ctx-no-recur c tail?) (make-ctx tail? #f (ctx-locals c) (ctx-fn-name c) #f))
  (define (ctx-with-locals c locals) (make-ctx (ctx-tail? c) (ctx-loop c) (append (reverse locals) (ctx-locals c)) (ctx-fn-name c) #f))
  (define (ctx-hinted c name) (make-ctx #f #f (ctx-locals c) (ctx-fn-name c) name))
  (define (ctx-with-loop c loop) (make-ctx (ctx-tail? c) loop (ctx-locals c) (ctx-fn-name c) (ctx-hint c)))

  (define (find-local csym)
    (let ((name (->string csym)))
      (find (lambda (l) (string=? (->string (local-sym l)) name)) (ctx-locals (cur)))))

  (define (make-local* csym kind . mutable)
    (make-local csym (tag-of csym) (and (pair? mutable) (car mutable)) kind))

  ;;; &env：词法局部的 符号 → LocalBinding map（Clojure 的 LOCAL_ENV；没有局部时为 nil）
  (define (clojure-locals-env)
    (let ((ls (reverse (ctx-locals (cur)))))
      (if (null? ls)
          #nil
          (fold-left
            (lambda (m l)
              (let ((s (local-sym l)) (k (local-kind l)))
                (assoc* m s (clr-new Cleat.Core.Expand.LocalBinding s s (or (local-tag l) #nil)
                              (eq? k 'arg) (eq? k 'field) (eq? k 'this) (and (local-mutable? l) #t) 0))))
            (clj-map) ls))))

  ;;; %ctx：(%ctx 上下文 子形式) 或 (%ctx 上下文 循环标签 参数个数 子形式)；只能由本库产生（gensym 键）
  (define (expand-in c stx expand)
    (parameterize ((current-ctx c)) (expand stx)))

  (define ctx-key
    (register-hidden-clj-form! "ctx"
      (lambda (e expand)
        (syntax-case e ()
          ((_ c body) (expand-in (syntax->datum #'c) #'body expand))
          ((_ c lp n body) (expand-in (ctx-with-loop (syntax->datum #'c) (cons #'lp (syntax->datum #'n))) #'body expand))))))

  ;;; %node：模板里的编译器节点（cleat-051）。(%node 头 (数据 …) 子形式 …) → (头 (quote 数据) … 子形式的展开 …)，
  ;;; 数据是展开期算好的常量（Var、类型名、描述），子形式照常展开（通常已包在 %ctx 里）
  (define node-key
    (register-hidden-clj-form! "node"
      (lambda (e expand)
        (syntax-case e ()
          ((_ h ds x ...)
           (cons (syntax->datum #'h)
                 (append (map (lambda (d) (list 'quote d)) (syntax->datum #'ds))
                         (map expand #'(x ...)))))))))

  (define (node-id head) (datum->syntax head node-key))

  (define (in-ctx head c stx)
    (if (eq? c (cur))
        stx
        (with-syntax ((k (datum->syntax head ctx-key)) (c (datum->syntax #'here c)) (x stx))
          #'(k c x))))

  (define (in-ctx/loop head c lp n stx)
    (with-syntax ((k (datum->syntax head ctx-key)) (c (datum->syntax #'here c)) (lp lp) (n n) (x stx))
      #'(k c lp n x)))

  ;;; 体：(begin (%ctx 非尾 f1) … fN)；空体为 nil。外层 %ctx 建立 c，最后一个形式继承它
  (define (body-stx head forms c)
    (cond
      ((null? forms) #''#nil)
      ((null? (cdr forms)) (car forms))
      (else
       (with-syntax (((f ...) (map (lambda (f) (in-ctx head (ctx-nontail c) f)) (all-but-last forms)))
                     (l (last forms)))
         #'(begin f ... l)))))

  (define (err e msg) (syntax-violation #f msg e))

  ;;; 重复的绑定名（Clojure 允许：(fn* [a a] a)、(loop* [a 1 a 2] a) 取最后一个；psyntax 报 "duplicate binding"）：
  ;;; 后面还有同名者的标识符换成临时名（它在体里本来就被遮蔽），于是 Scheme 的绑定形式不再有重复（cleat-055）
  (define (dedupe-ids ids)
    (let loop ((ids ids) (acc '()))
      (if (null? ids)
          (reverse acc)
          (loop (cdr ids)
                (cons (if (exists (lambda (y) (bound-identifier=? (car ids) y)) (cdr ids))
                          (car (generate-temporaries (list (car ids))))
                          (car ids))
                      acc)))))

  ;;; 语法列表（可能是带 wrap 的语法对象）→ Scheme 列表
  (define (stx-list x)
    (syntax-case x ()
      ((a ...) #'(a ...))
      (_ (syntax-violation #f "expected a list" x))))

  (define (clj-vector-stx? x) (eq? (syntax-ext-kind x) 'clj-vector))
  (define (vector-items x) (syntax-ext->list x 'clj-vector))
  (define (const-stx x) (datum->syntax #'here x))

  ;;; —— 特殊形式的登记 ——

  (define special-form-names
    (map string->symbol
      '("def" "if" "do" "let*" "loop*" "recur" "fn*" "quote" "var" "throw" "try" "monitor-enter" "monitor-exit" "." "new"
        "set!" "letfn*" "case*" "deftype*" "reify*" "import*" "catch" "finally" "&")))

  ;;; 库体里定义与表达式不能交错：登记写成定义
  (define-syntax registration
    (syntax-rules ()
      ((_ expr) (define dummy (begin expr #f)))))
  (define-syntax define-special
    (syntax-rules ()
      ((_ name (e) body ...) (registration (register-clj-special! 'name (lambda (e) body ...))))))
  (define-syntax define-form
    (syntax-rules ()
      ((_ name (e expand) body ...) (registration (register-clj-form! 'name (lambda (e expand) body ...))))))

  ;;; ———————————————— def ————————————————

  (define (source-file pos) (or (and pos (caddr pos)) (source-path)))

  (define (def-var e ns sym)
    (let ((nsname (sym-ns sym)) (name (clj-symbol (sym-name sym))))
      (if nsname
          (let ((target (namespace-for ns nsname)))
            (cond
              ((not target) (err e "Can't refer to qualified var that doesn't exist"))
              ((eq? target ns) (clr-call clojure.lang.Namespace intern ns name))
              ((nil->f (clr-call clojure.lang.Namespace FindInternedVar target name))
               (err e "Can't create defs outside of current ns"))
              (else (err e "Can't refer to qualified var that doesn't exist"))))
          (let ((o (nil->f (clr-call clojure.lang.Namespace GetMapping ns name))))
            (cond
              ((not o) (clr-call clojure.lang.Namespace intern ns name))
              ((and (var? o) (eq? (clr-call clojure.lang.Var get_Namespace o) ns)) o)
              ((var? o) (clr-call clojure.lang.Namespace intern ns name))   ; 替换 refer 来的 Var（Namespace 打印警告）
              (else (err e (string-append "Expecting var, but " (->string sym) " is mapped to " (->string o)))))))))

  (define-special def (e)
    (define (build head name doc init)
      (let ((sym (syntax->clj name)))
        (unless (clj-symbol? sym) (err e "First argument to def must be a Symbol"))
        (let* ((ns (current-ns))
               (v (def-var e ns sym))
               (sm (meta-of sym))
               (dynamic? (and (meta-get sm kw-dynamic) #t))
               (pos (syntax-position e))
               (n (sym-name sym)))
          (when dynamic? (clr-call clojure.lang.Var setDynamic v))
          (when (and (not dynamic?) (> (string-length n) 2) (string-prefix? "*" n) (string-suffix? "*" n))
            (clr-call System.IO.TextWriter (WriteLine System.String) (clr-static-call clojure.lang.RT errPrintWriter)
              (string-append "Warning: " (->string sym) " not declared dynamic and thus is not dynamically rebindable, "
                             "but its name suggests otherwise. Please either indicate ^:dynamic " (->string sym)
                             " or change the name. (" (source-file pos) ":" (if pos (number->string (car pos)) "0") ")")))
          (let ((al (meta-get sm kw-arglists)))
            (when al
              (clr-call clojure.lang.Var setMeta v (assoc* (meta-of v) kw-arglists (clr-static-call clojure.lang.RT (second System.Object) al)))))
          (let* ((m (assoc* sm kw-line (op-long (if pos (car pos) 0))))
                 (m (assoc* m kw-column (op-long (if pos (cadr pos) 0))))
                 (m (assoc* m kw-file (source-file pos)))
                 (m (if doc (assoc* m kw-doc doc) m))
                 (c (cur)))
            ;; DefExpr → ($clj-def 'var '有初值? '动态? 初值 元数据)（cleat-051）：动态性也在运行时设置，保存的 dll 加载后同样成立
            (with-syntax ((k (node-id head))
                          (data (const-stx (list v (and init #t) dynamic?)))
                          (meta (in-ctx head (ctx-nontail c) (clj-datum->syntax* head m)))
                          (init (if init (in-ctx head (ctx-hinted c (sym-name sym)) init) #''#nil)))
              #'(k $clj-def data init meta))))))
    (syntax-case e ()
      ((head name) (build #'head #'name #f #f))
      ((head name init) (build #'head #'name #f #'init))
      ((head name doc init) (string? (syntax->datum #'doc)) (build #'head #'name (syntax->datum #'doc) #'init))
      ((head name x y z . rest) (err e "Too many arguments to def"))
      ((head name x y) (err e "Too many arguments to def"))
      (_ (err e "Too few arguments to def"))))

  ;;; ———————————————— if / do / quote / var / throw / monitor / import* ————————————————

  (define-special if (e)
    (define (build head test then else)
      (let* ((c (ctx-clear (cur))) (nt (ctx-nontail c)))
        (with-syntax ((test (in-ctx head nt test))
                      (then (in-ctx head c then))
                      (else (if else (in-ctx head c else) #''#nil)))
          #'(if test then else))))
    (syntax-case e ()
      ((head test then) (build #'head #'test #'then #f))
      ((head test then else) (build #'head #'test #'then #'else))
      ((head a b c d . r) (err e "Too many arguments to if"))
      (_ (err e "Too few arguments to if"))))

  (define-special do (e)
    (syntax-case e ()
      ((head form ...)
       (let ((c (ctx-clear (cur))))
         (in-ctx #'head c (body-stx #'head #'(form ...) c))))))

  (define-form quote (e expand)
    (syntax-case e ()
      ((_ x) (list 'quote (syntax->clj #'x)))
      (_ (err e (string-append "Wrong number of args (" (number->string (- (length (stx-list e)) 1)) ") passed to quote")))))

  ;;; Compiler.LookupVar(sym, false)
  (define (lookup-var e sym)
    (let ((ns (current-ns)))
      (cond
        ((sym-ns sym)
         (let ((target (namespace-for ns (sym-ns sym))))
           (and target (nil->f (clr-call clojure.lang.Namespace FindInternedVar target (clj-symbol (sym-name sym)))))))
        ((sym-is? sym "ns") (clr-static-field-get clojure.lang.RT NSVar))
        ((sym-is? sym "in-ns") (clr-static-field-get clojure.lang.RT InNSVar))
        (else
         (let ((o (nil->f (clr-call clojure.lang.Namespace GetMapping ns sym))))
           (cond
             ((not o) #f)
             ((var? o) o)
             (else (err e (string-append "Expecting var, but " (->string sym) " is mapped to " (->string o))))))))))

  (define-form var (e expand)
    (syntax-case e ()
      ((_ x)
       (let ((sym (syntax->clj #'x)))
         (unless (clj-symbol? sym) (err e "var requires a symbol"))
         (let ((v (lookup-var e sym)))
           (unless v (err e (string-append "Unable to resolve var: " (->string sym) " in this context")))
           (list 'quote v))))
      (_ (err e "var requires a symbol"))))

  (define-special throw (e)
    (syntax-case e ()
      ((head x) (with-syntax ((x (in-ctx #'head (ctx-nontail (cur)) #'x))) #'($throw x)))
      ((head) (err e "Too few arguments to throw, throw expects a single Exception instance"))
      (_ (err e "Too many arguments to throw, throw expects a single Exception instance"))))

  (define (monitor-special name method)
    (lambda (e)
      (syntax-case e ()
        ((head x)
         (with-syntax ((x (in-ctx #'head (ctx-nontail (cur)) #'x)) (k (node-id #'head)) (data (const-stx (list method #f))))
           #'(k $clj-call data x)))
        (_ (err e (string-append name " requires one argument"))))))
  (registration (register-clj-special! 'monitor-enter (monitor-special "monitor-enter" "MonitorEnterOf")))
  (registration (register-clj-special! 'monitor-exit (monitor-special "monitor-exit" "MonitorExitOf")))

  (define-form import* (e expand)
    (syntax-case e ()
      ((_ x)
       (let ((name (syntax->clj #'x)))
         (unless (string? name) (err e "import* requires a string"))
         (let ((t (class-for-name name)))
           (unless t (err e (string-append "Unable to resolve classname: " name)))
           (clj-call "ImportOf" #f (list 'quote t)))))
      (_ (err e "import* requires a string"))))

  (define-form catch (e expand) (err e "catch must be inside try"))
  (define-form finally (e expand) (err e "finally must be inside try"))
  (define-form & (e expand) (err e "Unable to resolve symbol: & in this context"))

  ;;; ———————————————— let* / loop* / recur ————————————————

  ;;; 绑定向量 → ((标识符 . 初值) …)；检查与 C# ParseLet / ParseLetfn 相同
  (define (binding-pairs e bindings pairs-msg)
    (unless (clj-vector-stx? bindings) (err e "Bad binding form, expected vector"))
    (let ((items (vector-items bindings)))
      (unless (even? (length items)) (err e pairs-msg))
      (let loop ((items items) (acc '()))
        (if (null? items)
            (reverse acc)
            (let ((sym (syntax->clj (car items))))
              (unless (clj-symbol? sym) (err e (string-append "Bad binding form, expected symbol, got: " (->string sym))))
              (when (sym-ns sym) (err e (string-append "Can't let qualified name: " (->string sym))))
              (when (sym-is? sym "&") (err e "Can't use & as a local binding"))
              (loop (cddr items) (cons (cons (car items) (cadr items)) acc)))))))

  (define (let-special loop?)
    (lambda (e)
      (syntax-case e ()
        ((head bindings form ...)
         (let* ((head #'head)
                (pairs (binding-pairs e #'bindings "Bad binding form, expected matched symbol expression pairs"))
                (c (ctx-clear (cur))))
           ;; 每个初值在非尾位置、能看到之前的绑定
           (let loop ((ps pairs) (locals '()) (out '()))
             (if (pair? ps)
                 (let ((init (in-ctx head (ctx-nontail (ctx-with-locals c locals)) (cdar ps)))
                       (l (make-local* (syntax->clj (caar ps)) 'local)))
                   (loop (cdr ps) (append locals (list l)) (cons (list (caar ps) init) out)))
                 (let ((bindings (reverse out))
                       (bc (ctx-with-locals c locals)))
                   (if loop?
                       (let ((bc (make-ctx #t #f (ctx-locals bc) (ctx-fn-name bc) #f)))
                         (with-syntax ((((x i) ...) bindings)
                                       ((y ...) (dedupe-ids (map car bindings)))
                                       ((lp) (generate-temporaries '(loop)))
                                       (body (body-stx head #'(form ...) bc)))
                           (with-syntax ((body (in-ctx/loop head bc #'lp (length bindings) #'body)))
                             #'(let* ((x i) ...) (let lp ((y x) ...) body)))))
                       (with-syntax ((((x i) ...) bindings)
                                     (body (in-ctx head bc (body-stx head #'(form ...) bc))))
                         #'(let* ((x i) ...) body))))))))
        (_ (err e "Bad binding form, expected vector")))))
  (registration (register-clj-special! 'let* (let-special #f)))
  (registration (register-clj-special! 'loop* (let-special #t)))

  (define-form recur (e expand)
    (let ((c (cur)))
      (syntax-case e ()
        ((_ arg ...)
         (let ((args #'(arg ...)))
           (cond
             ((not (ctx-tail? c)) (err e "Can only recur from tail position"))
             ((not (ctx-loop c)) (err e "Cannot recur across try"))
             ((not (= (length args) (cdr (ctx-loop c))))
              (err e (format "Mismatched argument count to recur, expected: ~a args, got: ~a" (cdr (ctx-loop c)) (length args))))
             (else
              (with-syntax ((lp (car (ctx-loop c))) ((a ...) args))
                (expand-in (ctx-nontail c) #'(lp a ...) expand)))))))))

  ;;; ———————————————— fn* ————————————————

  ;;; 参数向量 → (values 必需参数标识符列表 剩余参数标识符|#f)，检查与 C# ParseFnClause 相同
  (define (fn-params e params)
    (unless (clj-vector-stx? params) (err e "Parameter declaration should be a vector"))
    (let loop ((ps (vector-items params)) (req '()) (rest #f) (amp #f))
      (if (null? ps)
          (begin
            (when (and amp (not rest)) (err e "Invalid parameter list"))
            (when (> (length req) 20) (err e "Can't specify more than 20 params"))
            (values (reverse req) rest))
          (let ((p (syntax->clj (car ps))))
            (unless (clj-symbol? p) (err e "fn params must be Symbols"))
            (when (sym-ns p) (err e (string-append "Can't use qualified name as parameter: " (->string p))))
            (cond
              ((sym-is? p "&")
               (when amp (err e "Invalid parameter list"))
               (loop (cdr ps) req rest #t))
              (amp
               (when rest (err e "Unexpected parameter"))
               (when (tag-of p) (err e "& arg cannot have type hint"))
               (loop (cdr ps) req (car ps) amp))
              (else (loop (cdr ps) (cons (car ps) req) rest amp)))))))

  ;;; ^long / ^double 形参（cleat-042）：循环变量的初值经 RT.longCast / RT.doubleCast（JVM 原始形参的入口转换），
  ;;; 编译器据此把它推断为 int64 / double 局部变量；其余形参原样
  (define rt-type #f)
  (define (prim-param-init head p)
    (let* ((t (tag-of (syntax->clj p)))
           (op (and t (clj-symbol? t) (not (sym-ns t))
                    (cond ((string=? (sym-name t) "long") "longCast")
                          ((string=? (sym-name t) "double") "doubleCast")
                          (else #f)))))
      (if op
          (begin
            (unless rt-type (set! rt-type (class-for-name "clojure.lang.RT")))
            (with-syntax ((k (node-id head)) (p p) (data (const-stx (list "CallStatic" 3 rt-type op '()))))
              #'(k $clj-call data p)))
          p)))

  ;;; 原始签名（cleat-042，JVM 的 IFn$LL 等）：形参与返回的 ^long / ^double 提示各给一个字母 L / D，其余 O；
  ;;; 返回类型取参数向量上的提示（defn 的 ^long [..]）。至少有一个原始类型、不超过 4 个形参、
  ;;; clojure.lang.primifs 里有这个接口时返回签名字符串（如 "LL"），否则 #f
  (define (prim-kind t)
    (and t (clj-symbol? t) (not (sym-ns t))
         (cond ((string=? (sym-name t) "long") "L") ((string=? (sym-name t) "double") "D") (else #f))))
  ;;; rettag：参数向量没有提示时的返回提示（defn 名字上的 ^long 经 fn 形式的 :rettag 传来，同 JVM 的 FnExpr；调用点用 Var 的 :tag）
  (define (prim-sig vec params . rettag)
    (let ((sig (apply string-append
                      (append (map (lambda (p) (or (prim-kind (tag-of p)) "O")) params)
                              (list (or (prim-kind (tag-of vec))
                                        (and (pair? rettag) (prim-kind (car rettag)))
                                        "O"))))))
      (and (<= (length params) 4)
           (exists (lambda (ch) (not (char=? ch #\O))) (string->list sig))
           (class-for-name (string-append "clojure.lang.primifs." sig))
           sig)))

  (define (fn-class-name c self)
    (let ((base (or (ctx-fn-name c) (munge (ns-name* (current-ns)))))
          (simple (cond
                    (self (munge (sym-name (syntax->clj self))))
                    ((ctx-hint c) (munge (ctx-hint c)))
                    (else (string-append "fn__" (op-next-id #f))))))
      (string-append base "$" simple)))

  (define-special fn* (e)
    (syntax-case e ()
      ((head . rest)
       (let* ((head #'head)
              (c (cur))
              (rest (stx-list #'rest))
              (self (and (pair? rest) (clj-symbol? (syntax->clj (car rest))) (car rest)))
              (rest (if self (cdr rest) rest))
              ;; (fn* [args] body…) → ((…))；子句按顺序逐个检查（与 C# 的报错顺序相同）
              (single? (and (pair? rest) (clj-vector-stx? (car rest))))
              (clauses (if single? (list rest) rest))
              (clause (lambda (cl)
                        (if single?
                            cl
                            (let ((d (syntax->clj cl)))
                              (unless (and (clj-list? d) (> (count* d) 0)) (err e (string-append "Bad fn clause: " (->string d))))
                              (syntax-case cl () ((p . b) (cons #'p (stx-list #'b))))))))
              (name (fn-class-name c self))
              (outer (append (if self (list (make-local (syntax->clj self) #f #f 'this)) '()) '()))
              (base-ctx (make-ctx #t #f (append outer (ctx-locals c)) name #f)))
         (define prims '())
         (let loop ((cls clauses) (fixed '()) (variadic #f) (arities '()))
           (if (pair? cls)
               (let*-values (((cl) (clause (car cls)))
                             ((req rest) (fn-params e (car cl))))
                 (let* ((n (length req))
                        (params (if rest (append req (list rest)) req))
                        (locals (map (lambda (p) (make-local* (syntax->clj p) 'arg)) params))
                        (bc (ctx-with-loop (ctx-with-locals base-ctx locals) #f))
                        (bc (make-ctx #t #f (ctx-locals bc) name #f)))
                   (cond
                     ((and rest variadic) (err e "Can't have more than 1 variadic overload"))
                     ((and (not rest) (memv n arities)) (err e "Can't have 2 overloads with same arity")))
                   (unless rest
                     (let ((sig (prim-sig (syntax->clj (car cl)) (map syntax->clj req)
                                          (nil->f (meta-get (meta-of (syntax->clj e)) kw-rettag)))))
                       (when sig (set! prims (cons (cons n sig) prims)))))
                   (with-syntax (((p ...) (dedupe-ids params))
                                 ((lp) (generate-temporaries '(recur)))
                                 (body (body-stx head (cdr cl) bc)))
                     (with-syntax ((body (in-ctx/loop head bc #'lp (length params) #'body))
                                   ((init ...) (map (lambda (p) (prim-param-init head p)) (stx-list #'(p ...)))))
                       (let ((compiled #'((p ...) (let lp ((p init) ...) body))))
                         (if rest
                             (loop (cdr cls) fixed (cons n compiled) arities)
                             (loop (cdr cls) (cons compiled fixed) variadic (cons n arities))))))))
               (begin
                 (when (and variadic (pair? arities) (>= (apply max arities) (+ (car variadic) 1)))
                   (err e "Can't have fixed arity function with more params than variadic function"))
                 ;; ($clj-fn '类名 '变长必需个数|#f '原始签名 (case-lambda 固定子句 … 变长子句))（cleat-051）：编译为 AFunction / RestFn 的子类；
                 ;; 变长子句在最后，形参为"必需参数 + 剩余 seq"
                 ;; :rettag（defn 放在 fn 形式上的返回类型提示）只给编译器看，同 JVM 的 FnExpr 不留在函数对象上——
                 ;; 否则每个 defn 的函数都被 WithMetaOf 包成 MetaWrapper（RestFn）：直接链接的守卫失配、每次调用走 applyTo（cleat-042）
                 (let* ((m (let ((m (user-meta (syntax->clj e))))
                             (if (nil? m) m (let ((m (dissoc* m kw-rettag))) (if (= (count* m) 0) #nil m))))))
                   (with-syntax ((k (node-id head))
                                 (data (const-stx (list name (and variadic (car variadic)) (reverse prims))))
                                 ((cl ...) (append (reverse fixed) (if variadic (list (cdr variadic)) '()))))
                     (let* ((fn #'(k $clj-fn data (case-lambda cl ...)))
                            (fn (if (nil? m)
                                    fn
                                    (with-syntax ((fn fn) (m (in-ctx head (ctx-nontail c) (clj-datum->syntax* head m)))
                                                  (data (const-stx (list "WithMetaOf" #f))))
                                      #'(k $clj-call data fn m)))))
                       (if self
                           (with-syntax ((self self) (fn fn)) #'(letrec ((self fn)) self))
                           fn)))))))))))

  ;;; ———————————————— letfn* ————————————————

  (define (fn*-form? d)
    (and (clj-list? d) (> (count* d) 0) (sym-is? (clr-static-call clojure.lang.RT (first System.Object) d) "fn*")))

  (define-special letfn* (e)
    (syntax-case e ()
      ((head bindings form ...)
       (let* ((head #'head)
              (pairs (binding-pairs e #'bindings "Bad binding form, expected matched symbol expression pairs"))
              (c (ctx-clear (cur)))
              (locals (map (lambda (p) (make-local* (syntax->clj (car p)) 'local)) pairs))
              (bc (ctx-with-locals c locals)))
         ;; 初值都是 (fn* …) 时为 letrec（编译器对 lambda 的 letrec 不装箱）。否则（宏展开成 fn*、或不是函数——JVM 的 Clojure
         ;; 在编译期拒绝）先绑定为 nil 再依次赋值：Scheme 的 letrec 会在读到未初始化的变量时抛 Scheme 条件（cleat-055 值边界）
         (with-syntax ((((x i) ...)
                        (map (lambda (p x)
                               (list x (in-ctx head (ctx-hinted bc (sym-name (syntax->clj (car p)))) (cdr p))))
                             pairs (dedupe-ids (map car pairs))))
                       (body (in-ctx head bc (body-stx head #'(form ...) bc))))
           (if (for-all (lambda (p) (fn*-form? (syntax->clj (cdr p)))) pairs)
               #'(letrec ((x i) ...) body)
               #'(let ((x #nil) ...) (set! x i) ... body)))))
      (_ (err e "Bad binding form, expected vector"))))

  ;;; ———————————————— try ————————————————

  (define (clause-head f)
    (let ((d (syntax->clj f)))
      (and (clj-list? d) (let ((h (clr-static-call clojure.lang.RT (first System.Object) d)))
                           (cond ((sym-is? h "catch") 'catch) ((sym-is? h "finally") 'finally) (else #f))))))

  (define-special try (e)
    (syntax-case e ()
      ((head form ...)
       (let* ((head #'head) (c (cur)) (tail? (ctx-tail? c)))
         (let loop ((fs #'(form ...)) (body '()) (catches '()) (fin #f))
           (if (pair? fs)
               (let ((f (car fs)))
                 (case (clause-head f)
                   ((catch)
                    (syntax-case f ()
                      ((_ tsym bsym hform ...)
                       (let* ((td (syntax->clj #'tsym))
                              ;; (catch :default e …)（ClojureScript / cljc 写法，cleat-032）：捕获一切，即 System.Exception
                              (t (if (and (keyword? td) (not (nil->f (clr-call clojure.lang.Keyword get_Namespace td)))
                                          (string=? (clr-call clojure.lang.Keyword get_Name td) "default"))
                                     (class-for-name "System.Exception")
                                     (maybe-type td))))
                         (unless t (err e (string-append "Unable to resolve classname: " (->string td))))
                         (let ((b (syntax->clj #'bsym)))
                           (unless (clj-symbol? b) (err e (string-append "Bad binding form, expected symbol, got: " (->string b))))
                           (when (sym-ns b) (err e (string-append "Can't bind qualified name:" (->string b))))
                           (let* ((hc (ctx-with-locals (ctx-no-recur c tail?)
                                        (list (make-local b (clj-symbol (clr-call System.Type get_FullName t)) #f 'catch))))
                                  (h (in-ctx head hc (body-stx head #'(hform ...) hc))))
                             (loop (cdr fs) body (cons (list (type-name t) #'bsym h) catches) fin)))))
                      (_ (err e "Unable to resolve classname: nil"))))
                   ((finally)
                    (unless (null? (cdr fs)) (err e "finally clause must be last in try expression"))
                    (syntax-case f ()
                      ((_ fform ...)
                       (let ((fc (ctx-no-recur c #f)))
                         (loop (cdr fs) body catches (in-ctx head fc (body-stx head #'(fform ...) fc)))))))
                   (else
                    (unless (null? catches) (err e "Only catch or finally clause can follow catch in try expression"))
                    (loop (cdr fs) (cons f body) catches fin))))
               (let* ((bc (ctx-no-recur c tail?))
                      (b (in-ctx head bc (body-stx head (reverse body) bc))))
                 (if (and (null? catches) (not fin))
                     b
                     (let ((x (if (null? catches)
                                  b
                                  (with-syntax ((b b)
                                                ((x ...) (apply append
                                                           (map (lambda (cl)
                                                                  (with-syntax ((t (const-stx (car cl))) (v (cadr cl)) (h (caddr cl)))
                                                                    (list #''t #'(lambda (v) h))))
                                                                (reverse catches)))))
                                    #'($try/catch b x ...)))))
                       (if fin
                           (with-syntax ((x x) (fin fin)) #'($try/finally x fin))
                           x))))))))))

  ;;; ———————————————— 宿主互操作：. / new / set! ————————————————

  ;;; 泛型实参：Type 的向量常量（object[]），没有则 nil
  (define (type-args-datum types) (if types (list->vector types) '()))

  ;;; 实参：(by-ref x) 不支持；其余在非尾位置
  (define (host-args e head args)
    (map (lambda (a)
           (let ((d (syntax->clj a)))
             (when (and (clj-list? d) (sym-is? (clr-static-call clojure.lang.RT (first System.Object) d) "by-ref"))
               (err e "by-ref arguments are not supported yet"))
             (in-ctx head (ctx-nontail (ctx-clear (cur))) a)))
         args))

  ;;; 表达式的静态类型（来自 ^Tag 提示或局部的提示），没有则 #f
  (define (expr-type d)
    (tag-type (or (tag-of d)
                  (and (clj-symbol? d) (not (sym-ns d))
                       (let ((l (find-local d))) (and l (local-tag l)))))))

  ;;; (. target member args…) 的分析：(values 静态类型|#f 目标语法|#f 成员名 属性? 实参语法列表 泛型实参|#f 必须是方法?)
  (define (parse-dot e)
    (syntax-case e ()
      ((head target member . args)
       (let* ((md (syntax->clj #'member))
              (td (syntax->clj #'target)))
         (let-values (((msym args method?)
                       (cond
                         ((clj-symbol? md) (values md (stx-list #'args) #f))
                         ((and (null? (stx-list #'args)) (clj-list? md)
                               (clj-symbol? (clr-static-call clojure.lang.RT (first System.Object) md)))
                          (syntax-case #'member () ((m . margs) (values (syntax->clj #'m) (stx-list #'margs) #t))))
                         (else (err e "Malformed member expression")))))
           (let* ((static (maybe-type td))
                  (first-arg (and (pair? args) (syntax->clj (car args))))
                  (type-args (and first-arg (clj-list? first-arg)
                                  (sym-is? (clr-static-call clojure.lang.RT (first System.Object) first-arg) "type-args")
                                  (map (lambda (t)
                                         (or (maybe-type t) (tag-type t) (err e (string-append "Unable to resolve classname: " (->string t)))))
                                       (cdr (seq->list first-arg)))))
                  (args (if type-args (cdr args) args))
                  (name (sym-name msym))
                  (prop? (and (> (string-length name) 1) (char=? (string-ref name 0) #\-)))
                  (member (munge (if prop? (substring name 1 (string-length name)) name)))
                  (type (or static (expr-type td))))
             (values static (and (not static) #'target) member prop? args type-args method? type)))))
      (_ (err e "Malformed member expression, expecting (. target member ...)"))))

  (registration (register-clj-special! (string->symbol ".") (lambda (e) (dot-special e))))
  (define (dot-special e)
    (syntax-case e ()
      ((head . _)
       (let-values (((static target member prop? args type-args method? type) (parse-dot e)))
         (let* ((head #'head)
                (nt (ctx-nontail (ctx-clear (cur))))
                (args (host-args e head args)))
           ;; ClojureOps.CallStatic / CallInstance / GetStaticField / GetInstanceField（Reflector 的规则）：类型、名字、泛型实参是常量
           (let ((t (or type #nil)) (gen (type-args-datum type-args)))
             (with-syntax ((k (node-id head)) ((a ...) args))
               (cond
                 ((and static (null? args) (not method?))
                  (with-syntax ((data (const-stx (if prop? (list "GetStaticField" #f t member) (list "CallStatic" 3 t member gen)))))
                    #'(k $clj-call data)))
                 (static (with-syntax ((data (const-stx (list "CallStatic" 3 t member gen)))) #'(k $clj-call data a ...)))
                 (else
                  (with-syntax ((x (in-ctx head nt target)))
                    (if (and prop? (null? args) (not method?))
                        (with-syntax ((data (const-stx (list "GetInstanceField" #f t member)))) #'(k $clj-call data x))
                        (with-syntax ((data (const-stx (list "CallInstance" 4 t member)))
                                      (gen (const-stx gen)))
                          #'(k $clj-call data x 'gen a ...)))))))))))))

  (define-special new (e)
    (syntax-case e ()
      ((head tsym arg ...)
       (let* ((td (syntax->clj #'tsym))
              (t (maybe-type td)))
         (unless t (err e (string-append "Unable to resolve classname: " (->string td))))
         (with-syntax ((k (node-id #'head)) (data (const-stx (list "NewOf" 1 t))) ((a ...) (host-args e #'head #'(arg ...))))
           #'(k $clj-call data a ...))))
      (_ (err e "wrong number of arguments, expecting: (new Classname args...)"))))

  (define-special set! (e)
    (syntax-case e ()
      ((head target val)
       (let* ((head #'head)
              (td (syntax->clj #'target))
              (v (in-ctx head (ctx-nontail (ctx-clear (cur))) #'val)))
         (define (field-set static target member)
           (with-syntax ((k (node-id head)) (data (const-stx (list "SetFieldOf" #f (or static #nil) member))) (v v)
                         (x (if target (in-ctx head (ctx-nontail (cur)) target) #''#nil)))
             #'(k $clj-call data x v)))
         (cond
           ((clj-symbol? td)
            (let ((l (and (not (sym-ns td)) (find-local td))))
              (cond
                (l
                 (unless (local-mutable? l) (err e (string-append "Cannot assign to non-mutable: " (->string td))))
                 (with-syntax ((x #'target) (v v)) #'(let ((tmp v)) (set! x tmp) tmp)))
                ((static-member-of td) => (lambda (sm) (field-set (car sm) #f (cdr sm))))
                (else
                 (let-values (((o msg p?) (resolve-in (current-ns) td)))
                   (cond
                     ((var? o) (with-syntax ((k (node-id head)) (data (const-stx (list o))) (v v)) #'(k $clj-var-set! data v)))
                     (msg (err e msg))
                     (else (err e "Invalid assignment target"))))))))
           ((and (clj-list? td) (rewrite-to-dot td e)) =>
            (lambda (dot)
              (let ((dot-stx (clj-datum->syntax* head dot)))
                (let-values (((static target member prop? args type-args method? type) (parse-dot dot-stx)))
                  (unless (null? args) (err e "Invalid assignment target"))
                  (field-set (or static type) target member)))))
           (else (err e "Invalid assignment target")))))
      (_ (err e "Malformed assignment, expecting (set! target val)"))))

  ;;; ———————————————— case* ————————————————

  (define-special case* (e)
    (syntax-case e ()
      ((head x shift mask default imap switch-type test-type . skip)
       (let* ((head #'head)
              (xd (syntax->clj #'x))
              (c (ctx-clear (cur))))
         (unless (and (clj-symbol? xd) (not (sym-ns xd)) (find-local xd)) (err e "case* expects a local as its expression"))
         (let* ((tt (sym-name* (syntax->clj #'test-type)))
                (kind (cond ((string=? tt "int") 0) ((string=? tt "hash-equiv") 1) (else 2)))
                (skip-set (let ((s (stx-list #'skip))) (if (pair? s) (syntax->clj (car s)) #nil)))
                (kvs (syntax-ext->list #'imap 'clj-map))
                (entries
                  (let loop ((kvs kvs) (acc '()))
                    (if (null? kvs)
                        (reverse acc)
                        (let* ((h (syntax->clj (car kvs)))
                               (pair (vector-items (cadr kvs))))
                          (loop (cddr kvs)
                                (cons (list h (syntax->clj (car pair)) (cadr pair)
                                            (and (not (nil? skip-set)) (clr-call clojure.lang.IPersistentSet contains skip-set h)))
                                      acc)))))))
           ;; ($clj-case '(shift mask 种类 ((哈希 测试值 跳过?) …)) x default then …)（cleat-051）：编译器按 CaseExpr 的规则算键、
           ;; IL switch 分派、比较测试值；分支与 default 在尾位置
           (let ((int (lambda (n) (string->number (->string n)))))
             (with-syntax ((k (node-id head))
                           (data (const-stx (list (list (int (syntax->clj #'shift)) (int (syntax->clj #'mask)) kind
                                                        (map (lambda (en) (list (int (car en)) (cadr en) (cadddr en))) entries)))))
                           (x (in-ctx head (ctx-nontail c) #'x))
                           ((then ...) (map (lambda (en) (in-ctx head c (caddr en))) entries))
                           (default (in-ctx head c #'default)))
               #'(k $clj-case data x default then ...))))))
      (_ (err e "Malformed case*"))))

  (define (sym-name* x) (if (keyword? x) (clr-call clojure.lang.Keyword get_Name x) (->string x)))

  ;;; ———————————————— deftype* / reify*（cleat-051：编译为真实 CLR 类型）————————————————

  (define (host-check e r) (if (string? r) (err e r) r))

  ;;; 类型名 → 编译器的类型描述 ("名字" . 值类型?)
  (define (type-desc name) (cons name (and (clr-static-call Cleat.Scheme.Clojure.HostSyntax IsValueType name) #t)))

  ;;; ($clj-deftype '描述 (lambda (字段 …) ($clj-methods 方法 …)))：字段是 lambda 绑定的变量（方法体里可读、可变字段可 set!），
  ;;; 方法是 (lambda (this 参数 …) 体)（recur 回到方法开头）。描述的格式见编译器 codegen 的 deftype-class!：
  ;;; 接口方法按 C# 展开器的规则在展开期用反射选定（TypeMethod），接口全集与没有直接实现的接口方法（桥接或抛错的桩）
  ;;; 由 HostSyntax.InterfaceLayout 算出——编译器不反射
  (define (build-type e head tagname class-name field-syms ifaces method-forms opts reify?)
    (let* ((ifaces (host-check e (clr-static-call Cleat.Scheme.Clojure.HostSyntax ResolveInterfaces ifaces)))
           (fields (map (lambda (f)
                          (let* ((s (syntax->clj f))
                                 (m (meta-of s))
                                 (vol (and (meta-get m kw-volatile-mutable) #t))
                                 (mut (or vol (and (meta-get m kw-unsync-mutable) #t)))
                                 (ft (tag-type (tag-of s))))
                            (list f s mut vol (if ft (type-name ft) "System.Object"))))
                        field-syms))
           (field-locals (map (lambda (f) (make-local (cadr f) (tag-of (cadr f)) (caddr f) 'field)) fields))
           (c (cur)))
      ;; 字段名重复：JVM 的 Clojure 在定义类时报 ClassFormatError "Duplicate field name"；psyntax 会报 Scheme 的 "duplicate binding"
      (let check ((fs fields))
        (when (pair? fs)
          (when (exists (lambda (g) (string=? (sym-name (cadr g)) (sym-name (cadar fs)))) (cdr fs))
            (err e (string-append "Duplicate field name \"" (sym-name (cadar fs)) "\" in " class-name)))
          (check (cdr fs))))
      ;; 方法体可以引用正在定义的类型（(new Foo …)、^Foo）：先登记同名的桩类型（HostSyntax.DefineStub，运行时换成真正的类型）
      (unless reify?
        (clr-static-call Cleat.Scheme.Clojure.HostSyntax (DefineStub System.String System.Object System.String System.Object) class-name
          (list->vector (map (lambda (f) (munge (sym-name (cadr f)))) fields))
          tagname ifaces))
      (let loop ((ms method-forms) (specs '()) (rs '()) (lambdas '()))
        (if (pair? ms)
            (syntax-case (car ms) ()
              ((mname params mbody ...)
               (let* ((pd (syntax->clj #'params))
                      (r (host-check e (clr-static-call Cleat.Scheme.Clojure.HostSyntax TypeMethod ifaces (syntax->clj #'mname) pd)))
                      (iface (nil->f (vector-ref r 0))) (name (vector-ref r 1)) (ret (vector-ref r 2)) (ptypes (vector->list (vector-ref r 3)))
                      (pitems (vector-items #'params))
                      (this (car pitems)) (args (cdr pitems))
                      (locals (append field-locals
                                      (list (make-local (syntax->clj this) (clj-symbol class-name) #f 'this))
                                      (map (lambda (a t) (make-local (clj-symbol (sym-name (syntax->clj a))) (clj-symbol t) #f 'arg)) args ptypes)))
                      (bc (make-ctx #t #f (append (reverse locals) (ctx-locals c)) (ctx-fn-name c) #f))
                      (spec (list iface name (type-desc ret) (map type-desc ptypes))))
                 (with-syntax (((this a ...) (dedupe-ids (cons this args)))
                               ((lp) (generate-temporaries '(recur)))
                               (body (body-stx head #'(mbody ...) bc)))
                   (with-syntax ((body (in-ctx/loop head bc #'lp (length args) #'body)))
                     (loop (cdr ms) (cons spec specs) (cons r rs)
                           (cons #'(lambda (this a ...) (let lp ((a a) ...) body)) lambdas))))))
              (_ (err e (string-append "Method name must be a symbol: " (->string (syntax->clj (car ms)))))))
            (let* ((layout (clr-static-call Cleat.Scheme.Clojure.HostSyntax InterfaceLayout ifaces (list->vector (reverse rs)) reify?))
                   (stubs (map (lambda (st)
                                 (list (vector-ref st 0) (vector-ref st 1) (type-desc (vector-ref st 2))
                                       (map type-desc (vector->list (vector-ref st 3)))
                                       (nil->f (vector-ref st 4))))
                               (vector->list (vector-ref layout 1))))
                   ;; defrecord（cleat-053）：末尾的 __meta __extmap __hash __hasheq 不进 getBasis；有它们时编译器另生成
                   ;; 只取基本字段的构造函数、去掉 __hash __hasheq 的构造函数与静态 create(IPersistentMap)（ClojureCLR 的 AltCtorDrops）
                   (drops (let count-drops ((fs (reverse fields)) (n 0))
                            (if (and (pair? fs) (member (sym-name (cadar fs)) '("__meta" "__extmap" "__hash" "__hasheq")))
                                (count-drops (cdr fs) (+ n 1))
                                n)))
                   (basis (let take ((fs fields) (n (- (length fields) drops))) (if (= n 0) '() (cons (car fs) (take (cdr fs) (- n 1))))))
                   (info (list class-name reify?
                               (map (lambda (f) (list (munge (sym-name (cadr f))) (caddr f) (cadddr f))) fields)
                               (vector->list (vector-ref layout 0))
                               (reverse specs)
                               stubs
                               (and (not reify?) (apply clj-vector (map cadr basis)))
                               (if (and (not reify?) (> drops 0)) (map (lambda (f) (sym-name (cadr f))) basis) '())
                               drops)))
              (with-syntax ((k (node-id head)) (data (const-stx (list info))) (none (const-stx '()))
                            ((f ...) (map car fields)) ((l ...) (reverse lambdas)))
                #'(k $clj-deftype data (lambda (f ...) (k $clj-methods none l ...)))))))))

  (define (nil-of x) (or x #nil))

  (define-special deftype* (e)
    (syntax-case e ()
      ((head tagname class-name fields . rest)
       (let loop ((rest (stx-list #'rest)) (opts (clj-map)))
         (if (and (pair? rest) (keyword? (syntax->clj (car rest))) (pair? (cdr rest)))
             (loop (cddr rest) (assoc* opts (syntax->clj (car rest)) (syntax->clj (cadr rest))))
             (if (meta-get opts kw-cleat-interface)
                 (build-interface e #'head (sym-name (syntax->clj #'class-name))
                                  (or (meta-get opts kw-implements) (clj-vector)) (meta-get opts kw-cleat-interface))
                 (build-type e #'head (sym-name (syntax->clj #'tagname)) (sym-name (syntax->clj #'class-name))
                             (vector-items #'fields) (or (meta-get opts kw-implements) (clj-vector)) rest opts #f)))))))

  ;;; gen-interface / definterface（cleat-053）：genclass.clj 的 gen-interface 宏展开为
  ;;;   (deftype* 名字 名字 [] :implements [父接口 …] :cleat/interface [[方法名 [参数类型 …] 返回类型] …])
  ;;; 类型是 Type 或类型符号。编译为 Lcil 的 interface 类型（描述的 reify? 位置为 interface），值为该类型——AOT 时与 deftype 一样
  ;;; 进同一个程序集。原始类型参数照原样（int、long …），没有 by-ref。
  (define (build-interface e head class-name extends sigs)
    (define (resolve-type x)
      (let ((t (if (clr-is System.Type x) x (maybe-type x))))
        (unless t (err e (string-append "Unable to resolve classname: " (->string x))))
        (type-desc (type-name t))))
    (let* ((ifaces (host-check e (clr-static-call Cleat.Scheme.Clojure.HostSyntax ResolveInterfaces extends)))
           (specs (map (lambda (sig)
                         (let ((items (seq->list sig)))
                           (unless (>= (length items) 3) (err e "Malformed gen-interface method signature"))
                           (list #f (sym-name* (car items)) (resolve-type (caddr items))
                                 (map (lambda (p)
                                        (when (clj-list? p) (err e "by-ref parameters are not supported in gen-interface"))
                                        (resolve-type p))
                                      (seq->list (cadr items))))))
                       (seq->list sigs)))
           (info (list class-name 'interface '()
                       (map type-name (vector->list ifaces))
                       specs '() #f)))
      (with-syntax ((k (node-id head)) (data (const-stx (list info))) (none (const-stx '())))
        #'(k $clj-deftype data (lambda () (k $clj-methods none))))))

  (define-special reify* (e)
    (syntax-case e ()
      ((head ifaces method ...)
       (let* ((head #'head)
              (is (clr-call clojure.lang.IPersistentCollection cons (syntax->clj #'ifaces) (clj-symbol "clojure.lang.IObj")))
              (class-name (string-append (munge (ns-name* (current-ns))) "$reify__" (op-next-id #f)))
              (node (build-type e head class-name class-name '() is #'(method ...) (clj-map) #t))
              (m (user-meta (syntax->clj e))))
         (if (nil? m)
             node
             (with-syntax ((k (node-id head)) (data (const-stx (list "WithMetaOf" #f)))
                           (node node) (m (in-ctx head (ctx-nontail (cur)) (clj-datum->syntax* head m))))
               #'(k $clj-call data node m)))))))

  ;;; ———————————————— 字面量与集合（boot 的 $datum 挂钩）————————————————

  (define no-const (list 'no-const))

  ;;; 常量的值：字面量本身；(quote x) → x；集合元素都是常量时按值重建；否则 no-const
  (define (const-value d)
    (cond
      ((clj-symbol? d) no-const)
      ((nil? d) d)
      ((or (clr-is clojure.lang.IRecord d) (clr-is clojure.lang.IType d)) d)
      ((syntax-extension-of d) =>
       (lambda (x)
         (let ((kids ((syntax-extension-children x) d)))
           (cond
             ((null? kids) d)
             ((eq? (syntax-extension-kind x) 'clj-list)
              (if (and (sym-is? (car kids) "quote") (= (length kids) 2)) (cadr kids) no-const))
             (else
              (let ((vals (map const-value kids)))
                (if (memq no-const vals)
                    no-const
                    ((syntax-extension-rebuild x) d vals))))))))
      (else d)))

  (define (find-id x)
    (cond
      ((identifier? x) x)
      ((syntax-ext-kind x) => (lambda (k) (exists find-id (syntax-ext->list x k))))
      (else #f)))

  (define (datum-expression e expand)
    (let ((d (syntax->clj e)))
      (cond
        ((nil? d) (list 'quote #nil))
        ;; Namespace 对象（RT 的引导 ns 宏的展开结果）：不能作为常量存进 dll，按名字取（cleat-052）
        ((clr-is clojure.lang.Namespace d)
         (clj-call "FindNamespaceOf" #f (list 'quote (clr-prop-get clojure.lang.Namespace Name d))))
        ((memq (syntax-ext-kind e) '(clj-vector clj-map clj-set clj-list))
         (let* ((v (const-value d))
                (m (user-meta d))
                (node (if (eq? v no-const)
                          (let ((kind (syntax-ext-kind e)))
                            (apply clj-call (case kind ((clj-vector) "VectorOf") ((clj-map) "MapOf") (else "SetOf")) 0
                                   (map (lambda (x) (expand-in (ctx-nontail (cur)) x expand)) (syntax-ext->list e kind))))
                          (list 'quote (if (clr-is clojure.lang.IObj v) (with-meta* v #nil) v)))))
           (cond
             ((nil? m) node)
             ((eq? (car node) 'quote) (list 'quote (with-meta* (cadr node) m)))
             (else
              (let ((mv (const-value m)))
                (clj-call "WithMetaOf" #f node
                      (if (eq? mv no-const)
                          (let ((id (find-id e)))
                            (unless id (err e "Unsupported non-constant metadata on a literal without symbols"))
                            (expand-in (ctx-nontail (cur)) (clj-datum->syntax* id m) expand))
                          (list 'quote mv))))))))
        (else (list 'quote d)))))

  ;;; ———————————————— 调用（boot 的 $application 挂钩与以 Var 开头的调用）————————————————

  ;;; InvokeExpr → ($clj-invoke f a …)（cleat-051：callvirt IFn::invoke(k)）
  (define (invoke-core f-core arg-cores) (cons* '$clj-invoke f-core arg-cores))

  (define (application e expand)
    (syntax-case e ()
      ((f arg ...)
       (let ((fd (syntax->clj #'f)) (nt (ctx-nontail (cur))) (args #'(arg ...)))
         (when (nil? fd) (err e (string-append "Can't call nil, form: " (print-str (syntax->clj e)))))
         (cond
           ;; 以宏 Var 对象开头（宏构造的 (#'m …)，cleat-052）：展开该宏，结果挂使用处上下文再展开
           ((macro-var-head (syntax->clj e) (lambda (msg) (err e msg)))
            => (lambda (v)
                 (expand (clj-datum->syntax* #'f (call-macro v (syntax->clj e) (syntax-position e))))))
           ((and (keyword? fd) (<= 1 (length args) 2))
             ;; KeywordInvokeExpr：(:k x) / (:k x 缺省)（cleat-051：ILookup.valAt 快速路径，否则 RT.get）
             (cons* '$clj-keyword-invoke (list 'quote fd) (map (lambda (a) (expand-in nt a expand)) args)))
           (else
            (invoke-core (expand-in nt #'f expand) (map (lambda (a) (expand-in nt a expand)) args))))))))

  ;;; 把 Clojure 形式 old 的源位置（:line :column :source-span）与 :tag 带到 new 上
  (define (transfer-source new old)
    (if (not (clr-is clojure.lang.IObj new))
        new
        (let* ((om (meta-of old))
               (nm (fold-left (lambda (m k)
                                (let ((v (meta-get om k)))
                                  (if (and v (not (meta-get m k))) (assoc* m k v) m)))
                              (if (nil? (meta-of new)) (clj-map) (meta-of new))
                              (list kw-line kw-column kw-source-span kw-tag))))
          (with-meta* new nm))))

  ;;; Var 的 :arglists 里参数个数为 n 的固定参数列表带原始提示时的签名（JVM 的 InvokeExpr：按 Var 元数据里的提示
  ;;; 生成 IFn$LL.invokePrim 调用；def 在展开初值之前就把 :arglists 放到 Var 上，自递归也能用），否则 #f
  (define (var-prim-sig vm n)
    (let ((al (meta-get vm kw-arglists)))
      (and al (clr-is clojure.lang.ISeq (clr-static-call clojure.lang.RT (seq System.Object) al))
           (let loop ((vs (seq->list al)))
             (cond ((null? vs) #f)
                   ((and (clr-is clojure.lang.IPersistentVector (car vs)) (= (count* (car vs)) n)
                         (let ((ps (seq->list (car vs))))
                           (and (not (exists (lambda (p) (and (clj-symbol? p) (sym-is? p "&"))) ps))
                                (prim-sig (car vs) ps (nil->f (meta-get vm kw-tag)))))))
                   (else (loop (cdr vs))))))))

  ;;; ———————————————— 小函数内联（cleat-042 第 4 项）————————————————
  ;;; 接口文件里记了 Lcore 的小函数（Driver.InlineTarget）在调用点展开为
  ;;;   ((lambda (r a …) (if ($clj-call "IsInstanceOf" #f '函数类 r) ((case-lambda 子句) a …) ($clj-invoke r a …))) Var读 实参 …)
  ;;; 守卫保证 with-redefs / 重定义照常生效；实参只求值一次。子句的约束变量逐一改名（同一个体内联多次时名字不重复，
  ;;; 前端按符号建表）。构建 Clojure 镜像时关闭（inline-calls? 为 #f）：镜像里的子句不含内联结果，各级镜像的接口文件相同，不动点成立
  (define inline-calls? (make-parameter #t))
  (define (inline-target v n)
    (let ((r (clr-static-call Cleat.Scheme.Clojure.Driver InlineTarget v n)))
      (and (vector? r) r)))

  ;; Lcore 的 α 改名：只看 Scheme 的序对与符号（与 parse-core 相同），约束形式是 lambda / case-lambda（含 annotated-）/ letrec / letrec*
  ;; 内联子句的形参与实参临时变量带 $inl 后缀：编译器不把它们推断为 double（函数边界上实参是 Object——Util.equiv 的同一性捷径、
  ;; double 除零等在 object 重载与 double 重载上行为不同；long 的两组重载行为相同，照常推断）
  (define (inl-gensym) (string->symbol (string-append (symbol->string (gensym)) "$inl")))
  (define (alpha-rename-clause cl)
    (define top? #t)
    (define (bind fs env)
      (cond ((null? fs) (values '() env))
            ((symbol? fs) (let ((n (if top? (inl-gensym) (gensym)))) (values n (cons (cons fs n) env))))
            ((pair? fs) (let-values (((n env) (bind (car fs) env)))
                          (let-values (((rest env) (bind (cdr fs) env)))
                            (values (cons n rest) env))))
            (else (values fs env))))
    (define (clause cl env)
      (let-values (((fs env) (bind (car cl) env)))
        (set! top? #f)
        (cons fs (walk* (cdr cl) env))))
    (define (walk* xs env)
      (cond ((pair? xs) (cons (walk (car xs) env) (walk* (cdr xs) env)))
            (else xs)))
    (define (walk x env)
      (cond
        ((symbol? x) (let ((p (assq x env))) (if p (cdr p) x)))
        ((not (pair? x)) x)
        ((eq? (car x) 'quote) x)
        ((eq? (car x) 'lambda) (cons 'lambda (clause (cdr x) env)))
        ((eq? (car x) 'case-lambda) (cons 'case-lambda (map (lambda (c) (clause c env)) (cdr x))))
        ((eq? (car x) 'annotated-case-lambda)
         (cons* 'annotated-case-lambda (cadr x) (map (lambda (c) (clause c env)) (cddr x))))
        ((eq? (car x) 'annotated-call) (cons* 'annotated-call (cadr x) (walk* (cddr x) env)))
        ((memq (car x) '(letrec letrec*))
         (let* ((names (map car (cadr x)))
                (env (append (map (lambda (s) (cons s (gensym))) names) env)))
           (cons* (car x)
                  (map (lambda (b) (list (walk (car b) env) (walk (cadr b) env))) (cadr x))
                  (walk* (cddr x) env))))
        (else (walk* x env))))
    (clause cl '()))

  (define (inline-call it v arg-cores)
    (let ((type (vector-ref it 0))
          (cl (alpha-rename-clause (vector-ref it 1)))
          (r (gensym))
          (ts (map (lambda (a) (inl-gensym)) arg-cores)))
      (cons `(lambda (,r ,@ts)
               (if ,(clj-call "IsInstanceOf" #f (list 'quote type) r)
                   ((case-lambda ,cl) ,@ts)
                   ,(invoke-core r ts)))
            (cons (var-reference v) arg-cores))))

  (define (var-call e v expand)
    (syntax-case e ()
      ((head arg ...)
       (let* ((args #'(arg ...))
              (n (length args))
              (vm (meta-of v))
              (inline (meta-get vm kw-inline))
              (arities (meta-get vm kw-inline-arities))
              (nt (ctx-nontail (cur))))
         (cond
           ((and inline (clr-is clojure.lang.IFn inline)
                 (or (not arities) (nil->f (op-invoke arities (op-long n)))))
            (when (and (not (eq? (clr-call clojure.lang.Var get_Namespace v) (current-ns)))
                       (not (clr-call clojure.lang.Var get_isPublic v)))
              (err e (string-append "var: " (->string v) " is not public")))
            (let* ((form (syntax->clj e))
                   (out (op-apply-to inline (clr-static-call clojure.lang.RT (next System.Object) form))))
              (expand (clj-datum->syntax* #'head (transfer-source out form)))))
           ((and (= n 2) (string=? (->string v) "#'clojure.core/instance?") (maybe-type (syntax->clj (car args))))
            => (lambda (t)
                 (clj-call "IsInstanceOf" #f (list 'quote t) (expand-in nt (cadr args) expand))))
           ;; 原始签名的函数（cleat-042）：($clj-call '"PrimInvoke" '#f '签名 Var读 实参 …)，编译器把 Var 的根转换成
           ;; clojure.lang.primifs.<签名> 并调用 invokePrim（实参与结果不装箱）；根被换成没有这个签名的函数时 InvalidCast（同 JVM）
           ((and (<= 1 n 4) (var-prim-sig vm n))
            => (lambda (sig)
                 (apply clj-call "PrimInvoke" #f (list 'quote sig) (var-reference v)
                        (map (lambda (a) (expand-in nt a expand)) args))))
           ((and (inline-calls?) (inline-target v n))
            => (lambda (it) (inline-call it v (map (lambda (a) (expand-in nt a expand)) args))))
           (else
            ;; 接口文件里的已知函数（cleat-033，designs/04 §6）：参数个数恰为某固定 arity 时生成受保护直接调用
            ;; （$clj-static-call 类名 参数个数 Var读 实参…）：编译器先取 Var 的当前根，还是这个函数类就直接
            ;; call 类::invokeStatic，被 with-redefs / 重定义换掉时退回普通 IFn.invoke
            (let ((cls (direct-link-target v n)))
              (if cls
                  (cons* '$clj-static-call (list 'quote cls) (list 'quote n) (var-reference v)
                         (map (lambda (a) (expand-in nt a expand)) args))
                  (invoke-core (var-reference v) (map (lambda (a) (expand-in nt a expand)) args))))))))))

  ;;; ———————————————— 宿主语法糖：.method、Type.、Type/member（解析不到的符号的后备）————————————————

  (define identity-sym (clj-symbol "clojure.core/identity"))
  (define dot-sym (clj-symbol "."))
  (define new-sym (clj-symbol "new"))

  (define (type-hinted x type-sym)
    (with-meta* x (clj-map kw-tag type-sym)))

  ;;; Type/.method 的目标：能带元数据（符号、列表）就挂 :tag，否则（字面量）原样——字面量的类型本来就已知
  (define (hint-target x type-sym)
    (if (clr-is clojure.lang.IObj x)
        (with-meta* x (assoc* (meta-of x) kw-tag type-sym))
        x))

  ;;; Type/member：(类型符号 . 成员名) 或 #f
  (define (static-member-of sym)
    (let ((nsname (sym-ns sym)))
      (and nsname
           (not (namespace-for (current-ns) nsname))
           (not (pos-digit? (sym-name sym)))
           (maybe-type (clj-symbol nsname))
           (cons (maybe-type (clj-symbol nsname)) (sym-name sym)))))

  ;;; 宿主糖的改写（Clojure 的 macroexpand-1 对它们的规则）；不是则 #f
  (define (rewrite-to-dot form where)
    (let* ((items (seq->list form))
           (op (car items)))
      (cond
        ((sym-is? op ".") form)
        ((not (clj-symbol? op)) #f)
        (else
         (let ((name (sym-name op)))
           (cond
             ((and (not (sym-ns op)) (> (string-length name) 1) (char=? (string-ref name 0) #\.))
              (when (< (length items) 2) (err where "Malformed member expression, expecting (.member target ...)"))
              (let* ((target (cadr items))
                     (target (if (maybe-type target)
                                 (type-hinted (clj-list identity-sym target) (clj-symbol "System.Type"))
                                 target)))
                (transfer-source (apply clj-list dot-sym target (clj-symbol (substring name 1 (string-length name))) (cddr items)) form)))
             ((and (not (sym-ns op)) (string-last-index name #\.)
                   (> (string-last-index name #\.) 0) (= (string-last-index name #\.) (- (string-length name) 1)))
              (transfer-source (apply clj-list new-sym (clj-symbol (substring name 0 (- (string-length name) 1))) (cdr items)) form))
             ((and (sym-ns op) (static-member-of op))
              => (lambda (sm)
                   (let ((tsym (clj-symbol (sym-ns op))))
                     (transfer-source
                       (cond
                         ((string=? name "new") (apply clj-list new-sym tsym (cdr items)))
                         ((and (> (string-length name) 1) (char=? (string-ref name 0) #\.))
                          (when (< (length items) 2) (err where (string-append "Malformed member expression, expecting (" (->string op) " target ...)")))
                          ;; 提示直接挂在目标上（cleat-053：原来是 (clojure.core/identity 目标)，core.clj 在 identity 定义之前就用 IMeta/.meta）
                          (apply clj-list dot-sym (hint-target (cadr items) (clj-symbol (clr-call System.Type get_FullName (car sm))))
                                 (clj-symbol (substring name 1 (string-length name))) (cddr items)))
                         (else (apply clj-list dot-sym tsym (clj-symbol name) (cdr items))))
                       form))))
             (else #f)))))))


  (define (sugar-transformer stx)
    (syntax-case stx ()
      ((head . _)
       (let* ((form (syntax->clj stx))
              (out (rewrite-to-dot form stx)))
         (clj-datum->syntax* #'head (or out form))))))

  ;;; 每种糖一个标签，值为改写转换器（经 do-macro-call，macroexpand-1 也展开它）
  (define static-member-label #f)
  (define host-sugar-label #f)

  (define (unresolved-symbol ns sym)
    (let ((name (sym-name sym)) (nsname (sym-ns sym)))
      (cond
        ((and nsname (maybe-array-type sym)) => type-label)
        ((and nsname (static-member-of sym)) static-member-label)
        ((and (not nsname) (> (string-length name) 1) (char=? (string-ref name 0) #\.)) host-sugar-label)
        ((and (not nsname) (string-last-index name #\.) (= (string-last-index name #\.) (- (string-length name) 1)))
         host-sugar-label)
        (else #f))))

  (define (register-syntax-kinds!)
    ;; Type/member：引用为静态成员（字段 / 属性 / 无参方法），调用经 macroexpand-1 改写为 (. Type member …)
    (register-binding-extension!
      (make-binding-extension 'clj-static-member
        (lambda (e t expand)
          (let ((sm (static-member-of (syntax->clj e))))
            (clj-call "CallStatic" 3 (list 'quote (car sm)) (list 'quote (munge (cdr sm))) (list 'quote '()))))
        #f #t #t))
    (register-binding-extension!
      (make-binding-extension 'clj-host-sugar
        (lambda (e t expand)
          (err e (string-append "Unable to resolve symbol: " (->string (syntax->clj e)) " in this context")))
        #f #t #t))
    (set! static-member-label (make-label (cons 'clj-static-member sugar-transformer)))
    (set! host-sugar-label (make-label (cons 'clj-host-sugar sugar-transformer)))
    (register-binding-extension!
      (make-binding-extension '$datum
        (lambda (e v expand) (and (clojure-expanding?) (datum-expression e expand)))
        #f #f #f))
    (register-binding-extension!
      (make-binding-extension '$application
        #f #f
        (lambda (e v expand) (and (clojure-form? e) (application e expand)))
        #f)))

  ;;; —— 入口（在 *ns* 里；引用本库的变量才会执行本库体、登记特殊形式）——

  (define (clojure-eval form) (eval-in-ns form))
  (define (clojure-expand form) (expand-in-ns form))
  (define (clojure-macroexpand-1 form) (macroexpand-1-in-ns form))
  (define (clojure-macroexpand form) (macroexpand-in-ns form))

  (register-syntax-kinds!)
  (set-unresolved-lookup! unresolved-symbol)
  (set-var-call-handler! var-call)
  (set-macro-env-provider! clojure-locals-env))
