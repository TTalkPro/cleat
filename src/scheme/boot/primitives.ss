;;; 原语表：由 Cleat.PrimGen 从 Cleat.Scheme（[Primitive]）与 Cleat.Core（Primitives.Table，名字 cleat.kernel/…）生成，请勿手工修改。
;;; 设计：designs/02-runtime.md §5、designs/15 §1；IronScheme 03 §6
;;;
;;; (primitive 名字 重载 …)
;;;   重载 = (overload "类型全名" "方法名" 固定参数个数 剩余参数? (标志 …) 内联模板 签名)
;;;   标志：pure（无副作用）、unsafe（不做类型检查）、allow-tail-call、mifoldable（常量参数可在编译期折叠）、
;;;         discard（结果可丢弃）、unrestricted（任何参数都不报错）、safeongoodargs（参数类型已知正确时免检查）；
;;;         inline（kernel 表：发射器有内联模板）；语义见 Cleat.Scheme/Primitive.cs（照 Chez cmacros.ss 的 prim-mask，skeg iron-069）。内联模板为名字或 #f
;;;   签名 = ((参数类型 …) (结果类型 …)) 或 #f；参数类型表末尾的 ... 表示前一个类型可重复
;;; (special 名字 种类)：编译器专门处理的原语；同名的 primitive 条目（若有）是非内联时的后备实现
(primitive-table 2
  (primitive $annotation-expression
    (overload "Cleat.Scheme.AnnotationPrimitives" "Expression" 1 #f (pure unsafe) #f #f))
  (primitive $annotation-source
    (overload "Cleat.Scheme.AnnotationPrimitives" "Source" 1 #f (pure unsafe) #f #f))
  (primitive $annotation-stripped
    (overload "Cleat.Scheme.AnnotationPrimitives" "Stripped" 1 #f (pure unsafe) #f #f))
  (primitive $annotation?
    (overload "Cleat.Scheme.AnnotationPrimitives" "IsAnnotation" 1 #f (pure unsafe) #f #f))
  (primitive $application-directory
    (overload "Cleat.Scheme.HostPrimitives" "ApplicationDirectory" 0 #f (unsafe) #f #f))
  (primitive $as-fixnum
    (overload "Cleat.Scheme.TypedPrimitives" "AsFixnum" 1 #f (unsafe) #f #f))
  (primitive $as-flonum
    (overload "Cleat.Scheme.TypedPrimitives" "AsFlonum" 1 #f (unsafe) #f #f))
  (primitive $assembly-file-id
    (overload "Cleat.Scheme.BootInterface" "AssemblyFileId" 1 #f (unsafe) #f #f))
  (primitive $bignum->bytes
    (overload "Cleat.Scheme.HostPrimitives" "BignumToBytes" 1 #f (pure unsafe) #f #f))
  (primitive $bignum->fixnum
    (overload "Cleat.Scheme.HostPrimitives" "BignumToFixnum" 1 #f (pure unsafe) #f #f))
  (primitive $bignum->flonum
    (overload "Cleat.Scheme.HostPrimitives" "BignumToFlonum" 1 #f (pure unsafe) #f #f))
  (primitive $bignum->string
    (overload "Cleat.Scheme.HostPrimitives" "BignumToString" 2 #f (pure unsafe) #f #f))
  (primitive $bignum-and
    (overload "Cleat.Scheme.HostPrimitives" "BignumAnd" 2 #f (pure unsafe) #f #f))
  (primitive $bignum-bit-count
    (overload "Cleat.Scheme.HostPrimitives" "BignumBitCount" 1 #f (pure unsafe) #f #f))
  (primitive $bignum-bit-length
    (overload "Cleat.Scheme.HostPrimitives" "BignumBitLength" 1 #f (pure unsafe) #f #f))
  (primitive $bignum-bit-set?
    (overload "Cleat.Scheme.HostPrimitives" "IsBignumBitSet" 2 #f (pure unsafe) #f #f))
  (primitive $bignum-expt
    (overload "Cleat.Scheme.HostPrimitives" "BignumExpt" 2 #f (pure unsafe) #f #f))
  (primitive $bignum-first-bit-set
    (overload "Cleat.Scheme.HostPrimitives" "BignumFirstBitSet" 1 #f (pure unsafe) #f #f))
  (primitive $bignum-fits-fixnum?
    (overload "Cleat.Scheme.HostPrimitives" "BignumFitsFixnum" 1 #f (pure unsafe) #f #f))
  (primitive $bignum-ior
    (overload "Cleat.Scheme.HostPrimitives" "BignumIor" 2 #f (pure unsafe) #f #f))
  (primitive $bignum-negative?
    (overload "Cleat.Scheme.HostPrimitives" "IsBignumNegative" 1 #f (pure unsafe) #f #f))
  (primitive $bignum-not
    (overload "Cleat.Scheme.HostPrimitives" "BignumNot" 1 #f (pure unsafe) #f #f))
  (primitive $bignum-quotient
    (overload "Cleat.Scheme.HostPrimitives" "BignumQuotient" 2 #f (pure unsafe) #f #f))
  (primitive $bignum-remainder
    (overload "Cleat.Scheme.HostPrimitives" "BignumRemainder" 2 #f (pure unsafe) #f #f))
  (primitive $bignum-reverse-bits
    (overload "Cleat.Scheme.HostPrimitives" "BignumReverseBits" 2 #f (pure unsafe) #f #f))
  (primitive $bignum-shift-left
    (overload "Cleat.Scheme.HostPrimitives" "BignumShiftLeft" 2 #f (pure unsafe) #f #f))
  (primitive $bignum-shift-right
    (overload "Cleat.Scheme.HostPrimitives" "BignumShiftRight" 2 #f (pure unsafe) #f #f))
  (primitive $bignum-xor
    (overload "Cleat.Scheme.HostPrimitives" "BignumXor" 2 #f (pure unsafe) #f #f))
  (primitive $bignum?
    (overload "Cleat.Scheme.HostPrimitives" "IsBignum" 1 #f (pure unsafe mifoldable discard unrestricted) #f ((ptr) (boolean))))
  (primitive $binary-input-port?
    (overload "Cleat.Scheme.PortPrimitives" "IsBinaryInputPort" 1 #f (pure unsafe) #f #f))
  (primitive $binary-output-port?
    (overload "Cleat.Scheme.PortPrimitives" "IsBinaryOutputPort" 1 #f (pure unsafe) #f #f))
  (primitive $binary-port?
    (overload "Cleat.Scheme.PortPrimitives" "IsBinaryPort" 1 #f (pure unsafe) #f #f))
  (primitive $boolean?
    (overload "Cleat.Scheme.HostPrimitives" "IsBoolean" 1 #f (pure unsafe mifoldable discard unrestricted) #f ((ptr) (boolean))))
  (primitive $boot-image-id
    (overload "Cleat.Scheme.BootInterface" "ImageId" 0 #f (unsafe) #f #f))
  (primitive $boot-interface-file
    (overload "Cleat.Scheme.BootInterface" "InterfaceFile" 0 #f (unsafe) #f #f))
  (primitive $buffered-port?
    (overload "Cleat.Scheme.PortPrimitives" "IsBufferedPort" 1 #f (pure unsafe) #f #f))
  (primitive $bytes->bignum
    (overload "Cleat.Scheme.HostPrimitives" "BytesToBignum" 1 #f (pure unsafe) #f #f))
  (primitive $bytes->double
    (overload "Cleat.Scheme.HostPrimitives" "BytesToDouble" 1 #f (pure unsafe) #f #f))
  (primitive $bytes->single
    (overload "Cleat.Scheme.HostPrimitives" "BytesToSingle" 1 #f (pure unsafe) #f #f))
  (primitive $bytes->sint
    (overload "Cleat.Scheme.HostPrimitives" "BytesToSint" 2 #f (pure unsafe) #f #f))
  (primitive $bytes->uint
    (overload "Cleat.Scheme.HostPrimitives" "BytesToUint" 2 #f (pure unsafe) #f #f))
  (primitive $bytevector-clone
    (overload "Cleat.Scheme.HostPrimitives" "BytevectorClone" 1 #f (unsafe) #f #f))
  (primitive $bytevector-copy!
    (overload "Cleat.Scheme.HostPrimitives" "BytevectorCopy" 5 #f (unsafe) #f #f))
  (primitive $bytevector-length
    (overload "Cleat.Scheme.HostPrimitives" "BytevectorLength" 1 #f (pure unsafe mifoldable) #f ((bytevector) (fixnum))))
  (primitive $bytevector-output-port-extract
    (overload "Cleat.Scheme.PortPrimitives" "BytevectorOutputPortExtract" 1 #f (unsafe) #f #f))
  (primitive $bytevector-ref
    (overload "Cleat.Scheme.Unsafe" "ByteVectorRef" 2 #f (pure unsafe mifoldable) $bytevector-ref ((bytevector fixnum) (fixnum))))
  (primitive $bytevector-reverse!
    (overload "Cleat.Scheme.HostPrimitives" "BytevectorReverse" 1 #f (unsafe) #f #f))
  (primitive $bytevector-set!
    (overload "Cleat.Scheme.Unsafe" "ByteVectorSet" 3 #f (unsafe) $bytevector-set! ((bytevector fixnum fixnum) (void))))
  (primitive $bytevector-truncate
    (overload "Cleat.Scheme.PortPrimitives" "BytevectorTruncate" 2 #f (unsafe) #f #f))
  (primitive $bytevector?
    (overload "Cleat.Scheme.HostPrimitives" "IsBytevector" 1 #f (pure unsafe mifoldable discard unrestricted) #f ((ptr) (boolean))))
  (primitive $car
    (overload "Cleat.Scheme.Unsafe" "Car" 1 #f (pure unsafe mifoldable) $car ((pair) (ptr))))
  (primitive $cdr
    (overload "Cleat.Scheme.Unsafe" "Cdr" 1 #f (pure unsafe mifoldable) $cdr ((pair) (ptr))))
  (primitive $char->integer
    (overload "Cleat.Scheme.HostPrimitives" "CharToInteger" 1 #f (pure unsafe mifoldable) #f ((char) (fixnum))))
  (primitive $char-alphabetic?
    (overload "Cleat.Scheme.HostPrimitives" "IsCharAlphabetic" 1 #f (pure unsafe mifoldable) #f ((char) (boolean))))
  (primitive $char-downcase
    (overload "Cleat.Scheme.HostPrimitives" "CharDowncase" 1 #f (pure unsafe mifoldable) #f ((char) (char))))
  (primitive $char-foldcase
    (overload "Cleat.Scheme.HostPrimitives" "CharFoldcase" 1 #f (pure unsafe mifoldable) #f ((char) (char))))
  (primitive $char-general-category
    (overload "Cleat.Scheme.HostPrimitives" "CharGeneralCategory" 1 #f (pure unsafe mifoldable) #f ((char) (symbol))))
  (primitive $char-lower-case?
    (overload "Cleat.Scheme.HostPrimitives" "IsCharLowerCase" 1 #f (pure unsafe mifoldable) #f ((char) (boolean))))
  (primitive $char-numeric?
    (overload "Cleat.Scheme.HostPrimitives" "IsCharNumeric" 1 #f (pure unsafe mifoldable) #f ((char) (boolean))))
  (primitive $char-title-case?
    (overload "Cleat.Scheme.HostPrimitives" "IsCharTitleCase" 1 #f (pure unsafe mifoldable) #f ((char) (boolean))))
  (primitive $char-upcase
    (overload "Cleat.Scheme.HostPrimitives" "CharUpcase" 1 #f (pure unsafe mifoldable) #f ((char) (char))))
  (primitive $char-upper-case?
    (overload "Cleat.Scheme.HostPrimitives" "IsCharUpperCase" 1 #f (pure unsafe mifoldable) #f ((char) (boolean))))
  (primitive $char-whitespace?
    (overload "Cleat.Scheme.HostPrimitives" "IsCharWhitespace" 1 #f (pure unsafe mifoldable) #f ((char) (boolean))))
  (primitive $char?
    (overload "Cleat.Scheme.HostPrimitives" "IsChar" 1 #f (pure unsafe mifoldable discard unrestricted) #f ((ptr) (boolean))))
  (primitive $chars->string
    (overload "Cleat.Scheme.HostPrimitives" "CharsToString" 1 #f (unsafe) #f #f))
  (primitive $check-initialized
    (overload "Cleat.Scheme.Calls" "CheckInitialized" 2 #f () #f #f))
  (primitive $codec-get-bytes
    (overload "Cleat.Scheme.HostPrimitives" "CodecGetBytes" 2 #f (unsafe) #f #f))
  (primitive $codec-get-string
    (overload "Cleat.Scheme.HostPrimitives" "CodecGetString" 2 #f (unsafe) #f #f))
  (primitive $codec?
    (overload "Cleat.Scheme.PortPrimitives" "IsCodec" 1 #f (pure unsafe) #f #f))
  (primitive $compare-time
    (overload "Cleat.Scheme.HostPrimitives" "CompareTime" 2 #f (pure unsafe) #f #f))
  (primitive $complex-acos
    (overload "Cleat.Scheme.HostPrimitives" "ComplexAcos" 1 #f (pure unsafe) #f #f))
  (primitive $complex-asin
    (overload "Cleat.Scheme.HostPrimitives" "ComplexAsin" 1 #f (pure unsafe) #f #f))
  (primitive $complex-atan
    (overload "Cleat.Scheme.HostPrimitives" "ComplexAtan" 1 #f (pure unsafe) #f #f))
  (primitive $complex-cos
    (overload "Cleat.Scheme.HostPrimitives" "ComplexCos" 1 #f (pure unsafe) #f #f))
  (primitive $complex-cosh
    (overload "Cleat.Scheme.HostPrimitives" "ComplexCosh" 1 #f (pure unsafe) #f #f))
  (primitive $complex-exp
    (overload "Cleat.Scheme.HostPrimitives" "ComplexExp" 1 #f (pure unsafe) #f #f))
  (primitive $complex-expt
    (overload "Cleat.Scheme.HostPrimitives" "ComplexExpt" 2 #f (pure unsafe) #f #f))
  (primitive $complex-log
    (overload "Cleat.Scheme.HostPrimitives" "ComplexLog" 1 #f (pure unsafe) #f #f))
  (primitive $complex-sin
    (overload "Cleat.Scheme.HostPrimitives" "ComplexSin" 1 #f (pure unsafe) #f #f))
  (primitive $complex-sinh
    (overload "Cleat.Scheme.HostPrimitives" "ComplexSinh" 1 #f (pure unsafe) #f #f))
  (primitive $complex-sqrt
    (overload "Cleat.Scheme.HostPrimitives" "ComplexSqrt" 1 #f (pure unsafe) #f #f))
  (primitive $complex-tan
    (overload "Cleat.Scheme.HostPrimitives" "ComplexTan" 1 #f (pure unsafe) #f #f))
  (primitive $complex-tanh
    (overload "Cleat.Scheme.HostPrimitives" "ComplexTanh" 1 #f (pure unsafe) #f #f))
  (primitive $complexnum->rectnum
    (overload "Cleat.Scheme.HostPrimitives" "ComplexnumToRectnum" 1 #f (pure unsafe) #f #f))
  (primitive $complexnum-imag
    (overload "Cleat.Scheme.HostPrimitives" "ComplexnumImag" 1 #f (pure unsafe) #f #f))
  (primitive $complexnum-real
    (overload "Cleat.Scheme.HostPrimitives" "ComplexnumReal" 1 #f (pure unsafe) #f #f))
  (primitive $complexnum?
    (overload "Cleat.Scheme.HostPrimitives" "IsComplexnum" 1 #f (pure unsafe mifoldable discard unrestricted) #f ((ptr) (boolean))))
  (primitive $compound-condition?
    (overload "Cleat.Scheme.HostPrimitives" "IsCompoundCondition" 1 #f (pure unsafe) #f #f))
  (primitive $condition?
    (overload "Cleat.Scheme.HostPrimitives" "IsCondition" 1 #f (pure unsafe mifoldable discard unrestricted) #f ((ptr) (boolean))))
  (primitive $console-error-port
    (overload "Cleat.Scheme.PortPrimitives" "ConsoleError" 0 #f (unsafe) #f #f))
  (primitive $console-foreground-color
    (overload "Cleat.Scheme.HostPrimitives" "ConsoleForegroundColor" 0 #f (unsafe) #f #f))
  (primitive $console-input-port
    (overload "Cleat.Scheme.PortPrimitives" "ConsoleInput" 0 #f (unsafe) #f #f))
  (primitive $console-output-port
    (overload "Cleat.Scheme.PortPrimitives" "ConsoleOutput" 0 #f (unsafe) #f #f))
  (primitive $cpu-architecture
    (overload "Cleat.Scheme.HostPrimitives" "CpuArchitecture" 0 #f (pure unsafe) #f #f))
  (primitive $culture-country
    (overload "Cleat.Scheme.HostPrimitives" "CultureCountry" 0 #f (unsafe) #f #f))
  (primitive $culture-language
    (overload "Cleat.Scheme.HostPrimitives" "CultureLanguage" 0 #f (unsafe) #f #f))
  (primitive $culture-name
    (overload "Cleat.Scheme.HostPrimitives" "CultureName" 0 #f (unsafe) #f #f))
  (primitive $current-directory
    (overload "Cleat.Scheme.HostPrimitives" "CurrentDirectory" 0 #f (unsafe) #f #f))
  (primitive $current-handlers
    (overload "Cleat.Scheme.ExceptionHandlers" "Current" 0 #f (unsafe) #f #f))
  (primitive $custom-port-input
    (overload "Cleat.Scheme.PortPrimitives" "CustomPortInput" 1 #f (pure unsafe) #f #f))
  (primitive $custom-port-output
    (overload "Cleat.Scheme.PortPrimitives" "CustomPortOutput" 1 #f (pure unsafe) #f #f))
  (primitive $custom-textual-port?
    (overload "Cleat.Scheme.PortPrimitives" "IsCustomTextualPort" 1 #f (pure unsafe) #f #f))
  (primitive $delete-file
    (overload "Cleat.Scheme.HostPrimitives" "DeleteFile" 1 #f (unsafe) #f #f))
  (primitive $directory-create
    (overload "Cleat.Scheme.HostPrimitives" "DirectoryCreate" 1 #f (unsafe) #f #f))
  (primitive $directory-exists?
    (overload "Cleat.Scheme.HostPrimitives" "DirectoryExists" 1 #f (unsafe) #f #f))
  (primitive $directory-files
    (overload "Cleat.Scheme.HostPrimitives" "DirectoryFiles" 2 #f (unsafe) #f #f))
  (primitive $double->bytes
    (overload "Cleat.Scheme.HostPrimitives" "DoubleToBytes" 1 #f (pure unsafe) #f #f))
  (primitive $eq-hashtable?
    (overload "Cleat.Scheme.HostPrimitives" "IsEqHashtable" 1 #f (pure unsafe mifoldable discard unrestricted) #f ((ptr) (boolean))))
  (primitive $exact-compare
    (overload "Cleat.Scheme.HostPrimitives" "ExactCompare" 2 #f (pure unsafe) #f #f))
  (primitive $exception->string
    (overload "Cleat.Scheme.HostPrimitives" "ExceptionToString" 1 #f (unsafe) #f #f))
  (primitive $exception-message
    (overload "Cleat.Scheme.HostPrimitives" "ExceptionMessage" 1 #f (unsafe) #f #f))
  (primitive $exception?
    (overload "Cleat.Scheme.HostPrimitives" "IsException" 1 #f (pure unsafe) #f #f))
  (primitive $exit
    (overload "Cleat.Scheme.HostPrimitives" "Exit" 1 #f (unsafe) #f #f))
  (primitive $file-exists?
    (overload "Cleat.Scheme.HostPrimitives" "FileExists" 1 #f (unsafe) #f #f))
  (primitive $file-last-write-time
    (overload "Cleat.Scheme.HostPrimitives" "FileLastWriteTime" 1 #f (unsafe) #f #f))
  (primitive $file-mtime
    (overload "Cleat.Scheme.HostPrimitives" "FileMtime" 1 #f (unsafe) #f #f))
  (primitive $fixnum->bignum
    (overload "Cleat.Scheme.HostPrimitives" "FixnumToBignum" 1 #f (pure unsafe) #f #f))
  (primitive $fixnum->flonum
    (overload "Cleat.Scheme.HostPrimitives" "FixnumToFlonum" 1 #f (pure unsafe mifoldable) #f ((fixnum) (flonum))))
  (primitive $fixnum->string
    (overload "Cleat.Scheme.HostPrimitives" "FixnumToString" 2 #f (pure unsafe) #f #f))
  (primitive $fixnum?
    (overload "Cleat.Scheme.HostPrimitives" "IsFixnum" 1 #f (pure unsafe mifoldable discard unrestricted) $fixnum? ((ptr) (boolean))))
  (primitive $fl*
    (overload "Cleat.Scheme.Unsafe" "FlMul" 2 #f (pure unsafe mifoldable) $fl* ((flonum flonum) (flonum))))
  (primitive $fl+
    (overload "Cleat.Scheme.Unsafe" "FlAdd" 2 #f (pure unsafe mifoldable) $fl+ ((flonum flonum) (flonum))))
  (primitive $fl-
    (overload "Cleat.Scheme.Unsafe" "FlNeg" 1 #f (pure unsafe mifoldable) $fl- ((flonum) (flonum)))
    (overload "Cleat.Scheme.Unsafe" "FlSub" 2 #f (pure unsafe mifoldable) $fl- ((flonum flonum) (flonum))))
  (primitive $fl/
    (overload "Cleat.Scheme.Unsafe" "FlDiv" 2 #f (pure unsafe mifoldable) $fl/ ((flonum flonum) (flonum))))
  (primitive $fl<=?
    (overload "Cleat.Scheme.Unsafe" "FlLe" 2 #f (pure unsafe mifoldable) $fl<=? ((flonum flonum) (boolean))))
  (primitive $fl<?
    (overload "Cleat.Scheme.Unsafe" "FlLt" 2 #f (pure unsafe mifoldable) $fl<? ((flonum flonum) (boolean))))
  (primitive $fl=?
    (overload "Cleat.Scheme.Unsafe" "FlEq" 2 #f (pure unsafe mifoldable) $fl=? ((flonum flonum) (boolean))))
  (primitive $fl>=?
    (overload "Cleat.Scheme.Unsafe" "FlGe" 2 #f (pure unsafe mifoldable) $fl>=? ((flonum flonum) (boolean))))
  (primitive $fl>?
    (overload "Cleat.Scheme.Unsafe" "FlGt" 2 #f (pure unsafe mifoldable) $fl>? ((flonum flonum) (boolean))))
  (primitive $flabs
    (overload "Cleat.Scheme.HostPrimitives" "FlAbs" 1 #f (pure unsafe mifoldable) #f ((flonum) (flonum))))
  (primitive $flacos
    (overload "Cleat.Scheme.HostPrimitives" "FlAcos" 1 #f (pure unsafe) #f ((flonum) (flonum))))
  (primitive $flasin
    (overload "Cleat.Scheme.HostPrimitives" "FlAsin" 1 #f (pure unsafe) #f ((flonum) (flonum))))
  (primitive $flatan
    (overload "Cleat.Scheme.HostPrimitives" "FlAtan" 1 #f (pure unsafe) #f ((flonum) (flonum))))
  (primitive $flatan2
    (overload "Cleat.Scheme.HostPrimitives" "FlAtan2" 2 #f (pure unsafe) #f ((flonum flonum) (flonum))))
  (primitive $flceiling
    (overload "Cleat.Scheme.HostPrimitives" "FlCeiling" 1 #f (pure unsafe mifoldable) #f ((flonum) (flonum))))
  (primitive $flcos
    (overload "Cleat.Scheme.HostPrimitives" "FlCos" 1 #f (pure unsafe) #f ((flonum) (flonum))))
  (primitive $flcosh
    (overload "Cleat.Scheme.HostPrimitives" "FlCosh" 1 #f (pure unsafe) #f ((flonum) (flonum))))
  (primitive $flexp
    (overload "Cleat.Scheme.HostPrimitives" "FlExp" 1 #f (pure unsafe) #f ((flonum) (flonum))))
  (primitive $flexpt
    (overload "Cleat.Scheme.HostPrimitives" "FlExpt" 2 #f (pure unsafe) #f ((flonum flonum) (flonum))))
  (primitive $flfloor
    (overload "Cleat.Scheme.HostPrimitives" "FlFloor" 1 #f (pure unsafe mifoldable) #f ((flonum) (flonum))))
  (primitive $flinfinite?
    (overload "Cleat.Scheme.HostPrimitives" "IsFlInfinite" 1 #f (pure unsafe mifoldable) #f ((flonum) (boolean))))
  (primitive $fllog
    (overload "Cleat.Scheme.HostPrimitives" "FlLog" 1 #f (pure unsafe) #f ((flonum) (flonum))))
  (primitive $fllog2
    (overload "Cleat.Scheme.HostPrimitives" "FlLog2" 2 #f (pure unsafe) #f ((flonum flonum) (flonum))))
  (primitive $flnan?
    (overload "Cleat.Scheme.HostPrimitives" "IsFlNan" 1 #f (pure unsafe mifoldable) #f ((flonum) (boolean))))
  (primitive $flonum->exact
    (overload "Cleat.Scheme.HostPrimitives" "FlonumToExact" 1 #f (pure unsafe) #f #f))
  (primitive $flonum->ratnum
    (overload "Cleat.Scheme.HostPrimitives" "FlonumToRatnum" 1 #f (pure unsafe) #f #f))
  (primitive $flonum?
    (overload "Cleat.Scheme.HostPrimitives" "IsFlonum" 1 #f (pure unsafe mifoldable discard unrestricted) $flonum? ((ptr) (boolean))))
  (primitive $flround
    (overload "Cleat.Scheme.HostPrimitives" "FlRound" 1 #f (pure unsafe mifoldable) #f ((flonum) (flonum))))
  (primitive $flsin
    (overload "Cleat.Scheme.HostPrimitives" "FlSin" 1 #f (pure unsafe) #f ((flonum) (flonum))))
  (primitive $flsinh
    (overload "Cleat.Scheme.HostPrimitives" "FlSinh" 1 #f (pure unsafe) #f ((flonum) (flonum))))
  (primitive $flsqrt
    (overload "Cleat.Scheme.HostPrimitives" "FlSqrt" 1 #f (pure unsafe) #f ((flonum) (flonum))))
  (primitive $fltan
    (overload "Cleat.Scheme.HostPrimitives" "FlTan" 1 #f (pure unsafe) #f ((flonum) (flonum))))
  (primitive $fltanh
    (overload "Cleat.Scheme.HostPrimitives" "FlTanh" 1 #f (pure unsafe) #f ((flonum) (flonum))))
  (primitive $fltruncate
    (overload "Cleat.Scheme.HostPrimitives" "FlTruncate" 1 #f (pure unsafe mifoldable) #f ((flonum) (flonum))))
  (primitive $fx*
    (overload "Cleat.Scheme.Unsafe" "FxMul" 2 #f (pure unsafe mifoldable) $fx* ((fixnum fixnum) (fixnum))))
  (primitive $fx+
    (overload "Cleat.Scheme.Unsafe" "FxAdd" 2 #f (pure unsafe mifoldable) $fx+ ((fixnum fixnum) (fixnum))))
  (primitive $fx-
    (overload "Cleat.Scheme.Unsafe" "FxNeg" 1 #f (pure unsafe mifoldable) $fx- ((fixnum) (fixnum)))
    (overload "Cleat.Scheme.Unsafe" "FxSub" 2 #f (pure unsafe mifoldable) $fx- ((fixnum fixnum) (fixnum))))
  (primitive $fx<=?
    (overload "Cleat.Scheme.Unsafe" "FxLe" 2 #f (pure unsafe mifoldable) $fx<=? ((fixnum fixnum) (boolean))))
  (primitive $fx<?
    (overload "Cleat.Scheme.Unsafe" "FxLt" 2 #f (pure unsafe mifoldable) $fx<? ((fixnum fixnum) (boolean))))
  (primitive $fx=?
    (overload "Cleat.Scheme.Unsafe" "FxEq" 2 #f (pure unsafe mifoldable) $fx=? ((fixnum fixnum) (boolean))))
  (primitive $fx>=?
    (overload "Cleat.Scheme.Unsafe" "FxGe" 2 #f (pure unsafe mifoldable) $fx>=? ((fixnum fixnum) (boolean))))
  (primitive $fx>?
    (overload "Cleat.Scheme.Unsafe" "FxGt" 2 #f (pure unsafe mifoldable) $fx>? ((fixnum fixnum) (boolean))))
  (primitive $fxabs
    (overload "Cleat.Scheme.HostPrimitives" "FxAbs" 1 #f (pure unsafe mifoldable) #f ((fixnum) (fixnum))))
  (primitive $fxand
    (overload "Cleat.Scheme.Unsafe" "FxAnd" 2 #f (pure unsafe mifoldable) $fxand ((fixnum fixnum) (fixnum))))
  (primitive $fxarithmetic-shift-left
    (overload "Cleat.Scheme.Unsafe" "FxShl" 2 #f (pure unsafe mifoldable) $fxarithmetic-shift-left ((fixnum fixnum) (fixnum))))
  (primitive $fxarithmetic-shift-right
    (overload "Cleat.Scheme.Unsafe" "FxShr" 2 #f (pure unsafe mifoldable) $fxarithmetic-shift-right ((fixnum fixnum) (fixnum))))
  (primitive $fxdiv
    (overload "Cleat.Scheme.Unsafe" "FxDiv" 2 #f (pure unsafe mifoldable) $fxdiv ((fixnum fixnum) (fixnum))))
  (primitive $fxdiv0
    (overload "Cleat.Scheme.Unsafe" "FxDiv0" 2 #f (pure unsafe mifoldable) $fxdiv0 ((fixnum fixnum) (fixnum))))
  (primitive $fxeven?
    (overload "Cleat.Scheme.Unsafe" "FxEven" 1 #f (pure unsafe mifoldable) $fxeven? ((fixnum) (boolean))))
  (primitive $fxior
    (overload "Cleat.Scheme.Unsafe" "FxIor" 2 #f (pure unsafe mifoldable) $fxior ((fixnum fixnum) (fixnum))))
  (primitive $fxmod
    (overload "Cleat.Scheme.Unsafe" "FxMod" 2 #f (pure unsafe mifoldable) $fxmod ((fixnum fixnum) (fixnum))))
  (primitive $fxmod0
    (overload "Cleat.Scheme.Unsafe" "FxMod0" 2 #f (pure unsafe mifoldable) $fxmod0 ((fixnum fixnum) (fixnum))))
  (primitive $fxnegative?
    (overload "Cleat.Scheme.Unsafe" "FxNegative" 1 #f (pure unsafe mifoldable) $fxnegative? ((fixnum) (boolean))))
  (primitive $fxnot
    (overload "Cleat.Scheme.Unsafe" "FxNot" 1 #f (pure unsafe mifoldable) $fxnot ((fixnum) (fixnum))))
  (primitive $fxodd?
    (overload "Cleat.Scheme.Unsafe" "FxOdd" 1 #f (pure unsafe mifoldable) $fxodd? ((fixnum) (boolean))))
  (primitive $fxpositive?
    (overload "Cleat.Scheme.Unsafe" "FxPositive" 1 #f (pure unsafe mifoldable) $fxpositive? ((fixnum) (boolean))))
  (primitive $fxxor
    (overload "Cleat.Scheme.Unsafe" "FxXor" 2 #f (pure unsafe mifoldable) $fxxor ((fixnum fixnum) (fixnum))))
  (primitive $fxzero?
    (overload "Cleat.Scheme.Unsafe" "FxZero" 1 #f (pure unsafe mifoldable) $fxzero? ((fixnum) (boolean))))
  (primitive $gc-collect
    (overload "Cleat.Scheme.HostPrimitives" "GcCollect" 0 #f (unsafe) #f #f))
  (primitive $gensym-realm
    (overload "Cleat.Scheme.SymbolPrimitives" "GensymRealm" 0 #f () #f #f)
    (overload "Cleat.Scheme.SymbolPrimitives" "GensymRealm" 1 #f () #f #f))
  (primitive $gensym-scope
    (overload "Cleat.Scheme.SymbolPrimitives" "GensymScope" 0 #f () #f #f)
    (overload "Cleat.Scheme.SymbolPrimitives" "GensymScope" 1 #f () #f #f))
  (primitive $get-environment-variable
    (overload "Cleat.Scheme.HostPrimitives" "GetEnvironmentVariable" 1 #f (unsafe) #f #f))
  (primitive $get-output-string
    (overload "Cleat.Scheme.PortPrimitives" "GetOutputString" 1 #f (unsafe) #f #f))
  (primitive $hashtable-clear!
    (overload "Cleat.Scheme.HostPrimitives" "HashtableClear" 1 #f (unsafe) #f #f))
  (primitive $hashtable-clone
    (overload "Cleat.Scheme.HostPrimitives" "HashtableClone" 1 #f (unsafe) #f #f))
  (primitive $hashtable-contains?
    (overload "Cleat.Scheme.HostPrimitives" "HashtableContains" 2 #f (unsafe) #f #f))
  (primitive $hashtable-count
    (overload "Cleat.Scheme.HostPrimitives" "HashtableCount" 1 #f (unsafe) #f #f))
  (primitive $hashtable-equivalence-function
    (overload "Cleat.Scheme.HostPrimitives" "HashtableEquivalenceFunction" 1 #f (pure unsafe) #f #f))
  (primitive $hashtable-from-null
    (overload "Cleat.Scheme.HostPrimitives" "HashtableFromNull" 1 #f (pure unsafe) #f #f))
  (primitive $hashtable-get
    (overload "Cleat.Scheme.HostPrimitives" "HashtableGet" 2 #f (unsafe) #f #f))
  (primitive $hashtable-hash-function
    (overload "Cleat.Scheme.HostPrimitives" "HashtableHashFunction" 1 #f (pure unsafe) #f #f))
  (primitive $hashtable-keys
    (overload "Cleat.Scheme.HostPrimitives" "HashtableKeys" 1 #f (unsafe) #f #f))
  (primitive $hashtable-mutable?
    (overload "Cleat.Scheme.HostPrimitives" "IsHashtableMutable" 1 #f (pure unsafe) #f #f))
  (primitive $hashtable-put!
    (overload "Cleat.Scheme.HostPrimitives" "HashtablePut" 3 #f (unsafe) #f #f))
  (primitive $hashtable-readonly-copy
    (overload "Cleat.Scheme.HostPrimitives" "HashtableReadOnlyCopy" 1 #f (unsafe) #f #f))
  (primitive $hashtable-remove!
    (overload "Cleat.Scheme.HostPrimitives" "HashtableRemove" 2 #f (unsafe) #f #f))
  (primitive $hashtable-to-null
    (overload "Cleat.Scheme.HostPrimitives" "HashtableToNull" 1 #f (pure unsafe) #f #f))
  (primitive $hashtable?
    (overload "Cleat.Scheme.HostPrimitives" "IsHashtable" 1 #f (pure unsafe mifoldable discard unrestricted) #f ((ptr) (boolean))))
  (primitive $immutable-string?
    (overload "Cleat.Scheme.HostPrimitives" "IsImmutableString" 1 #f (pure unsafe mifoldable discard unrestricted) #f ((ptr) (boolean))))
  (primitive $inexact-compare
    (overload "Cleat.Scheme.HostPrimitives" "InexactCompare" 2 #f (pure unsafe) #f #f))
  (primitive $input-port?
    (overload "Cleat.Scheme.PortPrimitives" "IsInputPort" 1 #f (pure unsafe) #f #f))
  (primitive $integer->char
    (overload "Cleat.Scheme.HostPrimitives" "IntegerToChar" 1 #f (pure unsafe mifoldable) #f ((fixnum) (char))))
  (primitive $latin-1-codec
    (overload "Cleat.Scheme.PortPrimitives" "Latin1Codec" 0 #f (unsafe) #f #f))
  (primitive $library-interface-files
    (overload "Cleat.Scheme.BootInterface" "LibraryInterfaceFiles" 0 #f (unsafe) #f #f))
  (primitive $list->typed-vector
    (overload "Cleat.Scheme.HostPrimitives" "ListToTypedVector" 2 #f (unsafe) #f #f))
  (primitive $list->vector
    (overload "Cleat.Scheme.HostPrimitives" "ListToVector" 1 #f (unsafe) #f #f))
  (primitive $list-copy
    (overload "Cleat.Scheme.HostPrimitives" "ListCopy" 1 #f (unsafe) #f #f))
  (primitive $machine-name
    (overload "Cleat.Scheme.HostPrimitives" "MachineName" 0 #f (unsafe) #f #f))
  (primitive $make-bytevector
    (overload "Cleat.Scheme.HostPrimitives" "MakeBytevector" 1 #f (unsafe) #f #f))
  (primitive $make-complexnum
    (overload "Cleat.Scheme.HostPrimitives" "MakeComplexnum" 2 #f (pure unsafe) #f #f))
  (primitive $make-custom-binary-input-port
    (overload "Cleat.Scheme.PortPrimitives" "MakeCustomBinaryInputPort" 5 #f (unsafe) #f #f))
  (primitive $make-custom-binary-input/output-port
    (overload "Cleat.Scheme.PortPrimitives" "MakeCustomBinaryInputOutputPort" 6 #f (unsafe) #f #f))
  (primitive $make-custom-binary-output-port
    (overload "Cleat.Scheme.PortPrimitives" "MakeCustomBinaryOutputPort" 5 #f (unsafe) #f #f))
  (primitive $make-custom-textual-input-port
    (overload "Cleat.Scheme.PortPrimitives" "MakeCustomTextualInputPort" 5 #f (unsafe) #f #f))
  (primitive $make-custom-textual-input/output-port
    (overload "Cleat.Scheme.PortPrimitives" "MakeCustomTextualInputOutputPort" 6 #f (unsafe) #f #f))
  (primitive $make-custom-textual-output-port
    (overload "Cleat.Scheme.PortPrimitives" "MakeCustomTextualOutputPort" 5 #f (unsafe) #f #f))
  (primitive $make-eq-hashtable
    (overload "Cleat.Scheme.HostPrimitives" "MakeEqHashtable" 1 #f (unsafe) #f #f))
  (primitive $make-guid
    (overload "Cleat.Scheme.HostPrimitives" "MakeGuid" 0 #f (unsafe) #f #f))
  (primitive $make-hashtable
    (overload "Cleat.Scheme.HostPrimitives" "MakeHashtable" 3 #f (unsafe) #f #f))
  (primitive $make-multiple-values
    (overload "Cleat.Scheme.HostPrimitives" "MakeMultipleValues" 1 #f (unsafe) #f #f))
  (primitive $make-param-slot
    (overload "Cleat.Scheme.Parameters" "MakeSlot" 1 #f (unsafe) #f #f))
  (primitive $make-random
    (overload "Cleat.Scheme.HostPrimitives" "MakeRandom" 0 #f (unsafe) #f #f))
  (primitive $make-rectnum
    (overload "Cleat.Scheme.HostPrimitives" "MakeRectnum" 2 #f (pure unsafe) #f #f))
  (primitive $make-semaphore
    (overload "Cleat.Scheme.HostPrimitives" "MakeSemaphore" 1 #f (unsafe) #f #f))
  (primitive $make-string
    (overload "Cleat.Scheme.HostPrimitives" "MakeString" 2 #f (unsafe) #f #f))
  (primitive $make-transcoder
    (overload "Cleat.Scheme.PortPrimitives" "MakeTranscoder" 3 #f (unsafe) #f #f))
  (primitive $make-typed-vector
    (overload "Cleat.Scheme.HostPrimitives" "MakeTypedVector" 2 #f (unsafe) #f #f))
  (primitive $make-utf16-codec
    (overload "Cleat.Scheme.HostPrimitives" "MakeUtf16Codec" 1 #f (unsafe) #f #f))
  (primitive $make-utf32-codec
    (overload "Cleat.Scheme.HostPrimitives" "MakeUtf32Codec" 1 #f (unsafe) #f #f))
  (primitive $make-vector
    (overload "Cleat.Scheme.HostPrimitives" "MakeVector" 1 #f (unsafe) #f #f))
  (primitive $multiple-values?
    (overload "Cleat.Scheme.HostPrimitives" "IsMultipleValues" 1 #f (pure unsafe) #f #f))
  (primitive $mutable-string?
    (overload "Cleat.Scheme.HostPrimitives" "IsMutableString" 1 #f (pure unsafe mifoldable discard unrestricted) #f ((ptr) (boolean))))
  (primitive $native-eol-style
    (overload "Cleat.Scheme.PortPrimitives" "NativeEolStyle" 0 #f (pure unsafe) #f #f))
  (primitive $native-transcoder
    (overload "Cleat.Scheme.PortPrimitives" "NativeTranscoder" 0 #f (pure unsafe) #f #f))
  (primitive $object->string
    (overload "Cleat.Scheme.HostPrimitives" "ObjectToString" 1 #f (unsafe) #f #f))
  (primitive $object-hash
    (overload "Cleat.Scheme.HostPrimitives" "ObjectHash" 1 #f (pure unsafe) #f #f))
  (primitive $open-bytevector-input-port
    (overload "Cleat.Scheme.PortPrimitives" "OpenBytevectorInputPort" 1 #f (unsafe) #f #f))
  (primitive $open-bytevector-output-port
    (overload "Cleat.Scheme.PortPrimitives" "OpenBytevectorOutputPort" 0 #f (unsafe) #f #f))
  (primitive $open-file-input-port
    (overload "Cleat.Scheme.PortPrimitives" "OpenFileInputPort" 2 #f (unsafe) #f #f))
  (primitive $open-input-file
    (overload "Cleat.Scheme.PortPrimitives" "OpenInputFile" 1 #f (unsafe) #f #f))
  (primitive $open-output-file
    (overload "Cleat.Scheme.PortPrimitives" "OpenOutputFile" 2 #f (unsafe) #f #f))
  (primitive $open-output-string
    (overload "Cleat.Scheme.PortPrimitives" "OpenOutputString" 0 #f (unsafe) #f #f))
  (primitive $open-string-input-port
    (overload "Cleat.Scheme.PortPrimitives" "OpenStringInputPort" 1 #f (unsafe) #f #f))
  (primitive $os-description
    (overload "Cleat.Scheme.HostPrimitives" "OsDescription" 0 #f (pure unsafe) #f #f))
  (primitive $os-name
    (overload "Cleat.Scheme.HostPrimitives" "OsName" 0 #f (pure unsafe) #f #f))
  (primitive $os-windows?
    (overload "Cleat.Scheme.HostPrimitives" "IsWindows" 0 #f (pure unsafe) #f #f))
  (primitive $output-port?
    (overload "Cleat.Scheme.PortPrimitives" "IsOutputPort" 1 #f (pure unsafe) #f #f))
  (primitive $param-ref
    (overload "Cleat.Scheme.Parameters" "Ref" 1 #f (unsafe) #f #f))
  (primitive $param-set!
    (overload "Cleat.Scheme.Parameters" "Set" 2 #f (unsafe) #f #f))
  (primitive $path-change-extension
    (overload "Cleat.Scheme.HostPrimitives" "PathChangeExtension" 2 #f (pure unsafe) #f #f))
  (primitive $path-combine
    (overload "Cleat.Scheme.HostPrimitives" "PathCombine" 2 #f (pure unsafe) #f #f))
  (primitive $path-directory-name
    (overload "Cleat.Scheme.HostPrimitives" "PathDirectoryName" 1 #f (pure unsafe) #f #f))
  (primitive $path-extension
    (overload "Cleat.Scheme.HostPrimitives" "PathExtension" 1 #f (pure unsafe) #f #f))
  (primitive $path-file-name
    (overload "Cleat.Scheme.HostPrimitives" "PathFileName" 1 #f (pure unsafe) #f #f))
  (primitive $path-full
    (overload "Cleat.Scheme.HostPrimitives" "PathFull" 1 #f (unsafe) #f #f))
  (primitive $path-trim-separator
    (overload "Cleat.Scheme.HostPrimitives" "PathTrimSeparator" 1 #f (pure unsafe) #f #f))
  (primitive $plain-string?
    (overload "Cleat.Scheme.HostPrimitives" "IsPlainString" 1 #f (pure unsafe mifoldable discard unrestricted) #f ((ptr) (boolean))))
  (primitive $port-buffer-mode
    (overload "Cleat.Scheme.PortPrimitives" "PortBufferMode" 1 #f (unsafe) #f #f))
  (primitive $port-close-input
    (overload "Cleat.Scheme.PortPrimitives" "CloseInput" 1 #f (unsafe) #f #f))
  (primitive $port-close-output
    (overload "Cleat.Scheme.PortPrimitives" "CloseOutput" 1 #f (unsafe) #f #f))
  (primitive $port-flush
    (overload "Cleat.Scheme.PortPrimitives" "Flush" 1 #f (unsafe) #f #f))
  (primitive $port-has-position?
    (overload "Cleat.Scheme.PortPrimitives" "HasPosition" 1 #f (unsafe) #f #f))
  (primitive $port-has-set-position?
    (overload "Cleat.Scheme.PortPrimitives" "HasSetPosition" 1 #f (unsafe) #f #f))
  (primitive $port-peek-char
    (overload "Cleat.Scheme.PortPrimitives" "PeekChar" 1 #f (unsafe) #f #f))
  (primitive $port-position
    (overload "Cleat.Scheme.PortPrimitives" "Position" 1 #f (unsafe) #f #f))
  (primitive $port-read-all
    (overload "Cleat.Scheme.PortPrimitives" "ReadAll" 1 #f (unsafe) #f #f))
  (primitive $port-read-byte
    (overload "Cleat.Scheme.PortPrimitives" "ReadByte" 1 #f (unsafe) #f #f))
  (primitive $port-read-bytes
    (overload "Cleat.Scheme.PortPrimitives" "ReadBytes" 4 #f (unsafe) #f #f))
  (primitive $port-read-char
    (overload "Cleat.Scheme.PortPrimitives" "ReadChar" 1 #f (unsafe) #f #f))
  (primitive $port-read-line
    (overload "Cleat.Scheme.PortPrimitives" "ReadLine" 1 #f (unsafe) #f #f))
  (primitive $port-read-string
    (overload "Cleat.Scheme.PortPrimitives" "ReadString" 2 #f (unsafe) #f #f))
  (primitive $port-set-position!
    (overload "Cleat.Scheme.PortPrimitives" "SetPosition" 2 #f (unsafe) #f #f))
  (primitive $port-transcoder
    (overload "Cleat.Scheme.PortPrimitives" "PortTranscoder" 1 #f (unsafe) #f #f))
  (primitive $port-write-byte
    (overload "Cleat.Scheme.PortPrimitives" "WriteByte" 2 #f (unsafe) #f #f))
  (primitive $port-write-bytes
    (overload "Cleat.Scheme.PortPrimitives" "WriteBytes" 4 #f (unsafe) #f #f))
  (primitive $port-write-char
    (overload "Cleat.Scheme.PortPrimitives" "WriteChar" 2 #f (unsafe) #f #f))
  (primitive $port-write-string
    (overload "Cleat.Scheme.PortPrimitives" "WriteString" 2 #f (unsafe) #f #f))
  (primitive $procedure-arity
    (overload "Cleat.Scheme.HostPrimitives" "ProcedureArity" 1 #f (pure unsafe) #f #f))
  (primitive $procedure-form
    (overload "Cleat.Scheme.HostPrimitives" "ProcedureForm" 1 #f (pure unsafe) #f #f))
  (primitive $procedure-name
    (overload "Cleat.Scheme.HostPrimitives" "ProcedureName" 1 #f (pure unsafe) #f #f))
  (primitive $procedure?
    (overload "Cleat.Scheme.HostPrimitives" "IsProcedure" 1 #f (pure unsafe mifoldable discard unrestricted) #f ((ptr) (boolean))))
  (primitive $program-failed
    (overload "Cleat.Scheme.HostPrimitives" "ProgramFailed" 0 #f (unsafe) #f #f))
  (primitive $raise-clr-exception
    (overload "Cleat.Scheme.Exceptions" "RaiseClrException" 1 #f (unsafe) #f #f))
  (primitive $random-next
    (overload "Cleat.Scheme.HostPrimitives" "RandomNext" 2 #f (unsafe) #f #f))
  (primitive $ratnum->flonum
    (overload "Cleat.Scheme.HostPrimitives" "RatnumToFlonum" 1 #f (pure unsafe) #f #f))
  (primitive $ratnum-denominator
    (overload "Cleat.Scheme.HostPrimitives" "RatnumDenominator" 1 #f (pure unsafe) #f #f))
  (primitive $ratnum-numerator
    (overload "Cleat.Scheme.HostPrimitives" "RatnumNumerator" 1 #f (pure unsafe) #f #f))
  (primitive $ratnum?
    (overload "Cleat.Scheme.HostPrimitives" "IsRatnum" 1 #f (pure unsafe mifoldable discard unrestricted) #f ((ptr) (boolean))))
  (primitive $rcd-parent
    (overload "Cleat.Scheme.HostPrimitives" "RcdParent" 1 #f (pure unsafe) #f #f))
  (primitive $rcd-protocol
    (overload "Cleat.Scheme.HostPrimitives" "RcdProtocol" 1 #f (pure unsafe) #f #f))
  (primitive $rcd-rtd
    (overload "Cleat.Scheme.HostPrimitives" "RcdRtd" 1 #f (pure unsafe) #f #f))
  (primitive $rcd?
    (overload "Cleat.Scheme.HostPrimitives" "IsRcd" 1 #f (pure unsafe) #f #f))
  (primitive $real-compare
    (overload "Cleat.Scheme.HostPrimitives" "RealCompare" 2 #f (pure unsafe) #f #f))
  (primitive $record-field-ref
    (overload "Cleat.Scheme.Records" "RecordFieldRef" 3 #f (unsafe) #f #f))
  (primitive $record-field-set!
    (overload "Cleat.Scheme.Records" "RecordFieldSet" 4 #f (unsafe) #f #f))
  (primitive $record-fields
    (overload "Cleat.Scheme.HostPrimitives" "RecordFields" 1 #f (pure unsafe) #f #f))
  (primitive $record-instance?
    (overload "Cleat.Scheme.Records" "RecordInstance" 2 #f (unsafe) #f #f))
  (primitive $record-rtd
    (overload "Cleat.Scheme.HostPrimitives" "RecordRtd" 1 #f (pure unsafe) #f #f))
  (primitive $record?
    (overload "Cleat.Scheme.HostPrimitives" "IsRecord" 1 #f (pure unsafe mifoldable discard unrestricted) #f ((ptr) (boolean))))
  (primitive $rectnum->complexnum
    (overload "Cleat.Scheme.HostPrimitives" "RectnumToComplexnum" 1 #f (pure unsafe) #f #f))
  (primitive $rectnum-imag
    (overload "Cleat.Scheme.HostPrimitives" "RectnumImag" 1 #f (pure unsafe) #f #f))
  (primitive $rectnum-real
    (overload "Cleat.Scheme.HostPrimitives" "RectnumReal" 1 #f (pure unsafe) #f #f))
  (primitive $rectnum?
    (overload "Cleat.Scheme.HostPrimitives" "IsRectnum" 1 #f (pure unsafe mifoldable discard unrestricted) #f ((ptr) (boolean))))
  (primitive $rtd-field-mutable?
    (overload "Cleat.Scheme.HostPrimitives" "IsRtdFieldMutable" 2 #f (pure unsafe) #f #f))
  (primitive $rtd-field-names
    (overload "Cleat.Scheme.HostPrimitives" "RtdFieldNames" 1 #f (pure unsafe) #f #f))
  (primitive $rtd-generative?
    (overload "Cleat.Scheme.HostPrimitives" "IsRtdGenerative" 1 #f (pure unsafe) #f #f))
  (primitive $rtd-name
    (overload "Cleat.Scheme.HostPrimitives" "RtdName" 1 #f (pure unsafe) #f #f))
  (primitive $rtd-opaque?
    (overload "Cleat.Scheme.HostPrimitives" "IsRtdOpaque" 1 #f (pure unsafe) #f #f))
  (primitive $rtd-parent
    (overload "Cleat.Scheme.HostPrimitives" "RtdParent" 1 #f (pure unsafe) #f #f))
  (primitive $rtd-sealed?
    (overload "Cleat.Scheme.HostPrimitives" "IsRtdSealed" 1 #f (pure unsafe) #f #f))
  (primitive $rtd-uid
    (overload "Cleat.Scheme.HostPrimitives" "RtdUid" 1 #f (pure unsafe) #f #f))
  (primitive $rtd?
    (overload "Cleat.Scheme.HostPrimitives" "IsRtd" 1 #f (pure unsafe) #f #f))
  (primitive $runtime-hooks-missing
    (overload "Cleat.Scheme.RuntimeHooks" "Missing" 0 #f () #f #f))
  (primitive $semaphore-release!
    (overload "Cleat.Scheme.HostPrimitives" "SemaphoreRelease" 1 #f (unsafe) #f #f))
  (primitive $semaphore-wait!
    (overload "Cleat.Scheme.HostPrimitives" "SemaphoreWait" 1 #f (unsafe) #f #f))
  (primitive $set-car!
    (overload "Cleat.Scheme.HostPrimitives" "SetCar" 2 #f (unsafe) #f #f))
  (primitive $set-cdr!
    (overload "Cleat.Scheme.HostPrimitives" "SetCdr" 2 #f (unsafe) #f #f))
  (primitive $set-compile-target!
    (overload "Cleat.Scheme.HostPrimitives" "SetCompileTarget" 1 #f (unsafe) #f #f))
  (primitive $set-console-foreground-color!
    (overload "Cleat.Scheme.HostPrimitives" "SetConsoleForegroundColor" 1 #f (unsafe) #f #f))
  (primitive $set-current-directory!
    (overload "Cleat.Scheme.HostPrimitives" "SetCurrentDirectory" 1 #f (unsafe) #f #f))
  (primitive $set-current-handlers!
    (overload "Cleat.Scheme.ExceptionHandlers" "Set" 1 #f (unsafe) #f #f))
  (primitive $set-runtime-hook!
    (overload "Cleat.Scheme.RuntimeHooks" "SetRuntimeHook" 2 #f () #f #f))
  (primitive $single->bytes
    (overload "Cleat.Scheme.HostPrimitives" "SingleToBytes" 1 #f (pure unsafe) #f #f))
  (primitive $sint->bytes
    (overload "Cleat.Scheme.HostPrimitives" "SintToBytes" 2 #f (pure unsafe) #f #f))
  (primitive $stack-trace
    (overload "Cleat.Scheme.HostPrimitives" "StackTrace" 0 #f (unsafe) #f #f))
  (primitive $standard-error-port
    (overload "Cleat.Scheme.PortPrimitives" "StandardError" 0 #f (unsafe) #f #f))
  (primitive $standard-input-port
    (overload "Cleat.Scheme.PortPrimitives" "StandardInput" 0 #f (unsafe) #f #f))
  (primitive $standard-output-port
    (overload "Cleat.Scheme.PortPrimitives" "StandardOutput" 0 #f (unsafe) #f #f))
  (primitive $start-thread
    (overload "Cleat.Scheme.HostPrimitives" "StartThread" 1 #f (unsafe) #f #f))
  (primitive $string->immutable
    (overload "Cleat.Scheme.HostPrimitives" "StringToImmutable" 1 #f (unsafe) #f #f))
  (primitive $string->list
    (overload "Cleat.Scheme.HostPrimitives" "StringToList" 1 #f (unsafe) #f #f))
  (primitive $string->symbol
    (overload "Cleat.Scheme.HostPrimitives" "StringToSymbol" 1 #f (pure unsafe) #f #f))
  (primitive $string-append
    (overload "Cleat.Scheme.HostPrimitives" "StringAppend" 1 #f (unsafe) #f #f))
  (primitive $string-ci-hash
    (overload "Cleat.Scheme.HostPrimitives" "StringCiHash" 1 #f (pure unsafe) #f #f))
  (primitive $string-compare
    (overload "Cleat.Scheme.HostPrimitives" "StringCompare" 2 #f (pure unsafe) #f #f))
  (primitive $string-copy
    (overload "Cleat.Scheme.HostPrimitives" "StringCopy" 1 #f (unsafe) #f #f))
  (primitive $string-fill!
    (overload "Cleat.Scheme.HostPrimitives" "StringFill" 2 #f (unsafe) #f #f))
  (primitive $string-format
    (overload "Cleat.Scheme.HostPrimitives" "StringFormat" 2 #f (unsafe) #f #f))
  (primitive $string-hash
    (overload "Cleat.Scheme.HostPrimitives" "StringHash" 1 #f (pure unsafe) #f #f))
  (primitive $string-length
    (overload "Cleat.Scheme.HostPrimitives" "StringLength" 1 #f (pure unsafe mifoldable) #f ((string) (fixnum))))
  (primitive $string-lower
    (overload "Cleat.Scheme.HostPrimitives" "StringLower" 1 #f (pure unsafe) #f #f))
  (primitive $string-mutable?
    (overload "Cleat.Scheme.HostPrimitives" "IsStringMutable" 1 #f (pure unsafe mifoldable discard unrestricted) #f ((ptr) (boolean))))
  (primitive $string-normalize
    (overload "Cleat.Scheme.HostPrimitives" "StringNormalize" 2 #f (pure unsafe) #f #f))
  (primitive $string-ref
    (overload "Cleat.Scheme.HostPrimitives" "StringRef" 2 #f (pure unsafe mifoldable) #f ((string fixnum) (char))))
  (primitive $string-replace
    (overload "Cleat.Scheme.HostPrimitives" "StringReplace" 3 #f (pure unsafe) #f #f))
  (primitive $string-set!
    (overload "Cleat.Scheme.HostPrimitives" "StringSet" 3 #f (unsafe) #f #f))
  (primitive $substring
    (overload "Cleat.Scheme.HostPrimitives" "Substring" 3 #f (unsafe) #f #f))
  (primitive $symbol-name
    (overload "Cleat.Scheme.HostPrimitives" "SymbolName" 1 #f (pure unsafe) #f #f))
  (primitive $symbol-value-set!
    (overload "Cleat.Scheme.HostPrimitives" "SymbolValueSet" 2 #f (unsafe) #f #f))
  (primitive $symbol?
    (overload "Cleat.Scheme.HostPrimitives" "IsSymbol" 1 #f (pure unsafe mifoldable discard unrestricted) #f ((ptr) (boolean))))
  (primitive $system-exception?
    (overload "Cleat.Scheme.HostPrimitives" "IsSystemException" 1 #f (pure unsafe) #f #f))
  (primitive $textual-port?
    (overload "Cleat.Scheme.PortPrimitives" "IsTextualPort" 1 #f (pure unsafe) #f #f))
  (primitive $timezone-offset-seconds
    (overload "Cleat.Scheme.HostPrimitives" "TimezoneOffsetSeconds" 0 #f (unsafe) #f #f))
  (primitive $transcoded-port
    (overload "Cleat.Scheme.PortPrimitives" "TranscodedPort" 2 #f (unsafe) #f #f))
  (primitive $transcoder-codec
    (overload "Cleat.Scheme.PortPrimitives" "TranscoderCodec" 1 #f (pure unsafe) #f #f))
  (primitive $transcoder-eol-style
    (overload "Cleat.Scheme.PortPrimitives" "TranscoderEolStyle" 1 #f (pure unsafe) #f #f))
  (primitive $transcoder-error-handling-mode
    (overload "Cleat.Scheme.PortPrimitives" "TranscoderHandlingMode" 1 #f (pure unsafe) #f #f))
  (primitive $transcoder?
    (overload "Cleat.Scheme.PortPrimitives" "IsTranscoder" 1 #f (pure unsafe) #f #f))
  (primitive $type-name
    (overload "Cleat.Scheme.HostPrimitives" "TypeName" 1 #f (pure unsafe) #f #f))
  (primitive $type-of
    (overload "Cleat.Scheme.HostPrimitives" "TypeOf" 1 #f (pure unsafe) #f #f))
  (primitive $type?
    (overload "Cleat.Scheme.HostPrimitives" "IsType" 1 #f (pure unsafe) #f #f))
  (primitive $typed-vector?
    (overload "Cleat.Scheme.HostPrimitives" "IsTypedVector" 2 #f (pure unsafe) #f #f))
  (primitive $uint->bytes
    (overload "Cleat.Scheme.HostPrimitives" "UintToBytes" 2 #f (pure unsafe) #f #f))
  (primitive $uninitialized
    (overload "Cleat.Scheme.Calls" "UninitializedValue" 0 #f () #f #f))
  (primitive $utf-16-codec
    (overload "Cleat.Scheme.PortPrimitives" "Utf16Codec" 0 #f (unsafe) #f #f))
  (primitive $utf-8-codec
    (overload "Cleat.Scheme.PortPrimitives" "Utf8Codec" 0 #f (unsafe) #f #f))
  (primitive $values-count
    (overload "Cleat.Scheme.ValuesPrimitives" "Count" 1 #f (pure unsafe) #f #f))
  (primitive $values-ref
    (overload "Cleat.Scheme.ValuesPrimitives" "Ref" 2 #f (pure unsafe) #f #f))
  (primitive $values-tail
    (overload "Cleat.Scheme.ValuesPrimitives" "Tail" 2 #f (pure unsafe) #f #f))
  (primitive $valuetype-vector?
    (overload "Cleat.Scheme.HostPrimitives" "IsValueTypeVector" 1 #f (pure unsafe) #f #f))
  (primitive $vector->list
    (overload "Cleat.Scheme.HostPrimitives" "VectorToList" 1 #f (unsafe) #f #f))
  (primitive $vector-clone
    (overload "Cleat.Scheme.HostPrimitives" "VectorClone" 1 #f (unsafe) #f #f))
  (primitive $vector-fill!
    (overload "Cleat.Scheme.HostPrimitives" "VectorFill" 2 #f (unsafe) #f #f))
  (primitive $vector-index-of
    (overload "Cleat.Scheme.HostPrimitives" "VectorIndexOf" 2 #f (unsafe) #f #f))
  (primitive $vector-length
    (overload "Cleat.Scheme.HostPrimitives" "VectorLength" 1 #f (pure unsafe mifoldable) #f ((vector) (fixnum))))
  (primitive $vector-ref
    (overload "Cleat.Scheme.Unsafe" "VectorRef" 2 #f (pure unsafe mifoldable) $vector-ref ((vector fixnum) (ptr))))
  (primitive $vector-reverse!
    (overload "Cleat.Scheme.HostPrimitives" "VectorReverse" 1 #f (unsafe) #f #f))
  (primitive $vector-set!
    (overload "Cleat.Scheme.Unsafe" "VectorSet" 3 #f (unsafe) $vector-set! ((vector fixnum ptr) (void))))
  (primitive $vector-sort!
    (overload "Cleat.Scheme.HostPrimitives" "VectorSort" 2 #f (unsafe) #f #f))
  (primitive $vector?
    (overload "Cleat.Scheme.HostPrimitives" "IsVector" 1 #f (pure unsafe mifoldable discard unrestricted) #f ((ptr) (boolean))))
  (primitive apply
    (overload "Cleat.Scheme.Control" "Apply" 1 #t (allow-tail-call) #f #f))
  (primitive bignum-sqrt
    (overload "Cleat.Scheme.Builtins" "SqrtBigInteger" 1 #f (pure) #f #f))
  (primitive bignum-sqrt-exact
    (overload "Cleat.Scheme.Builtins" "ExactSqrtBigInteger" 1 #f (pure) #f #f))
  (primitive bytevector->string
    (overload "Cleat.Scheme.IO" "ByteVectorToString" 2 #f () #f #f))
  (primitive call-with-return
    (overload "Cleat.Scheme.Control" "CallWithReturn" 1 #f () #f #f))
  (primitive call-with-values
    (overload "Cleat.Scheme.Control" "CallWithValues" 2 #f (allow-tail-call) #f #f))
  (primitive car
    (overload "Cleat.Scheme.Builtins" "Car" 1 #f (mifoldable) car ((pair) (ptr))))
  (primitive cdr
    (overload "Cleat.Scheme.Builtins" "Cdr" 1 #f (mifoldable) cdr ((pair) (ptr))))
  (primitive cleat-scheme-runtime
    (overload "Cleat.Scheme.Builtins" "GetRuntime" 0 #f () #f #f))
  (primitive cleat-scheme-version
    (overload "Cleat.Scheme.Builtins" "Version" 0 #f () #f #f))
  (primitive cleat.kernel/add
    (overload "clojure.lang.Numbers" "add" 2 #f (pure inline) #f #f))
  (primitive cleat.kernel/assoc
    (overload "clojure.lang.RT" "assoc" 3 #f () #f #f))
  (primitive cleat.kernel/bit-and
    (overload "clojure.lang.Numbers" "and" 2 #f (pure) #f #f))
  (primitive cleat.kernel/bit-or
    (overload "clojure.lang.Numbers" "or" 2 #f (pure) #f #f))
  (primitive cleat.kernel/bit-shift-left
    (overload "clojure.lang.Numbers" "shiftLeft" 2 #f (pure) #f #f))
  (primitive cleat.kernel/bit-shift-right
    (overload "clojure.lang.Numbers" "shiftRight" 2 #f (pure) #f #f))
  (primitive cleat.kernel/bit-xor
    (overload "clojure.lang.Numbers" "xor" 2 #f (pure) #f #f))
  (primitive cleat.kernel/boolean-cast
    (overload "clojure.lang.RT" "booleanCast" 1 #f (pure inline) #f #f))
  (primitive cleat.kernel/char-cast
    (overload "clojure.lang.RT" "charCast" 1 #f (pure) #f #f))
  (primitive cleat.kernel/class-of
    (overload "clojure.lang.Util" "classOf" 1 #f (pure) #f #f))
  (primitive cleat.kernel/compare
    (overload "clojure.lang.Util" "compare" 2 #f (pure) #f #f))
  (primitive cleat.kernel/conj
    (overload "clojure.lang.RT" "conj" 2 #f () #f #f))
  (primitive cleat.kernel/cons
    (overload "clojure.lang.RT" "cons" 2 #f () #f #f))
  (primitive cleat.kernel/count
    (overload "clojure.lang.RT" "count" 1 #f (inline) #f #f))
  (primitive cleat.kernel/dec
    (overload "clojure.lang.Numbers" "dec" 1 #f (pure inline) #f #f))
  (primitive cleat.kernel/divide
    (overload "clojure.lang.Numbers" "divide" 2 #f (pure) #f #f))
  (primitive cleat.kernel/double-cast
    (overload "clojure.lang.RT" "doubleCast" 1 #f (pure) #f #f))
  (primitive cleat.kernel/equals
    (overload "clojure.lang.Util" "equals" 2 #f (pure) #f #f))
  (primitive cleat.kernel/equiv
    (overload "clojure.lang.Util" "equiv" 2 #f (pure inline) #f #f))
  (primitive cleat.kernel/first
    (overload "clojure.lang.RT" "first" 1 #f (inline) #f #f))
  (primitive cleat.kernel/get
    (overload "clojure.lang.RT" "get" 2 #f (inline) #f #f))
  (primitive cleat.kernel/get3
    (overload "clojure.lang.RT" "get" 3 #f () #f #f))
  (primitive cleat.kernel/gt
    (overload "clojure.lang.Numbers" "gt" 2 #f (pure inline) #f #f))
  (primitive cleat.kernel/gte
    (overload "clojure.lang.Numbers" "gte" 2 #f (pure inline) #f #f))
  (primitive cleat.kernel/hash
    (overload "clojure.lang.Util" "hash" 1 #f (pure) #f #f))
  (primitive cleat.kernel/hasheq
    (overload "clojure.lang.Util" "hasheq" 1 #f (pure) #f #f))
  (primitive cleat.kernel/identical?
    (overload "clojure.lang.Util" "identical" 2 #f (pure inline) #f #f))
  (primitive cleat.kernel/inc
    (overload "clojure.lang.Numbers" "inc" 1 #f (pure inline) #f #f))
  (primitive cleat.kernel/int-cast
    (overload "clojure.lang.RT" "intCast" 1 #f (pure) #f #f))
  (primitive cleat.kernel/iter
    (overload "clojure.lang.RT" "iter" 1 #f () #f #f))
  (primitive cleat.kernel/keyword
    (overload "clojure.lang.RT" "keyword" 2 #f (pure) #f #f))
  (primitive cleat.kernel/list0
    (overload "clojure.lang.RT" "list" 0 #f (pure) #f #f))
  (primitive cleat.kernel/list1
    (overload "clojure.lang.RT" "list" 1 #f () #f #f))
  (primitive cleat.kernel/list2
    (overload "clojure.lang.RT" "list" 2 #f () #f #f))
  (primitive cleat.kernel/long-cast
    (overload "clojure.lang.RT" "longCast" 1 #f (pure) #f #f))
  (primitive cleat.kernel/lt
    (overload "clojure.lang.Numbers" "lt" 2 #f (pure inline) #f #f))
  (primitive cleat.kernel/lte
    (overload "clojure.lang.Numbers" "lte" 2 #f (pure inline) #f #f))
  (primitive cleat.kernel/map
    (overload "clojure.lang.RT" "map" 0 #t () #f #f))
  (primitive cleat.kernel/max
    (overload "clojure.lang.Numbers" "max" 2 #f (pure) #f #f))
  (primitive cleat.kernel/maybe-special-tag
    (overload "Cleat.Core.Runtime.Kernel" "MaybeSpecialTag" 1 #f () #f #f))
  (primitive cleat.kernel/maybe-type
    (overload "Cleat.Core.Runtime.Kernel" "MaybeType" 2 #f () #f #f))
  (primitive cleat.kernel/min
    (overload "clojure.lang.Numbers" "min" 2 #f (pure) #f #f))
  (primitive cleat.kernel/minus
    (overload "clojure.lang.Numbers" "minus" 2 #f (pure inline) #f #f))
  (primitive cleat.kernel/more
    (overload "clojure.lang.RT" "more" 1 #f (inline) #f #f))
  (primitive cleat.kernel/multiply
    (overload "clojure.lang.Numbers" "multiply" 2 #f (pure inline) #f #f))
  (primitive cleat.kernel/neg?
    (overload "clojure.lang.Numbers" "isNeg" 1 #f (pure) #f #f))
  (primitive cleat.kernel/negate
    (overload "clojure.lang.Numbers" "minus" 1 #f (pure) #f #f))
  (primitive cleat.kernel/next
    (overload "clojure.lang.RT" "next" 1 #f (inline) #f #f))
  (primitive cleat.kernel/nth
    (overload "clojure.lang.RT" "nth" 2 #f (inline) #f #f))
  (primitive cleat.kernel/nth3
    (overload "clojure.lang.RT" "nth" 3 #f () #f #f))
  (primitive cleat.kernel/num
    (overload "clojure.lang.Numbers" "num" 1 #f (pure) #f #f))
  (primitive cleat.kernel/num-equiv
    (overload "clojure.lang.Numbers" "equiv" 2 #f (pure inline) #f #f))
  (primitive cleat.kernel/pos?
    (overload "clojure.lang.Numbers" "isPos" 1 #f (pure) #f #f))
  (primitive cleat.kernel/print-string
    (overload "clojure.lang.RT" "printString" 1 #f () #f #f))
  (primitive cleat.kernel/quotient
    (overload "clojure.lang.Numbers" "quotient" 2 #f (pure) #f #f))
  (primitive cleat.kernel/reduced?
    (overload "clojure.lang.RT" "isReduced" 1 #f (pure) #f #f))
  (primitive cleat.kernel/remainder
    (overload "clojure.lang.Numbers" "remainder" 2 #f (pure) #f #f))
  (primitive cleat.kernel/seq
    (overload "clojure.lang.RT" "seq" 1 #f (inline) #f #f))
  (primitive cleat.kernel/set
    (overload "clojure.lang.RT" "set" 0 #t () #f #f))
  (primitive cleat.kernel/subvec
    (overload "clojure.lang.RT" "subvec" 3 #f () #f #f))
  (primitive cleat.kernel/unsigned-bit-shift-right
    (overload "clojure.lang.Numbers" "unsignedShiftRight" 2 #f (pure) #f #f))
  (primitive cleat.kernel/vector
    (overload "clojure.lang.RT" "vector" 0 #t () #f #f))
  (primitive cleat.kernel/zero?
    (overload "clojure.lang.Numbers" "isZero" 1 #f (pure inline) #f #f))
  (primitive command-line
    (overload "Cleat.Scheme.Builtins" "CommandLine" 0 #f () #f #f))
  (primitive compile-to-current-directory?
    (overload "Cleat.Scheme.Builtins" "CompileToCurrentDirectory" 0 #f () #f #f)
    (overload "Cleat.Scheme.Builtins" "CompileToCurrentDirectory" 1 #f () #f #f))
  (primitive compress-constants?
    (overload "Cleat.Scheme.Builtins" "CompressConstants" 0 #f () #f #f)
    (overload "Cleat.Scheme.Builtins" "CompressConstants" 1 #f () #f #f))
  (primitive condition
    (overload "Cleat.Scheme.Conditions" "MakeCondition" 0 #t () #f #f))
  (primitive condition-accessor
    (overload "Cleat.Scheme.Conditions" "ConditionAccessor" 2 #f () #f #f))
  (primitive condition-predicate
    (overload "Cleat.Scheme.Conditions" "ConditionPredicate" 1 #f () #f #f))
  (primitive cons
    (overload "Cleat.Scheme.Builtins" "Cons" 2 #f (discard unrestricted) cons ((ptr ptr) (pair))))
  (primitive debug-mode?
    (overload "Cleat.Scheme.Builtins" "IsDebugMode" 0 #f () #f #f)
    (overload "Cleat.Scheme.Builtins" "IsDebugMode" 1 #f () #f #f))
  (primitive decompose-flonum
    (overload "Cleat.Scheme.Builtins" "DecomposeFlonum" 1 #f () #f #f))
  (primitive deserialize-port
    (overload "Cleat.Scheme.Fasl" "DeserializePort" 1 #f () #f #f))
  (primitive deterministic-gensym?
    (overload "Cleat.Scheme.Builtins" "DeterministicGensym" 0 #f () #f #f)
    (overload "Cleat.Scheme.Builtins" "DeterministicGensym" 1 #f () #f #f))
  (primitive eof-object
    (overload "Cleat.Scheme.Builtins" "EofObject" 0 #f () #f #f))
  (primitive eof-object?
    (overload "Cleat.Scheme.Builtins" "IsEof" 1 #f () #f #f))
  (primitive eq?
    (overload "Cleat.Scheme.Builtins" "IsEqual" 2 #f (pure mifoldable discard unrestricted) eq? ((ptr ptr) (boolean))))
  (primitive eqv?
    (overload "Cleat.Scheme.Builtins" "IsEqualValue" 2 #f (pure mifoldable discard unrestricted) eqv? ((ptr ptr) (boolean))))
  (primitive fx*internal
    (overload "Cleat.Scheme.Builtins" "FxMultiplyInternal" 2 #f (pure) #f ((fixnum fixnum) (integer))))
  (primitive fx+internal
    (overload "Cleat.Scheme.Builtins" "FxPlusInternal" 2 #f (pure) #f ((fixnum fixnum) (integer))))
  (primitive fx-internal
    (overload "Cleat.Scheme.Builtins" "FxMinusInternal" 2 #f (pure) #f ((fixnum fixnum) (integer))))
  (primitive fxarithmetic-shift-left-internal
    (overload "Cleat.Scheme.Builtins" "FxShiftLeftInternal" 2 #f (pure) #f ((fixnum fixnum) (integer))))
  (primitive generic*
    (overload "Cleat.Scheme.Builtins" "Multiply" 2 #f (pure) #f #f))
  (primitive generic+
    (overload "Cleat.Scheme.Builtins" "Add" 2 #f (pure) #f #f))
  (primitive generic-
    (overload "Cleat.Scheme.Builtins" "Subtract" 2 #f (pure) #f #f))
  (primitive generic-div
    (overload "Cleat.Scheme.Builtins" "Div" 2 #f (pure) #f #f))
  (primitive generic-negate
    (overload "Cleat.Scheme.Builtins" "Negate" 1 #f (pure) #f #f))
  (primitive generic/
    (overload "Cleat.Scheme.Builtins" "Divide" 2 #f (pure) #f #f))
  (primitive gensym
    (overload "Cleat.Scheme.SymbolPrimitives" "Gensym" 0 #f () #f #f)
    (overload "Cleat.Scheme.SymbolPrimitives" "Gensym" 1 #f () #f #f))
  (primitive get-clr-type
    (overload "Cleat.Scheme.Builtins" "GetClrType" 1 #f () #f #f)
    (overload "Cleat.Scheme.Builtins" "GetClrType" 1 #t () #f #f))
  (primitive get-command-line
    (overload "Cleat.Scheme.Builtins" "CommandLine" 0 #f () #f #f))
  (primitive get-library-paths
    (overload "Cleat.Scheme.Builtins" "GetLibraryPaths" 0 #f () #f #f))
  (primitive hashtable-entries
    (overload "Cleat.Scheme.Hashtables" "HashtableEntries" 1 #f () #f #f))
  (primitive hashtable-for-each
    (overload "Cleat.Scheme.Hashtables" "HashtableForEach" 2 #f () #f #f))
  (primitive hashtable-map
    (overload "Cleat.Scheme.Hashtables" "HashtableMap" 2 #f () #f #f))
  (primitive host-type
    (overload "Cleat.Scheme.HostPrimitives" "HostType" 0 #f (pure) #f #f))
  (primitive host-version
    (overload "Cleat.Scheme.HostPrimitives" "HostVersion" 0 #f (pure) #f #f))
  (primitive inexact=?
    (overload "Cleat.Scheme.Builtins" "InexactEqual" 2 #f (pure) #f #f))
  (primitive list*
    (overload "Cleat.Scheme.Builtins" "ListStar" 1 #t () #f #f)
    (overload "Cleat.Scheme.Builtins" "ListStar" 2 #f () #f #f)
    (overload "Cleat.Scheme.Builtins" "ListStar" 3 #f () #f #f)
    (overload "Cleat.Scheme.Builtins" "ListStar" 4 #f () #f #f))
  (primitive list-prim
    (overload "Cleat.Scheme.Builtins" "List" 0 #f () #f #f)
    (overload "Cleat.Scheme.Builtins" "List" 0 #t () #f #f)
    (overload "Cleat.Scheme.Builtins" "List" 1 #f () #f #f)
    (overload "Cleat.Scheme.Builtins" "List" 2 #f () #f #f)
    (overload "Cleat.Scheme.Builtins" "List" 3 #f () #f #f)
    (overload "Cleat.Scheme.Builtins" "List" 4 #f () #f #f)
    (overload "Cleat.Scheme.Builtins" "List" 5 #f () #f #f))
  (primitive load-fasl
    (overload "Cleat.Scheme.ModuleLoader" "LoadFasl" 1 #f () #f #f))
  (primitive load-library-fasl
    (overload "Cleat.Scheme.ModuleLoader" "LoadLibraryDll" 1 #f () #f #f))
  (primitive lw-debug-mode?
    (overload "Cleat.Scheme.Builtins" "IsLightWeightDebugMode" 0 #f () #f #f)
    (overload "Cleat.Scheme.Builtins" "IsLightWeightDebugMode" 1 #f () #f #f))
  (primitive machine-type
    (overload "Cleat.Scheme.HostPrimitives" "MachineType" 0 #f (pure) #f #f))
  (primitive make-annotation
    (overload "Cleat.Scheme.Builtins" "MakeAnnotation" 3 #f () #f #f))
  (primitive make-record-constructor-descriptor
    (overload "Cleat.Scheme.Records" "MakeRecordConstructorDescriptor" 3 #f () #f #f))
  (primitive make-record-type-descriptor
    (overload "Cleat.Scheme.Records" "MakeRecordTypeDescriptor" 6 #f () #f #f)
    (overload "Cleat.Scheme.Records" "MakeRecordTypeDescriptorTyped" 7 #f () #f #f))
  (primitive nil?
    (overload "Cleat.Scheme.Builtins" "IsNil" 1 #f (pure mifoldable discard unrestricted) nil? ((ptr) (boolean))))
  (primitive not
    (overload "Cleat.Scheme.Builtins" "Not" 1 #f (pure mifoldable discard unrestricted) not ((ptr) (boolean))))
  (primitive null?
    (overload "Cleat.Scheme.Builtins" "IsNull" 1 #f (pure mifoldable discard unrestricted) null? ((ptr) (boolean))))
  (primitive open-file-input/output-port
    (overload "Cleat.Scheme.IO" "OpenFileInputOutputPort" 1 #f () #f #f)
    (overload "Cleat.Scheme.IO" "OpenFileInputOutputPort" 2 #f () #f #f)
    (overload "Cleat.Scheme.IO" "OpenFileInputOutputPort" 3 #f () #f #f)
    (overload "Cleat.Scheme.IO" "OpenFileInputOutputPort" 4 #f () #f #f))
  (primitive open-file-output-port
    (overload "Cleat.Scheme.IO" "OpenFileOutputPort" 1 #f () #f #f)
    (overload "Cleat.Scheme.IO" "OpenFileOutputPort" 2 #f () #f #f)
    (overload "Cleat.Scheme.IO" "OpenFileOutputPort" 3 #f () #f #f)
    (overload "Cleat.Scheme.IO" "OpenFileOutputPort" 4 #f () #f #f))
  (primitive open-tcp-input/output-port
    (overload "Cleat.Scheme.Builtins" "OpenTcpInputOutputPort" 3 #f () #f #f))
  (primitive pair?
    (overload "Cleat.Scheme.Builtins" "IsPair" 1 #f (pure mifoldable discard unrestricted) pair? ((ptr) (boolean))))
  (primitive pointer+
    (overload "Cleat.Scheme.Builtins" "PointerAdd" 2 #f () #f #f))
  (primitive read-annotated
    (overload "Cleat.Scheme.Builtins" "ReadAnnotated" 1 #f () #f #f))
  (primitive record-accessor
    (overload "Cleat.Scheme.Records" "RecordAccessor" 2 #f () #f #f))
  (primitive record-constructor
    (overload "Cleat.Scheme.Records" "RecordConstructor" 1 #f () #f #f))
  (primitive record-mutator
    (overload "Cleat.Scheme.Records" "RecordMutator" 2 #f () #f #f))
  (primitive record-predicate
    (overload "Cleat.Scheme.Records" "RecordPredicate" 1 #f () #f #f))
  (primitive record-rtd
    (overload "Cleat.Scheme.Records" "RecordRtd" 1 #f () #f #f))
  (primitive record?
    (overload "Cleat.Scheme.Records" "IsRecord" 1 #f () #f #f))
  (primitive remove-location
    (overload "Cleat.Scheme.SymbolPrimitives" "RemoveLocation" 1 #f () #f #f))
  (primitive reverse!
    (overload "Cleat.Scheme.Builtins" "NReverse" 1 #f () #f #f))
  (primitive serialize-port
    (overload "Cleat.Scheme.Fasl" "SerializePort" 2 #f () #f #f))
  (primitive set-symbol-value!
    (overload "Cleat.Scheme.SymbolPrimitives" "SetSymbolValue" 2 #f () #f #f))
  (primitive simple-conditions
    (overload "Cleat.Scheme.Conditions" "SimpleConditions" 1 #f () #f #f))
  (primitive strict-mode?
    (overload "Cleat.Scheme.Builtins" "StrictMode" 0 #f () #f #f)
    (overload "Cleat.Scheme.Builtins" "StrictMode" 1 #f () #f #f))
  (primitive string->bytevector
    (overload "Cleat.Scheme.IO" "StringToByteVector" 2 #f () #f #f))
  (primitive string->number
    (overload "Cleat.Scheme.Builtins" "StringToNumber" 1 #f (pure) #f #f)
    (overload "Cleat.Scheme.Builtins" "StringToNumber" 2 #f (pure) #f #f))
  (primitive string->symbol
    (overload "Cleat.Scheme.SymbolPrimitives" "StringToSymbol" 1 #f (pure) #f ((string) (symbol))))
  (primitive string->xml
    (overload "Cleat.Scheme.Builtins" "StringToXml" 1 #f () #f #f))
  (primitive string-split
    (overload "Cleat.Scheme.Builtins" "StringSplit" 1 #t () #f #f))
  (primitive symbol->string
    (overload "Cleat.Scheme.SymbolPrimitives" "SymbolToString" 1 #f (pure) #f ((symbol) (string))))
  (primitive symbol-bound?
    (overload "Cleat.Scheme.SymbolPrimitives" "IsSymbolBound" 1 #f () #f #f))
  (primitive symbol-value
    (overload "Cleat.Scheme.SymbolPrimitives" "SymbolValue" 1 #f () #f #f))
  (primitive target-host-type
    (overload "Cleat.Scheme.HostPrimitives" "TargetHostType" 0 #f () #f #f))
  (primitive time-it
    (overload "Cleat.Scheme.Builtins" "TimeIt" 2 #f () #f #f))
  (primitive ungensym
    (overload "Cleat.Scheme.SymbolPrimitives" "Ungensym" 1 #f (pure) #f #f))
  (primitive unspecified?
    (overload "Cleat.Scheme.Builtins" "IsUnspecified" 1 #f () #f #f))
  (primitive vector
    (overload "Cleat.Scheme.Builtins" "Vector" 0 #t (discard unrestricted) vector ((ptr ...) (vector))))
  (primitive vector-append
    (overload "Cleat.Scheme.Builtins" "VectorAppend" 0 #t () #f #f))
  (primitive vector-filter
    (overload "Cleat.Scheme.Builtins" "VectorFilter" 2 #f () #f #f))
  (primitive void
    (overload "Cleat.Scheme.Builtins" "Void" 0 #f (pure) void #f))
  (primitive with-clr-exception-handler
    (overload "Cleat.Scheme.Exceptions" "WithClrExceptionHandler" 2 #f () #f #f))
  (primitive with-timeout
    (overload "Cleat.Scheme.Builtins" "WithTimeout" 2 #f () #f #f))
  (special $and? and)   ; 短路与，参数为未求值表达式，结果为 CLR bool
  (special $break break)   ; 调用 Debugger.Break
  (special $or? or)   ; 短路或，参数为未求值表达式，结果为 CLR bool
  (special $throw throw)   ; 抛出参数（一个 .NET 异常对象）
  (special $try/catch try-catch)   ; ($try/catch body '类型 处理器 …)：按 CLR 类型捕获；类型 #f 即 SchemeException，处理器收到其条件（designs/15 §3）
  (special $try/finally try-finally)   ; ($try/finally body finalizer)：finalizer 在 finally 中求值
  (special apply apply)   ; 展开最后一个列表参数
  (special call-with-values call-with-values)   ; 消费者为字面 lambda 时可内联
  (special values values)   ; 在 Scheme 中定义（boot/exceptions.sls）；作为原语出现时编译为多值构造
)
