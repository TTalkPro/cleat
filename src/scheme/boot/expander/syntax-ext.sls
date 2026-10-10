;;; Copyright (c) 2026 Cleat contributors
;;;
;;; Permission is hereby granted, free of charge, to any person obtaining a
;;; copy of this software and associated documentation files (the "Software"),
;;; to deal in the Software without restriction, including without limitation
;;; the rights to use, copy, modify, merge, publish, distribute, sublicense,
;;; and/or sell copies of the Software, and to permit persons to whom the
;;; Software is furnished to do so, subject to the following conditions:
;;;
;;; The above copyright notice and this permission notice shall be included in
;;; all copies or substantial portions of the Software.
;;;
;;; THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
;;; IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
;;; FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.  IN NO EVENT SHALL
;;; THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
;;; LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
;;; FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
;;; DEALINGS IN THE SOFTWARE.

;;; 可扩展语法对象的注册表（cleat-048，designs/15 §2）。展开器 (boot expander core) 导入本库：
;;; 凡按"语法对象里的数据种类"分支的地方都查语法扩展表，凡遇到不认识的绑定种类都查绑定扩展表，
;;; 语法错误经 syntax-error-hook 抛出。经图例库 (ironscheme syntax-ext) 对外导出。
;;;
;;; 语法扩展 (kind pred children rebuild)：
;;;   kind      符号（vector、clj-vector …）
;;;   pred      datum → 布尔：认出裸数据
;;;   children  datum → 子项列表（map 为 k v 交替）
;;;   rebuild   (datum 新子项列表) → datum：保留不参与展开的部分（如元数据）；子项个数可以与原来不同
;;; 序对不在表里：psyntax 的算法依赖 car / cdr 遍历与非正规尾部，仍然特判。
;;; 向量是内建登记的第一项（kind = vector），从第一天起就让通用路径处理真实数据。
;;;
;;; 绑定扩展 (kind expand-reference expand-set! expand-form macroexpand-1?)：
;;;   kind               绑定种类符号（绑定是 (kind . value)）
;;;   expand-reference   (e value expand) → 核心表达式：标识符引用 e
;;;   expand-set!        (e value expand) → 核心表达式：(set! id v)，#f 表示不可赋值
;;;   expand-form        (e value expand) → 核心表达式：以该标识符开头的形式；#f 则按过程调用展开
;;;   macroexpand-1?     macroexpand-1 是否展开它（cleat-049 使用）
;;; expand 是 (lambda (stx) 核心表达式)：在当前环境里展开子表达式。

(library (boot expander syntax-ext)
  (export make-syntax-extension syntax-extension?
          syntax-extension-kind syntax-extension-pred
          syntax-extension-children syntax-extension-rebuild
          register-syntax-extension! syntax-extension-of syntax-extension-by-kind
          syntax-extensions syntax-ext-rebuild
          make-binding-extension binding-extension?
          binding-extension-kind binding-extension-expand-reference
          binding-extension-expand-set! binding-extension-expand-form
          binding-extension-macroexpand-1?
          register-binding-extension! binding-extension-of
          syntax-error-hook)
  (import
    (rnrs)
    (boot expander compat))

  (define-record syntax-extension (kind pred children rebuild))

  (define-record binding-extension
    (kind expand-reference expand-set! expand-form macroexpand-1?))

  (define vector-extension
    (make-syntax-extension 'vector vector? vector->list
      (lambda (v ls) (list->vector ls))))

  ;;; 语法扩展表：按登记顺序查找，第一个 pred 成立的项胜出
  (define extensions (list vector-extension))

  (define (syntax-extensions) extensions)

  ;;; 登记一种数据；同 kind 已登记则替换（位置不变）
  (define (register-syntax-extension! x)
    (unless (syntax-extension? x)
      (assertion-violation 'register-syntax-extension! "not a syntax extension" x))
    (let ((kind (syntax-extension-kind x)))
      (if (syntax-extension-by-kind kind)
          (set! extensions
            (map (lambda (y) (if (eq? (syntax-extension-kind y) kind) x y)) extensions))
          (set! extensions (append extensions (list x))))))

  ;;; datum 所属的语法扩展，或 #f（序对、符号、原子等）
  (define (syntax-extension-of x)
    (let f ((ls extensions))
      (cond
        ((null? ls) #f)
        (((syntax-extension-pred (car ls)) x) (car ls))
        (else (f (cdr ls))))))

  (define (syntax-extension-by-kind kind)
    (let f ((ls extensions))
      (cond
        ((null? ls) #f)
        ((eq? (syntax-extension-kind (car ls)) kind) (car ls))
        (else (f (cdr ls))))))

  ;;; 运行时重建（syntax 模板对非向量种类生成对它的调用）
  (define (syntax-ext-rebuild kind proto children)
    (let ((x (syntax-extension-by-kind kind)))
      (unless x
        (assertion-violation 'syntax-ext-rebuild "unknown syntax extension kind" kind))
      ((syntax-extension-rebuild x) proto children)))

  ;;; 绑定扩展表
  (define binding-extensions '())

  (define (register-binding-extension! x)
    (unless (binding-extension? x)
      (assertion-violation 'register-binding-extension! "not a binding extension" x))
    (let ((kind (binding-extension-kind x)))
      (set! binding-extensions
        (cons x
          (filter (lambda (y) (not (eq? (binding-extension-kind y) kind)))
                  binding-extensions)))))

  (define (binding-extension-of kind)
    (let f ((ls binding-extensions))
      (cond
        ((null? ls) #f)
        ((eq? (binding-extension-kind (car ls)) kind) (car ls))
        (else (f (cdr ls))))))

  ;;; 语法错误钩子：#f 或 (lambda (who msg form condition) …)。syntax-violation / syntax-error
  ;;; 构造好 R6RS 条件后先调用它（它可以改抛别的异常）；它正常返回则照常 raise 条件。
  (define syntax-error-hook (make-parameter #f)))
