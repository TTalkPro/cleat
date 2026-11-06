using System;
using System.Collections.Generic;
using System.Linq;
using Cleat.Compiler.Ir;
using Cleat.Core.Compiler;
using clojure.lang;

namespace Cleat.Compiler.Driver;

/// <summary>
/// 访问器内联（cleat-145，CLEAT_OPT_ACCINLINE=1 时开，默认关；driver.clj 的 accessors-of / merge-accessors / specialize-accessor-clause /
/// register-eval-accessors!）：deftype 里只读一个字段、或"存一个字段并返回新值"的接口方法记成访问器表；内联子句里整个体就是一次这种
/// 接口调用时，改成"具体类型守卫 + 字段直读 / 直存，失败走原接口调用"。全部在 L0 的数据形式（IrPrinter.ToData，同 np/unparse）上做。
/// </summary>
internal static class Accessors
{
    /// <summary>访问器：类名、字段名、:read / :write。值相等（合并时同一实现的重新定义覆盖，不同实现删掉）。</summary>
    internal sealed record Spec(object Class, object Field, string Kind);

    internal sealed class Table : Dictionary<(string Iface, string Method, int N), Spec> { }

    /// <summary>eval 路径的访问器表（driver.clj eval-accessors）。</summary>
    internal static readonly Table EvalAccessors = new Table();

    private static readonly Symbol QuoteSym = Symbol.intern("quote"), CallSym = Symbol.intern("call"), LetrecSym = Symbol.intern("letrec"),
        LambdaSym = Symbol.intern("lambda"), ClauseSym = Symbol.intern("clause"), BeginSym = Symbol.intern("begin"), SetSym = Symbol.intern("set!"),
        CljCallSym = Symbol.intern("clj-call"), IfSym = Symbol.intern("if");

    private static bool IsSeq(object x) => x is ISeq;
    private static int Count(object x) => x == null ? 0 : RT.count(x);
    private static object Nth(object x, int i) => RT.nth(x, i);
    private static bool Head(object x, Symbol h) => x is ISeq s && Equals(s.first(), h);
    private static List<object> Items(object x) => x == null ? new List<object>() : ((System.Collections.IEnumerable)x).Cast<object>().ToList();

    internal static string InterfaceNameOf(object x)
    {
        string s = x?.ToString() ?? "";
        if (s.StartsWith(",", StringComparison.Ordinal)) return s;
        int c = s.IndexOf(',');
        return c < 0 ? s : s.Substring(0, c).TrimEnd();
    }

    private static object QuotedOf(object x) => Head(x, QuoteSym) && Count(x) == 2 ? Nth(x, 1) : null;

    private static object UntaggedOf(object x) =>
        Head(x, CljCallSym) && Count(x) == 5 && Equals(Nth(x, 1), "Tagged") ? Nth(x, 4) : x;

    /// <summary>deftype 的 CLR 类名 → Type：先按名决议（eval 里 deftype 已求值；包按索引），再取编译期登记的类型；取不到不做守卫内联。</summary>
    private static Type AccessorClassType(object className)
    {
        if (className is not string n) return null;
        try { if (RT.classForName(n) is Type t) return t; } catch (Exception) { }
        try { return CompileTimeTypes.Get(n); } catch (Exception) { return null; }
    }

    /// <summary>剥掉 deftype 方法体的 named-let 外壳，返回 (形参, 真正的方法体)；形状不合 null。</summary>
    private static (object Ys, object Body)? MethodRealBody(object body)
    {
        if (!Head(body, CallSym) || Count(body) < 2) return null;
        var callee = Nth(body, 1);
        int inits = Count(body) - 2;
        if (!Head(callee, LetrecSym) || Count(callee) != 3) return null;
        var bs = Nth(callee, 1);
        var lp = Nth(callee, 2);
        if (bs is not Sequential || Count(bs) != 1) return null;
        var binding = Nth(bs, 0);
        if (binding is not Sequential || Count(binding) != 2 || !Equals(Nth(binding, 0), lp)) return null;
        var lam = Nth(binding, 1);
        if (!Head(lam, LambdaSym) || Count(lam) != 3) return null;
        var cl2 = Nth(lam, 2);
        if (!Head(cl2, ClauseSym) || Count(cl2) != 4 || Nth(cl2, 1) is not Sequential || Count(Nth(cl2, 1)) != inits) return null;
        return (Nth(cl2, 1), Nth(cl2, 3));
    }

    private static int? ReadAccessorField(object body, Dictionary<object, int> fieldNames) =>
        body is Symbol s && fieldNames.TryGetValue(s, out int i) ? i : null;

    /// <summary>方法体是"把形参 v 经一层临时绑定存进字段 f、返回同一个 v"：返回字段位置。</summary>
    private static int? SetAccessorField(object body, object ys, Dictionary<object, int> fieldNames)
    {
        if (!Head(body, BeginSym) || Count(body) != 3) return null;
        var wrapper = Nth(body, 1);
        var result = Nth(body, 2);
        if (!Head(wrapper, CallSym) || Count(wrapper) != 3) return null;
        var lambdaForm = Nth(wrapper, 1);
        var value = Nth(wrapper, 2);
        if (!Head(lambdaForm, LambdaSym) || Count(lambdaForm) != 3) return null;
        var clause = Nth(lambdaForm, 2);
        if (!Head(clause, ClauseSym) || Count(clause) != 4) return null;
        var temps = Nth(clause, 1);
        var inner = Nth(clause, 3);
        if (!Head(inner, BeginSym) || Count(inner) != 3) return null;
        var assignment = Nth(inner, 1);
        var returned = Nth(inner, 2);
        if (!Head(assignment, SetSym) || Count(assignment) != 3) return null;
        var target = Nth(assignment, 1);
        var tmp = Nth(assignment, 2);
        if (Count(temps) == 1 && value is Symbol && result is Symbol && tmp is Symbol && target is Symbol
            && Equals(result, value) && Items(ys).Any(y => Equals(y, value)) && Equals(returned, tmp) && Equals(tmp, Nth(temps, 0))
            && fieldNames.TryGetValue(target, out int i))
            return i;
        return null;
    }

    /// <summary>deftype 的数据形式 → 访问器表（driver.clj accessors-of）。</summary>
    internal static Table AccessorsOf(object sx)
    {
        var acc = new Table();
        var all = Items(sx);
        if (all.Count < 3) return acc;
        var info = all[1];
        var className = Nth(info, 0);
        var fieldDescs = Items(Nth(info, 2));
        var fields = Items(all[2]);
        var fieldNames = new Dictionary<object, int>();
        for (int k = 0; k < fields.Count; k++) fieldNames[fields[k]] = k;
        object ByIndex(int i) => Nth(fieldDescs[i], 0);
        var ms = Items(Nth(info, 4));
        var ls = all.Skip(3).ToList();
        for (int k = 0; k < ms.Count && k < ls.Count; k++)
        {
            var m = ms[k];
            object iface = Nth(m, 0), method = Nth(m, 1), ptypes = Nth(m, 3);
            var lambdaForm = ls[k];
            var clause = Head(lambdaForm, LambdaSym) && Count(lambdaForm) == 3 ? Nth(lambdaForm, 2) : null;
            var mb = Head(clause, ClauseSym) && Count(clause) == 4 ? MethodRealBody(Nth(clause, 3)) : null;
            int n = Count(ptypes);
            Spec spec = null;
            if (iface != null && mb is { } rb && rb.Body != null)
            {
                if (n == 0 && ReadAccessorField(rb.Body, fieldNames) is int fi) spec = new Spec(className, ByIndex(fi), "read");
                else if (n == 1 && SetAccessorField(rb.Body, rb.Ys, fieldNames) is int si)
                {
                    // 可变且非 volatile：直存用普通 stfld，volatile 字段需要 volatile. 前缀，不做特化
                    var d = fieldDescs[si];
                    if (Nth(d, 1) is true && Nth(d, 2) is not true) spec = new Spec(className, ByIndex(si), "write");
                }
            }
            if (iface != null && spec != null) acc[(InterfaceNameOf(iface), method?.ToString(), n)] = spec;
        }
        return acc;
    }

    /// <summary>合并（就地改 m）：同一接口方法有不同实现时删掉该项，相同实现覆盖。</summary>
    internal static void Merge(Table m, Table add)
    {
        foreach (var (k, v) in add)
        {
            if (m.TryGetValue(k, out var old)) { if (!old.Equals(v)) m.Remove(k); }
            else m[k] = v;
        }
    }

    /// <summary>L0 里全部 (clj-deftype …)（信息的第 2 项为 false：不是 reify / 记录桩）的访问器，合并进 into。</summary>
    internal static void Collect(Expr l0, Table into)
    {
        var w = new DeftypeWalker(into);
        w.Walk(l0);
    }

    private sealed class DeftypeWalker : Walker
    {
        private readonly Table _into;
        public DeftypeWalker(Table into) => _into = into;

        public override void VisitCljDeftype(CljDeftype n)
        {
            if (Nth(n.Datum, 1) is false) Merge(_into, AccessorsOf(IrPrinter.ToData(n)));
            base.VisitCljDeftype(n);
        }
    }

    /// <summary>eval 的顶层 L0：更新 eval 访问器表（driver.clj register-eval-accessors!）；默认关。</summary>
    internal static void RegisterEval(Expr l0)
    {
        if (!Analyzer.FormAnalyzer.OptAccinline) return;
        var found = new Table();
        Collect(l0, found);
        Merge(EvalAccessors, found);
    }

    /// <summary>剥一层"外层是纯转发实参的命名 let"：(内层形参, 外层表达式) 的改名对 + 里面的体；不合 null。</summary>
    private static (List<(object Y, object X)> Pairs, object Body)? PeelNamedLet(object x)
    {
        if (!Head(x, CallSym) || Count(x) < 2) return null;
        var callee = Nth(x, 1);
        var inits = Items(x).Skip(2).ToList();
        if (!Head(callee, LetrecSym) || Count(callee) != 3) return null;
        var bs = Nth(callee, 1);
        var lp = Nth(callee, 2);
        if (bs is not Sequential || Count(bs) != 1) return null;
        var pair = Nth(bs, 0);
        if (pair is not Sequential || Count(pair) != 2 || !Equals(Nth(pair, 0), lp)) return null;
        var lam = Nth(pair, 1);
        if (!Head(lam, LambdaSym) || Count(lam) != 3) return null;
        var cl2 = Nth(lam, 2);
        if (!Head(cl2, ClauseSym) || Count(cl2) != 4 || Nth(cl2, 1) is not Sequential || Count(Nth(cl2, 1)) != inits.Count) return null;
        var ys = Items(Nth(cl2, 1));
        return (ys.Select((y, i) => (y, inits[i])).ToList(), Nth(cl2, 3));
    }

    /// <summary>纯接口访问的包装子句 → "具体类型守卫 + 字段直读 / 直存，失败走原接口调用"；形状不合、类型决议不出、任何异常都原样返回。</summary>
    internal static object SpecializeClause(object cl, Table accessors)
    {
        try
        {
            var top = new HashSet<object>(Items(Nth(cl, 1)));
            object body = Nth(cl, 3);
            var ren = new Dictionary<object, object>();
            while (PeelNamedLet(body) is { } peeled)
            {
                foreach (var (y, i) in peeled.Pairs)
                    if (top.Contains(i) || ren.ContainsKey(i)) ren[y] = i;
                body = peeled.Body;
            }
            // 逐层改名可能多跳：一路走到本子句的形参才算"同一个表达式、求值一次"
            object Resolve(object s)
            {
                var seen = new HashSet<object>();
                for (var x = s; ;)
                {
                    if (top.Contains(x)) return x;
                    if (x is not Symbol || !seen.Add(x)) return null;
                    if (!ren.TryGetValue(x, out var r)) return null;
                    x = r;
                }
            }
            bool isCall = Head(body, CljCallSym) && Equals(Nth(body, 1), "CallInstance") && Util.equiv(Nth(body, 2), 4L) && Count(body) >= 7;
            if (!isCall) return cl;
            var iface = QuotedOf(Nth(body, 3));
            var method = QuotedOf(Nth(body, 4));
            var obj = UntaggedOf(Nth(body, 5));
            var args = Items(body).Skip(7).Select(UntaggedOf).ToList();
            var o = obj is Symbol ? Resolve(obj) : null;
            if (o == null || !args.All(a => a is Symbol)) return cl;
            var vs = args.Select(Resolve).ToList();
            if (vs.Any(v => v == null)) return cl;
            int n = vs.Count;
            if (iface == null || method == null || !accessors.TryGetValue((InterfaceNameOf(iface), method.ToString(), n), out var spec)) return cl;
            var ty = AccessorClassType(spec.Class);
            if (ty == null) return cl;
            object concrete = RT.list(QuoteSym, ty), field = RT.list(QuoteSym, spec.Field);
            object direct = spec.Kind switch
            {
                "read" when n == 0 => PersistentList.create(new object[] { CljCallSym, "GetInstanceFieldDirect", false, concrete, field, o }),
                "write" when n == 1 => PersistentList.create(new object[] { CljCallSym, "SetInstanceField", false, concrete, field, o, vs[0] }),
                _ => null,
            };
            if (direct == null) return cl;
            var items = Items(cl);
            items[3] = RT.list(IfSym, PersistentList.create(new object[] { CljCallSym, "IsInstanceOf", false, concrete, o }), direct, Nth(cl, 3));
            return PersistentList.create(items);
        }
        catch (Exception) { return cl; }
    }
}
