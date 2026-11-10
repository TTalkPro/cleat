using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Cleat.Compiler.Ir;
using Cleat.Core.Runtime;
using clojure.lang;
using static Cleat.Compiler.Analyzer.Clj;
using Symbol = clojure.lang.Symbol;

namespace Cleat.Compiler.Analyzer;

// 特殊形式（analyzer.clj 的"特殊形式"一节，原版 syntax.sls 逐个对照）：def if quote var throw monitor-* import* let* loop* recur
// fn* escape* await* letfn* try case*。宿主互操作（. new set!）在 HostInterop.cs，类型定义（deftype* reify* proxy-class* gen-class*）在 Types.cs。
public static partial class FormAnalyzer
{
    private static string SourceFile(Pos pos) => pos?.File ?? SourcePath();

    /// <summary>def-var：def 的目标 Var（限定名只能指当前 ns；别处 refer 来的同名 Var 被遮蔽）。</summary>
    private static Var DefVar(object form, Ctx ctx, Namespace ns, Symbol sym)
    {
        string nsname = sym.Namespace;
        var nm = Symbol.intern(sym.Name);
        if (nsname != null)
        {
            var target = NamespaceFor(ns, nsname);
            if (target == null) throw Err(form, ctx, "Can't refer to qualified var that doesn't exist");
            if (ReferenceEquals(target, ns)) return ns.intern(nm);
            if (target.FindInternedVar(nm) != null) throw Err(form, ctx, "Can't create defs outside of current ns");
            throw Err(form, ctx, "Can't refer to qualified var that doesn't exist");
        }
        var o = ns.GetMapping(nm);
        if (o == null) return ns.intern(nm);
        if (o is Var v && ReferenceEquals(v.Namespace, ns)) return v;
        if (o is Var) return ns.intern(nm);
        throw Err(form, ctx, "Expecting var, but " + sym + " is mapped to " + Str(o));
    }

    /// <summary>sp-def：分析期先 intern 并写 :arglists / 原始返回提示 / :escape-params，再分析初值（体里的自调用据此决定调用方式）。</summary>
    private static Expr SpDef(object form, Ctx ctx)
    {
        var items = Items(RT.next(form));
        Expr Build(object nsym, object doc, bool init)
        {
            if (nsym is not Symbol sym) throw Err(form, ctx, "First argument to def must be a Symbol");
            var ns = CurrentNs;
            var v = DefVar(form, ctx, ns, sym);
            var sm = (IPersistentMap)Meta(sym);
            bool dynamic = Truthy(Get(sm, DynamicKw));
            var pos = Position(form, ctx);
            string n = sym.Name;
            object initForm = init ? items[doc != null ? 2 : 1] : null;
            var eps = EscapeParamPositions(initForm);
            if (dynamic) v.setDynamic();
            if (!dynamic && n.Length > 2 && n.StartsWith('*') && n.EndsWith('*'))
                RT.errPrintWriter().WriteLine(
                    "Warning: " + sym + " not declared dynamic and thus is not dynamically rebindable, "
                    + "but its name suggests otherwise. Please either indicate ^:dynamic " + sym
                    + " or change the name. (" + SourceFile(pos) + ":" + (pos != null ? Str(pos.Line) : "0") + ")");
            // 分析期先把 :arglists（与名字上的原始返回类型提示 :tag，cleat-096）放上 Var
            if (Truthy(Get(sm, ArglistsKw)) && Get(sm, ArglistsKw) is var al)
            {
                var vmeta = (IPersistentMap)RT.assoc(v.meta(), ArglistsKw, RT.second(al));
                if (PrimKind(Get(sm, TagKw)) != null) vmeta = vmeta.assoc(TagKw, Get(sm, TagKw));
                v.setMeta(vmeta);
            }
            // ^:escape 形参必须先放 Var 元数据：函数体里的递归调用在 clj-def 求值之前分析
            if (eps != null && eps.count() > 0) v.setMeta((IPersistentMap)RT.assoc(v.meta(), EscapeParamsKw, eps));
            object m = RT.assoc(RT.assoc(RT.assoc(sm, LineKw, pos != null ? pos.Line ?? 0L : 0L), ColumnKw, pos != null ? pos.Column ?? 0L : 0L),
                                FileKw, SourceFile(pos));
            if (eps != null && eps.count() > 0) m = RT.assoc(m, EscapeParamsKw, eps);
            if (doc != null) m = RT.assoc(m, DocKw, doc);
            var initNode = init ? Analyze(initForm, ctx.Hinted(sym.Name)) : Q(null);
            var metaNode = Analyze(m, ctx.NonTail().At(null));
            return new CljDef(v, init, dynamic, initNode, metaNode);
        }
        return items.Count switch
        {
            1 => Build(items[0], null, false),
            2 => Build(items[0], null, true),
            3 => items[1] is string doc ? Build(items[0], doc, true) : throw Err(form, ctx, "Too many arguments to def"),
            0 => throw Err(form, ctx, "Too few arguments to def"),
            _ => throw Err(form, ctx, "Too many arguments to def"),
        };
    }

    /// <summary>sp-if</summary>
    private static Expr SpIf(object form, Ctx ctx)
    {
        var items = Items(RT.next(form));
        var c = ctx.Clear();
        var nt = c.NonTail();
        switch (items.Count)
        {
            case 2: { var t = Analyze(items[0], nt); var a = Analyze(items[1], c); return new If(t, a, Q(null)); }
            case 3: { var t = Analyze(items[0], nt); var a = Analyze(items[1], c); var b = Analyze(items[2], c); return new If(t, a, b); }
            case 0 or 1: throw Err(form, ctx, "Too few arguments to if");
            default: throw Err(form, ctx, "Too many arguments to if");
        }
    }

    /// <summary>sp-quote</summary>
    private static Expr SpQuote(object form, Ctx ctx)
    {
        int n = RT.count(form);
        if (n == 2) return Q(Boundary(RT.second(form)));
        // JVM 的 ConstantExpr.Parser：原因是带 {:form 形式} 的 ExceptionInfo（cleat-189，special 测试读 (ex-data (.InnerException e))）
        string msg = "Wrong number of args (" + (n - 1) + ") passed to quote";
        throw ExpanderEx(Position(form, ctx), Phase.CompileSyntaxCheck, msg,
            new ExceptionInfo(msg, RT.map(Keyword.intern(null, "form"), form)), "quote");
    }

    /// <summary>lookup-var：(var sym) 的 Var 或 null。</summary>
    private static Var LookupVar(object form, Ctx ctx, Symbol sym)
    {
        var ns = CurrentNs;
        if (sym.Namespace != null) return NamespaceFor(ns, sym.Namespace)?.FindInternedVar(Symbol.intern(sym.Name));
        if (SymIs(sym, "ns")) return RT.NSVar;
        if (SymIs(sym, "in-ns")) return RT.InNSVar;
        var o = ns.GetMapping(sym);
        if (o == null) return null;
        if (o is Var v) return v;
        throw Err(form, ctx, "Expecting var, but " + sym + " is mapped to " + Str(o));
    }

    /// <summary>sp-var</summary>
    private static Expr SpVar(object form, Ctx ctx)
    {
        var sym = RT.second(form);
        if (!(RT.count(form) == 2 && sym is Symbol)) throw Err(form, ctx, "var requires a symbol");
        var v = LookupVar(form, ctx, (Symbol)sym) ?? throw Err(form, ctx, "Unable to resolve var: " + sym + " in this context");
        return Q(v);
    }

    /// <summary>sp-throw</summary>
    private static Expr SpThrow(object form, Ctx ctx) => RT.count(form) switch
    {
        2 => new Throw(Analyze(RT.second(form), ctx.NonTail())),
        1 => throw Err(form, ctx, "Too few arguments to throw, throw expects a single Exception instance"),
        _ => throw Err(form, ctx, "Too many arguments to throw, throw expects a single Exception instance"),
    };

    /// <summary>sp-monitor</summary>
    private static Expr SpMonitor(string nm, string method, object form, Ctx ctx)
    {
        if (RT.count(form) == 2) return CljCallOf(method, s_false, Analyze(RT.second(form), ctx.NonTail()));
        throw Err(form, ctx, nm + " requires one argument");
    }

    /// <summary>sp-import*</summary>
    private static Expr SpImport(object form, Ctx ctx)
    {
        var nm = RT.second(form);
        if (!(RT.count(form) == 2 && nm is string)) throw Err(form, ctx, "import* requires a string");
        var t = ImportClassForName((string)nm) ?? throw Err(form, ctx, "Unable to resolve classname: " + nm);
        return CljCallOf("ImportOf", s_false, Q(t));
    }

    // —— let* / loop* ——

    /// <summary>binding-pairs：绑定向量 → (符号, 初值) 对，按顺序检查。</summary>
    private static List<(Symbol Sym, object Init)> BindingPairs(object form, Ctx ctx, object bindings, string pairsMsg)
    {
        if (bindings is not IPersistentVector bv) throw Err(form, ctx, "Bad binding form, expected vector");
        if (bv.count() % 2 != 0) throw Err(form, ctx, pairsMsg);
        var r = new List<(Symbol, object)>();
        for (int i = 0; i < bv.count(); i += 2)
        {
            var sym = bv.nth(i);
            if (sym is not Symbol s) throw Err(form, ctx, "Bad binding form, expected symbol, got: " + Str(sym));
            if (s.Namespace != null) throw Err(form, ctx, "Can't let qualified name: " + s);
            if (SymIs(s, "&")) throw Err(form, ctx, "Can't use & as a local binding");
            r.Add((s, bv.nth(i + 1)));
        }
        return r;
    }

    /// <summary>
    /// init-tag：没有提示的 let 局部取初值的类（(new T …)、(T. …)、带 ^T 提示的初值，以及 host-form-type 看得出的调用形式；只取引用类型）。
    /// </summary>
    private static Symbol InitTag(Ctx ctx, object d)
    {
        Type t = null;
        if (TagOf(d) is { } tg) t = TagType(tg);
        // 局部的初值是另一个局部：沿用它的类型（JVM 的 LocalBindingExpr 的 javaClass，cleat-189）
        else if (d is Symbol ds && ds.Namespace == null && ctx.FindLocal(ds)?.Tag is { } lt) t = TagType(lt);
        else if (IsList(d) && RT.seq(d) != null)
        {
            var h = RT.first(d);
            if (SymIs(h, "new")) t = RT.count(d) > 1 ? MaybeType(ctx, RT.second(d)) : null;
            else if (h is Symbol hs && hs.Namespace == null)
            {
                string n = hs.Name;
                int k = n.Length;
                if (k > 1 && n[k - 1] == '.' && n[0] != '.' && ctx.FindLocal(hs) == null)
                    t = MaybeType(ctx, Symbol.intern(n.Substring(0, k - 1)));
                else
                    t = HostFormType(ctx, d);
            }
            else t = HostFormType(ctx, d);
        }
        if (t != null && !t.IsValueType && !t.ContainsGenericParameters) return Symbol.intern(t.FullName);
        return null;
    }

    /// <summary>let 初值的静态类型（含值类型）：提示、字面量的类、局部的初值类型、宿主形式的类型；不知道 null（见 <see cref="Local.InitType"/>）。</summary>
    private static Type InitStaticType(Ctx ctx, object d)
    {
        try
        {
            if (TagOf(d) is { } tg) return TagType(tg);
            if (d is Symbol s) return s.Namespace == null && ctx.FindLocal(s) is { } l ? l.InitType ?? TagType(l.Tag) : null;
            return d == null ? null : HostArgType(ctx, d);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Local MakeLocal(Symbol csym, LocalKind kind, Symbol name) => new Local(csym, name, TagOf(csym), false, kind);

    /// <summary>dedupe-names：绑定名对应的 L0 名字；重复的绑定名（Clojure 取最后一个）前面的同名者换成临时名 t。</summary>
    private static List<Symbol> DedupeNames(IReadOnlyList<Symbol> syms)
    {
        var r = new List<Symbol>(syms.Count);
        for (int i = 0; i < syms.Count; i++)
        {
            string s = syms[i].ToString();
            bool dup = false;
            for (int j = i + 1; j < syms.Count; j++) if (syms[j].ToString() == s) { dup = true; break; }
            r.Add(FreshName(dup ? "t" : s));
        }
        return r;
    }

    /// <summary>
    /// sp-let（let* / loop*）。分析顺序照 psyntax：先分析体，再从最后一个绑定往前分析初值（报哪个错误、宏的副作用都按这个顺序）。
    /// </summary>
    private static Expr SpLet(bool loop, object form, Ctx ctx)
    {
        var bindings = RT.second(form);
        if (RT.count(form) < 2) throw Err(form, ctx, "Bad binding form, expected vector");
        var pairs = BindingPairs(form, ctx, bindings, "Bad binding form, expected matched symbol expression pairs");
        var c = ctx.Clear();
        var locals = new List<Local>();
        var names = new List<Symbol>();
        foreach (var (s, init) in pairs)
        {
            var nm = FreshName(s.ToString());
            var ic = c.WithLocals(locals);
            // loop 的局部同样按初值定类型（JVM 的 LocalBinding 不分 let / loop，cleat-192）：存储仍是 Object，recur 换了类型时在使用处转换失败，同 JVM
            var it = TagOf(s) == null ? InitTag(ic, init) : null;
            var initType = loop || !WarnReflectionOn() ? null : InitStaticType(ic, init);   // 只有警告用它：关着时不算
            locals.Add(new Local(s, nm, it ?? TagOf(s), false, LocalKind.Local, initType));
            names.Add(nm);
        }
        List<Expr> AnalyzeInits()
        {
            var r = new Expr[pairs.Count];
            for (int i = pairs.Count - 1; i >= 0; i--)
                r[i] = Analyze(pairs[i].Init, c.WithLocals(locals.GetRange(0, i)).NonTail());
            return r.ToList();
        }
        var forms = Items(RT.next(RT.next(form)));
        Expr body;
        List<Expr> inits;
        if (loop)
        {
            var lp = FreshName("loop");
            var ys = DedupeNames(pairs.Select(p => p.Sym).ToList());
            var inner = locals.Select((l, i) => l.WithName(ys[i])).ToList();
            // :in-loop：cleat-120 的循环体内联限制；ctx-nontail 只清 :loop，recur 实参等非尾位置仍在循环里
            var bc = c.WithLocals(inner) with { Tail = true, Loop = new LoopTarget(lp, pairs.Count), Hint = null, InLoop = true };
            var b = AnalyzeBody(forms, bc);
            body = NamedLet(lp, "loop", ys, names.Select(R), b);
            inits = AnalyzeInits();
        }
        else
        {
            body = AnalyzeBody(forms, c.WithLocals(locals));
            inits = AnalyzeInits();
        }
        if (pairs.Count == 0) return MkLet1(Array.Empty<Symbol>(), Array.Empty<Expr>(), body);
        for (int i = pairs.Count - 1; i >= 0; i--) body = MkLet1(new[] { names[i] }, new[] { inits[i] }, body);
        return body;
    }

    /// <summary>sp-recur</summary>
    private static Expr SpRecur(object form, Ctx ctx)
    {
        var args = Items(RT.next(form));
        if (!ctx.Tail) throw Err(form, ctx, "Can only recur from tail position");
        if (ctx.Loop == null) throw Err(form, ctx, "Cannot recur across try");
        if (args.Count != ctx.Loop.Count)
            throw Err(form, ctx, "Mismatched argument count to recur, expected: " + ctx.Loop.Count + " args, got: " + args.Count);
        return new Call(R(ctx.Loop.Label), ImmutableArray.CreateRange(AnalyzeAll(args, ctx.NonTail())));
    }

    // —— fn* ——

    /// <summary>fn-params：参数向量 → (必需参数, 剩余参数或 null)（检查与 C# ParseFnClause 相同）。</summary>
    private static (List<Symbol> Req, Symbol RestParam) FnParams(object form, Ctx ctx, object parms)
    {
        if (parms is not IPersistentVector) throw Err(form, ctx, "Parameter declaration should be a vector");
        var req = new List<Symbol>();
        Symbol rest = null;
        bool amp = false;
        foreach (var p in Items(parms))
        {
            if (p is not Symbol ps) throw Err(form, ctx, "fn params must be Symbols");
            if (ps.Namespace != null) throw Err(form, ctx, "Can't use qualified name as parameter: " + ps);
            if (SymIs(ps, "&"))
            {
                if (amp) throw Err(form, ctx, "Invalid parameter list");
                amp = true;
            }
            else if (amp)
            {
                if (rest != null) throw Err(form, ctx, "Unexpected parameter");
                if (TagOf(ps) != null) throw Err(form, ctx, "& arg cannot have type hint");
                rest = ps;
            }
            else req.Add(ps);
        }
        if (amp && rest == null) throw Err(form, ctx, "Invalid parameter list");
        if (req.Count > 20) throw Err(form, ctx, "Can't specify more than 20 params");
        return (req, rest);
    }

    private static readonly Lazy<Type> s_rtType = new(() => ClassForName("clojure.lang.RT"));

    /// <summary>prim-param-init：^long / ^double 形参的循环变量初值经 RT.longCast / doubleCast（cleat-042）。</summary>
    private static Expr PrimParamInit(Symbol p, Symbol outerName)
    {
        var t = TagOf(p);
        string op = t != null && t.Namespace == null ? t.Name switch { "long" => "longCast", "double" => "doubleCast", _ => null } : null;
        return op != null ? CljCallOf("CallStatic", s_fixed3, Q(s_rtType.Value), Q(op), Q(null), R(outerName)) : R(outerName);
    }

    /// <summary>fn-class-name：外层类名（或 ns）$ 名字（自名、def 的提示，或 fn__N）。</summary>
    private static string FnClassName(Ctx ctx, Symbol self)
    {
        string bse = ctx.FnName ?? MungeStr(CurrentNs.Name.ToString());
        string simple = self != null ? MungeStr(self.Name) : ctx.Hint != null ? MungeStr(ctx.Hint) : "fn__" + RT.nextID();
        return bse + "$" + simple;
    }

    private static bool RuntimeAsync() => AsyncSupport.RuntimeAsync;

    /// <summary>async-body：^:async fn 的体包成 thunk，AsyncStart / AsyncRunSync（cleat-043）。</summary>
    private static Expr AsyncBody(object form, Ctx bc, IEnumerable<object> forms)
    {
        var thunk = WithMeta(ListStar(new object[] { FnStarSym, PersistentVector.EMPTY }, forms), null);
        var node = ExpandFn(thunk, true, bc);
        return CljCallOf(RuntimeAsync() ? "AsyncStart" : "AsyncRunSync", s_false, node);
    }

    /// <summary>expand-fn*：fn* → (lambda CljFnName 子句…)，有自名时包一层 letrec。</summary>
    private static Expr ExpandFn(object form, bool thunk, Ctx ctx)
    {
        var c = ctx;
        bool isAsync = !thunk && (Truthy(Get(Meta(form), AsyncKw)) || (IsList(form) && Truthy(Get(Meta(RT.first(form)), AsyncKw))));
        object restItems = RT.next(form);
        var self = RT.first(restItems) as Symbol;
        if (self != null) restItems = RT.next(restItems);
        bool single = RT.first(restItems) is IPersistentVector;
        // 子句序列惰性地逐个实现（与 Clojure 的 loop 相同：fn 的类名先于后面子句里的 gensym 取 id）
        object clauses = single ? RT.list(restItems) : restItems;
        object ClauseOf(object cl)
        {
            if (single) return cl;
            if (!(IsList(cl) && RT.seq(cl) != null)) throw Err(form, ctx, "Bad fn clause: " + Str(cl));
            return cl;
        }
        string nm = FnClassName(c, self);
        var selfName = self != null ? FreshName(self.ToString()) : null;
        var baseLocals = LocalList.WithoutMarkers(c.Locals);
        if (thunk) baseLocals = LocalList.Cons(new Local(Symbol.intern(null, "\u0000async"), null, null, false, LocalKind.Async), baseLocals);
        if (self != null) baseLocals = LocalList.Cons(new Local(self, selfName, null, false, LocalKind.This), baseLocals);
        var baseCtx = new Ctx(true, null, baseLocals, nm, null, c.Pos, false);
        var prims = new List<object>();
        var fixedCls = new List<Clause>();
        (int N, Clause Clause)? variadic = null;
        var arities = new List<int>();
        for (var cs = RT.seq(clauses); cs != null; cs = cs.next())
        {
            var cl = ClauseOf(cs.first());
            var (req, rst) = FnParams(form, ctx, RT.first(cl));
            int n = req.Count;
            var ps = rst != null ? req.Append(rst).ToList() : req;
            if (rst != null && variadic != null) throw Err(form, ctx, "Can't have more than 1 variadic overload");
            if (rst == null && arities.Contains(n)) throw Err(form, ctx, "Can't have 2 overloads with same arity");
            if (!(rst != null || isAsync || thunk) && PrimSig(RT.first(cl), req, Get(Meta(form), RettagKw)) is { } sig)
                prims.Add(V((long)n, sig));
            var outerNames = DedupeNames(ps);
            var lp = FreshName("recur");
            var innerNames = DedupeNames(ps);
            // ^:escape 形参是语言层续延：直接调用点保留 owner，普通读出由 analyze-symbol 回退
            var locals = ps.Select((p, i) => Truthy(Get(Meta(p), EscapeKw))
                ? new Local(p, innerNames[i], null, false, LocalKind.Escape)
                : MakeLocal(p, LocalKind.Arg, innerNames[i])).ToList();
            var bc = baseCtx.WithLocals(locals) with { Tail = true, Loop = new LoopTarget(lp, ps.Count), FnName = nm, Hint = null };
            var body = isAsync ? AsyncBody(form, bc with { Loop = null }, Items(RT.next(cl))) : AnalyzeBody(Items(RT.next(cl)), bc);
            var inits = ps.Select((p, i) => PrimParamInit(p, outerNames[i])).ToList();
            var clause = MkClause(outerNames, NamedLet(lp, "recur", innerNames, inits, body));
            if (rst != null) variadic = (n, clause);
            else
            {
                fixedCls.Add(clause);
                arities.Add(n);
            }
        }
        if (variadic != null && arities.Count > 0 && arities.Max() >= variadic.Value.N + 1)
            throw Err(form, ctx, "Can't have fixed arity function with more params than variadic function");
        var built = fixedCls.ToList();
        if (variadic != null) built.Add(variadic.Value.Clause);
        // 变长子句的必需参数个数
        object variadicN = null;
        foreach (var cl in Items(clauses))
        {
            var (req, rst) = FnParams(form, ctx, RT.first(ClauseOf(cl)));
            if (rst != null) { variadicN = (long)req.Count; break; }
        }
        IPersistentMap fdata = RT.map(Ir.FnName.ClassKey, nm, Ir.FnName.VariadicKey, variadicN ?? s_false, Ir.FnName.PrimsKey, Vec(prims),
                                      Ir.FnName.AsyncKey, thunk && RuntimeAsync());
        // (fn* ^:once [] …)（lazy-seq、delay 的展开；JVM 的 FnExpr.onceOnly）：闭包字段在最后一次使用处清空（cleat-117）
        if (Truthy(Get(Meta(RT.first(form)), OnceKw))) fdata = fdata.assoc(OnceKw, true);
        Expr fnode = MkLambda(Ir.FnName.FromData(fdata), built.ToArray());
        var um = UserMeta(form)?.without(RettagKw);
        if (um != null && um.count() > 0) fnode = CljCallOf("WithMetaOf", s_false, fnode, Analyze(um, c.NonTail().At(null)));
        if (self != null) return new Letrec(ImmutableArray.Create(selfName), ImmutableArray.Create(fnode), R(selfName));
        return fnode;
    }

    /// <summary>
    /// sp-escape*（cleat-125）：(escape* (fn [k] body))。参数必须是直接 fn* 的单参子句；每次执行分配 owner，body 的直接续延调用返回 marker，
    /// 未证明路径经 EscapeFallbackOf 回到异常协议。
    /// </summary>
    private static Expr SpEscape(object form, Ctx ctx)
    {
        var rawF = RT.count(form) == 2 ? RT.second(form) : null;
        object f = null;
        if (IsList(rawF) && RT.seq(rawF) != null && (SymIs(RT.first(rawF), "fn*") || SymIs(RT.first(rawF), "fn")))
            f = SymIs(RT.first(rawF), "fn*") ? rawF : ListStar(new object[] { FnStarSym }, Items(RT.next(rawF)));
        if (!(IsList(f) && RT.seq(f) != null && SymIs(RT.first(f), "fn*"))) throw Err(form, ctx, "escape* requires a direct (fn [k] ...) form");
        var restItems = RT.next(f);
        bool single = RT.first(restItems) is IPersistentVector;
        var clauses = single ? new List<object> { restItems } : Items(restItems);
        var cl = clauses.Count == 1 ? clauses[0] : null;
        var parms = cl != null && RT.first(cl) is IPersistentVector pv ? pv : null;
        if (!(parms != null && parms.count() == 1 && parms.nth(0) is Symbol ks && ks.Namespace == null))
            throw Err(form, ctx, "escape* requires exactly one continuation parameter");
        var k = (Symbol)parms.nth(0);
        var ownerName = FreshName(k.ToString());
        var exName = FreshName("ex");
        var params2 = parms.assocN(0, WithMeta(k, RT.map(EscapeKw, true)));
        var fnForm = RT.list(FnStarSym, ListStar(new object[] { params2 }, Items(RT.next(cl))));
        var bodyCtx = ctx.WithLocals(new[] { new Local(k, ownerName, null, false, LocalKind.Escape) });
        var body = ExpandFn(fnForm, false, bodyCtx);
        var invoked = new Call(body, ImmutableArray.Create(R(ownerName)));
        var unwrapped = CljCallOf("EscapeUnwrap", s_false, R(ownerName), invoked);
        var handler = MkLambda(s_false, MkClause(new[] { exName }, CljCallOf("EscapeCatch", s_false, R(ownerName), R(exName))));
        var node = new TryCatch(unwrapped, ImmutableArray.Create<object>("System.Exception"), ImmutableArray.Create<Expr>(handler));
        return MkLet1(new[] { ownerName }, new Expr[] { CljCallOf("NewEscapeOwner", s_false) }, node);
    }

    /// <summary>sp-await*（cleat-043）</summary>
    private static Expr SpAwait(object form, Ctx ctx)
    {
        if (RT.count(form) != 2) throw Err(form, ctx, "Wrong number of arguments to await*, expected: (await* expr)");
        var m = LocalList.Enumerate(ctx.Locals).FirstOrDefault(l => l.IsMarker);
        if (m == null) throw Err(form, ctx, "(await* ...) can only be used inside a ^:async function or (async ...) block");
        if (m.Kind == LocalKind.NoAwait) throw Err(form, ctx, "(await* ...) cannot appear inside a catch or finally clause");
        return CljCallOf(RuntimeAsync() ? "AwaitAsync" : "AwaitBlocking", s_false, Analyze(RT.second(form), ctx.Clear().NonTail()));
    }

    // —— letfn* ——

    private static bool IsFnStarForm(object d) => IsList(d) && RT.seq(d) != null && SymIs(RT.first(d), "fn*");

    /// <summary>sp-letfn*：全是 fn* 时为 letrec，否则先绑 nil 再逐个 set!。</summary>
    private static Expr SpLetfn(object form, Ctx ctx)
    {
        var bindings = RT.second(form);
        var pairs = BindingPairs(form, ctx, bindings, "Bad binding form, expected matched symbol expression pairs");
        var c = ctx.Clear();
        var names = DedupeNames(pairs.Select(p => p.Sym).ToList());
        var locals = pairs.Select((p, i) => MakeLocal(p.Sym, LocalKind.Local, names[i])).ToList();
        var bc = c.WithLocals(locals);
        var inits = pairs.Select(p => Analyze(p.Init, bc.Hinted(p.Sym.Name))).ToList();
        var body = AnalyzeBody(Items(RT.next(RT.next(form))), bc);
        if (pairs.All(p => IsFnStarForm(p.Init)))
            return new Letrec(ImmutableArray.CreateRange(names), ImmutableArray.CreateRange(inits), body);
        var seq = names.Select((nm, i) => (Expr)new Set(nm, inits[i])).ToList();
        seq.Add(body);
        return MkLet1(names, names.Select(_ => (Expr)Q(null)), MkSeq(seq));
    }

    // —— try ——

    private enum ClauseHeadKind { None, Catch, Finally }

    private static ClauseHeadKind ClauseHead(object f)
    {
        if (!(IsList(f) && RT.seq(f) != null)) return ClauseHeadKind.None;
        if (SymIs(RT.first(f), "catch")) return ClauseHeadKind.Catch;
        if (SymIs(RT.first(f), "finally")) return ClauseHeadKind.Finally;
        return ClauseHeadKind.None;
    }

    /// <summary>sp-try：先把子句分组（检查顺序同原版），再按原版的展开顺序分析——体、各 catch 的处理器、finally。</summary>
    private static Expr SpTry(object form, Ctx ctx)
    {
        var c = ctx;
        bool tail = c.Tail;
        var body = new List<object>();
        var catches = new List<(Type T, Symbol B, List<object> Forms)>();
        List<object> fin = null;
        var fs = Items(RT.next(form));
        for (int i = 0; i < fs.Count; i++)
        {
            var f = fs[i];
            switch (ClauseHead(f))
            {
                case ClauseHeadKind.Catch:
                {
                    var fi = Items(f);
                    object td = fi.Count > 1 ? fi[1] : null, b = fi.Count > 2 ? fi[2] : null;
                    if (fi.Count < 3) throw Err(form, ctx, "Unable to resolve classname: nil");
                    var t = td is Keyword kw && kw.Namespace == null && kw.Name == "default" ? ClassForName("System.Exception") : MaybeType(ctx, td);
                    if (t == null) throw Err(form, ctx, "Unable to resolve classname: " + Str(td));
                    if (b is not Symbol bs) throw Err(form, ctx, "Bad binding form, expected symbol, got: " + Str(b));
                    if (bs.Namespace != null) throw Err(form, ctx, "Can't bind qualified name:" + bs);
                    catches.Add((t, bs, fi.GetRange(3, fi.Count - 3)));
                    break;
                }
                case ClauseHeadKind.Finally:
                    if (i + 1 < fs.Count) throw Err(form, ctx, "finally clause must be last in try expression");
                    fin = Items(RT.next(f));
                    break;
                default:
                    if (catches.Count > 0) throw Err(form, ctx, "Only catch or finally clause can follow catch in try expression");
                    body.Add(f);
                    break;
            }
        }
        var bnode = AnalyzeBody(body, c.NoRecur(tail));
        var hs = new List<(string Type, Expr Handler)>();
        foreach (var (t, b, hforms) in catches)
        {
            var bn = FreshName(b.ToString());
            // catch 是独立 lambda（codegen 单独成方法），:in-loop 到此为止
            var hc = (c.NoRecur(tail).NoAwait() with { InLoop = false })
                .WithLocals(new[] { new Local(b, bn, Symbol.intern(t.FullName), false, LocalKind.Catch) });
            hs.Add((TypeName(t), MkLambda(s_false, MkClause(new[] { bn }, AnalyzeBody(hforms, hc)))));
        }
        Expr finNode = fin != null ? AnalyzeBody(fin, c.NoRecur(false).NoAwait()) : null;
        if (catches.Count == 0 && finNode == null) return bnode;
        Expr x = catches.Count == 0 ? bnode
            : new TryCatch(bnode, ImmutableArray.CreateRange(hs.Select(h => (object)h.Type)), ImmutableArray.CreateRange(hs.Select(h => h.Handler)));
        return finNode != null ? new TryFinally(x, finNode) : x;
    }

    // —— case* ——

    /// <summary>sp-case*：描述 [shift mask 种类 [[哈希 测试值 skip?] …]]，再接 x、默认分支与各分支。</summary>
    private static Expr SpCase(object form, Ctx ctx)
    {
        if (RT.count(form) < 8) throw Err(form, ctx, "Malformed case*");
        var fi = Items(form);
        object x = fi[1], shift = fi[2], mask = fi[3], dflt = fi[4], imap = fi[5], testType = fi[7];
        var skip = fi.Skip(8).ToList();
        var c = ctx.Clear();
        if (!(x is Symbol xs && xs.Namespace == null && ctx.FindLocal(xs) != null)) throw Err(form, ctx, "case* expects a local as its expression");
        string tt = testType is Keyword tk ? tk.Name : Str(testType);
        long kind = tt switch { "int" => 0L, "hash-equiv" => 1L, _ => 2L };
        var skipSet = skip.Count > 0 ? skip[0] : null;
        var entries = new List<(object H, object Test, object Then, bool Skip)>();
        for (var s = RT.seq(imap); s != null; s = s.next())
        {
            var e = (IMapEntry)s.first();
            var pair = e.val();
            entries.Add((e.key(), Boundary(RT.nth(pair, 0, null)), RT.nth(pair, 1, null), Truthy(skipSet) && RT.booleanCast(RT.contains(skipSet, e.key()))));
        }
        var data = V(ParseLong(shift), ParseLong(mask), kind,
                     Vec(entries.Select(en => (object)V(ParseLong(en.H), en.Test, en.Skip))));
        // JVM 的 CaseExpr.emitExprForInts：int 测试而被测表达式不是原始类型时，*warn-on-reflection* 下报性能警告。
        // 看被测局部的初值类型（Local.InitType）：已知不是原始类型（如 (Object.) 的结果）才报，类型未知的不报（不误报）
        if (kind == 0L && WarnReflectionOn() && ctx.FindLocal(xs).InitType is { IsPrimitive: false })
        {
            var pos = Position(form, ctx);
            string sp = SourcePath();
            string file = pos?.File ?? (sp.Length > 0 ? sp : null) ?? "NO_SOURCE_PATH";
            RT.errPrintWriter().WriteLine("Performance warning, " + file + ":" + (pos != null ? Str(pos.Line) : "0") + ":" + (pos != null ? Str(pos.Column) : "0")
                + " - case has int tests, but tested expression is not primitive.");
        }
        var xn = Analyze(x, c.NonTail());
        var dn = Analyze(dflt, c);
        var thens = entries.Select(en => Analyze(en.Then, c)).ToList();
        return new CljCase(data, xn, dn, ImmutableArray.CreateRange(thens));
    }
}
