;; 编译器的中间语言（cleat-083）：src/scheme/lib/cleat/scheme/compiler/languages.sls 的 L0 ~ L3 逐字移植到 cleat.nanopass。
;; 阶段 A ~ C 与 Scheme 版形状相同（designs/19 §1"先忠实，后清理"），差分门控 G1 按 unparse 比较；Scheme 专用的节点在 cleat-092 按普查裁掉。
;;
;;   L0  前端的输出：固定文法，符号已分为词法变量 x、全局位置 (gref s)、库变量 (mref x)、原语 pr。
;;       let 与 label / jump 由 lower-loops 产生
;;   L1  recognize-let、simplify-letrec 之后：letrec 只绑定 lambda
;;   L2  convert-assignments 之后：被捕获且被赋值的词法变量改为 Box（box / unbox / set-box!）
;;   L3  uncover-free 之后：lambda 带自由变量表
;;
;; 应用一律带关键字 call（不带关键字的产生式会吞掉同形的形式，03 §9）。各节点的含义见 Scheme 版的注释。
(ns cleat.compiler.languages
  (:require [cleat.nanopass :refer [define-language]]))

(defn datum? [x] true)

;; Clojure 的 fn*（cleat-051）：class 是 Clojure 的类名（ns$name），variadic 是变长子句的必需参数个数（没有为 nil），
;; prims 是固定子句的原始签名（cleat-042），async? 为 ^:async fn 的体 thunk（cleat-043）
(defrecord CljFnName [class variadic prims async?])

(defn clj-fn-name? [x] (instance? CljFnName x))

;; 过程名（错误信息与打印用）：符号、字符串、无（nil / false），或 Clojure fn* 的 CljFnName
(defn name? [x] (or (symbol? x) (string? x) (nil? x) (false? x) (clj-fn-name? x)))

(defn flag? [x] (boolean? x))

(define-language L0
  (terminals
    (symbol (x))
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
    le
    (letrec ([x* e*] ...) body)
    (let ([x* e*] ...) body)
    (call e e* ...)
    (throw e)
    (try-finally e0 e1)
    (try-catch e (d* e*) ...)
    (label x (x* ...) body0 body1)
    (jump x e* ...)
    (uninitialized)
    (check-initialized e)
    (clj-var-ref d st)
    (clj-var-set! d e)
    (clj-def d st0 st1 e0 e1)
    (clj-invoke e e* ...)
    (clj-keyword-invoke d e e* ...)
    (clj-call d0 d1 e* ...)
    (clj-static-call d0 d1 e* ...)
    (clj-case d e0 e1 e* ...)
    (clj-deftype d (x* ...) le* ...))
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
    (+ (lambda nm (x* ...) cl* ...))))
