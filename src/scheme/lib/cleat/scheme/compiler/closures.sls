#| License
Copyright (c) 2026 the IronScheme contributors
All rights reserved.
This source code is subject to terms and conditions of the BSD License.
See docs/license.txt. |#

;;; pass 6 uncover-free：L2 → L3，每个 lambda 记下自由的词法变量（designs/03-compiler.md §4）。
;;; 库变量是模块的静态字段，不算自由变量。自由变量的顺序由遍历顺序决定，结果确定。
(library (cleat scheme compiler closures)
  (export uncover-free)
  (import (rnrs) (nanopass) (cleat scheme compiler languages))

  (define (set-add x s) (if (memq x s) s (append s (list x))))
  (define (set-union a b) (fold-left (lambda (s x) (set-add x s)) a b))
  (define (set-minus s xs) (filter (lambda (x) (not (memq x xs))) s))
  (define (union* ss) (fold-left set-union '() ss))

  (define (uncover-free ir)
    (let ((fvt (make-eq-hashtable)))
      (define (Expr e)
        (nanopass-case (L2 Expr) e
          [,x (list x)]
          [(quote ,d) '()]
          [(if ,e0 ,e1 ,e2) (union* (list (Expr e0) (Expr e1) (Expr e2)))]
          [(begin ,e* ... ,e) (union* (map Expr (append e* (list e))))]
          [(set! ,x ,e) (set-add x (Expr e))]
          [(gref ,s) '()]
          [(gset! ,s ,e) (Expr e)]
          [(mref ,x) '()]
          [(mset! ,x ,e) (Expr e)]
          [(prim ,pr) '()]
          [(primcall ,pr ,e* ...) (union* (map Expr e*))]
          [(lambda ,nm ,cl* ...)
           (let ((fv (union* (map Clause cl*))))
             (hashtable-set! fvt e fv)
             fv)]
          [(label ,x (,x* ...) ,body0 ,body1) (set-minus (set-union (Expr body0) (Expr body1)) (cons x x*))]
          [(jump ,x ,e* ...) (set-add x (union* (map Expr e*)))]
          [(letrec ([,x* ,le*] ...) ,body) (set-minus (union* (cons (Expr body) (map Expr le*))) x*)]
          [(let ([,x* ,e*] ...) ,body) (set-union (union* (map Expr e*)) (set-minus (Expr body) x*))]
          [(library ,d ([,x* ,s* ,e*] ...) ,body) (union* (cons (Expr body) (map Expr e*)))]
          [(call ,e ,e* ...) (union* (map Expr (cons e e*)))]
          [(and ,e* ...) (union* (map Expr e*))]
          [(or ,e* ...) (union* (map Expr e*))]
          [(throw ,e) (Expr e)]
          [(break) '()]
          [(try-finally ,e0 ,e1) (set-union (Expr e0) (Expr e1))]
          [(try-catch ,e (,d* ,e*) ...) (union* (cons (Expr e) (map Expr e*)))]
          [(escape-frame ,x ,body) (set-minus (Expr body) (list x))]
          [(try-handle ,x ,body0 ,body1) (set-union (Expr body0) (set-minus (Expr body1) (list x)))]
          [(host-call ,d0 ,d1 ,st ,e* ...) (union* (map Expr e*))]
          [(host-field-get ,d0 ,d1 ,st ,e* ...) (union* (map Expr e*))]
          [(host-field-set ,d0 ,d1 ,st ,e* ...) (union* (map Expr e*))]
          [(host-new ,d ,e* ...) (union* (map Expr e*))]
          [(host-new-array ,d ,e) (Expr e)]
          [(host-cast ,d ,e) (Expr e)]
          [(host-is ,d ,e) (Expr e)]
          [(host-typeof ,d) '()]
          [(host-using ,d) '()]
          [(host-reference ,d) '()]
          [(host-namespaces) '()]
          [(unsupported ,d ,e* ...) (union* (map Expr e*))]
          [(clj-var-ref ,d ,st) '()]
          [(clj-var-set! ,d ,e) (Expr e)]
          [(clj-def ,d ,st0 ,st1 ,e0 ,e1) (set-union (Expr e0) (Expr e1))]
          [(clj-invoke ,e ,e* ...) (union* (map Expr (cons e e*)))]
          [(clj-keyword-invoke ,d ,e ,e* ...) (union* (map Expr (cons e e*)))]
          [(clj-call ,d0 ,d1 ,e* ...) (union* (map Expr e*))]
          [(clj-static-call ,d0 ,d1 ,e* ...) (union* (map Expr e*))]
          [(clj-case ,d ,e0 ,e1 ,e* ...) (union* (map Expr (cons* e0 e1 e*)))]
          [(clj-deftype ,d (,x* ...) ,le* ...) (set-minus (union* (map Expr le*)) x*)]
          [(box ,e) (Expr e)]
          [(unbox ,x) (list x)]
          [(set-box! ,x ,e) (set-add x (Expr e))]))
      (define (Clause cl)
        (nanopass-case (L2 Clause) cl
          [(clause (,x* ...) ,x ,body) (set-minus (Expr body) (if x (cons x x*) x*))]))
      (Expr ir)
      (attach ir fvt)))

  (define-pass attach : L2 (ir fvt) -> L3 ()
    (LambdaExpr : LambdaExpr (ir) -> LambdaExpr ()
      [(lambda ,nm ,[cl*] ...)
       `(lambda ,nm (,(hashtable-ref fvt ir '()) ...) ,cl* ...)])))
