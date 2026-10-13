using System;
using System.Linq;
using System.Text;

namespace Cleat.Scheme;

/// <summary>符号与全局值槽相关的原语。</summary>
public static class SymbolPrimitives
{
    private static Symbol Sym(object o, string who) => Errors.RequiresType<Symbol>(o, who);

    [Primitive("symbol-value")]
    public static object SymbolValue(object symbol)
    {
        var s = Sym(symbol, "symbol-value");
        object v = s.Value;
        return v is Unbound ? Errors.UndefinedError(s) : v;
    }

    [Primitive("set-symbol-value!")]
    public static object SetSymbolValue(object symbol, object value)
    {
        Sym(symbol, "set-symbol-value!").Value = value;
        return Unspecified.Instance;
    }

    [Primitive("symbol-bound?")]
    public static object IsSymbolBound(object symbol) => RT.Bool(Sym(symbol, "symbol-bound?").IsBound);

    [Primitive("remove-location")]
    public static object RemoveLocation(object symbol)
    {
        Sym(symbol, "remove-location").Value = Unbound.Instance;
        return Unspecified.Instance;
    }

    [Primitive("string->symbol", Flags = PrimitiveFlags.Pure, Sig = "(string) -> (symbol)")]
    public static object StringToSymbol(object str) => str switch
    {
        string s => Symbol.Intern(s),
        SchemeString ss => Symbol.Intern(ss.ToString()),   // 可变字符串或宽串（iron-055）
        StringBuilder sb => Symbol.Intern(sb.ToString()),
        _ => Errors.AssertionViolation("string->symbol", "not a string", str),
    };

    [Primitive("symbol->string", Flags = PrimitiveFlags.Pure, Sig = "(symbol) -> (string)")]
    public static object SymbolToString(object symbol) => SchemeString.Normalize(Sym(symbol, "symbol->string").Name);

    [Primitive("gensym")]
    public static object Gensym() => Symbol.Gensym();

    [Primitive("gensym")]
    public static object Gensym(object name) => name switch
    {
        string s => Symbol.Gensym(s),
        SchemeString ss => Symbol.Gensym(ss.ToString()),
        StringBuilder sb => Symbol.Gensym(sb.ToString()),
        Symbol sym => Symbol.Gensym(sym.Ungensym().Name),
        _ => Errors.AssertionViolation("gensym", "not a string or symbol", name),
    };

    /// <summary>
    /// <c>($gensym-scope)</c> 返回当前 gensym 作用域（无则 #f），供之后恢复；
    /// <c>($gensym-scope 库名或字符串)</c> 进入新作用域（库名 <c>(rnrs base)</c> 记为 <c>rnrs.base</c>）；
    /// <c>($gensym-scope 先前的返回值)</c> 恢复。只在构建模式下影响 gensym 的名字（iron-014）。
    /// </summary>
    [Primitive("$gensym-scope")]
    public static object GensymScope() => (object)Symbol.CurrentGensymScope ?? RT.False;

    [Primitive("$gensym-scope")]
    public static object GensymScope(object scope)
    {
        switch (scope)
        {
            case Symbol.GensymScope s: Symbol.RestoreGensymScope(s); break;
            case string s: Symbol.EnterGensymScope(s); break;
            case Cons c: Symbol.EnterGensymScope(string.Join(".", c.Select(x => x is Symbol y ? y.Name : Convert.ToString(x, System.Globalization.CultureInfo.InvariantCulture)))); break;
            default:
                if (RT.IsTrue(scope)) return Errors.AssertionViolation("$gensym-scope", "expected a library name, a string, a saved scope or #f", scope);
                Symbol.RestoreGensymScope(null);
                break;
        }
        return Unspecified.Instance;
    }

    /// <summary>($gensym-realm) → 当前领域；($gensym-realm 字符串) 设置领域（见 Symbol.GensymRealm）。</summary>
    [Primitive("$gensym-realm")]
    public static object GensymRealm() => Symbol.GensymRealm;

    [Primitive("$gensym-realm")]
    public static object GensymRealm(object realm)
    {
        Symbol.GensymRealm = Errors.RequiresType<string>(realm, "$gensym-realm");
        return Unspecified.Instance;
    }

    [Primitive("ungensym", Flags = PrimitiveFlags.Pure)]
    public static object Ungensym(object symbol) => Sym(symbol, "ungensym").Ungensym();
}
