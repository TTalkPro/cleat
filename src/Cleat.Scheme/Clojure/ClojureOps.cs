using System;
using System.Threading;
using Cleat.Core.Runtime;
using clojure.lang;

namespace Cleat.Scheme.Clojure;

/// <summary>
/// Clojure 代码的运行时操作。cleat-050 里它们都是 Scheme 过程（<see cref="Procedure"/>），展开结果以常量 <c>((quote 过程) 实参 …)</c>
/// 调用；cleat-051 起编译后的代码经编译器的 Clojure 节点直接调用下面的静态方法（<c>$clj-call</c>）或 clojure.lang 的方法，
/// 这里的过程只剩展开期用的（宏调用、:const 的值、元数据与集合的遍历）。
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

    // —— Var（展开期：:const 的值）——

    public static readonly Procedure VarGet = new Op1("clojure-var-get", v => ((Var)v).get());

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

    // —— 编译后代码直接调用的静态方法（cleat-051 的 $clj-call 节点：参数与返回都是 object，尾部实参可打包成 object[]）——
    // 与上面同名操作的过程语义相同；编译器生成 call ClojureOps::方法，不经 Scheme 过程，也不经 Scheme 互操作的转换。

    public static object VectorOf(object[] items) => clojure.lang.RT.vector(items);
    public static object MapOf(object[] kvs) => clojure.lang.RT.map(kvs);
    public static object SetOf(object[] items) => clojure.lang.RT.set(items);
    public static object WithMetaOf(object o, object m) => ((IObj)o).withMeta((IPersistentMap)m);

    /// <summary>静态方法调用：t 为 Type，name 为成员名，gen 为泛型实参（Type 的 object[]）或 nil。</summary>
    public static object CallStatic(object t, object name, object gen, object[] args) =>
      Reflector.Call(HostSyntax.Real((Type)t), (string)name, null, args, null, Types(gen));

    /// <summary>实例方法调用：t 为静态已知的类型或 nil（按实例的运行时类型）；实例为 nil 抛 NullReferenceException。</summary>
    public static object CallInstance(object t, object name, object inst, object gen, object[] args) =>
      Reflector.Call(HostSyntax.Real((Type)t), (string)name, NotNil(inst, name), args, null, Types(gen));

    public static object GetStaticField(object t, object name) => Reflector.GetField(HostSyntax.Real((Type)t), (string)name, null);
    public static object GetInstanceField(object t, object name, object inst) => Reflector.GetField(HostSyntax.Real((Type)t), (string)name, NotNil(inst, name));

    public static object SetFieldOf(object t, object name, object inst, object v)
    {
        Reflector.SetField(HostSyntax.Real((Type)t), (string)name, inst, v);
        return v;
    }

    /// <summary>类型作为值（类名符号求值）：桩类型换成真正的类型（deftype* 方法体里引用正在定义的类型）。</summary>
    public static object RealType(object t) => HostSyntax.Real((Type)t);

    public static object NewOf(object t, object[] args) => Reflector.New(HostSyntax.Real((Type)t), args);
    public static object IsInstanceOf(object t, object x) => Reflector.Is(HostSyntax.Real((Type)t), x);

    public static object MonitorEnterOf(object x)
    {
        Monitor.Enter(x);
        return null;
    }

    public static object MonitorExitOf(object x)
    {
        Monitor.Exit(x);
        return null;
    }

    /// <summary>
    /// AOT 产物（.clj.dll）入口的第一个形式（cleat-052）：编译时运行的 Clojure boot 镜像 id 与现在的不同则抛
    /// <see cref="Cleat.Core.Runtime.BootInterface.StaleModuleException"/>，加载器回退源码（designs/04 §6）。
    /// </summary>
    public static object CheckBootImage(object imageId)
    {
        Cleat.Core.Runtime.BootInterface.Check(imageId?.ToString());
        return null;
    }

    // —— 编译路径的求值结果（cleat-055 值边界）——
    // 镜像的 compile-core 返回的 thunk 是 (lambda () (clr-static-call ModuleLoader Run asm))：Scheme 互操作把返回值按 Scheme 规范化
    // （long → fixnum Int32 等），Clojure 的顶层值经过它就变了。驱动把 Lcore 包成 ($clj-call 'HoldResult #f 核心)：值在编译后的代码里
    // 直接存进线程槽（不经 Scheme 互操作），thunk 返回 nil，再由过程 TakeResult 原样取出。

    [ThreadStatic] private static object t_result;

    public static object HoldResult(object x)
    {
        t_result = x;
        return null;
    }

    public static readonly Procedure TakeResult = new Op1("clojure-take-result", _ =>
    {
        object r = t_result;
        t_result = null;
        return r;
    });

    /// <summary>Namespace 对象作为表达式（RT 的引导 ns 宏展开为它）：按名字取，AOT 产物里不存 Namespace 常量（cleat-052）。</summary>
    public static object FindNamespaceOf(object name) => Namespace.findOrCreate((clojure.lang.Symbol)name);

    public static object ImportOf(object t)
    {
        ((Namespace)clojure.lang.RT.CurrentNSVar.deref()).importClass(HostSyntax.Real((Type)t));
        return null;
    }

    /// <summary>deftype* 求值时登记生成的类型（classForName 取最新定义的，cleat-051）；值为该类型。</summary>
    public static object DefineType(object t)
    {
        // 两个类型名解析器都要看到最新的定义：Clojure 侧（classForName → Loader）与展开期的 HostSyntax（Scheme 的 TypeResolver）
        Cleat.Core.Runtime.Loader.RegisterType((Type)t);
        Cleat.Scheme.TypeResolver.AddCompileTimeType((Type)t);
        return t;
    }

    // —— deftype* / reify* 方法的值转换（cleat-051）——

    /// <summary>方法的原始类型实参装箱成 Clojure 的值（int → long 等，Reflector.ConvertFrom 的规则）。</summary>
    public static object BoxArg(object x) => Reflector.ConvertFrom(x, null);

    /// <summary>方法体的值（object）→ 声明的返回类型，装箱后由调用方 unbox.any / castclass：按 Reflector.Convert&lt;T&gt;（Clojure 的实参转换规则）。</summary>
    public static object ConvertReturn(object x, Type t)
    {
        if (t == typeof(object)) return x;
        var f = s_converters.GetOrAdd(t, type =>
        {
            var m = typeof(Reflector).GetMethod(nameof(Reflector.Convert)).MakeGenericMethod(type);
            return v => m.Invoke(null, new[] { v });
        });
        try { return f(x); }
        catch (System.Reflection.TargetInvocationException e) when (e.InnerException != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, Func<object, object>> s_converters = new();
}
