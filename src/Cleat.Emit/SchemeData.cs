using System;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;

namespace Cleat.Emit;

/// <summary>
/// eval 的快速路径（iron-033；从 IronScheme 纳入）：Scheme 编译器产出的 Lcil S 表达式（Cleat.Scheme 的 Cons、Symbol 等）直接转成 SNode，
/// 不经"写成文本再读回"。发射器不引用 Cleat.Scheme（designs/05 §1），这里按类型全名识别，用表达式树生成字段读取器。
/// 可以出现的原子与 write-lcil-datum（compiler/constants.sls）一致：符号、字符串、精确整数、flonum、#t/#f、空表。
/// Clojure 数据的对应物是 <see cref="ClojureData"/>；发射器按顶层对象所属的程序集二选一。
/// </summary>
internal static class SchemeData
{
    private static Type s_consType, s_symbolType, s_emptyType;
    private static Func<object, object> s_car, s_cdr;
    private static Func<object, string> s_symbolName;

    private static Func<object, T> Getter<T>(Type t, string field)
    {
        var o = Expression.Parameter(typeof(object), "o");
        var f = t.GetField(field, BindingFlags.Public | BindingFlags.Instance) ?? throw new LcilException($"{t.FullName}.{field} not found");
        return Expression.Lambda<Func<object, T>>(Expression.Convert(Expression.Field(Expression.Convert(o, t), f), typeof(T)), o).Compile();
    }

    private static void Init(Assembly scheme)
    {
        if (s_consType != null) return;
        s_symbolType = scheme.GetType("Cleat.Scheme.Symbol", true);
        s_symbolName = Getter<string>(s_symbolType, "Name");
        var cons = scheme.GetType("Cleat.Scheme.Cons", true);
        s_car = Getter<object>(cons, "car");
        s_cdr = Getter<object>(cons, "cdr");
        s_emptyType = scheme.GetType("Cleat.Scheme.EmptyList", true);
        s_consType = cons;
    }

    public static SNode Convert(object x, Assembly scheme)
    {
        Init(scheme);
        return Node(x);
    }

    private static SNode Node(object x)
    {
        switch (x)
        {
            case null: throw new LcilException("#nil in Lcil data");
            case string s: return new SStr(s);
            case StringBuilder sb: return new SStr(sb.ToString());
            case bool b: return new SBool(b);
            case int i: return new SInt(i);
            case long l: return new SInt(l);
            case double d: return new SReal(d);
        }
        var t = x.GetType();
        if (t == s_emptyType) return new SList();
        // Scheme 字符串的另一种表示（可变串、含 BMP 外字符的串，iron-055）：取其 UTF-16 文本
        if (t.FullName == "Cleat.Scheme.SchemeString") return new SStr(x.ToString());
        if (t == s_consType)
        {
            var l = new SList();
            object p = x;
            while (p.GetType() != s_emptyType)
            {
                if (p.GetType() != s_consType) throw new LcilException("improper list in Lcil data");
                l.Items.Add(Node(s_car(p)));
                p = s_cdr(p);
            }
            return l;
        }
        if (t == s_symbolType) return new SSym(s_symbolName(x));
        // bignum：Lcil 中的整数操作数都在 long 范围内
        if (x is System.Numerics.BigInteger big && big >= long.MinValue && big <= long.MaxValue) return new SInt((long)big);
        throw new LcilException("cannot convert to Lcil: " + t.FullName);
    }
}
