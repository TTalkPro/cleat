using System.Runtime.CompilerServices;
using clojure.lang;

namespace Cleat.Core.Runtime;

/// <summary>集合的小工具（cleat-109）。</summary>
public static class Colls
{
    /// <summary>
    /// clojure.core/empty? 的实现：结果与 Clojure 1.12 的 <c>(if (counted? coll) (zero? (count coll)) (not (seq coll)))</c> 相同，
    /// 但先用类检查排除最常见的情形——ASeq（Cons、非空的 PersistentList、Range……）总是非空，LazySeq 看 seq；
    /// .NET 对没实现 Counted 的类型做接口检查要扫完整张接口表（nqueens 里 counted? 占 4%）。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsEmpty(object coll)
    {
        if (coll is null) return true;
        if (coll is ASeq) return false;
        if (coll is LazySeq ls) return ls.seq() is null;
        if (coll is Counted c) return c.count() == 0;
        return RT.seq(coll) is null;
    }
}
