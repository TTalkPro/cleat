using System;
using System.Collections.Generic;
using System.Threading;
using Cleat.Core.Compiler;
using clojure.lang;

namespace Cleat.Core.Runtime;

/// <summary>
/// 编译后的 Clojure 代码直接调用的运行时操作（编译器的 <c>clj-call</c> 节点：参数与返回都是 object，尾部实参可打包成 object[]），
/// 以及 deftype* / proxy / gen-class / async 生成代码的辅助方法。规则与 C# 展开器（cleat-013/014）给编译器的 Lcore 节点相同：
/// 调用 = IFn.invoke，Var 读 = getRawRoot / get，宿主互操作 = Reflector。cleat-082 从 Cleat.Scheme 搬来；展开期用的 Scheme 过程
/// 留在 Cleat.Scheme.Clojure.ExpandOps。
/// </summary>
public static class ClojureOps
{
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

    private static Type[] Types(object o)
    {
        if (o is not object[] a) return null;
        var r = new Type[a.Length];
        for (int i = 0; i < a.Length; i++) r[i] = (Type)a[i];
        return r;
    }

    public static object NotNil(object instance, object name) =>
      instance ?? throw new NullReferenceException($"Cannot invoke member {name} on nil");

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
    /// AOT 产物（包程序集）入口的第一个形式（cleat-052）：编译时运行的 Clojure boot 镜像 id 与现在的不同则抛
    /// <see cref="BootInterface.StaleModuleException"/>（要求重编，designs/17 §7）。
    /// </summary>
    public static object CheckBootImage(object imageId)
    {
        BootInterface.Check(imageId?.ToString());
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

    /// <summary>取出并清空 HoldResult 存的值（Scheme 侧的过程 TakeResult 调它）。</summary>
    public static object TakeHeldResult()
    {
        object r = t_result;
        t_result = null;
        return r;
    }

    /// <summary>Namespace 对象作为表达式（RT 的引导 ns 宏展开为它）：按名字取，AOT 产物里不存 Namespace 常量（cleat-052）。</summary>
    public static object FindNamespaceOf(object name) => Namespace.findOrCreate((clojure.lang.Symbol)name);

    public static object ImportOf(object t)
    {
        // 按名字取最新的定义（cleat-032）：defrecord 展开里的 (import user.R) 在 deftype* 求值之前就解析了类型名，
        // 重定义同名类型时展开期拿到的是旧定义
        var real = HostSyntax.Real((Type)t);
        ((Namespace)clojure.lang.RT.CurrentNSVar.deref()).importClass(Loader.FindType(real.FullName) ?? real);
        return null;
    }

    /// <summary>deftype* 求值时登记生成的类型（classForName 取最新定义的，cleat-051）；值为该类型。</summary>
    public static object DefineType(object t)
    {
        // 两处类型名决议都要看到最新的定义：Clojure 侧（classForName → Loader）与展开期的 HostSyntax（编译期类型登记表）
        Loader.RegisterType((Type)t);
        CompileTimeTypes.Add((Type)t);
        HostSyntax.ClearPendingStub(((Type)t).FullName);
        return t;
    }

    // —— proxy（cleat-043）：生成的方法按方法名在实例的 fn 映射里找实现，找不到（或值为 nil）时走基类方法 / 抛错 ——

    public static IFn ProxyFn(IPersistentMap map, string name) => map?.valAt(name) as IFn;

    /// <summary>update-proxy：并入新映射（值为 nil 的键保留，于是该方法回到缺省行为）。</summary>
    public static IPersistentMap ProxyUpdate(IPersistentMap cur, IPersistentMap m) =>
        (IPersistentMap)((IPersistentCollection)(cur ?? PersistentHashMap.EMPTY)).cons(m);

    // —— ^:async / await*（cleat-043，见 AsyncSupport）——
    public static object AwaitBlocking(object t) => AsyncSupport.AwaitBlocking(t);
    public static object AsyncStart(object thunk) => AsyncSupport.Start(thunk);
    public static object AsyncRunSync(object thunk) => AsyncSupport.RunSync(thunk);

    // —— gen-class（cleat-043）：方法经类型的静态 Var 字段找实现函数 ——

    /// <summary>Var 已绑定时的值（IFn），否则 null（方法走基类实现 / 抛 NotImplementedException）。</summary>
    public static IFn GenClassFn(Var v) => v != null && v.isBound ? v.get() as IFn : null;

    /// <summary>
    /// 类型初始化时加载实现命名空间（:load-impl-ns，缺省为真）：还没加载（不在 *loaded-libs* 里）时 require 它。
    /// 不能看命名空间在不在——初始化器先 intern 方法的 Var，命名空间已经建出来了；ns 形式在文件开头就登记 *loaded-libs*，
    /// 所以加载该命名空间的过程中用到这个类型时不会再次加载。
    /// </summary>
    public static void GenClassLoadImpl(string ns)
    {
        var sym = clojure.lang.Symbol.intern(ns);
        // 与 serialized-require 同一把锁（cleat-067）：类型冷加载时别的线程可能同时 require 同一个命名空间（CleatRuntime.Var）
        lock (clojure.lang.RT.REQUIRE_LOCK)
        {
            var loaded = ((IDeref)clojure.lang.RT.var("clojure.core", "*loaded-libs*").deref()).deref();
            if (!clojure.lang.RT.booleanCast(clojure.lang.RT.contains(loaded, sym)))
                clojure.lang.RT.var("clojure.core", "require").invoke(sym);
        }
    }

    /// <summary>:main 生成的 Main(string[])：以命令行参数调用 -main；-main 返回 Task 时等它完成（ClojureCLR 的做法）。</summary>
    public static void GenClassMain(Var main, string[] args)
    {
        var f = GenClassFn(main) ?? throw new NotImplementedException(main + " is not defined");
        if (f.applyTo(clojure.lang.RT.seq(args)) is System.Threading.Tasks.Task t) t.GetAwaiter().GetResult();
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
