using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection.Emit;
using Cleat.Compiler.Ir;
using Cleat.Core.Runtime;
using clojure.lang;
using Symbol = clojure.lang.Symbol;

namespace Cleat.Compiler.Lower;

/// <summary>deftype* 描述（向量，codegen.clj 的 dinfo-*）：[类名 种类 字段 接口 方法 桩 basis 记录键 drops]；种类 true reify*、false deftype*、:interface、:proxy、:genclass。</summary>
internal static class DeftypeInfo
{
    public static object Get(object d, int k) => RT.nth(d, k);
    public static object Kind(object d) => Get(d, 1);
    public static bool IsReify(object d) => Kind(d) is true;
    public static bool IsKind(object d, string k) => Kind(d) is Keyword kw && kw.Name == k && kw.Namespace == null;

    /// <summary>描述里的列表 → 列表（false / nil 为空）。</summary>
    public static List<object> Sl(object x) => x is null or false ? new List<object>() : ((IEnumerable)x).Cast<object>().ToList();

    public static bool Truthy(object x) => x is not null and not false;
}

internal static partial class CodeGen
{
    private static List<object> Sl(object x) => DeftypeInfo.Sl(x);
    private static bool Truthy(object x) => DeftypeInfo.Truthy(x);

    /// <summary>reify 闭包捕获的变量：各方法自由变量的并集（去掉字段），按出现顺序。</summary>
    internal static List<Symbol> DeftypeCaps(CljDeftype dt)
    {
        var acc = new List<Symbol>();
        foreach (var le in dt.Methods)
            foreach (var v in le.Free)
                if (!acc.Contains(v) && !dt.Fields.Contains(v)) acc.Add(v);
        return acc;
    }

    // 类型描述 [名字 值类型?]
    private static string TName(object t) => (string)RT.nth(t, 0);
    private static bool ValueType(object t) => RT.nth(t, 1) is true;
    private static string SpecIface(object sp) => Sl(sp)[0] as string;
    private static string SpecName(object sp) => (string)Sl(sp)[1];
    private static object SpecRet(object sp) => Sl(sp)[2];
    private static List<object> SpecParams(object sp) => Sl(Sl(sp)[3]);
    private static Raw FieldRaw(object ft) => ft as string == "System.Int64" ? Raw.L64 : ft as string == "System.Double" ? Raw.Fl : Raw.None;

    private static readonly System.Reflection.MethodInfo KeywordIntern = Rt.S(typeof(Keyword), "intern", typeof(string));

    /// <summary>栈上的 object → 声明类型 t（void 时丢弃）。</summary>
    private static List<Ins> ConvertObjectCode(object t)
    {
        string tn = TName(t);
        if (tn == "System.Void") return new List<Ins> { new Il(OpCodes.Pop) };
        if (tn == "System.Object") return new List<Ins>();
        if (ValueType(t))
            return new List<Ins>
            {
                new Il(OpCodes.Ldtoken, tn), new Il(OpCodes.Call, Rt.GetTypeFromHandle), new Il(OpCodes.Call, Rt.ConvertReturn), new Il(OpCodes.Unbox_Any, tn),
            };
        return new List<Ins> { new Il(OpCodes.Castclass, tn) };
    }

    /// <summary>第 j 个实参装成 Clojure 的值：值类型装箱并按 Clojure 的规则转换（int → long）。</summary>
    private static List<Ins> BoxedArgCode(int j, object t) =>
        ValueType(t)
            ? new List<Ins> { new Il(OpCodes.Ldarg, j), new Il(OpCodes.Box, TName(t)), new Il(OpCodes.Call, Rt.BoxArg) }
            : new List<Ins> { new Il(OpCodes.Ldarg, j) };

    private static List<Ins> ThrowNotImplemented(string msg) => new List<Ins>
    {
        new Il(OpCodes.Ldstr, msg), new Il(OpCodes.Newobj, typeof(NotImplementedException).GetConstructor(new[] { typeof(string) })), new Il(OpCodes.Throw),
    };

    private static IEnumerable<Ins> Ldargs(int n) => Enumerable.Range(1, n).Select(j => (Ins)new Il(OpCodes.Ldarg, j));

    private static object[] TNames(IEnumerable<object> ts) => ts.Select(t => (object)TName(t)).ToArray();

    private static void CgDeftype(CljDeftype dt, MethodCtx mc, Ctx ctx)
    {
        var d = dt.Datum;
        (ClassDecl Cls, List<Symbol> Caps) c =
            DeftypeInfo.IsKind(d, "interface") ? (InterfaceClass(mc, d), new List<Symbol>())
            : DeftypeInfo.IsKind(d, "proxy") ? (ProxyClass(mc, d), new List<Symbol>())
            : DeftypeInfo.IsKind(d, "genclass") ? (GenClassClass(mc, d), new List<Symbol>())
            : DeftypeClass(mc, dt);
        if (DeftypeInfo.IsReify(d))
        {
            foreach (var v in c.Caps) RefVar(mc, v, true);
            mc.Emit(OpCodes.Ldnull);
            mc.Emit(OpCodes.Newobj, new MethodRef(c.Cls, ".ctor", Objs(c.Caps.Count + 1), Rt.Void, ctor: true));
        }
        else
        {
            mc.Emit(OpCodes.Ldtoken, c.Cls);
            mc.Emit(OpCodes.Call, Rt.GetTypeFromHandle);
            mc.Emit(OpCodes.Call, Rt.DefineType);
        }
        FinishValue(mc, ctx);
    }

    private static ClassDecl InterfaceClass(MethodCtx mc, object d)
    {
        var u = mc.Unit;
        string name = (string)DeftypeInfo.Get(d, 0);
        u.TypeNames.Add(name);
        var c = new ClassDecl(name) { Interface = true, Abstract = true };
        foreach (var i in Sl(DeftypeInfo.Get(d, 3))) c.Interfaces.Add(i);
        foreach (var sp in Sl(DeftypeInfo.Get(d, 4)))
            c.AddMethod(new MethodDecl(SpecName(sp)) { AbstractMethod = true, Virtual = true, Params = TNames(SpecParams(sp)), Ret = TName(SpecRet(sp)) });
        u.Decl.Classes.Add(c);
        return c;
    }

    private static (ClassDecl, List<Symbol>) DeftypeClass(MethodCtx mc, CljDeftype dt)
    {
        var u = mc.Unit;
        var d = dt.Datum;
        string name = (string)DeftypeInfo.Get(d, 0);
        bool reify = DeftypeInfo.IsReify(d);
        var fields = Sl(DeftypeInfo.Get(d, 2)).Select(Sl).ToList();   // [名字 可变? volatile? 原始类型]
        var fnames = fields.Select(f => (string)f[0]).ToList();
        var ftypes = fields.Select(f => f.Count > 3 ? f[3] : false).ToList();
        var caps = DeftypeCaps(dt);
        var cnames = caps.Select((_, j) => "c" + j).ToList();
        var specs = Sl(DeftypeInfo.Get(d, 4));
        var used = new List<string>();
        var mnames = new List<string>();
        foreach (var sp in specs)
        {
            string key = SpecName(sp) + "(" + string.Join(",", SpecParams(sp).Select(TName)) + ")";
            mnames.Add(SpecIface(sp) == null || !used.Contains(key) ? SpecName(sp) : SpecIface(sp) + "." + SpecName(sp));
            used.Add(key);
        }
        var ctorFields = reify ? cnames.Concat(new[] { "__meta" }).ToList() : fnames;
        if (!reify && caps.Count > 0)
            throw new InvalidOperationException($"deftype*: deftype* methods cannot close over locals [{string.Join(" ", caps)}]");
        u.TypeNames.Add(name);
        var c = new ClassDecl(name) { Sealed = true };
        foreach (var i in Sl(DeftypeInfo.Get(d, 3))) c.Interfaces.Add(i);
        if (reify)
            foreach (var f in ctorFields) c.AddField(f, Rt.Obj);
        else
            for (int k = 0; k < fields.Count; k++)
            {
                var fd = c.AddField(fnames[k], ftypes[k] is string ft ? ft : Rt.Obj);
                fd.Volatile = fields[k].Count > 2 && Truthy(fields[k][2]);
            }
        // 构造器的形参都是 object；^long / ^double 字段按原始签名的转换语义存（cleat-112）
        var objCtor = new MethodDecl(".ctor", MethodKind.Ctor) { Params = Objs(ctorFields.Count) };
        objCtor.Body.Add(new Il(OpCodes.Ldarg_0));
        objCtor.Body.Add(new Il(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes)));
        for (int k = 0; k < ctorFields.Count; k++)
        {
            objCtor.Body.Add(new Il(OpCodes.Ldarg_0));
            objCtor.Body.Add(new Il(OpCodes.Ldarg, k + 1));
            var raw = reify ? Raw.None : FieldRaw(ftypes[k]);
            if (raw == Raw.L64) objCtor.Body.Add(new Il(OpCodes.Call, Rt.PrimArgLong));
            else if (raw == Raw.Fl) objCtor.Body.Add(new Il(OpCodes.Call, Rt.PrimArgDouble));
            objCtor.Body.Add(new Il(OpCodes.Stfld, c.Field(ctorFields[k])));
        }
        objCtor.Body.Add(new Il(OpCodes.Ret));
        c.AddMethod(objCtor);
        // 有原始字段时另有一个按字段类型的构造器（cleat-112）
        if (!reify && ftypes.Any(t => t is string))
        {
            var typed = new MethodDecl(".ctor", MethodKind.Ctor) { Params = ftypes.Select(t => t is string s ? (object)s : Rt.Obj).ToArray() };
            typed.Body.Add(new Il(OpCodes.Ldarg_0));
            typed.Body.Add(new Il(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes)));
            for (int k = 0; k < ctorFields.Count; k++)
            {
                typed.Body.Add(new Il(OpCodes.Ldarg_0));
                typed.Body.Add(new Il(OpCodes.Ldarg, k + 1));
                typed.Body.Add(new Il(OpCodes.Stfld, c.Field(ctorFields[k])));
            }
            typed.Body.Add(new Il(OpCodes.Ret));
            c.AddMethod(typed);
        }
        for (int k = 0; k < dt.Methods.Length; k++)
            c.AddMethod(DeftypeMethod(u, c, dt.Methods[k], specs[k], mnames[k], dt.Fields, fnames, reify ? fnames.Select(_ => (object)false).ToList() : ftypes, caps, cnames));
        foreach (var st in Sl(DeftypeInfo.Get(d, 5)).Select(Sl)) c.AddMethod(DeftypeStub(name, st, specs, mnames));
        if (reify)
        {
            c.AddMethod(new MethodDecl("meta")
            {
                Ret = Rt.PersistentMapType, ImplementsType = Rt.IMetaType, ImplementsName = "meta",
                Body = { new Il(OpCodes.Ldarg_0), new Il(OpCodes.Ldfld, c.Field("__meta")), new Il(OpCodes.Castclass, Rt.PersistentMapType), new Il(OpCodes.Ret) },
            });
            var wm = new MethodDecl("withMeta") { Params = new object[] { Rt.PersistentMapType }, Ret = Rt.IObjType, ImplementsType = Rt.IObjType, ImplementsName = "withMeta" };
            foreach (var cn in cnames)
            {
                wm.Body.Add(new Il(OpCodes.Ldarg_0));
                wm.Body.Add(new Il(OpCodes.Ldfld, c.Field(cn)));
            }
            wm.Body.Add(new Il(OpCodes.Ldarg_1));
            wm.Body.Add(new Il(OpCodes.Newobj, objCtor));
            wm.Body.Add(new Il(OpCodes.Ret));
            c.AddMethod(wm);
        }
        else
        {
            var basis = new MethodDecl("getBasis") { Static = true, Ret = Rt.PersistentVectorType };
            basis.Body.Add(new LdConst(u.ConstantIndex(DeftypeInfo.Get(d, 6))));
            basis.Body.Add(new Il(OpCodes.Castclass, Rt.PersistentVectorType));
            basis.Body.Add(new Il(OpCodes.Ret));
            c.AddMethod(basis);
            RecordMembers(c, d, objCtor);
        }
        u.Decl.Classes.Add(c);
        return (c, caps);
    }

    /// <summary>方法体：this 为 ldarg 0；原始类型的参数在入口装箱进局部变量；字段读写 this 的字段；返回值转换为声明类型。</summary>
    private static MethodDecl DeftypeMethod(UnitState u, ClassDecl cls, Lambda le, object spec, string mname, ImmutableArray<Symbol> fvars,
                                            List<string> fnames, List<object> ftypes, List<Symbol> caps, List<string> cnames)
    {
        var cl = le.Clauses[0];
        var mc = new MethodCtx(u, cls, false) { ClearArgsByRef = false };
        string iface = SpecIface(spec);
        var ret = SpecRet(spec);
        var ptypes = SpecParams(spec);
        mc.Vars[cl.Params[0]] = new Loc(LocKind.Self);
        for (int k = 1; k < cl.Params.Length; k++)
        {
            var t = ptypes[k - 1];
            if (ValueType(t))
            {
                int l = mc.NewLocal(Rt.Obj);
                mc.Emit(OpCodes.Ldarg, k);
                mc.Emit(OpCodes.Box, TName(t));
                mc.Emit(OpCodes.Call, Rt.BoxArg);
                mc.Emit(OpCodes.Stloc, l);
                mc.Vars[cl.Params[k]] = new Loc(LocKind.Local, l);
            }
            else mc.Vars[cl.Params[k]] = new Loc(LocKind.Arg, k);
        }
        for (int k = 0; k < fvars.Length && k < fnames.Count; k++)
        {
            var raw = FieldRaw(ftypes[k]);
            mc.Vars[fvars[k]] = new Loc(LocKind.DField, raw: raw, field: cls.Field(fnames[k]));
        }
        for (int k = 0; k < caps.Count; k++) mc.Vars[caps[k]] = new Loc(LocKind.Field, field: cls.Field(cnames[k]));
        mc.Big = BiggerThan(cl.Body, BigBodyNodes);
        InitClearing(mc, cl.Body);
        string rn = TName(ret);
        if (rn == "System.Void")
        {
            Cg(cl.Body, mc, Ctx.Effect);
            mc.Emit(OpCodes.Ret);
        }
        else if (rn == "System.Object") Cg(cl.Body, mc, Ctx.Tail);
        else
        {
            Cg(cl.Body, mc, Ctx.Value);
            mc.EmitAll(ConvertObjectCode(ret));
            mc.Emit(OpCodes.Ret);
        }
        var m = new MethodDecl(mname) { Override = iface == null, Params = TNames(ptypes), Ret = rn };
        if (iface != null)
        {
            m.ImplementsType = iface;
            m.ImplementsName = SpecName(spec);
        }
        return mc.Finish(m);
    }

    /// <summary>接口中没有直接实现的方法：转调同名同参的方法，或抛 NotImplementedException。</summary>
    private static MethodDecl DeftypeStub(string cls, List<object> st, List<object> specs, List<string> mnames)
    {
        string iface = (string)st[0], name = (string)st[1];
        var ret = st[2];
        var ptypes = Sl(st[3]);
        object target = st.Count > 4 && Truthy(st[4]) ? st[4] : null;
        var m = new MethodDecl(iface + "." + name) { Private = true, Params = TNames(ptypes), Ret = TName(ret), ImplementsType = iface, ImplementsName = name };
        if (target == null)
        {
            m.Body.AddRange(ThrowNotImplemented($"{iface}.{name} is not implemented by {cls}"));
            return m;
        }
        int ti = Convert.ToInt32(target);
        var sp = specs[ti];
        var tret = SpecRet(sp);
        m.Body.Add(new Il(OpCodes.Ldarg_0));
        m.Body.AddRange(Ldargs(ptypes.Count));
        m.Body.Add(new Il(OpCodes.Call, new MethodRef(cls, mnames[ti], TNames(SpecParams(sp)), TName(tret))));
        string rn = TName(ret), tn = TName(tret);
        if (rn == "System.Void")
        {
            if (tn != "System.Void") m.Body.Add(new Il(OpCodes.Pop));
        }
        else if (tn != rn)
        {
            if (tn == "System.Void") m.Body.Add(new Il(OpCodes.Ldnull));
            else if (ValueType(tret)) m.Body.Add(new Il(OpCodes.Box, tn));
            if (rn == "System.Object") { }
            else if (ValueType(ret))
            {
                m.Body.Add(new Il(OpCodes.Ldtoken, rn));
                m.Body.Add(new Il(OpCodes.Call, Rt.GetTypeFromHandle));
                m.Body.Add(new Il(OpCodes.Call, Rt.ConvertReturn));
                m.Body.Add(new Il(OpCodes.Unbox_Any, rn));
            }
            else m.Body.Add(new Il(OpCodes.Castclass, rn));
        }
        m.Body.Add(new Il(OpCodes.Ret));
        return m;
    }

    /// <summary>defrecord 的另外两个构造函数与静态 create。</summary>
    private static void RecordMembers(ClassDecl c, object d, MethodDecl fullCtor)
    {
        int n = Sl(DeftypeInfo.Get(d, 2)).Count;
        int count = RT.count(d);
        var ks = count > 7 ? Sl(RT.nth(d, 7)) : new List<object>();
        int drops = count > 8 ? Convert.ToInt32(RT.nth(d, 8)) : 0;
        int nb = n - drops;
        if (drops == 0) return;
        Ins[] boxZero = { new Il(OpCodes.Ldc_I4_0), new Il(OpCodes.Box, Rt.Int32) };
        MethodDecl AltCtor(int k, IEnumerable<Ins> tail)
        {
            var m = new MethodDecl(".ctor", MethodKind.Ctor) { Params = Objs(k) };
            m.Body.Add(new Il(OpCodes.Ldarg_0));
            m.Body.AddRange(Ldargs(k));
            m.Body.AddRange(tail);
            m.Body.Add(new Il(OpCodes.Call, fullCtor));
            m.Body.Add(new Il(OpCodes.Ret));
            return m;
        }
        if (drops == 4) c.AddMethod(AltCtor(nb, new Ins[] { new Il(OpCodes.Ldnull), new Il(OpCodes.Ldnull) }.Concat(boxZero).Concat(boxZero)));
        if (n - 2 > 0 && drops == 4) c.AddMethod(AltCtor(n - 2, boxZero.Concat(boxZero)));
        var create = new MethodDecl("create") { Static = true, Params = new object[] { Rt.PersistentMapType }, Ret = c };
        create.Locals.Add(typeof(Keyword));
        for (int j = 0; j < nb; j++) create.Locals.Add(Rt.Obj);
        for (int j = 0; j < ks.Count; j++)
        {
            create.Body.Add(new Il(OpCodes.Ldarg_0));
            create.Body.Add(new Il(OpCodes.Ldstr, (string)ks[j]));
            create.Body.Add(new Il(OpCodes.Call, KeywordIntern));
            create.Body.Add(new Il(OpCodes.Dup));
            create.Body.Add(new Il(OpCodes.Stloc, 0));
            create.Body.Add(new Il(OpCodes.Ldnull));
            create.Body.Add(new Il(OpCodes.Callvirt, Rt.ValAt(2)));
            create.Body.Add(new Il(OpCodes.Stloc, j + 1));
            create.Body.Add(new Il(OpCodes.Ldarg_0));
            create.Body.Add(new Il(OpCodes.Ldloc, 0));
            create.Body.Add(new Il(OpCodes.Callvirt, Rt.I(typeof(IPersistentMap), "without", Rt.Obj)));
            create.Body.Add(new Il(OpCodes.Starg, 0));
        }
        for (int j = 1; j <= nb; j++) create.Body.Add(new Il(OpCodes.Ldloc, j));
        create.Body.Add(new Il(OpCodes.Ldnull));
        create.Body.Add(new Il(OpCodes.Ldarg_0));
        create.Body.Add(new Il(OpCodes.Call, Rt.S(typeof(RT), "seqOrElse", Rt.Obj)));
        create.Body.AddRange(boxZero);
        create.Body.AddRange(boxZero);
        create.Body.Add(new Il(OpCodes.Newobj, fullCtor));
        create.Body.Add(new Il(OpCodes.Ret));
        c.AddMethod(create);
    }

    // —— proxy-class*（cleat-043）——

    private static readonly System.Reflection.MethodInfo ProxyFn = Rt.S(Rt.OpsType, "ProxyFn", typeof(IPersistentMap), typeof(string));
    private static readonly System.Reflection.MethodInfo ProxyUpdate = Rt.S(Rt.OpsType, "ProxyUpdate", typeof(IPersistentMap), typeof(IPersistentMap));
    private static readonly System.Reflection.MethodInfo InvokeIFn = Rt.S(Rt.OpsType, "InvokeIFn", typeof(IFn), typeof(object[]));

    private static List<Ins> InvokeCode(bool self, int firstArg, List<object> ptypes)
    {
        int n = ptypes.Count, k = n + (self ? 1 : 0);
        var code = new List<Ins>();
        if (k < 20)
        {
            if (self) code.Add(new Il(OpCodes.Ldarg_0));
            for (int j = 0; j < n; j++) code.AddRange(BoxedArgCode(firstArg + j, ptypes[j]));
            code.Add(new Il(OpCodes.Callvirt, Rt.Invoke(k)));
        }
        else
        {
            code.Add(new Il(OpCodes.Ldc_I4, k));
            code.Add(new Il(OpCodes.Newarr, Rt.Obj));
            if (self)
            {
                code.Add(new Il(OpCodes.Dup));
                code.Add(new Il(OpCodes.Ldc_I4_0));
                code.Add(new Il(OpCodes.Ldarg_0));
                code.Add(new Il(OpCodes.Stelem_Ref));
            }
            for (int j = 0; j < n; j++)
            {
                code.Add(new Il(OpCodes.Dup));
                code.Add(new Il(OpCodes.Ldc_I4, j + (self ? 1 : 0)));
                code.AddRange(BoxedArgCode(firstArg + j, ptypes[j]));
                code.Add(new Il(OpCodes.Stelem_Ref));
            }
            code.Add(new Il(OpCodes.Call, InvokeIFn));
        }
        return code;
    }

    private static ClassDecl ProxyClass(MethodCtx mc, object d)
    {
        var u = mc.Unit;
        var v = Sl(d);
        string name = (string)v[0];
        string super = (string)v[2];
        var ifaces = Sl(v[3]);
        var ctors = Sl(v[4]).Select(Sl).ToList();
        var methods = Sl(v[5]).Select(Sl).ToList();
        if (u.TypeNames.Contains(name)) return u.Decl.Classes.First(c => c.Name == name);
        u.TypeNames.Add(name);
        var c = new ClassDecl(name) { Base = super };
        foreach (var i in ifaces) c.Interfaces.Add(i);
        var fmap = c.AddField("__clojureFnMap", Rt.PersistentMapType);
        foreach (var ps in ctors)
        {
            var m = new MethodDecl(".ctor", MethodKind.Ctor) { Params = TNames(ps) };
            m.Body.Add(new Il(OpCodes.Ldarg_0));
            m.Body.AddRange(Ldargs(ps.Count));
            m.Body.Add(new Il(OpCodes.Call, new MethodRef(super, ".ctor", TNames(ps), Rt.Void, ctor: true)));
            m.Body.Add(new Il(OpCodes.Ret));
            c.AddMethod(m);
        }
        var iproxy = typeof(IProxy);
        c.AddMethod(new MethodDecl("__initClojureFnMappings")
        {
            Params = new object[] { Rt.PersistentMapType }, ImplementsType = iproxy, ImplementsName = "__initClojureFnMappings",
            Body = { new Il(OpCodes.Ldarg_0), new Il(OpCodes.Ldarg_1), new Il(OpCodes.Stfld, fmap), new Il(OpCodes.Ret) },
        });
        c.AddMethod(new MethodDecl("__updateClojureFnMappings")
        {
            Params = new object[] { Rt.PersistentMapType }, ImplementsType = iproxy, ImplementsName = "__updateClojureFnMappings",
            Body = { new Il(OpCodes.Ldarg_0), new Il(OpCodes.Ldarg_0), new Il(OpCodes.Ldfld, fmap), new Il(OpCodes.Ldarg_1), new Il(OpCodes.Call, ProxyUpdate), new Il(OpCodes.Stfld, fmap), new Il(OpCodes.Ret) },
        });
        c.AddMethod(new MethodDecl("__getClojureFnMappings")
        {
            Ret = Rt.PersistentMapType, ImplementsType = iproxy, ImplementsName = "__getClojureFnMappings",
            Body = { new Il(OpCodes.Ldarg_0), new Il(OpCodes.Ldfld, fmap), new Il(OpCodes.Ret) },
        });
        foreach (var md in methods)
        {
            // [方法名 返回 形参 访问 种类 目标 后备 键 可分派?]
            string mname = (string)md[0];
            var ret = md[1];
            var ptypes = Sl(md[2]);
            string access = md[3] as string, kind = md[4] as string;
            var target = Sl(md[5]);
            string fallback = md[6] as string, key = md[7] as string;
            bool ok = Truthy(md[8]);
            int n = ptypes.Count;
            var m = new MethodDecl(mname) { Params = TNames(ptypes), Ret = TName(ret) };
            if (kind == "override")
            {
                m.Override = true;
                m.Protected = access == "protected";
            }
            else if (mname != (string)target[1]) m.Private = true;
            if (kind == "implement")
            {
                m.ImplementsType = target[0];
                m.ImplementsName = (string)target[1];
            }
            var callBase = new List<Ins> { new Il(OpCodes.Ldarg_0) };
            callBase.AddRange(Ldargs(n));
            callBase.Add(new Il(OpCodes.Call, new MethodRef(target[0], (string)target[1], TNames(ptypes), TName(ret))));
            callBase.Add(new Il(OpCodes.Ret));
            if (ok)
            {
                var noFn = new Lbl();
                m.Body.Add(new Il(OpCodes.Ldarg_0));
                m.Body.Add(new Il(OpCodes.Ldfld, fmap));
                m.Body.Add(new Il(OpCodes.Ldstr, key));
                m.Body.Add(new Il(OpCodes.Call, ProxyFn));
                m.Body.Add(new Il(OpCodes.Dup));
                m.Body.Add(new Il(OpCodes.Brfalse, noFn));
                m.Body.AddRange(InvokeCode(true, 1, ptypes));
                m.Body.AddRange(ConvertObjectCode(ret));
                m.Body.Add(new Il(OpCodes.Ret));
                m.Body.Add(new LabelIns(noFn));
                m.Body.Add(new Il(OpCodes.Pop));
            }
            m.Body.AddRange(fallback == "base" ? callBase : ThrowNotImplemented($"{target[1]} is not implemented by proxy {name}"));
            c.AddMethod(m);
        }
        foreach (var md in methods.Where(md => md[6] as string == "base"))
        {
            var ret = md[1];
            var ptypes = Sl(md[2]);
            var target = Sl(md[5]);
            var m = new MethodDecl("proxy$super$" + target[1]) { Params = TNames(ptypes), Ret = TName(ret) };
            m.Body.Add(new Il(OpCodes.Ldarg_0));
            m.Body.AddRange(Ldargs(ptypes.Count));
            m.Body.Add(new Il(OpCodes.Call, new MethodRef(target[0], (string)target[1], TNames(ptypes), TName(ret))));
            m.Body.Add(new Il(OpCodes.Ret));
            c.AddMethod(m);
        }
        u.Decl.Classes.Add(c);
        return c;
    }

    // —— gen-class*（cleat-043 / 053）——

    private static readonly System.Reflection.MethodInfo GenClassFn = Rt.S(Rt.OpsType, "GenClassFn", typeof(Var));
    private static readonly System.Reflection.MethodInfo GenClassLoadImpl = Rt.S(Rt.OpsType, "GenClassLoadImpl", typeof(string));
    private static readonly System.Reflection.MethodInfo GenClassMain = Rt.S(Rt.OpsType, "GenClassMain", typeof(Var), typeof(string[]));
    private static readonly System.Reflection.MethodInfo InternPrivate = Rt.S(typeof(Var), "internPrivate", typeof(string), typeof(string));
    private static readonly System.Reflection.MethodInfo RtNth = Rt.S(typeof(RT), "nth", Rt.Obj, Rt.Int32);

    private static ClassDecl GenClassClass(MethodCtx mc, object d)
    {
        var u = mc.Unit;
        var v = Sl(d);
        string name = (string)v[0], super = (string)v[2], implNs = (string)v[4];
        var ifaces = Sl(v[3]);
        bool loadImpl = Truthy(v[5]);
        var varFields = Sl(v[6]).Select(Sl).ToList();
        var ctors = Sl(v[7]).Select(Sl).ToList();
        string init = Truthy(v[8]) ? (string)v[8] : null, postInit = Truthy(v[9]) ? (string)v[9] : null, state = Truthy(v[10]) ? (string)v[10] : null;
        var methods = Sl(v[11]).Select(Sl).ToList();
        string factory = Truthy(v[12]) ? (string)v[12] : null, main = Truthy(v[13]) ? (string)v[13] : null;
        var exposes = Sl(v[14]).Select(Sl).ToList();
        var exposesMethods = Sl(v[15]).Select(Sl).ToList();
        var c = new ClassDecl(name) { Base = super };
        foreach (var i in ifaces) c.Interfaces.Add(i);
        foreach (var vf in varFields) c.AddField((string)vf[0], Rt.VarType, isStatic: true);
        if (state != null) c.AddField(state, Rt.Obj, readOnly: true);
        List<Ins> GetFn(string f) => new List<Ins> { new Il(OpCodes.Ldsfld, c.Field(f)), new Il(OpCodes.Call, GenClassFn) };
        List<Ins> NotDefined(string what) => ThrowNotImplemented($"{what} is not defined for {name}");

        var cctor = new MethodDecl(".cctor", MethodKind.Cctor) { Static = true };
        foreach (var vf in varFields)
        {
            cctor.Body.Add(new Il(OpCodes.Ldstr, implNs));
            cctor.Body.Add(new Il(OpCodes.Ldstr, (string)vf[1]));
            cctor.Body.Add(new Il(OpCodes.Call, InternPrivate));
            cctor.Body.Add(new Il(OpCodes.Stsfld, c.Field((string)vf[0])));
        }
        if (loadImpl)
        {
            cctor.Body.Add(new Il(OpCodes.Ldstr, implNs));
            cctor.Body.Add(new Il(OpCodes.Call, GenClassLoadImpl));
        }
        cctor.Body.Add(new Il(OpCodes.Ret));
        c.AddMethod(cctor);

        foreach (var ct in ctors)
        {
            var ps = Sl(ct[0]);
            var sps = Sl(ct[1]);
            int n = ps.Count;
            bool same = ps.Select(TName).SequenceEqual(sps.Select(TName));
            var passThrough = new List<Ins> { new Il(OpCodes.Ldarg_0) };
            passThrough.AddRange(Ldargs(n));
            passThrough.Add(new Il(OpCodes.Call, new MethodRef(super, ".ctor", TNames(sps), Rt.Void, ctor: true)));
            var m = new MethodDecl(".ctor", MethodKind.Ctor) { Params = TNames(ps) };
            m.Locals.Add(Rt.Obj);
            if (init != null)
            {
                var noInit = new Lbl();
                var afterInit = new Lbl();
                m.Body.AddRange(GetFn(init));
                m.Body.Add(new Il(OpCodes.Dup));
                m.Body.Add(new Il(OpCodes.Brfalse, noInit));
                m.Body.AddRange(InvokeCode(false, 1, ps));
                m.Body.Add(new Il(OpCodes.Stloc, 0));
                m.Body.Add(new Il(OpCodes.Ldarg_0));
                for (int j = 0; j < sps.Count; j++)
                {
                    m.Body.Add(new Il(OpCodes.Ldloc, 0));
                    m.Body.Add(new Il(OpCodes.Ldc_I4, 0));
                    m.Body.Add(new Il(OpCodes.Call, RtNth));
                    m.Body.Add(new Il(OpCodes.Ldc_I4, j));
                    m.Body.Add(new Il(OpCodes.Call, RtNth));
                    m.Body.AddRange(ConvertObjectCode(sps[j]));
                }
                m.Body.Add(new Il(OpCodes.Call, new MethodRef(super, ".ctor", TNames(sps), Rt.Void, ctor: true)));
                if (state != null)
                {
                    m.Body.Add(new Il(OpCodes.Ldarg_0));
                    m.Body.Add(new Il(OpCodes.Ldloc, 0));
                    m.Body.Add(new Il(OpCodes.Ldc_I4, 1));
                    m.Body.Add(new Il(OpCodes.Call, RtNth));
                    m.Body.Add(new Il(OpCodes.Stfld, c.Field(state)));
                }
                m.Body.Add(new Il(OpCodes.Br, afterInit));
                m.Body.Add(new LabelIns(noInit));
                m.Body.Add(new Il(OpCodes.Pop));
                m.Body.AddRange(same ? passThrough : NotDefined("init"));
                m.Body.Add(new LabelIns(afterInit));
            }
            else m.Body.AddRange(same ? passThrough : NotDefined("init"));
            if (postInit != null)
            {
                var noPost = new Lbl();
                var endPost = new Lbl();
                m.Body.AddRange(GetFn(postInit));
                m.Body.Add(new Il(OpCodes.Dup));
                m.Body.Add(new Il(OpCodes.Brfalse, noPost));
                m.Body.AddRange(InvokeCode(true, 1, ps));
                m.Body.Add(new Il(OpCodes.Pop));
                m.Body.Add(new Il(OpCodes.Br, endPost));
                m.Body.Add(new LabelIns(noPost));
                m.Body.Add(new Il(OpCodes.Pop));
                m.Body.AddRange(NotDefined("post-init"));
                m.Body.Add(new LabelIns(endPost));
            }
            m.Body.Add(new Il(OpCodes.Ret));
            c.AddMethod(m);
        }

        foreach (var md in methods)
        {
            // [方法名 返回 形参 访问 种类 目标 后备 Var字段 重载Var字段 可分派?]
            string mname = (string)md[0];
            var ret = md[1];
            var ptypes = Sl(md[2]);
            string access = md[3] as string, kind = md[4] as string, fallback = md[6] as string, vf = md[7] as string;
            var target = Sl(md[5]);
            string ovf = Truthy(md[8]) ? (string)md[8] : null;
            bool ok = Truthy(md[9]);
            bool isStatic = kind == "static";
            int n = ptypes.Count;
            var m = new MethodDecl(mname) { Params = TNames(ptypes), Ret = TName(ret) };
            switch (kind)
            {
                case "override":
                    m.Override = true;
                    m.Protected = access == "protected";
                    break;
                case "implement":
                    m.Private = mname != (string)target[1];
                    m.ImplementsType = target[0];
                    m.ImplementsName = (string)target[1];
                    break;
                case "new": m.Virtual = true; break;
                default: m.Static = true; break;
            }
            if (ok)
            {
                var found = new Lbl();
                var noFn = new Lbl();
                if (ovf != null)
                {
                    m.Body.AddRange(GetFn(ovf));
                    m.Body.Add(new Il(OpCodes.Dup));
                    m.Body.Add(new Il(OpCodes.Brtrue, found));
                    m.Body.Add(new Il(OpCodes.Pop));
                }
                m.Body.AddRange(GetFn(vf));
                m.Body.Add(new Il(OpCodes.Dup));
                m.Body.Add(new Il(OpCodes.Brfalse, noFn));
                m.Body.Add(new LabelIns(found));
                m.Body.AddRange(InvokeCode(!isStatic, isStatic ? 0 : 1, ptypes));
                m.Body.AddRange(ConvertObjectCode(ret));
                m.Body.Add(new Il(OpCodes.Ret));
                m.Body.Add(new LabelIns(noFn));
                m.Body.Add(new Il(OpCodes.Pop));
            }
            if (fallback == "base")
            {
                m.Body.Add(new Il(OpCodes.Ldarg_0));
                m.Body.AddRange(Ldargs(n));
                m.Body.Add(new Il(OpCodes.Call, new MethodRef(target[0], (string)target[1], TNames(ptypes), TName(ret))));
                m.Body.Add(new Il(OpCodes.Ret));
            }
            else m.Body.AddRange(NotDefined(mname));
            c.AddMethod(m);
        }
        if (factory != null)
            foreach (var ct in ctors)
            {
                var ps = Sl(ct[0]);
                var m = new MethodDecl(factory) { Static = true, Params = TNames(ps), Ret = c };
                for (int j = 0; j < ps.Count; j++) m.Body.Add(new Il(OpCodes.Ldarg, j));
                m.Body.Add(new Il(OpCodes.Newobj, new MethodRef(c, ".ctor", TNames(ps), Rt.Void, ctor: true)));
                m.Body.Add(new Il(OpCodes.Ret));
                c.AddMethod(m);
            }
        if (main != null)
            c.AddMethod(new MethodDecl("Main")
            {
                Static = true, Params = new object[] { typeof(string[]) },
                Body = { new Il(OpCodes.Ldsfld, c.Field(main)), new Il(OpCodes.Ldarg_0), new Il(OpCodes.Call, GenClassMain), new Il(OpCodes.Ret) },
            });
        foreach (var ex in exposes)
        {
            // [声明类型 字段 字段类型 static? get set]
            object decl = ex[0];
            string f = (string)ex[1], ft = (string)ex[2], get = Truthy(ex[4]) ? (string)ex[4] : null, set = Truthy(ex[5]) ? (string)ex[5] : null;
            bool st = Truthy(ex[3]);
            var fr = new FieldRef(decl, f);
            if (get != null)
            {
                var m = new MethodDecl(get) { Static = st, Ret = ft };
                if (st) m.Body.Add(new Il(OpCodes.Ldsfld, fr));
                else
                {
                    m.Body.Add(new Il(OpCodes.Ldarg_0));
                    m.Body.Add(new Il(OpCodes.Ldfld, fr));
                }
                m.Body.Add(new Il(OpCodes.Ret));
                c.AddMethod(m);
            }
            if (set != null)
            {
                var m = new MethodDecl(set) { Static = st, Params = new object[] { ft } };
                if (st)
                {
                    m.Body.Add(new Il(OpCodes.Ldarg_0));
                    m.Body.Add(new Il(OpCodes.Stsfld, fr));
                }
                else
                {
                    m.Body.Add(new Il(OpCodes.Ldarg_0));
                    m.Body.Add(new Il(OpCodes.Ldarg_1));
                    m.Body.Add(new Il(OpCodes.Stfld, fr));
                }
                m.Body.Add(new Il(OpCodes.Ret));
                c.AddMethod(m);
            }
        }
        foreach (var em in exposesMethods)
        {
            // [公开名 返回 形参 声明类型 基类方法名]
            string pub = (string)em[0], ret = (string)em[1], sname = (string)em[4];
            var ptypes = Sl(em[2]).Cast<object>().ToArray();
            var m = new MethodDecl(pub) { Params = ptypes, Ret = ret };
            m.Body.Add(new Il(OpCodes.Ldarg_0));
            m.Body.AddRange(Ldargs(ptypes.Length));
            m.Body.Add(new Il(OpCodes.Call, new MethodRef(em[3], sname, ptypes, ret)));
            m.Body.Add(new Il(OpCodes.Ret));
            c.AddMethod(m);
        }
        u.TypeNames.Add(name);
        u.Decl.Classes.Add(c);
        return c;
    }

    // —— async（cleat-043）——

    private static readonly Type TaskType = typeof(System.Threading.Tasks.Task);

    private static void CgAwaitAsync(Expr e, MethodCtx mc, Ctx ctx)
    {
        var helpers = Type.GetType("System.Runtime.CompilerServices.AsyncHelpers", true);
        Cg(e, mc, Ctx.Value);
        mc.Emit(OpCodes.Call, Rt.S(typeof(AsyncSupport), "ToTask", Rt.Obj));
        mc.Emit(OpCodes.Dup);
        mc.Emit(OpCodes.Call, Rt.S(helpers, "Await", TaskType));
        mc.Emit(OpCodes.Call, Rt.S(typeof(AsyncSupport), "ResultOf", TaskType));
        FinishValue(mc, ctx);
    }

    private static MethodDecl AsyncClauseMethod(UnitState u, ClassDecl cls, Clause cl, List<(Symbol Var, FieldDecl F)> fields, Symbol self)
    {
        var mc = new MethodCtx(u, cls, false);
        foreach (var (v, f) in fields) mc.Vars[v] = new Loc(LocKind.Field, field: f);
        if (self != null) mc.Vars[self] = new Loc(LocKind.Self);
        mc.Big = BiggerThan(cl.Body, BigBodyNodes);
        mc.TryDepth = 1;
        Cg(cl.Body, mc, Ctx.Tail);
        return mc.Finish(new MethodDecl("invokeAsync") { Async = true, Ret = AsyncSupport.TaskOfObjectName });
    }
}
