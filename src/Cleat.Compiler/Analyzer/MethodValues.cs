using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cleat.Core.Compiler;
using clojure.lang;
using static Cleat.Compiler.Analyzer.Clj;
using Symbol = clojure.lang.Symbol;

namespace Cleat.Compiler.Analyzer;

/// <summary>
/// Clojure 1.12 的限定方法（cleat-192）：<c>T/m</c>（静态方法）、<c>T/.m</c>（实例方法，第一个实参是目标）、<c>T/new</c>（构造器）
/// 作为值时是 fn；<c>^[t …]</c>（:param-tags）选定重载。调用位置的 param-tags 按同样的规则转换实参。
/// <para>做法是改写成普通形式再分析：值 → <c>(fn* ([p…] (. T (m p…))) …)</c>；带 param-tags 时只有那一个元数，原始类型的实参经
/// <c>RT.xCast</c> 转换（同 JVM 按形参类型强制转换），引用类型的实参带 :tag 提示，于是分析期按提示静态决议到那个重载；
/// <c>_</c> 位置不转换。不带 param-tags 时按重载的元数各有一个子句，运行期按实参反射决议。</para>
/// </summary>
public static partial class FormAnalyzer
{
    private static readonly Keyword ParamTagsKw = Keyword.intern(null, "param-tags");
    private static readonly Symbol WildcardSym = Symbol.intern(null, "_"), RtSym = Symbol.intern(null, "clojure.lang.RT"),
        DoSym = Symbol.intern(null, "do");

    private static readonly Dictionary<string, string> s_primCasts = new()
    {
        ["int"] = "intCast", ["long"] = "longCast", ["double"] = "doubleCast", ["float"] = "floatCast", ["short"] = "shortCast",
        ["byte"] = "byteCast", ["sbyte"] = "sbyteCast", ["ushort"] = "ushortCast", ["uint"] = "uintCast", ["ulong"] = "ulongCast",
        ["char"] = "charCast", ["bool"] = "booleanCast", ["boolean"] = "booleanCast",
    };

    private enum QmKind { Static, Instance, Ctor }

    private static (QmKind Kind, string Name) QmOf(string member) =>
        member == "new" ? (QmKind.Ctor, ".ctor")
        : member.Length > 1 && member[0] == '.' ? (QmKind.Instance, MungeStr(member.Substring(1)))
        : (QmKind.Static, MungeStr(member));

    /// <summary>各重载的形参个数（实例方法不含目标）。</summary>
    private static List<int> QmArities(Type t, QmKind kind, string name) => kind switch
    {
        QmKind.Ctor => t.GetConstructors().Select(c => c.GetParameters().Length).Concat(t.IsValueType ? new[] { 0 } : Array.Empty<int>()).ToList(),
        QmKind.Instance => TypeAndInterfaces(t).SelectMany(x => x.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            .Where(m => m.Name == name).Select(m => m.GetParameters().Length).ToList(),
        _ => t.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name == name).Select(m => m.GetParameters().Length).ToList(),
    };

    private static bool HasStaticFieldOrProperty(Type t, string name) =>
        t.GetField(name, BindingFlags.Public | BindingFlags.Static) != null
        || t.GetProperty(name, BindingFlags.Public | BindingFlags.Static) is { } p && p.GetIndexParameters().Length == 0;

    private static Exception QmNotFound(object where, Ctx ctx, Type t, QmKind kind, string member) => Err(where, ctx, kind switch
    {
        QmKind.Ctor => "No constructor found for type " + t.FullName,
        QmKind.Instance => "No instance method " + member.Substring(1) + " found for type " + t.FullName,
        _ => "No static method or static field / property " + member + " found for type " + t.FullName,
    });

    /// <summary>param-tags 的一项作用到实参形式 a 上：_ 原样；原始类型 → (. RT (xCast a))；其余 → 带 :tag 的形式（字面量包进 (do a)）。</summary>
    private static object QmCoerce(object tag, object a)
    {
        if (tag == null || tag.Equals(WildcardSym)) return a;
        if (tag is Symbol ts && ts.Namespace == null && s_primCasts.TryGetValue(ts.Name, out var cast))
            return RT.list(DotSym, RtSym, RT.list(Symbol.intern(null, cast), a));
        object tagv = tag is Type ty ? Symbol.intern(null, ty.FullName) : tag;
        object x = a is IObj ? a : RT.list(DoSym, a);
        return WithMeta(x, RT.assoc(Meta(x), TagKw, tagv));
    }

    private static List<object> ParamTagsOf(Symbol sym)
    {
        var pt = Get(Meta(sym), ParamTagsKw);
        return pt == null ? null : Items(pt);
    }

    /// <summary>调用 T/m 的形式（实参已就位）。</summary>
    private static object QmCall(Type t, QmKind kind, string name, object target, IEnumerable<object> args) => kind switch
    {
        QmKind.Ctor => ListStar(new object[] { NewSym, t }, args),
        QmKind.Instance => RT.list(DotSym, WithMeta(target, RT.map(TagKw, Symbol.intern(null, t.FullName))), ListStar(new object[] { Symbol.intern(null, name) }, args)),
        _ => RT.list(DotSym, t, ListStar(new object[] { Symbol.intern(null, name) }, args)),
    };

    /// <summary>
    /// 值位置的 T/m：静态字段 / 属性返回 null（照旧读字段）；方法 / 构造器返回等价的 fn* 形式；没有这个成员报错（措辞含 static method /
    /// instance method / constructor，同 JVM）。
    /// </summary>
    private static object MethodValueForm(Ctx ctx, Symbol sym, Type t, string member)
    {
        var (kind, name) = QmOf(member);
        if (kind == QmKind.Static && HasStaticFieldOrProperty(t, name)) return null;
        var arities = QmArities(t, kind, name);
        if (arities.Count == 0) throw QmNotFound(sym, ctx, t, kind, member);
        var tags = ParamTagsOf(sym);
        IEnumerable<int> clauses = tags != null ? new[] { tags.Count } : arities.Distinct().OrderBy(n => n);
        if (tags != null && !arities.Contains(tags.Count))
            throw Err(sym, ctx, "param-tags " + RT.printString(Get(Meta(sym), ParamTagsKw)) + " do not match any arity of " + sym);
        var fn = new List<object> { FnStarSym };
        foreach (int n in clauses)
        {
            var ps = Enumerable.Range(0, n).Select(i => (object)Symbol.intern(null, "qm__p" + i)).ToList();
            var args = ps.Select((p, i) => tags != null ? QmCoerce(tags[i], p) : p).ToList();
            object target = Symbol.intern(null, "qm__target");
            var vec = kind == QmKind.Instance ? new object[] { target }.Concat(ps) : ps;
            fn.Add(RT.list(LazilyPersistentVector.create(vec.ToList()), QmCall(t, kind, name, target, args)));
        }
        return TransferSource(PersistentList.create(fn), sym);
    }

    /// <summary>调用位置 (^[…] T/m a …)：按 param-tags 转换实参（实例方法跳过目标），再交给原来的改写；没有 param-tags 时原样。</summary>
    private static List<object> QmCallArgs(Symbol sop, List<object> args)
    {
        var tags = ParamTagsOf(sop);
        if (tags == null) return args;
        bool inst = sop.Name.Length > 1 && sop.Name[0] == '.';
        int off = inst ? 1 : 0;
        return args.Select((a, i) => i >= off && i - off < tags.Count ? QmCoerce(tags[i - off], a) : a).ToList();
    }
}
