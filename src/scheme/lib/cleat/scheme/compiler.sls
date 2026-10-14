#| License
Copyright (c) 2026 the IronScheme contributors
All rights reserved.
This source code is subject to terms and conditions of the BSD License.
See docs/license.txt. |#

;;; 编译驱动（designs/03-compiler.md §8，iron-027）。
;;;
;;;   (compile-forms->lcil forms 程序集名 模块类型名) → Lcil 的 S 表达式
;;;   (compile->lcil-text forms 程序集名 模块类型名)  → Lcil 文本
;;;   (compile-core e)            eval：发射到内存，返回执行它的无参过程
;;;   (compile-library-fasl 文件 e) e 求值得到 13 元素的库描述列表；生成库 fasl（程序集），入口返回该列表
;;;   (compile-library-fasl/asm 文件 e 确定性?) 同上；确定性? 为真时程序集名取文件名主干
;;;   (compile-bootfile forms [文件]) 核心形式列表 → boot 镜像 dll（入口 #Module::Initialize）
;;;
;;; 编译器只读 Lcore 与原语表，不读运行时状态（03 §1）。发射器（Cleat.Emit，程序集 cleat-emit）在同一进程中以库 API 调用。
(library (cleat scheme compiler)
  (export
    compile-forms->lcil compile->lcil-text compile->lcil-text-from-sexpr
    compile-core compile-library-fasl compile-library-fasl/asm compile-bootfile
    front-end
    primitive-table-file
    canonical-gensyms
    unit-interface
    current-compile-target)
  (import
    (rnrs)
    (only (cleat scheme) make-parameter parameterize gensym)
    (cleat scheme clr)
    (cleat scheme compiler primitives)
    (cleat scheme compiler parse)
    (cleat scheme compiler loops)
    (cleat scheme compiler languages)
    (cleat scheme compiler front)
    (cleat scheme compiler inline)
    (cleat scheme compiler closures)
    (cleat scheme compiler codegen)
    (cleat scheme compiler targets)
    (cleat scheme compiler lcil)
    (only (cleat scheme unsafe) $set-compile-target! $try/catch $throw))

  (clr-reference cleat-emit)

  ;; 编译目标（designs/11-portability.md §2 D6，iron-057）：符号，现在只有 clr；未知目标报错。
  ;; 设置时同步给运行时（target-host-type 原语，展开期的 cond-expand 用它）
  (define current-compile-target
    (make-parameter 'clr
      (lambda (t)
        (let ((name (target-name (target-of t))))
          ($set-compile-target! name)
          name))))

  ;; 调试：设置环境变量 CLEAT_SCHEME_DUMP_PASSES=前缀 时，把各阶段的中间语言写到 <前缀>.l0 / .la / .l1 / .l2 / .l3
  ;; （eval 的单元另写 <前缀>.lcil）
  ;; （.la 是 lower-loops 的输出；IronScheme 里是 lower-async 的）
  (define (dump-pass tag unparse x)
    (let ((prefix (clr-static-call System.Environment GetEnvironmentVariable "CLEAT_SCHEME_DUMP_PASSES")))
      (when (string? prefix)
        (let ((f (string-append prefix "." tag)))
          (when (file-exists? f) (delete-file f))
          (call-with-output-file f (lambda (p) (let ((y (unparse x))) (if (string? y) (put-string p y) (write y p)))))))))

;; 逐形式转储（cleat-085，designs/19 D5）：环境变量 CLEAT_DUMP_IR=目录 时，每个形式的各阶段 IR 以规范文本写到
  ;; 目录/scheme/<单元>.ir（Cleat.Scheme.Clojure.IrDump），Clojure 编译器的影子模式写 目录/clojure/，tools/compiler-diff.clj 比较
  (define dump-ir? (clr-static-call Cleat.Scheme.Clojure.IrDump get_Enabled))
  (define current-dump-unit (make-parameter "unit"))
  (define last-dump-index -1)     ; 最近一个形式的序号：单个形式的单元（eval）的 lcil 记录用它
  (define (dump-ir index stage unparse x)
    (when dump-ir?
      (clr-static-call Cleat.Scheme.Clojure.IrDump Write "scheme" (current-dump-unit) index stage
                       (clr-static-call Cleat.Scheme.Clojure.IrDump PrintSchemeIr (unparse x)))))

  ;; Lcore → L3；返回 (values L3 analysis)
  (define (front-end x)
    (let* ((ix (if dump-ir? (clr-static-call Cleat.Scheme.Clojure.IrDump TakeIndex "scheme" (current-dump-unit)) 0))
           (_ (set! last-dump-index ix))
           (l0 (parse-core x))
           (_ (dump-pass "l0" unparse-L0 l0))
           (_ (dump-ir ix "p0" unparse-L0 l0))
           (l0 (let ((l l0))           ; lower-loops：局部循环降级（iron-034b；CLEAT_SCHEME_NO_034 时关，测量用）；lower-async 不纳入（cleat-046）
                 (let ((v (clr-static-call System.Environment GetEnvironmentVariable "CLEAT_SCHEME_NO_034")))
                   (if (and (string? v) (or (string=? v "1") (string=? v "loops"))) l (lower-loops l)))))
           (_ (dump-pass "la" unparse-L0 l0))
           (_ (dump-ir ix "l0" unparse-L0 l0))
           (an (analyze l0))
           (l1 (simplify-letrec l0 an))
           (_ (dump-ir ix "sl" unparse-L1 l1))
           (l1 (check-forward l1 an))
           (_ (dump-ir ix "cf" unparse-L1 l1))
           (l1 (cp0-lite l1))
           (_ (dump-ir ix "c1" unparse-L1 l1))
           ;; 过程内联与 let 传播（iron-070；CLEAT_NO_070=1 时关），内联暴露的常量再折叠一次
           (l1 (if inline-enabled? (cp0-lite (simplify-lets (cp0-inline l1 an) an)) l1))
           (_ (dump-ir ix "in" unparse-L1 l1))
           ;; if 化简（iron-073；CLEAT_NO_073 为 1 或含 if 时关）
           (l1 (simplify-if l1))
           (_ (dump-pass "l1" unparse-L1 l1))
           (_ (dump-ir ix "l1" unparse-L1 l1))
           (l2 (convert-assignments l1 an))
           (_ (dump-pass "l2" unparse-L2 l2))
           (_ (dump-ir ix "l2" unparse-L2 l2))
           (l3 (uncover-free l2)))
      (dump-pass "l3" unparse-L3 l3)
      (dump-ir ix "l3" unparse-L3 l3)
      (values l3 an)))

  (define (compile-forms->lcil forms assembly-name module-name)
    (let-values (((lcil consts) (compile-forms->lcil* forms assembly-name module-name #f)))
      lcil))

  ;; 返回 (values Lcil 常量列表)；live? 见 generate-unit
  (define (compile-forms->lcil* forms assembly-name module-name live?)
    (reset-fresh-names!)        ; lower-loops 的临时变量名在单元内唯一、跨编译确定
    (parameterize ((current-dump-unit assembly-name))
      (compile-forms->lcil** forms assembly-name module-name live?)))

  (define (compile-forms->lcil** forms assembly-name module-name live?)
    (let ((massigned (make-eq-hashtable)))
      ;; 按顺序逐个形式走前端，同时记下哪些形式含循环（loop-forms，只对它们做类型推断）
      (let loop ((fs forms) (l3s '()) (loops '()))
        (if (pair? fs)
            (let ((n0 (+ (loops-made) (typed-clauses-made))))
              (let-values (((l3 an) (front-end (car fs))))
                (let-values (((ks vs) (hashtable-entries (analysis-massigned an))))
                  (vector-for-each (lambda (k) (hashtable-set! massigned k #t)) ks))
                (loop (cdr fs) (cons l3 l3s) (cons (> (+ (loops-made) (typed-clauses-made)) n0) loops))))
            (parameterize ((current-target (target-of (current-compile-target)))
                           (loop-forms (reverse loops)))
              (let-values (((lcil consts) (generate-unit (reverse l3s) assembly-name module-name massigned live?)))
                (when dump-ir?
                  (clr-static-call Cleat.Scheme.Clojure.IrDump Write "scheme" assembly-name (if (= (length forms) 1) last-dump-index -1) "lcil" (lcil->string lcil)))
                (values lcil consts)))))))

  (define (compile->lcil-text forms assembly-name module-name)
    (lcil->string (compile-forms->lcil forms assembly-name module-name)))

  (define (compile->lcil-text-from-sexpr lcil) (lcil->string lcil))

  (define eval-counter 0)

  ;; 调试：设置环境变量 CLEAT_SCHEME_DUMP_FAILED_CORE=文件 时，编译失败的输入写到该文件
  (define (dump-failed-core e)
    (let ((f (clr-static-call System.Environment GetEnvironmentVariable "CLEAT_SCHEME_DUMP_FAILED_CORE")))
      (when (string? f)
        (when (file-exists? f) (delete-file f))
        ;; 用 fasl 写出：输入中可能有语法对象等常量
        (let ((p (open-file-output-port f)))
          (clr-static-call Cleat.Scheme.Fasl SerializePort e p)
          (close-port p)))))

  (define (compile-core e)
    ($try/catch (compile-core* e)
      '"System.Exception" (lambda (ex) (dump-failed-core e) ($throw ex))))

  (define (compile-core* e)
    (set! eval-counter (+ eval-counter 1))
    (let*-values (((name) (string-append "eval$" (number->string eval-counter)))
                  ((lcil consts) (compile-forms->lcil* (list e) name "#Module" #t))
                  ((_) (dump-pass "lcil" compile->lcil-text-from-sexpr lcil))
                  ;; 直接交出 Lcil 的 S 表达式，不经文本（iron-033）；CLEAT_SCHEME_DUMP_PASSES 的 .lcil 仍可用 compile->lcil-text 得到
                  ((asm) (clr-static-call Cleat.Emit.LcilEmitter (EmitInMemoryData Object String Object[])
                                          lcil name (list->vector consts))))
      (lambda ()
        (clr-static-call Cleat.Scheme.ModuleLoader Run asm))))

  (define (assembly-name-of filename)
    (let* ((f (clr-static-call System.IO.Path GetFileNameWithoutExtension filename)))
      f))

  ;; .fasl（iron-034e；cleat-060 照 skeg 统一扩展名）：编译产物都是程序集。程序集名：确定性? 为真时（库路径上按库名命名的
  ;; 预编译库，compile-fasl）或扩展名为 .dll 时用文件名主干——文件名已唯一，跨库引用与 ILVerify 按文件名解析；否则追加进程内
  ;; 唯一的后缀，别的单元按名字直接引用其中的类时不会与同名文件（另一目录的 util.fasl）的产物混淆。
  ;; 旁边写出接口文件 名字.ifc（运行时加载产物时登记，codegen 的 library-known 读）
  (define (compile-library-fasl filename e)
    (compile-library-fasl/asm filename e #f))

  (define (compile-library-fasl/asm filename e deterministic?)
    ;; Lcil 的 S 表达式直接交给发射器，不经文本（iron-033）
    (let* ((asm (if (or deterministic?
                        (string-ci=? (clr-static-call System.IO.Path GetExtension filename) ".dll"))
                    (assembly-name-of filename)
                    (string-append (assembly-name-of filename) "$" (assembly-safe (symbol->string (gensym))))))
           (lcil (compile-forms->lcil (list e) asm "#Module"))
           (entries (unit-interface)))
      (clr-static-call Cleat.Emit.LcilEmitter (SaveData Object String String Boolean) lcil filename filename #f)
      (write-interface-file filename asm entries)
      #t))

  ;; 程序集名只用字母、数字与 _ $ - .（gensym 的进程后缀里可能有 = + 等）
  (define (assembly-safe s)
    (list->string
      (map (lambda (c) (if (or (char-alphabetic? c) (char-numeric? c) (memv c '(#\_ #\$ #\- #\.))) c #\_))
           (string->list s))))

  (define (write-interface-file filename asm entries)
    (let ((ifc (clr-static-call System.IO.Path ChangeExtension filename ".ifc")))
      (when (file-exists? ifc) (delete-file ifc))
      (unless (null? entries)
        (call-with-output-file ifc
          (lambda (p)
            (put-string p "(cleat-scheme-interface 1 ")
            (write asm p)
            (for-each (lambda (x) (newline p) (write x p)) entries)
            (put-string p ")\n"))))))

  (define compile-bootfile
    (case-lambda
      ((forms) (compile-bootfile forms "cleat.scheme.boot.dll"))
      ((forms filename)
       (let ((text (compile->lcil-text forms (assembly-name-of filename) "#Module")))
         (clr-static-call Cleat.Emit.LcilEmitter (Save String String String Boolean) text filename filename #f)
         #t)))))
