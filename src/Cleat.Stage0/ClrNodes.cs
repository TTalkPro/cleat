using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Cleat.Scheme;

namespace Cleat.Stage0;

// clr-*-internal 形式（designs/03-compiler.md §7）。类型说明在分析时连同当时的命名空间列表一起保存，
// 第一次执行时经 TypeResolver 解析并缓存；成员按实参经 Interop 决议（与编译后代码的运行时后备相同）。

/// <summary>类型说明 + 分析时的命名空间列表；#f 表示按实例的运行时类型推断。</summary>
internal sealed class TypeRef
{
    private readonly object _spec;
    private readonly string[] _namespaces;
    private Type _type;

    public TypeRef(object spec, string[] namespaces) { _spec = spec; _namespaces = namespaces; }

    public bool Inferred => _spec is bool b && !b;

    public Type Get(object instance = null)
    {
        if (Inferred) return instance?.GetType() ?? typeof(object);
        if (_type != null) return _type;
        _type = TypeResolver.Resolve(_spec, _namespaces);
        if (_type == null) Errors.SyntaxError("clr", "type not found", _spec, RT.False);
        return _type;
    }

    public static Type[] List(object specs, string[] namespaces) =>
      specs is Cons c ? c.Select(s => new TypeRef(s, namespaces).Get()).ToArray() : Type.EmptyTypes;
}

internal static class NodeHelpers
{
    public static object[] EvalAll(Node[] nodes, Frame f)
    {
        var a = new object[nodes.Length];
        for (int i = 0; i < a.Length; i++) a[i] = nodes[i].Eval(f);
        return a;
    }
}

/// <summary>(clr-call-internal 'T 'member instance arg …)；instance 为 '() 表示静态调用。</summary>
internal sealed class ClrCall : Node
{
    private readonly TypeRef _type;
    private readonly string _member;
    private readonly object _paramSpecs, _genericSpecs;   // 成员说明为 (Name [#(泛型参数 …)] 参数类型 …) 时
    private readonly bool _explicitParams;
    private readonly string[] _namespaces;
    private readonly Node _instance;
    private readonly Node[] _args;
    private Type[] _paramTypes, _genericArgs;

    public ClrCall(TypeRef type, object memberSpec, Node instance, Node[] args, string[] namespaces)
    {
        _type = type; _instance = instance; _args = args; _namespaces = namespaces;
        switch (memberSpec)
        {
            case Symbol s: _member = s.Name; break;
            case string s: _member = s.IndexOf('(') is int i && i >= 0 ? s.Substring(0, i) : s; break;   // 与旧系统一样只取名字
            case Cons c:
                _member = ((Symbol)c.car).Name;
                object rest = c.cdr;
                if (rest is Cons r && r.car is object[] g) { _genericSpecs = Cons.FromArray(g); rest = r.cdr; }
                _paramSpecs = rest;
                _explicitParams = true;
                break;
            default: Errors.SyntaxError("clr-call", "invalid member", memberSpec, RT.False); break;
        }
    }

    public override object Eval(Frame f)
    {
        object inst = _instance.Eval(f);
        var a = NodeHelpers.EvalAll(_args, f);
        if (_explicitParams && _paramTypes == null)
        {
            _paramTypes = TypeRef.List(_paramSpecs, _namespaces);
            _genericArgs = _genericSpecs == null ? null : TypeRef.List(_genericSpecs, _namespaces);
        }
        return Interop.Call(_type.Get(inst), _member, inst, a, _paramTypes, _genericArgs);
    }
}

internal sealed class ClrFieldGet : Node
{
    private readonly TypeRef _type; private readonly string _name; private readonly Node _instance;
    public ClrFieldGet(TypeRef type, string name, Node instance) { _type = type; _name = name; _instance = instance; }
    public override object Eval(Frame f)
    {
        object inst = _instance.Eval(f);
        return Interop.GetField(_type.Get(inst), _name, inst);
    }
}

internal sealed class ClrFieldSet : Node
{
    private readonly TypeRef _type; private readonly string _name; private readonly Node _instance, _value;
    public ClrFieldSet(TypeRef type, string name, Node instance, Node value) { _type = type; _name = name; _instance = instance; _value = value; }
    public override object Eval(Frame f)
    {
        object inst = _instance.Eval(f);
        return Interop.SetField(_type.Get(inst), _name, inst, _value.Eval(f));
    }
}

internal sealed class ClrNew : Node
{
    private readonly TypeRef _type; private readonly Node[] _args;
    public ClrNew(TypeRef type, Node[] args) { _type = type; _args = args; }
    public override object Eval(Frame f) => Interop.New(_type.Get(), NodeHelpers.EvalAll(_args, f));
}

internal sealed class ClrNewArray : Node
{
    private readonly TypeRef _type; private readonly Node _size;
    public ClrNewArray(TypeRef type, Node size) { _type = type; _size = size; }
    public override object Eval(Frame f) => Interop.NewArray(_type.Get(), _size.Eval(f));
}

internal sealed class ClrCast : Node
{
    private readonly TypeRef _type; private readonly Node _value;
    public ClrCast(TypeRef type, Node value) { _type = type; _value = value; }
    public override object Eval(Frame f) => Interop.Cast(_type.Get(), _value.Eval(f));
}

internal sealed class ClrIs : Node
{
    private readonly TypeRef _type; private readonly Node _value;
    public ClrIs(TypeRef type, Node value) { _type = type; _value = value; }
    public override object Eval(Frame f) => Interop.Is(_type.Get(), _value.Eval(f));
}

internal sealed class ClrTypeOf : Node
{
    private readonly TypeRef _type;
    public ClrTypeOf(TypeRef type) { _type = type; }
    public override object Eval(Frame f) => _type.Get();
}

internal static class ClrReferences
{
    /// <summary>clr-reference：按程序集名或路径加载（分析时执行，与旧系统一样是编译期副作用）。</summary>
    public static void Load(object name)
    {
        string s = name is Symbol sym ? sym.Name : name as string;
        if (s == null) { Errors.SyntaxError("clr-reference", "reference is not a symbol or a string", name, RT.False); return; }
        Assembly asm = null;
        try { asm = File.Exists(s) ? Assembly.LoadFrom(Path.GetFullPath(s)) : Assembly.Load(s); }
        catch (FileNotFoundException) { }
        catch (FileLoadException) { }
        if (asm == null) Errors.SyntaxError("clr-reference", "assembly not found", name, RT.False);
    }
}
