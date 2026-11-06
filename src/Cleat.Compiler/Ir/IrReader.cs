using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using clojure.lang;

namespace Cleat.Compiler.Ir;

/// <summary>S 表达式不符合某一层的文法。</summary>
public sealed class IrFormatException : Exception
{
    public IrFormatException(string message) : base(message) { }
}

/// <summary>
/// S 表达式（Clojure 数据）→ 节点：<see cref="IrPrinter.ToData"/> 的逆，与 Clojure 编译器的 <c>cleat.nanopass/parse</c> 接受同样的输入
/// （子表可以是列表或向量；列表 metadata 里 line &gt; 0 时还原源位置）。用于读 .inl 内联子句与测试。
/// </summary>
public static class IrReader
{
    /// <summary>读一个表达式（lang 只影响 lambda 的形状：L3 带自由变量表，以及 L2 起才有的 box 节点）。</summary>
    public static Expr ReadExpr(object form, Langs lang = Langs.L0) => new State(lang).Expr(form);

    /// <summary>读一个子句 <c>(clause (x* ...) (maybe x) body)</c>（.inl 里存的就是子句）。</summary>
    public static Clause ReadClause(object form, Langs lang = Langs.L0) => new State(lang).Clause(form);

    /// <summary>从文本读一个表达式（EDN 语法，测试用）。</summary>
    public static Expr ReadText(string text, Langs lang = Langs.L0) =>
        ReadExpr(EdnReader.readString(text, PersistentHashMap.EMPTY), lang);

    private sealed class State
    {
        private readonly Langs _lang;

        public State(Langs lang) { _lang = lang; }

        private bool AtLeast(Langs l) => (int)_lang >= (int)l;

        public Expr Expr(object x)
        {
            if (x is Symbol s) return new Ref(s);
            var items = Items(x) ?? throw Bad("Expr", x);
            if (items.Count == 0 || items[0] is not Symbol head || head.Namespace != null) throw Bad("Expr", x);
            Expr e = head.Name switch
            {
                "quote" => Arity(items, 2, x) ? new Quote(items[1]) : null,
                "if" => Arity(items, 4, x) ? new If(Expr(items[1]), Expr(items[2]), Expr(items[3])) : null,
                "begin" => items.Count >= 2 ? new Begin(Exprs(items, 1)) : throw Bad("begin", x),
                "set!" => Arity(items, 3, x) ? new Set(Sym(items[1], x), Expr(items[2])) : null,
                "lambda" => Lambda(items, x),
                "letrec" => Arity(items, 3, x) ? Letrec(items, x) : null,
                "let" => Arity(items, 3, x) ? Let(items, x) : null,
                "call" => items.Count >= 2 ? new Call(Expr(items[1]), Exprs(items, 2)) : throw Bad("call", x),
                "throw" => Arity(items, 2, x) ? new Throw(Expr(items[1])) : null,
                "try-finally" => Arity(items, 3, x) ? new TryFinally(Expr(items[1]), Expr(items[2])) : null,
                "try-catch" => items.Count >= 2 ? TryCatch(items, x) : throw Bad("try-catch", x),
                "label" => Arity(items, 5, x) ? new Label(Sym(items[1], x), Syms(items[2], x), Expr(items[3]), Expr(items[4])) : null,
                "jump" => items.Count >= 2 ? new Jump(Sym(items[1], x), Exprs(items, 2)) : throw Bad("jump", x),
                "uninitialized" => Arity(items, 1, x) ? new Uninitialized() : null,
                "check-initialized" => Arity(items, 2, x) ? new CheckInitialized(Expr(items[1])) : null,
                "clj-var-ref" => Arity(items, 3, x) ? new CljVarRef(items[1], Flag(items[2], x)) : null,
                "clj-var-set!" => Arity(items, 3, x) ? new CljVarSet(items[1], Expr(items[2])) : null,
                "clj-def" => Arity(items, 6, x) ? new CljDef(items[1], Flag(items[2], x), Flag(items[3], x), Expr(items[4]), Expr(items[5])) : null,
                "clj-invoke" => items.Count >= 2 ? new CljInvoke(Expr(items[1]), Exprs(items, 2)) : throw Bad("clj-invoke", x),
                "clj-keyword-invoke" => items.Count >= 3 ? new CljKeywordInvoke(items[1], Expr(items[2]), Exprs(items, 3)) : throw Bad("clj-keyword-invoke", x),
                "clj-call" => items.Count >= 3 ? new CljCall(items[1], items[2], Exprs(items, 3)) : throw Bad("clj-call", x),
                "clj-static-call" => items.Count >= 3 ? new CljStaticCall(items[1], items[2], Exprs(items, 3)) : throw Bad("clj-static-call", x),
                "clj-case" => items.Count >= 4 ? new CljCase(items[1], Expr(items[2]), Expr(items[3]), Exprs(items, 4)) : throw Bad("clj-case", x),
                "clj-deftype" => items.Count >= 3 ? new CljDeftype(items[1], Syms(items[2], x), Lambdas(items, 3, x)) : throw Bad("clj-deftype", x),
                "box" when AtLeast(Langs.L2) => Arity(items, 2, x) ? new Box(Expr(items[1])) : null,
                "unbox" when AtLeast(Langs.L2) => Arity(items, 2, x) ? new Unbox(Sym(items[1], x)) : null,
                "set-box!" when AtLeast(Langs.L2) => Arity(items, 3, x) ? new SetBox(Sym(items[1], x), Expr(items[2])) : null,
                _ => throw Bad("Expr", x),
            };
            return WithPos(e, x);
        }

        public Clause Clause(object x)
        {
            var items = Items(x);
            if (items is null || items.Count != 4 || !Equals(items[0], IrPrinter.ClauseSym)) throw Bad("Clause", x);
            var rest = items[2] is null || items[2] is false ? null : Sym(items[2], x);
            return WithPos(new Clause(Syms(items[1], x), rest, Expr(items[3])), x);
        }

        private Lambda Lambda(List<object> items, object x)
        {
            if (items.Count < 2) throw Bad("lambda", x);
            object nm = items[1];
            if (nm is IPersistentMap m && m.containsKey(FnName.ClassKey)) nm = FnName.FromData(m);
            else if (!(nm is null || nm is false || nm is Symbol || nm is string)) throw Bad("lambda name", x);
            int first = 2;
            var free = default(ImmutableArray<Symbol>);
            if (AtLeast(Langs.L3))
            {
                if (items.Count < 3) throw Bad("L3 lambda", x);
                free = Syms(items[2], x);
                first = 3;
            }
            var cls = ImmutableArray.CreateBuilder<Clause>(items.Count - first);
            for (int i = first; i < items.Count; i++) cls.Add(Clause(items[i]));
            return new Lambda(nm, free, cls.MoveToImmutable());
        }

        private ImmutableArray<Lambda> Lambdas(List<object> items, int from, object x)
        {
            var b = ImmutableArray.CreateBuilder<Lambda>(items.Count - from);
            for (int i = from; i < items.Count; i++)
                b.Add(Expr(items[i]) as Lambda ?? throw Bad("clj-deftype method (lambda)", x));
            return b.MoveToImmutable();
        }

        private TryCatch TryCatch(List<object> items, object x)
        {
            var types = ImmutableArray.CreateBuilder<object>(items.Count - 2);
            var handlers = ImmutableArray.CreateBuilder<Expr>(items.Count - 2);
            for (int i = 2; i < items.Count; i++)
            {
                var h = Items(items[i]);
                if (h is null || h.Count != 2) throw Bad("try-catch handler", x);
                types.Add(h[0]);
                handlers.Add(Expr(h[1]));
            }
            return new TryCatch(Expr(items[1]), types.MoveToImmutable(), handlers.MoveToImmutable());
        }

        private Letrec Letrec(List<object> items, object x)
        {
            Bindings(items[1], x, out var vars, out var inits);
            return new Letrec(vars, inits, Expr(items[2]));
        }

        private Let Let(List<object> items, object x)
        {
            Bindings(items[1], x, out var vars, out var inits);
            return new Let(vars, inits, Expr(items[2]));
        }

        private void Bindings(object bs, object x, out ImmutableArray<Symbol> vars, out ImmutableArray<Expr> inits)
        {
            var items = Items(bs) ?? throw Bad("bindings", x);
            var v = ImmutableArray.CreateBuilder<Symbol>(items.Count);
            var e = ImmutableArray.CreateBuilder<Expr>(items.Count);
            foreach (var b in items)
            {
                var pair = Items(b);
                if (pair is null || pair.Count != 2) throw Bad("binding", x);
                v.Add(Sym(pair[0], x));
                e.Add(Expr(pair[1]));
            }
            vars = v.MoveToImmutable();
            inits = e.MoveToImmutable();
        }

        private ImmutableArray<Expr> Exprs(List<object> items, int from)
        {
            var b = ImmutableArray.CreateBuilder<Expr>(Math.Max(0, items.Count - from));
            for (int i = from; i < items.Count; i++) b.Add(Expr(items[i]));
            return b.MoveToImmutable();
        }

        private ImmutableArray<Symbol> Syms(object x, object form)
        {
            var items = Items(x) ?? throw Bad("symbol list", form);
            var b = ImmutableArray.CreateBuilder<Symbol>(items.Count);
            foreach (var i in items) b.Add(Sym(i, form));
            return b.MoveToImmutable();
        }

        private Symbol Sym(object x, object form) => x as Symbol ?? throw Bad("symbol", form);

        private bool Flag(object x, object form) => x is bool b ? b : throw Bad("flag (boolean)", form);

        private bool Arity(List<object> items, int n, object form) => items.Count == n ? true : throw Bad(items[0] + $" with {n - 1} operand(s)", form);

        private static T WithPos<T>(T n, object x) where T : Node
        {
            if (x is IMeta m && m.meta() is IPersistentMap meta && meta.valAt(IrPrinter.LineKey) is { } line)
            {
                int l = Convert.ToInt32(line);
                if (l > 0)
                {
                    int c = meta.valAt(IrPrinter.ColumnKey) is { } col ? Convert.ToInt32(col) : 0;
                    n.Pos = new SourcePos(l, c, meta.valAt(IrPrinter.FileKey) as string);
                }
            }
            return n;
        }

        private static List<object> Items(object x)
        {
            if (x is not (ISeq or IPersistentList or IPersistentVector)) return null;
            var r = new List<object>();
            for (var s = RT.seq(x); s != null; s = s.next()) r.Add(s.first());
            return r;
        }

        private Exception Bad(string what, object x)
        {
            string text;
            try { text = RT.printString(x); }
            catch (Exception e) when (e is not OutOfMemoryException) { text = x?.ToString() ?? "nil"; }
            if (text.Length > 300) text = text.Substring(0, 300) + "…";
            return new IrFormatException($"invalid {_lang} {what}: {text}");
        }
    }
}
