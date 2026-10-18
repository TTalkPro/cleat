using System;
using clojure.lang;
using System.Threading;

namespace Cleat.Core.Runtime;

/// <summary>
/// cleat-133 / 135 的运行时签名特化钩子。生成的通用入口先观察调用次数；达到阈值后把函数类名与实参类型交给
/// 编译器登记的回调。回调返回特化 IFn 时，通用入口把它写入函数类静态字段；返回 null 或未登记编译器时，
/// 调用始终留在原通用体，因此本类型只提供接合点，不改变任何可见语义。cleat-135 增加进程级生成上限，
/// 防止多签名 / 多函数的按需生成无限消耗内存。
/// </summary>
public static class RuntimeSigSpec
{
    /// <summary>默认观察阈值：先让解释器 / JIT 完成启动调用，再付出一次运行时编译成本。</summary>
    public const int Threshold = 16;

    /// <summary>进程里允许按需生成的特化程序集上限；保守首版预算。</summary>
    public const int MaxGenerated = 4096;

    private static int _generated;

    /// <summary>(函数类名, 实参, 通用函数) → 特化表项；null 表示该签名不能安全特化。由 Clojure 编译器登记。</summary>
    public static Func<string, object[], object, object> Compiler { get; set; }

    /// <summary>构建镜像时关闭：编译器自身的函数会执行超过阈值，递归触发运行时编译会自举死循环。</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>是否还能新增一个运行时生成程序集；达上限后既有特化继续使用，新签名只回通用体。</summary>
    public static bool CanGenerate() => System.Threading.Volatile.Read(ref _generated) < MaxGenerated;

    /// <summary>运行时特化程序集成功发射后登记；失败不占预算。</summary>
    public static void MarkGenerated() => Interlocked.Increment(ref _generated);

    /// <summary>生成代码在慢路径调用；异常一律视作不能特化，调用方回退通用体。</summary>
    public static object Promote(string className, object[] args, object generic)
    {
        var compiler = Compiler;
        if (!Enabled) return null;
        if (compiler is null) return null;
        try
        {
            return compiler(className, args, generic);
        }
        catch
        {
            return null;
        }
    }
}
