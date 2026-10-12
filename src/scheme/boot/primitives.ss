;;; 原语表：由 Cleat.PrimGen 从 Cleat.Scheme（[Primitive]）与 Cleat.Core（Primitives.Table，名字 cleat.kernel/…）生成，请勿手工修改。
;;; 设计：designs/02-runtime.md §5、designs/15 §1；IronScheme 03 §6
;;;
;;; (primitive 名字 重载 …)
;;;   重载 = (overload "类型全名" "方法名" 固定参数个数 剩余参数? (标志 …) 内联模板)
;;;   标志：pure（可常量折叠）、unsafe（不做类型检查）、allow-tail-call、inline（kernel 表：发射器有内联模板）；内联模板为名字或 #f
;;; (special 名字 种类)：编译器专门处理的原语；同名的 primitive 条目（若有）是非内联时的后备实现
(primitive-table 1
  (primitive $annotation-expression
    (overload "Cleat.Scheme.AnnotationPrimitives" "Expression" 1 #f (pure unsafe) #f))
  (primitive $annotation-source
    (overload "Cleat.Scheme.AnnotationPrimitives" "Source" 1 #f (pure unsafe) #f))
  (primitive $annotation-stripped
    (overload "Cleat.Scheme.AnnotationPrimitives" "Stripped" 1 #f (pure unsafe) #f))
  (primitive $annotation?
    (overload "Cleat.Scheme.AnnotationPrimitives" "IsAnnotation" 1 #f (pure unsafe) #f))
  (primitive $application-directory
    (overload "Cleat.Scheme.HostPrimitives" "ApplicationDirectory" 0 #f (unsafe) #f))
  (primitive $as-fixnum
    (overload "Cleat.Scheme.TypedPrimitives" "AsFixnum" 1 #f (unsafe) #f))
  (primitive $as-flonum
    (overload "Cleat.Scheme.TypedPrimitives" "AsFlonum" 1 #f (unsafe) #f))
  (primitive $assembly-file-id
    (overload "Cleat.Scheme.BootInterface" "AssemblyFileId" 1 #f (unsafe) #f))
  (primitive $bignum->bytes
    (overload "Cleat.Scheme.HostPrimitives" "BignumToBytes" 1 #f (pure unsafe) #f))
  (primitive $bignum->fixnum
    (overload "Cleat.Scheme.HostPrimitives" "BignumToFixnum" 1 #f (pure unsafe) #f))
  (primitive $bignum->flonum
    (overload "Cleat.Scheme.HostPrimitives" "BignumToFlonum" 1 #f (pure unsafe) #f))
  (primitive $bignum->string
    (overload "Cleat.Scheme.HostPrimitives" "BignumToString" 2 #f (pure unsafe) #f))
  (primitive $bignum-and
    (overload "Cleat.Scheme.HostPrimitives" "BignumAnd" 2 #f (pure unsafe) #f))
  (primitive $bignum-bit-count
    (overload "Cleat.Scheme.HostPrimitives" "BignumBitCount" 1 #f (pure unsafe) #f))
  (primitive $bignum-bit-length
    (overload "Cleat.Scheme.HostPrimitives" "BignumBitLength" 1 #f (pure unsafe) #f))
  (primitive $bignum-bit-set?
    (overload "Cleat.Scheme.HostPrimitives" "IsBignumBitSet" 2 #f (pure unsafe) #f))
  (primitive $bignum-expt
    (overload "Cleat.Scheme.HostPrimitives" "BignumExpt" 2 #f (pure unsafe) #f))
  (primitive $bignum-first-bit-set
    (overload "Cleat.Scheme.HostPrimitives" "BignumFirstBitSet" 1 #f (pure unsafe) #f))
  (primitive $bignum-fits-fixnum?
    (overload "Cleat.Scheme.HostPrimitives" "BignumFitsFixnum" 1 #f (pure unsafe) #f))
  (primitive $bignum-ior
    (overload "Cleat.Scheme.HostPrimitives" "BignumIor" 2 #f (pure unsafe) #f))
  (primitive $bignum-negative?
    (overload "Cleat.Scheme.HostPrimitives" "IsBignumNegative" 1 #f (pure unsafe) #f))
  (primitive $bignum-not
    (overload "Cleat.Scheme.HostPrimitives" "BignumNot" 1 #f (pure unsafe) #f))
  (primitive $bignum-quotient
    (overload "Cleat.Scheme.HostPrimitives" "BignumQuotient" 2 #f (pure unsafe) #f))
  (primitive $bignum-remainder
    (overload "Cleat.Scheme.HostPrimitives" "BignumRemainder" 2 #f (pure unsafe) #f))
  (primitive $bignum-reverse-bits
    (overload "Cleat.Scheme.HostPrimitives" "BignumReverseBits" 2 #f (pure unsafe) #f))
  (primitive $bignum-shift-left
    (overload "Cleat.Scheme.HostPrimitives" "BignumShiftLeft" 2 #f (pure unsafe) #f))
  (primitive $bignum-shift-right
    (overload "Cleat.Scheme.HostPrimitives" "BignumShiftRight" 2 #f (pure unsafe) #f))
  (primitive $bignum-xor
    (overload "Cleat.Scheme.HostPrimitives" "BignumXor" 2 #f (pure unsafe) #f))
  (primitive $bignum?
    (overload "Cleat.Scheme.HostPrimitives" "IsBignum" 1 #f (pure unsafe) #f))
  (primitive $binary-input-port?
    (overload "Cleat.Scheme.PortPrimitives" "IsBinaryInputPort" 1 #f (pure unsafe) #f))
  (primitive $binary-output-port?
    (overload "Cleat.Scheme.PortPrimitives" "IsBinaryOutputPort" 1 #f (pure unsafe) #f))
  (primitive $binary-port?
    (overload "Cleat.Scheme.PortPrimitives" "IsBinaryPort" 1 #f (pure unsafe) #f))
  (primitive $boolean?
    (overload "Cleat.Scheme.HostPrimitives" "IsBoolean" 1 #f (pure unsafe) #f))
  (primitive $boot-image-id
    (overload "Cleat.Scheme.BootInterface" "ImageId" 0 #f (unsafe) #f))
  (primitive $boot-interface-file
    (overload "Cleat.Scheme.BootInterface" "InterfaceFile" 0 #f (unsafe) #f))
  (primitive $buffered-port?
    (overload "Cleat.Scheme.PortPrimitives" "IsBufferedPort" 1 #f (pure unsafe) #f))
  (primitive $bytes->bignum
    (overload "Cleat.Scheme.HostPrimitives" "BytesToBignum" 1 #f (pure unsafe) #f))
  (primitive $bytes->double
    (overload "Cleat.Scheme.HostPrimitives" "BytesToDouble" 1 #f (pure unsafe) #f))
  (primitive $bytes->single
    (overload "Cleat.Scheme.HostPrimitives" "BytesToSingle" 1 #f (pure unsafe) #f))
  (primitive $bytes->sint
    (overload "Cleat.Scheme.HostPrimitives" "BytesToSint" 2 #f (pure unsafe) #f))
  (primitive $bytes->uint
    (overload "Cleat.Scheme.HostPrimitives" "BytesToUint" 2 #f (pure unsafe) #f))
  (primitive $bytevector-clone
    (overload "Cleat.Scheme.HostPrimitives" "BytevectorClone" 1 #f (unsafe) #f))
  (primitive $bytevector-copy!
    (overload "Cleat.Scheme.HostPrimitives" "BytevectorCopy" 5 #f (unsafe) #f))
  (primitive $bytevector-length
    (overload "Cleat.Scheme.HostPrimitives" "BytevectorLength" 1 #f (pure unsafe) #f))
  (primitive $bytevector-output-port-extract
    (overload "Cleat.Scheme.PortPrimitives" "BytevectorOutputPortExtract" 1 #f (unsafe) #f))
  (primitive $bytevector-ref
    (overload "Cleat.Scheme.Unsafe" "ByteVectorRef" 2 #f (pure unsafe) $bytevector-ref))
  (primitive $bytevector-reverse!
    (overload "Cleat.Scheme.HostPrimitives" "BytevectorReverse" 1 #f (unsafe) #f))
  (primitive $bytevector-set!
    (overload "Cleat.Scheme.Unsafe" "ByteVectorSet" 3 #f (unsafe) $bytevector-set!))
  (primitive $bytevector-truncate
    (overload "Cleat.Scheme.PortPrimitives" "BytevectorTruncate" 2 #f (unsafe) #f))
  (primitive $bytevector?
    (overload "Cleat.Scheme.HostPrimitives" "IsBytevector" 1 #f (pure unsafe) #f))
  (primitive $car
    (overload "Cleat.Scheme.Unsafe" "Car" 1 #f (pure unsafe) $car))
  (primitive $cdr
    (overload "Cleat.Scheme.Unsafe" "Cdr" 1 #f (pure unsafe) $cdr))
  (primitive $char->integer
    (overload "Cleat.Scheme.HostPrimitives" "CharToInteger" 1 #f (pure unsafe) #f))
  (primitive $char-alphabetic?
    (overload "Cleat.Scheme.HostPrimitives" "IsCharAlphabetic" 1 #f (pure unsafe) #f))
  (primitive $char-downcase
    (overload "Cleat.Scheme.HostPrimitives" "CharDowncase" 1 #f (pure unsafe) #f))
  (primitive $char-general-category
    (overload "Cleat.Scheme.HostPrimitives" "CharGeneralCategory" 1 #f (pure unsafe) #f))
  (primitive $char-numeric?
    (overload "Cleat.Scheme.HostPrimitives" "IsCharNumeric" 1 #f (pure unsafe) #f))
  (primitive $char-upcase
    (overload "Cleat.Scheme.HostPrimitives" "CharUpcase" 1 #f (pure unsafe) #f))
  (primitive $char-whitespace?
    (overload "Cleat.Scheme.HostPrimitives" "IsCharWhitespace" 1 #f (pure unsafe) #f))
  (primitive $char?
    (overload "Cleat.Scheme.HostPrimitives" "IsChar" 1 #f (pure unsafe) #f))
  (primitive $chars->string
    (overload "Cleat.Scheme.HostPrimitives" "CharsToString" 1 #f (unsafe) #f))
  (primitive $check-initialized
    (overload "Cleat.Scheme.Calls" "CheckInitialized" 2 #f () #f))
  (primitive $codec-get-bytes
    (overload "Cleat.Scheme.HostPrimitives" "CodecGetBytes" 2 #f (unsafe) #f))
  (primitive $codec-get-string
    (overload "Cleat.Scheme.HostPrimitives" "CodecGetString" 2 #f (unsafe) #f))
  (primitive $codec?
    (overload "Cleat.Scheme.PortPrimitives" "IsCodec" 1 #f (pure unsafe) #f))
  (primitive $compare-time
    (overload "Cleat.Scheme.HostPrimitives" "CompareTime" 2 #f (pure unsafe) #f))
  (primitive $complex-acos
    (overload "Cleat.Scheme.HostPrimitives" "ComplexAcos" 1 #f (pure unsafe) #f))
  (primitive $complex-asin
    (overload "Cleat.Scheme.HostPrimitives" "ComplexAsin" 1 #f (pure unsafe) #f))
  (primitive $complex-atan
    (overload "Cleat.Scheme.HostPrimitives" "ComplexAtan" 1 #f (pure unsafe) #f))
  (primitive $complex-cos
    (overload "Cleat.Scheme.HostPrimitives" "ComplexCos" 1 #f (pure unsafe) #f))
  (primitive $complex-cosh
    (overload "Cleat.Scheme.HostPrimitives" "ComplexCosh" 1 #f (pure unsafe) #f))
  (primitive $complex-exp
    (overload "Cleat.Scheme.HostPrimitives" "ComplexExp" 1 #f (pure unsafe) #f))
  (primitive $complex-expt
    (overload "Cleat.Scheme.HostPrimitives" "ComplexExpt" 2 #f (pure unsafe) #f))
  (primitive $complex-log
    (overload "Cleat.Scheme.HostPrimitives" "ComplexLog" 1 #f (pure unsafe) #f))
  (primitive $complex-sin
    (overload "Cleat.Scheme.HostPrimitives" "ComplexSin" 1 #f (pure unsafe) #f))
  (primitive $complex-sinh
    (overload "Cleat.Scheme.HostPrimitives" "ComplexSinh" 1 #f (pure unsafe) #f))
  (primitive $complex-sqrt
    (overload "Cleat.Scheme.HostPrimitives" "ComplexSqrt" 1 #f (pure unsafe) #f))
  (primitive $complex-tan
    (overload "Cleat.Scheme.HostPrimitives" "ComplexTan" 1 #f (pure unsafe) #f))
  (primitive $complex-tanh
    (overload "Cleat.Scheme.HostPrimitives" "ComplexTanh" 1 #f (pure unsafe) #f))
  (primitive $complexnum->rectnum
    (overload "Cleat.Scheme.HostPrimitives" "ComplexnumToRectnum" 1 #f (pure unsafe) #f))
  (primitive $complexnum-imag
    (overload "Cleat.Scheme.HostPrimitives" "ComplexnumImag" 1 #f (pure unsafe) #f))
  (primitive $complexnum-real
    (overload "Cleat.Scheme.HostPrimitives" "ComplexnumReal" 1 #f (pure unsafe) #f))
  (primitive $complexnum?
    (overload "Cleat.Scheme.HostPrimitives" "IsComplexnum" 1 #f (pure unsafe) #f))
  (primitive $compound-condition?
    (overload "Cleat.Scheme.HostPrimitives" "IsCompoundCondition" 1 #f (pure unsafe) #f))
  (primitive $condition?
    (overload "Cleat.Scheme.HostPrimitives" "IsCondition" 1 #f (pure unsafe) #f))
  (primitive $console-error-port
    (overload "Cleat.Scheme.PortPrimitives" "ConsoleError" 0 #f (unsafe) #f))
  (primitive $console-foreground-color
    (overload "Cleat.Scheme.HostPrimitives" "ConsoleForegroundColor" 0 #f (unsafe) #f))
  (primitive $console-input-port
    (overload "Cleat.Scheme.PortPrimitives" "ConsoleInput" 0 #f (unsafe) #f))
  (primitive $console-output-port
    (overload "Cleat.Scheme.PortPrimitives" "ConsoleOutput" 0 #f (unsafe) #f))
  (primitive $cpu-architecture
    (overload "Cleat.Scheme.HostPrimitives" "CpuArchitecture" 0 #f (pure unsafe) #f))
  (primitive $culture-country
    (overload "Cleat.Scheme.HostPrimitives" "CultureCountry" 0 #f (unsafe) #f))
  (primitive $culture-language
    (overload "Cleat.Scheme.HostPrimitives" "CultureLanguage" 0 #f (unsafe) #f))
  (primitive $culture-name
    (overload "Cleat.Scheme.HostPrimitives" "CultureName" 0 #f (unsafe) #f))
  (primitive $current-directory
    (overload "Cleat.Scheme.HostPrimitives" "CurrentDirectory" 0 #f (unsafe) #f))
  (primitive $current-handlers
    (overload "Cleat.Scheme.ExceptionHandlers" "Current" 0 #f (unsafe) #f))
  (primitive $custom-port-input
    (overload "Cleat.Scheme.PortPrimitives" "CustomPortInput" 1 #f (pure unsafe) #f))
  (primitive $custom-port-output
    (overload "Cleat.Scheme.PortPrimitives" "CustomPortOutput" 1 #f (pure unsafe) #f))
  (primitive $custom-textual-port?
    (overload "Cleat.Scheme.PortPrimitives" "IsCustomTextualPort" 1 #f (pure unsafe) #f))
  (primitive $delete-file
    (overload "Cleat.Scheme.HostPrimitives" "DeleteFile" 1 #f (unsafe) #f))
  (primitive $directory-create
    (overload "Cleat.Scheme.HostPrimitives" "DirectoryCreate" 1 #f (unsafe) #f))
  (primitive $directory-exists?
    (overload "Cleat.Scheme.HostPrimitives" "DirectoryExists" 1 #f (unsafe) #f))
  (primitive $directory-files
    (overload "Cleat.Scheme.HostPrimitives" "DirectoryFiles" 2 #f (unsafe) #f))
  (primitive $double->bytes
    (overload "Cleat.Scheme.HostPrimitives" "DoubleToBytes" 1 #f (pure unsafe) #f))
  (primitive $eq-hashtable?
    (overload "Cleat.Scheme.HostPrimitives" "IsEqHashtable" 1 #f (pure unsafe) #f))
  (primitive $exact-compare
    (overload "Cleat.Scheme.HostPrimitives" "ExactCompare" 2 #f (pure unsafe) #f))
  (primitive $exception->string
    (overload "Cleat.Scheme.HostPrimitives" "ExceptionToString" 1 #f (unsafe) #f))
  (primitive $exception-message
    (overload "Cleat.Scheme.HostPrimitives" "ExceptionMessage" 1 #f (unsafe) #f))
  (primitive $exception?
    (overload "Cleat.Scheme.HostPrimitives" "IsException" 1 #f (pure unsafe) #f))
  (primitive $exit
    (overload "Cleat.Scheme.HostPrimitives" "Exit" 1 #f (unsafe) #f))
  (primitive $file-exists?
    (overload "Cleat.Scheme.HostPrimitives" "FileExists" 1 #f (unsafe) #f))
  (primitive $file-last-write-time
    (overload "Cleat.Scheme.HostPrimitives" "FileLastWriteTime" 1 #f (unsafe) #f))
  (primitive $file-mtime
    (overload "Cleat.Scheme.HostPrimitives" "FileMtime" 1 #f (unsafe) #f))
  (primitive $fixnum->bignum
    (overload "Cleat.Scheme.HostPrimitives" "FixnumToBignum" 1 #f (pure unsafe) #f))
  (primitive $fixnum->flonum
    (overload "Cleat.Scheme.HostPrimitives" "FixnumToFlonum" 1 #f (pure unsafe) #f))
  (primitive $fixnum->string
    (overload "Cleat.Scheme.HostPrimitives" "FixnumToString" 2 #f (pure unsafe) #f))
  (primitive $fixnum?
    (overload "Cleat.Scheme.HostPrimitives" "IsFixnum" 1 #f (pure unsafe) #f))
  (primitive $fl*
    (overload "Cleat.Scheme.Unsafe" "FlMul" 2 #f (pure unsafe) $fl*))
  (primitive $fl+
    (overload "Cleat.Scheme.Unsafe" "FlAdd" 2 #f (pure unsafe) $fl+))
  (primitive $fl-
    (overload "Cleat.Scheme.Unsafe" "FlNeg" 1 #f (pure unsafe) $fl-)
    (overload "Cleat.Scheme.Unsafe" "FlSub" 2 #f (pure unsafe) $fl-))
  (primitive $fl/
    (overload "Cleat.Scheme.Unsafe" "FlDiv" 2 #f (pure unsafe) $fl/))
  (primitive $fl<=?
    (overload "Cleat.Scheme.Unsafe" "FlLe" 2 #f (pure unsafe) $fl<=?))
  (primitive $fl<?
    (overload "Cleat.Scheme.Unsafe" "FlLt" 2 #f (pure unsafe) $fl<?))
  (primitive $fl=?
    (overload "Cleat.Scheme.Unsafe" "FlEq" 2 #f (pure unsafe) $fl=?))
  (primitive $fl>=?
    (overload "Cleat.Scheme.Unsafe" "FlGe" 2 #f (pure unsafe) $fl>=?))
  (primitive $fl>?
    (overload "Cleat.Scheme.Unsafe" "FlGt" 2 #f (pure unsafe) $fl>?))
  (primitive $flabs
    (overload "Cleat.Scheme.HostPrimitives" "FlAbs" 1 #f (pure unsafe) #f))
  (primitive $flacos
    (overload "Cleat.Scheme.HostPrimitives" "FlAcos" 1 #f (pure unsafe) #f))
  (primitive $flasin
    (overload "Cleat.Scheme.HostPrimitives" "FlAsin" 1 #f (pure unsafe) #f))
  (primitive $flatan
    (overload "Cleat.Scheme.HostPrimitives" "FlAtan" 1 #f (pure unsafe) #f))
  (primitive $flatan2
    (overload "Cleat.Scheme.HostPrimitives" "FlAtan2" 2 #f (pure unsafe) #f))
  (primitive $flceiling
    (overload "Cleat.Scheme.HostPrimitives" "FlCeiling" 1 #f (pure unsafe) #f))
  (primitive $flcos
    (overload "Cleat.Scheme.HostPrimitives" "FlCos" 1 #f (pure unsafe) #f))
  (primitive $flcosh
    (overload "Cleat.Scheme.HostPrimitives" "FlCosh" 1 #f (pure unsafe) #f))
  (primitive $flexp
    (overload "Cleat.Scheme.HostPrimitives" "FlExp" 1 #f (pure unsafe) #f))
  (primitive $flexpt
    (overload "Cleat.Scheme.HostPrimitives" "FlExpt" 2 #f (pure unsafe) #f))
  (primitive $flfloor
    (overload "Cleat.Scheme.HostPrimitives" "FlFloor" 1 #f (pure unsafe) #f))
  (primitive $flinfinite?
    (overload "Cleat.Scheme.HostPrimitives" "IsFlInfinite" 1 #f (pure unsafe) #f))
  (primitive $fllog
    (overload "Cleat.Scheme.HostPrimitives" "FlLog" 1 #f (pure unsafe) #f))
  (primitive $fllog2
    (overload "Cleat.Scheme.HostPrimitives" "FlLog2" 2 #f (pure unsafe) #f))
  (primitive $flnan?
    (overload "Cleat.Scheme.HostPrimitives" "IsFlNan" 1 #f (pure unsafe) #f))
  (primitive $flonum->ratnum
    (overload "Cleat.Scheme.HostPrimitives" "FlonumToRatnum" 1 #f (pure unsafe) #f))
  (primitive $flonum?
    (overload "Cleat.Scheme.HostPrimitives" "IsFlonum" 1 #f (pure unsafe) #f))
  (primitive $flround
    (overload "Cleat.Scheme.HostPrimitives" "FlRound" 1 #f (pure unsafe) #f))
  (primitive $flsin
    (overload "Cleat.Scheme.HostPrimitives" "FlSin" 1 #f (pure unsafe) #f))
  (primitive $flsinh
    (overload "Cleat.Scheme.HostPrimitives" "FlSinh" 1 #f (pure unsafe) #f))
  (primitive $flsqrt
    (overload "Cleat.Scheme.HostPrimitives" "FlSqrt" 1 #f (pure unsafe) #f))
  (primitive $fltan
    (overload "Cleat.Scheme.HostPrimitives" "FlTan" 1 #f (pure unsafe) #f))
  (primitive $fltanh
    (overload "Cleat.Scheme.HostPrimitives" "FlTanh" 1 #f (pure unsafe) #f))
  (primitive $fltruncate
    (overload "Cleat.Scheme.HostPrimitives" "FlTruncate" 1 #f (pure unsafe) #f))
  (primitive $fx*
    (overload "Cleat.Scheme.Unsafe" "FxMul" 2 #f (pure unsafe) $fx*))
  (primitive $fx+
    (overload "Cleat.Scheme.Unsafe" "FxAdd" 2 #f (pure unsafe) $fx+))
  (primitive $fx-
    (overload "Cleat.Scheme.Unsafe" "FxNeg" 1 #f (pure unsafe) $fx-)
    (overload "Cleat.Scheme.Unsafe" "FxSub" 2 #f (pure unsafe) $fx-))
  (primitive $fx<=?
    (overload "Cleat.Scheme.Unsafe" "FxLe" 2 #f (pure unsafe) $fx<=?))
  (primitive $fx<?
    (overload "Cleat.Scheme.Unsafe" "FxLt" 2 #f (pure unsafe) $fx<?))
  (primitive $fx=?
    (overload "Cleat.Scheme.Unsafe" "FxEq" 2 #f (pure unsafe) $fx=?))
  (primitive $fx>=?
    (overload "Cleat.Scheme.Unsafe" "FxGe" 2 #f (pure unsafe) $fx>=?))
  (primitive $fx>?
    (overload "Cleat.Scheme.Unsafe" "FxGt" 2 #f (pure unsafe) $fx>?))
  (primitive $fxabs
    (overload "Cleat.Scheme.HostPrimitives" "FxAbs" 1 #f (pure unsafe) #f))
  (primitive $fxand
    (overload "Cleat.Scheme.Unsafe" "FxAnd" 2 #f (pure unsafe) $fxand))
  (primitive $fxarithmetic-shift-left
    (overload "Cleat.Scheme.Unsafe" "FxShl" 2 #f (pure unsafe) $fxarithmetic-shift-left))
  (primitive $fxarithmetic-shift-right
    (overload "Cleat.Scheme.Unsafe" "FxShr" 2 #f (pure unsafe) $fxarithmetic-shift-right))
  (primitive $fxdiv
    (overload "Cleat.Scheme.Unsafe" "FxDiv" 2 #f (pure unsafe) $fxdiv))
  (primitive $fxdiv0
    (overload "Cleat.Scheme.Unsafe" "FxDiv0" 2 #f (pure unsafe) $fxdiv0))
  (primitive $fxeven?
    (overload "Cleat.Scheme.Unsafe" "FxEven" 1 #f (pure unsafe) $fxeven?))
  (primitive $fxior
    (overload "Cleat.Scheme.Unsafe" "FxIor" 2 #f (pure unsafe) $fxior))
  (primitive $fxmod
    (overload "Cleat.Scheme.Unsafe" "FxMod" 2 #f (pure unsafe) $fxmod))
  (primitive $fxmod0
    (overload "Cleat.Scheme.Unsafe" "FxMod0" 2 #f (pure unsafe) $fxmod0))
  (primitive $fxnegative?
    (overload "Cleat.Scheme.Unsafe" "FxNegative" 1 #f (pure unsafe) $fxnegative?))
  (primitive $fxnot
    (overload "Cleat.Scheme.Unsafe" "FxNot" 1 #f (pure unsafe) $fxnot))
  (primitive $fxodd?
    (overload "Cleat.Scheme.Unsafe" "FxOdd" 1 #f (pure unsafe) $fxodd?))
  (primitive $fxpositive?
    (overload "Cleat.Scheme.Unsafe" "FxPositive" 1 #f (pure unsafe) $fxpositive?))
  (primitive $fxxor
    (overload "Cleat.Scheme.Unsafe" "FxXor" 2 #f (pure unsafe) $fxxor))
  (primitive $fxzero?
    (overload "Cleat.Scheme.Unsafe" "FxZero" 1 #f (pure unsafe) $fxzero?))
  (primitive $gc-collect
    (overload "Cleat.Scheme.HostPrimitives" "GcCollect" 0 #f (unsafe) #f))
  (primitive $gensym-realm
    (overload "Cleat.Scheme.SymbolPrimitives" "GensymRealm" 0 #f () #f)
    (overload "Cleat.Scheme.SymbolPrimitives" "GensymRealm" 1 #f () #f))
  (primitive $gensym-scope
    (overload "Cleat.Scheme.SymbolPrimitives" "GensymScope" 0 #f () #f)
    (overload "Cleat.Scheme.SymbolPrimitives" "GensymScope" 1 #f () #f))
  (primitive $get-environment-variable
    (overload "Cleat.Scheme.HostPrimitives" "GetEnvironmentVariable" 1 #f (unsafe) #f))
  (primitive $get-output-string
    (overload "Cleat.Scheme.PortPrimitives" "GetOutputString" 1 #f (unsafe) #f))
  (primitive $hashtable-clear!
    (overload "Cleat.Scheme.HostPrimitives" "HashtableClear" 1 #f (unsafe) #f))
  (primitive $hashtable-clone
    (overload "Cleat.Scheme.HostPrimitives" "HashtableClone" 1 #f (unsafe) #f))
  (primitive $hashtable-contains?
    (overload "Cleat.Scheme.HostPrimitives" "HashtableContains" 2 #f (unsafe) #f))
  (primitive $hashtable-count
    (overload "Cleat.Scheme.HostPrimitives" "HashtableCount" 1 #f (unsafe) #f))
  (primitive $hashtable-equivalence-function
    (overload "Cleat.Scheme.HostPrimitives" "HashtableEquivalenceFunction" 1 #f (pure unsafe) #f))
  (primitive $hashtable-from-null
    (overload "Cleat.Scheme.HostPrimitives" "HashtableFromNull" 1 #f (pure unsafe) #f))
  (primitive $hashtable-get
    (overload "Cleat.Scheme.HostPrimitives" "HashtableGet" 2 #f (unsafe) #f))
  (primitive $hashtable-hash-function
    (overload "Cleat.Scheme.HostPrimitives" "HashtableHashFunction" 1 #f (pure unsafe) #f))
  (primitive $hashtable-keys
    (overload "Cleat.Scheme.HostPrimitives" "HashtableKeys" 1 #f (unsafe) #f))
  (primitive $hashtable-mutable?
    (overload "Cleat.Scheme.HostPrimitives" "IsHashtableMutable" 1 #f (pure unsafe) #f))
  (primitive $hashtable-put!
    (overload "Cleat.Scheme.HostPrimitives" "HashtablePut" 3 #f (unsafe) #f))
  (primitive $hashtable-readonly-copy
    (overload "Cleat.Scheme.HostPrimitives" "HashtableReadOnlyCopy" 1 #f (unsafe) #f))
  (primitive $hashtable-remove!
    (overload "Cleat.Scheme.HostPrimitives" "HashtableRemove" 2 #f (unsafe) #f))
  (primitive $hashtable-to-null
    (overload "Cleat.Scheme.HostPrimitives" "HashtableToNull" 1 #f (pure unsafe) #f))
  (primitive $hashtable?
    (overload "Cleat.Scheme.HostPrimitives" "IsHashtable" 1 #f (pure unsafe) #f))
  (primitive $immutable-string?
    (overload "Cleat.Scheme.HostPrimitives" "IsImmutableString" 1 #f (pure unsafe) #f))
  (primitive $inexact-compare
    (overload "Cleat.Scheme.HostPrimitives" "InexactCompare" 2 #f (pure unsafe) #f))
  (primitive $input-port?
    (overload "Cleat.Scheme.PortPrimitives" "IsInputPort" 1 #f (pure unsafe) #f))
  (primitive $integer->char
    (overload "Cleat.Scheme.HostPrimitives" "IntegerToChar" 1 #f (pure unsafe) #f))
  (primitive $latin-1-codec
    (overload "Cleat.Scheme.PortPrimitives" "Latin1Codec" 0 #f (unsafe) #f))
  (primitive $library-interface-files
    (overload "Cleat.Scheme.BootInterface" "LibraryInterfaceFiles" 0 #f (unsafe) #f))
  (primitive $list->typed-vector
    (overload "Cleat.Scheme.HostPrimitives" "ListToTypedVector" 2 #f (unsafe) #f))
  (primitive $list->vector
    (overload "Cleat.Scheme.HostPrimitives" "ListToVector" 1 #f (unsafe) #f))
  (primitive $list-copy
    (overload "Cleat.Scheme.HostPrimitives" "ListCopy" 1 #f (unsafe) #f))
  (primitive $machine-name
    (overload "Cleat.Scheme.HostPrimitives" "MachineName" 0 #f (unsafe) #f))
  (primitive $make-bytevector
    (overload "Cleat.Scheme.HostPrimitives" "MakeBytevector" 1 #f (unsafe) #f))
  (primitive $make-complexnum
    (overload "Cleat.Scheme.HostPrimitives" "MakeComplexnum" 2 #f (pure unsafe) #f))
  (primitive $make-custom-binary-input-port
    (overload "Cleat.Scheme.PortPrimitives" "MakeCustomBinaryInputPort" 5 #f (unsafe) #f))
  (primitive $make-custom-binary-input/output-port
    (overload "Cleat.Scheme.PortPrimitives" "MakeCustomBinaryInputOutputPort" 6 #f (unsafe) #f))
  (primitive $make-custom-binary-output-port
    (overload "Cleat.Scheme.PortPrimitives" "MakeCustomBinaryOutputPort" 5 #f (unsafe) #f))
  (primitive $make-custom-textual-input-port
    (overload "Cleat.Scheme.PortPrimitives" "MakeCustomTextualInputPort" 5 #f (unsafe) #f))
  (primitive $make-custom-textual-input/output-port
    (overload "Cleat.Scheme.PortPrimitives" "MakeCustomTextualInputOutputPort" 6 #f (unsafe) #f))
  (primitive $make-custom-textual-output-port
    (overload "Cleat.Scheme.PortPrimitives" "MakeCustomTextualOutputPort" 5 #f (unsafe) #f))
  (primitive $make-eq-hashtable
    (overload "Cleat.Scheme.HostPrimitives" "MakeEqHashtable" 1 #f (unsafe) #f))
  (primitive $make-guid
    (overload "Cleat.Scheme.HostPrimitives" "MakeGuid" 0 #f (unsafe) #f))
  (primitive $make-hashtable
    (overload "Cleat.Scheme.HostPrimitives" "MakeHashtable" 3 #f (unsafe) #f))
  (primitive $make-multiple-values
    (overload "Cleat.Scheme.HostPrimitives" "MakeMultipleValues" 1 #f (unsafe) #f))
  (primitive $make-param-slot
    (overload "Cleat.Scheme.Parameters" "MakeSlot" 1 #f (unsafe) #f))
  (primitive $make-random
    (overload "Cleat.Scheme.HostPrimitives" "MakeRandom" 0 #f (unsafe) #f))
  (primitive $make-rectnum
    (overload "Cleat.Scheme.HostPrimitives" "MakeRectnum" 2 #f (pure unsafe) #f))
  (primitive $make-semaphore
    (overload "Cleat.Scheme.HostPrimitives" "MakeSemaphore" 1 #f (unsafe) #f))
  (primitive $make-string
    (overload "Cleat.Scheme.HostPrimitives" "MakeString" 2 #f (unsafe) #f))
  (primitive $make-transcoder
    (overload "Cleat.Scheme.PortPrimitives" "MakeTranscoder" 3 #f (unsafe) #f))
  (primitive $make-typed-vector
    (overload "Cleat.Scheme.HostPrimitives" "MakeTypedVector" 2 #f (unsafe) #f))
  (primitive $make-utf16-codec
    (overload "Cleat.Scheme.HostPrimitives" "MakeUtf16Codec" 1 #f (unsafe) #f))
  (primitive $make-utf32-codec
    (overload "Cleat.Scheme.HostPrimitives" "MakeUtf32Codec" 1 #f (unsafe) #f))
  (primitive $make-vector
    (overload "Cleat.Scheme.HostPrimitives" "MakeVector" 1 #f (unsafe) #f))
  (primitive $multiple-values?
    (overload "Cleat.Scheme.HostPrimitives" "IsMultipleValues" 1 #f (pure unsafe) #f))
  (primitive $mutable-string?
    (overload "Cleat.Scheme.HostPrimitives" "IsMutableString" 1 #f (pure unsafe) #f))
  (primitive $native-eol-style
    (overload "Cleat.Scheme.PortPrimitives" "NativeEolStyle" 0 #f (pure unsafe) #f))
  (primitive $native-transcoder
    (overload "Cleat.Scheme.PortPrimitives" "NativeTranscoder" 0 #f (pure unsafe) #f))
  (primitive $object->string
    (overload "Cleat.Scheme.HostPrimitives" "ObjectToString" 1 #f (unsafe) #f))
  (primitive $object-hash
    (overload "Cleat.Scheme.HostPrimitives" "ObjectHash" 1 #f (pure unsafe) #f))
  (primitive $open-bytevector-input-port
    (overload "Cleat.Scheme.PortPrimitives" "OpenBytevectorInputPort" 1 #f (unsafe) #f))
  (primitive $open-bytevector-output-port
    (overload "Cleat.Scheme.PortPrimitives" "OpenBytevectorOutputPort" 0 #f (unsafe) #f))
  (primitive $open-file-input-port
    (overload "Cleat.Scheme.PortPrimitives" "OpenFileInputPort" 2 #f (unsafe) #f))
  (primitive $open-input-file
    (overload "Cleat.Scheme.PortPrimitives" "OpenInputFile" 1 #f (unsafe) #f))
  (primitive $open-output-file
    (overload "Cleat.Scheme.PortPrimitives" "OpenOutputFile" 2 #f (unsafe) #f))
  (primitive $open-output-string
    (overload "Cleat.Scheme.PortPrimitives" "OpenOutputString" 0 #f (unsafe) #f))
  (primitive $open-string-input-port
    (overload "Cleat.Scheme.PortPrimitives" "OpenStringInputPort" 1 #f (unsafe) #f))
  (primitive $os-description
    (overload "Cleat.Scheme.HostPrimitives" "OsDescription" 0 #f (pure unsafe) #f))
  (primitive $os-name
    (overload "Cleat.Scheme.HostPrimitives" "OsName" 0 #f (pure unsafe) #f))
  (primitive $os-windows?
    (overload "Cleat.Scheme.HostPrimitives" "IsWindows" 0 #f (pure unsafe) #f))
  (primitive $output-port?
    (overload "Cleat.Scheme.PortPrimitives" "IsOutputPort" 1 #f (pure unsafe) #f))
  (primitive $param-ref
    (overload "Cleat.Scheme.Parameters" "Ref" 1 #f (unsafe) #f))
  (primitive $param-set!
    (overload "Cleat.Scheme.Parameters" "Set" 2 #f (unsafe) #f))
  (primitive $path-change-extension
    (overload "Cleat.Scheme.HostPrimitives" "PathChangeExtension" 2 #f (pure unsafe) #f))
  (primitive $path-combine
    (overload "Cleat.Scheme.HostPrimitives" "PathCombine" 2 #f (pure unsafe) #f))
  (primitive $path-directory-name
    (overload "Cleat.Scheme.HostPrimitives" "PathDirectoryName" 1 #f (pure unsafe) #f))
  (primitive $path-extension
    (overload "Cleat.Scheme.HostPrimitives" "PathExtension" 1 #f (pure unsafe) #f))
  (primitive $path-file-name
    (overload "Cleat.Scheme.HostPrimitives" "PathFileName" 1 #f (pure unsafe) #f))
  (primitive $path-full
    (overload "Cleat.Scheme.HostPrimitives" "PathFull" 1 #f (unsafe) #f))
  (primitive $path-trim-separator
    (overload "Cleat.Scheme.HostPrimitives" "PathTrimSeparator" 1 #f (pure unsafe) #f))
  (primitive $plain-string?
    (overload "Cleat.Scheme.HostPrimitives" "IsPlainString" 1 #f (pure unsafe) #f))
  (primitive $port-close-input
    (overload "Cleat.Scheme.PortPrimitives" "CloseInput" 1 #f (unsafe) #f))
  (primitive $port-close-output
    (overload "Cleat.Scheme.PortPrimitives" "CloseOutput" 1 #f (unsafe) #f))
  (primitive $port-flush
    (overload "Cleat.Scheme.PortPrimitives" "Flush" 1 #f (unsafe) #f))
  (primitive $port-has-position?
    (overload "Cleat.Scheme.PortPrimitives" "HasPosition" 1 #f (unsafe) #f))
  (primitive $port-has-set-position?
    (overload "Cleat.Scheme.PortPrimitives" "HasSetPosition" 1 #f (unsafe) #f))
  (primitive $port-peek-char
    (overload "Cleat.Scheme.PortPrimitives" "PeekChar" 1 #f (unsafe) #f))
  (primitive $port-position
    (overload "Cleat.Scheme.PortPrimitives" "Position" 1 #f (unsafe) #f))
  (primitive $port-read-all
    (overload "Cleat.Scheme.PortPrimitives" "ReadAll" 1 #f (unsafe) #f))
  (primitive $port-read-byte
    (overload "Cleat.Scheme.PortPrimitives" "ReadByte" 1 #f (unsafe) #f))
  (primitive $port-read-bytes
    (overload "Cleat.Scheme.PortPrimitives" "ReadBytes" 4 #f (unsafe) #f))
  (primitive $port-read-char
    (overload "Cleat.Scheme.PortPrimitives" "ReadChar" 1 #f (unsafe) #f))
  (primitive $port-read-line
    (overload "Cleat.Scheme.PortPrimitives" "ReadLine" 1 #f (unsafe) #f))
  (primitive $port-read-string
    (overload "Cleat.Scheme.PortPrimitives" "ReadString" 2 #f (unsafe) #f))
  (primitive $port-set-position!
    (overload "Cleat.Scheme.PortPrimitives" "SetPosition" 2 #f (unsafe) #f))
  (primitive $port-transcoder
    (overload "Cleat.Scheme.PortPrimitives" "PortTranscoder" 1 #f (unsafe) #f))
  (primitive $port-write-byte
    (overload "Cleat.Scheme.PortPrimitives" "WriteByte" 2 #f (unsafe) #f))
  (primitive $port-write-bytes
    (overload "Cleat.Scheme.PortPrimitives" "WriteBytes" 4 #f (unsafe) #f))
  (primitive $port-write-char
    (overload "Cleat.Scheme.PortPrimitives" "WriteChar" 2 #f (unsafe) #f))
  (primitive $port-write-string
    (overload "Cleat.Scheme.PortPrimitives" "WriteString" 2 #f (unsafe) #f))
  (primitive $procedure-arity
    (overload "Cleat.Scheme.HostPrimitives" "ProcedureArity" 1 #f (pure unsafe) #f))
  (primitive $procedure-form
    (overload "Cleat.Scheme.HostPrimitives" "ProcedureForm" 1 #f (pure unsafe) #f))
  (primitive $procedure-name
    (overload "Cleat.Scheme.HostPrimitives" "ProcedureName" 1 #f (pure unsafe) #f))
  (primitive $procedure?
    (overload "Cleat.Scheme.HostPrimitives" "IsProcedure" 1 #f (pure unsafe) #f))
  (primitive $program-failed
    (overload "Cleat.Scheme.HostPrimitives" "ProgramFailed" 0 #f (unsafe) #f))
  (primitive $raise-clr-exception
    (overload "Cleat.Scheme.Exceptions" "RaiseClrException" 1 #f (unsafe) #f))
  (primitive $random-next
    (overload "Cleat.Scheme.HostPrimitives" "RandomNext" 2 #f (unsafe) #f))
  (primitive $ratnum->flonum
    (overload "Cleat.Scheme.HostPrimitives" "RatnumToFlonum" 1 #f (pure unsafe) #f))
  (primitive $ratnum-denominator
    (overload "Cleat.Scheme.HostPrimitives" "RatnumDenominator" 1 #f (pure unsafe) #f))
  (primitive $ratnum-numerator
    (overload "Cleat.Scheme.HostPrimitives" "RatnumNumerator" 1 #f (pure unsafe) #f))
  (primitive $ratnum?
    (overload "Cleat.Scheme.HostPrimitives" "IsRatnum" 1 #f (pure unsafe) #f))
  (primitive $rcd-parent
    (overload "Cleat.Scheme.HostPrimitives" "RcdParent" 1 #f (pure unsafe) #f))
  (primitive $rcd-protocol
    (overload "Cleat.Scheme.HostPrimitives" "RcdProtocol" 1 #f (pure unsafe) #f))
  (primitive $rcd-rtd
    (overload "Cleat.Scheme.HostPrimitives" "RcdRtd" 1 #f (pure unsafe) #f))
  (primitive $rcd?
    (overload "Cleat.Scheme.HostPrimitives" "IsRcd" 1 #f (pure unsafe) #f))
  (primitive $real-compare
    (overload "Cleat.Scheme.HostPrimitives" "RealCompare" 2 #f (pure unsafe) #f))
  (primitive $record-field-ref
    (overload "Cleat.Scheme.Records" "RecordFieldRef" 3 #f (unsafe) #f))
  (primitive $record-field-set!
    (overload "Cleat.Scheme.Records" "RecordFieldSet" 4 #f (unsafe) #f))
  (primitive $record-fields
    (overload "Cleat.Scheme.HostPrimitives" "RecordFields" 1 #f (pure unsafe) #f))
  (primitive $record-instance?
    (overload "Cleat.Scheme.Records" "RecordInstance" 2 #f (unsafe) #f))
  (primitive $record-rtd
    (overload "Cleat.Scheme.HostPrimitives" "RecordRtd" 1 #f (pure unsafe) #f))
  (primitive $record?
    (overload "Cleat.Scheme.HostPrimitives" "IsRecord" 1 #f (pure unsafe) #f))
  (primitive $rectnum->complexnum
    (overload "Cleat.Scheme.HostPrimitives" "RectnumToComplexnum" 1 #f (pure unsafe) #f))
  (primitive $rectnum-imag
    (overload "Cleat.Scheme.HostPrimitives" "RectnumImag" 1 #f (pure unsafe) #f))
  (primitive $rectnum-real
    (overload "Cleat.Scheme.HostPrimitives" "RectnumReal" 1 #f (pure unsafe) #f))
  (primitive $rectnum?
    (overload "Cleat.Scheme.HostPrimitives" "IsRectnum" 1 #f (pure unsafe) #f))
  (primitive $rtd-field-mutable?
    (overload "Cleat.Scheme.HostPrimitives" "IsRtdFieldMutable" 2 #f (pure unsafe) #f))
  (primitive $rtd-field-names
    (overload "Cleat.Scheme.HostPrimitives" "RtdFieldNames" 1 #f (pure unsafe) #f))
  (primitive $rtd-generative?
    (overload "Cleat.Scheme.HostPrimitives" "IsRtdGenerative" 1 #f (pure unsafe) #f))
  (primitive $rtd-name
    (overload "Cleat.Scheme.HostPrimitives" "RtdName" 1 #f (pure unsafe) #f))
  (primitive $rtd-opaque?
    (overload "Cleat.Scheme.HostPrimitives" "IsRtdOpaque" 1 #f (pure unsafe) #f))
  (primitive $rtd-parent
    (overload "Cleat.Scheme.HostPrimitives" "RtdParent" 1 #f (pure unsafe) #f))
  (primitive $rtd-sealed?
    (overload "Cleat.Scheme.HostPrimitives" "IsRtdSealed" 1 #f (pure unsafe) #f))
  (primitive $rtd-uid
    (overload "Cleat.Scheme.HostPrimitives" "RtdUid" 1 #f (pure unsafe) #f))
  (primitive $rtd?
    (overload "Cleat.Scheme.HostPrimitives" "IsRtd" 1 #f (pure unsafe) #f))
  (primitive $runtime-hooks-missing
    (overload "Cleat.Scheme.RuntimeHooks" "Missing" 0 #f () #f))
  (primitive $semaphore-release!
    (overload "Cleat.Scheme.HostPrimitives" "SemaphoreRelease" 1 #f (unsafe) #f))
  (primitive $semaphore-wait!
    (overload "Cleat.Scheme.HostPrimitives" "SemaphoreWait" 1 #f (unsafe) #f))
  (primitive $set-car!
    (overload "Cleat.Scheme.HostPrimitives" "SetCar" 2 #f (unsafe) #f))
  (primitive $set-cdr!
    (overload "Cleat.Scheme.HostPrimitives" "SetCdr" 2 #f (unsafe) #f))
  (primitive $set-compile-target!
    (overload "Cleat.Scheme.HostPrimitives" "SetCompileTarget" 1 #f (unsafe) #f))
  (primitive $set-console-foreground-color!
    (overload "Cleat.Scheme.HostPrimitives" "SetConsoleForegroundColor" 1 #f (unsafe) #f))
  (primitive $set-current-directory!
    (overload "Cleat.Scheme.HostPrimitives" "SetCurrentDirectory" 1 #f (unsafe) #f))
  (primitive $set-current-handlers!
    (overload "Cleat.Scheme.ExceptionHandlers" "Set" 1 #f (unsafe) #f))
  (primitive $set-runtime-hook!
    (overload "Cleat.Scheme.RuntimeHooks" "SetRuntimeHook" 2 #f () #f))
  (primitive $single->bytes
    (overload "Cleat.Scheme.HostPrimitives" "SingleToBytes" 1 #f (pure unsafe) #f))
  (primitive $sint->bytes
    (overload "Cleat.Scheme.HostPrimitives" "SintToBytes" 2 #f (pure unsafe) #f))
  (primitive $stack-trace
    (overload "Cleat.Scheme.HostPrimitives" "StackTrace" 0 #f (unsafe) #f))
  (primitive $standard-error-port
    (overload "Cleat.Scheme.PortPrimitives" "StandardError" 0 #f (unsafe) #f))
  (primitive $standard-input-port
    (overload "Cleat.Scheme.PortPrimitives" "StandardInput" 0 #f (unsafe) #f))
  (primitive $standard-output-port
    (overload "Cleat.Scheme.PortPrimitives" "StandardOutput" 0 #f (unsafe) #f))
  (primitive $start-thread
    (overload "Cleat.Scheme.HostPrimitives" "StartThread" 1 #f (unsafe) #f))
  (primitive $string->immutable
    (overload "Cleat.Scheme.HostPrimitives" "StringToImmutable" 1 #f (unsafe) #f))
  (primitive $string->list
    (overload "Cleat.Scheme.HostPrimitives" "StringToList" 1 #f (unsafe) #f))
  (primitive $string->symbol
    (overload "Cleat.Scheme.HostPrimitives" "StringToSymbol" 1 #f (pure unsafe) #f))
  (primitive $string-append
    (overload "Cleat.Scheme.HostPrimitives" "StringAppend" 1 #f (unsafe) #f))
  (primitive $string-ci-hash
    (overload "Cleat.Scheme.HostPrimitives" "StringCiHash" 1 #f (pure unsafe) #f))
  (primitive $string-compare
    (overload "Cleat.Scheme.HostPrimitives" "StringCompare" 2 #f (pure unsafe) #f))
  (primitive $string-copy
    (overload "Cleat.Scheme.HostPrimitives" "StringCopy" 1 #f (unsafe) #f))
  (primitive $string-fill!
    (overload "Cleat.Scheme.HostPrimitives" "StringFill" 2 #f (unsafe) #f))
  (primitive $string-format
    (overload "Cleat.Scheme.HostPrimitives" "StringFormat" 2 #f (unsafe) #f))
  (primitive $string-hash
    (overload "Cleat.Scheme.HostPrimitives" "StringHash" 1 #f (pure unsafe) #f))
  (primitive $string-length
    (overload "Cleat.Scheme.HostPrimitives" "StringLength" 1 #f (pure unsafe) #f))
  (primitive $string-lower
    (overload "Cleat.Scheme.HostPrimitives" "StringLower" 1 #f (pure unsafe) #f))
  (primitive $string-mutable?
    (overload "Cleat.Scheme.HostPrimitives" "IsStringMutable" 1 #f (pure unsafe) #f))
  (primitive $string-normalize
    (overload "Cleat.Scheme.HostPrimitives" "StringNormalize" 2 #f (pure unsafe) #f))
  (primitive $string-ref
    (overload "Cleat.Scheme.HostPrimitives" "StringRef" 2 #f (pure unsafe) #f))
  (primitive $string-replace
    (overload "Cleat.Scheme.HostPrimitives" "StringReplace" 3 #f (pure unsafe) #f))
  (primitive $string-set!
    (overload "Cleat.Scheme.HostPrimitives" "StringSet" 3 #f (unsafe) #f))
  (primitive $substring
    (overload "Cleat.Scheme.HostPrimitives" "Substring" 3 #f (unsafe) #f))
  (primitive $symbol-name
    (overload "Cleat.Scheme.HostPrimitives" "SymbolName" 1 #f (pure unsafe) #f))
  (primitive $symbol-value-set!
    (overload "Cleat.Scheme.HostPrimitives" "SymbolValueSet" 2 #f (unsafe) #f))
  (primitive $symbol?
    (overload "Cleat.Scheme.HostPrimitives" "IsSymbol" 1 #f (pure unsafe) #f))
  (primitive $system-exception?
    (overload "Cleat.Scheme.HostPrimitives" "IsSystemException" 1 #f (pure unsafe) #f))
  (primitive $textual-port?
    (overload "Cleat.Scheme.PortPrimitives" "IsTextualPort" 1 #f (pure unsafe) #f))
  (primitive $timezone-offset-seconds
    (overload "Cleat.Scheme.HostPrimitives" "TimezoneOffsetSeconds" 0 #f (unsafe) #f))
  (primitive $transcoded-port
    (overload "Cleat.Scheme.PortPrimitives" "TranscodedPort" 2 #f (unsafe) #f))
  (primitive $transcoder-codec
    (overload "Cleat.Scheme.PortPrimitives" "TranscoderCodec" 1 #f (pure unsafe) #f))
  (primitive $transcoder-eol-style
    (overload "Cleat.Scheme.PortPrimitives" "TranscoderEolStyle" 1 #f (pure unsafe) #f))
  (primitive $transcoder-error-handling-mode
    (overload "Cleat.Scheme.PortPrimitives" "TranscoderHandlingMode" 1 #f (pure unsafe) #f))
  (primitive $transcoder?
    (overload "Cleat.Scheme.PortPrimitives" "IsTranscoder" 1 #f (pure unsafe) #f))
  (primitive $type-name
    (overload "Cleat.Scheme.HostPrimitives" "TypeName" 1 #f (pure unsafe) #f))
  (primitive $type-of
    (overload "Cleat.Scheme.HostPrimitives" "TypeOf" 1 #f (pure unsafe) #f))
  (primitive $type?
    (overload "Cleat.Scheme.HostPrimitives" "IsType" 1 #f (pure unsafe) #f))
  (primitive $typed-vector?
    (overload "Cleat.Scheme.HostPrimitives" "IsTypedVector" 2 #f (pure unsafe) #f))
  (primitive $uint->bytes
    (overload "Cleat.Scheme.HostPrimitives" "UintToBytes" 2 #f (pure unsafe) #f))
  (primitive $uninitialized
    (overload "Cleat.Scheme.Calls" "UninitializedValue" 0 #f () #f))
  (primitive $utf-16-codec
    (overload "Cleat.Scheme.PortPrimitives" "Utf16Codec" 0 #f (unsafe) #f))
  (primitive $utf-8-codec
    (overload "Cleat.Scheme.PortPrimitives" "Utf8Codec" 0 #f (unsafe) #f))
  (primitive $values-count
    (overload "Cleat.Scheme.ValuesPrimitives" "Count" 1 #f (pure unsafe) #f))
  (primitive $values-ref
    (overload "Cleat.Scheme.ValuesPrimitives" "Ref" 2 #f (pure unsafe) #f))
  (primitive $values-tail
    (overload "Cleat.Scheme.ValuesPrimitives" "Tail" 2 #f (pure unsafe) #f))
  (primitive $valuetype-vector?
    (overload "Cleat.Scheme.HostPrimitives" "IsValueTypeVector" 1 #f (pure unsafe) #f))
  (primitive $vector->list
    (overload "Cleat.Scheme.HostPrimitives" "VectorToList" 1 #f (unsafe) #f))
  (primitive $vector-clone
    (overload "Cleat.Scheme.HostPrimitives" "VectorClone" 1 #f (unsafe) #f))
  (primitive $vector-index-of
    (overload "Cleat.Scheme.HostPrimitives" "VectorIndexOf" 2 #f (unsafe) #f))
  (primitive $vector-length
    (overload "Cleat.Scheme.HostPrimitives" "VectorLength" 1 #f (pure unsafe) #f))
  (primitive $vector-ref
    (overload "Cleat.Scheme.Unsafe" "VectorRef" 2 #f (pure unsafe) $vector-ref))
  (primitive $vector-reverse!
    (overload "Cleat.Scheme.HostPrimitives" "VectorReverse" 1 #f (unsafe) #f))
  (primitive $vector-set!
    (overload "Cleat.Scheme.Unsafe" "VectorSet" 3 #f (unsafe) $vector-set!))
  (primitive $vector-sort!
    (overload "Cleat.Scheme.HostPrimitives" "VectorSort" 2 #f (unsafe) #f))
  (primitive $vector?
    (overload "Cleat.Scheme.HostPrimitives" "IsVector" 1 #f (pure unsafe) #f))
  (primitive apply
    (overload "Cleat.Scheme.Control" "Apply" 1 #t (allow-tail-call) #f))
  (primitive bignum-sqrt
    (overload "Cleat.Scheme.Builtins" "SqrtBigInteger" 1 #f (pure) #f))
  (primitive bignum-sqrt-exact
    (overload "Cleat.Scheme.Builtins" "ExactSqrtBigInteger" 1 #f (pure) #f))
  (primitive bytevector->string
    (overload "Cleat.Scheme.IO" "ByteVectorToString" 2 #f () #f))
  (primitive call-with-return
    (overload "Cleat.Scheme.Control" "CallWithReturn" 1 #f () #f))
  (primitive call-with-values
    (overload "Cleat.Scheme.Control" "CallWithValues" 2 #f (allow-tail-call) #f))
  (primitive car
    (overload "Cleat.Scheme.Builtins" "Car" 1 #f () car))
  (primitive cdr
    (overload "Cleat.Scheme.Builtins" "Cdr" 1 #f () cdr))
  (primitive cleat-scheme-runtime
    (overload "Cleat.Scheme.Builtins" "GetRuntime" 0 #f () #f))
  (primitive cleat-scheme-version
    (overload "Cleat.Scheme.Builtins" "Version" 0 #f () #f))
  (primitive cleat.kernel/add
    (overload "clojure.lang.Numbers" "add" 2 #f (pure inline) #f))
  (primitive cleat.kernel/assoc
    (overload "clojure.lang.RT" "assoc" 3 #f () #f))
  (primitive cleat.kernel/bit-and
    (overload "clojure.lang.Numbers" "and" 2 #f (pure) #f))
  (primitive cleat.kernel/bit-or
    (overload "clojure.lang.Numbers" "or" 2 #f (pure) #f))
  (primitive cleat.kernel/bit-shift-left
    (overload "clojure.lang.Numbers" "shiftLeft" 2 #f (pure) #f))
  (primitive cleat.kernel/bit-shift-right
    (overload "clojure.lang.Numbers" "shiftRight" 2 #f (pure) #f))
  (primitive cleat.kernel/bit-xor
    (overload "clojure.lang.Numbers" "xor" 2 #f (pure) #f))
  (primitive cleat.kernel/boolean-cast
    (overload "clojure.lang.RT" "booleanCast" 1 #f (pure inline) #f))
  (primitive cleat.kernel/char-cast
    (overload "clojure.lang.RT" "charCast" 1 #f (pure) #f))
  (primitive cleat.kernel/class-of
    (overload "clojure.lang.Util" "classOf" 1 #f (pure) #f))
  (primitive cleat.kernel/compare
    (overload "clojure.lang.Util" "compare" 2 #f (pure) #f))
  (primitive cleat.kernel/conj
    (overload "clojure.lang.RT" "conj" 2 #f () #f))
  (primitive cleat.kernel/cons
    (overload "clojure.lang.RT" "cons" 2 #f () #f))
  (primitive cleat.kernel/count
    (overload "clojure.lang.RT" "count" 1 #f (inline) #f))
  (primitive cleat.kernel/dec
    (overload "clojure.lang.Numbers" "dec" 1 #f (pure inline) #f))
  (primitive cleat.kernel/divide
    (overload "clojure.lang.Numbers" "divide" 2 #f (pure) #f))
  (primitive cleat.kernel/double-cast
    (overload "clojure.lang.RT" "doubleCast" 1 #f (pure) #f))
  (primitive cleat.kernel/equals
    (overload "clojure.lang.Util" "equals" 2 #f (pure) #f))
  (primitive cleat.kernel/equiv
    (overload "clojure.lang.Util" "equiv" 2 #f (pure inline) #f))
  (primitive cleat.kernel/first
    (overload "clojure.lang.RT" "first" 1 #f (inline) #f))
  (primitive cleat.kernel/get
    (overload "clojure.lang.RT" "get" 2 #f (inline) #f))
  (primitive cleat.kernel/get3
    (overload "clojure.lang.RT" "get" 3 #f () #f))
  (primitive cleat.kernel/gt
    (overload "clojure.lang.Numbers" "gt" 2 #f (pure inline) #f))
  (primitive cleat.kernel/gte
    (overload "clojure.lang.Numbers" "gte" 2 #f (pure inline) #f))
  (primitive cleat.kernel/hash
    (overload "clojure.lang.Util" "hash" 1 #f (pure) #f))
  (primitive cleat.kernel/hasheq
    (overload "clojure.lang.Util" "hasheq" 1 #f (pure) #f))
  (primitive cleat.kernel/identical?
    (overload "clojure.lang.Util" "identical" 2 #f (pure inline) #f))
  (primitive cleat.kernel/inc
    (overload "clojure.lang.Numbers" "inc" 1 #f (pure inline) #f))
  (primitive cleat.kernel/int-cast
    (overload "clojure.lang.RT" "intCast" 1 #f (pure) #f))
  (primitive cleat.kernel/iter
    (overload "clojure.lang.RT" "iter" 1 #f () #f))
  (primitive cleat.kernel/keyword
    (overload "clojure.lang.RT" "keyword" 2 #f (pure) #f))
  (primitive cleat.kernel/list0
    (overload "clojure.lang.RT" "list" 0 #f (pure) #f))
  (primitive cleat.kernel/list1
    (overload "clojure.lang.RT" "list" 1 #f () #f))
  (primitive cleat.kernel/list2
    (overload "clojure.lang.RT" "list" 2 #f () #f))
  (primitive cleat.kernel/long-cast
    (overload "clojure.lang.RT" "longCast" 1 #f (pure) #f))
  (primitive cleat.kernel/lt
    (overload "clojure.lang.Numbers" "lt" 2 #f (pure inline) #f))
  (primitive cleat.kernel/lte
    (overload "clojure.lang.Numbers" "lte" 2 #f (pure inline) #f))
  (primitive cleat.kernel/map
    (overload "clojure.lang.RT" "map" 0 #t () #f))
  (primitive cleat.kernel/max
    (overload "clojure.lang.Numbers" "max" 2 #f (pure) #f))
  (primitive cleat.kernel/maybe-special-tag
    (overload "Cleat.Core.Runtime.Kernel" "MaybeSpecialTag" 1 #f () #f))
  (primitive cleat.kernel/maybe-type
    (overload "Cleat.Core.Runtime.Kernel" "MaybeType" 2 #f () #f))
  (primitive cleat.kernel/min
    (overload "clojure.lang.Numbers" "min" 2 #f (pure) #f))
  (primitive cleat.kernel/minus
    (overload "clojure.lang.Numbers" "minus" 2 #f (pure inline) #f))
  (primitive cleat.kernel/more
    (overload "clojure.lang.RT" "more" 1 #f (inline) #f))
  (primitive cleat.kernel/multiply
    (overload "clojure.lang.Numbers" "multiply" 2 #f (pure inline) #f))
  (primitive cleat.kernel/neg?
    (overload "clojure.lang.Numbers" "isNeg" 1 #f (pure) #f))
  (primitive cleat.kernel/negate
    (overload "clojure.lang.Numbers" "minus" 1 #f (pure) #f))
  (primitive cleat.kernel/next
    (overload "clojure.lang.RT" "next" 1 #f (inline) #f))
  (primitive cleat.kernel/nth
    (overload "clojure.lang.RT" "nth" 2 #f (inline) #f))
  (primitive cleat.kernel/nth3
    (overload "clojure.lang.RT" "nth" 3 #f () #f))
  (primitive cleat.kernel/num
    (overload "clojure.lang.Numbers" "num" 1 #f (pure) #f))
  (primitive cleat.kernel/num-equiv
    (overload "clojure.lang.Numbers" "equiv" 2 #f (pure inline) #f))
  (primitive cleat.kernel/pos?
    (overload "clojure.lang.Numbers" "isPos" 1 #f (pure) #f))
  (primitive cleat.kernel/print-string
    (overload "clojure.lang.RT" "printString" 1 #f () #f))
  (primitive cleat.kernel/quotient
    (overload "clojure.lang.Numbers" "quotient" 2 #f (pure) #f))
  (primitive cleat.kernel/reduced?
    (overload "clojure.lang.RT" "isReduced" 1 #f (pure) #f))
  (primitive cleat.kernel/remainder
    (overload "clojure.lang.Numbers" "remainder" 2 #f (pure) #f))
  (primitive cleat.kernel/seq
    (overload "clojure.lang.RT" "seq" 1 #f (inline) #f))
  (primitive cleat.kernel/set
    (overload "clojure.lang.RT" "set" 0 #t () #f))
  (primitive cleat.kernel/subvec
    (overload "clojure.lang.RT" "subvec" 3 #f () #f))
  (primitive cleat.kernel/unsigned-bit-shift-right
    (overload "clojure.lang.Numbers" "unsignedShiftRight" 2 #f (pure) #f))
  (primitive cleat.kernel/vector
    (overload "clojure.lang.RT" "vector" 0 #t () #f))
  (primitive cleat.kernel/zero?
    (overload "clojure.lang.Numbers" "isZero" 1 #f (pure inline) #f))
  (primitive command-line
    (overload "Cleat.Scheme.Builtins" "CommandLine" 0 #f () #f))
  (primitive compile-to-current-directory?
    (overload "Cleat.Scheme.Builtins" "CompileToCurrentDirectory" 0 #f () #f)
    (overload "Cleat.Scheme.Builtins" "CompileToCurrentDirectory" 1 #f () #f))
  (primitive compress-constants?
    (overload "Cleat.Scheme.Builtins" "CompressConstants" 0 #f () #f)
    (overload "Cleat.Scheme.Builtins" "CompressConstants" 1 #f () #f))
  (primitive condition
    (overload "Cleat.Scheme.Conditions" "MakeCondition" 0 #t () #f))
  (primitive condition-accessor
    (overload "Cleat.Scheme.Conditions" "ConditionAccessor" 2 #f () #f))
  (primitive condition-predicate
    (overload "Cleat.Scheme.Conditions" "ConditionPredicate" 1 #f () #f))
  (primitive cons
    (overload "Cleat.Scheme.Builtins" "Cons" 2 #f () cons))
  (primitive debug-mode?
    (overload "Cleat.Scheme.Builtins" "IsDebugMode" 0 #f () #f)
    (overload "Cleat.Scheme.Builtins" "IsDebugMode" 1 #f () #f))
  (primitive decompose-flonum
    (overload "Cleat.Scheme.Builtins" "DecomposeFlonum" 1 #f () #f))
  (primitive deserialize-port
    (overload "Cleat.Scheme.Fasl" "DeserializePort" 1 #f () #f))
  (primitive deterministic-gensym?
    (overload "Cleat.Scheme.Builtins" "DeterministicGensym" 0 #f () #f)
    (overload "Cleat.Scheme.Builtins" "DeterministicGensym" 1 #f () #f))
  (primitive eof-object
    (overload "Cleat.Scheme.Builtins" "EofObject" 0 #f () #f))
  (primitive eof-object?
    (overload "Cleat.Scheme.Builtins" "IsEof" 1 #f () #f))
  (primitive eq?
    (overload "Cleat.Scheme.Builtins" "IsEqual" 2 #f (pure) eq?))
  (primitive eqv?
    (overload "Cleat.Scheme.Builtins" "IsEqualValue" 2 #f (pure) eqv?))
  (primitive fx*internal
    (overload "Cleat.Scheme.Builtins" "FxMultiplyInternal" 2 #f (pure) #f))
  (primitive fx+internal
    (overload "Cleat.Scheme.Builtins" "FxPlusInternal" 2 #f (pure) #f))
  (primitive fx-internal
    (overload "Cleat.Scheme.Builtins" "FxMinusInternal" 2 #f (pure) #f))
  (primitive fxarithmetic-shift-left-internal
    (overload "Cleat.Scheme.Builtins" "FxShiftLeftInternal" 2 #f (pure) #f))
  (primitive generic*
    (overload "Cleat.Scheme.Builtins" "Multiply" 2 #f (pure) #f))
  (primitive generic+
    (overload "Cleat.Scheme.Builtins" "Add" 2 #f (pure) #f))
  (primitive generic-
    (overload "Cleat.Scheme.Builtins" "Subtract" 2 #f (pure) #f))
  (primitive generic-div
    (overload "Cleat.Scheme.Builtins" "Div" 2 #f (pure) #f))
  (primitive generic-negate
    (overload "Cleat.Scheme.Builtins" "Negate" 1 #f (pure) #f))
  (primitive generic/
    (overload "Cleat.Scheme.Builtins" "Divide" 2 #f (pure) #f))
  (primitive gensym
    (overload "Cleat.Scheme.SymbolPrimitives" "Gensym" 0 #f () #f)
    (overload "Cleat.Scheme.SymbolPrimitives" "Gensym" 1 #f () #f))
  (primitive get-clr-type
    (overload "Cleat.Scheme.Builtins" "GetClrType" 1 #f () #f)
    (overload "Cleat.Scheme.Builtins" "GetClrType" 1 #t () #f))
  (primitive get-command-line
    (overload "Cleat.Scheme.Builtins" "CommandLine" 0 #f () #f))
  (primitive get-library-paths
    (overload "Cleat.Scheme.Builtins" "GetLibraryPaths" 0 #f () #f))
  (primitive hashtable-entries
    (overload "Cleat.Scheme.Hashtables" "HashtableEntries" 1 #f () #f))
  (primitive hashtable-for-each
    (overload "Cleat.Scheme.Hashtables" "HashtableForEach" 2 #f () #f))
  (primitive hashtable-map
    (overload "Cleat.Scheme.Hashtables" "HashtableMap" 2 #f () #f))
  (primitive host-type
    (overload "Cleat.Scheme.HostPrimitives" "HostType" 0 #f (pure) #f))
  (primitive host-version
    (overload "Cleat.Scheme.HostPrimitives" "HostVersion" 0 #f (pure) #f))
  (primitive inexact=?
    (overload "Cleat.Scheme.Builtins" "InexactEqual" 2 #f (pure) #f))
  (primitive list*
    (overload "Cleat.Scheme.Builtins" "ListStar" 1 #t () #f)
    (overload "Cleat.Scheme.Builtins" "ListStar" 2 #f () #f)
    (overload "Cleat.Scheme.Builtins" "ListStar" 3 #f () #f)
    (overload "Cleat.Scheme.Builtins" "ListStar" 4 #f () #f))
  (primitive list-prim
    (overload "Cleat.Scheme.Builtins" "List" 0 #f () #f)
    (overload "Cleat.Scheme.Builtins" "List" 0 #t () #f)
    (overload "Cleat.Scheme.Builtins" "List" 1 #f () #f)
    (overload "Cleat.Scheme.Builtins" "List" 2 #f () #f)
    (overload "Cleat.Scheme.Builtins" "List" 3 #f () #f)
    (overload "Cleat.Scheme.Builtins" "List" 4 #f () #f)
    (overload "Cleat.Scheme.Builtins" "List" 5 #f () #f))
  (primitive load-fasl
    (overload "Cleat.Scheme.ModuleLoader" "LoadFasl" 1 #f () #f))
  (primitive load-library-dll
    (overload "Cleat.Scheme.ModuleLoader" "LoadLibraryDll" 1 #f () #f))
  (primitive lw-debug-mode?
    (overload "Cleat.Scheme.Builtins" "IsLightWeightDebugMode" 0 #f () #f)
    (overload "Cleat.Scheme.Builtins" "IsLightWeightDebugMode" 1 #f () #f))
  (primitive machine-type
    (overload "Cleat.Scheme.HostPrimitives" "MachineType" 0 #f (pure) #f))
  (primitive make-annotation
    (overload "Cleat.Scheme.Builtins" "MakeAnnotation" 3 #f () #f))
  (primitive make-record-constructor-descriptor
    (overload "Cleat.Scheme.Records" "MakeRecordConstructorDescriptor" 3 #f () #f))
  (primitive make-record-type-descriptor
    (overload "Cleat.Scheme.Records" "MakeRecordTypeDescriptor" 6 #f () #f)
    (overload "Cleat.Scheme.Records" "MakeRecordTypeDescriptorTyped" 7 #f () #f))
  (primitive nil?
    (overload "Cleat.Scheme.Builtins" "IsNil" 1 #f (pure) nil?))
  (primitive not
    (overload "Cleat.Scheme.Builtins" "Not" 1 #f (pure) not))
  (primitive null?
    (overload "Cleat.Scheme.Builtins" "IsNull" 1 #f (pure) null?))
  (primitive open-file-input/output-port
    (overload "Cleat.Scheme.IO" "OpenFileInputOutputPort" 1 #f () #f)
    (overload "Cleat.Scheme.IO" "OpenFileInputOutputPort" 2 #f () #f)
    (overload "Cleat.Scheme.IO" "OpenFileInputOutputPort" 3 #f () #f)
    (overload "Cleat.Scheme.IO" "OpenFileInputOutputPort" 4 #f () #f))
  (primitive open-file-output-port
    (overload "Cleat.Scheme.IO" "OpenFileOutputPort" 1 #f () #f)
    (overload "Cleat.Scheme.IO" "OpenFileOutputPort" 2 #f () #f)
    (overload "Cleat.Scheme.IO" "OpenFileOutputPort" 3 #f () #f)
    (overload "Cleat.Scheme.IO" "OpenFileOutputPort" 4 #f () #f))
  (primitive open-tcp-input/output-port
    (overload "Cleat.Scheme.Builtins" "OpenTcpInputOutputPort" 3 #f () #f))
  (primitive pair?
    (overload "Cleat.Scheme.Builtins" "IsPair" 1 #f (pure) pair?))
  (primitive pointer+
    (overload "Cleat.Scheme.Builtins" "PointerAdd" 2 #f () #f))
  (primitive read-annotated
    (overload "Cleat.Scheme.Builtins" "ReadAnnotated" 1 #f () #f))
  (primitive record-accessor
    (overload "Cleat.Scheme.Records" "RecordAccessor" 2 #f () #f))
  (primitive record-constructor
    (overload "Cleat.Scheme.Records" "RecordConstructor" 1 #f () #f))
  (primitive record-mutator
    (overload "Cleat.Scheme.Records" "RecordMutator" 2 #f () #f))
  (primitive record-predicate
    (overload "Cleat.Scheme.Records" "RecordPredicate" 1 #f () #f))
  (primitive record-rtd
    (overload "Cleat.Scheme.Records" "RecordRtd" 1 #f () #f))
  (primitive record?
    (overload "Cleat.Scheme.Records" "IsRecord" 1 #f () #f))
  (primitive remove-location
    (overload "Cleat.Scheme.SymbolPrimitives" "RemoveLocation" 1 #f () #f))
  (primitive reverse!
    (overload "Cleat.Scheme.Builtins" "NReverse" 1 #f () #f))
  (primitive serialize-port
    (overload "Cleat.Scheme.Fasl" "SerializePort" 2 #f () #f))
  (primitive set-symbol-value!
    (overload "Cleat.Scheme.SymbolPrimitives" "SetSymbolValue" 2 #f () #f))
  (primitive simple-conditions
    (overload "Cleat.Scheme.Conditions" "SimpleConditions" 1 #f () #f))
  (primitive strict-mode?
    (overload "Cleat.Scheme.Builtins" "StrictMode" 0 #f () #f)
    (overload "Cleat.Scheme.Builtins" "StrictMode" 1 #f () #f))
  (primitive string->bytevector
    (overload "Cleat.Scheme.IO" "StringToByteVector" 2 #f () #f))
  (primitive string->number
    (overload "Cleat.Scheme.Builtins" "StringToNumber" 1 #f (pure) #f)
    (overload "Cleat.Scheme.Builtins" "StringToNumber" 2 #f (pure) #f))
  (primitive string->symbol
    (overload "Cleat.Scheme.SymbolPrimitives" "StringToSymbol" 1 #f (pure) #f))
  (primitive string->xml
    (overload "Cleat.Scheme.Builtins" "StringToXml" 1 #f () #f))
  (primitive string-split
    (overload "Cleat.Scheme.Builtins" "StringSplit" 1 #t () #f))
  (primitive symbol->string
    (overload "Cleat.Scheme.SymbolPrimitives" "SymbolToString" 1 #f (pure) #f))
  (primitive symbol-bound?
    (overload "Cleat.Scheme.SymbolPrimitives" "IsSymbolBound" 1 #f () #f))
  (primitive symbol-value
    (overload "Cleat.Scheme.SymbolPrimitives" "SymbolValue" 1 #f () #f))
  (primitive target-host-type
    (overload "Cleat.Scheme.HostPrimitives" "TargetHostType" 0 #f () #f))
  (primitive time-it
    (overload "Cleat.Scheme.Builtins" "TimeIt" 2 #f () #f))
  (primitive ungensym
    (overload "Cleat.Scheme.SymbolPrimitives" "Ungensym" 1 #f (pure) #f))
  (primitive unspecified?
    (overload "Cleat.Scheme.Builtins" "IsUnspecified" 1 #f () #f))
  (primitive vector
    (overload "Cleat.Scheme.Builtins" "Vector" 0 #t () vector))
  (primitive vector-append
    (overload "Cleat.Scheme.Builtins" "VectorAppend" 0 #t () #f))
  (primitive vector-filter
    (overload "Cleat.Scheme.Builtins" "VectorFilter" 2 #f () #f))
  (primitive void
    (overload "Cleat.Scheme.Builtins" "Void" 0 #f (pure) void))
  (primitive with-clr-exception-handler
    (overload "Cleat.Scheme.Exceptions" "WithClrExceptionHandler" 2 #f () #f))
  (primitive with-timeout
    (overload "Cleat.Scheme.Builtins" "WithTimeout" 2 #f () #f))
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
