#| License
Copyright (c) 2026 the IronScheme contributors
All rights reserved.
This source code is subject to terms and conditions of the BSD License.
See docs/license.txt. |#

;;; 原语表（designs/03-compiler.md §6）：编译器关于原语的全部知识都来自 boot/primitives.ss，
;;; 不查运行时（03 §1）。
;;;
;;;   (primitive-table-file [路径])     原语表文件，默认 "boot/primitives.ss"（相对当前目录）
;;;   (primitive-info 名字)             → primitive 记录或 #f
;;;   (primitive-overload 信息 实参个数) → 匹配的重载或 #f
(library (ironscheme compiler primitives)
  (export
    primitive-table-file
    primitive-info
    primitive?
    primitive-name primitive-overloads primitive-special
    primitive-overload
    overload-type overload-method overload-fixed overload-rest? overload-flags overload-inline
    overload-pure? overload-allow-tail-call?)
  (import (rnrs) (only (ironscheme) make-parameter))

  (define-record-type primitive
    (fields name overloads special))

  (define-record-type overload
    (fields type method fixed rest? flags inline))

  (define (overload-pure? o) (and (memq 'pure (overload-flags o)) #t))
  (define (overload-allow-tail-call? o) (and (memq 'allow-tail-call (overload-flags o)) #t))

  (define table #f)
  (define table-file-set? #f)

  ;; 原语表的内容在展开本库时读入并作为常量烘进编译器（boot 镜像中的编译器因此不依赖当前目录）；
  ;; 展开时找不到文件（不在 src/scheme 目录下展开）则为 #f，运行时再读文件。
  (define-syntax baked-primitive-table
    (lambda (x)
      (syntax-case x ()
        ((k)
         (datum->syntax #'k
           (list 'quote
                 (and (file-exists? "boot/primitives.ss")
                      (call-with-input-file "boot/primitives.ss" read))))))))

  (define baked (baked-primitive-table))

  ;; 设置后改为从该文件读取（转换函数在创建参数时也会被调用一次，那次不算）
  (define converter-calls 0)
  (define primitive-table-file
    (make-parameter "boot/primitives.ss"
      (lambda (f)
        (set! converter-calls (+ converter-calls 1))
        (when (> converter-calls 1)
          (set! table #f)
          (set! table-file-set? #t))
        f)))

  (define (load-table)
    (let ((form (if (and baked (not table-file-set?))
                    baked
                    (call-with-input-file (primitive-table-file) read)))
          (ht (make-eq-hashtable)))
      (unless (and (pair? form) (eq? (car form) 'primitive-table) (eqv? (cadr form) 1))
        (assertion-violation 'primitive-info "bad primitive table" (primitive-table-file)))
      (for-each
        (lambda (entry)
          (case (car entry)
            ((primitive)
             (let ((name (cadr entry)))
               (hashtable-set! ht name
                 (make-primitive name
                   (map (lambda (o)
                          ;; (overload "类型" "方法" 固定参数个数 剩余参数? (标志 …) 内联模板)
                          (apply make-overload (cdr o)))
                        (cddr entry))
                   (let ((old (hashtable-ref ht name #f))) (and old (primitive-special old)))))))
            ((special)
             (let* ((name (cadr entry))
                    (old (hashtable-ref ht name #f)))
               (hashtable-set! ht name
                 (make-primitive name (if old (primitive-overloads old) '()) (caddr entry)))))
            (else (assertion-violation 'primitive-info "bad primitive table entry" entry))))
        (cddr form))
      ht))

  (define (primitive-info name)
    (unless table (set! table (load-table)))
    (hashtable-ref table name #f))

  ;; 第一个参数个数相符的重载：固定参数个数相等，或有剩余参数且实参不少于固定参数
  (define (primitive-overload p argc)
    (let loop ((os (primitive-overloads p)))
      (cond
        ((null? os) #f)
        ((let ((o (car os)))
           (if (overload-rest? o) (>= argc (overload-fixed o)) (= argc (overload-fixed o))))
         (car os))
        (else (loop (cdr os)))))))
