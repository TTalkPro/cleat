using System;
using System.Runtime.CompilerServices;

namespace Cleat.Scheme;

/// <summary>
/// 无自由变量的过程共用的过程类（iron-034d，D3）。codegen 不再为每个
/// 无自由变量的同步过程生成一个类（IRONSCHEME_NO_034 含 shared 时恢复旧做法）：子句方法放进每单元几个共享的持有类（"#Module$P0" …），过程对象是本类的实例，
/// 由持有类的静态构造函数创建，持有各参数个数入口的委托。直接调用（call 持有类::P&lt;id&gt;$C&lt;i&gt;）不经过它。
/// <para>
/// 尾调用：C# 不能写 tail. 前缀，JIT 只在优化编译时才可能把委托调用做成尾调用（Debug 构建下深度尾递归会栈溢出）。
/// 所以过程对象实际是 codegen 为每个单元生成的子类（"#Module$SP"）的实例，它的 InvokeK / InvokeN 在 Lcil 中
/// 以 tail. callvirt 调委托（含参数打包后交给通用入口的路径）。这里的 C# 实现只是同样语义的后备。
/// </para>
/// </summary>
public class StaticProcedure : Procedure
{
    private readonly string _name;
    private readonly object _arity, _form;
    // 公开只读：codegen 为每个单元生成的子类（"#Module$SP"）在 Lcil 里读它们，用 tail. callvirt 调用
    public readonly Func<object> _f0;
    public readonly Func<object, object> _f1;
    public readonly Func<object, object, object> _f2;
    public readonly Func<object, object, object, object> _f3;
    public readonly Func<object, object, object, object, object> _f4;
    public readonly Func<object[], object> _fn;

    public StaticProcedure(string name, object arity, object form,
                           Func<object> f0, Func<object, object> f1, Func<object, object, object> f2,
                           Func<object, object, object, object> f3, Func<object, object, object, object, object> f4,
                           Func<object[], object> fn)
    {
        _name = name; _arity = arity; _form = form;
        _f0 = f0; _f1 = f1; _f2 = f2; _f3 = f3; _f4 = f4; _fn = fn;
    }

    public override string Name => _name ?? "procedure";
    public override object Arity => _arity;
    public override object Form => _form ?? Unspecified.Instance;

    // 与每过程一个类时相同：有匹配的固定参数子句就调它；否则有通用入口（剩余参数或多于 4 个参数的子句）
    // 就把参数打包交给它；都没有则报参数个数错误
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public override object Invoke0() => _f0 != null ? _f0() : _fn != null ? _fn(Array.Empty<object>()) : ArityError(0);
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public override object Invoke1(object a1) => _f1 != null ? _f1(a1) : _fn != null ? _fn(new[] { a1 }) : ArityError(1);
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public override object Invoke2(object a1, object a2) => _f2 != null ? _f2(a1, a2) : _fn != null ? _fn(new[] { a1, a2 }) : ArityError(2);
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public override object Invoke3(object a1, object a2, object a3) =>
      _f3 != null ? _f3(a1, a2, a3) : _fn != null ? _fn(new[] { a1, a2, a3 }) : ArityError(3);
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public override object Invoke4(object a1, object a2, object a3, object a4) =>
      _f4 != null ? _f4(a1, a2, a3, a4) : _fn != null ? _fn(new[] { a1, a2, a3, a4 }) : ArityError(4);
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public override object InvokeN(object[] args) => _fn != null ? _fn(args) : base.InvokeN(args);

    /// <summary>args[from..] 打包成列表（剩余参数；同 VariadicProcedure.RestList，持有类不是它的子类）。</summary>
    public static object Rest(object[] args, int from)
    {
        object rest = EmptyList.Instance;
        for (int i = args.Length - 1; i >= from; i--) rest = new Cons(args[i], rest);
        return rest;
    }

    /// <summary>通用入口里没有匹配的子句时（与生成的 InvokeN 相同的报错）。</summary>
    public static object NoMatch(object proc, object[] args) => ((Procedure)proc).ArityErrorPublic(args.Length);

    /// <summary>没有对应入口时的参数个数错误（生成的子类调用）。</summary>
    public object ArityFail(int given) => ArityError(given);

    /// <summary>基类的 InvokeN（按长度分派到 InvokeK），生成的子类在没有通用入口时调用。</summary>
    public object DispatchN(object[] args) => base.InvokeN(args);
}
