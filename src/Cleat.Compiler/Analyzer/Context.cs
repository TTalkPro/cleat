using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using clojure.lang;
using Symbol = clojure.lang.Symbol;

namespace Cleat.Compiler.Analyzer;

// 分析器的上下文与局部（analyzer.clj 开头的"上下文"一节，原版 syntax.sls 的 ctx / local）。

/// <summary>局部的种类（analyzer.clj Local 记录的 :kind）。Async / NoAwait 是标记，不是真正的局部。</summary>
internal enum LocalKind
{
    Local,
    Arg,
    Field,
    This,
    Catch,
    Escape,
    Async,
    NoAwait,
}

/// <summary>analyzer.clj 的 Local 记录：Sym 是 Clojure 符号，Name 是 L0 里的变量名，Tag 是类型提示符号。</summary>
internal sealed class Local
{
    public readonly Symbol Sym;
    public readonly Symbol Name;
    public readonly Symbol Tag;
    public readonly bool Mutable;
    public readonly LocalKind Kind;

    public Local(Symbol sym, Symbol name, Symbol tag, bool mutable, LocalKind kind)
    {
        Sym = sym; Name = name; Tag = tag; Mutable = mutable; Kind = kind;
    }

    /// <summary>analyzer.clj marker?</summary>
    public bool IsMarker => Kind is LocalKind.Async or LocalKind.NoAwait;

    public Local WithName(Symbol name) => new Local(Sym, name, Tag, Mutable, Kind);
}

/// <summary>局部表（新的在前），不可变的单链表；null 是空表（analyzer.clj ctx 的 :locals 序列）。</summary>
internal sealed class LocalList
{
    public readonly Local Head;
    public readonly LocalList Tail;

    public LocalList(Local head, LocalList tail) { Head = head; Tail = tail; }

    public static LocalList Cons(Local l, LocalList tail) => new LocalList(l, tail);

    public static IEnumerable<Local> Enumerate(LocalList ls)
    {
        for (var p = ls; p != null; p = p.Tail) yield return p.Head;
    }

    /// <summary>analyzer.clj without-markers。</summary>
    public static LocalList WithoutMarkers(LocalList ls)
    {
        if (ls == null) return null;
        var rest = WithoutMarkers(ls.Tail);
        if (ls.Head.IsMarker) return rest;
        return ReferenceEquals(rest, ls.Tail) ? ls : new LocalList(ls.Head, rest);
    }

    /// <summary>(concat (reverse locals) ls)：按顺序把 locals 加在前面（最后一个在最前）。</summary>
    public static LocalList PushAll(LocalList ls, IEnumerable<Local> locals)
    {
        foreach (var l in locals) ls = new LocalList(l, ls);
        return ls;
    }
}

/// <summary>源位置 [行 列 文件或nil]（analyzer.clj source-of 的结果；行、列来自 parse-long，可能为 nil）。</summary>
internal sealed record Pos(long? Line, long? Column, string File);

/// <summary>recur 目标：[循环标签 参数个数]（ctx 的 :loop）。</summary>
internal sealed record LoopTarget(Symbol Label, int Count);

/// <summary>
/// analyzer.clj 的 ctx：Tail 尾位置；Loop 为 null 或 recur 目标；Locals 局部（新的在前）；FnName 外层 fn 的类名；Hint def / letfn 给 fn* 的名字；
/// Pos 当前源位置；InLoop 处在 loop* 体里（cleat-120 的 :in-loop）。
/// </summary>
internal sealed record Ctx(bool Tail, LoopTarget Loop, LocalList Locals, string FnName, string Hint, Pos Pos, bool InLoop)
{
    /// <summary>analyzer.clj root-ctx。</summary>
    public static readonly Ctx Root = new Ctx(false, null, null, null, null, null, false);

    /// <summary>ctx-nontail</summary>
    public Ctx NonTail() => this with { Tail = false, Loop = null, Hint = null };

    /// <summary>ctx-clear</summary>
    public Ctx Clear() => Hint != null ? this with { Hint = null } : this;

    /// <summary>ctx-no-recur</summary>
    public Ctx NoRecur(bool tail) => this with { Tail = tail, Loop = null, Hint = null };

    /// <summary>ctx-with-locals</summary>
    public Ctx WithLocals(IEnumerable<Local> locals) => this with { Locals = LocalList.PushAll(Locals, locals), Hint = null };

    /// <summary>ctx-hinted</summary>
    public Ctx Hinted(string nm) => this with { Tail = false, Loop = null, Hint = nm };

    /// <summary>ctx-no-await</summary>
    public Ctx NoAwait() => this with { Locals = LocalList.Cons(new Local(Symbol.intern(null, "\u0000no-await"), null, null, false, LocalKind.NoAwait), Locals) };

    public Ctx At(Pos pos) => this with { Pos = pos };

    /// <summary>find-local：按符号的完整写法找最近的非标记局部。</summary>
    public Local FindLocal(object csym)
    {
        string n = csym.ToString();
        for (var p = Locals; p != null; p = p.Tail)
            if (!p.Head.IsMarker && n == p.Head.Sym.ToString()) return p.Head;
        return null;
    }
}

/// <summary>
/// 分析器共用的小工具（Clojure 核心函数的等价物）。注意 Clojure 代码里的整数（count 的结果、字面量）装箱后都是 Int64：
/// 放进 L0 数据的数都要写成 long（下游按 <c>is long</c> 认，例如 CljFnName 的 :variadic）。
/// </summary>
internal static class Clj
{
    public static readonly Keyword LineKw = Keyword.intern(null, "line"), ColumnKw = Keyword.intern(null, "column"), FileKw = Keyword.intern(null, "file"),
        SourceSpanKw = Keyword.intern(null, "source-span"), EndLineKw = Keyword.intern(null, "end-line"), EndColumnKw = Keyword.intern(null, "end-column"),
        TagKw = Keyword.intern(null, "tag"), ConstKw = Keyword.intern(null, "const"), InlineKw = Keyword.intern(null, "inline"),
        InlineAritiesKw = Keyword.intern(null, "inline-arities"), ArglistsKw = Keyword.intern(null, "arglists"), DynamicKw = Keyword.intern(null, "dynamic"),
        EscapeKw = Keyword.intern(null, "escape"), EscapeParamsKw = Keyword.intern(null, "escape-params"), DocKw = Keyword.intern(null, "doc"),
        AsyncKw = Keyword.intern(null, "async"), OnceKw = Keyword.intern(null, "once"), RettagKw = Keyword.intern(null, "rettag"),
        VolatileMutableKw = Keyword.intern(null, "volatile-mutable"), UnsyncMutableKw = Keyword.intern(null, "unsynchronized-mutable"),
        ImplementsKw = Keyword.intern(null, "implements"), CleatInterfaceKw = Keyword.intern("cleat", "interface"), InterfaceKw = Keyword.intern(null, "interface"),
        ProxyKw = Keyword.intern(null, "proxy"), GenClassKw = Keyword.intern(null, "genclass"), PackageKw = Keyword.intern(null, "package"),
        CleatL0Kw = Keyword.intern("cleat", "l0");

    public static bool Truthy(object x) => x != null && !(x is bool b && !b);

    /// <summary>
    /// 值边界（ClojureStaticCalls.RetObject：Int32 等窄整数 → Int64、Single → Double、decimal → BigDecimal）。Clojure 分析器从形式里
    /// 取元素（first / second / nth / 解构 / get）经过编译后的宿主调用，取出的标量都过这道转换；对向量的 reduce（mapv）不经过。
    /// reader 元数据里的行列是 Int32，语法引用会把它们带进形式（^{…} `(…)），所以要在同样的位置做同样的转换，L0 的装箱类型才一致。
    /// </summary>
    public static object Boundary(object v) => Cleat.Core.Runtime.ClojureStaticCalls.RetObject(v);

    /// <summary>clojure.core/str（单个值：nil 为空串，其余 RT.CultureToString）。</summary>
    public static string Str(object x) => x == null ? "" : RT.CultureToString(x);

    /// <summary>clojure.core/str（多个值相接）。</summary>
    public static string Str(params object[] xs)
    {
        var sb = new StringBuilder();
        foreach (var x in xs) sb.Append(Str(x));
        return sb.ToString();
    }

    /// <summary>clj-list?：列表或序列。</summary>
    public static bool IsList(object x) => x is IPersistentList or ISeq;

    /// <summary>sym-is?：未限定、名字为 n 的符号。</summary>
    public static bool SymIs(object x, string n) => x is Symbol s && s.Namespace == null && s.Name == n;

    /// <summary>序列的元素（nil 为空表）。</summary>
    public static List<object> Items(object x)
    {
        var r = new List<object>();
        for (var s = RT.seq(x); s != null; s = s.next()) r.Add(s.first());
        return r;
    }

    public static object Meta(object x) => x is IMeta m ? m.meta() : null;

    public static object Get(object m, object k) => RT.get(m, k);

    public static IPersistentList ListOf(IEnumerable<object> xs)
    {
        var l = new List<object>(xs);
        return PersistentList.create(l);
    }

    public static IPersistentVector Vec(IEnumerable<object> xs) => PersistentVector.create(new List<object>(xs).ToArray());

    /// <summary>向量字面量 [x …]（参数逐个作元素）。</summary>
    public static IPersistentVector V(params object[] xs) => PersistentVector.create(xs);

    public static object WithMeta(object x, object m) => ((IObj)x).withMeta((IPersistentMap)m);
}

/// <summary>分析器的全局状态：局部名序号与三个可变开关（analyzer.clj 的 counter、inline-calls?、reflection-warnings?、restrict-loop-inlines?）。</summary>
public static partial class FormAnalyzer
{
    // 序号在进程内唯一（不按形式重置）：一个编译单元里有多个形式，codegen 的类型表等按变量名记
    private static long s_counter;

    private static long NextCounter() => Interlocked.Increment(ref s_counter);

    /// <summary>
    /// analyzer.clj inline-calls?：调用点内联 / 直接链接 / KnownCall。构建镜像时关闭（种子与两级镜像的 .inl 一致）；CLEAT_NO_INLINE 时缺省关。
    /// </summary>
    public static bool InlineCalls { get; set; } = Environment.GetEnvironmentVariable("CLEAT_NO_INLINE") == null;

    /// <summary>analyzer.clj reflection-warnings?：镜像构建时关掉（driver/compile-boot）。</summary>
    public static bool ReflectionWarnings { get; set; } = true;

    /// <summary>analyzer.clj restrict-loop-inlines?：包构建时关掉（cleat-120 的循环体内联限制只用于 eval / load）。</summary>
    public static bool RestrictLoopInlines { get; set; } = true;

    /// <summary>
    /// fresh-name：L0 的局部变量名 g$名字$N$c（psyntax 的改名形状；名字是符号的完整写法）。codegen 的 clause-form 按这个形状还原源码里的名字。
    /// </summary>
    private static Symbol FreshName(string b) => Symbol.intern(null, "g$" + b + "$" + NextCounter() + "$c");
}
