using System.Collections.Concurrent;
using System.Threading;

namespace Cleat.Core.Runtime;

/// <summary>
/// 内存模式（eval）的常量直接以对象传给生成的程序集，不经编码（designs/05 §3）：
/// 发射器登记常量数组得到编号，生成的 $Constants 类型初始化器按编号取回（取一次后移除）。
/// </summary>
public static class ConstantPool
{
    private static readonly ConcurrentDictionary<int, object[]> s_pool = new ConcurrentDictionary<int, object[]>();
    private static int s_next;

    public static int Register(object[] constants)
    {
        int id = Interlocked.Increment(ref s_next);
        s_pool[id] = constants;
        return id;
    }

    public static object[] Take(int id) => s_pool.TryRemove(id, out var c) ? c : new object[0];
}
