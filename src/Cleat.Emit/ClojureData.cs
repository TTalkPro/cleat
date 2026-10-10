using System;
using System.Linq.Expressions;
using System.Reflection;

namespace Cleat.Emit;

/// <summary>
/// eval 的快速路径：编译器产出的 Lcil S 表达式（Cleat.Core 的 clojure.lang 数据：ISeq / IPersistentVector、Symbol、字符串、
/// 整数、双精度、布尔、nil）直接转成 SNode，不经"写成文本再读回"。发射器不引用 Cleat.Core（designs/05 §1），这里按类型全名识别。
/// 列表与向量都当作 Lcil 列表；nil 当作空表。
/// </summary>
internal static class ClojureData
{
    private static Type s_seqable, s_seq, s_symbol;
    private static Func<object, object> s_seqOf, s_first, s_next;
    private static Func<object, string> s_symbolName, s_symbolNs;

    private static void Init(Assembly core)
    {
        if (s_seqable != null) return;
        s_seqable = core.GetType("clojure.lang.Seqable", true);
        s_seq = core.GetType("clojure.lang.ISeq", true);
        s_symbol = core.GetType("clojure.lang.Symbol", true);
        s_seqOf = Caller<object>(s_seqable, "seq");
        s_first = Caller<object>(s_seq, "first");
        s_next = Caller<object>(s_seq, "next");
        s_symbolName = Caller<string>(s_symbol, "get_Name");
        s_symbolNs = Caller<string>(s_symbol, "get_Namespace");
    }

    private static Func<object, T> Caller<T>(Type t, string method)
    {
        var o = Expression.Parameter(typeof(object), "o");
        var m = t.GetMethod(method, Type.EmptyTypes) ?? throw new LcilException($"{t.FullName}.{method} not found");
        return Expression.Lambda<Func<object, T>>(Expression.Convert(Expression.Call(Expression.Convert(o, t), m), typeof(T)), o).Compile();
    }

    public static SNode Convert(object x, Assembly core)
    {
        Init(core);
        return Node(x);
    }

    private static SNode Node(object x)
    {
        switch (x)
        {
            case null: return new SList();
            case string s: return new SStr(s);
            case bool b: return new SBool(b);
            case int i: return new SInt(i);
            case long l: return new SInt(l);
            case double d: return new SReal(d);
        }
        var t = x.GetType();
        if (s_symbol.IsAssignableFrom(t))
        {
            string ns = s_symbolNs(x);
            return new SSym(ns == null ? s_symbolName(x) : ns + "/" + s_symbolName(x));
        }
        if (s_seqable.IsAssignableFrom(t))
        {
            var l = new SList();
            for (object p = s_seqOf(x); p != null; p = s_next(p)) l.Items.Add(Node(s_first(p)));
            return l;
        }
        if (x is System.Numerics.BigInteger big && big >= long.MinValue && big <= long.MaxValue) return new SInt((long)big);
        throw new LcilException("cannot convert to Lcil: " + t.FullName);
    }
}
