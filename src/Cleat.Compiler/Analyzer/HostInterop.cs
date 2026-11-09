using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cleat.Compiler.Ir;
using Cleat.Core.Compiler;
using clojure.lang;
using static Cleat.Compiler.Analyzer.Clj;
using Symbol = clojure.lang.Symbol;

namespace Cleat.Compiler.Analyzer;

// 宿主互操作（analyzer.clj 的"反射警告"与". / new / set!"两节，cleat-099 / 101 / 112）：实参与目标的静态类型、重载选择、
// 反射警告（措辞照 ClojureCLR，test_clojure 的 rt.clj 以正则核对）与 . / new / set! 三个特殊形式。
public static partial class FormAnalyzer
{
    /// <summary>parse-dot 的结果：{:static :target :member :prop? :args :type-args :method? :type}。</summary>
    private sealed record DotInfo(Type Static, object Target, string Member, bool Prop, List<object> Args, List<Type> TypeArgs, bool Method, Type Type);

    /// <summary>warn-reflection：向 *err* 报一行 "Reflection warning, 文件:行:列 - 文本"。</summary>
    private static void WarnReflection(object form, Ctx ctx, string msg)
    {
        var pos = Position(form, ctx);
        string sp = SourcePath();
        string file = pos?.File ?? (sp.Length > 0 ? sp : null) ?? "NO_SOURCE_PATH";
        RT.errPrintWriter().WriteLine("Reflection warning, " + file + ":" + (pos != null ? Str(pos.Line) : "0") + ":" + (pos != null ? Str(pos.Column) : "0") + " - " + msg);
    }

    /// <summary>warn-reflection?</summary>
    private static bool WarnReflectionOn() => ReflectionWarnings && RT.booleanCast(RT.WarnOnReflectionVar.deref());

    /// <summary>host-arg-type：宿主调用实参 / 目标的静态类型或 null（未知）。</summary>
    private static Type HostArgType(Ctx ctx, object a) => a switch
    {
        null => null,
        Symbol => ExprType(ctx, a),
        _ when IsList(a) => ExprType(ctx, a) ?? HostFormType(ctx, a),
        IPersistentVector => typeof(IPersistentVector),
        IPersistentMap => typeof(IPersistentMap),
        IPersistentSet => typeof(IPersistentSet),
        _ => Boundary(a).GetType(),   // (class a)：a 是经宿主调用取出的元素，已过值边界
    };

    /// <summary>numeric-widening：C# 的隐式数值转换（规范 §10.2.3）：源类型 → 能隐式转到的类型。</summary>
    private static readonly Dictionary<Type, HashSet<Type>> s_numericWidening = new()
    {
        [typeof(sbyte)] = new() { typeof(short), typeof(int), typeof(long), typeof(float), typeof(double), typeof(decimal) },
        [typeof(byte)] = new() { typeof(short), typeof(ushort), typeof(int), typeof(uint), typeof(long), typeof(ulong), typeof(float), typeof(double), typeof(decimal) },
        [typeof(short)] = new() { typeof(int), typeof(long), typeof(float), typeof(double), typeof(decimal) },
        [typeof(ushort)] = new() { typeof(int), typeof(uint), typeof(long), typeof(ulong), typeof(float), typeof(double), typeof(decimal) },
        [typeof(int)] = new() { typeof(long), typeof(float), typeof(double), typeof(decimal) },
        [typeof(uint)] = new() { typeof(long), typeof(ulong), typeof(float), typeof(double), typeof(decimal) },
        [typeof(long)] = new() { typeof(float), typeof(double), typeof(decimal) },
        [typeof(ulong)] = new() { typeof(float), typeof(double), typeof(decimal) },
        [typeof(char)] = new() { typeof(ushort), typeof(int), typeof(uint), typeof(long), typeof(ulong), typeof(float), typeof(double), typeof(decimal) },
        [typeof(float)] = new() { typeof(double) },
        [typeof(double)] = new(),
        [typeof(decimal)] = new(),
    };

    /// <summary>implicit-conversion?：同型、引用拓宽 / 装箱、数值拓宽。</summary>
    private static bool ImplicitConversion(Type p, Type a) =>
        p == a || p.IsAssignableFrom(a) || (s_numericWidening.TryGetValue(a, out var w) && w.Contains(p));

    /// <summary>accepts-arg?：未知实参什么都接受，已知要能隐式转换或数值间显式转换。</summary>
    private static bool AcceptsArg(Type p, Type a) =>
        a == null || ImplicitConversion(p, a) || (s_numericWidening.ContainsKey(p) && s_numericWidening.ContainsKey(a));

    /// <summary>better-param?：对静态类型 a 的实参，形参 p1 是否严格优于 p2（C# 的更优转换；未知实参按 object 算）。</summary>
    private static bool BetterParam(Type p1, Type p2, Type a)
    {
        if (p1 == p2) return false;
        if (a == null) return p1 == typeof(object);
        if (p1 == a) return true;
        if (p2 == a) return false;
        bool i1 = ImplicitConversion(p1, a), i2 = ImplicitConversion(p2, a);
        if (i1 && !i2) return true;
        if (i2 && !i1) return false;
        if (i1 && i2) return ImplicitConversion(p2, p1) && !ImplicitConversion(p1, p2);
        return false;
    }

    private static List<Type> ParamTypes(MethodBase m) => m.GetParameters().Select(p => p.ParameterType).ToList();

    /// <summary>
    /// select-overload：候选成员里按实参静态类型选出的那个，选不出 null：一个候选直接用；多个时取各形参都接受实参、
    /// 且每一位都不劣于其余候选并至少一位严格更优的那个。
    /// </summary>
    private static T SelectOverload<T>(IEnumerable<T> members0, IReadOnlyList<Type> types) where T : MethodBase
    {
        var members = members0.ToList();
        if (members.Count == 0) return null;
        if (members.Count == 1) return members[0];
        var applicable = members.Where(m =>
        {
            var ps = ParamTypes(m);
            for (int i = 0; i < Math.Min(ps.Count, types.Count); i++) if (!AcceptsArg(ps[i], types[i])) return false;
            return true;
        }).ToList();
        bool Better(T m, T o)
        {
            var ps = ParamTypes(m);
            var qs = ParamTypes(o);
            int k = Math.Min(Math.Min(ps.Count, qs.Count), types.Count);
            bool anyWorse = false, anyBetter = false;
            for (int i = 0; i < k; i++)
            {
                if (BetterParam(qs[i], ps[i], types[i])) anyWorse = true;
                if (BetterParam(ps[i], qs[i], types[i])) anyBetter = true;
            }
            return !anyWorse && anyBetter;
        }
        var best = applicable.Where(m => applicable.All(o => ReferenceEquals(m, o) || Better(m, o))).ToList();
        if (applicable.Count == 1) return applicable[0];
        if (best.Count == 1) return best[0];
        return null;
    }

    /// <summary>plain-method?：非泛型、形参没有 by-ref / 指针 / params 的方法。</summary>
    private static bool PlainMethod(MethodInfo m) =>
        !m.ContainsGenericParameters && !m.IsGenericMethodDefinition
        && !m.GetParameters().Any(p => p.ParameterType.IsByRef || p.ParameterType.IsPointer || p.IsDefined(typeof(ParamArrayAttribute), false));

    /// <summary>type-and-interfaces：t 自己，接口时再加上它继承的接口。</summary>
    private static IEnumerable<Type> TypeAndInterfaces(Type t) => t.IsInterface ? new[] { t }.Concat(t.GetInterfaces()) : new[] { t };

    /// <summary>host-methods：t 的公共方法里名为 nm、n 个形参、静态性为 isStatic 的。</summary>
    private static List<MethodInfo> HostMethods(Type t, string nm, int n, bool isStatic) =>
        TypeAndInterfaces(t).SelectMany(x => x.GetMethods())
            .Where(m => m.Name == nm && m.IsStatic == isStatic && m.GetParameters().Length == n).ToList();

    private static bool HostFieldOrProperty(Type t, string nm) =>
        TypeAndInterfaces(t).Any(x => x.GetField(nm) != null || x.GetProperty(nm) != null);

    /// <summary>host-call-return-type：(. target member args…) 能决议时的返回类型（方法的返回类型、属性 / 字段的类型），否则 null。</summary>
    private static Type HostCallReturnType(Ctx ctx, object dot)
    {
        var d = ParseDot(dot, ctx);
        if (d.Type == null || d.TypeArgs != null || HostSyntax.IsStub(d.Type)) return null;
        var types = d.Args.Select(a => HostArgType(ctx, a)).ToList();
        var m = SelectOverload(HostMethods(d.Type, d.Member, d.Args.Count, d.Static != null).Where(PlainMethod), types);
        if (m != null) return m.ReturnType != typeof(void) ? m.ReturnType : null;
        if (d.Args.Count != 0) return null;
        foreach (var x in TypeAndInterfaces(d.Type))
        {
            var r = x.GetProperty(d.Member)?.PropertyType ?? x.GetField(d.Member)?.FieldType;
            if (r != null) return r;
        }
        return null;
    }

    /// <summary>
    /// host-form-type：(f …) 形式的静态类型：(quote x) 的类、(new T …) / (T. …) 的 T、(var x) / (def …) 的 Var、宿主调用的返回类型、
    /// 其余调用按 Var 的 :tag 或 :inline 展开后的形式；看不出来 null（出错也是 null）。
    /// </summary>
    private static Type HostFormType(Ctx ctx, object a)
    {
        var op = RT.first(a);
        try
        {
            if (op is not Symbol sop) return null;
            if (sop.Namespace == null && ctx.FindLocal(sop) != null) return null;
            if (SymIs(op, "quote")) return Boundary(RT.second(a))?.GetType();
            if (SymIs(op, "new")) return MaybeType(ctx, RT.second(a));
            if (SymIs(op, ".")) return HostCallReturnType(ctx, a);
            if (SymIs(op, "var") || SymIs(op, "def")) return typeof(Var);
            if (SpecialHead(op) != null) return null;
            if (IsHostSugar(sop) || (sop.Namespace != null && StaticMemberOf(ctx, sop) != null))
            {
                var dot = RewriteToDot(ctx, a, a);
                if (dot == null) return null;
                return SymIs(RT.first(dot), "new") ? MaybeType(ctx, RT.second(dot)) : HostCallReturnType(ctx, dot);
            }
            var (o, _, _) = ResolveIn(CurrentNs, sop);
            if (o is Var v && !v.IsMacro)
            {
                var vm = v.meta();
                var inline = Get(vm, InlineKw);
                var arities = Get(vm, InlineAritiesKw);
                int n = RT.count(RT.next(a));
                var tt = TagType(Get(vm, TagKw));
                if (tt != null) return tt;
                // :inline 的调用在 var-call 里展开成宿主调用（(int x) → (. RT (intCast x))），类型看展开后的形式
                if (Truthy(inline) && inline is IFn ifn && (!Truthy(arities) || Truthy(((IFn)arities).invoke((long)n))))
                    return HostArgType(ctx, ifn.applyTo(RT.next(a)));
            }
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string ArgTypesString(IEnumerable<Type> types) => string.Join(", ", types.Select(t => t != null ? t.FullName : "unknown"));

    /// <summary>check-host-call：(. target member args…) 的反射警告。</summary>
    private static void CheckHostCall(object form, Ctx ctx, DotInfo d)
    {
        if (!WarnReflectionOn()) return;
        int n = d.Args.Count;
        var type = d.Type;
        string member = d.Member;
        if (type == null)
        {
            WarnReflection(form, ctx, n == 0 && !d.Method
                ? "reference to field/property " + member + " can't be resolved (target class is unknown)."
                : "call to method " + member + " can't be resolved (target class is unknown).");
            return;
        }
        string nm = type.FullName;
        if (HostSyntax.IsStub(type)) return;
        bool isStatic = d.Static != null;
        if (isStatic && n == 0) return;
        if (member.StartsWith("proxy$super$", StringComparison.Ordinal)) return;   // proxy-super：生成的代理类上的方法
        var types = d.Args.Select(a => HostArgType(ctx, a)).ToList();
        var methods = HostMethods(type, member, n, isStatic);
        var plain = methods.Where(PlainMethod).ToList();
        if (methods.Count > 0 && plain.Count == 0) return;
        if (SelectOverload(plain, types) != null) return;
        if (isStatic)
            WarnReflection(form, ctx, "call to static method " + member + " on " + nm + " can't be resolved "
                + (methods.Count == 0 ? "(no such method)." : "(argument types: " + ArgTypesString(types) + ")."));
        else if (n == 0 && !d.Method)
        {
            if (!HostFieldOrProperty(type, member))
                WarnReflection(form, ctx, "reference to field/property " + member + " on " + nm + " can't be resolved.");
        }
        else if (methods.Count == 0)
            WarnReflection(form, ctx, "call to method " + member + " on " + nm + " can't be resolved (no such method).");
        else
            WarnReflection(form, ctx, "call to method " + member + " on " + nm + " can't be resolved (argument types: " + ArgTypesString(types) + ").");
    }

    /// <summary>check-host-ctor：(new T args…) 的反射警告。</summary>
    private static void CheckHostCtor(object form, Ctx ctx, Type t, List<object> args)
    {
        if (!WarnReflectionOn() || HostSyntax.IsStub(t)) return;
        int n = args.Count;
        var ctors = t.GetConstructors().Where(c => c.GetParameters().Length == n);
        if (SelectOverload(ctors, args.Select(a => HostArgType(ctx, a)).ToList()) == null)
            WarnReflection(form, ctx, "call to " + t.FullName + " ctor can't be resolved.");
    }

    /// <summary>
    /// host-args：宿主调用的实参。静态类型是引用类型时包一层 (clj-call "Tagged" #f 'T e)，codegen 的静态决议据此选重载（cleat-101）。
    /// </summary>
    private static List<Expr> HostArgs(object form, Ctx ctx, IEnumerable<object> args)
    {
        var r = new List<Expr>();
        foreach (var a in args)
        {
            if (IsByRefArg(a)) throw Err(form, ctx, "by-ref is only allowed as an argument of a host method or constructor call");
            var node = Analyze(a, ctx.Clear().NonTail());
            Type t = a is string || Util.IsNumeric(a) || a is Keyword || a == null || a is bool || a is char ? null : HostArgType(ctx, a);
            r.Add(t != null && !t.IsValueType && t != typeof(object) && !HostSyntax.IsStub(t) ? CljCallOf("Tagged", s_false, Q(t), node) : node);
        }
        return r;
    }

    private static bool IsByRefArg(object a) => IsList(a) && SymIs(RT.first(a), "by-ref");

    /// <summary>
    /// by-ref 实参的宿主调用（cleat-183，ClojureCLR 的 <c>(by-ref x)</c>）：x 必须是局部（let / loop / fn 形参、catch 绑定、deftype 的可变字段）。
    /// 改写成 <c>(let [r (ByRefHost kind T member mask [inst] 实参 …)] (set! x (ByRefNth r 1)) … (ByRefNth r 0))</c>：调用返回
    /// [结果, 各 by-ref 位置的新值]，再按普通的局部赋值写回（被写的局部由前端按可变处理，被闭包捕获时装箱）。
    /// mask 的第 i 位是第 i 个实参（不算接收者）。决议：编译期唯一时 codegen 发射 ldloca，否则 ClojureOps.ByRefHost 走反射。
    /// </summary>
    private static Expr ByRefHostCall(object form, Ctx ctx, string kind, Type t, string member, Expr inst, List<object> args)
    {
        if (args.Count > 63) throw Err(form, ctx, "too many arguments for a by-ref host call");
        var nt = ctx.Clear().NonTail();
        long mask = 0;
        var nodes = new List<Expr>();
        var targets = new List<Symbol>();
        for (int i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (!IsByRefArg(a))
            {
                nodes.Add(Analyze(a, nt));
                continue;
            }
            if (RT.count(a) != 2) throw Err(form, ctx, "by-ref takes exactly one argument: " + Str(a));
            var x = RT.second(a);
            var l = x is Symbol xs && xs.Namespace == null ? ctx.FindLocal(xs) : null;
            bool assignable = l != null && (l.Kind is LocalKind.Local or LocalKind.Arg or LocalKind.Catch || l.Kind == LocalKind.Field && l.Mutable);
            if (!assignable)
                throw Err(form, ctx, "by-ref argument must be a local binding (let / loop / fn parameter, or a mutable deftype field), got: " + Str(x));
            mask |= 1L << i;
            targets.Add(l.Name);
            nodes.Add(Analyze(x, nt));
        }
        var operands = inst != null ? new[] { inst }.Concat(nodes) : nodes;
        var call = CljCallOf("ByRefHost", s_fixed4, new Expr[] { Q(kind), Q(t), Q(member), Q(mask) }.Concat(operands));
        var tmp = FreshName("byref");
        var body = targets.Select((n, j) => (Expr)new Set(n, CljCallOf("ByRefNth", s_false, R(tmp), Q((long)(j + 1))))).ToList();
        body.Add(CljCallOf("ByRefNth", s_false, R(tmp), Q(0L)));
        return MkLet1(new[] { tmp }, new[] { call }, MkSeq(body));
    }

    /// <summary>expr-type：提示或局部的 :tag → 类型。</summary>
    private static Type ExprType(Ctx ctx, object d)
    {
        object tag = TagOf(d);
        if (tag == null && d is Symbol s && s.Namespace == null) tag = ctx.FindLocal(s)?.Tag;
        return TagType(tag);
    }

    /// <summary>parse-dot：(. target member args…) 的各部分。</summary>
    private static DotInfo ParseDot(object form, Ctx ctx)
    {
        if (RT.count(form) < 3) throw Err(form, ctx, "Malformed member expression, expecting (. target member ...)");
        var fi = Items(form);
        object td = fi[1], md = fi[2];
        var more = fi.Skip(3).ToList();
        Symbol msym;
        List<object> args;
        bool method;
        if (md is Symbol ms) { msym = ms; args = more; method = false; }
        else if (more.Count == 0 && IsList(md) && RT.first(md) is Symbol ms2) { msym = ms2; args = Items(RT.next(md)); method = true; }
        else throw Err(form, ctx, "Malformed member expression");
        var st = MaybeType(ctx, td);
        var fa = args.Count > 0 ? args[0] : null;
        List<Type> typeArgs = null;
        if (IsList(fa) && SymIs(RT.first(fa), "type-args"))
            typeArgs = Items(RT.next(fa)).Select(t => MaybeType(ctx, t) ?? TagType(t) ?? throw Err(form, ctx, "Unable to resolve classname: " + Str(t))).ToList();
        if (typeArgs != null) args = args.Skip(1).ToList();
        string nm = msym.Name;
        bool prop = !method && nm.Length > 1 && nm[0] == '-';
        string member = MungeStr(prop ? nm.Substring(1) : nm);
        return new DotInfo(st, st == null ? td : null, member, prop, args, typeArgs, method, st ?? HostArgType(ctx, td));
    }

    /// <summary>sp-dot</summary>
    private static Expr SpDot(object form, Ctx ctx)
    {
        var d = ParseDot(form, ctx);
        var nt = ctx.Clear().NonTail();
        var t = d.Type;
        object gen = d.TypeArgs?.Cast<object>().ToArray();
        if (d.Args.Any(IsByRefArg))
        {
            if (d.TypeArgs != null) throw Err(form, ctx, "by-ref arguments are not supported in generic method calls (type-args)");
            if (d.Static != null) return ByRefHostCall(form, ctx, "static", t, d.Member, null, d.Args);
            return ByRefHostCall(form, ctx, "instance", t, d.Member, Analyze(d.Target, nt), d.Args);
        }
        if (d.TypeArgs == null) CheckHostCall(form, ctx, d);
        if (d.Static != null && d.Args.Count == 0 && !d.Method)
            return d.Prop ? CljCallOf("GetStaticField", s_false, Q(t), Q(d.Member)) : CljCallOf("CallStatic", s_fixed3, Q(t), Q(d.Member), Q(gen));
        if (d.Static != null)
            return CljCallOf("CallStatic", s_fixed3, new Expr[] { Q(t), Q(d.Member), Q(gen) }.Concat(HostArgs(form, ctx, d.Args)));
        var x = Analyze(d.Target, nt);
        if (d.Prop && d.Args.Count == 0 && !d.Method) return CljCallOf("GetInstanceField", s_false, Q(t), Q(d.Member), x);
        return CljCallOf("CallInstance", s_fixed4, new Expr[] { Q(t), Q(d.Member), x, Q(gen) }.Concat(HostArgs(form, ctx, d.Args)));
    }

    /// <summary>sp-new</summary>
    private static Expr SpNew(object form, Ctx ctx)
    {
        if (RT.count(form) < 2) throw Err(form, ctx, "wrong number of arguments, expecting: (new Classname args...)");
        var td = RT.second(form);
        var t = MaybeType(ctx, td) ?? throw Err(form, ctx, "Unable to resolve classname: " + Str(td));
        var args = Items(RT.next(RT.next(form)));
        if (args.Any(IsByRefArg)) return ByRefHostCall(form, ctx, "new", t, "", null, args);
        CheckHostCtor(form, ctx, t, args);
        return CljCallOf("NewOf", s_fixed1, new Expr[] { Q(t) }.Concat(HostArgs(form, ctx, args)));
    }

    /// <summary>sp-set!：局部（可变字段）、静态字段、Var、(. x -f) / (.-f x) 字段。</summary>
    private static Expr SpSet(object form, Ctx ctx)
    {
        if (RT.count(form) != 3) throw Err(form, ctx, "Malformed assignment, expecting (set! target val)");
        var fi = Items(form);
        object td = fi[1], val = fi[2];
        Expr FieldSet(Type st, object target, string member)
        {
            var x = target != null ? Analyze(target, ctx.NonTail()) : Q(null);
            var v = Analyze(val, ctx.Clear().NonTail());
            return CljCallOf("SetFieldOf", s_false, Q(st), Q(member), x, v);
        }
        if (td is Symbol ts)
        {
            var l = ts.Namespace == null ? ctx.FindLocal(ts) : null;
            if (l != null)
            {
                if (!l.Mutable) throw Err(form, ctx, "Cannot assign to non-mutable: " + ts);
                var v = Analyze(val, ctx.Clear().NonTail());
                var tmp = FreshName("tmp");
                return MkLet1(new[] { tmp }, new[] { v }, MkSeq(new Expr[] { new Set(l.Name, R(tmp)), R(tmp) }));
            }
            if (StaticMemberOf(ctx, ts) is { } sm) return FieldSet(sm.Type, null, sm.Member);
            var (o, msg, _) = ResolveIn(CurrentNs, ts);
            if (o is Var var) return new CljVarSet(var, Analyze(val, ctx.Clear().NonTail()));
            if (msg != null) throw Err(form, ctx, msg);
            throw Err(form, ctx, "Invalid assignment target");
        }
        if (IsList(td))
        {
            var dot = RewriteToDot(ctx, td, form);
            if (dot == null) throw Err(form, ctx, "Invalid assignment target");
            var d = ParseDot(dot, ctx);
            if (d.Args.Count > 0) throw Err(form, ctx, "Invalid assignment target");
            return FieldSet(d.Static ?? d.Type, d.Target, d.Member);
        }
        throw Err(form, ctx, "Invalid assignment target");
    }
}
