#| License
Copyright (c) 2026 the IronScheme contributors
All rights reserved.
This source code is subject to terms and conditions of the BSD License.
See docs/license.txt. |#

;;; 目标机器描述表（designs/11-portability.md §2 D6，iron-057）：codegen 需要的宿主运行时信息都从这里查，
;;; 不再写死在 codegen 里。现在只有一个表项 clr；JVM 等目标加表项（并为它写发射器）即可。
;;;
;;;   (target-of 'clr)             → target 记录；未知目标报错
;;;   (target-tail-calls? t)       → 是否发 tail. 前缀（CLR 为真）
;;;   (target-type t 'object)      → 运行时类型名（Lcil 里的类型引用字符串）
;;;   (target-core-assembly t)     → 运行时程序集名（Lcil 的 references）
(library (cleat scheme compiler targets)
  (export target-of target? target-name target-tail-calls? target-clojure-tail-calls? target-type target-core-assembly target-interop)
  (import (rnrs))

  (define-record-type target
    (fields name            ; 符号
            tail-calls?     ; 尾调用前缀可用
            clojure-tail-calls? ; Clojure 节点（IFn 调用）是否发尾调用前缀（cleat-051：Clojure 没有尾调用语义，CLR 上不发）
            core-assembly   ; 运行时程序集
            types           ; 符号 → 类型名字符串的 alist
            interops))      ; host-* 节点 → 该目标发射器的互操作指令名（04 §2.5）的 alist

  (define (target-interop t key)
    (cond
      ((assq key (target-interops t)) => cdr)
      (else (assertion-violation 'target-interop "unknown interop key" (target-name t) key))))

  (define (target-type t key)
    (cond
      ((assq key (target-types t)) => cdr)
      (else (assertion-violation 'target-type "unknown type key" (target-name t) key))))

  (define clr
    (make-target 'clr #t #f "Cleat.Scheme"
      '((object . "System.Object")
        (object-array . "System.Object[]")
        (int32 . "System.Int32")
        (int64 . "System.Int64")
        (double . "System.Double")
        (boolean . "System.Boolean")
        (char . "System.Char")
        (string . "System.String")
        (void . "System.Void")
        (exception . "System.Exception")
        (scheme-exception . "Cleat.Scheme.SchemeException")   ; raise 抛出的异常，携带条件（$try/catch 的类型 #f）
        (continuation-exception . "Cleat.Scheme.ContinuationException")   ; call-with-return 的逃逸（cleat-058），CLR catch 一律放行
        (procedure . "Cleat.Scheme.Procedure")
        (variadic-procedure . "Cleat.Scheme.VariadicProcedure")
        (cons . "Cleat.Scheme.Cons")
        (box . "Cleat.Scheme.Box")
        (symbol . "Cleat.Scheme.Symbol")
        (rt . "Cleat.Scheme.RT")
        (calls . "Cleat.Scheme.Calls")
        (inline-arith . "Cleat.Scheme.InlineArith")
        (boot-interface . "Cleat.Scheme.BootInterface")
        (unspecified . "Cleat.Scheme.Unspecified")
        ;; Clojure 运行时（cleat-051 的 Clojure 节点）
        (ifn . "clojure.lang.IFn")
        (clj-var . "clojure.lang.Var")
        (keyword . "clojure.lang.Keyword")
        (ilookup . "clojure.lang.ILookup")
        (clj-util . "clojure.lang.Util")
        (clj-rt . "clojure.lang.RT")
        (afunction . "clojure.lang.AFunction")
        (restfn . "clojure.lang.RestFn")
        (persistent-map . "clojure.lang.IPersistentMap")
        (iobj . "clojure.lang.IObj")
        (clojure-ops . "Cleat.Scheme.Clojure.ClojureOps")
        (type . "System.Type")
        (type-handle . "System.RuntimeTypeHandle"))
      '((call . clr-call) (field-get . clr-field-get) (field-set . clr-field-set) (new . clr-new)
        (new-array . clr-new-array) (cast . clr-cast) (is . clr-is) (typeof . clr-type) (namespaces . clr-namespaces))))

  (define targets (list clr))

  (define (target-of name)
    (cond
      ((target? name) name)
      ((and (symbol? name) (find (lambda (t) (eq? (target-name t) name)) targets)))
      (else (assertion-violation 'compile "unknown compilation target (known: clr)" name)))))
