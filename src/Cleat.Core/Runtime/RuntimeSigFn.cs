using System;
using clojure.lang;

namespace Cleat.Core.Runtime;

/// <summary>
/// cleat-133 / 134 的单签名特化表项：运行时生成只针对观察到的精确 CLR 类型。类型完全一致时调用类型化 IFn；
/// 其他签名或类型化体放弃推测时抛出 TypedBailout，由生成入口的 catch 回到同一个通用体，避免递归调用本包装器。
/// </summary>
public sealed class RuntimeSigFn
{
    private readonly Type[] _types;
    private readonly IFn _specialized;

    public RuntimeSigFn(Type[] types, IFn specialized)
    {
        _types = types;
        _specialized = specialized;
    }

    /// <summary>兼容已编入 seed 的三参构造；回退不再递归调用通用包装器。</summary>
    public RuntimeSigFn(Type[] types, IFn specialized, IFn generic) : this(types, specialized) { }

    public object Invoke()
    {
        if (_types.Length != 0) throw TypedBailout.Instance;
        return _specialized.invoke();
    }

    public object Invoke(object a1)
    {
        if (_types.Length != 1 || a1 is null || a1.GetType() != _types[0]) throw TypedBailout.Instance;
        return _specialized.invoke(a1);
    }

    public object Invoke(object a1, object a2)
    {
        if (_types.Length != 2 || a1 is null || a2 is null
            || a1.GetType() != _types[0] || a2.GetType() != _types[1]) throw TypedBailout.Instance;
        return _specialized.invoke(a1, a2);
    }

    public object Invoke(object a1, object a2, object a3)
    {
        if (_types.Length != 3 || a1 is null || a2 is null || a3 is null
            || a1.GetType() != _types[0] || a2.GetType() != _types[1] || a3.GetType() != _types[2])
            throw TypedBailout.Instance;
        return _specialized.invoke(a1, a2, a3);
    }

    public object Invoke(object a1, object a2, object a3, object a4)
    {
        if (!Matches(a1, a2, a3, a4)) throw TypedBailout.Instance;
        return _specialized.invoke(a1, a2, a3, a4);
    }

    public object Invoke(object a1, object a2, object a3, object a4, object a5)
    {
        if (!Matches(a1, a2, a3, a4, a5)) throw TypedBailout.Instance;
        return _specialized.invoke(a1, a2, a3, a4, a5);
    }

    public object Invoke(object a1, object a2, object a3, object a4, object a5, object a6)
    {
        if (!Matches(a1, a2, a3, a4, a5, a6)) throw TypedBailout.Instance;
        return _specialized.invoke(a1, a2, a3, a4, a5, a6);
    }

    public object Invoke(object a1, object a2, object a3, object a4, object a5, object a6, object a7)
    {
        if (!Matches(a1, a2, a3, a4, a5, a6, a7)) throw TypedBailout.Instance;
        return _specialized.invoke(a1, a2, a3, a4, a5, a6, a7);
    }

    public object Invoke(object a1, object a2, object a3, object a4, object a5, object a6, object a7, object a8)
    {
        if (!Matches(a1, a2, a3, a4, a5, a6, a7, a8)) throw TypedBailout.Instance;
        return _specialized.invoke(a1, a2, a3, a4, a5, a6, a7, a8);
    }

    /// <summary>超过常驻重载 arity 的调用接受一次临时数组；先保证语义安全，预算优化留给后续表项扩展。</summary>
    public object InvokeArray(object[] args)
    {
        if (!Matches(args)) throw TypedBailout.Instance;
        return _specialized.applyTo(RT.arrayToList(args));
    }

    private bool Matches(params object[] args)
    {
        if (args is null || args.Length != _types.Length) return false;
        for (var i = 0; i < args.Length; i++)
            if (args[i] is null || args[i].GetType() != _types[i]) return false;
        return true;
    }
}
