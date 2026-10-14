using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using clojure.lang;

namespace Cleat.Core.Runtime;

/// <summary>
/// 原语表（designs/02-runtime.md §5、04 §5）：编译器可以静态调用 / 内联的运行时静态方法。
/// 集中声明在这里而不是在 clojure.lang/ 的 EPL 文件上加属性；PrimitiveRegistry 据此在 cleat.kernel 命名空间里绑定同名 Var。
/// 每项：Clojure 名字、所在类型、方法名、参数类型、标志（:pure 可常量折叠；:inline 发射器有内联模板）。
/// 编译器按 (类型, 方法, 参数类型) 查表，core.clj 里的 (. clojure.lang.RT (first x)) 这类宿主调用因此可内联。
/// </summary>
public static class Primitives
{
    public sealed class Entry
    {
        public string Name;          // cleat.kernel/<name>
        public Type Type;
        public string Method;
        public Type[] Params;
        public bool Pure, Inline;
        public MethodInfo MethodInfo => Type.GetMethod(Method, BindingFlags.Public | BindingFlags.Static, null, Params, null)
                                        ?? throw new MissingMethodException(Type.FullName, Method);
        public override string ToString() => $"{Name} -> {Type.FullName}.{Method}({string.Join(",", Params.Select(p => p.FullName))})";
    }

    private static readonly Type O = typeof(object), S = typeof(string), I = typeof(int);

    private static Entry P(string name, Type t, string method, Type[] ps, bool pure = false, bool inline = false)
        => new Entry { Name = name, Type = t, Method = method, Params = ps, Pure = pure, Inline = inline };

    private static readonly Type RTT = typeof(RT), NT = typeof(Numbers), UT = typeof(Util);

    public static readonly IReadOnlyList<Entry> Table = new[]
    {
        // 序列与集合（RT）
        P("first", RTT, "first", new[] { O }, inline: true), P("next", RTT, "next", new[] { O }, inline: true), P("more", RTT, "more", new[] { O }, inline: true),
        P("seq", RTT, "seq", new[] { O }, inline: true), P("cons", RTT, "cons", new[] { O, O }), P("conj", RTT, "conj", new[] { typeof(IPersistentCollection), O }),
        P("assoc", RTT, "assoc", new[] { O, O, O }), P("get", RTT, "get", new[] { O, O }, inline: true), P("get3", RTT, "get", new[] { O, O, O }),
        P("nth", RTT, "nth", new[] { O, I }, inline: true), P("nth3", RTT, "nth", new[] { O, I, O }), P("count", RTT, "count", new[] { O }, inline: true),
        P("list0", RTT, "list", Type.EmptyTypes, pure: true), P("list1", RTT, "list", new[] { O }), P("list2", RTT, "list", new[] { O, O }),
        P("vector", RTT, "vector", new[] { typeof(object[]) }), P("map", RTT, "map", new[] { typeof(object[]) }), P("set", RTT, "set", new[] { typeof(object[]) }),
        P("subvec", RTT, "subvec", new[] { typeof(IPersistentVector), I, I }), P("iter", RTT, "iter", new[] { O }), P("reduced?", RTT, "isReduced", new[] { O }, pure: true),
        P("keyword", RTT, "keyword", new[] { S, S }, pure: true), P("boolean-cast", RTT, "booleanCast", new[] { O }, pure: true, inline: true),
        P("int-cast", RTT, "intCast", new[] { O }, pure: true), P("long-cast", RTT, "longCast", new[] { O }, pure: true), P("double-cast", RTT, "doubleCast", new[] { O }, pure: true),
        P("char-cast", RTT, "charCast", new[] { O }, pure: true), P("print-string", RTT, "printString", new[] { O }),
        // 数值（Numbers，装箱重载；拆箱版留给 cleat-042）
        P("add", NT, "add", new[] { O, O }, pure: true, inline: true), P("minus", NT, "minus", new[] { O, O }, pure: true, inline: true),
        P("negate", NT, "minus", new[] { O }, pure: true), P("multiply", NT, "multiply", new[] { O, O }, pure: true, inline: true),
        P("divide", NT, "divide", new[] { O, O }, pure: true), P("inc", NT, "inc", new[] { O }, pure: true, inline: true), P("dec", NT, "dec", new[] { O }, pure: true, inline: true),
        P("lt", NT, "lt", new[] { O, O }, pure: true, inline: true), P("lte", NT, "lte", new[] { O, O }, pure: true, inline: true),
        P("gt", NT, "gt", new[] { O, O }, pure: true, inline: true), P("gte", NT, "gte", new[] { O, O }, pure: true, inline: true),
        P("num-equiv", NT, "equiv", new[] { O, O }, pure: true, inline: true), P("zero?", NT, "isZero", new[] { O }, pure: true, inline: true),
        P("pos?", NT, "isPos", new[] { O }, pure: true), P("neg?", NT, "isNeg", new[] { O }, pure: true),
        P("quotient", NT, "quotient", new[] { O, O }, pure: true), P("remainder", NT, "remainder", new[] { O, O }, pure: true),
        P("bit-and", NT, "and", new[] { O, O }, pure: true), P("bit-or", NT, "or", new[] { O, O }, pure: true), P("bit-xor", NT, "xor", new[] { O, O }, pure: true),
        P("bit-shift-left", NT, "shiftLeft", new[] { O, O }, pure: true),
        P("bit-shift-right", NT, "shiftRight", new[] { O, O }, pure: true), P("unsigned-bit-shift-right", NT, "unsignedShiftRight", new[] { typeof(long), typeof(long) }, pure: true),
        P("max", NT, "max", new[] { O, O }, pure: true), P("min", NT, "min", new[] { O, O }, pure: true),
        P("num", NT, "num", new[] { O }, pure: true),
        // 相等与哈希（Util）
        P("equiv", UT, "equiv", new[] { O, O }, pure: true, inline: true), P("equals", UT, "equals", new[] { O, O }, pure: true),
        P("identical?", UT, "identical", new[] { O, O }, pure: true, inline: true), P("hash", UT, "hash", new[] { O }, pure: true),
        P("hasheq", UT, "hasheq", new[] { O }, pure: true), P("compare", UT, "compare", new[] { O, O }, pure: true), P("class-of", UT, "classOf", new[] { O }, pure: true),
        // 宿主类型名（Kernel，cleat-053：core.clj 原来直接调 HostExpr）
        P("maybe-special-tag", typeof(Kernel), "MaybeSpecialTag", new[] { O }), P("maybe-type", typeof(Kernel), "MaybeType", new[] { O, O }),
    };

    public static Entry Find(Type type, string method, Type[] ps) =>
      Table.FirstOrDefault(e => e.Type == type && e.Method == method && e.Params.SequenceEqual(ps));
}

/// <summary>原语包装成 IFn：按参数个数选重载，参数经 Reflector.Convert、返回值经 ConvertFrom。</summary>
public sealed class PrimitiveFn : AFunction
{
    private readonly string _name;
    private readonly Dictionary<int, Func<object[], object>> _byArity = new Dictionary<int, Func<object[], object>>();

    public PrimitiveFn(string name, IEnumerable<Primitives.Entry> overloads)
    {
        _name = name;
        foreach (var e in overloads)
        {
            var m = e.MethodInfo;
            var ps = m.GetParameters();
            bool isParams = ps.Length == 1 && ps[0].ParameterType == typeof(object[]);
            var args = Expression.Parameter(typeof(object[]), "args");
            Expression call;
            if (isParams) call = Expression.Call(m, args);
            else
                call = Expression.Call(m, ps.Select((p, i) =>
                  (Expression)Expression.Call(typeof(Reflector).GetMethod("Convert").MakeGenericMethod(p.ParameterType), Expression.ArrayIndex(args, Expression.Constant(i)))).ToArray());
            Expression body = m.ReturnType == typeof(void)
              ? Expression.Block(call, Expression.Constant(null, typeof(object)))
              : Expression.Call(typeof(Reflector).GetMethod("ConvertFrom"), Expression.Convert(call, typeof(object)), Expression.Constant(m.ReturnType));
            var f = Expression.Lambda<Func<object[], object>>(body, args).Compile();
            if (isParams) _byArity[-1] = f; else _byArity[ps.Length] = f;
        }
    }

    private object Call(params object[] args)
    {
        if (_byArity.TryGetValue(args.Length, out var f) || _byArity.TryGetValue(-1, out f)) return f(args);
        throw new ArityException(args.Length, _name);
    }

    public override object invoke() => Call();
    public override object invoke(object a) => Call(a);
    public override object invoke(object a, object b) => Call(a, b);
    public override object invoke(object a, object b, object c) => Call(a, b, c);
    public override object invoke(object a, object b, object c, object d) => Call(a, b, c, d);
    public override object applyTo(ISeq args) => Call(RT.toArray(args));
    public override string ToString() => "#primitive[" + _name + "]";
}

/// <summary>启动时把原语绑到 cleat.kernel/&lt;name&gt;（designs/02 §5）。</summary>
public static class PrimitiveRegistry
{
    public static readonly Symbol KernelNs = Symbol.intern("cleat.kernel");
    private static bool s_done;

    public static Namespace RegisterAll()
    {
        var ns = Namespace.findOrCreate(KernelNs);
        if (s_done) return ns;
        foreach (var g in Primitives.Table.GroupBy(e => e.Name))
        {
            var v = Var.intern(ns, Symbol.intern(g.Key), new PrimitiveFn("cleat.kernel/" + g.Key, g));
            v.setMeta(RT.map(Keyword.intern("primitive"), true));
        }
        s_done = true;
        return ns;
    }
}
