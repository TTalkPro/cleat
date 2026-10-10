using System;
using clojure.lang;

namespace Cleat.Core.Runtime;

/// <summary>
/// cleat.kernel 里不属于 RT / Numbers / Util 的原语（cleat-017 列出、cleat-053 补齐）：core.clj 原来直接调用 ClojureCLR 编译器
/// （<c>HostExpr.maybeSpecialTag</c>、<c>HostExpr.MaybeType</c>）的地方。
/// </summary>
public static class Kernel
{
    private static readonly char[] s_triggerTypeChars = ['`', ',', '[', '&', '*'];

    /// <summary><c>HostExpr.maybeSpecialTag</c>：<c>int long … objects longs …</c> → 类型，否则 nil。</summary>
    public static object MaybeSpecialTag(object sym)
        => sym is Symbol s && s.Namespace == null ? TypeResolver.SpecialTag(s.Name) : null;

    /// <summary>
    /// <c>HostExpr.MaybeType(form, stringOk)</c>：Type 原样；无命名空间的符号：含 <c>.</c> / 以 <c>]</c> 结尾 / 泛型等触发字符 → 类型名决议，
    /// 否则当前 <c>*ns*</c> 的映射或 <c>RT.classForName</c>；<c>stringOk</c> 时字符串按类型名决议。找不到返回 nil。
    /// </summary>
    public static object MaybeType(object form, object stringOk)
    {
        if (form is Type t0) return t0;
        if (form is string str) return RT.booleanCast(stringOk) ? TypeResolver.Resolve(str, Array.Empty<string>()) : null;
        if (form is not Symbol sym || sym.Namespace != null) return null;
        string n = sym.Name;
        if (n.IndexOf('.') > 0 || n[^1] == ']' || n.IndexOfAny(s_triggerTypeChars) >= 0)
            return TypeResolver.Resolve(n, Array.Empty<string>());
        if (RT.CurrentNSVar.deref() is Namespace ns && ns.GetMapping(sym) is Type t) return t;
        try { return RT.classForName(n); }
        catch (Exception) { return null; }
    }
}
