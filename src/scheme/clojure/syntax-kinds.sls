;;; Clojure 的数据种类登记为语法扩展（cleat-048，designs/15 §2）。
;;; 按需加载，不进 Scheme boot 镜像：导入本库即登记（库体执行时调用 register-syntax-extension!）。
;;;
;;; 种类（kind）与子项（children）：
;;;   clj-vector  clojure.lang.IPersistentVector   元素
;;;   clj-map     clojure.lang.IPersistentMap      k v k v …（seq 顺序）
;;;   clj-set     clojure.lang.IPersistentSet      元素（seq 顺序）
;;;   clj-list    clojure.lang.IPersistentList / ISeq   元素
;;; rebuild 用原数据的元数据（IMeta.meta → IObj.withMeta）；子项个数可以变（模板里的省略号）。
;;; map 与 set 的重建不检查重复（同 assoc / conj 语义：后者覆盖），因为剥离 wrap 后不同的语法对象可能变成相等的数据。
;;;
;;; clojure-annotate：把元数据带 :line / :column（可选 :end-line / :end-column / :file）的 Clojure 集合
;;; 转成 psyntax 的注解（make-annotation 表达式 源位置 剥离后的数据），使语法错误带源位置；
;;; clojure-datum->syntax = datum->syntax 加上这一步。

(library (clojure syntax-kinds)
  (export clojure-syntax-kinds clojure-annotate clojure-datum->syntax clojure-source-of
          clj-vector clj-map clj-set clj-list clj-keyword seq->list)
  (import
    (rnrs)
    (only (ironscheme reader) make-annotation)
    (ironscheme syntax-ext)
    (ironscheme clr))

  ;;; ISeq → Scheme 列表
  (define (seq->list s)
    (let loop ((s (clr-static-call clojure.lang.RT (seq System.Object) s)) (acc '()))
      (if (null? s)
          (reverse acc)
          (loop (clr-call clojure.lang.ISeq next s)
                (cons (clr-call clojure.lang.ISeq first s) acc)))))

  (define (meta-of x)
    (if (clr-is clojure.lang.IMeta x)
        (clr-call clojure.lang.IMeta meta x)
        '()))

  (define (with-meta-of new old)
    (let ((m (meta-of old)))
      (if (and (not (null? m)) (clr-is clojure.lang.IObj new))
          (clr-call clojure.lang.IObj withMeta new m)
          new)))

  ;;; 构造（也供测试用）
  (define (clj-vector . xs)
    (clr-static-call clojure.lang.PersistentVector (create vector) (list->vector xs)))
  (define (clj-map . kvs)
    (let ((a (list->vector kvs)))
      (if (<= (vector-length a) 16)
          (clr-static-call clojure.lang.PersistentArrayMap (createAsIfByAssoc vector) a)
          (clr-static-call clojure.lang.PersistentHashMap (create vector) a))))
  (define (clj-set . xs)
    (clr-static-call clojure.lang.PersistentHashSet (create vector) (list->vector xs)))
  (define (clj-list . xs)
    (clr-static-call clojure.lang.PersistentList (create System.Collections.IList) (list->vector xs)))
  (define (clj-keyword name)
    (clr-static-call clojure.lang.Keyword (intern System.String) name))

  (define (map-children m)
    (apply append
      (map (lambda (e)
             (list (clr-call clojure.lang.IMapEntry key e)
                   (clr-call clojure.lang.IMapEntry val e)))
           (seq->list m))))

  (define clj-vector-kind
    (make-syntax-extension 'clj-vector
      (lambda (x) (clr-is clojure.lang.IPersistentVector x))
      seq->list
      (lambda (x ls) (with-meta-of (apply clj-vector ls) x))))

  (define clj-map-kind
    (make-syntax-extension 'clj-map
      (lambda (x) (clr-is clojure.lang.IPersistentMap x))
      map-children
      (lambda (x ls) (with-meta-of (apply clj-map ls) x))))

  (define clj-set-kind
    (make-syntax-extension 'clj-set
      (lambda (x) (clr-is clojure.lang.IPersistentSet x))
      seq->list
      (lambda (x ls) (with-meta-of (apply clj-set ls) x))))

  (define clj-list-kind
    (make-syntax-extension 'clj-list
      (lambda (x) (or (clr-is clojure.lang.IPersistentList x) (clr-is clojure.lang.ISeq x)))
      seq->list
      (lambda (x ls) (with-meta-of (apply clj-list ls) x))))

  (define clojure-syntax-kinds
    (list clj-vector-kind clj-map-kind clj-set-kind clj-list-kind))

  (define (clojure-kind? e) (memq e clojure-syntax-kinds))

  ;;; 源位置：(文件 . "(行,列) - (结束行,结束列)")，与 reader 的注解同一格式；没有 :line 时 #f
  (define kw-line (clj-keyword "line"))
  (define kw-column (clj-keyword "column"))
  (define kw-end-line (clj-keyword "end-line"))
  (define kw-end-column (clj-keyword "end-column"))
  (define kw-file (clj-keyword "file"))

  (define (meta-ref m k)
    (let ((v (clr-static-call clojure.lang.RT (get System.Object System.Object) m k)))
      (if (null? v) #f v)))

  (define (->string v) (clr-call System.Object ToString v))

  (define (clojure-source-of x)
    (let ((m (meta-of x)))
      (and (not (null? m))
           (let ((line (meta-ref m kw-line)))
             (and line
                  (let* ((col (or (meta-ref m kw-column) 1))
                         (eline (or (meta-ref m kw-end-line) line))
                         (ecol (or (meta-ref m kw-end-column) col))
                         (file (meta-ref m kw-file)))
                    (cons (if file (->string file) "")
                          (string-append "(" (->string line) "," (->string col) ") - ("
                                         (->string eline) "," (->string ecol) ")"))))))))

  (define (clojure-annotate x)
    (let ((e (syntax-extension-of x)))
      (if (and e (clojure-kind? e))
          (let* ((kids ((syntax-extension-children e) x))
                 (akids (map clojure-annotate kids))
                 (expr (if (for-all eq? kids akids) x ((syntax-extension-rebuild e) x akids)))
                 (src (clojure-source-of x)))
            (if src (make-annotation expr src x) expr))
          x)))

  (define (clojure-datum->syntax id x)
    (datum->syntax id (clojure-annotate x)))

  (for-each register-syntax-extension! clojure-syntax-kinds))
