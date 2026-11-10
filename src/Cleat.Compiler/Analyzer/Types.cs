using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Cleat.Compiler.Ir;
using Cleat.Core.Compiler;
using clojure.lang;
using static Cleat.Compiler.Analyzer.Clj;
using Symbol = clojure.lang.Symbol;

namespace Cleat.Compiler.Analyzer;

// 类型定义（analyzer.clj 的"deftype* / reify* / gen-interface / proxy-class* / gen-class*"一节，cleat-051 / 112 / 144）：
// 产出 (clj-deftype 描述 (字段…) 方法lambda…)，描述是 Clojure 向量，形状见 codegen 的 deftype-info-*。
public static partial class FormAnalyzer
{
    /// <summary>host-check：HostSyntax 返回的错误字符串报成语法错误。</summary>
    private static object HostCheck(object form, Ctx ctx, object r) => r is string s ? throw Err(form, ctx, s) : r;

    /// <summary>type-desc：[类型名 值类型?]</summary>
    private static IPersistentVector TypeDesc(object nm) => V(nm, HostSyntax.IsValueType((string)nm));

    /// <summary>deftype 字段（build-type 里的映射 {:sym :mutable :volatile :type :name}）。</summary>
    private sealed record FieldInfo(Symbol Sym, bool Mutable, bool Volatile, string Type, Symbol Name);

    /// <summary>prim-field-type（cleat-112）：^long / ^double 字段编成 Int64 / Double 字段，其余 null（object）。</summary>
    private static string PrimFieldType(FieldInfo f) => f.Type is "System.Int64" or "System.Double" ? f.Type : null;

    private static object NilToFalse(object x) => x ?? s_false;

    /// <summary>build-type：deftype* / reify* 的类型：字段、接口方法的 lambda 与描述；deftype 先登记桩类型。</summary>
    private static Expr BuildType(object form, Ctx ctx, string tagname, string className, List<object> fieldSyms, object ifaces0, object methodForms, bool reify)
    {
        var ifaces = HostCheck(form, ctx, HostSyntax.ResolveInterfaces(ifaces0));
        var fields = fieldSyms.Select(s0 =>
        {
            var s = (Symbol)s0;
            var m = Meta(s);
            bool vol = Truthy(Get(m, VolatileMutableKw));
            bool mut = vol || Truthy(Get(m, UnsyncMutableKw));
            var ft = TagType(TagOf(s));
            return new FieldInfo(s, mut, vol, ft != null ? TypeName(ft) : "System.Object", FreshName(s.ToString()));
        }).ToList();
        var fieldLocals = fields.Select(f => new Local(f.Sym, f.Name, TagOf(f.Sym), f.Mutable, LocalKind.Field)).ToList();
        var c = ctx;
        for (int i = 0; i < fields.Count; i++)
            if (fields.Skip(i + 1).Any(o => o.Sym.Name == fields[i].Sym.Name))
                throw Err(form, ctx, "Duplicate field name \"" + fields[i].Sym.Name + "\" in " + className);
        if (!reify)
            HostSyntax.DefineStub(className, fields.Select(f => (object)MungeStr(f.Sym.Name)).ToArray(),
                                  fields.Select(f => (object)PrimFieldType(f)).ToArray(), tagname, ifaces);
        var ms = Items(methodForms).Select(m =>
        {
            if (!(IsList(m) && RT.first(m) is Symbol)) throw Err(form, ctx, "Method name must be a symbol: " + Str(RT.first(m)));
            var mi = Items(m);
            var mname = mi[0];
            var parms = mi.Count > 1 ? mi[1] : null;
            var r = (object[])HostCheck(form, ctx, HostSyntax.TypeMethod(ifaces, mname, parms));
            var ptypes = ((object[])r[3]).ToList();
            var spec = V(r[0] ?? s_false, r[1], TypeDesc(r[2]), Vec(ptypes.Select(p => (object)TypeDesc(p))));
            return (Params: parms, Body: mi.Skip(2).ToList(), R: r, Spec: spec, PTypes: ptypes);
        }).ToList();
        var lambdas = ms.Select(m =>
        {
            var pitems = Items(m.Params).Cast<Symbol>().ToList();
            var thisSym = pitems[0];
            var args = pitems.Skip(1).ToList();
            var allNames = DedupeNames(pitems);
            var thisName = allNames[0];
            var argNames = allNames.Skip(1).ToList();
            var lp = FreshName("recur");
            var inner = DedupeNames(args);
            var locals = fieldLocals
                .Append(new Local(thisSym, thisName, Symbol.intern(className), false, LocalKind.This))
                .Concat(args.Select((a, i) => new Local(Symbol.intern(a.Name), inner[i], Symbol.intern((string)m.PTypes[i]), false, LocalKind.Arg)))
                .ToList();
            var bc = new Ctx(true, new LoopTarget(lp, args.Count), LocalList.PushAll(LocalList.WithoutMarkers(c.Locals), locals), c.FnName, null, c.Pos, false);
            var b = AnalyzeBody(m.Body, bc);
            return MkLambda(s_false, MkClause(new[] { thisName }.Concat(argNames), NamedLet(lp, "recur", inner, argNames.Select(R), b)));
        }).ToList();
        var layout = (object[])HostSyntax.InterfaceLayout(ifaces, ms.Select(m => (object)m.R).ToArray(), reify);
        var stubs = Vec(((object[])layout[1]).Cast<object[]>().Select(st => (object)V(
            st[0], st[1], TypeDesc(st[2]), Vec(((object[])st[3]).Select(p => (object)TypeDesc(p))),
            // 目标方法下标（Int32）：Clojure 侧 (nth st 4) 经值边界成 Int64
            Boundary(st[4]) ?? s_false)));
        int drops = 0;
        for (int i = fields.Count - 1; i >= 0 && fields[i].Sym.Name is "__meta" or "__extmap" or "__hash" or "__hasheq"; i--) drops++;
        var basis = fields.Take(fields.Count - drops).ToList();
        // 描述：[类名 reify? 字段 接口 方法 桩 basis 记录键 drops]
        var info = V(className, reify,
            Vec(fields.Select(f => (object)V(MungeStr(f.Sym.Name), f.Mutable, f.Volatile, (object)PrimFieldType(f) ?? s_false))),
            Vec((object[])layout[0]),
            Vec(ms.Select(m => (object)m.Spec)),
            stubs,
            reify ? s_false : Vec(basis.Select(f => (object)f.Sym)),
            !reify && drops > 0 ? Vec(basis.Select(f => (object)f.Sym.Name)) : PersistentVector.EMPTY,
            (long)drops);
        return new CljDeftype(info, ImmutableArray.CreateRange(fields.Select(f => f.Name)), ImmutableArray.CreateRange(lambdas));
    }

    /// <summary>build-interface：(deftype* … :cleat/interface 签名…)（gen-interface / definterface）。</summary>
    private static Expr BuildInterface(object form, Ctx ctx, string className, object extends, object sigs)
    {
        Type ResolveType(object x)
        {
            var t = x as Type ?? MaybeType(ctx, x);
            return t ?? throw Err(form, ctx, "Unable to resolve classname: " + Str(x));
        }
        // typed-sigs 是惰性的 map：签名里的错误在接口决议之后才报
        var ifaces = HostCheck(form, ctx, HostSyntax.ResolveInterfaces(extends));
        var typedSigs = Items(sigs).Select(sig =>
        {
            var items = Items(sig);
            if (items.Count < 3) throw Err(form, ctx, "Malformed gen-interface method signature");
            string mname = items[0] is Keyword k ? k.Name : Str(items[0]);
            var ret = ResolveType(items[2]);
            var ps = Items(items[1]).Select(p =>
            {
                if (IsList(p)) throw Err(form, ctx, "by-ref parameters are not supported in gen-interface");
                return ResolveType(p);
            }).ToList();
            return (Name: mname, Ret: ret, Params: ps);
        }).ToList();
        if (s_optCrossnsType)
            HostSyntax.DefineInterfaceStub(className, ifaces,
                typedSigs.Select(s => (object)new object[] { s.Name, s.Ret, s.Params.Cast<object>().ToArray() }).ToArray());
        var specs = typedSigs.Select(s => (object)V(s_false, s.Name, TypeDesc(TypeName(s.Ret)), Vec(s.Params.Select(p => (object)TypeDesc(TypeName(p)))))).ToList();
        var info = V(className, InterfaceKw, PersistentVector.EMPTY, Vec(((object[])ifaces).Cast<Type>().Select(t => (object)TypeName(t))), Vec(specs),
                     PersistentVector.EMPTY, s_false);
        return new CljDeftype(info, ImmutableArray<Symbol>.Empty, ImmutableArray<Lambda>.Empty);
    }

    /// <summary>sp-deftype*：(deftype* 标签名 类名 [字段…] 选项… 方法…)。</summary>
    private static Expr SpDeftype(object form, Ctx ctx)
    {
        if (RT.count(form) < 4) throw Err(form, ctx, "Malformed deftype*");
        var fi = Items(form);
        object tagname = fi[1], className = fi[2], fields = fi[3];
        var r = fi.Skip(4).ToList();
        IPersistentMap opts = PersistentArrayMap.EMPTY;
        int i = 0;
        while (i < r.Count && r[i] is Keyword && i + 1 < r.Count)
        {
            opts = opts.assoc(r[i], r[i + 1]);
            i += 2;
        }
        var restForms = PersistentList.create(r.Skip(i).ToList());
        if (Truthy(opts.valAt(CleatInterfaceKw)))
            return BuildInterface(form, ctx, NameOf(className), opts.valAt(ImplementsKw) ?? PersistentVector.EMPTY, opts.valAt(CleatInterfaceKw));
        return BuildType(form, ctx, NameOf(tagname), NameOf(className), Items(fields), opts.valAt(ImplementsKw) ?? PersistentVector.EMPTY,
                         restForms, false);
    }

    private static string NameOf(object x) => x is string s ? s : ((Named)x).getName();

    private static readonly Symbol IObjSym = Symbol.intern("clojure.lang.IObj");

    /// <summary>
    /// sp-reify*：类名 外层fn类名$reify__N（外层类名去掉末尾的 __N，同 JVM 的 trimGenID；不在 fn 里时 ns$reify__N，cleat-193：
    /// promise 的对象打印为 clojure.core$promise$reify__…），接口加上 IObj；有元数据时 WithMetaOf。
    /// </summary>
    private static Expr SpReify(object form, Ctx ctx)
    {
        var ifaces = RT.second(form);
        var methods = RT.next(RT.next(form));   // 惰性：类名先于方法体里的 gensym 取 id
        var iss = RT.conj((IPersistentCollection)ifaces, IObjSym);
        string className = (ctx.FnName != null ? TrimGenId(ctx.FnName) : MungeStr(CurrentNs.Name.ToString())) + "$reify__" + RT.nextID();
        Expr node = BuildType(form, ctx, className, className, new List<object>(), iss, methods, true);
        var m = UserMeta(form);
        if (m != null) node = CljCallOf("WithMetaOf", s_false, node, Analyze(m, ctx.NonTail().At(null)));
        return node;
    }

    /// <summary>JVM 的 trimGenID：去掉末尾的 __数字（只在后缀全是数字时去，munge 出的 __GT_ 之类不动）。</summary>
    private static string TrimGenId(string name)
    {
        int i = name.LastIndexOf("__", StringComparison.Ordinal);
        if (i <= 0 || i + 2 >= name.Length) return name;
        for (int k = i + 2; k < name.Length; k++) if (!char.IsAsciiDigit(name[k])) return name;
        return name.Substring(0, i);
    }

    /// <summary>v->l：数组的元素，其余为空。</summary>
    private static IEnumerable<object> VToL(object x) => x is Array a ? a.Cast<object>() : Enumerable.Empty<object>();

    private static object Nth(object x, int i) => Boundary(RT.nth(x, i));

    /// <summary>sp-proxy-class*（core_proxy 的 proxy 类型布局）。</summary>
    private static Expr SpProxyClass(object form, Ctx ctx)
    {
        var bases = Vec(Items(RT.next(form)));
        var layout = HostCheck(form, ctx, HostSyntax.ProxyLayout(CurrentNs.Name.ToString(), bases));
        var info = V(Nth(layout, 0), ProxyKw, Nth(layout, 1),
            Vec(VToL(Nth(layout, 2))),
            Vec(VToL(Nth(layout, 3)).Select(ps => (object)Vec(Items(ps).Select(p => (object)TypeDesc(p))))),
            Vec(VToL(Nth(layout, 4)).Select(m => (object)V(
                Nth(m, 0), TypeDesc(Nth(m, 1)), Vec(Items(Nth(m, 2)).Select(p => (object)TypeDesc(p))),
                Nth(m, 3), Nth(m, 4), Vec(VToL(Nth(m, 5))), Nth(m, 6), Nth(m, 7), Nth(m, 8)))));
        return new CljDeftype(info, ImmutableArray<Symbol>.Empty, ImmutableArray<Lambda>.Empty);
    }

    /// <summary>sp-gen-class*（genclass 的类型布局）。</summary>
    private static Expr SpGenClass(object form, Ctx ctx)
    {
        var layout = HostCheck(form, ctx, HostSyntax.GenClassLayout(CurrentNs.Name.ToString(), RT.second(form)));
        IPersistentVector Descs(object x) => Vec(VToL(x).Select(p => (object)TypeDesc(p)));
        var info = V(Nth(layout, 0), GenClassKw, Nth(layout, 1), Vec(VToL(Nth(layout, 2))),
            Nth(layout, 3), Nth(layout, 4),
            Vec(VToL(Nth(layout, 5)).Select(x => (object)Vec(VToL(x)))),
            Vec(VToL(Nth(layout, 6)).Select(cc => (object)V(Descs(Nth(cc, 0)), Descs(Nth(cc, 1))))),
            NilToFalse(Nth(layout, 7)), NilToFalse(Nth(layout, 8)), NilToFalse(Nth(layout, 9)),
            Vec(VToL(Nth(layout, 10)).Select(m => (object)V(
                Nth(m, 0), TypeDesc(Nth(m, 1)), Descs(Nth(m, 2)), Nth(m, 3), Nth(m, 4),
                Nth(m, 5) is { } t ? Vec(Items(t)) : s_false,
                Nth(m, 6), Nth(m, 7), NilToFalse(Nth(m, 8)), Nth(m, 9)))),
            NilToFalse(Nth(layout, 11)), NilToFalse(Nth(layout, 12)),
            Vec(VToL(Nth(layout, 13)).Select(x => (object)V(Nth(x, 0), Nth(x, 1), Nth(x, 2), Nth(x, 3), NilToFalse(Nth(x, 4)), NilToFalse(Nth(x, 5))))),
            Vec(VToL(Nth(layout, 14)).Select(x => (object)V(Nth(x, 0), Nth(x, 1), Vec(VToL(Nth(x, 2))), Nth(x, 3), Nth(x, 4)))));
        return new CljDeftype(info, ImmutableArray<Symbol>.Empty, ImmutableArray<Lambda>.Empty);
    }
}
