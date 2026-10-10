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
;;; 库变量（library-letrec* 绑定的变量）编译为模块类型的静态字段，不是词法变量，不会被闭包捕获。
(library (ironscheme compiler languages)
  (export
    L0 unparse-L0
    L1 unparse-L1
    L2 unparse-L2
    L3 unparse-L3
    datum? name? flag?)
  (import (rnrs) (nanopass))

  (define (datum? x) #t)
  ;; 过程名（错误信息与打印用）：符号、字符串或 #f
  (define (name? x) (or (symbol? x) (string? x) (not x)))
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
      (unsupported d e* ...))
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
