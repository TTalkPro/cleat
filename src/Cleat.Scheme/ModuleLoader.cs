using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Cleat.Scheme;

/// <summary>
/// 加载编译产物并调用入口（iron-021）。boot 镜像的入口执行全部顶层代码（定义全局、登记钩子与库）；
/// 库 dll 的入口返回 13 元素的库描述列表，交给展开器的 try-load-from-file 安装；
/// 内存模块（eval）由发射器直接给出 Assembly，调用 <see cref="Run"/>。
/// </summary>
public static class ModuleLoader
{
    /// <summary>为真时把每个加载的模块写到标准错误（旧 Ports.ShowImports）。</summary>
    public static bool ShowImports;

    /// <summary>"~/" 前缀表示运行时所在目录（旧 Ports.GetPath）。</summary>
    public static string ResolvePath(string filename)
    {
        if (filename.StartsWith("~/", StringComparison.Ordinal) || filename.StartsWith("~\\", StringComparison.Ordinal))
            return Path.Combine(Builtins.ApplicationDirectory, filename.Substring(2));
        return filename;
    }

    /// <summary>
    /// 加载程序集。inMemory 为真时按字节加载（不锁文件，可在同一进程中重新编译后再加载）；
    /// 同名 .pdb 与 dll 修改时间相近（5 秒内）时一并加载（旧 Ports.IsSameAge）。
    /// </summary>
    public static Assembly LoadAssembly(string path, bool inMemory = false)
    {
        path = Path.GetFullPath(ResolvePath(path));
        if (!File.Exists(path)) Errors.FileNotFoundViolation("load-module", "file not found", path);
        if (ShowImports) Console.Error.WriteLine("loading " + path);
        if (!inMemory) return Assembly.LoadFrom(path);
        var pdb = Path.ChangeExtension(path, ".pdb");
        bool samePdb = File.Exists(pdb) && Math.Abs((File.GetLastWriteTime(path) - File.GetLastWriteTime(pdb)).TotalSeconds) < 5;
        return samePdb ? Assembly.Load(File.ReadAllBytes(path), File.ReadAllBytes(pdb)) : Assembly.Load(File.ReadAllBytes(path));
    }

    /// <summary>入口方法；没有 Cleat.Core.CleatModuleAttribute（发射器按 Lcil 的 (entry …) 写入）或入口不符合约定时报错。</summary>
    public static MethodInfo EntryPoint(Assembly asm)
    {
        var attr = asm.GetCustomAttributesData().FirstOrDefault(a => a.AttributeType.FullName == typeof(Cleat.Core.CleatModuleAttribute).FullName);
        if (attr == null) throw new InvalidOperationException($"{asm.GetName().Name}: not a Cleat module (no CleatModule attribute)");
        string type = (string)attr.ConstructorArguments[0].Value, method = (string)attr.ConstructorArguments[1].Value;
        var t = asm.GetType(type) ?? throw new InvalidOperationException($"{asm.GetName().Name}: entry type {type} not found");
        var m = t.GetMethod(method, BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
        if (m == null || m.ReturnType != typeof(object))
            throw new InvalidOperationException($"{asm.GetName().Name}: entry {type}::{method} must be public static object {method}()");
        return m;
    }

    /// <summary>调用模块入口并返回其结果；入口抛出的异常原样传出。</summary>
    public static object Run(Assembly asm)
    {
        BootInterface.Register(asm);
        var entry = EntryPoint(asm);
        var f = (Func<object>)Delegate.CreateDelegate(typeof(Func<object>), entry);
        return f();
    }

    public static object LoadModule(string path, bool inMemory = false)
    {
        var asm = LoadAssembly(path, inMemory);
        BootInterface.RegisterInterfaceFile(Path.GetFullPath(ResolvePath(path)), asm);
        return Run(asm);
    }

    /// <summary>
    /// (load-fasl 文件名) → compile-file / compile-library / compile-program 产物（.fasl）入口的返回值（iron-037）。
    /// 按字节加载：不锁文件（同一进程中可重新编译后再加载），每次加载在独立的加载上下文中，同名不冲突。
    /// </summary>
    [Primitive("load-fasl")]
    public static object LoadFasl(object filename) =>
      LoadModule(Errors.RequiresType<string>(filename is SchemeString or System.Text.StringBuilder ? filename.ToString() : filename, "load-fasl"), inMemory: true);

    /// <summary>(load-library-dll 文件名) → 库 dll 入口返回的库描述列表（boot/expander/main.sls 的 load-library-from-dll）。</summary>
    [Primitive("load-library-dll")]
    public static object LoadLibraryDll(object filename) =>
      LoadModule(Errors.RequiresType<string>(filename is SchemeString or System.Text.StringBuilder ? filename.ToString() : filename, "load-library-dll"));
}
