using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Cleat.Compiler.Ir;
using Cleat.Core.Runtime;
using clojure.lang;
using Symbol = clojure.lang.Symbol;
using Ref = Cleat.Compiler.Ir.Ref;
using Box = Cleat.Compiler.Ir.Box;
using Label = Cleat.Compiler.Ir.Label;

namespace Cleat.Compiler.Lower;

/// <summary>结果的使用方式（codegen.clj 的 ctx）。</summary>
internal enum Ctx { Value, Effect, Test, Tail }

/// <summary>
/// 变量与值的原始表示（designs/30 §3.3 的 L4 表示；codegen.clj 的 :l64 :fl :fx :num）。None 是 object。
/// Fx 只来自 Int32 常量（读入器给的元数据），Num 是未定类型的数（cleat-102，<see cref="Cleat.Core.Runtime.Num"/>）。
/// </summary>
internal enum Raw { None, L64, Fl, Fx, Num }

/// <summary>
/// 变量位置的种类（codegen.clj var-location 的向量头）：Arg / Local 是 object；TArg / TLocal 是原始类型的形参 / 局部变量（:targ / :tlocal）；
/// NumArg 是推断为 Num 的 object 形参（:numarg：装箱用途读原来的实参，原始用途读入口转换出的 Num 局部变量，cleat-102）。
/// </summary>
internal enum LocKind { Arg, Local, TArg, TLocal, NumArg, Field, DField, Self }

/// <summary>
/// 变量的位置（codegen.clj var-location）。Index 是形参 / 局部变量的下标；NumArg 时 NumLocal 是 Num 局部变量的下标。
/// 相等按值（codegen.clj 的位置是向量，jump 的 keep 集按值比较存储位置）。
/// </summary>
internal sealed class Loc : IEquatable<Loc>
{
    public Loc(LocKind kind, int index = 0, Raw raw = Raw.None, FieldDecl field = null, int numLocal = -1)
    {
        Kind = kind;
        Index = index;
        Raw = raw;
        Field = field;
        NumLocal = numLocal;
    }

    public LocKind Kind { get; }
    public int Index { get; }
    public Raw Raw { get; }
    public FieldDecl Field { get; }
    public int NumLocal { get; }

    public bool Equals(Loc o) => o is not null && Kind == o.Kind && Index == o.Index && Raw == o.Raw && ReferenceEquals(Field, o.Field) && NumLocal == o.NumLocal;
    public override bool Equals(object o) => Equals(o as Loc);
    public override int GetHashCode() => HashCode.Combine(Kind, Index, Raw, Field, NumLocal);
}

/// <summary>编译单元的生成状态（codegen.clj make-unit）。</summary>
internal sealed class UnitState
{
    public UnitState(UnitDecl decl) { Decl = decl; }

    public UnitDecl Decl { get; }
    public readonly HashSet<string> TypeNames = new HashSet<string>(StringComparer.Ordinal);
    private readonly Dictionary<object, (int Index, object Datum)> _constIndex = new Dictionary<object, (int, object)>(new ConstComparer());

    /// <summary>eval 单元里 Var 的 static readonly 字段（cleat-114）：Var（按身份）→ 字段。</summary>
    public readonly List<(Var Var, FieldDecl Field)> VarFields = new List<(Var, FieldDecl)>();
    public ClassDecl VarsClass;

    private sealed class ConstComparer : IEqualityComparer<object>
    {
        public new bool Equals(object a, object b) => object.Equals(a, b);
        public int GetHashCode(object o) => o?.GetHashCode() ?? 0;
    }

    private static bool NegZero(object d) => d is double x && x == 0 && double.IsNegative(x);

    /// <summary>数、字符、字符串、关键字、布尔按值合并；其余只有同一个对象才共用下标（codegen.clj constant-index!）。</summary>
    private static bool EqualsDedupable(object d) => d is long or int or double or decimal or float or short or byte or sbyte or BigInt or Ratio or BigDecimal or System.Numerics.BigInteger
                                                     or char or string or Keyword or bool;

    public int ConstantIndex(object d)
    {
        bool neg = NegZero(d);
        (int Index, object Datum) hit = default;
        bool found = !neg && d != null && _constIndex.TryGetValue(d, out hit);
        if (found && (ReferenceEquals(hit.Datum, d) || EqualsDedupable(d))) return hit.Index;
        int k = Decl.Constants.Count;
        if (!neg && !found && d != null) _constIndex[d] = (k, d);
        Decl.Constants.Add(d);
        return k;
    }
}

/// <summary>方法上下文（codegen.clj make-method-ctx）。</summary>
internal sealed class MethodCtx
{
    public MethodCtx(UnitState unit, ClassDecl cls, bool isStatic)
    {
        Unit = unit;
        Class = cls;
        Static = isStatic;
        Source = CodeGen.UnitSource();
    }

    public UnitState Unit { get; }
    public ClassDecl Class { get; }
    public bool Static { get; }
    public readonly Dictionary<Symbol, Loc> Vars = new Dictionary<Symbol, Loc>();
    public readonly List<object> Locals = new List<object>();
    public List<Ins> Buffer = new List<Ins>();
    public int TryDepth;
    public string Source;
    public (string File, int Line)? LastSrc;
    /// <summary>循环标签：入口、形参、分层方法里尾位置循环的 keep 集（循环体引用的体外变量；null 表示不清，cleat-117）。</summary>
    public readonly Dictionary<Symbol, (Lbl Label, ImmutableArray<Symbol> Params, HashSet<Symbol> Keep)> Labels = new Dictionary<Symbol, (Lbl, ImmutableArray<Symbol>, HashSet<Symbol>)>();
    public Raw RetType;

    /// <summary>方法体超过 big-body-nodes 个节点：变量不取原始存储（codegen.clj :big?）。</summary>
    public bool Big;
    public readonly Dictionary<Symbol, int> Uses = new Dictionary<Symbol, int>();
    public readonly Dictionary<Symbol, int> Pinned = new Dictionary<Symbol, int>();

    /// <summary>正在生成 (fn* ^:once …) 的方法：闭包字段也按最后一次使用清除（cleat-117）。</summary>
    public bool OnceFn;

    /// <summary>deftype 方法：形参不按引用清除（codegen.clj *clear-args-by-ref*）。</summary>
    public bool ClearArgsByRef = true;

    public void Emit(OpCode op, object arg = null) => Buffer.Add(new Il(op, arg));
    public void Emit(Ins ins) => Buffer.Add(ins);
    public void EmitAll(IEnumerable<Ins> code) => Buffer.AddRange(code);
    public void Mark(Lbl l) => Buffer.Add(new LabelIns(l));

    public int NewLocal(object type)
    {
        Locals.Add(type);
        return Locals.Count - 1;
    }

    /// <summary>在单独的缓冲区里生成代码，返回指令序列（codegen.clj with-buffer）。</summary>
    public List<Ins> WithBuffer(Action gen)
    {
        var saved = Buffer;
        Buffer = new List<Ins>();
        gen();
        var code = Buffer;
        Buffer = saved;
        return code;
    }

    public List<Ins> InTry(Action gen)
    {
        TryDepth++;
        var code = WithBuffer(gen);
        TryDepth--;
        return code;
    }

    public bool Clearing => Uses.Count > 0;

    /// <summary>写成 L5 方法。</summary>
    public MethodDecl Finish(MethodDecl m)
    {
        m.Locals.AddRange(Locals);
        m.Body = Buffer;
        m.Source ??= Source;
        return m;
    }
}

/// <summary>
/// L3 → L5（codegen.clj 的 cg / cg* 与类生成；cleat-174 的基础档：值都是 object，只有原始签名的形参与返回值是原始类型）。
/// </summary>
internal static partial class CodeGen
{
    internal static readonly bool OptCharBox = !OptFlags.Off("charbox");
    internal static readonly bool OptBoxCache = !OptFlags.Off("boxcache");
    internal static readonly bool OptBoxDouble = !OptFlags.Off("boxdouble");

    // —— 源文件与序列点（cleat-158）——

    internal static string RealSource(object f)
    {
        string s = f?.ToString();
        return string.IsNullOrEmpty(s) || s == "NO_SOURCE_PATH" || s == "NO_SOURCE_FILE" ? null : s;
    }

    /// <summary>本编译单元的源文件：驱动按单元放的线程局部值优先，否则 RT 的 *source-path*。</summary>
    internal static string UnitSource() =>
        RealSource(Cleat.Core.Compiler.DriverOps.SourceFile.Value) ?? RealSource(RT.SourcePathVar.deref());

    private static Line SeqPointCode(MethodCtx mc, Node e)
    {
        if (e is null || !e.Pos.IsKnown) return null;
        var p = e.Pos;
        string f = RealSource(p.File);
        if (f != null) mc.Source = f;
        if (mc.Source == null) return null;
        if (mc.LastSrc is { } last && last.File == f && last.Line == p.Line) return null;
        mc.LastSrc = (f, p.Line);
        int c = Math.Max(1, p.Column);
        return new Line(p.Line, c, p.Line, c + 1);
    }

    private static void EmitSeqPoint(MethodCtx mc, Node e)
    {
        if (SeqPointCode(mc, e) is { } l) mc.Emit(l);
    }

    // —— 收尾 ——

    private static void FinishValue(MethodCtx mc, Ctx ctx)
    {
        switch (ctx)
        {
            case Ctx.Effect: mc.Emit(OpCodes.Pop); break;
            case Ctx.Test: mc.Emit(OpCodes.Call, Rt.IsTrue); break;
            case Ctx.Tail:
                if (mc.RetType == Raw.L64) mc.Emit(OpCodes.Call, Rt.PrimRetLong);
                else if (mc.RetType == Raw.Fl) mc.Emit(OpCodes.Call, Rt.PrimRetDouble);
                mc.Emit(OpCodes.Ret);
                break;
        }
    }

    private static void FinishBool(MethodCtx mc, Ctx ctx)
    {
        switch (ctx)
        {
            case Ctx.Effect: mc.Emit(OpCodes.Pop); break;
            case Ctx.Value: mc.Emit(OpCodes.Call, Rt.BoolBox); break;
            case Ctx.Tail:
                mc.Emit(OpCodes.Call, Rt.BoolBox);
                FinishValue(mc, Ctx.Tail);
                break;
        }
    }

    private static void FinishNone(MethodCtx mc, Ctx ctx)
    {
        switch (ctx)
        {
            case Ctx.Test: mc.Emit(OpCodes.Ldc_I4_1); break;
            case Ctx.Value: mc.Emit(OpCodes.Ldnull); break;
            case Ctx.Tail:
                mc.Emit(OpCodes.Ldnull);
                FinishValue(mc, Ctx.Tail);
                break;
        }
    }

    // —— 装箱 ——

    internal static void BoxLong(MethodCtx mc)
    {
        if (OptBoxCache) mc.Emit(OpCodes.Call, Rt.LongBoxGet);
        else mc.Emit(OpCodes.Box, Rt.Int64);
    }

    internal static void BoxDouble(MethodCtx mc)
    {
        if (OptBoxDouble) mc.Emit(OpCodes.Call, Rt.LongBoxDouble);
        else mc.Emit(OpCodes.Box, Rt.Double);
    }

    private static void BoxChar(MethodCtx mc)
    {
        if (OptCharBox) mc.Emit(OpCodes.Call, Rt.CharBoxGet);
        else mc.Emit(OpCodes.Box, Rt.Char);
    }

    /// <summary>栈上的原始值装箱（codegen.clj box-raw!；None 不动：没有原始类型的 :dfield）。</summary>
    internal static void BoxRaw(MethodCtx mc, Raw r)
    {
        switch (r)
        {
            case Raw.L64: BoxLong(mc); break;
            case Raw.Fl: BoxDouble(mc); break;
            case Raw.Num: mc.Emit(OpCodes.Call, Rt.NumBox); break;
            case Raw.Fx: mc.Emit(OpCodes.Box, Rt.Int32); break;
        }
    }

    /// <summary>原始表示的 CLR 类型（codegen.clj type-clr；None 是 object）。</summary>
    internal static Type RawType(Raw r) => r switch
    {
        Raw.L64 => Rt.Int64,
        Raw.Fl => Rt.Double,
        Raw.Fx => Rt.Int32,
        Raw.Num => Rt.NumType,
        _ => Rt.Obj,
    };

    // —— 常量 ——

    private static bool BmpSafe(string s)
    {
        for (int j = 0; j < s.Length; j++)
        {
            char c = s[j];
            if (char.IsHighSurrogate(c) && j + 1 < s.Length && char.IsLowSurrogate(s[j + 1])) { j++; continue; }
            if (char.IsSurrogate(c)) return false;
        }
        return true;
    }

    internal static void LoadConstant(MethodCtx mc, object d)
    {
        switch (d)
        {
            case int n:
                mc.Emit(OpCodes.Ldc_I4, n);
                mc.Emit(OpCodes.Box, Rt.Int32);
                break;
            case bool b: mc.Emit(OpCodes.Ldsfld, b ? Rt.TrueField : Rt.FalseField); break;
            case null: mc.Emit(OpCodes.Ldnull); break;
            case char c:
                mc.Emit(OpCodes.Ldc_I4, (int)c);
                BoxChar(mc);
                break;
            case string s when BmpSafe(s): mc.Emit(OpCodes.Ldstr, s); break;
            default: mc.Emit(new LdConst(mc.Unit.ConstantIndex(d))); break;
        }
    }

    /// <summary>Var 对象压栈（类型为 Var）。eval 单元：$Vars 的 static readonly 字段；镜像与包：ldconst + castclass。</summary>
    internal static void LoadVarObject(MethodCtx mc, object d)
    {
        var u = mc.Unit;
        if (u.Decl.Live && d is Var v)
        {
            FieldDecl f = null;
            foreach (var (vv, ff) in u.VarFields)
                if (ReferenceEquals(vv, v)) { f = ff; break; }
            if (f == null)
            {
                u.VarsClass ??= new ClassDecl("$Vars") { Abstract = true, Sealed = true };
                f = u.VarsClass.AddField("v" + u.VarFields.Count, Rt.VarType, isStatic: true, readOnly: true);
                u.VarFields.Add((v, f));
            }
            mc.Emit(OpCodes.Ldsfld, f);
        }
        else
        {
            mc.Emit(new LdConst(u.ConstantIndex(d)));
            mc.Emit(OpCodes.Castclass, Rt.VarType);
        }
    }

    /// <summary>$Vars 的类型初始化器：从常量表取各 Var 填字段（在取常量表之前调用：它把 Var 登记进常量表）。</summary>
    internal static void FinishVarsClass(UnitState u)
    {
        if (u.VarsClass == null) return;
        var cctor = new MethodDecl(".cctor", MethodKind.Cctor) { Static = true };
        foreach (var (v, f) in u.VarFields)
        {
            cctor.Body.Add(new LdConst(u.ConstantIndex(v)));
            cctor.Body.Add(new Il(OpCodes.Castclass, Rt.VarType));
            cctor.Body.Add(new Il(OpCodes.Stsfld, f));
        }
        cctor.Body.Add(new Il(OpCodes.Ret));
        u.VarsClass.AddMethod(cctor);
        u.Decl.Classes.Add(u.VarsClass);
    }

    // —— 操作数（lift-try）——

    private static bool ContainsTry(List<Ins> code) => code.Any(x => x is TryIns);

    private static object CodeResultType(List<Ins> code)
    {
        if (code.Count == 0 || code[^1] is not Il last) return Rt.Obj;
        if (last.Op == OpCodes.Call || last.Op == OpCodes.Callvirt)
        {
            var rt = (last.Arg as MethodInfo)?.ReturnType;
            return rt == Rt.Int32 || rt == Rt.Double ? rt : Rt.Obj;
        }
        if (last.Op == OpCodes.Unbox_Any) return last.Arg is Type t && (t == Rt.Int32 || t == Rt.Double) ? t : Rt.Obj;
        if (last.Op == OpCodes.Ldc_I4) return Rt.Int32;
        if (last.Op == OpCodes.Ldc_R8) return Rt.Double;
        return Rt.Obj;
    }

    private static IEnumerable<Ins> ReloadCast(List<Ins> code) =>
        code.Count > 0 && code[^1] is Il { Op: var op } last && op == OpCodes.Castclass ? new Ins[] { last } : Array.Empty<Ins>();

    /// <summary>依次生成多个操作数；排在后面的操作数含 try 时先全部存入临时变量（进入 try 时栈须为空）。</summary>
    private static void Operands(MethodCtx mc, IEnumerable<Action> gens)
    {
        var codes = gens.Select(g => mc.WithBuffer(g)).ToList();
        if (codes.Count > 0 && codes.Skip(1).Any(ContainsTry))
        {
            var temps = new List<(int T, List<Ins> Code)>();
            foreach (var c in codes)
            {
                int t = mc.NewLocal(CodeResultType(c));
                mc.EmitAll(c);
                mc.Emit(OpCodes.Stloc, t);
                temps.Add((t, c));
            }
            foreach (var (t, c) in temps)
            {
                mc.Emit(OpCodes.Ldloc, t);
                if (ReferenceEquals(CodeResultType(c), Rt.Obj)) mc.EmitAll(ReloadCast(c));
            }
        }
        else
            foreach (var c in codes) mc.EmitAll(c);
    }

    /// <summary>栈顶 n 个值打包成 object[]。</summary>
    private static void PackArray(MethodCtx mc, int n)
    {
        // 同 codegen.clj pack-array!：按顺序分配局部变量，最后压栈的值先出栈（存进最后分配的那个）
        var temps = new int[n];
        for (int k = 0; k < n; k++) temps[k] = mc.NewLocal(Rt.Obj);
        for (int k = n - 1; k >= 0; k--) mc.Emit(OpCodes.Stloc, temps[k]);
        mc.Emit(OpCodes.Ldc_I4, n);
        mc.Emit(OpCodes.Newarr, Rt.Obj);
        for (int j = 0; j < n; j++)
        {
            mc.Emit(OpCodes.Dup);
            mc.Emit(OpCodes.Ldc_I4, j);
            LdlocClear(mc, temps[j]);
            mc.Emit(OpCodes.Stelem_Ref);
        }
    }

    // —— 变量与局部变量清除（cleat-042 第 5 项）——

    private static Loc VarLocation(MethodCtx mc, Symbol x) =>
        mc.Vars.TryGetValue(x, out var l) ? l : throw new InvalidOperationException($"codegen: unbound variable {x}");

    /// <summary>
    /// 最后一次读局部变量 / 形参（codegen.clj load-slot!）：分层编译的方法（<see cref="TieredMethod"/>）经 ldloca / ldarga + Locals.Take
    /// （tier-0 把 JIT 临时变量报告为整个方法内活着）；其余 ld + 置 null（Take 让地址暴露，cleat-116）。
    /// </summary>
    private static void LoadSlot(MethodCtx mc, bool arg, int n)
    {
        if (TieredMethod)
        {
            mc.Emit(arg ? OpCodes.Ldarga : OpCodes.Ldloca, n);
            mc.Emit(OpCodes.Call, Rt.LocalsTake);
            return;
        }
        mc.Emit(arg ? OpCodes.Ldarg : OpCodes.Ldloc, n);
        mc.Emit(OpCodes.Ldnull);
        mc.Emit(arg ? OpCodes.Starg : OpCodes.Stloc, n);
    }

    private static void ClearVar(MethodCtx mc, Symbol x)
    {
        if (!mc.Vars.TryGetValue(x, out var loc)) return;
        switch (loc.Kind)
        {
            case LocKind.Local:
                mc.Emit(OpCodes.Ldnull);
                mc.Emit(OpCodes.Stloc, loc.Index);
                break;
            case LocKind.Arg:
                mc.Emit(OpCodes.Ldnull);
                mc.Emit(OpCodes.Starg, loc.Index);
                break;
            case LocKind.Field when mc.OnceFn:
                mc.Emit(OpCodes.Ldarg_0);
                mc.Emit(OpCodes.Ldnull);
                mc.Emit(OpCodes.Stfld, loc.Field);
                break;
        }
    }

    /// <summary>引用变量 x：计数减一；load 时压栈；是最后一个引用、不在钉住的循环里、是 object 局部变量或形参时清掉槽。</summary>
    internal static void RefVar(MethodCtx mc, Symbol x, bool load)
    {
        bool last = false;
        if (mc.Uses.TryGetValue(x, out int n))
        {
            mc.Uses[x] = n - 1;
            last = n == 1 && (!mc.Pinned.TryGetValue(x, out int p) || p == 0);
        }
        Loc loc = last && mc.Vars.TryGetValue(x, out var l) ? l : null;
        if (load && loc is { Kind: LocKind.Local }) LoadSlot(mc, false, loc.Index);
        else if (load && loc is { Kind: LocKind.Arg } && mc.ClearArgsByRef) LoadSlot(mc, true, loc.Index);
        else
        {
            if (load) LoadVar(mc, x);
            if (last) ClearVar(mc, x);
        }
    }

    internal static void LdlocClear(MethodCtx mc, int t)
    {
        if (mc.Clearing) LoadSlot(mc, false, t);
        else mc.Emit(OpCodes.Ldloc, t);
    }

    /// <summary>try-catch 的处理器 (lambda nm (fv*) (clause (x) false body))：异常变量与体（处理器内联进 catch 块）。</summary>
    private static (Symbol Var, Expr Body) HandlerParts(Expr h)
    {
        var l = (Lambda)h;
        var cl = l.Clauses[0];
        return (cl.Params[0], cl.Body);
    }

    /// <summary>
    /// 遍历变量引用（onRef）与约束（onBind）；不进入嵌套 lambda 的体，闭包创建时读的自由变量算引用；try 的处理器体内联，照常遍历。
    /// </summary>
    private static void WalkVarRefs(Expr e, Action<Symbol> onRef, Action<Symbol> onBind)
    {
        void FreeOf(Lambda le, Symbol self)
        {
            foreach (var x in le.Free)
                if (self is null || !x.Equals(self)) onRef(x);
        }

        void Walk(Expr x)
        {
            switch (x)
            {
                case Ref r: onRef(r.Name); break;
                case If i: Walk(i.Test); Walk(i.Then); Walk(i.Else); break;
                case Begin b: foreach (var y in b.Body) Walk(y); break;
                case Set s: Walk(s.Value); break;
                case Lambda l: foreach (var v in l.Free) onRef(v); break;
                case Letrec lr:
                    foreach (var v in lr.Vars) onBind(v);
                    for (int k = 0; k < lr.Vars.Length; k++) FreeOf((Lambda)lr.Inits[k], lr.Vars[k]);
                    Walk(lr.Body);
                    break;
                case Let lt:
                    foreach (var y in lt.Inits) Walk(y);
                    foreach (var v in lt.Vars) onBind(v);
                    Walk(lt.Body);
                    break;
                case Call c: Walk(c.Fn); foreach (var y in c.Args) Walk(y); break;
                case Throw t: Walk(t.Value); break;
                case TryFinally tf: Walk(tf.Body); Walk(tf.Finally); break;
                case TryCatch tc:
                    Walk(tc.Body);
                    foreach (var h in tc.Handlers)
                    {
                        var (bn, hb) = HandlerParts(h);
                        onBind(bn);
                        Walk(hb);
                    }
                    break;
                case Label lb: foreach (var v in lb.Params) onBind(v); Walk(lb.Body0); Walk(lb.Body1); break;
                case Jump j: foreach (var y in j.Args) Walk(y); break;
                case Box bx: Walk(bx.Value); break;
                case Unbox ub: onRef(ub.Name); break;
                case SetBox sb: onRef(sb.Name); Walk(sb.Value); break;
                case CljVarSet vs: Walk(vs.Value); break;
                case CljDef d: Walk(d.Expr0); Walk(d.Expr1); break;
                case CljInvoke ci: Walk(ci.Fn); foreach (var y in ci.Args) Walk(y); break;
                case CljKeywordInvoke k: Walk(k.Target); foreach (var y in k.Args) Walk(y); break;
                case CljCall cc: foreach (var y in cc.Args) Walk(y); break;
                case CljStaticCall sc: foreach (var y in sc.Args) Walk(y); break;
                case CheckInitialized ci: Walk(ci.Value); break;
                case CljCase cs: Walk(cs.Expr0); Walk(cs.Expr1); foreach (var y in cs.Rest) Walk(y); break;
                case CljDeftype dt:
                    if (DeftypeInfo.IsReify(dt.Datum))
                        foreach (var v in DeftypeCaps(dt)) onRef(v);
                    break;
            }
        }

        Walk(e);
    }

    internal static void InitClearing(MethodCtx mc, Expr body) =>
        WalkVarRefs(body, x => mc.Uses[x] = (mc.Uses.TryGetValue(x, out var n) ? n : 0) + 1, _ => { });

    private static List<(Symbol, int)> BranchRefCounts(MethodCtx mc, Expr e)
    {
        var r = new List<(Symbol, int)>();
        if (mc.Uses.Count == 0) return r;
        var t = new Dictionary<Symbol, int>();
        var order = new List<Symbol>();
        WalkVarRefs(e, x =>
        {
            if (!mc.Uses.ContainsKey(x)) return;
            if (!t.ContainsKey(x)) order.Add(x);
            t[x] = (t.TryGetValue(x, out var n) ? n : 0) + 1;
        }, _ => { });
        foreach (var x in order) r.Add((x, t[x]));
        return r;
    }

    private static void AdjustUses(MethodCtx mc, List<(Symbol X, int N)> counts, int sign)
    {
        foreach (var (x, n) in counts) mc.Uses[x] = (mc.Uses.TryGetValue(x, out var c) ? c : 0) + sign * n;
    }

    private static List<Symbol> LoopOuterRefs(Expr body, IEnumerable<Symbol> ps)
    {
        var refs = new HashSet<Symbol>();
        var order = new List<Symbol>();
        var bound = new HashSet<Symbol>(ps);
        WalkVarRefs(body, x => { if (refs.Add(x)) order.Add(x); }, x => bound.Add(x));
        return order.Where(x => !bound.Contains(x)).ToList();
    }

    private static void Pin(MethodCtx mc, IEnumerable<Symbol> xs, int d)
    {
        foreach (var v in xs) mc.Pinned[v] = (mc.Pinned.TryGetValue(v, out var n) ? n : 0) + d;
    }

    internal static void LoadVar(MethodCtx mc, Symbol x)
    {
        var loc = VarLocation(mc, x);
        switch (loc.Kind)
        {
            case LocKind.Arg: mc.Emit(OpCodes.Ldarg, loc.Index); break;
            case LocKind.Local: mc.Emit(OpCodes.Ldloc, loc.Index); break;
            case LocKind.TArg:
                mc.Emit(OpCodes.Ldarg, loc.Index);
                BoxRaw(mc, loc.Raw);
                break;
            case LocKind.TLocal:
                mc.Emit(OpCodes.Ldloc, loc.Index);
                BoxRaw(mc, loc.Raw);
                break;
            case LocKind.NumArg: mc.Emit(OpCodes.Ldarg, loc.Index); break;   // Num 形参的装箱用途读原来的实参（cleat-102）
            case LocKind.Field:
                mc.Emit(OpCodes.Ldarg_0);
                mc.Emit(OpCodes.Ldfld, loc.Field);
                break;
            case LocKind.DField:
                mc.Emit(OpCodes.Ldarg_0);
                mc.Emit(OpCodes.Ldfld, loc.Field);
                BoxRaw(mc, loc.Raw);
                break;
            case LocKind.Self: mc.Emit(OpCodes.Ldarg_0); break;
        }
    }

    private static void StoreVar(MethodCtx mc, Symbol x)
    {
        var loc = VarLocation(mc, x);
        switch (loc.Kind)
        {
            case LocKind.Arg: mc.Emit(OpCodes.Starg, loc.Index); break;
            case LocKind.Local: mc.Emit(OpCodes.Stloc, loc.Index); break;
            case LocKind.TLocal: mc.Emit(OpCodes.Stloc, loc.Index); break;
            case LocKind.NumArg: mc.Emit(OpCodes.Stloc, loc.NumLocal); break;   // 别名的写回（值就是形参本身）
            case LocKind.DField:
                {
                    int t = mc.NewLocal(Rt.Obj);
                    mc.Emit(OpCodes.Stloc, t);
                    mc.Emit(OpCodes.Ldarg_0);
                    mc.Emit(OpCodes.Ldloc, t);
                    if (loc.Raw == Raw.L64) mc.Emit(OpCodes.Call, Rt.PrimArgLong);
                    else if (loc.Raw == Raw.Fl) mc.Emit(OpCodes.Call, Rt.PrimArgDouble);
                    mc.Emit(OpCodes.Stfld, loc.Field);
                    break;
                }
            default: throw new InvalidOperationException($"codegen: cannot assign captured variable {x}");
        }
    }

    /// <summary>g$名字$序号$后缀 → 名字（分析器的临时名，报错信息用）。</summary>
    internal static string SourceName(Symbol x)
    {
        string s = x.ToString();
        int n = s.Length;
        if (n > 2 && s[0] == 'g' && s[1] == '$')
        {
            int dollars = 0;
            for (int j = n - 1; j >= 2; j--)
            {
                if (s[j] != '$') continue;
                if (dollars == 1) return s.Substring(2, j - 2);
                dollars++;
            }
        }
        return s;
    }

    private static bool BranchesTo(List<Ins> code, Lbl l)
    {
        foreach (var x in code)
        {
            if (x is Il il && il.Arg == l) return true;
            if (x is Il { Arg: Lbl[] ls } && ls.Contains(l)) return true;
            if (x is TryIns t && (BranchesTo(t.Body, l) || t.Catches.Any(c => BranchesTo(c.Code, l)) || (t.Finally != null && BranchesTo(t.Finally, l))))
                return true;
        }
        return false;
    }

    // —— 表达式 ——

    private static bool RawTailLeaf(Expr e) => e is not (If or Begin or Let or Letrec or Label or Jump or Throw or TryFinally or TryCatch);

    /// <summary>
    /// codegen.clj cg：原始签名的方法里尾位置的叶子表达式类型已是返回的原始类型时直接以原始值返回（cleat-042）；其余照常（cg*）。
    /// </summary>
    internal static void Cg(Expr e, MethodCtx mc, Ctx ctx)
    {
        var rt = ctx == Ctx.Tail ? mc.RetType : Raw.None;
        if (rt != Raw.None && RawTailLeaf(e) && ExprType(e) == RawKind(rt))
        {
            CgRaw(e, mc, rt);
            mc.Emit(OpCodes.Ret);
        }
        else CgStar(e, mc, ctx);
    }

    private static void CgStar(Expr e, MethodCtx mc, Ctx ctx)
    {
        EmitSeqPoint(mc, e);
        switch (e)
        {
            case Ref r:
                RefVar(mc, r.Name, ctx != Ctx.Effect);
                if (ctx != Ctx.Effect) FinishValue(mc, ctx);
                break;
            case Label lb: CgLabel(lb, mc, ctx); break;
            case Jump j: CgJump(j, mc, ctx); break;
            case Quote q:
                if (ctx == Ctx.Effect) break;
                if (ctx == Ctx.Test)
                {
                    mc.Emit(q.Datum is not null && q.Datum is not false ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
                    break;
                }
                LoadConstant(mc, q.Datum);
                FinishValue(mc, ctx);
                break;
            case If i:
                {
                    var elseL = new Lbl();
                    var endL = new Lbl();
                    // 分层编译的方法（cleat-114）：值不留在求值栈上跨过汇合点——JIT 把跨块的栈值溢出到临时变量，tier-0 把它报告为整个方法内活着；
                    // 两支各存进同一个局部变量，汇合后取出并清空
                    int merge = TieredMethod && ctx == Ctx.Value ? mc.NewLocal(Rt.Obj) : -1;
                    Cg(i.Test, mc, Ctx.Test);
                    mc.Emit(OpCodes.Brfalse, elseL);
                    // 局部变量清除按分支：生成 then 支时先扣掉 else 支的引用数，生成 else 支前加回
                    var c2 = BranchRefCounts(mc, i.Else);
                    AdjustUses(mc, c2, -1);
                    Cg(i.Then, mc, ctx);
                    AdjustUses(mc, c2, +1);
                    if (merge >= 0) mc.Emit(OpCodes.Stloc, merge);
                    if (ctx != Ctx.Tail) mc.Emit(OpCodes.Br, endL);
                    mc.Mark(elseL);
                    Cg(i.Else, mc, ctx);
                    if (merge >= 0) mc.Emit(OpCodes.Stloc, merge);
                    if (ctx != Ctx.Tail) mc.Mark(endL);
                    if (merge >= 0)
                    {
                        mc.Emit(OpCodes.Ldloca, merge);
                        mc.Emit(OpCodes.Call, Rt.LocalsTake);
                    }
                    break;
                }
            case Begin b:
                for (int k = 0; k < b.Body.Length - 1; k++) Cg(b.Body[k], mc, Ctx.Effect);
                Cg(b.Body[^1], mc, ctx);
                break;
            case Set s:
                Cg(s.Value, mc, Ctx.Value);
                StoreVar(mc, s.Name);
                FinishNone(mc, ctx);
                break;
            case Lambda l:
                if (ctx == Ctx.Effect) break;
                {
                    var c = LambdaClass(l, mc, null);
                    MakeClosure(mc, c, l.Free);
                    FinishValue(mc, ctx);
                }
                break;
            case Letrec lr:
                {
                    // 命名 fn*（自引用）：先创建全部函数对象，再填自由变量字段（组内互相引用）
                    var classes = new List<FnClass>();
                    for (int k = 0; k < lr.Vars.Length; k++) classes.Add(LambdaClass((Lambda)lr.Inits[k], mc, lr.Vars[k]));
                    foreach (var x in lr.Vars) BindLocal(mc, x);   // letrec 的变量不进推断，总是 object
                    for (int k = 0; k < lr.Vars.Length; k++)
                    {
                        mc.Emit(OpCodes.Newobj, classes[k].Ctor);
                        StoreVar(mc, lr.Vars[k]);
                    }
                    for (int k = 0; k < lr.Vars.Length; k++)
                    {
                        var x = lr.Vars[k];
                        var le = (Lambda)lr.Inits[k];
                        FillFields(mc, classes[k], le.Free.Where(v => !v.Equals(x)).ToList(), () => LoadVar(mc, x));
                    }
                    Cg(lr.Body, mc, ctx);
                    break;
                }
            case Let lt:
                // 右边依次求值（看不到同一 let 的变量），再依次存入新变量
                BindLetVars(lt, mc);
                Cg(lt.Body, mc, ctx);
                break;
            case Call c:
                // lower-loops 把立即调用的 lambda 变成 let、命名 let 变成 label；剩下的按 IFn 调用
                CgStar(new CljInvoke(c.Fn, c.Args), mc, ctx);
                break;
            case Throw t:
                Cg(t.Value, mc, Ctx.Value);
                mc.Emit(OpCodes.Castclass, typeof(Exception));
                mc.Emit(OpCodes.Throw);
                if (ctx == Ctx.Value) mc.Emit(OpCodes.Ldnull);
                else if (ctx == Ctx.Test) mc.Emit(OpCodes.Ldc_I4_0);
                break;
            case TryFinally tf: CgTryFinally(tf, mc, ctx); break;
            case TryCatch tc: CgTryCatch(tc, mc, ctx); break;
            case CljVarRef vr:
                if (ctx == Ctx.Effect) break;
                LoadVarObject(mc, vr.Var);
                mc.Emit(OpCodes.Callvirt, vr.Flag ? Rt.VarGet : Rt.VarGetRawRoot);
                FinishValue(mc, ctx);
                break;
            case CljVarSet vs:
                Operands(mc, new Action[] { () => LoadVarObject(mc, vs.Var), () => Cg(vs.Value, mc, Ctx.Value) });
                mc.Emit(OpCodes.Callvirt, Rt.VarSet);
                FinishValue(mc, ctx);
                break;
            case CljDef d: CgDef(d, mc, ctx); break;
            case CljInvoke ci: CgInvoke(ci.Fn, ci.Args, mc, ctx); break;
            case CljKeywordInvoke k: CgKeywordInvoke(k, mc, ctx); break;
            case CljCall cc: CgCljCall(cc, mc, ctx); break;
            case CljStaticCall sc: CgStaticCall(sc, mc, ctx); break;
            case CljCase cs: CgCase(cs, mc, ctx); break;
            case CljDeftype dt: CgDeftype(dt, mc, ctx); break;
            case Box bx:
                Cg(bx.Value, mc, Ctx.Value);
                mc.Emit(OpCodes.Newobj, Rt.BoxCtor);
                FinishValue(mc, ctx);
                break;
            case Unbox ub:
                if (ctx == Ctx.Effect) RefVar(mc, ub.Name, false);
                else
                {
                    RefVar(mc, ub.Name, true);
                    mc.Emit(OpCodes.Castclass, Rt.BoxType);
                    mc.Emit(OpCodes.Ldfld, Rt.BoxValue);
                    FinishValue(mc, ctx);
                }
                break;
            case SetBox sb:
                Operands(mc, new Action[]
                {
                    () => { RefVar(mc, sb.Name, true); mc.Emit(OpCodes.Castclass, Rt.BoxType); },
                    () => Cg(sb.Value, mc, Ctx.Value),
                });
                mc.Emit(OpCodes.Stfld, Rt.BoxValue);
                FinishNone(mc, ctx);
                break;
            case Uninitialized:
                if (ctx == Ctx.Effect) break;
                mc.Emit(OpCodes.Ldsfld, Rt.Uninitialized);
                FinishValue(mc, ctx);
                break;
            case CheckInitialized ci:
                {
                    string nm = ci.Value switch { Ref r => SourceName(r.Name), Unbox u => SourceName(u.Name), _ => "?" };
                    Cg(ci.Value, mc, Ctx.Value);
                    mc.Emit(OpCodes.Ldstr, nm);
                    mc.Emit(OpCodes.Call, Rt.CheckInitialized);
                    FinishValue(mc, ctx);
                    break;
                }
            default:
                throw new NotSupportedException($"codegen: node not supported: {e.Kind}");
        }
    }

    private static void CgLabel(Label lb, MethodCtx mc, Ctx ctx)
    {
        // 局部循环（lower-loops 产生）：形参是 IL 局部变量（推断定了类型的取原始存储），jump 赋值后 br 到循环体；body0 没跳到循环体时循环体不生成
        foreach (var x in lb.Params) BindLocal(mc, x);
        var lf = new Lbl();
        var endL = new Lbl();
        var outer = LoopOuterRefs(lb.Body1, lb.Params);
        // 分层方法里尾位置的循环：jump 读完实参后，循环体不再引用的 object 形参置空——fn 级 recur 的入口把形参复制进循环变量，
        // tier-0 帧里形参整个方法内都报告为活，抓着最初传进来的序列（cleat-117）
        mc.Labels[lb.Name] = (lf, lb.Params, TieredMethod && ctx == Ctx.Tail ? new HashSet<Symbol>(outer) : null);
        var code = mc.WithBuffer(() => Cg(lb.Body0, mc, ctx));
        mc.EmitAll(code);
        if (!BranchesTo(code, lf)) return;
        if (ctx != Ctx.Tail) mc.Emit(OpCodes.Br, endL);
        mc.Mark(lf);
        // 体外绑定、体内引用的变量在循环里钉住（不清除）；循环体不跳回自己时（arity 标签）不是循环，不钉（cleat-117）
        if (!JumpsTo(lb.Body1, lb.Name)) outer = new List<Symbol>();
        Pin(mc, outer, +1);
        Cg(lb.Body1, mc, ctx);
        Pin(mc, outer, -1);
        if (ctx != Ctx.Tail) mc.Mark(endL);
    }

    private static void CgJump(Jump j, MethodCtx mc, Ctx ctx)
    {
        if (!mc.Labels.TryGetValue(j.Target, out var entry)) throw new InvalidOperationException($"codegen: jump to unknown label {j.Target}");
        // 实参先全部求值到临时变量再写入形参（并行赋值）；有类型的形参用原始值的临时变量；Num 形参的别名（实参就是形参本身）不用算也不用写
        int n = Math.Min(entry.Params.Length, j.Args.Length);
        var temps = new int[n];
        for (int k = 0; k < n; k++)
        {
            var p = entry.Params[k];
            if (NumAliasLoc(mc, p) != null)
            {
                temps[k] = -1;
                continue;
            }
            var rt = VarRawType(mc, p);
            if (rt != Raw.None) CgRaw(j.Args[k], mc, rt);
            else Cg(j.Args[k], mc, Ctx.Value);
            temps[k] = mc.NewLocal(RawType(rt));
            mc.Emit(OpCodes.Stloc, temps[k]);
        }
        if (entry.Keep is { } keep) ClearUnkeptArgs(mc, entry.Params, j.Args, keep);
        for (int k = 0; k < n; k++)
        {
            if (temps[k] < 0) continue;
            if (VarRawType(mc, entry.Params[k]) != Raw.None) mc.Emit(OpCodes.Ldloc, temps[k]);
            else LdlocClear(mc, temps[k]);
            StoreVar(mc, entry.Params[k]);
        }
        mc.Emit(OpCodes.Br, entry.Label);
        if (ctx == Ctx.Value) mc.Emit(OpCodes.Ldnull);
        else if (ctx == Ctx.Test) mc.Emit(OpCodes.Ldc_I4_0);
    }

    private static void CgTryFinally(TryFinally tf, MethodCtx mc, Ctx ctx)
    {
        int tmp = ctx == Ctx.Effect ? -1 : mc.NewLocal(Rt.Obj);
        var body = mc.InTry(() =>
        {
            Cg(tf.Body, mc, tmp >= 0 ? Ctx.Value : Ctx.Effect);
            if (tmp >= 0) mc.Emit(OpCodes.Stloc, tmp);
        });
        var f = mc.WithBuffer(() => Cg(tf.Finally, mc, Ctx.Effect));
        mc.Emit(new TryIns { Body = body, Finally = f });
        if (tmp >= 0)
        {
            mc.Emit(OpCodes.Ldloc, tmp);
            FinishValue(mc, ctx);
        }
    }

    private static void CgTryCatch(TryCatch tc, MethodCtx mc, Ctx ctx)
    {
        // try { 值 = body } catch (T_i) { 异常 → 处理器的形参；值 = 处理器体 } …：处理器内联进 catch 块（cleat-092）。
        // 处理器引用的外层变量在 body 里钉住（异常可能在 body 清掉它们之后抛出）；各处理器互为分支，生成第 i 个时先扣掉后面处理器的引用数
        int tmp = ctx == Ctx.Effect ? -1 : mc.NewLocal(Rt.Obj);
        var hctx = tmp >= 0 ? Ctx.Value : Ctx.Effect;
        var parts = tc.Handlers.Select(HandlerParts).ToList();
        var hrefs = parts.SelectMany(p => LoopOuterRefs(p.Body, new[] { p.Var })).Distinct().ToList();
        Pin(mc, hrefs, +1);
        var body = mc.InTry(() =>
        {
            Cg(tc.Body, mc, hctx);
            if (tmp >= 0) mc.Emit(OpCodes.Stloc, tmp);
        });
        Pin(mc, hrefs, -1);
        var counts = parts.Select(p => BranchRefCounts(mc, p.Body)).ToList();
        var t = new TryIns { Body = body };
        for (int j = 0; j < parts.Count; j++)
        {
            int exc = mc.NewLocal(Rt.Obj);
            mc.Vars[parts[j].Var] = new Loc(LocKind.Local, exc);
            var later = counts.Skip(j + 1).ToList();
            foreach (var c in later) AdjustUses(mc, c, -1);
            var code = mc.InTry(() =>
            {
                Cg(parts[j].Body, mc, hctx);
                if (tmp >= 0) mc.Emit(OpCodes.Stloc, tmp);
            });
            foreach (var c in later) AdjustUses(mc, c, +1);
            code.Insert(0, new Il(OpCodes.Stloc, exc));
            t.Catches.Add((tc.Types[j], code));
        }
        mc.Emit(t);
        if (tmp >= 0)
        {
            mc.Emit(OpCodes.Ldloc, tmp);
            FinishValue(mc, ctx);
        }
    }

    private static void CgDef(CljDef d, MethodCtx mc, Ctx ctx)
    {
        void LoadVarObj() => LoadVarObject(mc, d.Datum);
        if (d.Flag1)
        {
            LoadVarObj();
            mc.Emit(OpCodes.Callvirt, Rt.VarSetDynamic);
            mc.Emit(OpCodes.Pop);
        }
        if (d.Flag0)
        {
            Operands(mc, new Action[] { LoadVarObj, () => Cg(d.Expr0, mc, Ctx.Value) });
            mc.Emit(OpCodes.Callvirt, Rt.VarBindRoot);
        }
        Operands(mc, new Action[] { LoadVarObj, () => { Cg(d.Expr1, mc, Ctx.Value); mc.Emit(OpCodes.Castclass, Rt.PersistentMapType); } });
        mc.Emit(OpCodes.Callvirt, Rt.VarSetMeta);
        if (ctx != Ctx.Effect)
        {
            LoadConstant(mc, d.Datum);
            FinishValue(mc, ctx);
        }
    }

    private static void CgInvoke(Expr fn, ImmutableArray<Expr> args, MethodCtx mc, Ctx ctx)
    {
        int argc = args.Length;
        var gens = new List<Action> { () => { Cg(fn, mc, Ctx.Value); mc.Emit(OpCodes.Castclass, Rt.IFnType); } };
        foreach (var a in args) gens.Add(() => Cg(a, mc, Ctx.Value));
        Operands(mc, gens);
        if (argc > 20) PackArray(mc, argc - 20);
        mc.Emit(OpCodes.Callvirt, Rt.Invoke(argc > 20 ? 21 : argc));
        FinishValue(mc, ctx);
    }

    private static void CgKeywordInvoke(CljKeywordInvoke k, MethodCtx mc, Ctx ctx)
    {
        int t = mc.NewLocal(Rt.Obj);
        var defs = k.Args.Select(_ => mc.NewLocal(Rt.Obj)).ToList();
        var slow = new Lbl();
        var end = new Lbl();
        Cg(k.Target, mc, Ctx.Value);
        mc.Emit(OpCodes.Stloc, t);
        for (int j = 0; j < defs.Count; j++)
        {
            Cg(k.Args[j], mc, Ctx.Value);
            mc.Emit(OpCodes.Stloc, defs[j]);
        }
        mc.Emit(OpCodes.Ldloc, t);
        mc.Emit(OpCodes.Isinst, Rt.ILookupType);
        mc.Emit(OpCodes.Dup);
        mc.Emit(OpCodes.Brfalse, slow);
        LoadConstant(mc, k.Keyword);
        foreach (var l in defs) mc.Emit(OpCodes.Ldloc, l);
        mc.Emit(OpCodes.Callvirt, Rt.ValAt(1 + defs.Count));
        mc.Emit(OpCodes.Br, end);
        mc.Mark(slow);
        mc.Emit(OpCodes.Pop);
        mc.Emit(OpCodes.Ldloc, t);
        LoadConstant(mc, k.Keyword);
        foreach (var l in defs) mc.Emit(OpCodes.Ldloc, l);
        mc.Emit(OpCodes.Call, Rt.RtGet(2 + defs.Count));
        mc.Mark(end);
        FinishValue(mc, ctx);
    }

    /// <summary>(quote d) 的 d；不是常量时 NoDatum。</summary>
    internal static readonly object NoDatum = new object();

    internal static object QuotedDatum(Expr e) => e is Quote q ? q.Datum : NoDatum;

    /// <summary>
    /// cleat-145 的读侧直读（codegen.clj cg-get-instance-field-direct!）：(GetInstanceFieldDirect 'T 'f x)，内联子句已有具体类型守卫，
    /// 所以按守卫过的静态字段读发射（不补 Instance(=NotNil)、object 结果不过 RetObject）；决议不出时退回普通的 GetInstanceField。
    /// </summary>
    private static void CgGetInstanceFieldDirect(ImmutableArray<Expr> args, MethodCtx mc, Ctx ctx)
    {
        var sl = HostCallSignature("GetInstanceFieldDirect", false, args);
        if (sl != null && !ReferenceEquals(sl, Pending)) CgStaticHostCall(sl, mc, ctx, true, false);
        else CgCljCall(new CljCall("GetInstanceField", false, args), mc, ctx);
    }

    /// <summary>
    /// cleat-145（codegen.clj cg-set-instance-field!）：(SetInstanceField 'T 'f x v) 按源顺序求值 x、v 各一次，stfld 直存并返回 v；
    /// 原始字段（^long / ^double）同 :dfield 做入口转换。类型 / 字段决议不出时退回 SetFieldOf 的反射赋值（与原接口调用同义）。
    /// </summary>
    private static void CgSetInstanceField(ImmutableArray<Expr> args, MethodCtx mc, Ctx ctx)
    {
        var ty = QuotedDatum(args[0]) as Type;
        var fname = QuotedDatum(args[1]) as string;
        object[] sig = ty != null && fname != null ? ClojureStaticCalls.ResolveField(ty, fname, false) : null;
        if (sig == null || sig.Length == 0)
        {
            CgCljCall(new CljCall("SetFieldOf", false, args), mc, ctx);
            return;
        }
        string decl = (string)sig[0], fret = sig[3]?.ToString();
        int temp = mc.NewLocal(Rt.Obj);
        Cg(args[2], mc, Ctx.Value);
        mc.Emit(OpCodes.Castclass, decl);
        Cg(args[3], mc, Ctx.Value);
        mc.Emit(OpCodes.Stloc, temp);
        mc.Emit(OpCodes.Ldloc, temp);
        if (fret == "System.Int64") mc.Emit(OpCodes.Call, Rt.PrimArgLong);
        else if (fret == "System.Double") mc.Emit(OpCodes.Call, Rt.PrimArgDouble);
        mc.Emit(OpCodes.Stfld, new FieldRef(decl, (string)sig[1]));
        mc.Emit(OpCodes.Ldloc, temp);
        FinishValue(mc, ctx);
    }

    private static void CgCljCall(CljCall cc, MethodCtx mc, Ctx ctx)
    {
        string op = cc.Op as string;
        var args = cc.Args;
        switch (op)
        {
            case "PrimInvoke":
                CgPrimInvoke(args, mc, ctx);
                return;
            case "Tagged":
            case "Speculated":
                Cg(args[1], mc, ctx);   // 静态类型标注 / 推测的实参：生成代码时透明
                return;
            case "KnownCall":
                CgKnownCall(args, mc, ctx);
                return;
            case "EscapePropagate" when args.Length == 1:
                CgEscapePropagate(args[0], mc, ctx);
                return;
            case "IsInstanceOf" when args.Length == 2 && QuotedDatum(args[0]) is Type t && ClojureStaticCalls.IsinstName(t).Length > 0:
                Cg(args[1], mc, Ctx.Value);
                mc.Emit(OpCodes.Isinst, ClojureStaticCalls.IsinstName(t));   // 按名字：编镜像 / 包时解析到本单元重新定义的同名类型
                mc.Emit(OpCodes.Ldnull);
                mc.Emit(OpCodes.Cgt_Un);
                FinishBool(mc, ctx);
                return;
            case "GetInstanceFieldDirect" when cc.Datum is false && args.Length == 3:
                CgGetInstanceFieldDirect(args, mc, ctx);
                return;
            case "SetInstanceField" when cc.Datum is false && args.Length == 4:
                CgSetInstanceField(args, mc, ctx);
                return;
            case "AwaitAsync" when args.Length == 1:
                CgAwaitAsync(args[0], mc, ctx);
                return;
            case "TypedSelf":
                // 类型化体里的自调用（cleat-104）
                CgTypedSelf(cc, mc);
                BoxRaw(mc, TypedSelfRet(cc));
                FinishValue(mc, ctx);
                return;
            case "TypedEntry":
                CgTypedEntry(args, mc, ctx);
                return;
        }
        // 有 Num 操作数的算术（cleat-102）、long 操作数的 Numbers 原始算术（cleat-042）
        var arith = NumEmit(cc, mc);
        if (arith == ArithResult.None) arith = NumbersRawEmit(cc, mc);
        switch (arith)
        {
            case ArithResult.Bool:
                FinishBool(mc, ctx);
                return;
            case ArithResult.L64:
                BoxRaw(mc, Raw.L64);
                FinishValue(mc, ctx);
                return;
            case ArithResult.Num:
                BoxRaw(mc, Raw.Num);
                FinishValue(mc, ctx);
                return;
        }
        if (TryStaticHostCall(cc, mc, ctx)) return;
        // 缺省：ClojureOps 的同名静态方法（fixed 为整数时多余的实参打包成 object[]）
        var m = op is null ? null : Rt.OpsMethod(op, cc.Datum, args.Length)
            ?? throw new InvalidOperationException($"codegen: no ClojureOps.{op} for {args.Length} argument(s) (fixed {cc.Datum})");
        Operands(mc, args.Select(a => (Action)(() => Cg(a, mc, Ctx.Value))));
        if (cc.Datum is not false) PackArray(mc, args.Length - Convert.ToInt32(cc.Datum));
        mc.Emit(OpCodes.Call, m);
        FinishValue(mc, ctx);
    }

    private static void CgPrimInvoke(ImmutableArray<Expr> e, MethodCtx mc, Ctx ctx)
    {
        CgPrimInvokeCore(e, mc);
        BoxFromSig(mc, SigRet((string)QuotedDatum(e[0])));
        FinishValue(mc, ctx);
    }

    /// <summary>(PrimInvoke '签名 f 实参 …)：f 转为 primifs.&lt;签名&gt;、callvirt invokePrim；结果是签名的原始返回值。类型已是签名原始类型的实参原始求值。</summary>
    private static void CgPrimInvokeCore(ImmutableArray<Expr> e, MethodCtx mc)
    {
        string sig = (string)QuotedDatum(e[0]);
        var iface = PrimIface(sig);
        var cs = SigParams(sig);
        int n = e.Length - 2;
        var raws = new Raw[n];
        for (int k = 0; k < n; k++)
        {
            var r = SigCharRaw(cs[k]);
            raws[k] = r != Raw.None && KindRaw(ExprType(e[k + 2])) == r ? r : Raw.None;
        }
        var codes = new List<List<Ins>> { mc.WithBuffer(() => Cg(e[1], mc, Ctx.Value)) };
        for (int k = 0; k < n; k++)
        {
            var a = e[k + 2];
            var r = raws[k];
            codes.Add(mc.WithBuffer(() => { if (r != Raw.None) CgRaw(a, mc, r); else Cg(a, mc, Ctx.Value); }));
        }
        if (codes.Skip(1).Any(ContainsTry))
        {
            var temps = codes.Select((c, k) => { int t = mc.NewLocal(k == 0 ? Rt.Obj : RawType(raws[k - 1])); mc.EmitAll(c); mc.Emit(OpCodes.Stloc, t); return t; }).ToList();
            mc.Emit(OpCodes.Ldloc, temps[0]);
            mc.Emit(OpCodes.Castclass, iface);
            for (int k = 1; k < temps.Count; k++)
            {
                mc.Emit(OpCodes.Ldloc, temps[k]);
                if (raws[k - 1] == Raw.None) UnboxToSig(mc, cs[k - 1]);
            }
        }
        else
        {
            mc.EmitAll(codes[0]);
            mc.Emit(OpCodes.Castclass, iface);
            for (int k = 1; k < codes.Count; k++)
            {
                mc.EmitAll(codes[k]);
                if (raws[k - 1] == Raw.None) UnboxToSig(mc, cs[k - 1]);
            }
        }
        mc.Emit(OpCodes.Callvirt, iface.GetMethod("invokePrim"));
    }

    private static void CgKnownCall(ImmutableArray<Expr> e, MethodCtx mc, Ctx ctx)
    {
        // (KnownCall '类 Var读 实参 …)（cleat-110）：先求函数值再求实参，isinst 类时 call 类.invokeStatic(f, 实参 …)，否则 IFn.invoke
        var t = QuotedDatum(e[0]) as Type;
        var f = e[1];
        var args = e.RemoveRange(0, 2);
        int n = args.Length;
        bool ok = t != null && (mc.Unit.Decl.Live || (!t.Assembly.IsDynamic && !string.IsNullOrEmpty(t.Assembly.Location)))
                  && n <= 20 && ClojureStaticCalls.IsinstName(t).Length > 0;
        if (!ok)
        {
            CgInvoke(f, args, mc, ctx);
            return;
        }
        int root = mc.NewLocal(Rt.Obj);
        Cg(f, mc, Ctx.Value);
        mc.Emit(OpCodes.Stloc, root);
        var temps = new List<int>();
        foreach (var a in args)
        {
            Cg(a, mc, Ctx.Value);
            int l = mc.NewLocal(Rt.Obj);
            mc.Emit(OpCodes.Stloc, l);
            temps.Add(l);
        }
        var slow = new Lbl();
        var end = new Lbl();
        string cname = ClojureStaticCalls.IsinstName(t);
        mc.Emit(OpCodes.Ldloc, root);
        mc.Emit(OpCodes.Isinst, cname);
        mc.Emit(OpCodes.Brfalse, slow);
        LdlocClear(mc, root);
        foreach (var l in temps) LdlocClear(mc, l);
        int merge = TieredMethod ? mc.NewLocal(Rt.Obj) : -1;   // 结果不留在栈上跨过汇合点（同 if，cleat-114）
        mc.Emit(OpCodes.Call, new MethodRef(cname, "invokeStatic", Objs(n + 1), Rt.Obj));
        if (merge >= 0) mc.Emit(OpCodes.Stloc, merge);
        mc.Emit(OpCodes.Br, end);
        mc.Mark(slow);
        LdlocClear(mc, root);
        mc.Emit(OpCodes.Castclass, Rt.IFnType);
        foreach (var l in temps) LdlocClear(mc, l);
        mc.Emit(OpCodes.Callvirt, Rt.Invoke(n));
        if (merge >= 0) mc.Emit(OpCodes.Stloc, merge);
        mc.Mark(end);
        TakeMerge(mc, merge);
        FinishValue(mc, ctx);
    }

    /// <summary>分层方法的汇合局部变量：取出并清空（ldloca + Locals.Take）。</summary>
    private static void TakeMerge(MethodCtx mc, int merge)
    {
        if (merge < 0) return;
        mc.Emit(OpCodes.Ldloca, merge);
        mc.Emit(OpCodes.Call, Rt.LocalsTake);
    }

    private static void CgEscapePropagate(Expr e, MethodCtx mc, Ctx ctx)
    {
        // marker 是跨调用返回的信号：不在保护区内时尾位置直接 ret，其他位置 throw 同一个 marker；保护区里退回运行时异常协议
        if (mc.TryDepth > 0)
        {
            CgCljCall(new CljCall("EscapePropagateProtected", false, ImmutableArray.Create(e)), mc, ctx);
            return;
        }
        int r = mc.NewLocal(Rt.Obj);
        var normal = new Lbl();
        Cg(e, mc, Ctx.Value);
        mc.Emit(OpCodes.Stloc, r);
        mc.Emit(OpCodes.Ldloc, r);
        mc.Emit(OpCodes.Call, Rt.IsEscapeMarker);
        mc.Emit(OpCodes.Brfalse, normal);
        mc.Emit(OpCodes.Ldloc, r);
        mc.Emit(ctx == Ctx.Tail ? OpCodes.Ret : OpCodes.Throw);
        mc.Mark(normal);
        mc.Emit(OpCodes.Ldloc, r);
        FinishValue(mc, ctx);
    }

    private static void CgStaticCall(CljStaticCall sc, MethodCtx mc, Ctx ctx)
    {
        // 受保护的直接链接（cleat-033）：根还是函数类 d0 时以根作 self 直接 call d0::invokeStatic，否则普通 IFn.invoke
        var e = sc.Args;
        int n = e.Length - 1;
        int root = mc.NewLocal(Rt.Obj);
        var defs = new List<int>();
        for (int k = 1; k < e.Length; k++) defs.Add(mc.NewLocal(Rt.Obj));
        var slow = new Lbl();
        var end = new Lbl();
        Cg(e[0], mc, Ctx.Value);
        mc.Emit(OpCodes.Stloc, root);
        for (int k = 1; k < e.Length; k++)
        {
            Cg(e[k], mc, Ctx.Value);
            mc.Emit(OpCodes.Stloc, defs[k - 1]);
        }
        object cls = sc.Datum0 is Type ty ? ty : (object)(string)sc.Datum0;
        mc.Emit(OpCodes.Ldloc, root);
        mc.Emit(OpCodes.Isinst, cls);
        mc.Emit(OpCodes.Brfalse, slow);
        LdlocClear(mc, root);
        foreach (var l in defs) LdlocClear(mc, l);
        // eval 单元（分层编译）：结果不留在栈上跨过汇合点（同 if，cleat-114）
        int merge = TieredMethod ? mc.NewLocal(Rt.Obj) : -1;
        mc.Emit(OpCodes.Call, new MethodRef(cls, "invokeStatic", Enumerable.Repeat((object)Rt.Obj, n + 1).ToArray(), Rt.Obj));
        if (merge >= 0) mc.Emit(OpCodes.Stloc, merge);
        mc.Emit(OpCodes.Br, end);
        mc.Mark(slow);
        LdlocClear(mc, root);
        mc.Emit(OpCodes.Castclass, Rt.IFnType);
        foreach (var l in defs) LdlocClear(mc, l);
        mc.Emit(OpCodes.Callvirt, Rt.Invoke(n));
        if (merge >= 0) mc.Emit(OpCodes.Stloc, merge);
        mc.Mark(end);
        TakeMerge(mc, merge);
        FinishValue(mc, ctx);
    }

    private static void CgCase(CljCase cs, MethodCtx mc, Ctx ctx)
    {
        // CaseExpr（ClojureCLR 的语义）：d = [shift mask kind entries]，entries 每项 [hash 常量 跳过比较?]
        var d = ((System.Collections.IEnumerable)cs.Datum).Cast<object>().ToList();
        int shift = Convert.ToInt32(d[0]), mask = Convert.ToInt32(d[1]), kind = Convert.ToInt32(d[2]);
        var entries = ((System.Collections.IEnumerable)d[3]).Cast<object>().Select(en => ((System.Collections.IEnumerable)en).Cast<object>().ToList()).ToList();
        int x = mc.NewLocal(Rt.Obj), k = mc.NewLocal(Rt.Int32);
        var defL = new Lbl();
        var endL = new Lbl();
        var labels = entries.Select(_ => new Lbl()).ToList();
        var hs = entries.Select(en => Convert.ToInt32(en[0])).ToList();
        Cg(cs.Expr0, mc, Ctx.Value);
        mc.Emit(OpCodes.Stloc, x);
        if (kind == 0)
        {
            mc.Emit(OpCodes.Ldloc, x);
            mc.Emit(OpCodes.Call, Rt.UtilIsNonCharNumeric);
            mc.Emit(OpCodes.Brfalse, defL);
            mc.Emit(OpCodes.Ldloc, x);
            mc.Emit(OpCodes.Call, Rt.UtilConvertToInt);
        }
        else
        {
            mc.Emit(OpCodes.Ldloc, x);
            mc.Emit(OpCodes.Call, Rt.UtilHash);
        }
        if (mask != 0)
        {
            mc.Emit(OpCodes.Ldc_I4, shift);
            mc.Emit(OpCodes.Shr);
            mc.Emit(OpCodes.Ldc_I4, mask);
            mc.Emit(OpCodes.And);
        }
        mc.Emit(OpCodes.Stloc, k);
        if (hs.Count > 0)
        {
            int lo = hs.Min(), hi = hs.Max();
            if ((long)hi - lo <= 8 + 2 * hs.Count)
            {
                mc.Emit(OpCodes.Ldloc, k);
                mc.Emit(OpCodes.Ldc_I4, lo);
                mc.Emit(OpCodes.Sub);
                var table = new Lbl[hi - lo + 1];
                for (int h = lo; h <= hi; h++)
                {
                    int j = hs.IndexOf(h);
                    table[h - lo] = j >= 0 ? labels[j] : defL;
                }
                mc.Emit(OpCodes.Switch, table);
            }
            else
                for (int j = 0; j < hs.Count; j++)
                {
                    mc.Emit(OpCodes.Ldloc, k);
                    mc.Emit(OpCodes.Ldc_I4, hs[j]);
                    mc.Emit(OpCodes.Beq, labels[j]);
                }
        }
        mc.Emit(OpCodes.Br, defL);
        for (int j = 0; j < entries.Count; j++)
        {
            mc.Mark(labels[j]);
            if (!RT.booleanCast(entries[j][2]))
            {
                mc.Emit(OpCodes.Ldloc, x);
                LoadConstant(mc, entries[j][1]);
                if (kind == 2) mc.Emit(OpCodes.Ceq);
                else mc.Emit(OpCodes.Call, Rt.UtilEquiv);
                mc.Emit(OpCodes.Brfalse, defL);
            }
            Cg(cs.Rest[j], mc, ctx);
            if (ctx != Ctx.Tail) mc.Emit(OpCodes.Br, endL);
        }
        mc.Mark(defL);
        Cg(cs.Expr1, mc, ctx);
        if (ctx != Ctx.Tail) mc.Mark(endL);
    }

    // —— 原始签名（cleat-042）——

    internal static readonly string PrimifsPrefix = "clojure.lang.primifs.";

    internal static Type PrimIface(string sig) =>
        typeof(IFn).Assembly.GetType(PrimifsPrefix + sig) ?? throw new InvalidOperationException($"codegen: no primitive interface {sig}");

    internal static char[] SigParams(string sig) => sig.Substring(0, sig.Length - 1).ToCharArray();
    internal static char SigRet(string sig) => sig[^1];
    internal static Type SigCharType(char c) => c == 'L' ? Rt.Int64 : c == 'D' ? Rt.Double : Rt.Obj;
    internal static Raw SigCharRaw(char c) => c == 'L' ? Raw.L64 : c == 'D' ? Raw.Fl : Raw.None;

    internal static void UnboxToSig(MethodCtx mc, char c)
    {
        if (c == 'L') mc.Emit(OpCodes.Call, Rt.PrimArgLong);
        else if (c == 'D') mc.Emit(OpCodes.Call, Rt.PrimArgDouble);
    }

    internal static void BoxFromSig(MethodCtx mc, char c)
    {
        if (c == 'L') BoxLong(mc);
        else if (c == 'D') BoxDouble(mc);
    }
}
