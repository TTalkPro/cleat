using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using clojure.lang;

namespace Cleat.Core.Expand;

/// <summary>
/// 展开器（designs/03-language-core.md §2）：一个顶层形式 → Lcore。宏展开、符号决议、特殊形式的语法检查与规范化，
/// 把需要运行时 / 反射的信息（Var 的 dynamic、deftype 的接口方法签名、类型名）做成数据。不做闭包分析、不折叠、不发射。
/// 规则按 ClojureCLR 的 Compiler.Analyze / AnalyzeSeq / MacroexpandSeq1 与各 Expr.Parser，错误信息与 Clojure 一致。
/// </summary>
public sealed class Expander
{
    // ———————————————— 符号 ————————————————

    private static Symbol S(string n) => Symbol.intern(n);
    private static Symbol S(string ns, string n) => Symbol.intern(ns, n);
    public static readonly Symbol DefSym = S("def"), LoopSym = S("loop*"), RecurSym = S("recur"), IfSym = S("if"), CaseSym = S("case*"),
      LetSym = S("let*"), LetfnSym = S("letfn*"), DoSym = S("do"), FnSym = S("fn*"), QuoteSym = S("quote"), TheVarSym = S("var"),
      ImportSym = S("import*"), DotSym = S("."), AssignSym = S("set!"), DeftypeSym = S("deftype*"), ReifySym = S("reify*"), TrySym = S("try"),
      ThrowSym = S("throw"), MonitorEnterSym = S("monitor-enter"), MonitorExitSym = S("monitor-exit"), CatchSym = S("catch"), FinallySym = S("finally"),
      NewSym = S("new"), AmpersandSym = S("&"), NsSym = S("ns"), InNsSym = S("in-ns"), IdentitySym = S("clojure.core", "identity"),
      InstanceSym = S("clojure.core", "instance?"), TypeArgsSym = S("type-args"), ByRefSym = S("by-ref"), ThisSym = S("this");

    public static readonly IPersistentSet Specials = PersistentHashSet.create(
      DefSym, LoopSym, RecurSym, IfSym, CaseSym, LetSym, LetfnSym, DoSym, FnSym, QuoteSym, TheVarSym, ImportSym, DotSym, AssignSym,
      DeftypeSym, ReifySym, TrySym, ThrowSym, MonitorEnterSym, MonitorExitSym, CatchSym, FinallySym, NewSym, AmpersandSym);

    public static bool IsSpecial(object sym) => sym is Symbol && Specials.contains(sym);

    private static readonly Keyword InlineKey = Keyword.intern("inline"), InlineAritiesKey = Keyword.intern("inline-arities"),
      ConstKey = Keyword.intern("const"), OnceKey = Keyword.intern("once"), ImplementsKey = Keyword.intern("implements"),
      ArglistsKey = Keyword.intern("arglists"), VolatileMutableKey = Keyword.intern("volatile-mutable"), UnsyncMutableKey = Keyword.intern("unsynchronized-mutable");

    public const int MaxPositionalArity = 20;

    // ———————————————— 状态 ————————————————

    /// <summary>当前源文件（*file* / *source-path*），只用于错误信息与 def 的元数据。</summary>
    public string SourcePath { get; set; }

    private int _counter;            // 词法变量唯一化计数：每个顶层形式重置（designs/08 §7）
    private IPersistentMap _env = PersistentHashMap.EMPTY;   // Symbol → LocalBinding（宏的 &env）
    private int _fnDepth;
    private int _line, _column;

    /// <summary>recur 的目标：当前 loop / fn 子句的绑定（null 表示此处不能 recur）。</summary>
    private sealed class Ctx
    {
        public bool Tail;                 // 尾位置（recur 只能在这里）
        public IList<LocalBinding> LoopLocals;   // recur 目标；null = 不可 recur（try 内、非尾位置）
        public Ctx With(bool tail) => new Ctx { Tail = tail, LoopLocals = tail ? LoopLocals : null };
        public static Ctx NoRecur(bool tail) => new Ctx { Tail = tail, LoopLocals = null };
    }

    public Namespace CurrentNamespace => (Namespace)RT.CurrentNSVar.deref();

    // ———————————————— 入口 ————————————————

    /// <summary>展开一个顶层形式。顶层 (do …) 不在这里拆开：由加载器逐个子形式调用（Clojure 的 eval 语义）。</summary>
    public object ExpandTopLevel(object form)
    {
        _counter = 0;
        _env = PersistentHashMap.EMPTY;
        _fnDepth = 0;
        _line = _column = 0;
        return Analyze(form, new Ctx { Tail = false, LoopLocals = null });
    }

    /// <summary>顶层形式是否 (do …)：加载器要先 macroexpand 再拆（与 Clojure 相同）。</summary>
    public bool IsTopLevelDo(object form, out ISeq body)
    {
        var me = Macroexpand(form);
        if (me is ISeq s && DoSym.Equals(RT.first(s)))
        {
            body = RT.next(s);
            return true;
        }
        body = null;
        return false;
    }

    public object Macroexpand(object form)
    {
        object exf = Macroexpand1(form);
        return exf != form ? Macroexpand(exf) : form;
    }

    // ———————————————— 分析 ————————————————

    private ExpanderException Error(string message, Keyword phase = null, Exception cause = null) =>
      new ExpanderException(SourcePath, _line, _column, phase ?? ExpanderException.PhaseCompile, message, cause);

    private object Analyze(object form, Ctx cx)
    {
        if (form is LazySeq)
        {
            object mform = form;
            form = RT.seq(form) ?? PersistentList.EMPTY;
            form = ((IObj)form).withMeta(RT.meta(mform));
        }
        switch (form)
        {
            case null: return Const(null);
            case bool b: return Const(b);
            case Symbol sym: return AnalyzeSymbol(sym);
            case Keyword kw: return Const(kw);
            case string s: return Const(s);
            case ISeq seq: return AnalyzeSeq(seq, cx);
            case IPersistentCollection coll when !(form is IRecord) && !(form is IType) && coll.count() == 0:
                return WithMetaIfAny(form, Const(form));
            case IRecord _: return Const(form);
            case IType _: return Const(form);
            case IPersistentVector v: return AnalyzeColl(Lcore.Vector, v, RT.seq(v), form);
            case IPersistentMap m:
                {
                    var items = new List<object>();
                    for (ISeq s = m.seq(); s != null; s = s.next())
                    {
                        var e = (IMapEntry)s.first();
                        items.Add(e.key()); items.Add(e.val());
                    }
                    return AnalyzeColl(Lcore.Map, m, RT.seq(items), form);
                }
            case IPersistentSet st: return AnalyzeColl(Lcore.Set, st, RT.seq(st), form);
            default: return Const(form);   // 数、字符、正则、Type、其他宿主对象
        }
    }

    private static object Const(object d) => Lcore.List(Lcore.Const, d);

    private static bool IsConst(object node) => node is ISeq s && Lcore.Const.Equals(RT.first(s));

    /// <summary>集合字面量：元素全是常量时整个是常量；否则按元素构造。元数据（如 ^:foo []）经 with-meta。</summary>
    private object AnalyzeColl(Symbol kind, object coll, ISeq items, object form)
    {
        var nodes = new List<object> { kind };
        bool allConst = true;
        for (ISeq s = items; s != null; s = s.next())
        {
            var n = Analyze(s.first(), Ctx.NoRecur(false));
            allConst &= IsConst(n);
            nodes.Add(n);
        }
        object node = allConst ? Const(coll is IObj o ? o.withMeta(null) : coll) : (object)PersistentList.create(nodes).seq();
        return WithMetaIfAny(form, node);
    }

    private object WithMetaIfAny(object form, object node)
    {
        var meta = RT.meta(form);
        if (meta == null) return node;
        // 剥掉 reader 的源位置；剩下的才是用户元数据
        meta = meta.without(RT.LineKey).without(RT.ColumnKey).without(RT.FileKey).without(RT.SourceSpanKey).without(RT.EndLineKey).without(RT.EndColumnKey);
        if (meta.count() == 0) return node;
        if (IsConst(node)) return Const(((IObj)RT.second(node)).withMeta(meta));
        return Lcore.List(S("with-meta"), node, Analyze(meta, Ctx.NoRecur(false)));
    }

    // —— 符号 ——

    private object AnalyzeSymbol(Symbol symbol)
    {
        Symbol tag = TagOf(symbol);
        if (symbol.Namespace == null)
        {
            var b = ReferenceLocal(symbol);
            if (b != null) return Tagged(tag ?? b.Tag, Lcore.List(Lcore.Local, b.Unique));
        }
        else if (NamespaceFor(symbol) == null && !Util.IsPosDigit(symbol.Name))
        {
            // Type/member：静态字段 / 属性 / 无参方法（成员种类由发射器按 Clojure 规则决定）
            var t = MaybeType(Symbol.intern(symbol.Namespace));
            if (t != null)
                return Tagged(tag, Lcore.List(Lcore.HostCall, TypeName(t), Munge(symbol.Name), true, null, PersistentList.EMPTY));
        }
        object o = Resolve(symbol);
        switch (o)
        {
            case Var v:
                if (v.IsMacro) throw Error("Can't take the value of a macro: " + v);
                if (RT.booleanCast(RT.get(v.meta(), ConstKey))) return Const(v.get());
                return Tagged(tag, VarRefNode(v));
            case Type t: return Const(t);
            default: throw Error($"Unable to resolve symbol: {symbol} in this context");
        }
    }

    private static object VarRefNode(Var v) =>
      Lcore.List(Lcore.VarRef, Symbol.intern(v.Namespace.Name.Name, v.Symbol.Name),
        RT.map(Lcore.DynamicKey, v.isDynamic(), Lcore.RedefKey, RT.booleanCast(RT.get(v.meta(), Lcore.RedefKey)) || RT.booleanCast(RT.get(v.meta(), RT.DeclaredKey))));

    private object Tagged(Symbol tag, object node)
    {
        if (tag == null) return node;
        var t = TagType(tag);
        return t == null ? node : Lcore.List(Lcore.Tag, TypeName(t), node);
    }

    // —— 序列 ——

    private object AnalyzeSeq(ISeq form, Ctx cx)
    {
        int line = _line, column = _column;
        var meta = RT.meta(form);
        if (meta != null)
        {
            if (meta.valAt(RT.LineKey) is object l) _line = Util.ConvertToInt(l);
            if (meta.valAt(RT.ColumnKey) is object c) _column = Util.ConvertToInt(c);
        }
        try
        {
            object me = MacroexpandSeq1(form);
            if (me != form) return Analyze(me, cx);

            object op = RT.first(form);
            if (op == null) throw Error($"Can't call nil, form: {form}");

            IFn inline = IsInline(op, RT.count(RT.next(form)));
            if (inline != null)
                return Analyze(MaybeTransferSourceInfo(PreserveTag(form, inline.applyTo(RT.next(form))), form), cx);

            object node;
            if (FnSym.Equals(op)) node = ParseFn(form);
            else if (op is Symbol s && Specials.contains(s)) node = ParseSpecial(s, form, cx);
            else node = ParseInvoke(form, cx);
            return Lcore.WithPos(node, Pos());
        }
        catch (ExpanderException) { throw; }
        catch (Exception e)
        {
            throw Error(e.Message, ExpanderException.PhaseCompile, e);
        }
        finally
        {
            _line = line; _column = column;
        }
    }

    private IPersistentMap Pos() => _line > 0 ? RT.map(RT.LineKey, (long)_line, RT.ColumnKey, (long)_column) : null;

    private object ParseSpecial(Symbol op, ISeq form, Ctx cx)
    {
        switch (op.Name)
        {
            case "def": return ParseDef(form);
            case "if": return ParseIf(form, cx);
            case "do": return ParseBody(RT.next(form), cx);
            case "let*": return ParseLet(form, cx, false);
            case "loop*": return ParseLet(form, cx, true);
            case "recur": return ParseRecur(form, cx);
            case "quote":
                if (RT.count(form) != 2) throw Error("Wrong number of args to quote");
                return Const(RT.second(form));
            case "var":
                {
                    var sym = RT.second(form) as Symbol ?? throw Error("var requires a symbol");
                    var v = LookupVar(sym, false) ?? throw Error($"Unable to resolve var: {sym} in this context");
                    return Lcore.List(Lcore.TheVar, Symbol.intern(v.Namespace.Name.Name, v.Symbol.Name));
                }
            case "import*":
                {
                    var name = RT.second(form) as string ?? throw Error("import* requires a string");
                    var t = RT.classForName(name) ?? throw Error("Unable to resolve classname: " + name);
                    return Lcore.List(Lcore.Import, t.FullName);
                }
            case ".": return ParseDot(form, cx);
            case "new": return ParseNew(form, cx);
            case "set!": return ParseSetBang(form, cx);
            case "letfn*": return ParseLetfn(form, cx);
            case "deftype*": return ParseDeftype(form);
            case "reify*": return ParseReify(form);
            case "try": return ParseTry(form, cx);
            case "throw":
                if (RT.count(form) != 2) throw Error("Too many arguments to throw, throw expects a single Exception instance");
                return Lcore.List(Lcore.Throw, Analyze(RT.second(form), Ctx.NoRecur(false)));
            case "monitor-enter":
            case "monitor-exit":
                if (RT.count(form) != 2) throw Error($"{op} requires one argument");
                return Lcore.List(op.Name == "monitor-enter" ? Lcore.MonitorEnter : Lcore.MonitorExit, Analyze(RT.second(form), Ctx.NoRecur(false)));
            case "case*": return ParseCase(form, cx);
            case "catch": case "finally": throw Error($"{op} must be inside try");
            case "&": throw Error("Unable to resolve symbol: & in this context");
            default: throw Error("Unknown special form: " + op);
        }
    }

    // —— 宏展开 ——

    public object Macroexpand1(object form) => form is ISeq s ? MacroexpandSeq1(s) : form;

    private object MacroexpandSeq1(ISeq form)
    {
        object op = RT.first(form);
        if (IsSpecial(op)) return form;
        Var v = IsMacro(op);
        if (v != null)
        {
            try
            {
                ISeq args = RT.cons(form, RT.cons(_env, form.next()));
                return v.applyTo(args);
            }
            catch (ArityException e)
            {
                // 隐藏宏的两个额外参数（&form &env）
                string reduced = RemoveFnSuffix(e.Name);
                if (reduced == Munge(v.Namespace.Name.Name) + "$" + Munge(v.Symbol.Name)) throw new ArityException(e.Actual - 2, e.Name);
                throw;
            }
            catch (ExpanderException) { throw; }
            catch (Exception e)
            {
                var phase = e is ArgumentException || e is InvalidOperationException || e is IExceptionInfo ? ExpanderException.PhaseSyntax : ExpanderException.PhaseMacroexpand;
                throw Error(e.Message, phase, e);
            }
        }
        if (op is Symbol sym)
        {
            string sname = sym.Name;
            if (sname.Length > 1 && sname[0] == '.' && sym.Namespace == null)
            {
                // (.substring s 2 5) => (. s substring 2 5)
                if (form.count() < 2) throw Error("Malformed member expression, expecting (.member target ...)");
                Symbol method = Symbol.intern(sname.Substring(1));
                object target = RT.second(form);
                if (MaybeType(target) != null)
                    target = ((IObj)RT.list(IdentitySym, target)).withMeta(RT.map(RT.TagKey, S("System.Type")));
                return MaybeTransferSourceInfo(PreserveTag(form, RT.listStar(DotSym, target, method, form.next().next())), form);
            }
            int index = sname.LastIndexOf('.');
            if (index > 0 && index == sname.Length - 1 && sym.Namespace == null)
                // (StringBuilder. "foo") => (new StringBuilder "foo")
                return MaybeTransferSourceInfo(RT.listStar(NewSym, Symbol.intern(sname.Substring(0, index)), form.next()), form);
            if (sym.Namespace != null && NamespaceFor(sym) == null && !Util.IsPosDigit(sym.Name) && ReferenceLocal(sym) == null)
            {
                // Type/member …  Type/.method target …  Type/new …
                var t = MaybeType(Symbol.intern(sym.Namespace));
                if (t != null)
                {
                    object rewritten;
                    if (sname == "new") rewritten = RT.listStar(NewSym, Symbol.intern(sym.Namespace), form.next());
                    else if (sname.Length > 1 && sname[0] == '.')
                    {
                        if (form.count() < 2) throw Error($"Malformed member expression, expecting ({sym} target ...)");
                        var target = ((IObj)RT.list(IdentitySym, RT.second(form))).withMeta(RT.map(RT.TagKey, Symbol.intern(t.FullName)));
                        rewritten = RT.listStar(DotSym, target, Symbol.intern(sname.Substring(1)), form.next().next());
                    }
                    else rewritten = RT.listStar(DotSym, Symbol.intern(sym.Namespace), Symbol.intern(sname), form.next());
                    return MaybeTransferSourceInfo(PreserveTag(form, rewritten), form);
                }
            }
        }
        return form;
    }

    private static string RemoveFnSuffix(string name)
    {
        // ns$name__1234 → ns$name
        int i = name.IndexOf("__", StringComparison.Ordinal);
        return i > 0 ? name.Substring(0, i) : name;
    }

    private Var IsMacro(object op)
    {
        if (op is Symbol s && ReferenceLocal(s) != null) return null;
        Var v = op as Var ?? (op is Symbol sym ? LookupVar(sym, false) : null);
        if (v != null && v.IsMacro)
        {
            if (v.Namespace != CurrentNamespace && !v.isPublic) throw Error($"Var: {v} is not public");
            return v;
        }
        return null;
    }

    private IFn IsInline(object op, int arity)
    {
        if (op is Symbol s && ReferenceLocal(s) != null) return null;
        Var v = op as Var ?? (op is Symbol sym ? LookupVar(sym, false) : null);
        if (v == null) return null;
        if (v.Namespace != CurrentNamespace && !v.isPublic) throw Error($"var: {v} is not public");
        if (!(RT.get(v.meta(), InlineKey) is IFn ret)) return null;
        var pred = RT.get(v.meta(), InlineAritiesKey) as IFn;
        return pred == null || RT.booleanCast(pred.invoke((long)arity)) ? ret : null;
    }

    private static object MaybeTransferSourceInfo(object newForm, object oldForm)
    {
        if (!(newForm is IObj n) || !(oldForm is IObj o) || o.meta() == null) return newForm;
        var meta = n.meta() ?? PersistentArrayMap.EMPTY;
        foreach (var k in new object[] { RT.LineKey, RT.ColumnKey, RT.SourceSpanKey })
            if (o.meta().containsKey(k) && !meta.containsKey(k)) meta = meta.assoc(k, o.meta().valAt(k));
        return n.withMeta(meta);
    }

    private static object PreserveTag(ISeq src, object dst)
    {
        Symbol tag = TagOf(src);
        return tag != null && dst is IObj o ? o.withMeta((IPersistentMap)RT.assoc(o.meta(), RT.TagKey, tag)) : dst;
    }

    // —— 决议 ——

    private LocalBinding ReferenceLocal(Symbol sym) => _env.valAt(sym) as LocalBinding;

    private Namespace NamespaceFor(Symbol sym) => NamespaceFor(CurrentNamespace, sym);

    private static Namespace NamespaceFor(Namespace inns, Symbol sym)
    {
        Symbol nsSym = Symbol.intern(sym.Namespace);
        return inns.LookupAlias(nsSym) ?? Namespace.find(nsSym);
    }

    /// <summary>Compiler.Resolve：Var、Type 或抛错。</summary>
    private object Resolve(Symbol symbol)
    {
        var n = CurrentNamespace;
        if (symbol.Namespace != null)
        {
            Namespace ns = NamespaceFor(n, symbol);
            if (ns == null)
            {
                var at = MaybeArrayType(symbol);
                if (at != null) return at;
                throw Error("No such namespace: " + symbol.Namespace);
            }
            Var v = ns.FindInternedVar(Symbol.intern(symbol.Name));
            if (v == null) throw Error("No such var: " + symbol);
            if (v.Namespace != n && !v.isPublic) throw Error($"var: {symbol} is not public");
            return v;
        }
        if (symbol.Name.IndexOf('.') > 0 || symbol.Name[symbol.Name.Length - 1] == ']')
            return RT.classForName(symbol.Name) ?? throw Error("Unable to resolve classname: " + symbol);
        if (symbol.Equals(NsSym)) return RT.NSVar;
        if (symbol.Equals(InNsSym)) return RT.InNSVar;
        object o = n.GetMapping(symbol);
        if (o == null)
        {
            if (symbol.Name.IndexOfAny(RT._triggerTypeChars) != -1 && RT.classForName(symbol.Name) is Type t) return t;
            if (RT.booleanCast(RT.AllowUnresolvedVarsVar.deref())) return symbol;
            throw Error($"Unable to resolve symbol: {symbol} in this context");
        }
        return o;
    }

    /// <summary>Compiler.LookupVar：internNew 时在当前命名空间创建。</summary>
    private Var LookupVar(Symbol sym, bool internNew)
    {
        Var var = null;
        if (sym.Namespace != null)
        {
            Namespace ns = NamespaceFor(sym);
            if (ns == null) return null;
            Symbol name = Symbol.intern(sym.Name);
            var = internNew && ns == CurrentNamespace ? CurrentNamespace.intern(name) : ns.FindInternedVar(name);
        }
        else if (sym.Equals(NsSym)) var = RT.NSVar;
        else if (sym.Equals(InNsSym)) var = RT.InNSVar;
        else
        {
            object o = CurrentNamespace.GetMapping(sym);
            if (o == null)
            {
                if (internNew) var = CurrentNamespace.intern(Symbol.intern(sym.Name));
            }
            else if (o is Var v) var = v;
            else throw Error($"Expecting var, but {sym} is mapped to {o}");
        }
        return var;
    }

    // —— 类型 ——

    /// <summary>HostExpr.MaybeType：符号（或 Type）是否指一个类型。</summary>
    public Type MaybeType(object form)
    {
        if (form is Type t0) return t0;
        if (!(form is Symbol sym) || sym.Namespace != null) return null;
        if (sym.Name.IndexOf('.') > 0 || sym.Name[sym.Name.Length - 1] == ']' || sym.Name.IndexOfAny(RT._triggerTypeChars) >= 0)
            return Runtime.TypeResolver.Resolve(sym.Name, Array.Empty<string>());   // 全名、数组、泛型、|…| 语法（cleat-010）
        if (_env.containsKey(sym)) return null;
        if (CurrentNamespace.GetMapping(sym) is Type t) return t;
        try { return RT.classForName(sym.Name); } catch (Exception) { return null; }
    }

    private static Type MaybeArrayType(Symbol sym)
    {
        // JVM 风格 long/1；ClojureCLR 的 HostExpr.MaybeArrayType
        if (sym.Namespace == null || !Util.IsPosDigit(sym.Name)) return null;
        var comp = TagType(Symbol.intern(sym.Namespace));
        if (comp == null) return null;
        int dim = sym.Name[0] - '0';
        for (int i = 0; i < dim; i++) comp = comp.MakeArrayType();
        return comp;
    }

    private static readonly Dictionary<string, Type> s_specialTags = new Dictionary<string, Type>
    {
        ["int"] = typeof(int), ["long"] = typeof(long), ["float"] = typeof(float), ["double"] = typeof(double), ["char"] = typeof(char),
        ["short"] = typeof(short), ["byte"] = typeof(byte), ["sbyte"] = typeof(sbyte), ["ushort"] = typeof(ushort), ["uint"] = typeof(uint),
        ["ulong"] = typeof(ulong), ["bool"] = typeof(bool), ["boolean"] = typeof(bool), ["decimal"] = typeof(decimal), ["void"] = typeof(void),
        ["object"] = typeof(object), ["objects"] = typeof(object[]), ["ints"] = typeof(int[]), ["longs"] = typeof(long[]), ["floats"] = typeof(float[]),
        ["doubles"] = typeof(double[]), ["chars"] = typeof(char[]), ["shorts"] = typeof(short[]), ["bytes"] = typeof(byte[]), ["booleans"] = typeof(bool[]),
        ["bools"] = typeof(bool[]), ["uints"] = typeof(uint[]), ["ushorts"] = typeof(ushort[]), ["ulongs"] = typeof(ulong[]), ["sbytes"] = typeof(sbyte[]),
    };

    private static Symbol TagOf(object o)
    {
        object tag = RT.get(RT.meta(o), RT.TagKey);
        return tag switch
        {
            Symbol s => s,
            string str => Symbol.intern(null, str),
            Type t => Symbol.intern(null, t.FullName),
            _ => null,
        };
    }

    /// <summary>类型提示 → Type；解析不了返回 null（提示只是提示，发射器会回退到运行时决议）。</summary>
    private static Type TagType(Symbol tag)
    {
        if (tag == null) return null;
        if (tag.Namespace == null && s_specialTags.TryGetValue(tag.Name, out var t)) return t;
        var at = MaybeArrayType(tag);
        if (at != null) return at;
        if (tag.Namespace != null) return null;
        var ns = (Namespace)RT.CurrentNSVar.deref();
        if (ns?.GetMapping(tag) is Type mapped) return mapped;
        try { return RT.classForName(tag.Name); } catch (Exception) { return null; }
    }

    /// <summary>Lcore 里的类型名：全名；动态程序集里的类型加程序集名以便发射器找到。</summary>
    public static string TypeName(Type t) => t.Assembly.IsDynamic ? t.FullName + ", " + t.Assembly.GetName().Name : t.FullName;

    public static string Munge(string name) => clojure.lang.Munge.munge(name);

    // ———————————————— 词法环境 ————————————————

    private LocalBinding RegisterLocal(Symbol sym, Symbol tag, bool isArg, bool isField = false, bool isThis = false, bool isMutable = false)
    {
        var unique = Symbol.intern(null, sym.Name + "__" + (++_counter));
        if (tag != null) unique = (Symbol)unique.withMeta(RT.map(RT.TagKey, tag));
        var b = new LocalBinding(sym, unique, tag, isArg, isField, isThis, isMutable, _fnDepth);
        _env = _env.assoc(sym, b);
        return b;
    }

    private T WithEnv<T>(Func<T> body)
    {
        var saved = _env;
        try { return body(); }
        finally { _env = saved; }
    }

    // ———————————————— 特殊形式 ————————————————

    private object ParseBody(ISeq forms, Ctx cx)
    {
        if (forms == null) return Const(null);
        var nodes = new List<object> { Lcore.Do };
        for (ISeq s = forms; s != null; s = s.next())
            nodes.Add(Analyze(s.first(), s.next() == null ? cx : Ctx.NoRecur(false)));
        return nodes.Count == 2 ? nodes[1] : PersistentList.create(nodes).seq();
    }

    private object ParseIf(ISeq form, Ctx cx)
    {
        if (RT.count(form) > 4) throw Error("Too many arguments to if");
        if (RT.count(form) < 3) throw Error("Too few arguments to if");
        return Lcore.List(Lcore.If, Analyze(RT.second(form), Ctx.NoRecur(false)), Analyze(RT.third(form), cx), Analyze(RT.fourth(form), cx));
    }

    private object ParseDef(ISeq form)
    {
        string docstring = null;
        if (RT.count(form) == 4 && RT.third(form) is string str)
        {
            docstring = str;
            form = RT.list(RT.first(form), RT.second(form), RT.fourth(form));
        }
        if (RT.count(form) > 3) throw Error("Too many arguments to def");
        if (RT.count(form) < 2) throw Error("Too few arguments to def");
        var sym = RT.second(form) as Symbol ?? throw Error("First argument to def must be a Symbol");
        Var v = LookupVar(sym, true) ?? throw Error("Can't refer to qualified var that doesn't exist");
        if (!v.Namespace.Equals(CurrentNamespace))
        {
            if (sym.Namespace == null) v = CurrentNamespace.intern(sym);
            else throw Error("Can't create defs outside of current namespace");
        }
        IPersistentMap mm = sym.meta();
        bool isDynamic = RT.booleanCast(RT.get(mm, Lcore.DynamicKey));
        if (isDynamic) v.setDynamic();
        if (!isDynamic && sym.Name.Length > 2 && sym.Name.StartsWith("*", StringComparison.Ordinal) && sym.Name.EndsWith("*", StringComparison.Ordinal))
            RT.errPrintWriter().WriteLine($"Warning: {sym} not declared dynamic and thus is not dynamically rebindable, but its name suggests otherwise. Please either indicate ^:dynamic {sym} or change the name. ({SourcePath}:{_line})");
        if (RT.booleanCast(RT.get(mm, ArglistsKey)))
            v.setMeta((IPersistentMap)RT.assoc(v.meta(), ArglistsKey, RT.second(mm.valAt(ArglistsKey))));   // 去掉 quote
        mm = (IPersistentMap)RT.assoc(mm, RT.LineKey, (long)_line).assoc(RT.ColumnKey, (long)_column).assoc(RT.FileKey, SourcePath ?? "NO_SOURCE_FILE");
        if (docstring != null) mm = (IPersistentMap)RT.assoc(mm, RT.DocKey, docstring);
        object metaNode = Analyze(mm, Ctx.NoRecur(false));
        bool initProvided = RT.count(form) == 3;
        object init = initProvided ? Analyze(RT.third(form), Ctx.NoRecur(false)) : null;
        return Lcore.List(Lcore.Def, Symbol.intern(v.Namespace.Name.Name, v.Symbol.Name), RT.map(Lcore.DynamicKey, isDynamic, Lcore.InitKey, initProvided), metaNode, init);
    }

    private object ParseLet(ISeq form, Ctx cx, bool isLoop)
    {
        if (!(RT.second(form) is IPersistentVector bindings)) throw Error("Bad binding form, expected vector");
        if (bindings.count() % 2 != 0) throw Error("Bad binding form, expected matched symbol/value pairs.");
        ISeq body = RT.next(RT.next(form));
        return WithEnv(() =>
        {
            var inits = new List<object>();
            var locals = new List<LocalBinding>();
            for (int i = 0; i < bindings.count(); i += 2)
            {
                var sym = bindings.nth(i) as Symbol ?? throw Error("Bad binding form, expected symbol, got " + bindings.nth(i));
                if (sym.Namespace != null) throw Error("Can't let qualified name: " + sym);
                if (sym.Equals(AmpersandSym)) throw Error("Can't use & as a local binding");
                object init = Analyze(bindings.nth(i + 1), Ctx.NoRecur(false));
                var b = RegisterLocal(sym, TagOf(sym), false);
                inits.Add(b.Unique); inits.Add(init);
                locals.Add(b);
            }
            Ctx bodyCx = isLoop ? new Ctx { Tail = true, LoopLocals = locals } : cx;
            object bodyNode = ParseBody(body, bodyCx);
            return Lcore.List(isLoop ? Lcore.Loop : Lcore.Let, LazilyPersistentVector.createOwning(inits.ToArray()), bodyNode);
        });
    }

    private object ParseRecur(ISeq form, Ctx cx)
    {
        if (!cx.Tail) throw Error("Can only recur from tail position");
        if (cx.LoopLocals == null) throw Error("Cannot recur across try");   // 或不在 loop / fn 内
        var args = new List<object> { Lcore.Recur };
        for (ISeq s = RT.next(form); s != null; s = s.next()) args.Add(Analyze(s.first(), Ctx.NoRecur(false)));
        if (args.Count - 1 != cx.LoopLocals.Count)
            throw Error($"Mismatched argument count to recur, expected: {cx.LoopLocals.Count} args, got: {args.Count - 1}");
        return PersistentList.create(args).seq();
    }

    private object ParseLetfn(ISeq form, Ctx cx)
    {
        if (!(RT.second(form) is IPersistentVector bindings)) throw Error("Bad binding form, expected vector");
        if (bindings.count() % 2 != 0) throw Error("Bad binding form, expected matched symbol/expression pairs.");
        ISeq body = RT.next(RT.next(form));
        return WithEnv(() =>
        {
            var lbs = new List<LocalBinding>();
            for (int i = 0; i < bindings.count(); i += 2)
            {
                var sym = bindings.nth(i) as Symbol ?? throw Error("Bad binding form, expected symbol, got " + bindings.nth(i));
                if (sym.Namespace != null) throw Error("Can't let qualified name: " + sym);
                lbs.Add(RegisterLocal(sym, TagOf(sym), false));
            }
            var inits = new List<object>();
            for (int i = 0; i < bindings.count(); i += 2)
            {
                inits.Add(lbs[i / 2].Unique);
                inits.Add(Analyze(bindings.nth(i + 1), Ctx.NoRecur(false)));
            }
            return Lcore.List(Lcore.Letfn, LazilyPersistentVector.createOwning(inits.ToArray()), ParseBody(body, cx));
        });
    }

    private object ParseFn(ISeq form)
    {
        bool once = RT.booleanCast(RT.get(RT.meta(form.first()), OnceKey));
        Symbol name = RT.second(form) as Symbol;
        ISeq clauses = name != null ? RT.next(RT.next(form)) : RT.next(form);
        if (RT.first(clauses) is IPersistentVector) clauses = RT.list(clauses);   // (fn* [args] body…) → ((…))
        return WithEnv(() =>
        {
            _fnDepth++;
            try
            {
                LocalBinding self = name != null ? RegisterLocal(name, null, false, isThis: true) : null;
                var nodes = new List<object> { Lcore.Fn, self?.Unique, once };
                var arities = new SortedDictionary<int, object>();
                int variadicArity = -1;
                for (ISeq s = clauses; s != null; s = s.next())
                {
                    var clause = s.first() as ISeq ?? throw Error("Bad fn clause: " + s.first());
                    var (node, required, variadic) = ParseFnClause(clause);
                    if (variadic)
                    {
                        if (variadicArity >= 0) throw Error("Can't have more than 1 variadic overload");
                        variadicArity = required;
                    }
                    else if (arities.ContainsKey(required)) throw Error("Can't have 2 overloads with the same arity.");
                    else arities[required] = node;
                    nodes.Add(node);
                }
                if (variadicArity >= 0 && arities.Count > 0 && arities.Keys.Max() >= variadicArity + 1)
                    throw Error("Can't have fixed arity methods with more params than the variadic method.");
                return PersistentList.create(nodes).seq();
            }
            finally { _fnDepth--; }
        });
    }

    private (object node, int required, bool variadic) ParseFnClause(ISeq clause)
    {
        var parms = RT.first(clause) as IPersistentVector ?? throw Error("Parameter declaration should be a vector");
        ISeq body = RT.next(clause);
        return WithEnv(() =>
        {
            var required = new List<LocalBinding>();
            LocalBinding rest = null;
            bool seenAmp = false;
            for (int i = 0; i < parms.count(); i++)
            {
                var p = parms.nth(i) as Symbol ?? throw Error("fn params must be Symbols");
                if (p.Namespace != null) throw Error("Can't use qualified name as parameter: " + p);
                if (p.Equals(AmpersandSym))
                {
                    if (seenAmp) throw Error("Invalid parameter list");
                    seenAmp = true;
                    continue;
                }
                if (seenAmp)
                {
                    if (rest != null) throw Error("Unexpected parameter");
                    if (TagOf(p) != null) throw Error("& arg cannot have type hint");
                    rest = RegisterLocal(p, S("clojure.lang.ISeq"), true);
                }
                else required.Add(RegisterLocal(p, TagOf(p), true));
            }
            if (seenAmp && rest == null) throw Error("Invalid parameter list");
            if (required.Count > MaxPositionalArity) throw Error($"Can't specify more than {MaxPositionalArity} params");
            var loopLocals = new List<LocalBinding>(required);
            if (rest != null) loopLocals.Add(rest);
            object bodyNode = ParseBody(body, new Ctx { Tail = true, LoopLocals = loopLocals });
            var ps = PersistentList.create(required.Select(b => (object)b.Unique).ToList()).seq() ?? PersistentList.EMPTY;
            return (Lcore.List(ps, rest?.Unique, bodyNode), required.Count, rest != null);
        });
    }

    private object ParseInvoke(ISeq form, Ctx cx)
    {
        object fexpr = Analyze(form.first(), Ctx.NoRecur(false));
        // (instance? T x) 且 T 是类型字面量
        if (fexpr is ISeq fs && Lcore.VarRef.Equals(RT.first(fs)) && InstanceSym.Equals(RT.second(fs)) && RT.count(form) == 3)
        {
            var t = MaybeType(RT.second(form));
            if (t != null) return Lcore.List(Lcore.InstanceOf, TypeName(t), Analyze(RT.third(form), Ctx.NoRecur(false)));
        }
        // (:kw x)
        if (IsConst(fexpr) && RT.second(fexpr) is Keyword kw && RT.count(form) == 2)
            return Lcore.List(Lcore.KeywordInvoke, kw, Analyze(RT.second(form), Ctx.NoRecur(false)));
        var args = new List<object>();
        for (ISeq s = RT.next(form); s != null; s = s.next()) args.Add(Analyze(s.first(), Ctx.NoRecur(false)));
        object call = Lcore.List(Lcore.Call, fexpr, PersistentList.create(args).seq() ?? PersistentList.EMPTY);
        return Tagged(TagOf(form), call);
    }

    // —— 宿主互操作 ——

    private object ParseDot(ISeq form, Ctx cx)
    {
        if (RT.count(form) < 3) throw Error("Malformed member expression, expecting (. target member ... ) or  (. target (member ...))");
        object target = RT.second(form);
        Symbol memberSym;
        ISeq args;
        bool methodRequired = false;
        if (RT.third(form) is Symbol s) { memberSym = s; args = RT.next(RT.next(RT.next(form))); }
        else if (RT.count(form) == 3 && RT.third(form) is ISeq seq && RT.first(seq) is Symbol sym) { memberSym = sym; args = RT.next(seq); methodRequired = true; }
        else throw Error("Malformed member expression, expecting (. target member-name args... ) or (. target (member-name args...)), where member-name is a Symbol");

        Type staticType = MaybeType(target);
        object instance = staticType == null ? Analyze(target, Ctx.NoRecur(false)) : null;
        // (type-args T …) 作为第一个实参：泛型方法
        IPersistentVector typeArgs = null;
        if (RT.first(args) is ISeq ta && TypeArgsSym.Equals(RT.first(ta)))
        {
            var ts = new List<object>();
            for (ISeq t = RT.next(ta); t != null; t = t.next())
                ts.Add(TypeName(MaybeType(t.first()) ?? TagType(t.first() as Symbol) ?? throw Error("Unable to resolve classname: " + t.first())));
            typeArgs = LazilyPersistentVector.createOwning(ts.ToArray());
            args = args.next();
        }
        bool isPropName = memberSym.Name.Length > 1 && memberSym.Name[0] == '-';
        string memberName = Munge(isPropName ? memberSym.Name.Substring(1) : memberSym.Name);
        string typeName = staticType != null ? TypeName(staticType) : InstanceTypeName(instance);
        Symbol tag = TagOf(form);
        if (RT.count(args) == 0 && !methodRequired)
        {
            // 字段 / 属性 / 无参方法：成员种类由发射器按 Clojure 规则决定；-name 只接受字段与属性
            object node = isPropName
              ? Lcore.List(Lcore.HostField, typeName, memberName, staticType != null, instance)
              : Lcore.List(Lcore.HostCall, typeName, memberName, staticType != null, instance, PersistentList.EMPTY);
            return Tagged(tag, node);
        }
        var argNodes = new List<object>();
        for (ISeq a = args; a != null; a = a.next()) argNodes.Add(ParseHostArg(a.first()));
        object callNode = Lcore.List(Lcore.HostCall, typeName, memberName, staticType != null, instance, PersistentList.create(argNodes).seq());
        if (typeArgs != null) callNode = ((IObj)callNode).withMeta(RT.map(Keyword.intern("type-args"), typeArgs));
        return Tagged(tag, callNode);
    }

    /// <summary>实参：(by-ref x) 第一版报错；其余按表达式，类型提示保留在 (tag …) 里。</summary>
    private object ParseHostArg(object arg)
    {
        if (arg is ISeq s && ByRefSym.Equals(RT.first(s))) throw Error("by-ref arguments are not supported yet");
        return Analyze(arg, Ctx.NoRecur(false));
    }

    /// <summary>实例的静态已知类型（来自 (tag …)），没有则 null（发射器按实例的运行时类型决议）。</summary>
    private static string InstanceTypeName(object instance) =>
      instance is ISeq s && Lcore.Tag.Equals(RT.first(s)) ? (string)RT.second(s) : null;

    private object ParseNew(ISeq form, Ctx cx)
    {
        if (RT.count(form) < 2) throw Error("wrong number of arguments, expecting: (new Typename args ...)");
        var t = MaybeType(RT.second(form)) ?? throw Error("Unable to resolve classname: " + RT.second(form));
        var args = new List<object>();
        for (ISeq a = RT.next(RT.next(form)); a != null; a = a.next()) args.Add(ParseHostArg(a.first()));
        return Lcore.List(Lcore.HostNew, TypeName(t), PersistentList.create(args).seq() ?? PersistentList.EMPTY);
    }

    private object ParseSetBang(ISeq form, Ctx cx)
    {
        if (RT.count(form) != 3) throw Error("Malformed assignment, expecting (set! target val)");
        object target = RT.second(form);
        object value = Analyze(RT.third(form), Ctx.NoRecur(false));
        if (target is Symbol sym)
        {
            var b = sym.Namespace == null ? ReferenceLocal(sym) : null;
            if (b != null)
            {
                if (!b.IsMutable) throw Error("Cannot assign to non-mutable: " + sym);
                return Lcore.List(Lcore.SetBang, Lcore.List(Lcore.Local, b.Unique), value);
            }
            var tnode = Analyze(sym, Ctx.NoRecur(false));
            if (tnode is ISeq ts && Lcore.VarRef.Equals(RT.first(ts))) return Lcore.List(Lcore.SetBang, tnode, value);
            if (tnode is ISeq hs && Lcore.HostCall.Equals(RT.first(hs)))   // Type/Field
                return Lcore.List(Lcore.HostSetField, RT.second(hs), RT.third(hs), true, null, value);
            throw Error("Invalid assignment target");
        }
        object tn = Analyze(target, Ctx.NoRecur(false));
        if (tn is ISeq tag && Lcore.Tag.Equals(RT.first(tag))) tn = RT.third(tag);
        if (tn is ISeq n && (Lcore.HostField.Equals(RT.first(n)) || Lcore.HostCall.Equals(RT.first(n)) && RT.count(RT.nth(n, 5)) == 0))
            return Lcore.List(Lcore.HostSetField, RT.second(n), RT.third(n), RT.fourth(n), RT.nth(n, 4), value);
        throw Error("Invalid assignment target");
    }

    private object ParseTry(ISeq form, Ctx cx)
    {
        var body = new List<object>();
        var catches = new List<object>();
        object finallyNode = null;
        bool caught = false;
        for (ISeq fs = form.next(); fs != null; fs = fs.next())
        {
            object f = fs.first();
            object op = f is ISeq fseq ? fseq.first() : null;
            if (!CatchSym.Equals(op) && !FinallySym.Equals(op))
            {
                if (caught) throw Error("Only catch or finally clause can follow catch in try expression");
                body.Add(f);
            }
            else if (CatchSym.Equals(op))
            {
                // (catch :default e …) catches everything (cljc spelling, cleat-032)
                var t = (RT.second(f) is Keyword dk && dk.Namespace == null && dk.Name == "default" ? typeof(Exception) : MaybeType(RT.second(f)))
                        ?? throw Error("Unable to resolve classname: " + RT.second(f));
                var sym = RT.third(f) as Symbol ?? throw Error("Bad binding form, expected symbol, got: " + RT.third(f));
                if (sym.Namespace != null) throw Error("Can't bind qualified name: " + sym);
                var handler = WithEnv(() =>
                {
                    var b = RegisterLocal(sym, Symbol.intern(t.FullName), false);
                    return Lcore.List(Lcore.Catch, TypeName(t), b.Unique, ParseBody(RT.next(RT.next(RT.next(f))), Ctx.NoRecur(cx.Tail)));
                });
                catches.Add(handler);
                caught = true;
            }
            else
            {
                if (fs.next() != null) throw Error("finally clause must be last in try expression");
                finallyNode = ParseBody(RT.next(f), Ctx.NoRecur(false));
            }
        }
        object bodyNode = ParseBody(RT.seq(body), Ctx.NoRecur(cx.Tail));
        if (catches.Count == 0 && finallyNode == null) return bodyNode;
        return Lcore.List(Lcore.Try, bodyNode, PersistentList.create(catches).seq() ?? PersistentList.EMPTY, finallyNode);
    }

    private object ParseCase(ISeq form, Ctx cx)
    {
        // (case* expr shift mask default map<minhash, [test then]> table-type test-type skip-check?)，由 case 宏准备
        var args = LazilyPersistentVector.create(form.next());
        object exprForm = args.nth(0);
        var exprNode = Analyze(exprForm, Ctx.NoRecur(false));
        if (!(exprNode is ISeq en) || !Lcore.Local.Equals(RT.first(en))) throw Error("case* expects a local as its expression");
        int shift = Util.ConvertToInt(args.nth(1)), mask = Util.ConvertToInt(args.nth(2));
        object defaultNode = Analyze(args.nth(3), cx);
        var caseMap = (IPersistentMap)args.nth(4);
        var switchType = (Keyword)args.nth(5);
        var testType = (Keyword)args.nth(6);
        object skipCheck = RT.count(args) < 8 ? null : args.nth(7);
        var entries = new List<object>();
        var keys = new SortedSet<int>();
        for (ISeq s = caseMap.seq(); s != null; s = s.next())
        {
            var me = (IMapEntry)s.first();
            int minhash = Util.ConvertToInt(me.key());
            keys.Add(minhash);
            object pair = me.val();
            object test = testType.Name == "int" ? (object)(long)Util.ConvertToInt(RT.first(pair)) : RT.first(pair);
            entries.Add(Lcore.List((long)minhash, test, Analyze(RT.second(pair), cx)));
        }
        entries.Sort((a, b) => ((long)RT.first(a)).CompareTo((long)RT.first(b)));
        int low = keys.Count > 0 ? keys.Min : 0, high = keys.Count > 0 ? keys.Max : 0;
        return Lcore.List(Lcore.Case, exprNode, (long)shift, (long)mask, (long)low, (long)high, defaultNode,
          PersistentList.create(entries).seq() ?? PersistentList.EMPTY, switchType, testType, skipCheck);
    }

    // —— deftype / reify ——

    private object ParseDeftype(ISeq form)
    {
        // (deftype* tagname classname [fields] :implements [interfaces] :tag tagname methods*)
        ISeq rform = RT.next(form);
        string tagname = ((Symbol)rform.first()).Name;
        rform = rform.next();
        Symbol className = (Symbol)rform.first();
        rform = rform.next();
        var fields = (IPersistentVector)rform.first();
        rform = rform.next();
        IPersistentMap opts = PersistentHashMap.EMPTY;
        while (rform != null && rform.first() is Keyword)
        {
            opts = opts.assoc(rform.first(), RT.second(rform));
            rform = rform.next().next();
        }
        var interfaces = (IPersistentVector)RT.get(opts, ImplementsKey, PersistentVector.EMPTY);
        return BuildType(tagname, className.Name, fields, interfaces, rform, opts, isReify: false);
    }

    private object ParseReify(ISeq form)
    {
        // (reify* [interfaces] (method-name [args] body)*)
        ISeq rform = RT.next(form);
        var interfaces = ((IPersistentVector)RT.first(rform)).cons(Symbol.intern("clojure.lang.IObj"));
        string className = Munge(CurrentNamespace.Name.Name) + "$reify__" + (++_counter);
        object node = BuildType(className, className, null, interfaces, RT.next(rform), PersistentHashMap.EMPTY, isReify: true);
        var fmeta = RT.meta(form)?.without(RT.LineKey).without(RT.ColumnKey).without(RT.FileKey).without(RT.SourceSpanKey);
        return RT.count(fmeta) > 0 ? Lcore.List(S("with-meta"), node, Analyze(fmeta, Ctx.NoRecur(false))) : node;
    }

    private object BuildType(string tagname, string className, IPersistentVector fieldSyms, IPersistentVector interfaceSyms, ISeq methodForms, IPersistentMap opts, bool isReify)
    {
        var ifaces = new List<Type>();
        for (ISeq s = RT.seq(interfaceSyms); s != null; s = s.next())
        {
            var t = Resolve((Symbol)s.first()) as Type ?? throw Error("Unable to resolve classname: " + s.first());
            if (!t.IsInterface) throw Error("only interfaces are supported, had: " + t.Name);
            ifaces.Add(t);
        }
        var candidates = GatherMethods(ifaces);
        return WithEnv(() =>
        {
            _fnDepth++;
            try
            {
                // 字段是方法体内的词法变量（IsField）；reify 没有声明字段，捕获的局部由编译器算
                var fieldNodes = new List<object>();
                var fieldLocals = new List<LocalBinding>();
                if (fieldSyms != null)
                    for (int i = 0; i < fieldSyms.count(); i++)
                    {
                        var sym = (Symbol)fieldSyms.nth(i);
                        bool vol = RT.booleanCast(RT.get(sym.meta(), VolatileMutableKey));
                        bool mut = vol || RT.booleanCast(RT.get(sym.meta(), UnsyncMutableKey));
                        var ft = TagType(TagOf(sym));
                        var b = RegisterLocal(sym, TagOf(sym), false, isField: true, isMutable: mut);
                        fieldLocals.Add(b);
                        fieldNodes.Add(Lcore.List(Lcore.Field, sym.Name, b.Unique, ft == null ? "System.Object" : TypeName(ft), mut, vol));
                    }
                var methodNodes = new List<object>();
                for (ISeq s = methodForms; s != null; s = s.next())
                    methodNodes.Add(ParseTypeMethod((ISeq)s.first(), className, candidates));
                var node = Lcore.List(Lcore.Deftype, tagname, className,
                  PersistentList.create(fieldNodes).seq() ?? PersistentList.EMPTY,
                  PersistentList.create(ifaces.Select(t => (object)TypeName(t)).ToList()).seq() ?? PersistentList.EMPTY,
                  PersistentList.create(methodNodes).seq() ?? PersistentList.EMPTY,
                  opts.assoc(Keyword.intern("reify"), isReify));
                return node;
            }
            finally { _fnDepth--; }
        });
    }

    private sealed class Candidate
    {
        public MethodInfo Method;
        public Type Interface;   // 来自哪个接口（Object 的虚方法为 null）
    }

    /// <summary>接口（含继承的接口）的全部实例方法 + Object 的虚方法，按名字分组。</summary>
    private static Dictionary<string, List<Candidate>> GatherMethods(List<Type> ifaces)
    {
        var all = new Dictionary<string, List<Candidate>>();
        void Add(Type iface, MethodInfo m)
        {
            if (m.IsStatic) return;
            if (!all.TryGetValue(m.Name, out var list)) all[m.Name] = list = new List<Candidate>();
            if (list.Any(c => c.Method == m)) return;
            list.Add(new Candidate { Method = m, Interface = iface });
        }
        foreach (var t in ifaces)
        {
            foreach (var m in t.GetMethods()) Add(t, m);
            foreach (var it in t.GetInterfaces()) foreach (var m in it.GetMethods()) Add(it, m);
        }
        foreach (var m in typeof(object).GetMethods(BindingFlags.Public | BindingFlags.Instance))
            if (m.IsVirtual && !m.IsFinal) Add(null, m);
        return all;
    }

    private object ParseTypeMethod(ISeq form, string className, Dictionary<string, List<Candidate>> candidates)
    {
        // (name [this args*] body…)；name 可为 Iface.name（显式实现）
        var dotName = RT.first(form) as Symbol ?? throw Error("Method name must be a symbol: " + RT.first(form));
        string methodName = dotName.Name;
        Type explicitIface = null;
        int idx = dotName.Name.LastIndexOf('.');
        if (idx >= 0)
        {
            explicitIface = RT.classForName(dotName.Name.Substring(0, idx)) ?? throw Error($"Unable to find interface {dotName.Name.Substring(0, idx)} for explicit method implementation: {dotName.Name}");
            methodName = dotName.Name.Substring(idx + 1);
        }
        methodName = Munge(methodName);
        var parms = RT.second(form) as IPersistentVector ?? throw Error("Method parameters must be a vector: " + dotName);
        if (parms.count() == 0 || !(parms.nth(0) is Symbol)) throw Error("Must supply at least one argument for 'this' in: " + dotName);
        var thisName = (Symbol)parms.nth(0);
        int argc = parms.count() - 1;
        var matches = (candidates.TryGetValue(methodName, out var list) ? list : new List<Candidate>())
          .Where(c => c.Method.GetParameters().Length == argc && (explicitIface == null || c.Interface == explicitIface)).ToList();
        if (matches.Count == 0) throw Error($"Can't define method not in interfaces: {dotName.Name}");
        // 参数类型提示：多个候选时必须用提示区分
        var hinted = new Type[argc];
        bool anyHint = TagOf(dotName) != null;
        for (int i = 0; i < argc; i++)
        {
            var p = parms.nth(i + 1) as Symbol ?? throw Error("Params must be Symbols");
            var tag = TagOf(p);
            if (tag != null) { anyHint = true; hinted[i] = TagType(tag) ?? throw Error("Unable to resolve classname: " + tag); }
        }
        Candidate chosen;
        if (matches.Count == 1) chosen = matches[0];
        else
        {
            if (!anyHint) throw Error("Must hint overloaded method: " + dotName.Name);
            var retHint = TagType(TagOf(dotName));
            var exact = matches.Where(c =>
            {
                var ps = c.Method.GetParameters();
                for (int i = 0; i < argc; i++) if ((hinted[i] ?? typeof(object)) != ps[i].ParameterType) return false;
                return retHint == null || c.Method.ReturnType == retHint;
            }).ToList();
            if (exact.Count != 1) throw Error("Can't find matching overloaded method: " + dotName.Name);
            chosen = exact[0];
        }
        var m = chosen.Method;
        return WithEnv(() =>
        {
            var self = RegisterLocal(thisName, Symbol.intern(className), true, isThis: true);
            var paramNodes = new List<object>();
            var locals = new List<LocalBinding>();
            var ps = m.GetParameters();
            for (int i = 0; i < argc; i++)
            {
                var p = (Symbol)parms.nth(i + 1);
                var b = RegisterLocal(Symbol.intern(p.Name), Symbol.intern(ps[i].ParameterType.FullName), true);
                locals.Add(b);
                paramNodes.Add(Lcore.List(b.Unique, TypeName(ps[i].ParameterType)));
            }
            object body = ParseBody(RT.next(RT.next(form)), new Ctx { Tail = true, LoopLocals = locals });
            return Lcore.List(Lcore.Method, chosen.Interface == null ? null : TypeName(chosen.Interface), m.Name,
              self.Unique, PersistentList.create(paramNodes).seq() ?? PersistentList.EMPTY, TypeName(m.ReturnType), body);
        });
    }

    // ———————————————— reader 决议接口 ————————————————

    /// <summary>供 reader 的语法引用使用：按展开器的规则解析。</summary>
    public sealed class ReaderResolver : IReaderResolver
    {
        private readonly Expander _e;
        public ReaderResolver(Expander e) { _e = e; }
        public Symbol CurrentNS() => _e.CurrentNamespace.Name;
        public Symbol ResolveClass(Symbol sym)
        {
            var t = _e.MaybeType(sym);
            return t == null ? null : Symbol.intern(null, t.FullName);
        }
        public Symbol ResolveAlias(Symbol sym)
        {
            var ns = _e.CurrentNamespace.LookupAlias(sym);
            return ns?.Name;
        }
        public Symbol ResolveVar(Symbol sym)
        {
            var o = _e.CurrentNamespace.GetMapping(sym);
            return o is Var v ? Symbol.intern(v.Namespace.Name.Name, v.Symbol.Name) : null;
        }
        public bool IsSpecial(object sym) => Expander.IsSpecial(sym);
    }
}
