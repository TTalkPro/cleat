using System.Runtime.CompilerServices;

namespace Cleat.Core.Runtime;

/// <summary>
/// 布尔单例与真值判断，生成的代码在每个 if 与布尔结果处调用（cleat-082 从元层搬来）。
/// </summary>
public static class Truth
{
    public static readonly object True = true;
    public static readonly object False = false;

    /// <summary>真值：false 与 nil（.NET null）为假，其余为真。每个 if 都调用：要内联进生成的代码（cleat-109 的剖析里它占 8%）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsTrue(object o) => o != null && (o is not bool b || b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static object Bool(bool b) => b ? True : False;
}
