using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cleat.Compiler.Ir;

namespace Cleat.Compiler.Passes;

/// <summary>
/// 一个 pass（designs/30 §2 D2）：只做一件事，输入属于 <see cref="Input"/>、输出属于 <see cref="Output"/>（都是单个层）。
/// </summary>
public interface IPass
{
    /// <summary>pass 名（CLEAT_DUMP_IR 里用的名字），惯例与类名相同。</summary>
    string Name { get; }
    Langs Input { get; }
    Langs Output { get; }
    Node Run(Node ir);
}

/// <summary>管线的诊断选项：缺省从环境变量读（<see cref="FromEnvironment"/>），测试直接构造。</summary>
public sealed class PipelineOptions
{
    /// <summary>每个 pass 前后按层校验（<c>CLEAT_IR_CHECK=1</c>）。</summary>
    public bool Check { get; init; }

    /// <summary>
    /// 转储哪些 pass 之后的 IR（<c>CLEAT_DUMP_IR=pass,pass…</c>，逗号 / 空白分隔、按名字精确匹配、不区分大小写；<c>*</c> 或 <c>all</c> 表示全部；
    /// 名字 <c>input</c> 表示管线的输入）。
    /// </summary>
    public IReadOnlySet<string> Dump { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>转储写到哪里：<c>CLEAT_DUMP_IR_FILE</c> 设置时追加到该文件，否则标准错误。</summary>
    public TextWriter DumpTo { get; init; }

    public static PipelineOptions None { get; } = new PipelineOptions();

    private static PipelineOptions s_env;

    /// <summary>从环境变量读一次（进程内缓存）。</summary>
    public static PipelineOptions FromEnvironment() => s_env ??= Read(Environment.GetEnvironmentVariable);

    internal static PipelineOptions Read(Func<string, string> env)
    {
        string check = env("CLEAT_IR_CHECK");
        string dump = env("CLEAT_DUMP_IR");
        string file = env("CLEAT_DUMP_IR_FILE");
        return new PipelineOptions
        {
            Check = !string.IsNullOrEmpty(check) && check != "0",
            Dump = new HashSet<string>((dump ?? "").Split(new[] { ',', ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase),
            DumpTo = string.IsNullOrEmpty(file) ? null : new FileAppendWriter(file),
        };
    }

    internal bool Dumps(string name) => Dump.Count > 0 && (Dump.Contains(name) || Dump.Contains("*") || Dump.Contains("all"));

    // 每次转储打开、追加、关闭：编译可能发生在任意线程与进程的任意时刻，不持有文件句柄（调用方按 writer 加锁）
    private sealed class FileAppendWriter : StringWriter
    {
        private readonly string _path;
        public FileAppendWriter(string path) { _path = path; }

        public override void Flush()
        {
            var sb = GetStringBuilder();
            if (sb.Length == 0) return;
            File.AppendAllText(_path, sb.ToString());
            sb.Clear();
        }
    }
}

/// <summary>按序运行 pass；按选项在 pass 前后校验、转储。</summary>
public sealed class Pipeline
{
    private readonly IPass[] _passes;

    public Pipeline(params IPass[] passes)
    {
        _passes = passes ?? throw new ArgumentNullException(nameof(passes));
        for (int i = 1; i < _passes.Length; i++)
            if (_passes[i].Input != _passes[i - 1].Output)
                throw new ArgumentException($"pass {_passes[i].Name} expects {_passes[i].Input} but {_passes[i - 1].Name} produces {_passes[i - 1].Output}");
    }

    public IReadOnlyList<IPass> Passes => _passes;

    public Node Run(Node ir, PipelineOptions options = null)
    {
        options ??= PipelineOptions.FromEnvironment();
        if (_passes.Length == 0) return ir;
        if (options.Check) Validate(ir, _passes[0].Input, "input");
        if (options.Dumps("input")) DumpIr(options, "input", _passes[0].Input, ir);
        foreach (var p in _passes)
        {
            ir = p.Run(ir);
            if (options.Check) Validate(ir, p.Output, p.Name);
            if (options.Dumps(p.Name)) DumpIr(options, p.Name, p.Output, ir);
        }
        return ir;
    }

    private static void Validate(Node ir, Langs lang, string after)
    {
        var errors = IrValidator.Errors(ir, lang);
        if (errors.Count > 0)
            throw new IrValidationException(lang, errors.Prepend($"after {after}:").ToList());
    }

    private static void DumpIr(PipelineOptions options, string pass, Langs lang, Node ir)
    {
        var w = options.DumpTo ?? Console.Error;
        string text = $";; {pass} ({lang})\n{IrPrinter.ToText(ir)}\n";
        lock (w)
        {
            w.Write(text);
            w.Flush();
        }
    }
}
