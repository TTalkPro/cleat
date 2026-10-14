using System;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Cleat.Core.Runtime;
using clojure.lang;

namespace Cleat.Core.Compiler;

/// <summary>
/// Clojure 写的编译驱动（cleat-090，cleat.compiler.driver）用的宿主操作。以 IFn 给出的几个，结果经 IFn 调用交回、不经 RetObject
/// （宿主调用的 Object 返回值会把 Int32 换成 long；形式的值要原样交出）。
/// </summary>
public static class DriverOps
{
    /// <summary>
    /// (run-form thunk form) → thunk 的值。异常不包装、原样传出；第一次经过（最内层的形式）时记下 文件:行（<see cref="Diagnostics.Stamp"/>），
    /// 与 Scheme 驱动的 RunForm 相同。
    /// </summary>
    public static readonly IFn RunForm = new RunFormFn();

    /// <summary>(take-result) → 取出并清空 <see cref="ClojureOps.HoldResult"/> 存的值。</summary>
    public static readonly IFn TakeResult = new TakeResultFn();

    /// <summary>(run-module 程序集) → 内存模块入口（CleatModule 特性指明的类型与方法）的返回值；异常原样传出。</summary>
    public static readonly IFn RunModule = new RunModuleFn();

    private sealed class RunModuleFn : AFn
    {
        public override object invoke(object asm)
        {
            var a = (System.Reflection.Assembly)asm;
            var attr = System.Linq.Enumerable.First(a.GetCustomAttributesData(), x => x.AttributeType.FullName == typeof(CleatModuleAttribute).FullName);
            var t = a.GetType((string)attr.ConstructorArguments[0].Value!)!;
            var m = t.GetMethod((string)attr.ConstructorArguments[1].Value!, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static, null, Type.EmptyTypes, null)!;
            return ((Func<object>)Delegate.CreateDelegate(typeof(Func<object>), m))();
        }
    }

    private sealed class RunFormFn : AFn
    {
        public override object invoke(object thunk, object form)
        {
            try { return ((IFn)thunk).invoke(); }
            catch (Exception e) when (Diagnostics.Stamp(e, form)) { throw; }
        }
    }

    private sealed class TakeResultFn : AFn
    {
        public override object invoke() => ClojureOps.TakeHeldResult();
    }

    /// <summary>
    /// 驱动的线程状态（正在收集的单元、是否在构建镜像、包程序集）：不用 Clojure 的动态绑定——用户代码里不成对的 pushThreadBindings
    /// （如 REPL 里先 push 再在下一个形式 set!）会被驱动自己的 binding 弹出时一起弹掉。
    /// </summary>
    public static readonly System.Threading.ThreadLocal<object> State = new();

    /// <summary>简单形式的求值次数与编译执行的次数（度量与测试用；两个驱动共用）。</summary>
    public static long SimpleCount, CompiledCount;

    /// <summary>环境变量 CLEAT_NO_SIMPLE_EVAL 设置时关闭简单形式的解释（度量与对照测试用）。</summary>
    public static bool SimpleEvalEnabled = Environment.GetEnvironmentVariable("CLEAT_NO_SIMPLE_EVAL") == null;

    public static void CountSimple() => SimpleCount++;
    public static void CountCompiled() => CompiledCount++;

    /// <summary>(compile-boot 形式列表 dll路径) 与 (compile-package 包名 形式列表 根目录列表或true dll路径)：Clojure 驱动登记（install-hooks!）。</summary>
    public static IFn CompileBootHook, CompilePackageHook;

    public static void InstallCompileHooks(IFn compileBoot, IFn compilePackage)
    {
        CompileBootHook = compileBoot;
        CompilePackageHook = compilePackage;
    }

    /// <summary>接口文件条目的可内联子句（Clojure 驱动写 .inl，cleat-090）。</summary>
    public static void AddInline(InterfaceFile.Entry e, int arity, byte[] data) => (e.Inline ??= new System.Collections.Generic.Dictionary<int, byte[]>())[arity] = data;

    /// <summary>dll 的模块 MVID（发射器按内容哈希）：Clojure 镜像的 id，写进接口文件。</summary>
    public static Guid Mvid(string dllPath)
    {
        using var pe = new PEReader(File.OpenRead(dllPath));
        var md = pe.GetMetadataReader();
        return md.GetGuid(md.GetModuleDefinition().Mvid);
    }
}
