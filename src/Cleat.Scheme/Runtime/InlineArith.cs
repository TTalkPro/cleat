namespace Cleat.Scheme;

/// <summary>
/// 编译器在调用处内联通用算术时用的助手（iron-034f，designs 03 §6）：两个操作数已知是 fixnum，
/// 结果在 fixnum 范围内时装箱，否则转为大整数（通用 + - *）或返回 null（fx+ fx- fx*：调用方转去慢路径报溢出）。
/// 方法很小，JIT 会内联。
/// </summary>
public static class InlineArith
{
    public static object Add(int a, int b) => Result((long)a + b);
    public static object Subtract(int a, int b) => Result((long)a - b);
    public static object Multiply(int a, int b) => Result((long)a * b);

    /// <summary>int64 结果装箱：在 fixnum 范围内是 fixnum，否则是大整数。拆箱循环（iron-071）溢出转慢路径时也用。</summary>
    public static object Result(long r) => r == (int)r ? RT.Box((int)r) : Builtins.ToIntegerIfPossible(r);

    public static object FxAdd(int a, int b) => FxResult((long)a + b);
    public static object FxSubtract(int a, int b) => FxResult((long)a - b);
    public static object FxMultiply(int a, int b) => FxResult((long)a * b);

    private static object FxResult(long r) => r == (int)r ? RT.Box((int)r) : null;
}

/// <summary>
/// 内联的 call-with-values（iron-034，D6）：生产者的结果是一个值，或 <see cref="MultipleValues"/>。
/// 编译器先比较个数，相符时逐个取出绑定给消费者的形参，不相符时走原来的 call-with-values 报错。
/// </summary>
public static class ValuesPrimitives
{
    [Primitive("$values-count", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object Count(object r) => RT.Box(r is MultipleValues mv ? mv.Length : 1);

    [Primitive("$values-ref", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object Ref(object r, object i) => r is MultipleValues mv ? mv[(int)i] : r;

    /// <summary>第 n 个起的值组成的表（消费者的剩余参数）。</summary>
    [Primitive("$values-tail", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object Tail(object r, object n)
    {
        int from = (int)n;
        if (r is not MultipleValues mv) return from == 0 ? new Cons(r) : EmptyList.Instance;
        object rest = EmptyList.Instance;
        for (int i = mv.Length - 1; i >= from; i--) rest = new Cons(mv[i], rest);
        return rest;
    }
}

/// <summary>
/// typed-lambda 的形参类型（iron-034 D6）：parse 把 Int32 / Double 类型的形参包成 ($as-fixnum x) / ($as-flonum x)，
/// codegen 据此用 int32 / double 局部变量（拆箱）。作为值使用时就是参数本身。
/// </summary>
public static class TypedPrimitives
{
    [Primitive("$as-fixnum", Flags = PrimitiveFlags.Unsafe)]
    public static object AsFixnum(object x) => x;

    [Primitive("$as-flonum", Flags = PrimitiveFlags.Unsafe)]
    public static object AsFlonum(object x) => x;
}
