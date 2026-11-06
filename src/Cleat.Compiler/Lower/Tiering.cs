using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using Cleat.Compiler.Ir;
using Cleat.Core.Runtime;
using clojure.lang;
using Symbol = clojure.lang.Symbol;
using Ref = Cleat.Compiler.Ir.Ref;

namespace Cleat.Compiler.Lower;

/// <summary>
/// MarkTierClasses（cleat-180，designs/30 §4 的"分层"组；codegen.clj mark-tier-classes! 与 *tiered-method* 的各处行为，cleat-114 / 117）：
/// 会被反复调用或长时间循环的函数类分层编译（方法带 <see cref="MethodDecl.Tiered"/>，发射器不加 AggressiveOptimization），
/// 这些类的方法里局部变量的最后一次读经 Locals.Take、值汇合经局部变量（tier-0 把 JIT 临时变量报告为整个方法内活着）。
/// eval 单元总是做；镜像与包按 <c>imagetier</c>（cleat-117）。
/// </summary>
internal static partial class CodeGen
{
    internal static readonly bool OptImageTier = !OptFlags.Off("imagetier");

    [ThreadStatic] private static bool s_tieredMethod;

    /// <summary>
    /// 正在生成分层编译（tier-0 起步）的函数类的方法（codegen.clj *tiered-method*）。与 Clojure 的动态绑定相同：在 <see cref="CljFnClass"/>
    /// 里按类设置、生成完恢复，嵌套的 fn / reify 在生成期间看到的是外层的值，直到它们自己的 CljFnClass 重新设置。
    /// </summary>
    internal static bool TieredMethod => s_tieredMethod;

    /// <summary>
    /// 转发方法把形参交给真正的实现（codegen.clj arg-take!）：分层方法经 Take（tier-0 里形参槽在调用期间还报告为活），其余直接 ldarg。
    /// </summary>
    internal static void ArgTake(MethodCtx mc, int j)
    {
        if (TieredMethod)
        {
            mc.Emit(OpCodes.Ldarga, j);
            mc.Emit(OpCodes.Call, Rt.LocalsTake);
        }
        else mc.Emit(OpCodes.Ldarg, j);
    }

    /// <summary>
    /// codegen.clj mark-tier-classes!：体里有循环的任何 fn、经自己的 Var 以同样的参数个数调用自己的 defn（自递归）、letfn 里调用同组绑定的 fn
    /// （局部递归 / 互递归）的类名记入 TierClasses。只调用一次、没有循环的 fn（-main 一类）照旧直接按优化编译。形式外面可能套着 HoldResult。
    /// </summary>
    internal static void MarkTierClasses(Expr form)
    {
        void Mark(Expr le)
        {
            if (le is Lambda { Name: FnName nm }) Tabs.TierClasses.Add((string)nm.Class);
        }
        L3Some(e =>
        {
            switch (e)
            {
                // 只看 lambda 自己的体（不进嵌套的 lambda）：-main 里嵌套一个带循环的闭包时，-main 本身只运行一次，不能停在 tier-0
                case Lambda:
                    if (L3Some(RealLoop, e, own: true)) Mark(e);
                    return false;
                // 自递归：某个子句经自己的 Var 以同样的参数个数调用自己（可以在嵌套的 lambda 里，如 map 的 lazy-seq）。只是引用 Var 不算
                // （defprotocol 的分派函数读自己 Var 上的缓存），不同 arity 之间的委托也不算（transduce 的 3 参版本调 4 参版本）——
                // 只调用一次时它们停在 tier-0，把实参抓到被调用的归约结束（cleat-117）
                case CljDef d:
                    {
                        bool Own(Expr f) => f is CljVarRef vr && ReferenceEquals(vr.Var, d.Datum);
                        const int Any = -2;
                        int SelfArgc(Expr n) => n switch
                        {
                            CljInvoke ci when Own(ci.Fn) => ci.Args.Length,
                            CljStaticCall sc when Own(sc.Args[0]) => sc.Args.Length - 1,
                            CljCall { Op: "TypedSelf" } => Any,
                            CljCall { Op: "KnownCall" or "PrimInvoke" } cc when Own(cc.Args[1]) => cc.Args.Length - 2,
                            _ => -1,
                        };
                        if (d.Expr0 is Lambda l
                            && l.Clauses.Any(cl =>
                            {
                                int k = cl.Params.Length;
                                bool rest = cl.Rest != null;
                                return L3Some(n =>
                                {
                                    int c = SelfArgc(n);
                                    return c == Any || (c >= 0 && (rest ? c >= k : c == k));
                                }, cl.Body);
                            }))
                            Mark(l);
                        return false;
                    }
                case Letrec lr:
                    {
                        var names = new HashSet<Symbol>(lr.Vars);
                        // 调用同组的绑定才算（defprotocol 的分派函数 G__ 把自己传给缓存函数，只是引用）
                        foreach (var le in lr.Inits)
                            if (L3Some(n => n is CljInvoke { Fn: Ref f } && names.Contains(f.Name) || n is Call { Fn: Ref g } && names.Contains(g.Name), le))
                                Mark(le);
                        return false;
                    }
                default: return false;
            }
        }, form);
    }
}
