using System;
using System.Diagnostics;
using Cleat.Scheme;

namespace Cleat.Stage0;

// 解释器的执行结构：核心形式预分析成 Node 树（Analyzer.cs），执行时不再查名字。
// 词法变量存放在 Frame 链中，按（层数, 下标）访问；全局变量即符号的值槽。
//
// 尾调用：尾位置上对解释器闭包（以及 apply）的调用不直接执行，而是返回 TailCall，
// 由 Closure.Execute 的循环接着执行，所以尾递归不增长 C# 栈。对原语等其他过程直接调用。

public sealed class Frame
{
    public readonly object[] S;
    public readonly Frame P;
    public Frame(object[] slots, Frame parent) { S = slots; P = parent; }
}

public sealed class TailCall
{
    public readonly Procedure P;
    public readonly object[] A;
    public TailCall(Procedure p, object[] a) { P = p; A = a; }
}

public abstract class Node
{
    /// <summary>求值。只有分析时标为尾位置的应用节点才可能返回 <see cref="TailCall"/>。</summary>
    public abstract object Eval(Frame f);
}

internal sealed class Const : Node
{
    private readonly object _v;
    public Const(object v) { _v = v; }
    public override object Eval(Frame f) => _v;
}

internal sealed class LocalRef0 : Node
{
    private readonly int _i;
    public LocalRef0(int i) { _i = i; }
    public override object Eval(Frame f) => f.S[_i];
}

internal sealed class LocalRef1 : Node
{
    private readonly int _i;
    public LocalRef1(int i) { _i = i; }
    public override object Eval(Frame f) => f.P.S[_i];
}

internal sealed class LocalRef : Node
{
    private readonly int _d, _i;
    public LocalRef(int d, int i) { _d = d; _i = i; }
    public override object Eval(Frame f)
    {
        for (int k = _d; k > 0; k--) f = f.P;
        return f.S[_i];
    }
}

/// <summary>letrec 绑定的变量：初始化之前读取报 &amp;assertion（R6RS letrec 限制）。</summary>
internal sealed class LetrecRef : Node
{
    private readonly int _d, _i;
    private readonly Symbol _name;
    public LetrecRef(int d, int i, Symbol name) { _d = d; _i = i; _name = name; }
    public override object Eval(Frame f)
    {
        for (int k = _d; k > 0; k--) f = f.P;
        object v = f.S[_i];
        return v is Uninitialized ? Errors.AssertionViolation(_name.Ungensym().Name, "attempt to read uninitialized variable") : v;
    }
}

internal sealed class LocalSet : Node
{
    private readonly int _d, _i;
    private readonly Node _value;
    public LocalSet(int d, int i, Node value) { _d = d; _i = i; _value = value; }
    public override object Eval(Frame f)
    {
        object v = _value.Eval(f);
        for (int k = _d; k > 0; k--) f = f.P;
        f.S[_i] = v;
        return Unspecified.Instance;
    }
}

internal sealed class GlobalRef : Node
{
    private readonly Symbol _s;
    public GlobalRef(Symbol s) { _s = s; }
    public override object Eval(Frame f)
    {
        object v = _s.Value;
        return v is Unbound ? Errors.UndefinedError(_s) : v;
    }
}

internal sealed class GlobalSet : Node
{
    private readonly Symbol _s;
    private readonly Node _value;
    public GlobalSet(Symbol s, Node value) { _s = s; _value = value; }
    public override object Eval(Frame f)
    {
        _s.Value = _value.Eval(f);
        return Unspecified.Instance;
    }
}

internal sealed class If : Node
{
    private readonly Node _test, _then, _els;
    public If(Node test, Node then, Node els) { _test = test; _then = then; _els = els; }
    public override object Eval(Frame f) =>
      RT.IsTrue(_test.Eval(f)) ? _then.Eval(f) : _els == null ? Unspecified.Instance : _els.Eval(f);
}

internal sealed class Seq : Node
{
    private readonly Node[] _body;
    public Seq(Node[] body) { _body = body; }
    public override object Eval(Frame f)
    {
        int last = _body.Length - 1;
        for (int i = 0; i < last; i++) _body[i].Eval(f);
        return _body[last].Eval(f);
    }
}

/// <summary>letrec / letrec*：新建一层，按顺序求值并赋值（letrec 按 letrec* 处理）。</summary>
internal sealed class Letrec : Node
{
    private readonly Node[] _inits;
    private readonly Node _body;
    public Letrec(Node[] inits, Node body) { _inits = inits; _body = body; }
    public override object Eval(Frame f)
    {
        var slots = new object[_inits.Length];
        for (int i = 0; i < slots.Length; i++) slots[i] = Uninitialized.Instance;
        var nf = new Frame(slots, f);
        for (int i = 0; i < _inits.Length; i++) slots[i] = _inits[i].Eval(nf);
        return _body.Eval(nf);
    }
}

/// <summary>
/// (library-letrec* name ((x loc e) …) body)：同 letrec*，每个值求出后同时写入对外位置 loc 的值槽
/// （程序的 name 为 '()，不写位置；与旧 LibraryLetrecStarGenerator 一致）。
/// </summary>
public sealed class LibraryLetrec : Node
{
    private readonly Node[] _inits;
    private readonly Symbol[] _locs;
    private readonly Node _body;
    private Frame _prelinked;

    public LibraryLetrec(Node[] inits, Symbol[] locs, Node body) { _inits = inits; _locs = locs; _body = body; }

    /// <summary>
    /// 预链接（只用于种子这样的顶层库序列）：先建好库的帧，创建全部库级过程并写入位置，之后 Eval 只求其余的值。
    /// 旧 boot 编译器把库级过程静态链接，种子里因此有跨库的前向引用（如 exceptions 初始化时调用 compat 的
    /// make-parameter），逐个执行库体会遇到未绑定的位置。
    /// </summary>
    public void Prelink()
    {
        var slots = new object[_inits.Length];
        for (int i = 0; i < slots.Length; i++) slots[i] = Uninitialized.Instance;
        _prelinked = new Frame(slots, null);
        for (int i = 0; i < _inits.Length; i++)
        {
            if (!(_inits[i] is Lambda)) continue;
            slots[i] = _inits[i].Eval(_prelinked);
            if (_locs != null) _locs[i].Value = slots[i];
        }
    }

    public override object Eval(Frame f)
    {
        var nf = _prelinked;
        bool skipLambdas = nf != null;
        if (nf == null)
        {
            var fresh = new object[_inits.Length];
            for (int i = 0; i < fresh.Length; i++) fresh[i] = Uninitialized.Instance;
            nf = new Frame(fresh, f);
        }
        var slots = nf.S;
        for (int i = 0; i < _inits.Length; i++)
        {
            if (skipLambdas && _inits[i] is Lambda) continue;
            object v = _inits[i].Eval(nf);
            slots[i] = v;
            if (_locs != null) _locs[i].Value = v;
        }
        return _body.Eval(nf);
    }
}

internal sealed class Lambda : Node
{
    public readonly LambdaInfo Info;
    public Lambda(LambdaInfo info) { Info = info; }
    public override object Eval(Frame f) => new Closure(Info, f);
}

internal sealed class App : Node
{
    private readonly Node _fn;
    private readonly Node[] _args;
    private readonly bool _tail;
    public App(Node fn, Node[] args, bool tail) { _fn = fn; _args = args; _tail = tail; }

    public override object Eval(Frame f)
    {
        object p = _fn.Eval(f);
        var a = new object[_args.Length];
        for (int i = 0; i < a.Length; i++) a[i] = _args[i].Eval(f);
        if (!(p is Procedure proc)) return Errors.AssertionViolation("apply", "not a procedure", p);
        if (_tail && (proc is Closure || ReferenceEquals(proc, Closure.ApplyPrimitive))) return new TailCall(proc, a);
        return Closure.Call(proc, a);
    }
}

// —— 原语表中的 special 条目：作用于未求值的参数（iron-012）——

internal sealed class And : Node
{
    private readonly Node[] _args;
    public And(Node[] args) { _args = args; }
    public override object Eval(Frame f)
    {
        foreach (var a in _args) if (!RT.IsTrue(a.Eval(f))) return RT.False;
        return RT.True;
    }
}

internal sealed class Or : Node
{
    private readonly Node[] _args;
    public Or(Node[] args) { _args = args; }
    public override object Eval(Frame f)
    {
        foreach (var a in _args) if (RT.IsTrue(a.Eval(f))) return RT.True;
        return RT.False;
    }
}

internal sealed class Throw : Node
{
    private readonly Node _arg;
    public Throw(Node arg) { _arg = arg; }
    public override object Eval(Frame f)
    {
        object e = _arg.Eval(f);
        if (e is Exception ex) throw ex;
        return Errors.AssertionViolation("$throw", "not an exception", e);
    }
}

internal sealed class Break : Node
{
    public override object Eval(Frame f)
    {
        if (Debugger.IsAttached) Debugger.Break();
        return Unspecified.Instance;
    }
}

/// <summary>($try/finally body handler)：两者都不在尾位置。</summary>
internal sealed class TryFinally : Node
{
    private readonly Node _body, _handler;
    public TryFinally(Node body, Node handler) { _body = body; _handler = handler; }
    public override object Eval(Frame f)
    {
        try { return _body.Eval(f); }
        finally { _handler.Eval(f); }
    }
}

internal sealed class Unsupported : Node
{
    private readonly string _what;
    public Unsupported(string what) { _what = what; }
    public override object Eval(Frame f) => Errors.AssertionViolation(_what, "not supported by stage0");
}

/// <summary>出错时的过程调用链（由内向外），只在异常筛选阶段记录，正常执行没有开销。</summary>
public static class Backtrace
{
    [ThreadStatic] private static Exception s_current;
    [ThreadStatic] private static System.Collections.Generic.List<string> s_names;

    public static bool Note(Exception e, Closure c)
    {
        if (!ReferenceEquals(e, s_current)) { s_current = e; s_names = new System.Collections.Generic.List<string>(); }
        if (s_names.Count < 40) s_names.Add(c.Name);
        return false;
    }

    public static System.Collections.Generic.List<string> For(Exception e) =>
      ReferenceEquals(e, s_current) ? s_names : new System.Collections.Generic.List<string>();
}

/// <summary>case-lambda 的一个子句。</summary>
public sealed class Clause
{
    public int Required;     // 必需参数个数
    public bool Rest;        // 有剩余参数
    public Node Body;
}

public sealed class LambdaInfo
{
    public Clause[] Clauses;
    public string Name;
}

/// <summary>解释器生成的过程。与原语、编译后代码遵守同一 Procedure 调用约定。</summary>
public sealed class Closure : Procedure
{
    /// <summary>apply 原语：尾位置上的 (apply f … lst) 在蹦床中展开，保证经 apply 的尾调用也不增长栈。</summary>
    public static Procedure ApplyPrimitive;

    private readonly LambdaInfo _info;
    private readonly Frame _env;

    public Closure(LambdaInfo info, Frame env) { _info = info; _env = env; }

    public override string Name => _info.Name ?? "lambda";

    /// <summary>procedure-arity（与旧系统一致）：固定参数子句为 n，有剩余参数的为 n.0；多个子句为列表，没有子句为 '()。</summary>
    public override object Arity
    {
        get
        {
            var a = Array.ConvertAll(_info.Clauses, c => c.Rest ? (object)(double)c.Required : RT.Box(c.Required));
            return a.Length == 0 ? null : a.Length == 1 ? a[0] : Cons.FromArray(a);
        }
    }

    public override object Invoke0() => Execute(Array.Empty<object>());
    public override object Invoke1(object a1) => Execute(new[] { a1 });
    public override object Invoke2(object a1, object a2) => Execute(new[] { a1, a2 });
    public override object Invoke3(object a1, object a2, object a3) => Execute(new[] { a1, a2, a3 });
    public override object Invoke4(object a1, object a2, object a3, object a4) => Execute(new[] { a1, a2, a3, a4 });
    public override object InvokeN(object[] args) => Execute(args);

    public static object Call(Procedure p, object[] a)
    {
        if (p is Closure c) return c.Execute(a);
        switch (a.Length)
        {
            case 0: return p.Invoke0();
            case 1: return p.Invoke1(a[0]);
            case 2: return p.Invoke2(a[0], a[1]);
            case 3: return p.Invoke3(a[0], a[1], a[2]);
            case 4: return p.Invoke4(a[0], a[1], a[2], a[3]);
            default: return p.InvokeN(a);
        }
    }

    /// <summary>蹦床：执行本过程；尾调用返回的 TailCall 在此循环中继续，直到得到值。</summary>
    public object Execute(object[] args)
    {
        Closure c = this;
        try
        {
            return Run(ref c, args);
        }
        catch (Exception e) when (Backtrace.Note(e, c))
        {
            throw;   // 不会执行：Note 总是返回 false，只在异常第一遍扫描时记录调用链
        }
    }

    private object Run(ref Closure c, object[] args)
    {
        while (true)
        {
            object r = c.Enter(args);
            if (!(r is TailCall tc)) return r;
            var p = tc.P;
            args = tc.A;
            while (ReferenceEquals(p, ApplyPrimitive))
            {
                if (args.Length == 0 || !(args[0] is Procedure f)) return p.InvokeN(args);
                args = SpreadApplyArgs(args);
                p = f;
            }
            if (p is Closure next) { c = next; continue; }
            return Call(p, args);
        }
    }

    // (apply f a … lst) 的参数 → f 的参数
    private static object[] SpreadApplyArgs(object[] args)
    {
        int fixedCount = args.Length - 2;
        var list = new System.Collections.Generic.List<object>();
        for (int i = 1; i <= fixedCount; i++) list.Add(args[i]);
        if (args.Length >= 2)
        {
            object l = args[args.Length - 1];
            while (l is Cons c) { list.Add(c.car); l = c.cdr; }
            if (l != null) Errors.AssertionViolation("apply", "not a proper list", args[args.Length - 1]);
        }
        return list.ToArray();
    }

    private object Enter(object[] args)
    {
        foreach (var cl in _info.Clauses)
        {
            if (!cl.Rest)
            {
                if (args.Length == cl.Required) return cl.Body.Eval(new Frame(args, _env));
            }
            else if (args.Length >= cl.Required)
            {
                var slots = new object[cl.Required + 1];
                Array.Copy(args, slots, cl.Required);
                object rest = null;
                for (int i = args.Length - 1; i >= cl.Required; i--) rest = new Cons(args[i], rest);
                slots[cl.Required] = rest;
                return cl.Body.Eval(new Frame(slots, _env));
            }
        }
        return ArityError(args.Length);
    }
}
