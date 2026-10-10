#| License
Copyright (c) 2026 the IronScheme contributors
All rights reserved.
This source code is subject to terms and conditions of the BSD License.
See docs/license.txt. |#

;;; 编译器的中间语言（designs/03-compiler.md §4）。
;;;
;;;   L0  parse-core 的输出：固定文法，符号已分为词法变量 x、全局位置 (gref s)、库变量 (mref x)、原语 pr。
;;;       let 与 label / jump 由 lower-loops 产生（(ironscheme compiler loops)，iron-034b），parse-core 不产生
;;;   L1  recognize-let、simplify-letrec 之后：letrec 只绑定 lambda
;;;   L2  convert-assignments 之后：被捕获且被赋值的词法变量改为 Box（box / unbox / set-box!）
;;;   L3  uncover-free 之后：lambda 带自由变量表
;;;
;;; 应用一律带关键字 call（nanopass 中不带关键字的应用产生式会吞掉同形的形式，03 §9）。
;;; Clojure 节点（cleat-051，clj- 开头）：Var 的读 / 写 / def、IFn 调用、关键字调用、ClojureOps 静态方法调用、deftype* / reify*；
;;; fn* 是名字位置放 clj-fn-name 记录的 lambda（前中端与普通 lambda 相同，codegen 生成 AFunction / RestFn 的子类）。
;;; 库变量（library-letrec* 绑定的变量）编译为模块类型的静态字段，不是词法变量，不会被闭包捕获。
(library (ironscheme compiler languages)
  (export
    L0 unparse-L0
    L1 unparse-L1
    L2 unparse-L2
    L3 unparse-L3
    datum? name? flag?
    make-clj-fn-name clj-fn-name? clj-fn-name-class clj-fn-name-variadic)
  (import (rnrs) (nanopass))

  (define (datum? x) #t)
  ;; Clojure 的 fn*（cleat-051）：class 是 Clojure 的类名（ns$name），variadic 是变长子句（总是最后一个子句，
  ;; 形参为"必需参数 + 剩余 seq"）的必需参数个数，没有变长子句为 #f
  (define-record-type clj-fn-name (fields class variadic))
  ;; 过程名（错误信息与打印用）：符号、字符串或 #f；Clojure fn* 为 clj-fn-name 记录
  (define (name? x) (or (symbol? x) (string? x) (not x) (clj-fn-name? x)))
  (define (flag? x) (boolean? x))

  (define-language L0
    (terminals
      (symbol (x s pr))
      (datum (d))
      (name (nm))
      (flag (st)))
    (entry Expr)
    (Expr (e body)
      x
      (quote d)
      (if e0 e1 e2)
      (begin e* ... e)
      (set! x e)
      (gref s)
      (gset! s e)
      (mref x)
      (mset! x e)
      (prim pr)
      (primcall pr e* ...)
      le
      (letrec ([x* e*] ...) body)
      (let ([x* e*] ...) body)
      (library d ([x* s* e*] ...) body)
      (call e e* ...)
      (and e* ...)
      (or e* ...)
      (throw e)
      (break)
      (try-finally e0 e1)
      ;; ($try/catch body '类型 处理器 …)（designs/15 §3）：d 是 CLR 类型名字符串或 #f（SchemeException，处理器收到其条件）；
      ;; 处理器 e* 是求值得到单参数过程的表达式，先于 body 求值
      (try-catch e (d* e*) ...)
      ;; 局部循环（iron-034b，由 lower-loops 产生）：label 绑定 x 与形参 x*，jump 给形参赋值后跳回
      (label x (x* ...) body0 body1)
      (jump x e* ...)
      ;; 互操作（03 §7）：d 为类型说明（字符串或 #f）、成员说明（字符串或 ("名字" ("P" …))）
      (host-call d0 d1 st e* ...)       ; st 为真表示静态；否则 e* 的第一个是实例
      (host-field-get d0 d1 st e* ...)
      (host-field-set d0 d1 st e* ...)
      (host-new d e* ...)
      (host-new-array d e)
      (host-cast d e)
      (host-is d e)
      (host-typeof d)
      (host-using d)
      (host-reference d)
      (host-namespaces)
      (unsupported d e* ...)
      ;; —— Clojure 节点（cleat-051）——
      (clj-var-ref d st)                 ; d 为 Var；st 为真（^:dynamic）时 Var.get，否则 getRawRoot
      (clj-var-set! d e)                 ; Var.set，值为新值
      (clj-def d st0 st1 e0 e1)          ; st0：有初值（bindRoot e0）；st1：^:dynamic（setDynamic）；setMeta e1；值为 Var
      (clj-invoke e e* ...)              ; IFn.invoke(k)
      (clj-keyword-invoke d e e* ...)    ; d 为关键字；ILookup.valAt 快速路径，否则 RT.get；e* 为可选的缺省值
      (clj-call d0 d1 e* ...)            ; ClojureOps 的静态方法 d0；d1 为 #f（全部是 object 参数）或固定参数个数 n（其余打包成 object[]）
      (clj-static-call d0 d1 e* ...)     ; 受保护的直接链接（cleat-033，designs/04 §6）：d0 为接口文件登记的已知函数类（"类, 程序集"），d1 为参数个数；e0 取 Var 当前根，还是这个类就直接调 invokeStatic，否则普通 invoke
      (clj-case d e0 e1 e* ...)          ; case*：d 为 (shift mask 种类 ((哈希 测试值 跳过检查?) …))，e0 为被测值，e1 为 default，e* 与条目对齐
      (clj-deftype d (x* ...) le* ...))  ; d 为类型描述；x* 为字段（方法体里可读、可变字段可 set!），le* 为方法（this 参数 …）
    (LambdaExpr (le)
      (lambda nm cl* ...))
    (Clause (cl)
      (clause (x* ...) (maybe x) body)))

  (define-language L1 (extends L0)
    (Expr (e body)
      (- (letrec ([x* e*] ...) body))
      (+ (letrec ([x* le*] ...) body))))

  (define-language L2 (extends L1)
    (Expr (e body)
      (+ (box e)
         (unbox x)
         (set-box! x e))))

  (define-language L3 (extends L2)
    (LambdaExpr (le)
      (- (lambda nm cl* ...))
      (+ (lambda nm (x* ...) cl* ...)))))
