using System;
using System.Collections.Generic;

namespace Cleat.Scheme;

/// <summary>控制相关原语：apply、call-with-values、只支持逃逸续延的 call-with-return（cleat-058，skeg 13 §2）。设计：designs/02-runtime.md §4、§6。</summary>
public static class Control
{
    private static Procedure Proc(object o, string who) => Errors.RequiresType<Procedure>(o, who);

    /// <summary>以参数数组调用过程（统一入口）。</summary>
    public static object Call(object proc, object[] args) => Proc(proc, "apply").InvokeN(args);

    /// <summary>(apply proc arg ... list)：最后一个参数必须是正规列表。
    /// 先数出实参个数，不超过 4 个时直接 Invoke0..4，不建中间表（iron-067）。</summary>
    [Primitive("apply", Flags = PrimitiveFlags.AllowTailCall)]
    public static object Apply(object proc, params object[] args)
    {
        var p = Proc(proc, "apply");
        if (args.Length == 0)
            return Errors.AssertionViolation("apply", "expected at least 2 arguments", proc);

        int fixedCount = args.Length - 1;
        object list = args[fixedCount];
        int count = fixedCount;
        object tail = list;
        for (; tail is Cons c; tail = c.cdr) count++;
        if (tail is not EmptyList)
            return Errors.AssertionViolation("apply", "last argument is not a proper list", list);

        if (count > 4)
        {
            var all = new object[count];
            Array.Copy(args, all, fixedCount);
            int i = fixedCount;
            for (var t = list; t is Cons c; t = c.cdr) all[i++] = c.car;
            return p.InvokeN(all);
        }
        object a0 = null, a1 = null, a2 = null, a3 = null;
        int k = 0;
        void Put(object v)
        {
            switch (k++)
            {
                case 0: a0 = v; break;
                case 1: a1 = v; break;
                case 2: a2 = v; break;
                default: a3 = v; break;
            }
        }
        for (int i = 0; i < fixedCount; i++) Put(args[i]);
        for (var t = list; t is Cons c; t = c.cdr) Put(c.car);
        return count switch
        {
            0 => p.Invoke0(),
            1 => p.Invoke1(a0),
            2 => p.Invoke2(a0, a1),
            3 => p.Invoke3(a0, a1, a2),
            _ => p.Invoke4(a0, a1, a2, a3),
        };
    }

    /// <summary>(values obj ...)：在 Scheme 中定义（boot/exceptions.sls），此处供编译器与 stage0 使用。</summary>
    public static object Values(params object[] args) => args.Length == 1 ? args[0] : new MultipleValues(args);

    [Primitive("call-with-values", Flags = PrimitiveFlags.AllowTailCall)]
    public static object CallWithValues(object producer, object consumer)
    {
        var c = Proc(consumer, "call-with-values");
        object r = Proc(producer, "call-with-values").Invoke0();
        if (r is not MultipleValues mv) return c.Invoke1(r);
        return mv.Length switch
        {
            0 => c.Invoke0(),
            2 => c.Invoke2(mv[0], mv[1]),
            3 => c.Invoke3(mv[0], mv[1], mv[2]),
            _ => c.InvokeN(mv.ToArray()),
        };
    }

    /// <summary>
    /// call-with-return（原 call/cc，cleat-058）：只支持逃逸续延（一次性、只能向上返回到捕获帧）。
    /// 捕获帧退出后（无论正常返回还是异常）续延失效，再调用会报明确的错误。
    /// </summary>
    [Primitive("call-with-return")]
    public static object CallWithReturn(object receiver)
    {
        var r = Proc(receiver, "call-with-return");
        var k = new EscapeContinuation();
        try
        {
            return r.Invoke1(k);
        }
        catch (ContinuationException e) when (ReferenceEquals(e.Continuation, k))
        {
            return e.Value;
        }
        finally
        {
            k.Invalidate();
        }
    }
}

/// <summary>逃逸续延：调用时抛出 <see cref="ContinuationException"/>，由捕获帧按身份接住。</summary>
public sealed class EscapeContinuation : VariadicProcedure
{
    private volatile bool _valid = true;

    public bool IsValid => _valid;

    /// <summary>捕获帧退出时调用（codegen 生成的 escape-frame 也调用，10 §3.3），之后续延失效。</summary>
    public void Invalidate() => _valid = false;

    public override string Name => "continuation";

    public override object InvokeN(object[] args)
    {
        if (!_valid)
            return Errors.AssertionViolation("continuation",
              "continuation is no longer valid (only escaping, one-shot continuations are supported)");
        throw new ContinuationException(this, args.Length == 1 ? args[0] : new MultipleValues(args));
    }
}

/// <summary>
/// 续延控制流用的异常。不是错误；with-clr-exception-handler、Clojure 的 try 等所有 CLR catch 必须放行。
/// </summary>
public sealed class ContinuationException : Exception
{
    public EscapeContinuation Continuation { get; }
    public object Value { get; }

    public ContinuationException(EscapeContinuation k, object value) : base("continuation invoked")
    {
        Continuation = k;
        Value = value;
    }
}
