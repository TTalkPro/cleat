;   Copyright (c) Rich Hickey. All rights reserved.
;   The use and distribution terms for this software are covered by the
;   Eclipse Public License 1.0 (http://opensource.org/licenses/eclipse-1.0.php)
;   which can be found in the file epl-v10.html at the root of this distribution.
;   By using this software in any fashion, you are agreeing to be bound by
;   the terms of this license.
;   You must not remove this notice, or any other, from this software.

;; CLEAT-032: clojure.math for Cleat. The public API is Clojure 1.11's clojure.math (the portable one, java.lang.Math
;; semantics), implemented on System.Math and Cleat.Core.Runtime.ClojureMath; ClojureCLR's System.Math-flavoured extras
;; (ceiling, truncate, scaleb, sign-long, clamp-double, bit-increment ...) are kept too, so code written for either works.
(ns
  ^{:author "Alex Miller (API); Cleat port"
    :doc "Clojure wrapper functions for java.lang.Math-style math on System.Math."}
  clojure.math)

(def ^{:doc "Constant for e, the base for natural logarithms." :added "1.11" :const true :tag 'double} E Math/E)
(def ^{:doc "Constant for pi, the ratio of the circumference of a circle to its diameter." :added "1.11" :const true :tag 'double} PI Math/PI)
(def ^{:doc "Constant for tau, the number of radians in one turn." :const true :tag 'double} Tau Math/Tau)

(defn sin
  {:doc "Returns the sine of an angle."}
  [a]
  (Math/Sin (double a)))

(defn cos
  {:doc "Returns the cosine of an angle."}
  [a]
  (Math/Cos (double a)))

(defn tan
  {:doc "Returns the tangent of an angle."}
  [a]
  (Math/Tan (double a)))

(defn asin
  {:doc "Returns the arc sine of an angle, in the range -pi/2 to pi/2."}
  [a]
  (Math/Asin (double a)))

(defn acos
  {:doc "Returns the arc cosine of a, in the range 0.0 to pi."}
  [a]
  (Math/Acos (double a)))

(defn atan
  {:doc "Returns the arc tangent of a, in the range of -pi/2 to pi/2."}
  [a]
  (Math/Atan (double a)))

(defn to-radians
  {:doc "Converts an angle in degrees to an approximate equivalent angle in radians."}
  [a]
  (Cleat.Core.Runtime.ClojureMath/ToRadians (double a)))

(defn to-degrees
  {:doc "Converts an angle in radians to an approximate equivalent angle in degrees."}
  [a]
  (Cleat.Core.Runtime.ClojureMath/ToDegrees (double a)))

(defn exp
  {:doc "Returns Euler's number e raised to the power of a."}
  [a]
  (Math/Exp (double a)))

(defn log
  {:doc "Returns the natural logarithm (base e) of a."}
  [a]
  (Math/Log (double a)))

(defn log10
  {:doc "Returns the logarithm (base 10) of a."}
  [a]
  (Math/Log10 (double a)))

(defn log2
  {:doc "Returns the logarithm (base 2) of a."}
  [a]
  (Math/Log2 (double a)))

(defn sqrt
  {:doc "Returns the positive square root of a."}
  [a]
  (Math/Sqrt (double a)))

(defn cbrt
  {:doc "Returns the cube root of a."}
  [a]
  (Math/Cbrt (double a)))

(defn ceil
  {:doc "Returns the smallest double greater than or equal to a, and equal to a mathematical integer."}
  [a]
  (Math/Ceiling (double a)))

(defn ceiling
  {:doc "Returns the smallest double greater than or equal to a, and equal to a mathematical integer."}
  [a]
  (Math/Ceiling (double a)))

(defn floor
  {:doc "Returns the largest double less than or equal to a, and equal to a mathematical integer."}
  [a]
  (Math/Floor (double a)))

(defn truncate
  {:doc "Returns the integral part of a."}
  [a]
  (Math/Truncate (double a)))

(defn rint
  {:doc "Returns the double closest to a and equal to a mathematical integer; ties go to the even integer."}
  [a]
  (Cleat.Core.Runtime.ClojureMath/Rint (double a)))

(defn round
  {:doc "Returns the closest long to a; ties round towards positive infinity. NaN => 0; saturates at Long/MIN_VALUE and Long/MAX_VALUE."}
  [a]
  (Cleat.Core.Runtime.ClojureMath/Round (double a)))

(defn ulp
  {:doc "Returns the size of an ulp (unit in last place) for d."}
  [a]
  (Cleat.Core.Runtime.ClojureMath/Ulp (double a)))

(defn signum
  {:doc "Returns the signum function of d: zero if d is zero, 1.0 if d > 0, -1.0 if d < 0; NaN and signed zeros are returned as is."}
  [a]
  (Cleat.Core.Runtime.ClojureMath/Signum (double a)))

(defn sinh
  {:doc "Returns the hyperbolic sine of x."}
  [a]
  (Math/Sinh (double a)))

(defn cosh
  {:doc "Returns the hyperbolic cosine of x."}
  [a]
  (Math/Cosh (double a)))

(defn tanh
  {:doc "Returns the hyperbolic tangent of x."}
  [a]
  (Math/Tanh (double a)))

(defn asinh
  {:doc "Returns the inverse hyperbolic sine of x."}
  [a]
  (Math/Asinh (double a)))

(defn acosh
  {:doc "Returns the inverse hyperbolic cosine of x."}
  [a]
  (Math/Acosh (double a)))

(defn atanh
  {:doc "Returns the inverse hyperbolic tangent of x."}
  [a]
  (Math/Atanh (double a)))

(defn expm1
  {:doc "Returns e^x - 1; accurate for x near zero."}
  [a]
  (Cleat.Core.Runtime.ClojureMath/Expm1 (double a)))

(defn log1p
  {:doc "Returns ln(1+x); accurate for x near zero."}
  [a]
  (Cleat.Core.Runtime.ClojureMath/Log1p (double a)))

(defn get-exponent
  {:doc "Returns the unbiased exponent used in the representation of d."}
  [a]
  (Cleat.Core.Runtime.ClojureMath/GetExponent (double a)))

(defn next-up
  {:doc "Returns the adjacent double of d in the direction of ##Inf."}
  [a]
  (Cleat.Core.Runtime.ClojureMath/NextUp (double a)))

(defn next-down
  {:doc "Returns the adjacent double of d in the direction of ##-Inf."}
  [a]
  (Cleat.Core.Runtime.ClojureMath/NextDown (double a)))

(defn increment-exact
  {:doc "Returns a incremented by 1, throws ArithmeticException on long overflow."}
  [a]
  (Cleat.Core.Runtime.ClojureMath/IncrementExact (long a)))

(defn decrement-exact
  {:doc "Returns a decremented by 1, throws ArithmeticException on long overflow."}
  [a]
  (Cleat.Core.Runtime.ClojureMath/DecrementExact (long a)))

(defn negate-exact
  {:doc "Returns the negation of a, throws ArithmeticException on long overflow."}
  [a]
  (Cleat.Core.Runtime.ClojureMath/NegateExact (long a)))

(defn ilogb
  {:doc "Returns the base 2 integer logarithm of x (System.Math/ILogB)."}
  [a]
  (Math/ILogB (double a)))

(defn bit-increment
  {:doc "Returns the next larger double (System.Math/BitIncrement)."}
  [a]
  (Math/BitIncrement (double a)))

(defn bit-decrement
  {:doc "Returns the next smaller double (System.Math/BitDecrement)."}
  [a]
  (Math/BitDecrement (double a)))

(defn reciprocal-estimate
  {:doc "Returns an estimate of 1/x."}
  [a]
  (Math/ReciprocalEstimate (double a)))

(defn reciprocal-sqrt-estimate
  {:doc "Returns an estimate of 1/sqrt(x)."}
  [a]
  (Math/ReciprocalSqrtEstimate (double a)))

(defn sign-double
  {:doc "Returns the sign of a double as an integer: -1, 0 or 1 (System.Math/Sign)."}
  [a]
  (long (Math/Sign (double a))))

(defn sign-long
  {:doc "Returns the sign of a long as an integer: -1, 0 or 1 (System.Math/Sign)."}
  [a]
  (long (Math/Sign (long a))))

(defn atan2
  {:doc "Returns the angle theta from the conversion of rectangular coordinates (x, y) to polar coordinates (r, theta)."}
  [y x]
  (Math/Atan2 (double y) (double x)))

(defn pow
  {:doc "Returns the value of a raised to the power of b."}
  [a b]
  (Math/Pow (double a) (double b)))

(defn IEEE-remainder
  {:doc "Returns the remainder per IEEE 754: dividend - divisor * n, n the integer nearest the exact quotient (ties to even)."}
  [dividend divisor]
  (Math/IEEERemainder (double dividend) (double divisor)))

(defn hypot
  {:doc "Returns sqrt(x^2 + y^2) without intermediate underflow or overflow."}
  [x y]
  (Cleat.Core.Runtime.ClojureMath/Hypot (double x) (double y)))

(defn copy-sign
  {:doc "Returns a double with the magnitude of the first argument and the sign of the second."}
  [magnitude sign]
  (Math/CopySign (double magnitude) (double sign)))

(defn next-after
  {:doc "Returns the adjacent floating point number to start in the direction of the second argument."}
  [start direction]
  (Cleat.Core.Runtime.ClojureMath/NextAfter (double start) (double direction)))

(defn scalb
  {:doc "Returns d * 2^scaleFactor, rounded as if by a single correctly rounded floating-point multiply."}
  [d scaleFactor]
  (Cleat.Core.Runtime.ClojureMath/Scalb (double d) (long scaleFactor)))

(defn scaleb
  {:doc "Returns d * 2^scaleFactor (System.Math/ScaleB)."}
  [d scaleFactor]
  (Cleat.Core.Runtime.ClojureMath/Scalb (double d) (long scaleFactor)))

(defn add-exact
  {:doc "Returns the sum of x and y, throws ArithmeticException on long overflow."}
  [x y]
  (Cleat.Core.Runtime.ClojureMath/AddExact (long x) (long y)))

(defn subtract-exact
  {:doc "Returns the difference of x and y, throws ArithmeticException on long overflow."}
  [x y]
  (Cleat.Core.Runtime.ClojureMath/SubtractExact (long x) (long y)))

(defn multiply-exact
  {:doc "Returns the product of x and y, throws ArithmeticException on long overflow."}
  [x y]
  (Cleat.Core.Runtime.ClojureMath/MultiplyExact (long x) (long y)))

(defn floor-div
  {:doc "Integer division that rounds to negative infinity (as opposed to zero)."}
  [x y]
  (Cleat.Core.Runtime.ClojureMath/FloorDiv (long x) (long y)))

(defn floor-mod
  {:doc "Integer modulus x - (floor-div(x, y) * y); sign matches y."}
  [x y]
  (Cleat.Core.Runtime.ClojureMath/FloorMod (long x) (long y)))

(defn max-magnitude
  {:doc "Returns the larger magnitude of two doubles (System.Math/MaxMagnitude)."}
  [x y]
  (Math/MaxMagnitude (double x) (double y)))

(defn min-magnitude
  {:doc "Returns the smaller magnitude of two doubles (System.Math/MinMagnitude)."}
  [x y]
  (Math/MinMagnitude (double x) (double y)))

(defn fused-multiply-add
  {:doc "Returns (x * y) + z rounded once (System.Math/FusedMultiplyAdd)."}
  [x y z]
  (Math/FusedMultiplyAdd (double x) (double y) (double z)))

(defn clamp-double
  {:doc "Returns value clamped to the inclusive range of min and max."}
  [value min max]
  (Math/Clamp (double value) (double min) (double max)))

(defn clamp-long
  {:doc "Returns value clamped to the inclusive range of min and max."}
  [value min max]
  (Math/Clamp (long value) (long min) (long max)))

(defn random
  {:doc "Returns a positive double between 0.0 and 1.0, chosen pseudorandomly with approximately random distribution."}
  []
  (Cleat.Core.Runtime.ClojureMath/Random))
