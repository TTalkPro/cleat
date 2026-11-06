using System;
using System.Collections.Generic;

namespace Cleat.Compiler.Ir;

/// <summary>IR 不属于声明的层，或违反了该层的不变量。</summary>
public sealed class IrValidationException : Exception
{
    public IrValidationException(Langs lang, IReadOnlyList<string> errors)
        : base($"IR is not valid {lang}:\n  " + string.Join("\n  ", errors))
    {
        Lang = lang;
        Errors = errors;
    }

    public Langs Lang { get; }
    public IReadOnlyList<string> Errors { get; }
}

/// <summary>
/// 按层校验 IR（designs/30 §2 D3）：只出现本层的节点 + 本层的附加不变量。测试与 <c>CLEAT_IR_CHECK=1</c> 时管线在每个 pass 之后调用。
/// 附加不变量：
/// <list type="bullet">
/// <item>L1 起：letrec 只绑定 lambda。</item>
/// <item>L3 的 lambda 带自由变量表；L3 之前不带。</item>
/// <item>各层：begin 至少一个表达式；let / letrec 的变量与初值、try-catch 的类型与处理器一一对应；子节点不为 null。</item>
/// </list>
/// </summary>
public static class IrValidator
{
    private const int MaxErrors = 20;

    /// <summary>校验 node 属于 lang（单个层）；不通过时抛 <see cref="IrValidationException"/>。</summary>
    public static void Check(Node node, Langs lang)
    {
        var errors = Errors(node, lang);
        if (errors.Count > 0) throw new IrValidationException(lang, errors);
    }

    /// <summary>校验并返回错误表（最多 20 条）。</summary>
    public static IReadOnlyList<string> Errors(Node node, Langs lang)
    {
        if (lang == Langs.None || (lang & (lang - 1)) != 0) throw new ArgumentException($"expected a single language, got {lang}", nameof(lang));
        var v = new Checker(lang);
        if (node is null) v.Report(null, "null IR");
        else v.Walk(node);
        return v.Errors;
    }

    private sealed class Checker : Walker
    {
        private readonly Langs _lang;
        public readonly List<string> Errors = new List<string>();

        public Checker(Langs lang) { _lang = lang; }

        private bool AtLeast(Langs l) => (int)_lang >= (int)l;

        public void Report(Node n, string message)
        {
            if (Errors.Count >= MaxErrors) return;
            string where = n is null ? "" : n.Pos.IsKnown ? $" at {n.Pos.File}:{n.Pos.Line}:{n.Pos.Column}" : "";
            string text = n is null ? "" : IrPrinter.ToText(n);
            if (text.Length > 160) text = text.Substring(0, 160) + "…";
            Errors.Add(n is null ? message : $"{message}{where}: {text}");
        }

        public override void Walk(Node n)
        {
            if (Errors.Count >= MaxErrors) return;
            if (n is null) { Report(null, "null child node"); return; }
            if ((n.Langs & _lang) == 0) { Report(n, $"{n.Kind} is not a node of {_lang}"); return; }
            switch (n)
            {
                case Begin b when b.Body.IsDefaultOrEmpty:
                    Report(n, "begin needs at least one expression");
                    break;
                case Letrec lr:
                    Pairs(n, lr.Vars.Length, lr.Inits.Length, "letrec variables / initial values");
                    if (AtLeast(Langs.L1))
                        foreach (var e in lr.Inits)
                            if (e is not Lambda) Report(n, $"{_lang} letrec binds only lambda (got {e?.Kind.ToString() ?? "null"})");
                    break;
                case Let lt:
                    Pairs(n, lt.Vars.Length, lt.Inits.Length, "let variables / initial values");
                    break;
                case TryCatch tc:
                    Pairs(n, tc.Types.Length, tc.Handlers.Length, "try-catch types / handlers");
                    break;
                case Lambda l:
                    if (AtLeast(Langs.L3) && l.Free.IsDefault) Report(n, $"{_lang} lambda needs a free-variable list");
                    if (!AtLeast(Langs.L3) && !l.Free.IsDefault) Report(n, $"{_lang} lambda must not have a free-variable list (that is L3)");
                    break;
            }
            base.Walk(n);
        }

        private void Pairs(Node n, int a, int b, string what)
        {
            if (a != b) Report(n, $"{what} differ in length ({a} vs {b})");
        }
    }
}
