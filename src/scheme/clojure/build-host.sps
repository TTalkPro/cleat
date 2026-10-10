;;; 预编译宿主用的 Scheme 库（cleat-052）：在 Scheme 镜像上运行（Cleat.Host.csproj 的 GenerateClojureHostLibraries 目标）：
;;;   stage0 -I lib -I nanopass --boot cleat.scheme.boot.dll clojure/build-host.sps <输出目录>
;;; 1. 从源码装入 (clojure driver) 及其依赖（宿主启动所需），再装入 (clojure compile) 及其依赖（编译器与 nanopass，只有 AOT 需要）；
;;; 2. compile-whole-program 把宿主程序 clojure/host.sps 与全部从源码装入的库写成 libN.fasl + program.fasl（按装入顺序，依赖在前）；
;;; 3. 写清单 cleat-host.manifest：
;;;      library libN.fasl (库名)     启动时按序装入
;;;      program program.fasl         启动时运行（登记钩子）
;;;      deferred libN.fasl (库名)    第一次 AOT 编译时再按序装入
;;; 宿主（Cleat.Scheme.Clojure.ClojureHost）读清单；fasl 对旧镜像编译时退回源码。
(import (rnrs) (rnrs eval) (ironscheme))

(define out-dir
  (let ((args (command-line)))
    (if (and (pair? args) (pair? (cdr args))) (cadr args) (error 'build-host "usage: build-host.sps <output directory>"))))

(define (path . parts)
  (fold-left (lambda (acc p) (string-append acc "/" p)) (car parts) (cdr parts)))

(define before (installed-libraries #t))
(define (new-libraries)
  (filter (lambda (n) (not (member n before))) (reverse (installed-libraries #t))))

(environment '(clojure driver))
(define startup (new-libraries))
(environment '(clojure compile))
(define all (new-libraries))

(display "startup libraries: ") (write startup) (newline)
(display "deferred libraries: ") (write (list-tail all (length startup))) (newline)

;; 输出目录里的旧产物先删掉（库的个数可能变少）
(when (file-exists? (path out-dir "cleat-host.manifest")) (delete-file (path out-dir "cleat-host.manifest")))
(let loop ((i 0))
  (let ((f (path out-dir (string-append "lib" (number->string i) ".fasl"))))
    (when (file-exists? f)
      (delete-file f)
      (let ((ifc (path out-dir (string-append "lib" (number->string i) ".ifc"))))
        (when (file-exists? ifc) (delete-file ifc)))
      (loop (+ i 1)))))

(compile-whole-program "clojure/host.sps" out-dir)

;; compile-whole-program 的 libN 与 all 一一对应（都按装入顺序）
(call-with-output-file (path out-dir "cleat-host.manifest")
  (lambda (p)
    (display ";; cleat-052：宿主的 Scheme 库（build-host.sps 生成）\n" p)
    (let loop ((names all) (i 0))
      (unless (null? names)
        (when (= i (length startup)) (display "program program.fasl\n" p))
        (display (if (< i (length startup)) "library " "deferred ") p)
        (display (string-append "lib" (number->string i) ".fasl ") p)
        (write (car names) p)
        (newline p)
        (loop (cdr names) (+ i 1))))
    (when (= (length all) (length startup)) (display "program program.fasl\n" p))))

(display "wrote ") (display (path out-dir "cleat-host.manifest")) (newline)
