using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading.Tasks;
using clojure.lang;

namespace Cleat.Core.Runtime;

/// <summary>
/// ^:async fn 与 await*（cleat-043，ClojureCLR 的 clojure.clr.async.task.alpha 的表面）。两种实现，编译期按运行时选定：
/// <list type="bullet">
/// <item>运行时 async（.NET 11 起缺省；.NET 10 要环境变量 DOTNET_RuntimeAsync=1）：fn 体编进带 async 实现标志、声明返回
/// Task&lt;object&gt; 的方法，await* 调 AsyncHelpers.Await，挂起与恢复由运行时完成——不占线程，与 C# 的 async 方法同一机制。</item>
/// <item>退化：await* 同步等待（GetAwaiter().GetResult()），fn 返回已完成 / 已失败的 Task。结果相同，只是等待时占着线程。</item>
/// </list>
/// 用运行时 async 编译的 AOT 程序集要在支持它的运行时上加载（类型带 async 方法，不支持时加载失败）。
/// </summary>
public static class AsyncSupport
{
    private static readonly Lazy<bool> s_runtimeAsync = new(Probe);

    /// <summary>运行时 async 是否可用（CLEAT_RUNTIME_ASYNC=0 强制退化）。</summary>
    public static bool RuntimeAsync => s_runtimeAsync.Value;

    private static bool Probe()
    {
        if (Environment.GetEnvironmentVariable("CLEAT_RUNTIME_ASYNC") == "0") return false;
        if (Type.GetType("System.Runtime.CompilerServices.AsyncHelpers, System.Runtime") == null) return false;
        try
        {
            // 定义一个带 async 标志的方法：运行时不支持时 CreateType 抛 TypeLoadException
            var ab = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("cleat$async$probe"), AssemblyBuilderAccess.RunAndCollect);
            var tb = ab.DefineDynamicModule("probe").DefineType("Probe", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
            var mb = tb.DefineMethod("F", MethodAttributes.Public | MethodAttributes.Static, typeof(Task), Type.EmptyTypes);
            mb.SetImplementationFlags((MethodImplAttributes)0x2000);
            mb.GetILGenerator().Emit(OpCodes.Ret);
            tb.CreateType();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Task&lt;object&gt; 的类型名（async 方法的返回类型）。</summary>
    public static string TaskOfObjectName => typeof(Task<object>).FullName;

    /// <summary>await 的对象 → Task：Task / Task&lt;T&gt; 原样，ValueTask / ValueTask&lt;T&gt; 经 AsTask。</summary>
    public static Task ToTask(object t)
    {
        switch (t)
        {
            case Task task: return task;
            case ValueTask vt: return vt.AsTask();
            case null: throw new NullReferenceException("await* of nil");
        }
        var ty = t.GetType();
        if (ty.IsGenericType && ty.GetGenericTypeDefinition() == typeof(ValueTask<>))
            return (Task)ty.GetMethod("AsTask").Invoke(t, null);
        throw new InvalidCastException("await* requires a Task, Task<T>, ValueTask or ValueTask<T>, got: " + ty.FullName);
    }

    private static readonly ConcurrentDictionary<Type, Func<Task, object>> s_results = new();

    /// <summary>完成了的 Task 的值：失败时抛出原来的异常；Task&lt;T&gt; 取 Result（值类型装箱），非泛型 Task 为 nil。</summary>
    public static object ResultOf(Task t)
    {
        t.GetAwaiter().GetResult();
        return s_results.GetOrAdd(t.GetType(), static ty =>
        {
            for (var b = ty; b != null && b != typeof(Task); b = b.BaseType)
                if (b.IsGenericType && b.GetGenericTypeDefinition() == typeof(Task<>))
                {
                    var arg = b.GetGenericArguments()[0];
                    if (arg.Name == "VoidTaskResult") break;
                    var p = b.GetProperty("Result");
                    return task => p.GetValue(task);
                }
            return static _ => null;
        })(t);
    }

    /// <summary>退化实现的 await*：同步等待并取值。</summary>
    public static object AwaitBlocking(object t) => ResultOf(ToTask(t));

    /// <summary>运行时 async：体 thunk 的 invoke 返回它的 Task&lt;object&gt;。</summary>
    public static object Start(object thunk) => ((IFn)thunk).invoke();

    /// <summary>退化实现：同步运行体，值包成已完成的 Task，异常包成已失败的 Task（与 async 方法一致，不同步抛出）。</summary>
    public static object RunSync(object thunk)
    {
        try { return Task.FromResult(((IFn)thunk).invoke()); }
        catch (Exception e) { return Task.FromException<object>(e); }
    }
}
