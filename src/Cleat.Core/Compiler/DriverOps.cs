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
    /// (run-form thunk form) → thunk 的值。异常不包装、原样传出；第一次经过（最内层的形式）时记下 文件:行（<see cref="Diagnostics.Stamp"/>）。
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

    /// <summary>
    /// 当前编译单元的源文件名（cleat-158 的序列点 / PDB 用）：线程局部，不用 Clojure 的动态绑定——理由同 <see cref="State"/>。
    /// </summary>
    public static readonly System.Threading.ThreadLocal<string> SourceFile = new();

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

    /// <summary>
    /// 自举用的改名工具编译器的源码（cleat-091，designs/19 D7）：正在运行的编译器（cleat.compiler.*）不能在同一进程里重新加载自己的源码
    /// 编自己，所以把 from（src/clojure/cleat）下的 nanopass.clj、nanopass/、compiler/ 复制到 to 下的 nanopass0.clj、nanopass0/、compiler0/，
    /// 文本里的命名空间 cleat.nanopass / cleat.compiler 改成 cleat.nanopass0 / cleat.compiler0。
    /// </summary>
    public static void RenameCompilerSources(string from, string to)
    {
        static string Rename(string text) => text.Replace("cleat.nanopass", "cleat.nanopass0").Replace("cleat.compiler", "cleat.compiler0");
        Directory.CreateDirectory(to);
        File.WriteAllText(Path.Combine(to, "nanopass0.clj"), Rename(File.ReadAllText(Path.Combine(from, "nanopass.clj"))));
        foreach (var (src, dst) in new[] { ("nanopass", "nanopass0"), ("compiler", "compiler0") })
        {
            string dir = Path.Combine(to, dst);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            foreach (var f in Directory.GetFiles(Path.Combine(from, src), "*.clj", SearchOption.AllDirectories))
            {
                string target = Path.Combine(dir, Path.GetRelativePath(Path.Combine(from, src), f));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllText(target, Rename(File.ReadAllText(f)));
            }
        }
    }

    /// <summary>dll 的模块 MVID（发射器按内容哈希）：Clojure 镜像的 id，写进接口文件。</summary>
    public static Guid Mvid(string dllPath)
    {
        using var pe = new PEReader(File.OpenRead(dllPath));
        var md = pe.GetMetadataReader();
        return md.GetGuid(md.GetModuleDefinition().Mvid);
    }
}
