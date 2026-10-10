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
;;; 不导入 (ironscheme compiler)（它与 nanopass 不是镜像里登记的库，导入就要另外装入一份）：eval 用镜像自己的 compile-core；
;;; AOT 需要的 (clojure compile) 在第一次 compile 时才装入（宿主的延后库，或源码）。
(library (clojure driver)
  (export clojure-eval-toplevel clojure-load-reader clojure-compile-reader install-clojure-hooks!
          compile-core-of-image)
  (import
    (rnrs)
    (rnrs eval)
    (only (ironscheme) symbol-value parameterize make-parameter)
    (ironscheme clr)
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

  (define (eval-form form)
    (run-form
      (lambda ()
        (let ((core (clojure-expand form)))
          (let ((unit (current-unit)))
            (when unit (vector-set! unit 0 (cons core (vector-ref unit 0)))))
          (let ((v (try-eval-simple core)))
            (if (eq? v not-simple)
                ((compile-core-of-image core))
                v))))
      form))

  (define (clojure-eval-toplevel form)
    (let* ((form (if (macroexpand-top? form)
                     (run-form (lambda () (clojure-macroexpand form)) form)
                     form))
           (body (top-level-do-body form)))
      (if body
          (let loop ((xs body) (last '()))
            (if (null? xs) last (loop (cdr xs) (clojure-eval-toplevel (car xs)))))
          (eval-form form))))

  ;;; eval 钩子（REPL、-e、clojure.core/eval）：不记入正在编译的单元（Clojure 的 eval 也不写类文件）
  (define (eval-hook form)
    (parameterize ((current-unit #f))
      (clojure-eval-toplevel form)))

  (define (clojure-load-reader reader path name)
    (let ((r (make-reader reader)) (eof (clr-new System.Object)))
      (with-ns-binding (current-ns)
        (lambda ()
          (let loop ((last '()))
            (let ((form (read-form r eof)))
              (if (eq? form eof)
                  last
                  (loop (clojure-eval-toplevel form)))))))))

  (define (load-hook reader path name)
    (parameterize ((current-unit #f))
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

  (define (install! name proc)
    (clr-static-call Cleat.Scheme.Clojure.Driver InstallHook name proc))

  (define (install-clojure-hooks!)
    (clr-static-call clojure.lang.RT Init)
    (install! "eval" eval-hook)
    (install! "load-source" load-hook)
    (install! "compile" clojure-compile-reader)
    (install! "macroexpand-1" clojure-macroexpand-1)
    (clr-static-call Cleat.Scheme.Clojure.Driver InstallReportError)))
