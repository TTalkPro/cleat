using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using clojure.lang;

namespace Cleat.Scheme.Clojure;

/// <summary>
/// Scheme 前端在展开期用的 Clojure 操作，都是 Scheme 过程（<see cref="Procedure"/>），展开结果以常量 <c>((quote 过程) 实参 …)</c> 调用：
/// 宏调用、:const 的值、元数据与集合的遍历、编译路径求值结果的取回。编译后代码直接调用的静态方法在 Cleat.Core.Runtime.ClojureOps（cleat-082）。
/// <para>为什么不用 Scheme 的 <c>clr-call</c>：它对返回值（含声明为 object 的）做 <c>Interop.ConvertFrom</c>——long → fixnum（Int32）、
/// 枚举 → 符号、字符串规范化，Clojure 的值经过它就变了（designs/13 §7 的值边界）。过程调用的实参与返回值不经转换。</para>
/// </summary>
public static class ExpandOps
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
            return Cleat.Core.Runtime.ClojureOps.InvokeIFn(f, a);
        }
    }

    // —— 调用 ——

    public static readonly Procedure Invoke = new InvokeProc();
    public static readonly Procedure ApplyTo = new Op2("clojure-apply-to", (f, s) => ((IFn)f).applyTo(clojure.lang.RT.seq(s)));

    // —— Var（展开期：:const 的值）——

    public static readonly Procedure VarGet = new Op1("clojure-var-get", v => ((Var)v).get());

    // —— 宿主互操作（Reflector 的规则；类型为 nil 时按实例的运行时类型）——

    // —— 展开期使用（Clojure 数据的遍历，不经 Scheme 互操作的转换）——

    /// <summary>(seq->list x)：clojure.lang.RT.seq(x) 的元素组成的 Scheme 列表。</summary>
    public static readonly Procedure SeqToList = new Op1("clojure-seq->list", x =>
    {
        var items = new System.Collections.Generic.List<object>();
        for (ISeq s = clojure.lang.RT.seq(x); s != null; s = s.next()) items.Add(s.first());
        object r = EmptyList.Instance;
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
        object r = EmptyList.Instance;
        for (int i = items.Count - 1; i >= 0; i--) r = new Cleat.Scheme.Cons(items[i], r);
        return r;
    });

    public static readonly Procedure Get = new Op2("clojure-get", (m, k) => clojure.lang.RT.get(m, k));
    public static readonly Procedure Meta = new Op1("clojure-meta", x => x is IMeta m ? m.meta() : null);
    public static readonly Procedure ToLong = new Op1("clojure-long", x => Convert.ToInt64(x));
    /// <summary>
    /// 语法对象里的 map / set（子项是注解或语法对象，哈希按对象标识，每个进程不同）重建时保持给定的顺序（cleat-053：否则
    /// 集合字面量展开出的构造参数顺序随进程变化，镜像不是不动点）。map 用 PersistentArrayMap（不查重：子项是不同的语法对象）；
    /// set 用按原位置比较的 PersistentTreeSet。
    /// </summary>
    /// 子项都是 Clojure 数据时（syntax->clj 转回数据）照常建 map / set。
    public static readonly Procedure OrderedMap = new Op1("clojure-ordered-map", kvs =>
    {
        var a = SchemeListToArray(kvs);
        if (!a.Any(IsSchemeObject))
            return a.Length <= 16 ? PersistentArrayMap.createAsIfByAssoc(a) : PersistentHashMap.create(a);
        return new PersistentArrayMap(a);
    });

    public static readonly Procedure OrderedSet = new Op1("clojure-ordered-set", items =>
    {
        var a = SchemeListToArray(items);
        return a.Any(IsSchemeObject) ? OrderedSetOf(a) : PersistentHashSet.create(a);
    });

    private static bool IsSchemeObject(object x) => x != null && x.GetType().Assembly == typeof(Procedure).Assembly;

    private static object[] SchemeListToArray(object list)
    {
        var items = new List<object>();
        for (object p = list; p is Cleat.Scheme.Cons c; p = c.cdr) items.Add(c.car);
        return items.ToArray();
    }

    private static object OrderedSetOf(object[] items)
    {
        var index = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < items.Length; i++) index.TryAdd(items[i] ?? s_nullItem, i);
        // 不是这些子项本身的对象（按值查找成员时）：取与它相等的子项的位置，都不相等的排在最后
        int Pos(object x)
        {
            if (index.TryGetValue(x ?? s_nullItem, out int i)) return i;
            for (int j = 0; j < items.Length; j++)
                if (Util.equiv(items[j], x)) return j;
            return int.MaxValue;
        }
        var cmp = Comparer<object>.Create((a, b) =>
        {
            int c = Pos(a).CompareTo(Pos(b));
            return c != 0 || Util.equiv(a, b) ? c : Util.hasheq(a).CompareTo(Util.hasheq(b));
        });
        return PersistentTreeSet.create(cmp, clojure.lang.RT.seq(items));
    }

    private static readonly object s_nullItem = new object();

    public static readonly Procedure NextId = new Op1("clojure-next-id", _ => clojure.lang.RT.nextID().ToString(System.Globalization.CultureInfo.InvariantCulture));

    // —— 编译路径的求值结果：值由编译后的代码经 Cleat.Core.Runtime.ClojureOps.HoldResult 存进线程槽，这里原样取出（cleat-055）——

    public static readonly Procedure TakeResult = new Op1("clojure-take-result", _ => Cleat.Core.Runtime.ClojureOps.TakeHeldResult());
}
