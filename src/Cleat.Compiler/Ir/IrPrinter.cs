using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using clojure.lang;

namespace Cleat.Compiler.Ir;

/// <summary>
/// 节点 → S 表达式（Clojure 数据）。L0 ~ L3 的形状与 Clojure 编译器的 <c>cleat.nanopass/unparse</c> 逐字一致：
/// 同样的列表结构、缺省变长参数写 false、源位置作为列表的 metadata <c>{:line :column :file}</c>（column 至少为 1，file 只留文件名）。
/// 这是 .inl 内联文件与 G1 差分的格式（designs/30 §2 D3 / D6）。
/// </summary>
public static class IrPrinter
{
    internal static readonly Symbol QuoteSym = Symbol.intern("quote"), IfSym = Symbol.intern("if"), BeginSym = Symbol.intern("begin"),
        SetSym = Symbol.intern("set!"), LambdaSym = Symbol.intern("lambda"), ClauseSym = Symbol.intern("clause"),
        LetrecSym = Symbol.intern("letrec"), LetSym = Symbol.intern("let"), CallSym = Symbol.intern("call"), ThrowSym = Symbol.intern("throw"),
        TryFinallySym = Symbol.intern("try-finally"), TryCatchSym = Symbol.intern("try-catch"), LabelSym = Symbol.intern("label"),
        JumpSym = Symbol.intern("jump"), UninitializedSym = Symbol.intern("uninitialized"), CheckInitializedSym = Symbol.intern("check-initialized"),
        CljVarRefSym = Symbol.intern("clj-var-ref"), CljVarSetSym = Symbol.intern("clj-var-set!"), CljDefSym = Symbol.intern("clj-def"),
        CljInvokeSym = Symbol.intern("clj-invoke"), CljKeywordInvokeSym = Symbol.intern("clj-keyword-invoke"), CljCallSym = Symbol.intern("clj-call"),
        CljStaticCallSym = Symbol.intern("clj-static-call"), CljCaseSym = Symbol.intern("clj-case"), CljDeftypeSym = Symbol.intern("clj-deftype"),
        BoxSym = Symbol.intern("box"), UnboxSym = Symbol.intern("unbox"), SetBoxSym = Symbol.intern("set-box!");

    internal static readonly Keyword LineKey = Keyword.intern(null, "line"), ColumnKey = Keyword.intern(null, "column"), FileKey = Keyword.intern(null, "file");

    /// <summary>节点 → Clojure 数据。</summary>
    public static object ToData(Node n)
    {
        object sx = n switch
        {
            null => throw new ArgumentNullException(nameof(n)),
            Ref r => r.Name,
            Quote q => L(QuoteSym, q.Datum),
            If i => L(IfSym, ToData(i.Test), ToData(i.Then), ToData(i.Else)),
            Begin b => L(BeginSym, Many(b.Body)),
            Set s => L(SetSym, s.Name, ToData(s.Value)),
            Lambda l => L(LambdaSym, NameData(l.Name), l.Free.IsDefault ? Splice.None : (object)List(Syms(l.Free)), Many(l.Clauses)),
            Clause c => L(ClauseSym, List(Syms(c.Params)), (object)c.Rest ?? false, ToData(c.Body)),
            Letrec lr => L(LetrecSym, Bindings(lr.Vars, lr.Inits), ToData(lr.Body)),
            Let lt => L(LetSym, Bindings(lt.Vars, lt.Inits), ToData(lt.Body)),
            Call c => L(CallSym, ToData(c.Fn), Many(c.Args)),
            Throw t => L(ThrowSym, ToData(t.Value)),
            TryFinally tf => L(TryFinallySym, ToData(tf.Body), ToData(tf.Finally)),
            TryCatch tc => L(TryCatchSym, ToData(tc.Body), new Splice(Handlers(tc))),
            Label lb => L(LabelSym, lb.Name, List(Syms(lb.Params)), ToData(lb.Body0), ToData(lb.Body1)),
            Jump j => L(JumpSym, j.Target, Many(j.Args)),
            Uninitialized => L(UninitializedSym),
            CheckInitialized ci => L(CheckInitializedSym, ToData(ci.Value)),
            CljVarRef vr => L(CljVarRefSym, vr.Var, vr.Flag),
            CljVarSet vs => L(CljVarSetSym, vs.Var, ToData(vs.Value)),
            CljDef d => L(CljDefSym, d.Datum, d.Flag0, d.Flag1, ToData(d.Expr0), ToData(d.Expr1)),
            CljInvoke ci => L(CljInvokeSym, ToData(ci.Fn), Many(ci.Args)),
            CljKeywordInvoke k => L(CljKeywordInvokeSym, k.Keyword, ToData(k.Target), Many(k.Args)),
            CljCall cc => L(CljCallSym, cc.Op, cc.Datum, Many(cc.Args)),
            CljStaticCall sc => L(CljStaticCallSym, sc.Datum0, sc.Datum1, Many(sc.Args)),
            CljCase cs => L(CljCaseSym, cs.Datum, ToData(cs.Expr0), ToData(cs.Expr1), Many(cs.Rest)),
            CljDeftype dt => L(CljDeftypeSym, dt.Datum, List(Syms(dt.Fields)), Many(dt.Methods)),
            Box bx => L(BoxSym, ToData(bx.Value)),
            Unbox ub => L(UnboxSym, ub.Name),
            SetBox sb => L(SetBoxSym, sb.Name, ToData(sb.Value)),
            _ => throw new NotSupportedException($"IrPrinter: no S-expression form for {n.Kind}"),
        };
        if (n.Pos.IsKnown && sx is IObj o && sx is ISeq)
            sx = o.withMeta(PosMeta(n.Pos));
        return sx;
    }

    /// <summary>节点 → 文本（不带 metadata），用于 CLEAT_DUMP_IR 与调试。</summary>
    public static string ToText(Node n)
    {
        try { return RT.printString(ToData(n)); }
        catch (Exception e) when (e is not OutOfMemoryException) { return $"#<{n.Kind} (unprintable: {e.Message})>"; }
    }

    /// <summary>节点 → 文本写到 w。</summary>
    public static void Write(Node n, TextWriter w) => w.Write(ToText(n));

    internal static IPersistentMap PosMeta(SourcePos p) =>
        RT.map(LineKey, (long)p.Line, ColumnKey, (long)Math.Max(1, p.Column), FileKey, p.File is null ? null : Path.GetFileName(p.File));

    private static object NameData(object nm) => nm is FnName f ? f.ToData() : nm;

    // L(...) 的参数里，Splice 展开成多个元素（变长部分），其余原样
    private sealed class Splice
    {
        public static readonly Splice None = new Splice(Array.Empty<object>());
        public readonly IReadOnlyList<object> Items;
        public Splice(IReadOnlyList<object> items) { Items = items; }
    }

    private static Splice Many<T>(ImmutableArray<T> xs) where T : Node
    {
        var a = new object[xs.Length];
        for (int i = 0; i < a.Length; i++) a[i] = ToData(xs[i]);
        return new Splice(a);
    }

    private static object[] Syms(ImmutableArray<Symbol> xs)
    {
        var a = new object[xs.IsDefault ? 0 : xs.Length];
        for (int i = 0; i < a.Length; i++) a[i] = xs[i];
        return a;
    }

    private static object Bindings(ImmutableArray<Symbol> vars, ImmutableArray<Expr> inits)
    {
        var a = new object[vars.Length];
        for (int i = 0; i < a.Length; i++) a[i] = List(vars[i], ToData(inits[i]));
        return List(a);
    }

    private static object[] Handlers(TryCatch tc)
    {
        var a = new object[tc.Types.Length];
        for (int i = 0; i < a.Length; i++) a[i] = List(tc.Types[i], ToData(tc.Handlers[i]));
        return a;
    }

    private static IPersistentList L(params object[] parts)
    {
        var items = new List<object>(parts.Length + 4);
        foreach (var p in parts)
            if (p is Splice s) items.AddRange(s.Items);
            else items.Add(p);
        return PersistentList.create(items);
    }

    private static IPersistentList List(params object[] items) => PersistentList.create(items);
}
