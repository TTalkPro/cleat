#| License
Copyright (c) 2026 the Skeg contributors
All rights reserved.
This source code is subject to terms and conditions of the BSD License.
See docs/license.txt. |#

;;; 原语元数据（iron-069，照 Chez s/primdata.ss 裁剪）：boot 导出的过程按名字登记签名、标志与编译期折叠实现。
;;;
;;; 两张表：
;;;   1. boot 过程表（本文件）：+ fx+ vector-ref car … 在 Skeg 里是 boot 镜像导出的全局位置，不是 C# 原语，
;;;      编译器只能按位置符号的名字认（去掉 gensym 后缀的名字在表里且后缀是 $%b，与 codegen 的 inline-ops 同一规则：
;;;      用户自己定义的 + 带进程戳，不会被误认）。boot 镜像自己编译时位置还没有 $%b 后缀，不折叠。
;;;   2. C# 原语（boot/primitives.ss 的 mifoldable 标志 + 这里按 $ 名字登记的折叠实现）。
;;;
;;; 条目：(名字 签名 标志 折叠实现)
;;;   签名 = ((参数类型 …) (结果类型 …))，参数类型表末尾的 ... 表示前一个类型可重复；类型名与 Primitive.cs 的 Sig 相同
;;;   标志（语义照 Chez cmacros.ss 的 prim-mask 注释）：
;;;     pure            无副作用、不分配可变对象
;;;     mifoldable      参数全为常量时可按机器无关的方式折叠（不蕴含 pure：car 对常量表可折叠）
;;;     partial-folder  变参过程的常量参数可以先合并（(+ 1 2 x) → (+ 3 x)），只对精确数做
;;;     discard         结果可丢弃（effect 上下文可删）；只有同时 unrestricted 才真的删
;;;     unrestricted    任何参数都不报错
;;;     safeongoodargs  参数类型已知正确时不需要检查（供 iron-071 的收窄消费）
;;;   折叠实现：(lambda (值表) → (list 结果) 或 #f)；#f 表示这组参数不折叠（溢出、越界、除零……留给运行时报错）。
;;;   折叠在编译器自己的算术上做，不调用目标镜像里的过程（03 §1：编译器不查运行时）；结果必须是可编码的常量。
;;;
;;;   (primdata-lookup 名字)        → 条目或 #f（名字是符号，boot 过程表）
;;;   (primdata-flag? 条目 标志)
;;;   (primdata-result-type 条目)   → 结果类型符号或 #f（多值、未知时 #f）
;;;   (primdata-arity-ok? 条目 n)   → 按签名判断参数个数
;;;   (primdata-fold 条目 值表)     → (list 结果) 或 #f
;;;   (primcall-fold 原语名 值表)   → C# 原语的折叠：(list 结果) 或 #f（要求 primitives.ss 标 mifoldable）
;;;   (boot-location-name 符号)     → 位置符号是 boot 的（$%b 后缀）时给去掉后缀的名字（符号），否则 #f
;;;   (foldable-constant? x)        → x 能作为折叠结果写进常量表
(library (cleat scheme compiler primdata)
  (export primdata-lookup primdata-name primdata-sig primdata-flags primdata-flag? primdata-result-type primdata-arity-ok?
          primdata-fold primcall-fold boot-location-name foldable-constant?)
  (import
    (rnrs)
    (only (rnrs r5rs) quotient remainder modulo)
    (cleat scheme compiler primitives))

  (define-record-type primdata
    (fields name sig flags folder))

  (define (primdata-flag? p flag) (and (memq flag (primdata-flags p)) #t))

  (define (primdata-result-type p)
    (let ((sig (primdata-sig p)))
      (and sig (pair? (cadr sig)) (null? (cdadr sig)) (caadr sig))))

  (define (primdata-arity-ok? p n)
    (let ((sig (primdata-sig p)))
      (or (not sig)
          (let ((args (car sig)))
            (if (and (pair? args) (memq '... args))
                (>= n (- (length args) 2))
                (= n (length args)))))))

  (define (primdata-fold p vs)
    (let ((f (primdata-folder p)))
      (and f (guard (c (#t #f)) (f vs)))))

  ;; —— 折叠结果的范围：常量表能编码、且语义上不可变的对象 ——
  (define (foldable-constant? x)
    (or (number? x) (boolean? x) (char? x) (symbol? x) (null? x)
        ;; 常量表 / 向量的子项（quote 过的数据本来就不可变）
        (pair? x) (vector? x) (string? x)))

  (define (ok r) (and (foldable-constant? r) (list r)))

  ;; —— 折叠实现的构件 ——
  (define (all? pred vs) (for-all pred vs))
  (define (real-constant? v) (and (number? v) (real? v)))
  (define (exact-integer? v) (and (integer? v) (exact? v)))

  ;; n 元实数运算（+ * - / 与比较）：参数全为实数常量；flonum 的运算按 IEEE 在哪台机器上都一样
  (define (fold-real op least)
    (lambda (vs)
      (and (>= (length vs) least) (all? real-constant? vs) (ok (apply op vs)))))
  ;; 二元 fixnum 运算：参数与结果都是 fixnum（溢出留给运行时报错）
  (define (fold-fx2 op)
    (lambda (vs)
      (and (= (length vs) 2) (all? fixnum? vs)
           (let ((r (op (car vs) (cadr vs)))) (and (fixnum? r) (list r))))))
  (define (fold-fx1 op)
    (lambda (vs)
      (and (= (length vs) 1) (fixnum? (car vs))
           (let ((r (op (car vs)))) (and (fixnum? r) (list r))))))
  (define (fold-fx-pred op)
    (lambda (vs) (and (= (length vs) 1) (fixnum? (car vs)) (list (op (car vs))))))
  (define (fold-fx-cmp op)
    (lambda (vs) (and (>= (length vs) 2) (all? fixnum? vs) (list (apply op vs)))))
  ;; 除法类：除数为 0 不折叠
  (define (fold-fx-div op)
    (lambda (vs)
      (and (= (length vs) 2) (all? fixnum? vs) (not (eqv? (cadr vs) 0))
           (let ((r (op (car vs) (cadr vs)))) (and (fixnum? r) (list r))))))
  (define (fold-fl2 op)
    (lambda (vs) (and (= (length vs) 2) (all? flonum? vs) (list (op (car vs) (cadr vs))))))
  (define (fold-fl1 op)
    (lambda (vs) (and (= (length vs) 1) (flonum? (car vs)) (list (op (car vs))))))
  (define (fold-fl-cmp op)
    (lambda (vs) (and (>= (length vs) 2) (all? flonum? vs) (list (apply op vs)))))
  (define (fold-fl-pred op)
    (lambda (vs) (and (= (length vs) 1) (flonum? (car vs)) (list (op (car vs))))))
  ;; 精确整数的位运算
  (define (fold-bits op least)
    (lambda (vs) (and (>= (length vs) least) (all? exact-integer? vs) (ok (apply op vs)))))
  (define (fold-char1 op)
    (lambda (vs) (and (= (length vs) 1) (char? (car vs)) (list (op (car vs))))))
  (define (fold-char-cmp op)
    (lambda (vs) (and (>= (length vs) 2) (all? char? vs) (list (apply op vs)))))
  ;; 任意参数的谓词
  (define (fold-pred op)
    (lambda (vs) (and (= (length vs) 1) (list (op (car vs))))))
  ;; eq? / eqv? 只对不分配的常量：符号、布尔、空表、fixnum、字符
  (define (eq-constant? v) (or (symbol? v) (boolean? v) (null? v) (fixnum? v) (char? v)))
  (define (fold-eq op)
    (lambda (vs) (and (= (length vs) 2) (all? eq-constant? vs) (list (op (car vs) (cadr vs))))))
  (define (fold-vector-ref vs)
    (and (= (length vs) 2) (vector? (car vs)) (fixnum? (cadr vs))
         (<= 0 (cadr vs)) (< (cadr vs) (vector-length (car vs)))
         (ok (vector-ref (car vs) (cadr vs)))))
  (define (fold-string-ref vs)
    (and (= (length vs) 2) (string? (car vs)) (fixnum? (cadr vs))
         (<= 0 (cadr vs)) (< (cadr vs) (string-length (car vs)))
         (list (string-ref (car vs) (cadr vs)))))
  (define (fold-length vs)
    (and (= (length vs) 1) (list? (car vs)) (list (length (car vs)))))
  (define (fold-pair-op op)
    (lambda (vs) (and (= (length vs) 1) (pair? (car vs)) (ok (op (car vs))))))
  (define (fold-list-op pred op)
    (lambda (vs) (and (= (length vs) 1) (pred (car vs)) (ok (op (car vs))))))
  ;; expt：只折叠精确整数的小幂（结果位数有限；Chez 注：可能太耗时所以不折叠一般情形）
  (define (fold-expt vs)
    (and (= (length vs) 2) (exact-integer? (car vs)) (exact-integer? (cadr vs))
         (<= 0 (cadr vs) 64) (<= (abs (car vs)) 65536)
         (list (expt (car vs) (cadr vs)))))
  (define (fold-integer->char vs)
    (and (= (length vs) 1) (fixnum? (car vs))
         (let ((k (car vs)))
           (and (<= 0 k #x10FFFF) (not (<= #xD800 k #xDFFF)) (list (integer->char k))))))
  (define (fold-div-op op)              ; div / mod 等：实数、除数非零；被除数不能是无穷 / NaN（运行时报错）
    (lambda (vs)
      (and (= (length vs) 2) (all? real-constant? vs) (not (zero? (cadr vs)))
           (not (and (flonum? (car vs)) (or (nan? (car vs)) (infinite? (car vs)))))
           (ok (op (car vs) (cadr vs))))))
  (define (fold-int-div op)             ; quotient / remainder / modulo：整数、除数非零
    (lambda (vs)
      (and (= (length vs) 2) (all? integer? vs) (not (zero? (cadr vs))) (ok (op (car vs) (cadr vs))))))
  (define (fold-real1 pred op)
    (lambda (vs) (and (= (length vs) 1) (pred (car vs)) (ok (op (car vs))))))

  ;; —— 表 ——
  (define ARITH '(pure mifoldable partial-folder discard safeongoodargs))
  (define ARITH2 '(pure mifoldable discard safeongoodargs))
  (define PRED '(pure mifoldable discard unrestricted safeongoodargs))
  (define CMP '(pure mifoldable discard safeongoodargs))
  (define ACCESS '(mifoldable discard safeongoodargs))
  (define ALLOC '(discard unrestricted))

  (define table (make-eq-hashtable))
  (define (def name sig flags folder)
    (hashtable-set! table name (make-primdata name sig flags folder)))


  (define (primdata-lookup name)
    (hashtable-ref table name #f))

  ;; —— C# 原语（primcall）的折叠：primitives.ss 标 mifoldable 的才折叠，实现按 $ 名字登记 ——
  (define primcall-folders (make-eq-hashtable))
  (define (defp name folder) (hashtable-set! primcall-folders name folder))

  (define (primcall-fold pr vs)
    (let ((p (primitive-info pr)))
      (and p
           (let ((o (primitive-overload p (length vs)))
                 (f (hashtable-ref primcall-folders pr #f)))
             (and o f (overload-flag? o 'mifoldable)
                  (guard (c (#t #f)) (f vs)))))))

  ;; —— boot 位置符号 → 名字 ——
  ;; g$名字$序号$%b → 名字（符号）；其他（词法变量、用户库的位置、boot 自己编译时的进程戳后缀）→ #f
  (define (boot-location-name s)
    (let* ((str (symbol->string s)) (n (string-length str)))
      (and (> n 5)
           (char=? (string-ref str 0) #\g) (char=? (string-ref str 1) #\$)
           (string=? (substring str (- n 3) n) "$%b")
           (let loop ((i 2))
             (cond
               ((>= i (- n 3)) #f)
               ((char=? (string-ref str i) #\$) (and (> i 2) (string->symbol (substring str 2 i))))
               (else (loop (+ i 1))))))))

  ;; —— 填表（R6RS 库体里表达式要放在定义之后）——
  ;; (rnrs base) 数值
  (def '+ '((number ...) (number)) ARITH (fold-real + 0))
  (def '* '((number ...) (number)) ARITH (fold-real * 0))
  (def '- '((number number ...) (number)) ARITH2 (fold-real - 1))
  (def '/ '((number number ...) (number)) ARITH2
       (lambda (vs) (and (>= (length vs) 1) (all? real-constant? vs)
                         (not (exists (lambda (v) (and (exact? v) (zero? v))) (if (null? (cdr vs)) vs (cdr vs))))
                         (ok (apply / vs)))))
  (def '= '((number number ...) (boolean)) CMP (fold-real = 2))
  (def '< '((number number ...) (boolean)) CMP (fold-real < 2))
  (def '> '((number number ...) (boolean)) CMP (fold-real > 2))
  (def '<= '((number number ...) (boolean)) CMP (fold-real <= 2))
  (def '>= '((number number ...) (boolean)) CMP (fold-real >= 2))
  (def 'zero? '((number) (boolean)) CMP (fold-real1 real-constant? zero?))
  (def 'positive? '((number) (boolean)) CMP (fold-real1 real-constant? positive?))
  (def 'negative? '((number) (boolean)) CMP (fold-real1 real-constant? negative?))
  (def 'odd? '((number) (boolean)) CMP (fold-real1 integer? odd?))
  (def 'even? '((number) (boolean)) CMP (fold-real1 integer? even?))
  (def 'abs '((number) (number)) ARITH2 (fold-real1 real-constant? abs))
  (def 'max '((number number ...) (number)) ARITH2 (fold-real max 1))
  (def 'min '((number number ...) (number)) ARITH2 (fold-real min 1))
  (def 'quotient '((integer integer) (integer)) ARITH2 (fold-int-div quotient))
  (def 'remainder '((integer integer) (integer)) ARITH2 (fold-int-div remainder))
  (def 'modulo '((integer integer) (integer)) ARITH2 (fold-int-div modulo))
  (def 'div '((number number) (number)) ARITH2 (fold-div-op div))
  (def 'mod '((number number) (number)) ARITH2 (fold-div-op mod))
  (def 'div0 '((number number) (number)) ARITH2 (fold-div-op div0))
  (def 'mod0 '((number number) (number)) ARITH2 (fold-div-op mod0))
  (def 'expt '((number number) (number)) '(pure discard) fold-expt)
  (def 'exact '((number) (number)) ARITH2
       (fold-real1 (lambda (v) (and (real-constant? v) (not (and (flonum? v) (or (nan? v) (infinite? v)))))) exact))
  (def 'inexact '((number) (number)) ARITH2 (fold-real1 real-constant? inexact))
  (def 'exact->inexact '((number) (number)) ARITH2 (fold-real1 real-constant? inexact))
  (def 'inexact->exact '((number) (number)) ARITH2
       (fold-real1 (lambda (v) (and (real-constant? v) (not (and (flonum? v) (or (nan? v) (infinite? v)))))) exact))
  (def 'floor '((number) (number)) ARITH2 (fold-real1 real-constant? floor))
  (def 'ceiling '((number) (number)) ARITH2 (fold-real1 real-constant? ceiling))
  (def 'round '((number) (number)) ARITH2 (fold-real1 real-constant? round))
  (def 'truncate '((number) (number)) ARITH2 (fold-real1 real-constant? truncate))
  (def 'number? '((ptr) (boolean)) PRED (fold-pred number?))
  (def 'integer? '((ptr) (boolean)) PRED (fold-pred integer?))
  (def 'rational? '((ptr) (boolean)) PRED (fold-pred rational?))
  (def 'real? '((ptr) (boolean)) PRED (fold-pred real?))
  (def 'exact? '((number) (boolean)) CMP (fold-real1 number? exact?))
  (def 'inexact? '((number) (boolean)) CMP (fold-real1 number? inexact?))
  ;; (rnrs arithmetic fixnums)：Skeg 的 fx 过程都是二元（R6RS）
  (def 'fixnum? '((ptr) (boolean)) PRED (fold-pred fixnum?))
  (def 'fx+ '((fixnum fixnum) (fixnum)) ARITH2 (fold-fx2 +))
  (def 'fx- '((fixnum fixnum) (fixnum)) ARITH2
       (lambda (vs) (and (<= 1 (length vs) 2) (all? fixnum? vs) (let ((r (apply - vs))) (and (fixnum? r) (list r))))))
  (def 'fx* '((fixnum fixnum) (fixnum)) ARITH2 (fold-fx2 *))
  (def 'fxdiv '((fixnum fixnum) (fixnum)) ARITH2 (fold-fx-div div))
  (def 'fxmod '((fixnum fixnum) (fixnum)) ARITH2 (fold-fx-div mod))
  (def 'fxdiv0 '((fixnum fixnum) (fixnum)) ARITH2 (fold-fx-div div0))
  (def 'fxmod0 '((fixnum fixnum) (fixnum)) ARITH2 (fold-fx-div mod0))
  (def 'fx=? '((fixnum fixnum fixnum ...) (boolean)) CMP (fold-fx-cmp =))
  (def 'fx<? '((fixnum fixnum fixnum ...) (boolean)) CMP (fold-fx-cmp <))
  (def 'fx>? '((fixnum fixnum fixnum ...) (boolean)) CMP (fold-fx-cmp >))
  (def 'fx<=? '((fixnum fixnum fixnum ...) (boolean)) CMP (fold-fx-cmp <=))
  (def 'fx>=? '((fixnum fixnum fixnum ...) (boolean)) CMP (fold-fx-cmp >=))
  (def 'fxzero? '((fixnum) (boolean)) CMP (fold-fx-pred zero?))
  (def 'fxpositive? '((fixnum) (boolean)) CMP (fold-fx-pred positive?))
  (def 'fxnegative? '((fixnum) (boolean)) CMP (fold-fx-pred negative?))
  (def 'fxodd? '((fixnum) (boolean)) CMP (fold-fx-pred odd?))
  (def 'fxeven? '((fixnum) (boolean)) CMP (fold-fx-pred even?))
  (def 'fxmax '((fixnum fixnum ...) (fixnum)) ARITH2 (lambda (vs) (and (>= (length vs) 1) (all? fixnum? vs) (list (apply max vs)))))
  (def 'fxmin '((fixnum fixnum ...) (fixnum)) ARITH2 (lambda (vs) (and (>= (length vs) 1) (all? fixnum? vs) (list (apply min vs)))))
  (def 'fxabs '((fixnum) (fixnum)) ARITH2 (fold-fx1 abs))
  (def 'fxand '((fixnum ...) (fixnum)) ARITH (lambda (vs) (and (all? fixnum? vs) (list (apply bitwise-and vs)))))
  (def 'fxior '((fixnum ...) (fixnum)) ARITH (lambda (vs) (and (all? fixnum? vs) (list (apply bitwise-ior vs)))))
  (def 'fxxor '((fixnum ...) (fixnum)) ARITH (lambda (vs) (and (all? fixnum? vs) (list (apply bitwise-xor vs)))))
  (def 'fxnot '((fixnum) (fixnum)) ARITH2 (fold-fx1 bitwise-not))
  (def 'fxarithmetic-shift '((fixnum fixnum) (fixnum)) ARITH2
       (lambda (vs) (and (= (length vs) 2) (all? fixnum? vs) (< (abs (cadr vs)) 32)
                         (let ((r (bitwise-arithmetic-shift (car vs) (cadr vs)))) (and (fixnum? r) (list r))))))
  (def 'fxarithmetic-shift-left '((fixnum fixnum) (fixnum)) ARITH2
       (lambda (vs) (and (= (length vs) 2) (all? fixnum? vs) (<= 0 (cadr vs) 31)
                         (let ((r (bitwise-arithmetic-shift-left (car vs) (cadr vs)))) (and (fixnum? r) (list r))))))
  (def 'fxarithmetic-shift-right '((fixnum fixnum) (fixnum)) ARITH2
       (lambda (vs) (and (= (length vs) 2) (all? fixnum? vs) (<= 0 (cadr vs) 31)
                         (list (bitwise-arithmetic-shift-right (car vs) (cadr vs))))))
  (def 'fixnum-width '(() (fixnum)) '(pure) #f)        ; 依赖机器，不折叠（Chez 同）
  (def 'least-fixnum '(() (fixnum)) '(pure) #f)
  (def 'greatest-fixnum '(() (fixnum)) '(pure) #f)
  ;; (rnrs arithmetic flonums)
  (def 'flonum? '((ptr) (boolean)) PRED (fold-pred flonum?))
  (def 'fl+ '((flonum ...) (flonum)) ARITH (lambda (vs) (and (all? flonum? vs) (list (apply fl+ vs)))))
  (def 'fl* '((flonum ...) (flonum)) ARITH (lambda (vs) (and (all? flonum? vs) (list (apply fl* vs)))))
  (def 'fl- '((flonum flonum ...) (flonum)) ARITH2 (lambda (vs) (and (>= (length vs) 1) (all? flonum? vs) (list (apply fl- vs)))))
  (def 'fl/ '((flonum flonum ...) (flonum)) ARITH2 (lambda (vs) (and (>= (length vs) 1) (all? flonum? vs) (list (apply fl/ vs)))))
  (def 'fl=? '((flonum flonum flonum ...) (boolean)) CMP (fold-fl-cmp fl=?))
  (def 'fl<? '((flonum flonum flonum ...) (boolean)) CMP (fold-fl-cmp fl<?))
  (def 'fl>? '((flonum flonum flonum ...) (boolean)) CMP (fold-fl-cmp fl>?))
  (def 'fl<=? '((flonum flonum flonum ...) (boolean)) CMP (fold-fl-cmp fl<=?))
  (def 'fl>=? '((flonum flonum flonum ...) (boolean)) CMP (fold-fl-cmp fl>=?))
  (def 'flzero? '((flonum) (boolean)) CMP (fold-fl-pred flzero?))
  (def 'flpositive? '((flonum) (boolean)) CMP (fold-fl-pred flpositive?))
  (def 'flnegative? '((flonum) (boolean)) CMP (fold-fl-pred flnegative?))
  (def 'flnan? '((flonum) (boolean)) CMP (fold-fl-pred flnan?))
  (def 'flinfinite? '((flonum) (boolean)) CMP (fold-fl-pred flinfinite?))
  (def 'flfinite? '((flonum) (boolean)) CMP (fold-fl-pred flfinite?))
  (def 'flinteger? '((flonum) (boolean)) CMP (fold-fl-pred flinteger?))
  (def 'flabs '((flonum) (flonum)) ARITH2 (fold-fl1 flabs))
  (def 'flfloor '((flonum) (flonum)) ARITH2 (fold-fl1 flfloor))
  (def 'flceiling '((flonum) (flonum)) ARITH2 (fold-fl1 flceiling))
  (def 'flround '((flonum) (flonum)) ARITH2 (fold-fl1 flround))
  (def 'fltruncate '((flonum) (flonum)) ARITH2 (fold-fl1 fltruncate))
  (def 'flmax '((flonum flonum ...) (flonum)) ARITH2 (lambda (vs) (and (>= (length vs) 1) (all? flonum? vs) (list (apply flmax vs)))))
  (def 'flmin '((flonum flonum ...) (flonum)) ARITH2 (lambda (vs) (and (>= (length vs) 1) (all? flonum? vs) (list (apply flmin vs)))))
  ;; 超越函数：结果依赖宿主的数学库，只给签名不折叠
  (def 'flsqrt '((flonum) (flonum)) ARITH2 #f)
  (def 'flexp '((flonum) (flonum)) ARITH2 #f)
  (def 'fllog '((flonum flonum ...) (flonum)) ARITH2 #f)
  (def 'flsin '((flonum) (flonum)) ARITH2 #f)
  (def 'flcos '((flonum) (flonum)) ARITH2 #f)
  (def 'fltan '((flonum) (flonum)) ARITH2 #f)
  (def 'flasin '((flonum) (flonum)) ARITH2 #f)
  (def 'flacos '((flonum) (flonum)) ARITH2 #f)
  (def 'flatan '((flonum flonum ...) (flonum)) ARITH2 #f)
  (def 'flexpt '((flonum flonum) (flonum)) ARITH2 #f)
  (def 'fixnum->flonum '((fixnum) (flonum)) ARITH2 (lambda (vs) (and (= (length vs) 1) (fixnum? (car vs)) (list (inexact (car vs))))))
  (def 'real->flonum '((number) (flonum)) ARITH2 (fold-real1 real-constant? inexact))
  ;; (rnrs arithmetic bitwise)
  (def 'bitwise-and '((integer ...) (integer)) ARITH (fold-bits bitwise-and 0))
  (def 'bitwise-ior '((integer ...) (integer)) ARITH (fold-bits bitwise-ior 0))
  (def 'bitwise-xor '((integer ...) (integer)) ARITH (fold-bits bitwise-xor 0))
  (def 'bitwise-not '((integer) (integer)) ARITH2 (fold-bits bitwise-not 1))
  (def 'bitwise-bit-count '((integer) (integer)) ARITH2 (fold-bits bitwise-bit-count 1))
  (def 'bitwise-length '((integer) (integer)) ARITH2 (fold-bits bitwise-length 1))
  (def 'bitwise-arithmetic-shift '((integer integer) (integer)) ARITH2
       (lambda (vs) (and (= (length vs) 2) (all? exact-integer? vs) (< (abs (cadr vs)) 64) (ok (bitwise-arithmetic-shift (car vs) (cadr vs))))))
  (def 'bitwise-arithmetic-shift-left '((integer integer) (integer)) ARITH2
       (lambda (vs) (and (= (length vs) 2) (all? exact-integer? vs) (<= 0 (cadr vs) 63) (ok (bitwise-arithmetic-shift-left (car vs) (cadr vs))))))
  (def 'bitwise-arithmetic-shift-right '((integer integer) (integer)) ARITH2
       (lambda (vs) (and (= (length vs) 2) (all? exact-integer? vs) (<= 0 (cadr vs) 63) (ok (bitwise-arithmetic-shift-right (car vs) (cadr vs))))))
  ;; 表与向量
  (def 'car '((pair) (ptr)) ACCESS (fold-pair-op car))
  (def 'cdr '((pair) (ptr)) ACCESS (fold-pair-op cdr))
  (def 'caar '((pair) (ptr)) ACCESS (fold-list-op (lambda (v) (and (pair? v) (pair? (car v)))) caar))
  (def 'cadr '((pair) (ptr)) ACCESS (fold-list-op (lambda (v) (and (pair? v) (pair? (cdr v)))) cadr))
  (def 'cdar '((pair) (ptr)) ACCESS (fold-list-op (lambda (v) (and (pair? v) (pair? (car v)))) cdar))
  (def 'cddr '((pair) (ptr)) ACCESS (fold-list-op (lambda (v) (and (pair? v) (pair? (cdr v)))) cddr))
  (def 'cons '((ptr ptr) (pair)) ALLOC #f)
  (def 'list '((ptr ...) (list)) ALLOC #f)
  (def 'pair? '((ptr) (boolean)) PRED (fold-pred pair?))
  (def 'null? '((ptr) (boolean)) PRED (fold-pred null?))
  (def 'list? '((ptr) (boolean)) PRED (fold-pred list?))
  (def 'length '((list) (fixnum)) ACCESS fold-length)
  (def 'vector? '((ptr) (boolean)) PRED (fold-pred vector?))
  (def 'vector '((ptr ...) (vector)) ALLOC #f)
  (def 'make-vector '((fixnum ptr ...) (vector)) '(discard) #f)
  (def 'vector-length '((vector) (fixnum)) ACCESS (fold-list-op vector? vector-length))
  (def 'vector-ref '((vector fixnum) (ptr)) ACCESS fold-vector-ref)
  (def 'vector-set! '((vector fixnum ptr) (void)) '(safeongoodargs) #f)
  (def 'bytevector? '((ptr) (boolean)) PRED (fold-pred bytevector?))
  (def 'bytevector-length '((bytevector) (fixnum)) ACCESS (fold-list-op bytevector? bytevector-length))
  (def 'bytevector-u8-ref '((bytevector fixnum) (fixnum)) ACCESS
       (lambda (vs) (and (= (length vs) 2) (bytevector? (car vs)) (fixnum? (cadr vs))
                         (<= 0 (cadr vs)) (< (cadr vs) (bytevector-length (car vs)))
                         (list (bytevector-u8-ref (car vs) (cadr vs))))))
  ;; 字符串、字符、符号
  (def 'string? '((ptr) (boolean)) PRED (fold-pred string?))
  (def 'string-length '((string) (fixnum)) ACCESS (fold-list-op string? string-length))
  (def 'string-ref '((string fixnum) (char)) ACCESS fold-string-ref)
  (def 'string->symbol '((string) (symbol)) '(pure mifoldable discard) (fold-list-op string? string->symbol))
  (def 'symbol? '((ptr) (boolean)) PRED (fold-pred symbol?))
  (def 'char? '((ptr) (boolean)) PRED (fold-pred char?))
  (def 'char->integer '((char) (fixnum)) ARITH2 (fold-char1 char->integer))
  (def 'integer->char '((fixnum) (char)) ARITH2 fold-integer->char)
  (def 'char-upcase '((char) (char)) ARITH2 (fold-char1 char-upcase))
  (def 'char-downcase '((char) (char)) ARITH2 (fold-char1 char-downcase))
  (def 'char-foldcase '((char) (char)) ARITH2 (fold-char1 char-foldcase))
  (def 'char-alphabetic? '((char) (boolean)) CMP (fold-char1 char-alphabetic?))
  (def 'char-numeric? '((char) (boolean)) CMP (fold-char1 char-numeric?))
  (def 'char-whitespace? '((char) (boolean)) CMP (fold-char1 char-whitespace?))
  (def 'char-upper-case? '((char) (boolean)) CMP (fold-char1 char-upper-case?))
  (def 'char-lower-case? '((char) (boolean)) CMP (fold-char1 char-lower-case?))
  (def 'char=? '((char char char ...) (boolean)) CMP (fold-char-cmp char=?))
  (def 'char<? '((char char char ...) (boolean)) CMP (fold-char-cmp char<?))
  (def 'char>? '((char char char ...) (boolean)) CMP (fold-char-cmp char>?))
  (def 'char<=? '((char char char ...) (boolean)) CMP (fold-char-cmp char<=?))
  (def 'char>=? '((char char char ...) (boolean)) CMP (fold-char-cmp char>=?))
  ;; 其余谓词与相等
  (def 'boolean? '((ptr) (boolean)) PRED (fold-pred boolean?))
  (def 'procedure? '((ptr) (boolean)) PRED (fold-pred procedure?))
  (def 'not '((ptr) (boolean)) PRED (fold-pred not))
  (def 'eq? '((ptr ptr) (boolean)) PRED (fold-eq eq?))
  (def 'eqv? '((ptr ptr) (boolean)) PRED (fold-eq eqv?))
  (def 'equal? '((ptr ptr) (boolean)) '(discard unrestricted) #f)   ; 常量上也可比，但 equal? 遇环不终止，不折叠
  (defp '$fx+ (fold-fx2 +)) (defp '$fx* (fold-fx2 *))
  (defp '$fx- (lambda (vs) (and (<= 1 (length vs) 2) (all? fixnum? vs) (let ((r (apply - vs))) (and (fixnum? r) (list r))))))
  (defp '$fx=? (fold-fx-cmp =)) (defp '$fx<? (fold-fx-cmp <)) (defp '$fx<=? (fold-fx-cmp <=))
  (defp '$fx>? (fold-fx-cmp >)) (defp '$fx>=? (fold-fx-cmp >=))
  (defp '$fxand (fold-fx2 bitwise-and)) (defp '$fxior (fold-fx2 bitwise-ior)) (defp '$fxxor (fold-fx2 bitwise-xor))
  (defp '$fxnot (fold-fx1 bitwise-not))
  (defp '$fxzero? (fold-fx-pred zero?)) (defp '$fxnegative? (fold-fx-pred negative?)) (defp '$fxpositive? (fold-fx-pred positive?))
  (defp '$fxeven? (fold-fx-pred even?)) (defp '$fxodd? (fold-fx-pred odd?))
  (defp '$fl+ (fold-fl2 fl+)) (defp '$fl* (fold-fl2 fl*)) (defp '$fl/ (fold-fl2 fl/))
  (defp '$fl- (lambda (vs) (and (<= 1 (length vs) 2) (all? flonum? vs) (list (apply fl- vs)))))
  (defp '$fl=? (fold-fl-cmp fl=?)) (defp '$fl<? (fold-fl-cmp fl<?)) (defp '$fl<=? (fold-fl-cmp fl<=?))
  (defp '$fl>? (fold-fl-cmp fl>?)) (defp '$fl>=? (fold-fl-cmp fl>=?))
  (defp '$car (fold-pair-op car)) (defp '$cdr (fold-pair-op cdr))
  (defp 'car (fold-pair-op car)) (defp 'cdr (fold-pair-op cdr))
  (defp '$vector-ref fold-vector-ref) (defp '$vector-length (fold-list-op vector? vector-length))
  (defp '$string-length (fold-list-op string? string-length)) (defp '$string-ref fold-string-ref)
  (defp '$char->integer (fold-char1 char->integer)) (defp '$integer->char fold-integer->char)
  (defp '$char-upcase (fold-char1 char-upcase)) (defp '$char-downcase (fold-char1 char-downcase))
  (defp '$char-foldcase (fold-char1 char-foldcase))
  (defp '$fixnum->flonum (lambda (vs) (and (= (length vs) 1) (fixnum? (car vs)) (list (inexact (car vs))))))
  (defp 'not (fold-pred not)) (defp 'null? (fold-pred null?)) (defp 'pair? (fold-pred pair?))
  (defp 'eq? (fold-eq eq?)) (defp 'eqv? (fold-eq eqv?))
  (defp '$fixnum? (fold-pred fixnum?)) (defp '$flonum? (fold-pred flonum?)) (defp '$char? (fold-pred char?))
  (defp '$symbol? (fold-pred symbol?)) (defp '$vector? (fold-pred vector?)) (defp '$boolean? (fold-pred boolean?))
  (defp '$procedure? (fold-pred procedure?))
)
