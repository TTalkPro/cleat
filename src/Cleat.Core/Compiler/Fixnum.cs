using clojure.lang;

namespace Cleat.Core.Compiler;

/// <summary>
/// (fixnum n) → 装箱的 System.Int32（cleat-088）。Clojure 代码里的整数都是 long，宿主调用返回的 Int32 也被 RetObject 换成 long；
/// Clojure 编译器在 cleat-092 之前照 Scheme 版生成 Scheme 数据（过程的 arity、fixnum 常量），要真正的 Int32，经 IFn 调用取得。
/// </summary>
public sealed class Fixnum : AFn
{
    public static readonly Fixnum Instance = new();

    public override object invoke(object n) => n switch
    {
        int i => i,
        long l => (int)l,
        _ => System.Convert.ToInt32(n),
    };
}
