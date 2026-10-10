using System;
using clojure.lang;

namespace Cleat.Scheme.Clojure;

/// <summary>
/// Clojure 的 fn* 值（cleat-050，designs/13 §3 / §7）：clojure.lang.AFunction（于是 IFn、Fn、IObj、IComparer、MethodImplCache 齐全），
/// 包着展开器生成的 Scheme 过程——
/// <list type="bullet">
/// <item><c>_fixed</c>：全部固定参数子句合成的一个 case-lambda；<c>_mask</c> 的第 n 位表示有 n 个参数的子句（n ≤ 20）。</item>
/// <item><c>_variadic</c>：变长子句（若有），参数为"必需参数 + 剩余参数"，剩余参数是 ISeq 或 nil（与 RestFn 的 doInvoke 相同；
/// applyTo 把实参序列的尾部原样传入，所以对无限序列 apply 也成立）。</item>
/// </list>
/// 参数个数不符抛 ArityException，名字为 Clojure 的类名规则 <c>ns$name</c>（消息里 demunge 为 <c>ns/name</c>）。
/// 只经 IFn 调用：Clojure 的调用一律展开为 IFn.invoke（ClojureOps.Invoke），Scheme 代码看不到这个对象。
/// cleat-051 以真实的函数类取代它（调用约定、原语签名）；cleat-055 审计值边界。
/// </summary>
public sealed class ClojureFn : AFunction
{
    private readonly string _name;
    private readonly Procedure _fixed;
    private readonly Procedure _variadic;
    private readonly int _required;
    private readonly int _mask;
    private readonly IPersistentMap _meta;

    public ClojureFn(string name, Procedure fixedArities, Procedure variadic, int required, int mask, IPersistentMap meta = null)
    {
        _name = name;
        _fixed = fixedArities;
        _variadic = variadic;
        _required = required;
        _mask = fixedArities == null ? 0 : mask;
        _meta = meta;
    }

    /// <summary>Clojure 类名形式的名字（ns$name），ArityException 用它。</summary>
    public string Name => _name;

    public override IPersistentMap meta() => _meta;

    public override IObj withMeta(IPersistentMap meta) =>
      meta == _meta ? this : new ClojureFn(_name, _fixed, _variadic, _required, _mask, meta);

    public override bool HasArity(int arity) =>
      arity >= 0 && arity <= 20 && (_mask & (1 << arity)) != 0 || _variadic != null && arity >= _required;

    public override string ToString() => _name;

    private ArityException Arity(int n) => new ArityException(n, _name);

    /// <summary>args 为全部实参（个数 ≥ _required）：前 _required 个照传，其余打包为 ArraySeq（没有则 nil）。</summary>
    private object CallVariadic(object[] args)
    {
        var a = new object[_required + 1];
        Array.Copy(args, a, _required);
        if (args.Length > _required)
        {
            var rest = new object[args.Length - _required];
            Array.Copy(args, _required, rest, 0, rest.Length);
            a[_required] = ArraySeq.create(rest);
        }
        return Invoke(_variadic, a);
    }

    private static object Invoke(Procedure p, object[] a) => a.Length switch
    {
        1 => p.Invoke1(a[0]),
        2 => p.Invoke2(a[0], a[1]),
        3 => p.Invoke3(a[0], a[1], a[2]),
        4 => p.Invoke4(a[0], a[1], a[2], a[3]),
        _ => p.InvokeN(a),
    };

    public override object applyTo(ISeq arglist)
    {
        if (_variadic != null && clojure.lang.RT.BoundedLength(arglist, _required) > _required)
        {
            var a = new object[_required + 1];
            ISeq s = arglist;
            for (int i = 0; i < _required; i++, s = s.next()) a[i] = s.first();
            a[_required] = s;
            return Invoke(_variadic, a);
        }
        return ApplyToHelper(this, arglist);
    }

    public override object invoke()
    {
        if ((_mask & (1 << 0)) != 0) return _fixed.Invoke0();
        if (_variadic != null && 0 >= _required) return CallVariadic(System.Array.Empty<object>());
        throw Arity(0);
    }

    public override object invoke(object a1)
    {
        if ((_mask & (1 << 1)) != 0) return _fixed.Invoke1(a1);
        if (_variadic != null && 1 >= _required) return CallVariadic(new object[] { a1 });
        throw Arity(1);
    }

    public override object invoke(object a1, object a2)
    {
        if ((_mask & (1 << 2)) != 0) return _fixed.Invoke2(a1, a2);
        if (_variadic != null && 2 >= _required) return CallVariadic(new object[] { a1, a2 });
        throw Arity(2);
    }

    public override object invoke(object a1, object a2, object a3)
    {
        if ((_mask & (1 << 3)) != 0) return _fixed.Invoke3(a1, a2, a3);
        if (_variadic != null && 3 >= _required) return CallVariadic(new object[] { a1, a2, a3 });
        throw Arity(3);
    }

    public override object invoke(object a1, object a2, object a3, object a4)
    {
        if ((_mask & (1 << 4)) != 0) return _fixed.Invoke4(a1, a2, a3, a4);
        if (_variadic != null && 4 >= _required) return CallVariadic(new object[] { a1, a2, a3, a4 });
        throw Arity(4);
    }

    public override object invoke(object a1, object a2, object a3, object a4, object a5)
    {
        if ((_mask & (1 << 5)) != 0) return _fixed.InvokeN(new object[] { a1, a2, a3, a4, a5 });
        if (_variadic != null && 5 >= _required) return CallVariadic(new object[] { a1, a2, a3, a4, a5 });
        throw Arity(5);
    }

    public override object invoke(object a1, object a2, object a3, object a4, object a5, object a6)
    {
        if ((_mask & (1 << 6)) != 0) return _fixed.InvokeN(new object[] { a1, a2, a3, a4, a5, a6 });
        if (_variadic != null && 6 >= _required) return CallVariadic(new object[] { a1, a2, a3, a4, a5, a6 });
        throw Arity(6);
    }

    public override object invoke(object a1, object a2, object a3, object a4, object a5, object a6, object a7)
    {
        if ((_mask & (1 << 7)) != 0) return _fixed.InvokeN(new object[] { a1, a2, a3, a4, a5, a6, a7 });
        if (_variadic != null && 7 >= _required) return CallVariadic(new object[] { a1, a2, a3, a4, a5, a6, a7 });
        throw Arity(7);
    }

    public override object invoke(object a1, object a2, object a3, object a4, object a5, object a6, object a7, object a8)
    {
        if ((_mask & (1 << 8)) != 0) return _fixed.InvokeN(new object[] { a1, a2, a3, a4, a5, a6, a7, a8 });
        if (_variadic != null && 8 >= _required) return CallVariadic(new object[] { a1, a2, a3, a4, a5, a6, a7, a8 });
        throw Arity(8);
    }

    public override object invoke(object a1, object a2, object a3, object a4, object a5, object a6, object a7, object a8, object a9)
    {
        if ((_mask & (1 << 9)) != 0) return _fixed.InvokeN(new object[] { a1, a2, a3, a4, a5, a6, a7, a8, a9 });
        if (_variadic != null && 9 >= _required) return CallVariadic(new object[] { a1, a2, a3, a4, a5, a6, a7, a8, a9 });
        throw Arity(9);
    }

    public override object invoke(object a1, object a2, object a3, object a4, object a5, object a6, object a7, object a8, object a9, object a10)
    {
        if ((_mask & (1 << 10)) != 0) return _fixed.InvokeN(new object[] { a1, a2, a3, a4, a5, a6, a7, a8, a9, a10 });
        if (_variadic != null && 10 >= _required) return CallVariadic(new object[] { a1, a2, a3, a4, a5, a6, a7, a8, a9, a10 });
        throw Arity(10);
    }

    public override object invoke(object a1, object a2, object a3, object a4, object a5, object a6, object a7, object a8, object a9, object a10, object a11)
    {
        if ((_mask & (1 << 11)) != 0) return _fixed.InvokeN(new object[] { a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 });
        if (_variadic != null && 11 >= _required) return CallVariadic(new object[] { a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 });
        throw Arity(11);
    }

    public override object invoke(object a1, object a2, object a3, object a4, object a5, object a6, object a7, object a8, object a9, object a10, object a11, object a12)
    {
        if ((_mask & (1 << 12)) != 0) return _fixed.InvokeN(new object[] { a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11, a12 });
        if (_variadic != null && 12 >= _required) return CallVariadic(new object[] { a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11, a12 });
        throw Arity(12);
    }

    public override object invoke(object a1, object a2, object a3, object a4, object a5, object a6, object a7, object a8, object a9, object a10, object a11, object a12, object a13)
    {
        if ((_mask & (1 << 13)) != 0) return _fixed.InvokeN(new object[] { a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11, a12, a13 });
        if (_variadic != null && 13 >= _required) return CallVariadic(new object[] { a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11, a12, a13 });
        throw Arity(13);
    }

    public override object invoke(object a1, object a2, object a3, object a4, object a5, object a6, object a7, object a8, object a9, object a10, object a11, object a12, object a13, object a14)
    {
        if ((_mask & (1 << 14)) != 0) return _fixed.InvokeN(new object[] { a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11, a12, a13, a14 });
        if (_variadic != null && 14 >= _required) return CallVariadic(new object[] { a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11, a12, a13, a14 });
        throw Arity(14);
    }

    public override object invoke(object a1, object a2, object a3, object a4, object a5, object a6, object a7, object a8, object a9, object a10, object a11, object a12, object a13, object a14, object a15)
    {
        if ((_mask & (1 << 15)) != 0) return _fixed.InvokeN(new object[] { a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11, a12, a13, a14, a15 });
        if (_variadic != null && 15 >= _required) return CallVariadic(new object[] { a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11, a12, a13, a14, a15 });
        throw Arity(15);
    }

    public override object invoke(object a1, object a2, object a3, object a4, object a5, object a6, object a7, object a8, object a9, object a10, object a11, object a12, object a13, object a14, object a15, object a16)
    {
        if ((_mask & (1 << 16)) != 0) return _fixed.InvokeN(new object[] { a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11, a12, a13, a14, a15, a16 });
        if (_variadic != null && 16 >= _required) return CallVariadic(new object[] { a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11, a12, a13, a14, a15, a16 });
        throw Arity(16);
    }

    public override object invoke(object a1, object a2, object a3, object a4, object a5, object a6, object a7, object a8, object a9, object a10, object a11, object a12, object a13, object a14, object a15, object a16, object a17)
    {
        if ((_mask & (1 << 17)) != 0) return _fixed.InvokeN(new object[] { a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11, a12, a13, a14, a15, a16, a17 });
        if (_variadic != null && 17 >= _required) return CallVariadic(new object[] { a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11, a12, a13, a14, a15, a16, a17 });
        throw Arity(17);
    }

    public override object invoke(object a1, object a2, object a3, object a4, object a5, object a6, object a7, object a8, object a9, object a10, object a11, object a12, object a13, object a14, object a15, object a16, object a17, object a18)
    {
        if ((_mask & (1 << 18)) != 0) return _fixed.InvokeN(new object[] { a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11, a12, a13, a14, a15, a16, a17, a18 });
        if (_variadic != null && 18 >= _required) return CallVariadic(new object[] { a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11, a12, a13, a14, a15, a16, a17, a18 });
        throw Arity(18);
    }

    public override object invoke(object a1, object a2, object a3, object a4, object a5, object a6, object a7, object a8, object a9, object a10, object a11, object a12, object a13, object a14, object a15, object a16, object a17, object a18, object a19)
    {
        if ((_mask & (1 << 19)) != 0) return _fixed.InvokeN(new object[] { a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11, a12, a13, a14, a15, a16, a17, a18, a19 });
        if (_variadic != null && 19 >= _required) return CallVariadic(new object[] { a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11, a12, a13, a14, a15, a16, a17, a18, a19 });
        throw Arity(19);
    }

    public override object invoke(object a1, object a2, object a3, object a4, object a5, object a6, object a7, object a8, object a9, object a10, object a11, object a12, object a13, object a14, object a15, object a16, object a17, object a18, object a19, object a20)
    {
        if ((_mask & (1 << 20)) != 0) return _fixed.InvokeN(new object[] { a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11, a12, a13, a14, a15, a16, a17, a18, a19, a20 });
        if (_variadic != null && 20 >= _required) return CallVariadic(new object[] { a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11, a12, a13, a14, a15, a16, a17, a18, a19, a20 });
        throw Arity(20);
    }

    public override object invoke(object a1, object a2, object a3, object a4, object a5, object a6, object a7, object a8, object a9, object a10, object a11, object a12, object a13, object a14, object a15, object a16, object a17, object a18, object a19, object a20, params object[] args)
    {
        var all = new object[20 + args.Length];
        object[] head = { a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11, a12, a13, a14, a15, a16, a17, a18, a19, a20 };
        head.CopyTo(all, 0);
        args.CopyTo(all, 20);
        if (_variadic != null && all.Length >= _required) return CallVariadic(all);
        throw Arity(all.Length);
    }
}
