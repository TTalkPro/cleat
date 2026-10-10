using System;
using System.Threading;
using Cleat.Core.Runtime;
using clojure.lang;

namespace Cleat.Scheme.Clojure;

/// <summary>
/// Clojure 代码的运行时操作（cleat-050）：都是 Scheme 过程（<see cref="Procedure"/>），由 <c>(clojure syntax)</c> 的展开结果以
/// 常量 <c>((quote 过程) 实参 …)</c> 调用。
/// <para>为什么不用 Scheme 的 <c>clr-call</c>：它对返回值（含声明为 object 的）做 <c>Interop.ConvertFrom</c>——long → fixnum（Int32）、
/// 枚举 → 符号、字符串规范化，Clojure 的值经过它就变了（designs/13 §7 的值边界）。过程调用的实参与返回值不经转换。</para>
/// 规则与 C# 展开器（cleat-013/014）给编译器的 Lcore 节点相同：调用 = IFn.invoke，Var 读 = getRawRoot / get，宿主互操作 = Reflector。
/// cleat-051 的编译器节点取代这些过程。
/// </summary>
public static class ClojureOps
{
    // —— 过程的外壳 ——

    private sealed class Op1 : Procedure
    {
        private readonly string _name;
        private readonly Func<object, object> _f;
        public Op1(string name, Func<object, object> f) { _name = name; _f = f; }
        public override string Name => _name;
        public override object Invoke1(object a) => _f(a);
    }

    private sealed class Op2 : Procedure
    {
        private readonly string _name;
        private readonly Func<object, object, object> _f;
        public Op2(string name, Func<object, object, object> f) { _name = name; _f = f; }
        public override string Name => _name;
        public override object Invoke2(object a, object b) => _f(a, b);
    }

    private sealed class Op3 : Procedure
    {
        private readonly string _name;
        private readonly Func<object, object, object, object> _f;
        public Op3(string name, Func<object, object, object, object> f) { _name = name; _f = f; }
        public override string Name => _name;
        public override object Invoke3(object a, object b, object c) => _f(a, b, c);
    }

    private sealed class Op4 : Procedure
    {
        private readonly string _name;
        private readonly Func<object, object, object, object, object> _f;
        public Op4(string name, Func<object, object, object, object, object> f) { _name = name; _f = f; }
        public override string Name => _name;
        public override object Invoke4(object a, object b, object c, object d) => _f(a, b, c, d);
    }

    private sealed class OpN : VariadicProcedure
    {
        private readonly string _name;
        private readonly Func<object[], object> _f;
        public OpN(string name, Func<object[], object> f) { _name = name; _f = f; }
        public override string Name => _name;
        public override object InvokeN(object[] args) => _f(args);
    }

    /// <summary>(invoke f a …)：f 当作 IFn 调用（不是 IFn 时与 ClojureCLR 一样抛 InvalidCastException）。</summary>
    private sealed class InvokeProc : Procedure
    {
        public override string Name => "clojure-invoke";
        public override object Invoke1(object f) => ((IFn)f).invoke();
        public override object Invoke2(object f, object a) => ((IFn)f).invoke(a);
        public override object Invoke3(object f, object a, object b) => ((IFn)f).invoke(a, b);
        public override object Invoke4(object f, object a, object b, object c) => ((IFn)f).invoke(a, b, c);
        public override object InvokeN(object[] args)
        {
            if (args.Length == 0) return ArityErrorPublic(0);
            var f = (IFn)args[0];
            var a = new object[args.Length - 1];
            Array.Copy(args, 1, a, 0, a.Length);
            return InvokeIFn(f, a);
        }
    }

    /// <summary>IFn 按实参个数分派（超过 20 个经 applyTo）。</summary>
    public static object InvokeIFn(IFn f, object[] a) => a.Length switch
    {
        0 => f.invoke(),
        1 => f.invoke(a[0]),
        2 => f.invoke(a[0], a[1]),
        3 => f.invoke(a[0], a[1], a[2]),
        4 => f.invoke(a[0], a[1], a[2], a[3]),
        5 => f.invoke(a[0], a[1], a[2], a[3], a[4]),
        6 => f.invoke(a[0], a[1], a[2], a[3], a[4], a[5]),
        7 => f.invoke(a[0], a[1], a[2], a[3], a[4], a[5], a[6]),
        8 => f.invoke(a[0], a[1], a[2], a[3], a[4], a[5], a[6], a[7]),
        _ => f.applyTo(ArraySeq.create(a)),
    };

    // —— 调用 ——

    public static readonly Procedure Invoke = new InvokeProc();
    public static readonly Procedure ApplyTo = new Op2("clojure-apply-to", (f, s) => ((IFn)f).applyTo(clojure.lang.RT.seq(s)));

    /// <summary>(make-fn 名字 固定子句过程|#f 变长过程|#f 必需参数个数 固定个数位图)</summary>
    public static readonly Procedure MakeFn = new OpN("clojure-make-fn", a =>
      new ClojureFn(a[0].ToString(), a[1] as Procedure, a[2] as Procedure, Convert.ToInt32(a[3]), Convert.ToInt32(a[4])));

    // —— Var ——

    public static readonly Procedure VarRoot = new Op1("clojure-var-root", v => ((Var)v).getRawRoot());
    public static readonly Procedure VarGet = new Op1("clojure-var-get", v => ((Var)v).get());
    public static readonly Procedure VarSet = new Op2("clojure-var-set", (v, x) => ((Var)v).set(x));
    public static readonly Procedure VarBindRoot = new Op2("clojure-var-bind-root", (v, x) => { ((Var)v).bindRoot(x); return v; });
    public static readonly Procedure VarSetMeta = new Op2("clojure-var-set-meta", (v, m) => { ((Var)v).setMeta((IPersistentMap)m); return v; });

    // —— 集合字面量（与 Clojure 的 VectorExpr / MapExpr / SetExpr 相同：map 与 set 运行时检查重复键）——

    public static readonly Procedure Vector = new OpN("clojure-vector", a => clojure.lang.RT.vector((object[])a.Clone()));
    public static readonly Procedure HashMap = new OpN("clojure-map", a => clojure.lang.RT.map((object[])a.Clone()));
    public static readonly Procedure HashSet = new OpN("clojure-set", a => clojure.lang.RT.set((object[])a.Clone()));
    public static readonly Procedure WithMeta = new Op2("clojure-with-meta", (o, m) => ((IObj)o).withMeta((IPersistentMap)m));

    // —— 宿主互操作（Reflector 的规则；类型为 nil 时按实例的运行时类型）——

    private static Type[] Types(object o)
    {
        if (o is not object[] a) return null;
        var r = new Type[a.Length];
        for (int i = 0; i < a.Length; i++) r[i] = (Type)a[i];
        return r;
    }

    private static object NotNil(object instance, object name) =>
      instance ?? throw new NullReferenceException($"Cannot invoke member {name} on nil");

    /// <summary>(host-call 类型|nil 名字 实参数组 泛型实参|nil)：静态调用。</summary>
    public static readonly Procedure StaticCall = new Op4("clojure-static-call", (t, name, args, gen) =>
      Reflector.Call((Type)t, (string)name, null, (object[])args, null, Types(gen)));

    /// <summary>(instance-call 类型|nil 名字 实例 实参数组 泛型实参|nil)：实例为 nil 抛 NullReferenceException（不当作静态调用）。</summary>
    public static readonly Procedure InstanceCall = new OpN("clojure-instance-call", a =>
      Reflector.Call((Type)a[0], (string)a[1], NotNil(a[2], a[1]), (object[])a[3], null, Types(a[4])));

    public static readonly Procedure StaticField = new Op2("clojure-static-field", (t, name) => Reflector.GetField((Type)t, (string)name, null));
    public static readonly Procedure InstanceField = new Op3("clojure-instance-field", (t, name, inst) =>
      Reflector.GetField((Type)t, (string)name, NotNil(inst, name)));

    /// <summary>(set-field 类型|nil 名字 实例|nil 值)：返回值（set! 的值）。</summary>
    public static readonly Procedure SetField = new Op4("clojure-set-field", (t, name, inst, v) =>
    {
        Reflector.SetField((Type)t, (string)name, inst, v);
        return v;
    });

    public static readonly Procedure New = new Op2("clojure-new", (t, args) => Reflector.New((Type)t, (object[])args));
    public static readonly Procedure IsInstance = new Op2("clojure-instance?", (t, x) => Reflector.Is((Type)t, x));

    public static readonly Procedure MonitorEnter = new Op1("clojure-monitor-enter", x => { Monitor.Enter(x); return null; });
    public static readonly Procedure MonitorExit = new Op1("clojure-monitor-exit", x => { Monitor.Exit(x); return null; });

    /// <summary>(import 类型)：导入 *ns*（运行时，与 ClojureCLR 的 ImportExpr 相同；值为 nil）。</summary>
    public static readonly Procedure Import = new Op1("clojure-import", t =>
    {
        ((Namespace)clojure.lang.RT.CurrentNSVar.deref()).importClass((Type)t);
        return null;
    });

    // —— case*（ClojureCLR CaseExpr 的语义；分支由展开结果按下标分派）——

    /// <summary>
    /// (case-index x shift mask 种类 哈希数组 测试数组 跳过检查数组) → 命中的分支下标，或 -1（default）。
    /// 种类：0 = :int（非整数 → default；键为 ConvertToInt）、1 = :hash-equiv（Util.hash，比较用 equiv）、2 = :hash-identity（比较用同一性）；
    /// 跳过检查的分支（哈希碰撞，由 case 宏生成 condp）不比较测试值。
    /// </summary>
    public static readonly Procedure CaseIndex = new OpN("clojure-case-index", a =>
    {
        object x = a[0];
        int shift = Convert.ToInt32(a[1]), mask = Convert.ToInt32(a[2]), kind = Convert.ToInt32(a[3]);
        var hashes = (object[])a[4];
        var tests = (object[])a[5];
        var skip = (object[])a[6];
        int key;
        if (kind == 0)
        {
            if (!Util.IsNonCharNumeric(x)) return -1;
            key = Util.ConvertToInt(x);
        }
        else key = Util.hash(x);
        if (mask != 0) key = (key >> shift) & mask;
        for (int i = 0; i < hashes.Length; i++)
        {
            if (Convert.ToInt32(hashes[i]) != key) continue;
            if (skip[i] is true) return i;
            bool hit = kind == 2 ? ReferenceEquals(x, tests[i]) : Util.equiv(x, tests[i]);
            return hit ? i : -1;
        }
        return -1;
    });

    // —— 展开期使用（Clojure 数据的遍历，不经 Scheme 互操作的转换）——

    /// <summary>(seq->list x)：clojure.lang.RT.seq(x) 的元素组成的 Scheme 列表。</summary>
    public static readonly Procedure SeqToList = new Op1("clojure-seq->list", x =>
    {
        var items = new System.Collections.Generic.List<object>();
        for (ISeq s = clojure.lang.RT.seq(x); s != null; s = s.next()) items.Add(s.first());
        object r = null;
        for (int i = items.Count - 1; i >= 0; i--) r = new Cleat.Scheme.Cons(items[i], r);
        return r;
    });

    /// <summary>(map->list m)：k v k v … 的 Scheme 列表（seq 顺序）。</summary>
    public static readonly Procedure MapToList = new Op1("clojure-map->list", m =>
    {
        var items = new System.Collections.Generic.List<object>();
        for (ISeq s = clojure.lang.RT.seq(m); s != null; s = s.next())
        {
            var e = (IMapEntry)s.first();
            items.Add(e.key());
            items.Add(e.val());
        }
        object r = null;
        for (int i = items.Count - 1; i >= 0; i--) r = new Cleat.Scheme.Cons(items[i], r);
        return r;
    });

    public static readonly Procedure Get = new Op2("clojure-get", (m, k) => clojure.lang.RT.get(m, k));
    public static readonly Procedure Meta = new Op1("clojure-meta", x => x is IMeta m ? m.meta() : null);
    public static readonly Procedure ToLong = new Op1("clojure-long", x => Convert.ToInt64(x));
    public static readonly Procedure NextId = new Op1("clojure-next-id", _ => clojure.lang.RT.nextID().ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>需要 cleat-051 编译器节点的形式（deftype* / reify*）的占位：运行时报错。</summary>
    public static readonly Procedure Unsupported = new OpN("clojure-unsupported", a =>
      throw new NotSupportedException($"{a[0]} is not supported yet: it needs the cleat-051 compiler (Clojure nodes: real CLR types)"));
}
