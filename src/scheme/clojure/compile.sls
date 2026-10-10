;;; Clojure 代码保存为 dll（cleat-051，designs/05 §3、designs/04 §9）。按需加载，不进 Scheme boot 镜像。
;;;
;;;   (clojure-compile-forms 形式列表 dll路径) → dll路径
;;;     在 *ns* 里逐个展开、求值（之后的形式要看到前面的 def / 宏 / deftype*，与 Clojure 的 compile 相同），
;;;     收集各形式的 Lcore，用 Scheme 编译器编成一个持久化程序集：每个形式一个 Init<k>，入口 Initialize 依次执行。
;;;     常量编码进 dll（Fasl）：Var 为 (var ns name)，加载时 intern；关键字、符号、集合、long 等用 Clojure 的标签。
;;;     加载：Cleat.Scheme.ModuleLoader.LoadModule（或 Cleat.Core 的 Loader），执行全部顶层形式。
;;;
;;;   Clojure 值的常量编码器（编译器 (ironscheme compiler constants) 的外来常量挂钩）在本库体执行时登记。
;;;   这是 cleat-052 编译驱动（compile / compile-file、.clj.dll 与接口文件）之前的最小版本。

(library (clojure compile)
  (export clojure-compile-forms encode-clojure-constant)
  (import
    (rnrs)
    (ironscheme clr)
    (only (ironscheme compiler) compile-forms->lcil compile-core)
    (only (ironscheme compiler constants) set-foreign-constant-encoder!)
    (clojure syntax-kinds)
    (clojure namespaces)
    (clojure syntax))

  (clr-reference cleat-emit)

  (define seq->list* (clr-static-field-get Cleat.Scheme.Clojure.ClojureOps SeqToList))
  (define map->list* (clr-static-field-get Cleat.Scheme.Clojure.ClojureOps MapToList))
  (define meta* (clr-static-field-get Cleat.Scheme.Clojure.ClojureOps Meta))

  (define (->string x) (clr-call System.Object ToString x))
  (define (named ns name) (if (null? ns) (list name) (list ns name)))

  ;;; x 是 Clojure 的值时返回它的编码（designs/05 §3、LCIL.md §8），否则 #f（交给 Scheme 的编码）。
  ;;; encode 编码子项（经同一个编码器，所以子项里的 long 等也走这里）。
  ;;; 带元数据的对象（符号、集合）写成 (meta 元数据 对象)
  (define (encode-clojure-constant x encode)
    (define (with-meta enc)
      (let ((m (if (clr-is clojure.lang.IMeta x) (meta* x) '())))
        (if (null? m) enc (list 'meta (encode m) enc))))
    (cond
      ((null? x) #f)
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
      ((clr-is clojure.lang.IPersistentMap x) (with-meta (cons 'map (map encode (map->list* x)))))
      ((clr-is clojure.lang.PersistentTreeSet x) (with-meta (cons 'sorted-set (map encode (seq->list* x)))))
      ((clr-is clojure.lang.IPersistentSet x) (with-meta (cons 'set (map encode (seq->list* x)))))
      ((or (clr-is clojure.lang.IPersistentList x) (clr-is clojure.lang.ISeq x))
       (with-meta (cons 'clist (map encode (seq->list* x)))))
      ((clr-is clojure.lang.BigInt x) (list 'bigint (->string x)))
      ((clr-is clojure.lang.BigDecimal x) (list 'bigdec (->string x)))
      ((clr-is clojure.lang.Ratio x) (list 'ratio (->string x)))
      ((clr-is System.Text.RegularExpressions.Regex x) (list 'regex (->string x)))
      ((clr-is System.Type x) (list 'type (clr-prop-get System.Type FullName x)))
      (else #f)))

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

  (set-foreign-constant-encoder! encode-clojure-constant))
