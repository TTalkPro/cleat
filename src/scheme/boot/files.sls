#| License
Copyright (c) 2007-2016 Llewellyn Pritchard
All rights reserved.
This source code is subject to terms and conditions of the BSD License.
See docs/license.txt. |#

(library (boot files)
  (export
    file-exists?
    delete-file
    get-directory-name
    file-newer?
    file-mtime)
    
  (import 
    (except (rnrs) file-exists? delete-file)
    (only (ironscheme) typed-lambda)
    (ironscheme contracts)
    (ironscheme unsafe))
    

  ;; 文件系统经 $ 原语（IronScheme.Core.HostPrimitives，iron-056）
  (define/contract (file-exists? fn:string)
    ($file-exists? fn))
    
  (define/contract (delete-file fn:string)
    ($delete-file fn))
    
  (define/contract (get-directory-name path)
    ($path-directory-name path))   
    
  (define (get-last-write-time filename)
    ($file-last-write-time filename))
    
  (define (file-mtime filename)
    ($file-mtime filename))
    
  (define (compare-time t1 t2)
    ($compare-time t1 t2))    
    
  (define/contract (file-newer? file1:string file2:string)
    (let ((r (compare-time (get-last-write-time file1)
                           (get-last-write-time file2))))
      (fx>=? r 0))))