using System;

namespace Cleat.Scheme;

/// <summary>
/// Scheme 过程。设计：designs/02-runtime.md §4。
///
/// 调用约定：
/// - 参数个数 0 ~ 4 的调用走 <c>InvokeK</c>，其余走 <see cref="InvokeN"/>。
/// - <see cref="InvokeN"/> 是统一入口：任何参数个数都可以用它调用。
///
/// 固定参数个数的过程（含 case-lambda 中没有剩余参数的情形）继承本类，
/// 只重写对应个数的 <c>InvokeK</c>；默认 <see cref="InvokeN"/> 按长度分派到 <c>InvokeK</c>，
/// 未重写的 <c>InvokeK</c> 报参数个数错误。
/// 有剩余参数的过程继承 <see cref="VariadicProcedure"/>。
/// </summary>
public abstract class Procedure
{
    public const int MaxFixedArity = 4;

    public virtual object Invoke0() => ArityError(0);
    public virtual object Invoke1(object a1) => ArityError(1);
    public virtual object Invoke2(object a1, object a2) => ArityError(2);
    public virtual object Invoke3(object a1, object a2, object a3) => ArityError(3);
    public virtual object Invoke4(object a1, object a2, object a3, object a4) => ArityError(4);

    public virtual object InvokeN(object[] args) => args.Length switch
    {
        0 => Invoke0(),
        1 => Invoke1(args[0]),
        2 => Invoke2(args[0], args[1]),
        3 => Invoke3(args[0], args[1], args[2]),
        4 => Invoke4(args[0], args[1], args[2], args[3]),
        _ => ArityError(args.Length),
    };

    // 与旧运行时 Callable.Call 同名的便捷调用（非虚，直接转发；移植的 C# 代码沿用这些名字）
    public object Call() => Invoke0();
    public object Call(object a1) => Invoke1(a1);
    public object Call(object a1, object a2) => Invoke2(a1, a2);
    public object Call(object a1, object a2, object a3) => Invoke3(a1, a2, a3);
    public object Call(object a1, object a2, object a3, object a4) => Invoke4(a1, a2, a3, a4);
    public object Call(params object[] args) => InvokeN(args);

    /// <summary>过程名，用于错误信息与打印。编译器生成的类重写为源码中的名字。</summary>
    public virtual string Name => GetType().Name;

    /// <summary>procedure-arity 的结果；未知时为 unspecified（boot/misc.sls）。</summary>
    public virtual object Arity => Unspecified.Instance;

    /// <summary>procedure-form 的结果（过程的形参表）；未知时为 unspecified（boot/misc.sls）。</summary>
    public virtual object Form => Unspecified.Instance;

    public override string ToString() => "#<procedure " + Name + ">";

    internal object ArityErrorPublic(int given) => ArityError(given);

    protected object ArityError(int given) =>
      Errors.AssertionViolation(Name, "incorrect number of arguments", RT.Box(given));
}

/// <summary>
/// 有剩余参数的过程。子类实现 <see cref="InvokeN"/>（在其中打包剩余参数为列表）；
/// <c>InvokeK</c> 默认转发到 <see cref="InvokeN"/>，编译器可以为常见个数另行重写以免分配数组。
/// </summary>
public abstract class VariadicProcedure : Procedure
{
    public override object Invoke0() => InvokeN(Array.Empty<object>());
    public override object Invoke1(object a1) => InvokeN(new[] { a1 });
    public override object Invoke2(object a1, object a2) => InvokeN(new[] { a1, a2 });
    public override object Invoke3(object a1, object a2, object a3) => InvokeN(new[] { a1, a2, a3 });
    public override object Invoke4(object a1, object a2, object a3, object a4) => InvokeN(new[] { a1, a2, a3, a4 });

    public abstract override object InvokeN(object[] args);

    /// <summary>把 args[from..] 打包成列表，供子类实现剩余参数。</summary>
    protected static object RestList(object[] args, int from)
    {
        object rest = EmptyList.Instance;
        for (int i = args.Length - 1; i >= from; i--) rest = new Cons(args[i], rest);
        return rest;
    }
}

/// <summary>被赋值且被闭包捕获的变量（赋值转换，03 §4 第 5 步）。</summary>
public sealed class Box
{
    public object Value;
    public Box(object value) { Value = value; }
}
