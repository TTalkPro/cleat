using System;
using System.Collections.Generic;
using System.IO;
using Cleat.Scheme;

namespace Cleat.Stage0;

/// <summary>启动与入口：登记原语、提供 compile-core、读取并执行 Lcore 文件。</summary>
public static class Interpreter
{
    private static bool s_initialized;

    public static void Initialize()
    {
        if (s_initialized) return;
        s_initialized = true;
        PrimitiveRegistry.RegisterAll();
        Closure.ApplyPrimitive = (Procedure)Symbol.Intern("apply").Value;
        // 展开器的 eval 经 compile-core 执行展开结果：(compile-core 核心形式) → 无参过程
        Symbol.Intern("compile-core").Value = new CompileCore();
    }

    /// <summary>分析并执行一个顶层核心形式。</summary>
    public static object Eval(object form) => new Analyzer().AnalyzeTop(form).Eval(null);

    /// <summary>
    /// 读取并执行文件中的全部核心形式，返回形式个数。先全部分析，对顶层 library-letrec* 预链接
    /// （见 LibraryLetrec.Prelink），再按顺序执行。
    /// </summary>
    public static int RunFile(string path)
    {
        List<object> forms;
        using (var reader = new StreamReader(path))
            forms = new SchemeReader(reader, path, false).ReadAll();
        var analyzer = new Analyzer();
        var nodes = forms.ConvertAll(analyzer.AnalyzeTop);
        foreach (var n in nodes) (n as LibraryLetrec)?.Prelink();
        foreach (var n in nodes) n.Eval(null);
        return nodes.Count;
    }

    /// <summary>
    /// 在交互环境中求值一个 Scheme 表达式（经展开器的 eval-embedded，需先执行种子）。
    /// 不用 eval-r6rs：它用 clr-guard 吞掉 CLR 异常，load/program 的 ProgramFailedException 就传不出来。
    /// </summary>
    public static object EvalTopLevel(object expr)
    {
        var eval = Symbol.Intern("eval-embedded").Value as Procedure
          ?? throw new InvalidOperationException("eval-embedded is not defined (seed not loaded?)");
        return eval.Invoke1(expr);
    }

    public static void Report(Exception e)
    {
        Console.Out.Flush();
        if (e is SchemeException se)
        {
            Console.Error.WriteLine("stage0: unhandled condition:");
            object conds = se.Condition is Condition ? Conditions.SimpleConditions(se.Condition) : null;
            if (conds == null) Console.Error.WriteLine("  " + Show(se.Condition, 3));
            for (object p = conds; p is Cons c; p = c.cdr)
            {
                var r = (Record)c.car;
                Console.Error.WriteLine("  " + r.Rtd?.Name + ": " + string.Join(" ", Array.ConvertAll(r.Fields, f => Show(f, 3))));
            }
        }
        else Console.Error.WriteLine("stage0: " + e);
        var bt = Backtrace.For(e);
        if (bt.Count > 0) Console.Error.WriteLine("  in: " + string.Join(" <- ", bt));
    }

    /// <summary>出错信息用的简单打印（Scheme 打印器可能还没加载）。</summary>
    public static string Show(object x, int depth)
    {
        switch (x)
        {
            case null: return "#nil";
            case EmptyList: return "()";
            case string s: return "\"" + s + "\"";
            case Symbol s: return s.Name;
            case bool b: return b ? "#t" : "#f";
            case Cons c when depth > 0:
                var sb = new System.Text.StringBuilder("(");
                int n = 0;
                object p = c;
                for (; p is Cons pc && n < 12; p = pc.cdr, n++) sb.Append(n > 0 ? " " : "").Append(Show(pc.car, depth - 1));
                if (p is Cons) sb.Append(" ...");
                else if (p is not EmptyList) sb.Append(" . ").Append(Show(p, depth - 1));
                return sb.Append(')').ToString();
            case Cons _: return "(...)";
            case object[] v: return "#(" + (depth > 0 ? string.Join(" ", Array.ConvertAll(v.Length > 12 ? v[..12] : v, o => Show(o, depth - 1))) : "...") + ")";
            default: return x.ToString();
        }
    }

    private sealed class CompileCore : Procedure
    {
        public override string Name => "compile-core";
        public override object Invoke1(object expr) => new Thunk(new Analyzer().AnalyzeTop(expr));
    }

    private sealed class Thunk : Procedure
    {
        private readonly Node _node;
        public Thunk(Node node) { _node = node; }
        public override string Name => "compiled-core";
        public override object Invoke0() => _node.Eval(null);
    }
}
