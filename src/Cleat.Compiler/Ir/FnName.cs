using clojure.lang;

namespace Cleat.Compiler.Ir;

/// <summary>
/// Clojure <c>fn*</c> 的名字（对应 languages.clj 的 CljFnName 记录）：Class 是 Clojure 类名（ns$name），Variadic 是变长子句的必需参数个数
/// （没有为 null），Prims 是固定子句的原始签名，Async 为 ^:async fn 的体 thunk。
/// S 表达式里写成映射 <c>{:class … :variadic … :prims … :async? …}</c>（Clojure 编译器的记录也是映射，读入时两者都认）。
/// </summary>
public sealed class FnName
{
    public static readonly Keyword ClassKey = Keyword.intern(null, "class");
    public static readonly Keyword VariadicKey = Keyword.intern(null, "variadic");
    public static readonly Keyword PrimsKey = Keyword.intern(null, "prims");
    public static readonly Keyword AsyncKey = Keyword.intern(null, "async?");

    private readonly IPersistentMap _data;

    public FnName(object cls, object variadic, object prims, bool async)
        : this(RT.map(ClassKey, cls, VariadicKey, variadic, PrimsKey, prims, AsyncKey, async))
    {
    }

    /// <summary>全部键（记录上 assoc 的额外键，如 ^:once 的 :once，也保留）。</summary>
    private FnName(IPersistentMap data) { _data = data; }

    public object Class => _data.valAt(ClassKey);
    public object Variadic => _data.valAt(VariadicKey);
    public object Prims => _data.valAt(PrimsKey);
    public bool Async => RT.booleanCast(_data.valAt(AsyncKey));

    /// <summary>其他键的值（:once 等）。</summary>
    public object Get(Keyword k) => _data.valAt(k);

    public IPersistentMap ToData() => _data;

    /// <summary>映射或 Clojure 的 CljFnName 记录 → FnName（记录转成普通映射，键全部保留）。</summary>
    public static FnName FromData(IPersistentMap m)
    {
        IPersistentMap plain = PersistentArrayMap.EMPTY;
        for (var s = m.seq(); s != null; s = s.next())
        {
            var e = (IMapEntry)s.first();
            plain = plain.assoc(e.key(), e.val());
        }
        return new FnName(plain);
    }
}
