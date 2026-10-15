// 不装箱的数（cleat-102；按 skeg iron-080 的 Num 改成 Clojure 的语义）：编译器把"用于算术、但编译期定不出是 long 还是 double"的
// 局部变量、循环变量与形参放在 Num 里，运算不分配。只活在局部变量与求值栈上；存进数据结构、传给未知函数时装箱成普通的 Clojure 值。

using System;
using System.Runtime.CompilerServices;
using clojure.lang;

namespace Cleat.Core.Runtime;

/// <summary>
/// 16 字节的值（两个字段，JIT 可拆进寄存器）：
/// <list type="bullet">
/// <item><c>Tag == null</c>：long，值在 <c>Bits</c>；</item>
/// <item><c>Tag == FlTag</c>：double，<c>Bits</c> 是它的位；</item>
/// <item>其余：已装箱的值本身（BigInt、Ratio、BigDecimal，以及编译器推测错了的非数——运算时交给 Numbers 报错）；null 记作 <c>NullTag</c>。</item>
/// </list>
/// 任何值都能放进来，所以推断错了只是慢，不会错。快路径：两个 long（不溢出）、两个 double、long 与 double 混合（long 转 double，
/// 同 Numbers 的传染规则）；其余装箱后调 <see cref="Numbers"/> 的 object 重载，结果与异常（整数溢出的 ArithmeticException 等）与不经 Num 时完全相同。
/// 与 skeg 的不同：long 溢出不升大整数而是走慢路径抛异常（Clojure 的 + - * 语义）；<c>Equiv</c> 是 <c>==</c>（Numbers.equiv），<c>=</c>（Util.equiv）不经 Num。
/// </summary>
public readonly struct Num
{
    public readonly long Bits;
    public readonly object Tag;

    private static readonly object FlTag = new object();
    private static readonly object NullTag = new object();

    // 显式静态构造函数：类型不带 beforefieldinit，Startup 在运行编译出的代码之前先初始化，JIT 之后把 FlTag 当常量
    static Num() { }

    private Num(long bits, object tag) { Bits = bits; Tag = tag; }

    private bool IsLong { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => Tag is null; }
    private bool IsFl { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => ReferenceEquals(Tag, FlTag); }
    private double Fl { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => BitConverter.Int64BitsToDouble(Bits); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Num FromInt64(long l) => new Num(l, null);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Num FromDouble(double d) => new Num(BitConverter.DoubleToInt64Bits(d), FlTag);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Num FromObject(object o) => o switch
    {
        long l => new Num(l, null),
        double d => FromDouble(d),
        null => new Num(0, NullTag),
        _ => new Num(0, o)
    };

    /// <summary>装箱成普通的 Clojure 值（long / double / 原对象 / nil）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static object Box(Num n)
    {
        if (n.Tag is null) return LongBox.Get(n.Bits);
        if (ReferenceEquals(n.Tag, FlTag)) return n.Fl;
        return ReferenceEquals(n.Tag, NullTag) ? null : n.Tag;
    }

    /// <summary>是否 double（循环推测的入口检查用，cleat-103）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsDouble(Num n) => ReferenceEquals(n.Tag, FlTag);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double DoubleValue(Num n) => n.Fl;

    /// <summary>是否 long（循环推测的入口检查用，cleat-103）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsInt64(Num n) => n.Tag is null;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long Int64Value(Num n) => n.Bits;

    /// <summary>推断为 double 的算术（double 与任何数相加等）经 Num 算出后取 double：不是 double 时（推断错了）按 Clojure 的规则转换或报错。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double ToDouble(Num n) => ReferenceEquals(n.Tag, FlTag) ? n.Fl : ClojureStaticCalls.PrimRetDouble(Box(n));

    /// <summary>推断为 long 的算术经 Num 算出后取 long。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ToInt64(Num n) => n.Tag is null ? n.Bits : ClojureStaticCalls.PrimRetLong(Box(n));

    // —— 算术 ——

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Num Add(Num a, Num b)
    {
        if (a.IsLong)
        {
            if (b.IsLong)
            {
                long r = a.Bits + b.Bits;
                if (((a.Bits ^ r) & (b.Bits ^ r)) >= 0) return new Num(r, null);
            }
            else if (b.IsFl) return FromDouble(a.Bits + b.Fl);
        }
        else if (a.IsFl)
        {
            if (b.IsFl) return FromDouble(a.Fl + b.Fl);
            if (b.IsLong) return FromDouble(a.Fl + b.Bits);
        }
        return SlowAdd(a, b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Num Subtract(Num a, Num b)
    {
        if (a.IsLong)
        {
            if (b.IsLong)
            {
                long r = a.Bits - b.Bits;
                if (((a.Bits ^ b.Bits) & (a.Bits ^ r)) >= 0) return new Num(r, null);
            }
            else if (b.IsFl) return FromDouble(a.Bits - b.Fl);
        }
        else if (a.IsFl)
        {
            if (b.IsFl) return FromDouble(a.Fl - b.Fl);
            if (b.IsLong) return FromDouble(a.Fl - b.Bits);
        }
        return SlowSubtract(a, b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Num Multiply(Num a, Num b)
    {
        if (a.IsLong)
        {
            if (b.IsLong)
            {
                long hi = Math.BigMul(a.Bits, b.Bits, out long lo);
                if (hi == (lo >> 63)) return new Num(lo, null);
            }
            else if (b.IsFl) return FromDouble(a.Bits * b.Fl);
        }
        else if (a.IsFl)
        {
            if (b.IsFl) return FromDouble(a.Fl * b.Fl);
            if (b.IsLong) return FromDouble(a.Fl * b.Bits);
        }
        return SlowMultiply(a, b);
    }

    /// <summary>
    /// 两个 long 相除可能是 Ratio：交给 Numbers。除数为零（long 0 或 double ±0.0）也交给 Numbers：object 路径先查 isZero 再分派，
    /// 所以 <c>(/ 1.0 0)</c> 与 <c>(/ 1.0 0.0)</c> 经 object 重载都抛 Divide by zero（JVM 同），只有静态决议到 divide(double, double) 的才得 ##Inf。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Num Divide(Num a, Num b)
    {
        if (b.IsFl)
        {
            double d = b.Fl;
            if (d != 0.0)
            {
                if (a.IsFl) return FromDouble(a.Fl / d);
                if (a.IsLong) return FromDouble(a.Bits / d);
            }
        }
        else if (b.IsLong && b.Bits != 0 && a.IsFl) return FromDouble(a.Fl / b.Bits);
        return SlowDivide(a, b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Num Inc(Num a)
    {
        if (a.IsLong) { if (a.Bits != long.MaxValue) return new Num(a.Bits + 1, null); }
        else if (a.IsFl) return FromDouble(a.Fl + 1.0);
        return FromObject(Numbers.inc(Box(a)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Num Dec(Num a)
    {
        if (a.IsLong) { if (a.Bits != long.MinValue) return new Num(a.Bits - 1, null); }
        else if (a.IsFl) return FromDouble(a.Fl - 1.0);
        return FromObject(Numbers.dec(Box(a)));
    }

    [MethodImpl(MethodImplOptions.NoInlining)] private static Num SlowAdd(Num a, Num b) => FromObject(Numbers.add(Box(a), Box(b)));
    [MethodImpl(MethodImplOptions.NoInlining)] private static Num SlowSubtract(Num a, Num b) => FromObject(Numbers.minus(Box(a), Box(b)));
    [MethodImpl(MethodImplOptions.NoInlining)] private static Num SlowMultiply(Num a, Num b) => FromObject(Numbers.multiply(Box(a), Box(b)));
    [MethodImpl(MethodImplOptions.NoInlining)] private static Num SlowDivide(Num a, Num b) => FromObject(Numbers.divide(Box(a), Box(b)));

    // —— 比较（Numbers 的规则：long 与 double 混合时 long 转 double；NaN 的比较都为假）——

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool LessThan(Num a, Num b) =>
        a.IsLong ? (b.IsLong ? a.Bits < b.Bits : b.IsFl ? a.Bits < b.Fl : Numbers.lt(Box(a), Box(b)))
        : a.IsFl ? (b.IsFl ? a.Fl < b.Fl : b.IsLong ? a.Fl < b.Bits : Numbers.lt(Box(a), Box(b)))
        : Numbers.lt(Box(a), Box(b));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool LessOrEqual(Num a, Num b) =>
        a.IsLong ? (b.IsLong ? a.Bits <= b.Bits : b.IsFl ? a.Bits <= b.Fl : Numbers.lte(Box(a), Box(b)))
        : a.IsFl ? (b.IsFl ? a.Fl <= b.Fl : b.IsLong ? a.Fl <= b.Bits : Numbers.lte(Box(a), Box(b)))
        : Numbers.lte(Box(a), Box(b));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool GreaterThan(Num a, Num b) =>
        a.IsLong ? (b.IsLong ? a.Bits > b.Bits : b.IsFl ? a.Bits > b.Fl : Numbers.gt(Box(a), Box(b)))
        : a.IsFl ? (b.IsFl ? a.Fl > b.Fl : b.IsLong ? a.Fl > b.Bits : Numbers.gt(Box(a), Box(b)))
        : Numbers.gt(Box(a), Box(b));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool GreaterOrEqual(Num a, Num b) =>
        a.IsLong ? (b.IsLong ? a.Bits >= b.Bits : b.IsFl ? a.Bits >= b.Fl : Numbers.gte(Box(a), Box(b)))
        : a.IsFl ? (b.IsFl ? a.Fl >= b.Fl : b.IsLong ? a.Fl >= b.Bits : Numbers.gte(Box(a), Box(b)))
        : Numbers.gte(Box(a), Box(b));

    /// <summary><c>==</c>（Numbers.equiv：数值相等，long 与 double 混合时转 double）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Equiv(Num a, Num b) =>
        a.IsLong ? (b.IsLong ? a.Bits == b.Bits : b.IsFl ? a.Bits == b.Fl : Numbers.equiv(Box(a), Box(b)))
        : a.IsFl ? (b.IsFl ? a.Fl == b.Fl : b.IsLong ? a.Fl == b.Bits : Numbers.equiv(Box(a), Box(b)))
        : Numbers.equiv(Box(a), Box(b));
}
