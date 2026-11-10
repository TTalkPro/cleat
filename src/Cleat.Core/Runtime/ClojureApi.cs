using System;
using Cleat.Core.Runtime;
using clojure.lang;

namespace clojure.clr.api;

/// <summary>
/// ClojureCLR 给 C# 宿主的入口（cleat-190，对应 JVM 的 <c>clojure.java.api.Clojure</c>），写法与 ClojureCLR 相同：
/// <code>
/// IFn plus = Clojure.var("clojure.core", "+");
/// plus.invoke(1, 2);
/// IFn require = Clojure.var("clojure.core", "require");
/// require.invoke(Clojure.read("clojure.set"));
/// </code>
/// <see cref="var(object, object)"/> 返回 Var 本身（Var 是 IFn，调用时取当前值；没有就 intern 一个未绑定的，同 JVM），<see cref="read"/> 用
/// <c>clojure.edn/read-string</c>。在 Cleat.Core 里，所以宿主 cleat 里的 Clojure 代码也能 import 它。
/// 惰性启动与包程序集的类型相同（<see cref="RuntimeBootstrap.EnsureStarted"/>：没启动时按名字载入 Cleat.Hosting、以缺省选项
/// 调 <c>CleatRuntime.Init</c>）；要指定镜像、加载路径时先调 <c>Cleat.Core.Hosting.CleatRuntime.Init(options)</c>。
/// 与 <c>CleatRuntime</c> 不同，这里不把调用排到工作线程：调用发生在调用方线程上（源码加载仍经 Hosting 登记的加载线程，见 designs/17 §4.3）。
/// </summary>
public static class Clojure
{
    private static Symbol AsSym(object o) => o switch
    {
        Symbol s => s,
        string str => Symbol.intern(str),
        null => throw new ArgumentNullException(nameof(o)),
        _ => throw new ArgumentException($"expected a string or symbol, got {o.GetType().FullName}"),
    };

    /// <summary>限定名（"clojure.core/+" 或符号）的 Var。</summary>
    public static IFn var(object qualifiedName)
    {
        var s = AsSym(qualifiedName);
        if (s.Namespace == null) throw new ArgumentException($"expected a namespace-qualified name, got {s}");
        return var(s.Namespace, s.Name);
    }

    /// <summary>命名空间 ns 里名为 name 的 Var（字符串或符号）；命名空间或 Var 不存在时创建（同 JVM 的 Var.intern）。</summary>
    public static IFn var(object ns, object name)
    {
        RuntimeBootstrap.EnsureStarted();
        return Var.intern(AsSym(ns), AsSym(name));
    }

    private static Var s_ednReadString;

    /// <summary>按 EDN 读一个值（<c>clojure.edn/read-string</c>）。</summary>
    public static object read(string s)
    {
        RuntimeBootstrap.EnsureStarted();
        return EdnReadString().invoke(s);
    }

    private static Var EdnReadString()
    {
        if (s_ednReadString is { } v) return v;
        var edn = Symbol.intern("clojure.edn");
        var core = Namespace.find(Symbol.intern("clojure.core"))
            ?? throw new InvalidOperationException("clojure.core is not loaded (no boot image?)");
        lock (RT.REQUIRE_LOCK)   // 与 serialized-require 同一把锁（并发的首次调用只加载一次）
        {
            if (Namespace.find(edn)?.FindInternedVar(Symbol.intern("read-string")) is not { } rs || !rs.isBound)
            {
                core.FindInternedVar(Symbol.intern("require")).invoke(edn);
                rs = Namespace.find(edn).FindInternedVar(Symbol.intern("read-string"));
            }
            return s_ednReadString = rs;
        }
    }
}
