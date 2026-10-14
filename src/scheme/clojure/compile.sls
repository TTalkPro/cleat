;;; Clojure 代码保存为 dll（cleat-051，designs/05 §3、designs/04 §9）。按需加载，不进 Scheme boot 镜像。
;;;
;;;   (clojure-compile-forms 形式列表 dll路径) → dll路径
;;;     在 *ns* 里逐个展开、求值（之后的形式要看到前面的 def / 宏 / deftype*，与 Clojure 的 compile 相同），
;;;     收集各形式的 Lcore，用 Scheme 编译器编成一个持久化程序集：每个形式一个 Init<k>，入口 Initialize 依次执行。
;;;     常量编码进 dll（Fasl）：Var 为 (var ns name)，加载时 intern；关键字、符号、集合、long 等用 Clojure 的标签。
;;;     加载：Cleat.Scheme.ModuleLoader.LoadModule（或 Cleat.Core 的 Loader），执行全部顶层形式。
;;;
;;;   (clojure-compile-unit Lcore列表 dll路径 镜像id或#f) → dll路径（cleat-052，编译驱动 (clojure driver) 的 compile 钩子调用）
;;;     各形式已经由驱动展开、求值过（顺序即求值顺序，顶层 do 已拆开），这里只编译保存：dll 加上旁边的接口文件（Driver.WriteInterfaceFile）；
;;;     镜像id（编译时运行的 Clojure boot 镜像）不是 #f 时，入口第一个形式是 BootInterface.Check（镜像换了则加载器回退源码）。
;;;
;;;   Clojure 值的常量编码器（编译器 (cleat scheme compiler constants) 的外来常量挂钩）在本库体执行时登记。

(library (clojure compile)
  (export clojure-compile-forms clojure-compile-unit clojure-compile-boot-unit clojure-compile-package-unit encode-clojure-constant)
  (import
    (rnrs)
    (only (cleat scheme) parameterize)
    (cleat scheme clr)
    (only (cleat scheme compiler) compile-forms->lcil compile-core compile->lcil-text-from-sexpr canonical-gensyms)
    (only (cleat scheme compiler constants) set-foreign-constant-encoder!)
    (clojure syntax-kinds)
    (clojure namespaces)
    (clojure syntax))

  (clr-reference cleat-emit)

  (define seq->list* (clr-static-field-get Cleat.Scheme.Clojure.ExpandOps SeqToList))
  (define map->list* (clr-static-field-get Cleat.Scheme.Clojure.ExpandOps MapToList))
  (define meta* (clr-static-field-get Cleat.Scheme.Clojure.ExpandOps Meta))

  (define (->string x) (clr-call System.Object ToString x))
  (define (named ns name) (if (nil? ns) (list name) (list ns name)))

  ;;; x 是 Clojure 的值时返回它的编码（designs/05 §3、LCIL.md §8），否则 #f（交给 Scheme 的编码）。
  ;;; encode 编码子项（经同一个编码器，所以子项里的 long 等也走这里）。
  ;;; 带元数据的对象（符号、集合）写成 (meta 元数据 对象)
  (define (encode-clojure-constant x encode)
    (define (with-meta enc)
      (let ((m (if (clr-is clojure.lang.IMeta x) (meta* x) #nil)))
        (if (nil? m) enc (list 'meta (encode m) enc))))
    (cond
      ((nil? x) #f)
      ((clr-is clojure.lang.Var x)
       (list 'var (->string (clr-prop-get clojure.lang.Namespace Name (clr-prop-get clojure.lang.Var Namespace x)))
             (clr-prop-get clojure.lang.Symbol Name (clr-prop-get clojure.lang.Var Symbol x))))
      ((clr-is System.Int64 x) (list 'long (string->number (->string x))))
      ((clr-is System.Boolean x) (if (clr-call System.Boolean Equals x #t) '(true) '(false)))
      ((clr-is clojure.lang.Keyword x)
       (cons 'kw (named (clr-prop-get clojure.lang.Keyword Namespace x) (clr-prop-get clojure.lang.Keyword Name x))))
      ((clr-is clojure.lang.Symbol x)
       (with-meta (cons 'csym (named (clr-prop-get clojure.lang.Symbol Namespace x) (clr-prop-get clojure.lang.Symbol Name x)))))
      ((clr-is clojure.lang.IPersistentVector x) (with-meta (cons 'cvec (map encode (seq->list* x)))))
      ((clr-is clojure.lang.PersistentTreeMap x) (with-meta (cons 'sorted-map (map encode (map->list* x)))))
      ((clr-is clojure.lang.IPersistentMap x) (with-meta (cons 'map (sorted-pairs (map encode (map->list* x))))))
      ((clr-is clojure.lang.PersistentTreeSet x) (with-meta (cons 'sorted-set (map encode (seq->list* x)))))
      ((clr-is clojure.lang.IPersistentSet x) (with-meta (cons 'set (sorted-items (map encode (seq->list* x))))))
      ((or (clr-is clojure.lang.IPersistentList x) (clr-is clojure.lang.ISeq x))
       (with-meta (cons 'clist (map encode (seq->list* x)))))
      ((clr-is clojure.lang.BigInt x) (list 'bigint (->string x)))
      ((clr-is clojure.lang.BigDecimal x) (list 'bigdec (->string x)))
      ((clr-is clojure.lang.Ratio x) (list 'ratio (->string x)))
      ((clr-is System.Text.RegularExpressions.Regex x) (list 'regex (->string x)))
      ((clr-is System.Type x) (list 'type (clr-prop-get System.Type FullName x)))
      (else #f)))

  ;;; 哈希 map / set 的项按编码的文本排序（cleat-053）：迭代顺序依赖键的哈希，Type、Var 等的哈希按对象标识、每个进程不同；
  ;;; 加载时按值重建，顺序不影响语义，排序使两次编译的 Lcil 相同（镜像不动点）
  (define (encoded-text x) (call-with-string-output-port (lambda (p) (write x p))))
  (define (sorted-items xs)
    (map cdr (list-sort (lambda (a b) (string<? (car a) (car b)))
                        (map (lambda (x) (cons (encoded-text x) x)) xs))))
  (define (sorted-pairs kvs)
    (let loop ((xs kvs) (pairs '()))
      (if (null? xs)
          (apply append (map cdr (list-sort (lambda (a b) (string<? (car a) (car b))) pairs)))
          (loop (cddr xs) (cons (cons (encoded-text (car xs)) (list (car xs) (cadr xs))) pairs)))))

  (define (clojure-compile-forms forms dll-path)
    (let* ((cores (map (lambda (f)
                         (let ((core (clojure-expand f)))
                           ((compile-core core))
                           core))
                       forms))
           (asm (clr-static-call System.IO.Path GetFileNameWithoutExtension dll-path))
           (lcil (compile-forms->lcil cores asm "#Module")))
      (clr-static-call Cleat.Emit.LcilEmitter (SaveData Object String String Boolean) lcil dll-path dll-path #f)
      dll-path))

  (define (clojure-compile-unit cores dll-path image-id)
    (let* ((cores (if image-id
                      (cons (list '$clj-call (list 'quote "CheckBootImage") (list 'quote #f) (list 'quote image-id)) cores)
                      cores))
           (asm (clr-static-call System.IO.Path GetFileNameWithoutExtension dll-path))
           (lcil (compile-forms->lcil cores asm "#Module")))
      ;; 调试：设置环境变量 CLEAT_DUMP_LCIL 时旁边写 Lcil 文本（名字.clj.dll.lcil；镜像总是写）
      (when (string? (clr-static-call System.Environment GetEnvironmentVariable "CLEAT_DUMP_LCIL"))
        (let ((f (string-append dll-path ".lcil")))
          (when (file-exists? f) (delete-file f))
          (call-with-output-file f (lambda (p) (put-string p (compile->lcil-text-from-sexpr lcil))))))
      (when (file-exists? dll-path) (delete-file dll-path))
      (clr-static-call Cleat.Emit.LcilEmitter (SaveData Object String String Boolean) lcil dll-path dll-path #f)
      (clr-static-call Cleat.Scheme.Clojure.Driver (WriteInterfaceFile String Object Object) dll-path cores lcil)
      dll-path))

  ;;; Clojure 镜像（cleat-053）：一个单元编成 dll，旁边写 Lcil 文本（同名 .lcil）与接口文件（.ifc）。Scheme 的 gensym 规范化
  ;;; （与 Scheme 镜像的构建相同），使两次构建的 Lcil 可以逐字比较（不动点）。
  (define (clojure-compile-boot-unit cores dll-path)
    (let* ((asm (clr-static-call System.IO.Path GetFileNameWithoutExtension dll-path))
           (lcil (parameterize ((canonical-gensyms #t))
                   (compile-forms->lcil cores asm "#Module")))
           (text (compile->lcil-text-from-sexpr lcil))
           (lcil-path (clr-static-call System.IO.Path ChangeExtension dll-path ".lcil")))
      (when (file-exists? lcil-path) (delete-file lcil-path))
      (call-with-output-file lcil-path (lambda (p) (put-string p text)))
      (when (file-exists? dll-path) (delete-file dll-path))
      (clr-static-call Cleat.Emit.LcilEmitter (Save String String String Boolean) text lcil-path dll-path #f)
      (clr-static-call Cleat.Scheme.Clojure.Driver (WriteInterfaceFile String Object Object) dll-path cores lcil)
      dll-path))

;;; 包程序集（cleat-067，designs/17 §2）：segments 是 ((加载路径 . Lcore列表) …)，按加载顺序；全部形式编成一个单元（每个形式
  ;;; 一个 Init<k>），之后改写 Lcil：入口 Initialize 只做镜像核对（Init0，有镜像id时）与预链接，每个文件一个入口 Load<i> 依次调它的
  ;;; Init<k>；程序集级特性 CleatPackage(包名)、CleatNamespace(加载路径, "#Module", "Load<i>") 作索引（加载器先跑一次 Initialize）；
  ;;; (lazy-start)：类型初始化器先拉起运行时；接口文件作为嵌入资源 cleat.ifc / cleat.inl。
  (define (clojure-compile-package-unit segments package dll-path image-id)
    (let* ((check (if image-id
                      (list (list '$clj-call (list 'quote "CheckBootImage") (list 'quote #f) (list 'quote image-id)))
                      '()))
           (cores (apply append check (map cdr segments)))
           (asm (clr-static-call System.IO.Path GetFileNameWithoutExtension dll-path))
           (lcil (package-lcil (compile-forms->lcil cores asm "#Module") segments package (length check)))
           ;; 接口文件作为嵌入资源（designs/17 §2.1）
           (ifc (clr-static-call Cleat.Scheme.Clojure.Driver WritePackageInterface dll-path cores lcil))
           (inl (clr-static-call Cleat.Scheme.Clojure.Driver InlineFileOf ifc))
           (lcil (with-resources lcil (cons (list "cleat.ifc" ifc) (if inl (list (list "cleat.inl" inl)) '())))))
      (when (string? (clr-static-call System.Environment GetEnvironmentVariable "CLEAT_DUMP_LCIL"))
        (let ((f (string-append dll-path ".lcil")))
          (when (file-exists? f) (delete-file f))
          (call-with-output-file f (lambda (p) (put-string p (compile->lcil-text-from-sexpr lcil))))))
      (when (file-exists? dll-path) (delete-file dll-path))
      (clr-static-call Cleat.Emit.LcilEmitter (SaveData Object String String Boolean) lcil dll-path dll-path #f)
      (clr-static-call Cleat.Scheme.Clojure.Driver DeletePackageInterface ifc)
      dll-path))

  (define (with-resources lcil resources)
    (map (lambda (item)
           (if (and (pair? item) (eq? (car item) 'assembly))
               (append item (map (lambda (r) (cons 'resource r)) resources))
               item))
         lcil))

  (define obj-type "System.Object")
  (define (init-call k) `(call "#Module" ,(string-append "Init" (number->string k)) () ,obj-type))
  (define (load-name i) (string-append "Load" (number->string i)))

  ;; (method "Load<i>" …)：依次调 Init<start> … Init<start+n-1>，返回最后一个的值（没有形式时 nil）
  (define (load-method i start n)
    `(method ,(load-name i) (attrs static) (params) (ret ,obj-type)
       (body
         ,@(if (= n 0)
               '((ldnull) (ret))
               (let loop ((k start) (left n))
                 (if (= left 1)
                     (list (init-call k) '(ret))
                     (cons* (init-call k) '(pop) (loop (+ k 1) (- left 1)))))))))

  (define (init-call? x)
    (and (pair? x) (eq? (car x) 'call) (pair? (cdr x)) (equal? (cadr x) "#Module")))

  ;; Initialize 的体：保留第一个 Init 调用之前的部分（镜像核对与预链接），再调前 nfixed 个 Init，返回 nil
  (define (rewrite-entry m nfixed)
    (map (lambda (c)
           (if (and (pair? c) (eq? (car c) 'body))
               (let loop ((xs (cdr c)) (acc '()))
                 (if (or (null? xs) (init-call? (car xs)))
                     `(body ,@(reverse acc)
                            ,@(apply append (let f ((k 0)) (if (= k nfixed) '() (cons (list (init-call k) '(pop)) (f (+ k 1))))))
                            (ldnull) (ret))
                     (loop (cdr xs) (cons (car xs) acc))))
               c))
         m))

  (define (package-lcil lcil segments package nfixed)
    (let* ((loads (let loop ((ss segments) (i 0) (start nfixed))
                    (if (null? ss)
                        '()
                        (let ((n (length (cdar ss))))
                          (cons (cons (caar ss) (load-method i start n)) (loop (cdr ss) (+ i 1) (+ start n)))))))
           (attrs (cons* '(lazy-start)                            ; 类型初始化器先拉起运行时（designs/17 §4）
                         `(attribute "Cleat.Core.CleatPackageAttribute" ,package)
                        (map (lambda (l) `(attribute "Cleat.Core.CleatNamespaceAttribute" ,(car l) "#Module" ,(cadr (cdr l))))
                             loads))))
      (map (lambda (item)
             (cond
               ((and (pair? item) (eq? (car item) 'assembly)) (append item attrs))
               ((and (pair? item) (eq? (car item) 'type) (equal? (cadr item) "#Module"))
                (append (map (lambda (x)
                               (if (and (pair? x) (eq? (car x) 'method) (equal? (cadr x) "Initialize"))
                                   (rewrite-entry x nfixed)
                                   x))
                             item)
                        (map cdr loads)))
               (else item)))
           lcil)))

  (set-foreign-constant-encoder! encode-clojure-constant))
