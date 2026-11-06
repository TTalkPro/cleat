using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Cleat.Compiler.Emit;
using Cleat.Compiler.Ir;
using Cleat.Compiler.Lower;
using Cleat.Compiler.Passes;
using Cleat.Compiler.Passes.Front;
using Cleat.Core.Compiler;
using clojure.lang;

namespace Cleat.Compiler.Driver;

/// <summary>
/// C# 编译器的驱动（driver.clj 的 eval / load 部分）：每个顶层形式 分析 → 前中端 → L5 → 发射 → 执行，之后才读下一个（def 的宏立即生效）。
/// 宏展开与分析用 <see cref="Analyzer.FormAnalyzer"/>（cleat-172 起；之前借用 Clojure 编译器的分析器）。
/// </summary>
public static partial class CsDriver
{
    private static int s_evalCounter;

    /// <summary>分析器：形式 → L0（宏展开在 eval-toplevel 里另做）。</summary>
    internal static Func<object, Expr> Analyze = Analyzer.FormAnalyzer.AnalyzeTop;

    /// <summary>宏展开到不再是宏调用。</summary>
    internal static Func<object, object> MacroexpandFull = Analyzer.FormAnalyzer.MacroexpandFull;

    /// <summary>把 form 的源位置 metadata 转给 x。</summary>
    internal static Func<object, object, object> TransferSource = Analyzer.FormAnalyzer.TransferSource;

    /// <summary>macroexpand-1 钩子的实现。</summary>
    internal static Func<object, object> Macroexpand1 = Analyzer.FormAnalyzer.Macroexpand1;

    /// <summary>eval 钩子（REPL、-e、clojure.core/eval）。</summary>
    public static object EvalHook(object form)
    {
        string s = RT.SourceVar.deref()?.ToString();
        string source = s is "NO_SOURCE_FILE" or "<eval>" or "<stdin>" or null ? "REPL" : s;
        string old = DriverOps.SourceFile.Value;
        DriverOps.SourceFile.Value = source;
        try { return EvalToplevel(form); }
        finally { DriverOps.SourceFile.Value = old; }
    }

    /// <summary>Clojure 的 Compiler.eval：序列形式先 macroexpand（带上原形式的源位置），顶层 (do …) 拆开逐个求值。</summary>
    public static object EvalToplevel(object form)
    {
        if (form is ISeq or IPersistentList)
        {
            var f0 = form;
            form = TransferSource(DriverOps.RunForm.invoke(new Thunk(() => MacroexpandFull(f0)), f0), f0);
            if (form is ISeq s && Equals(s.first(), DoSym))   // 展开结果可能不是序列（(and) → true），同 driver.clj 的 seq?
            {
                object last = null;
                for (var r = s.next(); r != null; r = r.next()) last = EvalToplevel(TransferSource(r.first(), form));
                return last;
            }
        }
        return EvalForm(form);
    }

    private static readonly Symbol DoSym = Symbol.intern("do");

    private sealed class Thunk : AFn
    {
        private readonly Func<object> _f;
        public Thunk(Func<object> f) { _f = f; }
        public override object invoke() => _f();
    }

    private static object EvalForm(object form) =>
        DriverOps.RunForm.invoke(new Thunk(() =>
        {
            var l0 = Analyze(form);
            Accessors.RegisterEval(l0);   // driver.clj register-eval-accessors!（访问器内联，默认关）
            object v;
            // 简单形式直接解释、不生成程序集（driver.clj eval-l0）；两个驱动共用 DriverOps 的计数
            if (DriverOps.SimpleEvalEnabled && SimpleEval.Simple(l0, new HashSet<Symbol>()))
            {
                DriverOps.CountSimple();
                v = SimpleEval.Eval(l0, new Dictionary<Symbol, object>());
                State?.Unit?.Add(l0);   // 编镜像 / 包：求值之后记入单元（后序）
                return v;
            }
            DriverOps.CountCompiled();
            v = RunCompiled(l0);
            State?.Unit?.Add(l0);
            if (State?.Unit == null && State?.Boot != true && State?.Package == null) RegisterEvalInlines(l0, s_lastEvalUnit);
            return v;
        }), form);

    private static UnitDecl s_lastEvalUnit;

    /// <summary>L0 → 内存程序集 → 执行，值经 HoldResult 取回。</summary>
    internal static object RunCompiled(Expr l0)
    {
        string name = "eval$s" + Interlocked.Increment(ref s_evalCounter);
        var held = new CljCall("HoldResult", false, System.Collections.Immutable.ImmutableArray.Create(l0));
        var unit = CompileUnit(new[] { (Expr)held }, name, "#Module", true);
        s_lastEvalUnit = unit;
        var asm = IlEmitter.EmitInMemory(unit);
        DriverOps.RunModule.invoke(asm);
        return DriverOps.TakeResult.invoke();
    }

    /// <summary>一个编译单元：L0 形式 → L5。</summary>
    internal static UnitDecl CompileUnit(IReadOnlyList<Expr> l0s, string assemblyName, string moduleName, bool live)
    {
        var l3s = new List<Expr>();
        var loopFlags = new List<bool>();
        foreach (var l0 in l0s)
        {
            var st = new FrontState();
            l3s.Add(FrontEnd.Run(l0, st));
            loopFlags.Add(st.LoopsMade > 0 || TypedClauses(l0) > 0);   // pipeline.clj front-ends：生成了循环或有 ^long / ^double 形参的形式才做类型推断
        }
        return CodeGen.GenerateUnit(l3s, assemblyName, moduleName, live, loopFlags);
    }

    /// <summary>
    /// 形式里 ^long / ^double 形参的入口转换（(clj-call "CallStatic" 3 RT "longCast" …)）的个数（pipeline.clj typed-clauses；原版 parse-core 的 typed-count）。
    /// </summary>
    internal static int TypedClauses(Expr l0)
    {
        int n = l0 is CljCall { Op: "CallStatic" } cc && cc.Args.Length >= 2 && cc.Args[1] is Quote { Datum: "longCast" or "doubleCast" } ? 1 : 0;
        foreach (var k in LowerLoops.Kids(l0)) n += TypedClauses(k);
        return n;
    }

    /// <summary>逐个读形式、求值，*ns* 在外层绑定（driver.clj load-reader）；构建镜像时每个形式前重置 id，结束后记一个 (in-ns '原命名空间)。</summary>
    internal static object LoadReader(TextReader reader, string path, string name)
    {
        var r = reader as LineNumberingTextReader ?? new LineNumberingTextReader(reader);
        var eof = new object();
        Var.pushThreadBindings(RT.mapUniqueKeys(RT.CurrentNSVar, RT.CurrentNSVar.deref()));
        try
        {
            object last = null;
            while (true)
            {
                object form = Diagnostics.ReadForm(r, eof);
                if (ReferenceEquals(form, eof)) break;
                if (State?.Boot == true) ResetIds();
                last = EvalToplevel(form);
            }
            return last;
        }
        finally
        {
            Var.popThreadBindings();
            // 镜像里被加载文件的形式之后 *ns* 要恢复：记一个 (in-ns '原命名空间)（弹出绑定后的 *ns* 就是加载前的）
            if (State is { Boot: true, Unit: { } u })
                u.Add(Analyze(RT.list(Symbol.intern("clojure.core", "in-ns"), RT.list(Symbol.intern("quote"), ((Namespace)RT.CurrentNSVar.deref()).Name))));
        }
    }


}
