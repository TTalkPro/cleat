using System;
using Cleat.Core.Runtime;

namespace Cleat.Compiler;

/// <summary>
/// 编译器的入口（designs/30 §2 D1）：<c>Startup.EnsureCompiler</c> 按名字加载本程序集（Cleat.Compiler）并调用 <see cref="Install"/>，
/// 登记运行时钩子与镜像、包的编译入口。
/// </summary>
public static class CompilerHooks
{
    /// <summary>登记 eval / load-source / compile / macroexpand-1 与镜像、包的编译入口。</summary>
    public static void Install()
    {
        RuntimeHooks.Eval = Driver.CsDriver.EvalHook;
        RuntimeHooks.LoadSource = Driver.CsDriver.LoadHook;
        RuntimeHooks.Compile = Driver.CsDriver.CompileHook;
        RuntimeHooks.Macroexpand1 = (form, _) => Driver.CsDriver.Macroexpand1(form);
        Cleat.Core.Compiler.DriverOps.InstallCompileHooks(new Driver.CsDriver.CompileBootFn(), new Driver.CsDriver.CompilePackageFn());
    }
}
