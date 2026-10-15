namespace Cleat.Core.Runtime;

/// <summary>
/// 无提示函数的类型化入口（cleat-104）：类型化体里的自调用发现 Var 已被重新定义、新函数的结果不是推测的原始类型时抛出，
/// 通用入口捕获后改走通用体重算（类型化体只对无副作用的函数生成，重算安全）。预先分配，不带有意义的栈。
/// </summary>
public sealed class TypedBailout : System.Exception
{
    public static readonly TypedBailout Instance = new();

    private TypedBailout() : base("typed entry bailout") { }

    /// <summary>经 Var 调用到的不是本函数时，结果按推测的 long 取回</summary>
    public static long Long(object o) => o is long l ? l : throw Instance;

    /// <summary>同上，double</summary>
    public static double Double(object o) => o is double d ? d : throw Instance;
}
