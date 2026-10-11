;;; 编译驱动（cleat-052，designs/04 §9）：Clojure 的 eval / load / compile，登记 Cleat.Core 的运行时钩子。按需加载，不进 Scheme boot 镜像。
;;;
;;;   (clojure-eval-toplevel 形式)      Clojure 的 Compiler.eval：集合形式先 macroexpand（头部是 def… 的除外）；顶层 (do …) 拆开逐个求值
;;;                                    （后面的子形式看得到前面的 def / in-ns / 宏）；其余展开为 Lcore 后，简单形式由 C# 小解释器求值，
;;;                                    否则经镜像的编译器（compile-core）生成内存程序集执行。
;;;   (clojure-load-reader 读者 路径 名字)  load-source 钩子：逐个读形式、求值；*ns* 在外层绑定（文件里的 in-ns / ns 对后续形式生效，
;;;                                    加载结束后恢复）。返回最后一个值。
;;;   (clojure-compile-reader 读者 路径 名字) compile 钩子（*compile-files* 为真时加载器调用）：同 load，同时收集每个顶层形式的 Lcore，
;;;                                    结束后编成一个持久化程序集 a.b.clj.dll（ClojureCLR 的命名）与接口文件 a.b.clj.ifc。
;;;   (install-clojure-hooks!)          登记 eval / load-source / compile / macroexpand-1 / report-error。
;;;
;;; 不导入 (cleat scheme compiler)（它与 nanopass 不是镜像里登记的库，导入就要另外装入一份）：eval 用镜像自己的 compile-core；
;;; AOT 需要的 (clojure compile) 在第一次 compile 时才装入（宿主的延后库，或源码）。
(library (clojure driver)
  (export clojure-eval-toplevel clojure-load-reader clojure-compile-reader install-clojure-hooks!
          compile-core-of-image clojure-compile-boot)
  (import
    (rnrs)
    (rnrs eval)
    (only (cleat scheme) symbol-value parameterize make-parameter)
    (cleat scheme clr)
    (clojure syntax-kinds)
    (clojure namespaces)
    (clojure syntax))

  (define-syntax driver-op
    (syntax-rules () ((_ name) (clr-static-field-get Cleat.Scheme.Clojure.Driver name))))

  (define try-eval-simple (driver-op TryEvalSimple))
  (define not-simple (driver-op NotSimple))
  (define make-reader (driver-op MakeReader))
  (define read-form (driver-op ReadForm))
  (define run-form (driver-op RunForm))
  (define top-level-do-body (driver-op TopLevelDoBody))
  (define macroexpand-top? (driver-op MacroexpandTop))

  ;;; 镜像自己的 compile-core（编译器的代码在镜像里，但不是可导入的库）：经原语位置表取
  (define compile-core-of-image
    (let ((locs ((symbol-value 'current-primitive-locations))))
      (symbol-value (locs 'compile-core))))

  ;;; —— AOT 单元：编译一个文件时收集各顶层形式的 Lcore（按求值顺序）——
  (define current-unit (make-parameter #f))

  ;;; 形式在求值**之后**记入单元（后序）：求值中嵌套加载的文件（load / require）的形式排在这个形式之前——镜像初始化时
  ;;; 先执行被加载文件的形式，再执行 (load …) / (require …) 本身（镜像初始化期间加载是空操作，见 Loader.LoadBootImage；cleat-053）。
  ;;; 没有嵌套加载时与先序相同。
  (define (eval-form form)
    (run-form
      (lambda ()
        (let* ((core (clojure-expand form))
               (unit (current-unit))
               (v (let ((v (try-eval-simple core)))
                    (if (eq? v not-simple)
                        (run-compiled core)
                        v))))
          (when unit (vector-set! unit 0 (cons core (vector-ref unit 0))))
          v))
      form))

  ;;; 构建 Clojure 镜像（cleat-053）：boot-build 为真时嵌套加载的文件也记入同一个单元（平时每个文件各自一个单元或不记）
  (define boot-build (make-parameter #f))

  ;;; 构建镜像时每个顶层形式（按读入顺序编号）开始前把 RT 的 id 计数器置为 编号 × 100000（designs/08 §7）：gensym
  ;;; （x__123__auto__、p__123）与 fn 类名（fn__123）不随构建进程之前消耗过多少 id 变化——镜像 A 上构建 B 与 B 上构建 C 相同
  (define form-ordinal 0)
  (define (reset-ids!)
    (set! form-ordinal (+ form-ordinal 1))
    (clr-static-call clojure.lang.RT setNextID (* form-ordinal 100000)))

  ;;; 编译执行：值经 ClojureOps.HoldResult 存放、TakeResult 取出——compile-core 的 thunk 经 Scheme 互操作返回，
  ;;; 会把 long 规范成 Int32（cleat-055 值边界）。AOT 单元收集的是未包装的 core
  (define take-result (clr-static-field-get Cleat.Scheme.Clojure.ClojureOps TakeResult))
  (define (run-compiled core)
    ((compile-core-of-image (list '$clj-call (list 'quote "HoldResult") (list 'quote #f) core)))
    (take-result #f))

  (define (clojure-eval-toplevel form)
    (let* ((form (if (macroexpand-top? form)
                     (run-form (lambda () (clojure-macroexpand form)) form)
                     form))
           (body (top-level-do-body form)))
      (if body
          (let loop ((xs body) (last #nil))
            (if (null? xs) last (loop (cdr xs) (clojure-eval-toplevel (car xs)))))
          (eval-form form))))

  ;;; eval 钩子（REPL、-e、clojure.core/eval）：不记入正在编译的单元（Clojure 的 eval 也不写类文件）
  (define (eval-hook form)
    (parameterize ((current-unit #f))
      (clojure-eval-toplevel form)))

  (define (clojure-load-reader reader path name)
    (let* ((r (make-reader reader)) (eof (clr-new System.Object))
           (ns0 (current-ns))
           (v (with-ns-binding ns0
                (lambda ()
                  (let loop ((last #nil))
                    (let ((form (read-form r eof)))
                      (if (eq? form eof)
                          last
                          (begin
                            (when (boot-build) (reset-ids!))
                            (loop (clojure-eval-toplevel form))))))))))
      ;; 构建镜像时：load 结束后 *ns* 恢复（绑定弹出），镜像里被加载文件的形式之后也要恢复——记一个 (in-ns '原命名空间)
      (let ((unit (current-unit)))
        (when (and unit (boot-build))
          (vector-set! unit 0 (cons (clojure-expand (restore-ns-form ns0)) (vector-ref unit 0)))))
      v))

  (define (restore-ns-form ns)
    (clj-list (clj-symbol "clojure.core/in-ns")
              (clj-list (clj-symbol "quote") (clr-prop-get clojure.lang.Namespace Name ns))))

  (define (load-hook reader path name)
    (parameterize ((current-unit (and (boot-build) (current-unit))))
      (clojure-load-reader reader path name)))

  ;;; —— compile：延后装入 (clojure compile) ——
  (define compile-unit #f)
  (define (compile-unit-proc)
    (unless compile-unit
      (clr-static-call Cleat.Scheme.Clojure.ClojureHost EnsureCompileLibraries)
      (set! compile-unit (eval 'clojure-compile-unit (environment '(clojure compile)))))
    compile-unit)

  (define (clojure-compile-reader reader path name)
    (let ((compile! (compile-unit-proc))
          (unit (vector '())))
      (let ((v (parameterize ((current-unit unit))
                 (clojure-load-reader reader path name))))
        (compile! (reverse (vector-ref unit 0))
                  (clr-static-call Cleat.Scheme.Clojure.Driver CompileOutputPath path name)
                  (clr-static-call Cleat.Scheme.Clojure.Driver ClojureImageId))
        v)))

  ;;; (clojure-compile-boot 形式列表 dll路径)：在 user 命名空间里依次求值形式（boot-order 的 load / require），全部加载到的文件的
  ;;; 顶层形式按求值顺序（后序）收集成**一个**单元，编译为 Clojure 镜像 dll（同名 .lcil 文本、.ifc 接口文件）。
  ;;; 编译时若已运行着一个 Clojure 镜像（不动点的第二段），不加 CheckBootImage。
  (define compile-boot-unit #f)
  (define (clojure-compile-boot forms dll-path)
    (unless compile-boot-unit
      (compile-unit-proc)
      (set! compile-boot-unit (eval 'clojure-compile-boot-unit (environment '(clojure compile)))))
    (let ((unit (vector '())))
      (set! form-ordinal 0)
      (parameterize ((current-unit unit) (boot-build #t) (inline-calls? #f))
        (for-each (lambda (f) (reset-ids!) (clojure-eval-toplevel f)) forms))
      (compile-boot-unit (reverse (vector-ref unit 0)) dll-path)))

  (define (install! name proc)
    (clr-static-call Cleat.Scheme.Clojure.Driver InstallHook name proc))

  (define (install-clojure-hooks!)
    (clr-static-call clojure.lang.RT Init)
    (install! "eval" eval-hook)
    (install! "load-source" load-hook)
    (install! "compile" clojure-compile-reader)
    (install! "macroexpand-1" clojure-macroexpand-1)
    (install! "compile-boot" clojure-compile-boot)
    (clr-static-call Cleat.Scheme.Clojure.Driver InstallReportError)))
