using System;
using clojure.lang;

namespace Cleat.Core.Runtime;

/// <summary>
/// cleat-133 的单签名特化表项：运行时生成只针对观察到的精确 CLR 类型。类型完全一致时调用类型化 IFn；
/// 其他签名或类型化体放弃推测时调用编译时的通用函数。通用函数按对象捕获，因此旧函数对象在 with-redefs 后仍保持旧语义。
/// </summary>
public sealed class RuntimeSigFn
{
    private readonly Type[] _types;
    private readonly IFn _specialized;
    private readonly IFn _generic;

    public RuntimeSigFn(Type[] types, IFn specialized, IFn generic)
    {
        _types = types;
        _specialized = specialized;
        _generic = generic;
    }

    public object Invoke()
    {
        if (_types.Length != 0) return _generic.invoke();
        try { return _specialized.invoke(); }
        catch (TypedBailout) { return _generic.invoke(); }
    }

    public object Invoke(object a1)
    {
        if (_types.Length != 1 || a1 is null || a1.GetType() != _types[0]) return _generic.invoke(a1);
        try { return _specialized.invoke(a1); }
        catch (TypedBailout) { return _generic.invoke(a1); }
    }

    public object Invoke(object a1, object a2)
    {
        if (_types.Length != 2 || a1 is null || a2 is null
            || a1.GetType() != _types[0] || a2.GetType() != _types[1]) return _generic.invoke(a1, a2);
        try { return _specialized.invoke(a1, a2); }
        catch (TypedBailout) { return _generic.invoke(a1, a2); }
    }

    public object Invoke(object a1, object a2, object a3)
    {
        if (_types.Length != 3 || a1 is null || a2 is null || a3 is null
            || a1.GetType() != _types[0] || a2.GetType() != _types[1] || a3.GetType() != _types[2])
            return _generic.invoke(a1, a2, a3);
        try { return _specialized.invoke(a1, a2, a3); }
        catch (TypedBailout) { return _generic.invoke(a1, a2, a3); }
    }

    public object Invoke(object a1, object a2, object a3, object a4)
    {
        if (!Matches(a1, a2, a3, a4)) return _generic.invoke(a1, a2, a3, a4);
        try { return _specialized.invoke(a1, a2, a3, a4); }
        catch (TypedBailout) { return _generic.invoke(a1, a2, a3, a4); }
    }

    public object Invoke(object a1, object a2, object a3, object a4, object a5)
    {
        if (!Matches(a1, a2, a3, a4, a5)) return _generic.invoke(a1, a2, a3, a4, a5);
        try { return _specialized.invoke(a1, a2, a3, a4, a5); }
        catch (TypedBailout) { return _generic.invoke(a1, a2, a3, a4, a5); }
    }

    public object Invoke(object a1, object a2, object a3, object a4, object a5, object a6)
    {
        if (!Matches(a1, a2, a3, a4, a5, a6)) return _generic.invoke(a1, a2, a3, a4, a5, a6);
        try { return _specialized.invoke(a1, a2, a3, a4, a5, a6); }
        catch (TypedBailout) { return _generic.invoke(a1, a2, a3, a4, a5, a6); }
    }

    public object Invoke(object a1, object a2, object a3, object a4, object a5, object a6, object a7)
    {
        if (!Matches(a1, a2, a3, a4, a5, a6, a7)) return _generic.invoke(a1, a2, a3, a4, a5, a6, a7);
        try { return _specialized.invoke(a1, a2, a3, a4, a5, a6, a7); }
        catch (TypedBailout) { return _generic.invoke(a1, a2, a3, a4, a5, a6, a7); }
    }

    public object Invoke(object a1, object a2, object a3, object a4, object a5, object a6, object a7, object a8)
    {
        if (!Matches(a1, a2, a3, a4, a5, a6, a7, a8)) return _generic.invoke(a1, a2, a3, a4, a5, a6, a7, a8);
        try { return _specialized.invoke(a1, a2, a3, a4, a5, a6, a7, a8); }
        catch (TypedBailout) { return _generic.invoke(a1, a2, a3, a4, a5, a6, a7, a8); }
    }

    /// <summary>超过常驻重载 arity 的调用接受一次临时数组；先保证语义安全，预算优化留给后续表项扩展。</summary>
    public object InvokeArray(object[] args)
    {
        var seq = RT.arrayToList(args);
        if (!Matches(args)) return _generic.applyTo(seq);
        try { return _specialized.applyTo(seq); }
        catch (TypedBailout) { return _generic.applyTo(seq); }
    }

    private bool Matches(params object[] args)
    {
        if (args is null || args.Length != _types.Length) return false;
        for (var i = 0; i < args.Length; i++)
            if (args[i] is null || args[i].GetType() != _types[i]) return false;
        return true;
    }
}
