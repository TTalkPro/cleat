using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Cleat.Compiler.Ir;
using Cleat.Core.Runtime;
using clojure.lang;
using Ref = Cleat.Compiler.Ir.Ref;

namespace Cleat.Compiler.Driver;

/// <summary>
/// 简单形式的解释（driver.clj 的 simple? / atomic? / seval，原版 Driver.TryEvalSimple，designs/04 §9：Chez 的 compile-interpret-simple）：
/// 不创建函数 / 类型的形式（常量、局部变量、Var 的读写与 def、调用、ClojureOps 的 clj-call、关键字调用、let、if、begin）直接在 L0 上解释，
/// 不生成内存程序集。先整体检查（<see cref="Simple"/>），通过才求值。
/// </summary>
internal static class SimpleEval
{
    private static readonly ConcurrentDictionary<(string, int, int), MethodInfo> s_opsMethods = new ConcurrentDictionary<(string, int, int), MethodInfo>();

    /// <summary>clj-call 的目标：ClojureOps 的公有静态方法（固定个数时最后一个参数是 object[]）；没有时 null（driver.clj ops-method）。</summary>
    private static MethodInfo OpsMethod(object nm, object fixedArg, int argc)
    {
        if (nm is not string name) return null;
        int fixedN;
        if (fixedArg is false) fixedN = -1;
        else if (fixedArg is long l) fixedN = (int)l;
        else if (fixedArg is int i) fixedN = i;
        else return null;
        if (fixedN > argc) return null;
        int pc = fixedN < 0 ? argc : fixedN + 1;
        return s_opsMethods.GetOrAdd((name, pc, fixedN), k =>
            typeof(ClojureOps).GetMethods(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(m =>
            {
                var ps = m.GetParameters();
                return m.Name == k.Item1 && ps.Length == k.Item2 && (k.Item3 < 0 || ps[k.Item2 - 1].ParameterType == typeof(object[]));
            }));
    }

    /// <summary>立即调用的单子句、无剩余参数的 lambda（let）：形参与体，否则 null（driver.clj let-head）。</summary>
    private static Clause LetHead(Expr e) =>
        e is Lambda { Clauses.Length: 1 } lam && lam.Clauses[0].Rest == null ? lam.Clauses[0] : null;

    /// <summary>
    /// 调用的实参、let 的初值：常量、局部变量、Var 读、实参都是原子的运行时操作。嵌套的调用不解释——解释器把实参值放在数组 / 环境里直到调用返回，
    /// 惰性序列的头就被抓住（编译后的代码按局部变量清除，cleat-117）。
    /// </summary>
    private static bool Atomic(Expr x, HashSet<Symbol> scope) => x switch
    {
        Ref r => scope.Contains(r.Name),
        Quote => true,
        CljVarRef v => v.Var is Var,
        CljCall cc => !Equals(cc.Op, "KnownCall") && Simple(cc, scope) && cc.Args.All(a => Atomic(a, scope)),
        _ => false,
    };

    internal static bool Simple(Expr x, HashSet<Symbol> scope)
    {
        switch (x)
        {
            case Ref r: return scope.Contains(r.Name);
            case Quote: return true;
            case If i: return Simple(i.Test, scope) && Simple(i.Then, scope) && Simple(i.Else, scope);
            case Begin b: return b.Body.All(e => Simple(e, scope));
            case CljVarRef v: return v.Var is Var;
            case CljVarSet vs: return vs.Var is Var && Simple(vs.Value, scope);
            case CljDef d: return d.Datum is Var && Simple(d.Expr0, scope) && Simple(d.Expr1, scope);
            case CljInvoke ci: return Atomic(ci.Fn, scope) && ci.Args.All(a => Atomic(a, scope));
            case CljKeywordInvoke k: return k.Keyword is Keyword && k.Args.Length <= 1 && Atomic(k.Target, scope) && k.Args.All(a => Atomic(a, scope));
            case CljCall cc:
                // 用户函数的已知调用（cleat-110）：解释时就是 IFn 调用
                if (Equals(cc.Op, "KnownCall")) return cc.Args.Skip(1).All(a => Atomic(a, scope));
                return OpsMethod(cc.Op, cc.Datum, cc.Args.Length) != null && cc.Args.All(a => Atomic(a, scope));
            case Call c:
                {
                    var cl = LetHead(c.Fn);
                    if (cl == null || cl.Params.Length != c.Args.Length || !c.Args.All(a => Atomic(a, scope))) return false;
                    var inner = new HashSet<Symbol>(scope);
                    inner.UnionWith(cl.Params);
                    return Simple(cl.Body, inner);
                }
            default: return false;
        }
    }

    private static object Unwrap(MethodInfo m, object[] args)
    {
        try { return m.Invoke(null, args); }
        catch (TargetInvocationException e) when (e.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }
    }

    internal static object Eval(Expr x, IReadOnlyDictionary<Symbol, object> env)
    {
        switch (x)
        {
            case Ref r:
                return env.TryGetValue(r.Name, out var val) ? val : throw new InvalidOperationException("simple eval: unbound local " + r.Name);
            case Quote q: return q.Datum;
            case If i: return Truth.IsTrue(Eval(i.Test, env)) ? Eval(i.Then, env) : Eval(i.Else, env);
            case Begin b:
                {
                    object last = null;
                    foreach (var e in b.Body) last = Eval(e, env);
                    return last;
                }
            case CljVarRef v: return v.Flag ? ((Var)v.Var).get() : ((Var)v.Var).getRawRoot();
            case CljVarSet vs: return ((Var)vs.Var).set(Eval(vs.Value, env));
            case CljDef d:
                {
                    // DefExpr（与 codegen 的 clj-def 相同的顺序）：setDynamic、bindRoot（有初值时）、setMeta；值为 Var
                    var v = (Var)d.Datum;
                    if (d.Flag1) v.setDynamic();
                    if (d.Flag0) v.bindRoot(Eval(d.Expr0, env));
                    v.setMeta((IPersistentMap)Eval(d.Expr1, env));
                    return v;
                }
            case CljInvoke ci:
                {
                    var f = Eval(ci.Fn, env);
                    var args = ci.Args.Select(a => Eval(a, env)).ToArray();
                    return ((IFn)f).applyTo(RT.seq(args));
                }
            case CljKeywordInvoke k:
                {
                    var target = Eval(k.Target, env);
                    return k.Args.Length == 0 ? RT.get(target, k.Keyword) : RT.get(target, k.Keyword, Eval(k.Args[0], env));
                }
            case CljCall cc:
                {
                    if (Equals(cc.Op, "KnownCall"))
                    {
                        var f = Eval(cc.Args[1], env);
                        var args = cc.Args.Skip(2).Select(a => Eval(a, env)).ToArray();
                        return ((IFn)f).applyTo(RT.seq(args));
                    }
                    var m = OpsMethod(cc.Op, cc.Datum, cc.Args.Length);
                    var vals = cc.Args.Select(a => Eval(a, env)).ToArray();
                    object[] margs;
                    if (cc.Datum is false) margs = vals;
                    else
                    {
                        int n = Convert.ToInt32(cc.Datum);
                        margs = new object[n + 1];
                        Array.Copy(vals, margs, n);
                        margs[n] = vals.Skip(n).ToArray();
                    }
                    return Unwrap(m, margs);
                }
            case Call c:
                {
                    var cl = LetHead(c.Fn);
                    var inner = new Dictionary<Symbol, object>(env.Count + cl.Params.Length);
                    foreach (var kv in env) inner[kv.Key] = kv.Value;
                    var vals = c.Args.Select(a => Eval(a, env)).ToArray();
                    for (int j = 0; j < vals.Length; j++) inner[cl.Params[j]] = vals[j];
                    return Eval(cl.Body, inner);
                }
            default: throw new InvalidOperationException("simple eval: not a simple form " + x.Kind);
        }
    }
}
