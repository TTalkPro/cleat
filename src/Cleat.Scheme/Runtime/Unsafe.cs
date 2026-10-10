using System;

namespace Cleat.Scheme;

/// <summary>
/// 不做类型检查的 $ 原语（旧系统 Runtime/Unsafe.cs 中的内联发射器）。
/// 编译器按 Inline 模板内联它们（iron-025）；这里的 C# 实现供 stage0 解释执行与非内联调用使用。
/// 类型不符时直接抛 InvalidCastException（与内联后的行为一致）。
///
/// 不在此处：$and? $or? $break $throw $try/finally——它们作用于未求值的参数表达式，是编译器 / stage0 的特殊形式，
/// 在原语表中以 (special …) 条目出现（iron-012）。
/// </summary>
public static class Unsafe
{
    private const PrimitiveFlags U = PrimitiveFlags.Unsafe, UP = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure;

    private static object B(bool b) => RT.Bool(b);
    private static object I(int i) => RT.Box(i);

    [Primitive("$car", Flags = UP, Inline = "$car")] public static object Car(object p) => ((Cons)p).car;
    [Primitive("$cdr", Flags = UP, Inline = "$cdr")] public static object Cdr(object p) => ((Cons)p).cdr;

    [Primitive("$vector-ref", Flags = UP, Inline = "$vector-ref")]
    public static object VectorRef(object v, object i) => v is object[] a ? a[(int)i] : ((Array)v).GetValue((int)i);

    [Primitive("$vector-set!", Flags = U, Inline = "$vector-set!")]
    public static object VectorSet(object v, object i, object x)
    {
        if (v is object[] a) a[(int)i] = x; else ((Array)v).SetValue(x, (int)i);
        return Unspecified.Instance;
    }

    [Primitive("$bytevector-ref", Flags = UP, Inline = "$bytevector-ref")]
    public static object ByteVectorRef(object bv, object i) => I(((byte[])bv)[(int)i]);

    [Primitive("$bytevector-set!", Flags = U, Inline = "$bytevector-set!")]
    public static object ByteVectorSet(object bv, object i, object x)
    {
        ((byte[])bv)[(int)i] = Convert.ToByte(x);
        return Unspecified.Instance;
    }

    // fixnum：32 位，不检查溢出（unchecked，与内联的 IL add/sub/mul 一致）
    [Primitive("$fx=?", Flags = UP, Inline = "$fx=?")] public static object FxEq(object a, object b) => B((int)a == (int)b);
    [Primitive("$fx<?", Flags = UP, Inline = "$fx<?")] public static object FxLt(object a, object b) => B((int)a < (int)b);
    [Primitive("$fx<=?", Flags = UP, Inline = "$fx<=?")] public static object FxLe(object a, object b) => B((int)a <= (int)b);
    [Primitive("$fx>?", Flags = UP, Inline = "$fx>?")] public static object FxGt(object a, object b) => B((int)a > (int)b);
    [Primitive("$fx>=?", Flags = UP, Inline = "$fx>=?")] public static object FxGe(object a, object b) => B((int)a >= (int)b);
    [Primitive("$fx+", Flags = UP, Inline = "$fx+")] public static object FxAdd(object a, object b) => I(unchecked((int)a + (int)b));
    [Primitive("$fx*", Flags = UP, Inline = "$fx*")] public static object FxMul(object a, object b) => I(unchecked((int)a * (int)b));
    [Primitive("$fx-", Flags = UP, Inline = "$fx-")] public static object FxNeg(object a) => I(unchecked(-(int)a));
    [Primitive("$fx-", Flags = UP, Inline = "$fx-")] public static object FxSub(object a, object b) => I(unchecked((int)a - (int)b));
    [Primitive("$fxand", Flags = UP, Inline = "$fxand")] public static object FxAnd(object a, object b) => I((int)a & (int)b);
    [Primitive("$fxior", Flags = UP, Inline = "$fxior")] public static object FxIor(object a, object b) => I((int)a | (int)b);
    [Primitive("$fxxor", Flags = UP, Inline = "$fxxor")] public static object FxXor(object a, object b) => I((int)a ^ (int)b);
    [Primitive("$fxnot", Flags = UP, Inline = "$fxnot")] public static object FxNot(object a) => I(~(int)a);

    // 与旧系统一致：截断除法与 C# 余数（余数符号随被除数），不是 R6RS 的 div/mod；div0/mod0 与 div/mod 相同
    [Primitive("$fxdiv", Flags = UP, Inline = "$fxdiv")] public static object FxDiv(object a, object b) => I((int)a / (int)b);
    [Primitive("$fxdiv0", Flags = UP, Inline = "$fxdiv0")] public static object FxDiv0(object a, object b) => I((int)a / (int)b);
    [Primitive("$fxmod", Flags = UP, Inline = "$fxmod")] public static object FxMod(object a, object b) => I((int)a % (int)b);
    [Primitive("$fxmod0", Flags = UP, Inline = "$fxmod0")] public static object FxMod0(object a, object b) => I((int)a % (int)b);

    [Primitive("$fxarithmetic-shift-left", Flags = UP, Inline = "$fxarithmetic-shift-left")]
    public static object FxShl(object a, object b) => I((int)a << (int)b);
    [Primitive("$fxarithmetic-shift-right", Flags = UP, Inline = "$fxarithmetic-shift-right")]
    public static object FxShr(object a, object b) => I((int)a >> (int)b);

    [Primitive("$fxzero?", Flags = UP, Inline = "$fxzero?")] public static object FxZero(object a) => B((int)a == 0);
    [Primitive("$fxnegative?", Flags = UP, Inline = "$fxnegative?")] public static object FxNegative(object a) => B((int)a < 0);
    [Primitive("$fxpositive?", Flags = UP, Inline = "$fxpositive?")] public static object FxPositive(object a) => B((int)a > 0);
    // 旧内联发射器为 (x + 1) == 0 / (x + 1) == 1，并非奇偶判断；Scheme 源码未使用，这里按正确语义实现
    [Primitive("$fxeven?", Flags = UP, Inline = "$fxeven?")] public static object FxEven(object a) => B(((int)a & 1) == 0);
    [Primitive("$fxodd?", Flags = UP, Inline = "$fxodd?")] public static object FxOdd(object a) => B(((int)a & 1) != 0);

    // flonum
    [Primitive("$fl=?", Flags = UP, Inline = "$fl=?")] public static object FlEq(object a, object b) => B((double)a == (double)b);
    [Primitive("$fl<?", Flags = UP, Inline = "$fl<?")] public static object FlLt(object a, object b) => B((double)a < (double)b);
    [Primitive("$fl<=?", Flags = UP, Inline = "$fl<=?")] public static object FlLe(object a, object b) => B((double)a <= (double)b);
    [Primitive("$fl>?", Flags = UP, Inline = "$fl>?")] public static object FlGt(object a, object b) => B((double)a > (double)b);
    [Primitive("$fl>=?", Flags = UP, Inline = "$fl>=?")] public static object FlGe(object a, object b) => B((double)a >= (double)b);
    [Primitive("$fl+", Flags = UP, Inline = "$fl+")] public static object FlAdd(object a, object b) => (double)a + (double)b;
    [Primitive("$fl*", Flags = UP, Inline = "$fl*")] public static object FlMul(object a, object b) => (double)a * (double)b;
    [Primitive("$fl-", Flags = UP, Inline = "$fl-")] public static object FlNeg(object a) => -(double)a;
    [Primitive("$fl-", Flags = UP, Inline = "$fl-")] public static object FlSub(object a, object b) => (double)a - (double)b;
    [Primitive("$fl/", Flags = UP, Inline = "$fl/")] public static object FlDiv(object a, object b) => (double)a / (double)b;
}
