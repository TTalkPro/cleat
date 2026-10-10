using System;
using BigInteger = System.Numerics.BigInteger;

namespace Cleat.Scheme;

/// <summary>get-clr-type 原语。</summary>
public partial class Builtins
{
    [Primitive("get-clr-type")]
    public static object GetClrType(object name) => GetClrType(name, Array.Empty<object>());

    /// <summary>(get-clr-type 'Name type-arg …)：按全名查找（不经命名空间列表）；带类型参数时构造泛型。</summary>
    [Primitive("get-clr-type")]
    public static object GetClrType(object name, params object[] typeargs)
    {
        var t = TypeResolver.GetTypeFast(RequiresNotNull<Symbol>(name).Name);
        if (t == null) return FALSE;
        if (typeargs.Length == 0) return t;
        return t.MakeGenericType(Array.ConvertAll(typeargs, x => RequiresNotNull<Type>(x)));
    }
}

/// <summary>从旧 Helpers.cs 移植的互操作辅助方法（Scheme 源码按名字调用 Helpers.GetTypeFast）；FFI 部分已删（designs/15 §1）。</summary>
public static partial class Helpers
{
    public static Type GetTypeFast(string name) => TypeResolver.GetTypeFast(name);

    public static T ConvertToDelegate<T>(object proc) where T : Delegate =>
      proc is T d ? d : (T)Interop.ToDelegate(typeof(T), Errors.RequiresType<Procedure>(proc, "convert-to-delegate"));

    public static object EnumToSymbol<T>(T value) => Interop.EnumToSymbol(value);

    public static T SymbolToEnum<T>(object symbol) => (T)Interop.SymbolToEnum(typeof(T), symbol);

    public static T[] RequiresArray<T>(object obj) => (T[])Interop.ConvertTo(obj, typeof(T[]), "requires-array");

    public static T UnsafeConvert<T>(object o) => o is T t ? t : default;
}
