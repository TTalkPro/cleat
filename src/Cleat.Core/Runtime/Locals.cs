using System.Runtime.CompilerServices;

namespace Cleat.Core.Runtime;

/// <summary>
/// 局部变量清除（cleat-042 第 5 项）。生成的代码在变量的最后一次引用处经 <c>ldloca</c> / <c>ldarga</c> 调用 <see cref="Take"/>：
/// 取出值并把槽置 null。不能写成 <c>ldloc x; ldnull; stloc x</c>——向一个还挂在求值栈上的局部变量赋值时，JIT 先把栈上的值溢出到
/// 临时变量，而 tier-0 代码的临时变量在整个方法里都报告为活的：清掉了 IL 局部变量，头仍被那个隐藏的临时变量抓着
/// （(reduce + (map inc (range 3e7))) 在 512 MB 堆上限下 OOM，关掉分层编译则正常——就是这个原因）。
/// 优化后的代码里本方法内联掉。
/// </summary>
public static class Locals
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static object Take(ref object slot)
    {
        object v = slot;
        slot = null;
        return v;
    }
}
