using System;
using System.Threading;

namespace Cleat.Scheme;

/// <summary>
/// 参数对象的存储：每个参数一个 <see cref="AsyncLocal{T}"/>，值随 ExecutionContext 流动——parameterize 的值在
/// Task.Run 的子任务里可见；子任务里的赋值不流回。没有设置过的上下文读到回退值：创建时的初始值，以及之后在
/// **创建线程**上的赋值（启动时主线程上 <c>(p v)</c> 设置的库路径、库集合等，宿主的工作线程也要看到）。
/// 其他线程上的赋值只影响自己的上下文。（IronScheme 的 async 帧已删，designs/15 §1。）
/// </summary>
public sealed class ParamSlot
{
    /// <summary>#nil 是 null，而 AsyncLocal 的 null 表示"未设置"，所以 #nil 另用哨兵存放。</summary>
    private static readonly object s_nil = new object();

    private readonly AsyncLocal<object> _local = new AsyncLocal<object>();
    private readonly int _ownerThread = Environment.CurrentManagedThreadId;
    private volatile object _fallback;

    public ParamSlot(object initial) { _fallback = initial; }

    public object Value
    {
        get
        {
            var v = _local.Value;
            return v == null ? _fallback : ReferenceEquals(v, s_nil) ? null : v;
        }
        set
        {
            if (Environment.CurrentManagedThreadId == _ownerThread) _fallback = value;
            _local.Value = value ?? s_nil;
        }
    }
}

/// <summary>make-parameter 用的原语（boot/expander/compat.sls）。</summary>
public static class Parameters
{
    [Primitive("$make-param-slot", Flags = PrimitiveFlags.Unsafe)]
    public static object MakeSlot(object initial) => new ParamSlot(initial);

    [Primitive("$param-ref", Flags = PrimitiveFlags.Unsafe)]
    public static object Ref(object slot) => ((ParamSlot)slot).Value;

    [Primitive("$param-set!", Flags = PrimitiveFlags.Unsafe)]
    public static object Set(object slot, object value)
    {
        ((ParamSlot)slot).Value = value;
        return Unspecified.Instance;
    }
}

/// <summary>
/// 当前异常处理器栈（boot/exceptions.sls 的 with-exception-handler / raise 使用）：Scheme 过程的列表，栈顶在前，
/// 线程静态（IronScheme 的 AsyncLocal 与 async 帧字段已删；cleat-047 把整个处理器栈换成 $try/catch 后本类删除）。
/// 缺省（以及设为空表时）是只含默认处理器的表，默认处理器把条件包成 <see cref="SchemeException"/> 抛出。
/// </summary>
public static class ExceptionHandlers
{
    [ThreadStatic] private static object t_handlers;
    private static readonly object s_default = new Cons(new DefaultHandler());

    [Primitive("$current-handlers", Flags = PrimitiveFlags.Unsafe)]
    public static object Current() => t_handlers is null or EmptyList ? s_default : t_handlers;

    [Primitive("$set-current-handlers!", Flags = PrimitiveFlags.Unsafe)]
    public static object Set(object handlers)
    {
        t_handlers = handlers;
        return Unspecified.Instance;
    }

    private sealed class DefaultHandler : Procedure
    {
        public override object Invoke1(object condition) => throw new SchemeException(condition);
        public override string Name => "default-exception-handler";
    }
}
