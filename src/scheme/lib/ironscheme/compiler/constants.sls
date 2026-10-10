#| License
Copyright (c) 2026 the IronScheme contributors
All rights reserved.
This source code is subject to terms and conditions of the BSD License.
See docs/license.txt. |#

;;; 常量的中立编码（designs/04-lcil-and-emitter.md §3，iron-020）。
;;; 编译器把 quote 的常量写进 Lcil 常量表；发射器把编码转成 fasl，运行时读回同样的对象。
;;; 只看常量对象本身，不读运行时的全局状态（03 §1）。
;;;
;;;   (encode-constants obj*) → 编码的列表；共享与环用 (def n x) / (ref n) 表示，编号在整张表内统一
;;;   (encode-constant obj)   → 单个常量的编码
;;;   (write-lcil-datum x port) → 按 Lcil 词法（04 §2.1）写出编码等 S 表达式
(library (ironscheme compiler constants)
  (export encode-constant encode-constants write-lcil-datum)
  (import
    (rnrs)
    (rnrs mutable-strings)
    (only (ironscheme) unspecified?)
    (only (ironscheme unsafe)
          $compound-condition? $record? $record-rtd $record-fields $rtd? $rcd? $rcd-rtd $rcd-parent $rcd-protocol
          $annotation? $annotation-expression $annotation-source $annotation-stripped
          $string-mutable? $eq-hashtable? $string->immutable $plain-string?))

  ;; 常量是编译器自己宿主里的对象，检查它们经 $ 原语（iron-057；原来是 clr-is / clr-field-get）
  ;; 复合条件也是 Record 的子类，但没有记录类型；单独编码为 (cnd 简单条件 …)
  (define (compound-condition? x) ($compound-condition? x))
  (define (record-object? x) ($record? x))
  (define (record-object-rtd x) ($record-rtd x))
  (define (record-object-fields x) ($record-fields x))
  (define (rtd? x) ($rtd? x))
  ;; 记录构造描述符：只支持没有 protocol 的（nanopass 生成的代码把展开时创建的 RCD 作为常量）
  (define (rcd? x) ($rcd? x))
  (define (rcd-type x) ($rcd-rtd x))
  (define (rcd-parent x) ($rcd-parent x))
  (define (rcd-protocol x) ($rcd-protocol x))
  (define (annotation? x) ($annotation? x))
  ;; 可变串：SchemeString 的可变串（iron-055），或旧运行时的 StringBuilder（用旧种子构建 boot 时）
  (define (mutable-string? x) ($string-mutable? x))
  ;; 只支持 eq 哈希表（make-eq-hashtable 得到的类型恰为 Hashtable）；展开器的 rib 用它做索引
  (define (eq-hashtable? x) ($eq-hashtable? x))

  ;; 有 eq? 身份、可能被共享的对象
  (define (shareable? x)
    (or (pair? x) (vector? x) (bytevector? x) (mutable-string? x)
        (rtd? x) (rcd? x) (record-object? x) (compound-condition? x) (annotation? x) (eq-hashtable? x)))

  ;; 哈希表条目：键为符号、字符串、定长整数时按其文本排序，使编码确定（表的枚举顺序取决于对象哈希值）
  (define (sorted-entries ht)
    (let-values (((ks vs) (hashtable-entries ht)))
      (let ((es (map cons (vector->list ks) (vector->list vs))))
        (define (sort-key k)
          (cond
            ((symbol? k) (string-append "s" (symbol->string k)))
            ((string? k) (string-append "t" k))
            ((fixnum? k) (string-append "i" (number->string k)))
            (else #f)))
        (if (for-all (lambda (e) (sort-key (car e))) es)
            (list-sort (lambda (a b) (string<? (sort-key (car a)) (sort-key (car b)))) es)
            es))))

  (define (rtd-parts rtd)
    (list (record-type-uid rtd) (record-type-parent rtd)))

  ;; 第一遍：统计可共享对象出现的次数。已见过的不再深入，所以环也能结束；沿表尾循环，长表不递归。
  (define (count-objects! counts x)
    (let loop ((x x))
      (when (shareable? x)
        (let ((n (hashtable-ref counts x 0)))
          (hashtable-set! counts x (+ n 1))
          (when (= n 0)
            (cond
              ((pair? x) (loop (car x)) (loop (cdr x)))
              ((vector? x) (vector-for-each loop x))
              ((rtd? x) (for-each loop (rtd-parts x)))
              ((record-object? x)
               (loop (record-object-rtd x))
               (vector-for-each loop (record-object-fields x)))
              ((annotation? x)
               (loop (annotation-expression* x))
               (loop (annotation-source* x))
               (loop (annotation-stripped* x)))
              ((compound-condition? x) (for-each loop (simple-conditions x)))
              ((rcd? x) (loop (rcd-type x)) (loop (rcd-parent x)))
              ((eq-hashtable? x)
               (for-each (lambda (e) (loop (car e)) (loop (cdr e))) (sorted-entries x)))))))))

  (define (annotation-expression* x) ($annotation-expression x))
  (define (annotation-source* x) ($annotation-source x))
  (define (annotation-stripped* x) ($annotation-stripped x))

  (define hex-digits "0123456789ABCDEF")

  (define (bytes->hex bv)
    (let* ((n (bytevector-length bv))
           (s (make-string (* 2 n))))
      (do ((i 0 (+ i 1))) ((= i n) s)
        (let ((b (bytevector-u8-ref bv i)))
          (string-set! s (* 2 i) (string-ref hex-digits (div b 16)))
          (string-set! s (+ (* 2 i) 1) (string-ref hex-digits (mod b 16)))))))

  ;; flonum → IEEE 位模式（大端十六进制 16 位），保证往返精确，含 -0.0 与 NaN
  (define (flonum->hex x)
    (let ((bv (make-bytevector 8)))
      (bytevector-ieee-double-set! bv 0 x (endianness big))
      (bytes->hex bv)))

  (define (encode-number x)
    (cond
      ((and (exact? x) (integer? x))
       (if (fixnum? x) `(fix ,x) `(big ,(number->string x))))
      ((and (exact? x) (rational? x)) `(rat ,(number->string x)))
      ((flonum? x) `(flo ,(flonum->hex x)))
      ((not (real? x))
       `(cpx ,(encode-number (real-part x)) ,(encode-number (imag-part x))))
      (else (assertion-violation 'encode-constant "cannot encode number" x))))

  (define (encode-rtd-fields rtd)
    (let ((names (record-type-field-names rtd)))
      (let loop ((i (- (vector-length names) 1)) (acc '()))
        (if (< i 0)
            acc
            (loop (- i 1)
                  (cons (list (if (record-field-mutable? rtd i) 'mutable 'immutable)
                              (symbol->string (vector-ref names i)))
                        acc))))))

  (define (make-encoder counts)
    (let ((ids (make-eq-hashtable))
          (next 0))
      (define (shared? x) (> (hashtable-ref counts x 0) 1))
      (define (encode x)
        (cond
          ((and (shareable? x) (hashtable-ref ids x #f)) => (lambda (id) `(ref ,id)))
          ((and (shareable? x) (shared? x))
           ;; 先登记编号再编码子项：环指回自己时得到 (ref n)
           (let ((id next))
             (set! next (+ next 1))
             (hashtable-set! ids x id)
             `(def ,id ,(encode-object x))))
          (else (encode-object x))))
      (define (encode-object x)
        (cond
          ((null? x) '(nil))
          ((eq? x #t) '(true))
          ((eq? x #f) '(false))
          ((unspecified? x) '(unspecified))
          ((eof-object? x) '(eof))
          ((number? x) (encode-number x))
          ((char? x) `(chr ,(char->integer x)))
          ((mutable-string? x) `(mstr ,($string->immutable x)))
          ((string? x) `(str ,x))
          ((symbol? x) `(sym ,(symbol->string x)))
          ((pair? x) (encode-list x))
          ((vector? x) `(vec ,@(map encode (vector->list x))))
          ((bytevector? x) `(bv ,(bytes->hex x)))
          ((rtd? x)
           `(rtd ,(symbol->string (record-type-name x))
                 ,(let ((uid (record-type-uid x)))
                    (cond
                      ((symbol? uid) `(sym ,(symbol->string uid)))
                      ((string? uid) `(str ,uid))
                      (else '(false))))
                 ,(let ((p (record-type-parent x))) (if p (encode p) '(false)))
                 ,(record-type-sealed? x)
                 ,(record-type-opaque? x)
                 ,(encode-rtd-fields x)))
          ((record-object? x)
           `(rec ,(encode (record-object-rtd x))
                 ,@(map encode (vector->list (record-object-fields x)))))
          ((annotation? x)
           `(ann ,(encode (annotation-expression* x))
                 ,(encode (annotation-source* x))
                 ,(encode (annotation-stripped* x))))
          ((compound-condition? x) `(cnd ,@(map encode (simple-conditions x))))
          ((rcd? x)
           (when (procedure? (rcd-protocol x))
             (assertion-violation 'encode-constant "cannot encode a record constructor descriptor with a protocol" x))
           `(rcd ,(encode (rcd-type x)) ,(let ((p (rcd-parent x))) (if (rcd? p) (encode p) '(false)))))
          ((eq-hashtable? x)
           `(eqht ,@(apply append (map (lambda (e) (list (encode (car e)) (encode (cdr e)))) (sorted-entries x)))))
          (else (assertion-violation 'encode-constant "cannot encode object" x))))
      ;; 表尾中未被共享的序对连成一段：(list e …) 或 (dlist e … 表尾)
      (define (encode-list x)
        (let loop ((p (cdr x)) (acc (list (encode (car x)))))
          (cond
            ((null? p) `(list ,@(reverse acc)))
            ((and (pair? p) (not (shared? p)))
             (loop (cdr p) (cons (encode (car p)) acc)))
            (else `(dlist ,@(reverse acc) ,(encode p))))))
      encode))

  (define (encode-constants objs)
    (let ((counts (make-eq-hashtable)))
      (for-each (lambda (x) (count-objects! counts x)) objs)
      (map (make-encoder counts) objs)))

  (define (encode-constant x)
    (car (encode-constants (list x))))

  ;; —— 按 Lcil 词法写出 ——

  ;; 字符串可能按 UTF-16 码元给出代理项：成对的合成一个码点（Lcil 的 \x 转义不接受代理项）
  (define (write-lcil-string s port)
    (define (hex n)
      (put-string port "\\x")
      (put-string port (number->string n 16))
      (put-char port #\;))
    (put-char port #\")
    (if (plain-string? s)
        (put-string port s)
        (escaped-string s port hex))
    (put-char port #\"))

  ;; 只含可打印 ASCII、且没有 " 与 \ 的字符串整段写出（绝大多数情况）
  (define (plain-string? s)
    ($plain-string? s))

  (define (escaped-string s port hex)
    (let ((n (string-length s)))
      (let loop ((i 0))
        (when (< i n)
          (let* ((c (string-ref s i))
                 (k (char->integer c)))
            (cond
              ((char=? c #\") (put-string port "\\\"") (loop (+ i 1)))
              ((char=? c #\\) (put-string port "\\\\") (loop (+ i 1)))
              ((char<=? #\space c #\~) (put-char port c) (loop (+ i 1)))
              ((and (<= #xD800 k #xDBFF) (< (+ i 1) n) (<= #xDC00 (char->integer (string-ref s (+ i 1))) #xDFFF))
               (hex (+ #x10000 (* (- k #xD800) #x400) (- (char->integer (string-ref s (+ i 1))) #xDC00)))
               (loop (+ i 2)))
              ((<= #xD800 k #xDFFF) (assertion-violation 'write-lcil-datum "unpaired surrogate in string" s))
              (else (hex k) (loop (+ i 1)))))))))

  (define (write-lcil-datum x port)
    (cond
      ((pair? x)
       (put-char port #\()
       (let loop ((x x) (first? #t))
         (cond
           ((null? x) (put-char port #\)))
           ((pair? x)
            (unless first? (put-char port #\space))
            (write-lcil-datum (car x) port)
            (loop (cdr x) #f))
           (else (assertion-violation 'write-lcil-datum "improper list" x)))))
      ((null? x) (put-string port "()"))
      ((string? x) (write-lcil-string x port))
      ((symbol? x) (put-string port (symbol->string x)))
      ((eq? x #t) (put-string port "#t"))
      ((eq? x #f) (put-string port "#f"))
      ((and (exact? x) (integer? x)) (put-string port (number->string x)))
      ((flonum? x) (put-string port (number->string x)))
      (else (assertion-violation 'write-lcil-datum "cannot write as Lcil" x)))))
