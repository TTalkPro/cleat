using clojure.lang;

namespace Cleat.Core.Runtime;

/// <summary>
/// 平凡形式的直接求值（cleat-161，designs/29 §3.3）：<see cref="Startup.EvalForm"/> 用它在编译器还没加载时直接给出值，
/// 让 <c>-e nil</c>、REPL 的首个平凡输入不为一个常量付那一整格编译器成本（159 量到 206 ~ 212 ms）。
///
/// 只认"求值就是它自己"的形式：自求值常量（nil / true / false / 数字 / 字符串 / 字符 / 关键字）、<c>()</c>（→ nil）、
/// <c>(quote x)</c>（→ x），以及递归不含符号、不带元数据的 vector / map / set 字面量。符号要解析、调用与特殊形式要展开，
/// 一律交回编译器。这是驱动侧简单形式解释器（<c>src/clojure/cleat/compiler/driver.clj</c> 的 <c>simple?</c>）的真子集：
/// 判错只会多加载一次编译器，不会把该编译的形式自己算出来。认与不认的边界由 Core 单测固定（SimpleFormsTests）。
/// </summary>
public static class SimpleForms
{
    /// <summary>quote 的形式头：读取器给的是不分命名空间的 quote，语法引用会给出 clojure.core/quote，两个都认。</summary>
    static readonly Symbol s_quote = Symbol.intern("quote");

    static readonly Symbol s_coreQuote = Symbol.intern("clojure.core", "quote");

    /// <summary>试求平凡形式的值；false 表示这个形式要编译器（调用方随后按需加载它）。只读结构，不求值、不改动任何东西。</summary>
    public static bool Try(object form, out object value)
    {
        value = null;
        switch (form)
        {
            case null: return true;                                     // nil
            case bool or string or char or Keyword: value = form; return true;
            case byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal:
                value = form; return true;
            case BigInt or BigInteger or BigDecimal or Ratio: value = form; return true;
            case Symbol: return false;                                  // 符号的值来自 Var / 动态绑定
            case ISeq s:
                // 空表 () 是常量（与编译器一致：求值还是它自己，不是 nil）；(quote x) 求值为 x；
                // 其余是调用或特殊形式
                if (s is IPersistentList empty && empty.count() == 0) { value = form; return true; }
                return TryQuote(s, out value);
            case IMeta wrapped when wrapped.meta() is not null: return false;   // 带元数据的字面量交回编译器，口径不变
            case IPersistentCollection:
                // vector / map / set 的字面量：元素（map 是 MapEntry，递归进去看键与值）全是常量才是常量
                if (!Constants(form)) return false;
                value = form;
                return true;
            default: return false;                                      // 宿主对象之类：不自己判定
        }
    }

    /// <summary>(quote x) → x。操作数原样交出（与编译器一致：连它自己的元数据一起）。</summary>
    static bool TryQuote(ISeq s, out object value)
    {
        value = null;
        if (s.first() is not Symbol head || (head != s_quote && head != s_coreQuote)) return false;
        ISeq rest = s.next();
        if (rest is null || rest.next() is { }) return false;         // 参数不是一个
        value = rest.first();
        return true;
    }

    /// <summary>集合里的每个元素（就地放着的值）都是常量？嵌套的表不算（它的求值不是它自己）。</summary>
    static bool Constants(object coll)
    {
        switch (coll)
        {
            case null: return true;
            case bool or string or char or Keyword: return true;
            case byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal: return true;
            case BigInt or BigInteger or BigDecimal or Ratio: return true;
            case Symbol: return false;
            case ISeq: return false;
            case IMeta wrapped when wrapped.meta() is not null: return false;
            case IPersistentVector v: return Elements(v.seq());
            case IPersistentMap m: return Elements(m.seq());     // 逐项是 MapEntry（一个向量），递归进去看键与值
            case IPersistentSet st: return Elements(st.seq());
            default: return false;
        }
    }

    static bool Elements(ISeq s)
    {
        for (; s != null; s = s.next())
            if (!Constants(s.first())) return false;
        return true;
    }
}
