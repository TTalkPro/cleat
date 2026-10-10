using System;
using System.Collections.Generic;

namespace Cleat.Scheme;

/// <summary>控制相关原语：apply、call-with-values。设计：designs/02-runtime.md §4。逃逸续延（call/cc）已删除（cleat-047，designs/15 §3）。</summary>
public static class Control
{
    private static Procedure Proc(object o, string who) => Errors.RequiresType<Procedure>(o, who);

    /// <summary>以参数数组调用过程（统一入口）。</summary>
    public static object Call(object proc, object[] args) => Proc(proc, "apply").InvokeN(args);

    /// <summary>(apply proc arg ... list)：最后一个参数必须是正规列表。</summary>
    [Primitive("apply", Flags = PrimitiveFlags.AllowTailCall)]
    public static object Apply(object proc, params object[] args)
    {
        var p = Proc(proc, "apply");
        if (args.Length == 0)
            return Errors.AssertionViolation("apply", "expected at least 2 arguments", proc);

        var all = new List<object>(args.Length + 4);
        for (int i = 0; i < args.Length - 1; i++) all.Add(args[i]);
        object tail = args[args.Length - 1];
        for (; tail is Cons c; tail = c.cdr) all.Add(c.car);
        if (tail != null)
            return Errors.AssertionViolation("apply", "last argument is not a proper list", args[args.Length - 1]);

        return all.Count switch
        {
            0 => p.Invoke0(),
            1 => p.Invoke1(all[0]),
            2 => p.Invoke2(all[0], all[1]),
            3 => p.Invoke3(all[0], all[1], all[2]),
            4 => p.Invoke4(all[0], all[1], all[2], all[3]),
            _ => p.InvokeN(all.ToArray()),
        };
    }

    /// <summary>(values obj ...)：在 Scheme 中定义（boot/exceptions.sls），此处供编译器与 stage0 使用。</summary>
    public static object Values(params object[] args) => args.Length == 1 ? args[0] : new MultipleValues(args);

    [Primitive("call-with-values", Flags = PrimitiveFlags.AllowTailCall)]
    public static object CallWithValues(object producer, object consumer)
    {
        var c = Proc(consumer, "call-with-values");
        object r = Proc(producer, "call-with-values").Invoke0();
        return r is MultipleValues mv ? c.InvokeN(mv.ToArray()) : c.Invoke1(r);
    }
}
