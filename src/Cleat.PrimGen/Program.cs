using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Cleat.Core.Runtime;
using Cleat.Scheme;

namespace Cleat.PrimGen;

/// <summary>
/// 原语表生成（designs/02 §5、15 §1；从 IronScheme.PrimGen 合并）。两张表、两个文件：
/// <list type="bullet">
/// <item><c>src/boot/primitives.ss</c>（Scheme 编译器读）：反射 Cleat.Scheme 的全部 <c>[Primitive]</c> 方法，再附上 Cleat.Core 的
///   <c>Primitives.Table</c>（名字 <c>cleat.kernel/…</c>），格式 <c>(primitive 名字 (overload "类型" "方法" 固定参数个数 剩余参数? (标志 …) 内联模板) …)</c>；</item>
/// <item><c>src/boot/primitives.edn</c>（工具用）：Cleat.Core 的表，<see cref="Primitives.ToEdn"/>。</item>
/// </list>
/// 用法：<c>primgen [primitives.edn 路径] [primitives.ss 路径]</c>（缺省相对仓库根目录）；<c>primgen --check &lt;primitives.ss&gt;</c> 检查已有表是否最新（不一致返回 1）。
/// </summary>
public static class Program
{
    /// <summary>编译器专门处理的原语（IronScheme 03 §6）。种类名供编译器分派使用。async 的 await 已随 async 删除；call/cc 待 cleat-047 删除。</summary>
    private static readonly (string Name, string Kind, string Note)[] s_specials =
    {
        ("$and?", "and", "短路与，参数为未求值表达式，结果为 CLR bool"),
        ("$or?", "or", "短路或，参数为未求值表达式，结果为 CLR bool"),
        ("$break", "break", "调用 Debugger.Break"),
        ("$throw", "throw", "抛出参数（一个 .NET 异常对象）"),
        ("$try/finally", "try-finally", "($try/finally body finalizer)：finalizer 在 finally 中求值"),
        ("call/cc", "escape-frame", "逃逸续延捕获帧（IronScheme 02 §6；cleat-047 删除）"),
        ("call-with-current-continuation", "escape-frame", "同 call/cc"),
        ("call-with-values", "call-with-values", "消费者为字面 lambda 时可内联"),
        ("apply", "apply", "展开最后一个列表参数"),
        ("values", "values", "在 Scheme 中定义（boot/exceptions.sls）；作为原语出现时编译为多值构造"),
    };

    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 2 && args[0] == "--check")
            {
                string expected = Generate();
                string actual = File.Exists(args[1]) ? File.ReadAllText(args[1]) : "";
                if (expected == actual) { Console.WriteLine("primitives table is up to date"); return 0; }
                Console.Error.WriteLine("primitives table is out of date: regenerate with primgen");
                return 1;
            }
            if (args.Length > 2)
            {
                Console.Error.WriteLine("usage: primgen [<primitives.edn> [<primitives.ss>]] | --check <primitives.ss>");
                return 2;
            }
            string root = FindRoot();
            string edn = args.Length > 0 ? args[0] : Path.Combine(root, "src", "boot", "primitives.edn");
            string ss = args.Length > 1 ? args[1] : Path.Combine(root, "src", "boot", "primitives.ss");
            WriteIfChanged(edn, Primitives.ToEdn());
            string table = Generate();
            Verify(table);
            WriteIfChanged(ss, table);
            Console.WriteLine($"primgen: {Primitives.Table.Count} kernel primitives -> {edn}; {Cleat.Scheme.PrimitiveRegistry.All.Count} scheme + {Primitives.Table.Select(e => e.Name).Distinct().Count()} kernel primitives, {s_specials.Length} specials -> {ss}");
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("primgen: " + e.Message);
            return 1;
        }
    }

    /// <summary>内容不变时不写文件（增量构建不改时间戳）。</summary>
    private static void WriteIfChanged(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
        if (!File.Exists(path) || File.ReadAllText(path) != text) File.WriteAllText(path, text, new UTF8Encoding(false));
    }

    private static string FindRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "Cleat.slnx"))) return d.FullName;
        return Directory.GetCurrentDirectory();
    }

    private static string Flags(PrimitiveFlags f)
    {
        var names = new List<string>();
        if (f.HasFlag(PrimitiveFlags.Pure)) names.Add("pure");
        if (f.HasFlag(PrimitiveFlags.Unsafe)) names.Add("unsafe");
        if (f.HasFlag(PrimitiveFlags.AllowTailCall)) names.Add("allow-tail-call");
        return "(" + string.Join(" ", names) + ")";
    }

    private static string Str(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string Overload(string type, string method, int fixedArity, bool hasRest, string flags, string inline) =>
      "\n    (overload " + Str(type) + ' ' + Str(method) + ' ' + fixedArity + ' ' + (hasRest ? "#t" : "#f") + ' ' + flags + ' ' + inline + ')';

    /// <summary>Scheme 编译器读的原语表：Cleat.Scheme 的 [Primitive] 原语 + Cleat.Core 的 kernel 表 + special。</summary>
    public static string Generate()
    {
        var sb = new StringBuilder();
        sb.Append(";;; 原语表：由 Cleat.PrimGen 从 Cleat.Scheme（[Primitive]）与 Cleat.Core（Primitives.Table，名字 cleat.kernel/…）生成，请勿手工修改。\n");
        sb.Append(";;; 设计：designs/02-runtime.md §5、designs/15 §1；IronScheme 03 §6\n");
        sb.Append(";;;\n");
        sb.Append(";;; (primitive 名字 重载 …)\n");
        sb.Append(";;;   重载 = (overload \"类型全名\" \"方法名\" 固定参数个数 剩余参数? (标志 …) 内联模板)\n");
        sb.Append(";;;   标志：pure（可常量折叠）、unsafe（不做类型检查）、allow-tail-call、inline（kernel 表：发射器有内联模板）；内联模板为名字或 #f\n");
        sb.Append(";;; (special 名字 种类)：编译器专门处理的原语；同名的 primitive 条目（若有）是非内联时的后备实现\n");
        sb.Append("(primitive-table 1\n");

        // 两张表按名字合并排序（Scheme 原语名与 cleat.kernel/… 不会重名）
        var entries = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in Cleat.Scheme.PrimitiveRegistry.All)
        {
            var e = new StringBuilder();
            // 重载按（固定参数个数、是否剩余参数、类型名、方法名）排序，保证输出稳定
            foreach (var o in kv.Value.OrderBy(o => o.FixedArity).ThenBy(o => o.HasRest).ThenBy(o => o.Method.DeclaringType.FullName, StringComparer.Ordinal).ThenBy(o => o.Method.Name, StringComparer.Ordinal))
                e.Append(Overload(o.Method.DeclaringType.FullName, o.Method.Name, o.FixedArity, o.HasRest, Flags(o.Attribute.Flags), o.Attribute.Inline ?? "#f"));
            entries.Add(kv.Key, e.ToString());
        }
        foreach (var g in Primitives.Table.GroupBy(e => e.Name))
        {
            var e = new StringBuilder();
            foreach (var p in g.OrderBy(p => p.Params.Length).ThenBy(p => p.Type.FullName, StringComparer.Ordinal).ThenBy(p => p.Method, StringComparer.Ordinal))
            {
                var ps = p.MethodInfo.GetParameters();
                bool rest = ps.Length > 0 && ps[^1].IsDefined(typeof(ParamArrayAttribute), false);
                var flags = new List<string>();
                if (p.Pure) flags.Add("pure");
                if (p.Inline) flags.Add("inline");
                e.Append(Overload(p.Type.FullName, p.Method, rest ? ps.Length - 1 : ps.Length, rest, "(" + string.Join(" ", flags) + ")", "#f"));
            }
            entries.Add("cleat.kernel/" + g.Key, e.ToString());
        }
        foreach (var kv in entries) sb.Append("  (primitive ").Append(kv.Key).Append(kv.Value).Append(")\n");
        foreach (var s in s_specials.OrderBy(s => s.Name, StringComparer.Ordinal))
            sb.Append("  (special ").Append(s.Name).Append(' ').Append(s.Kind).Append(")   ; ").Append(s.Note).Append('\n');
        sb.Append(")\n");
        return sb.ToString();
    }

    /// <summary>用 Scheme reader 读回，确认每个名字都按原样读成符号。</summary>
    private static void Verify(string text)
    {
        var table = (Cons)new SchemeReader(new StringReader(text), "primitives.ss", false).Read();
        var names = new List<string>();
        for (object x = ((Cons)table.cdr).cdr; x is Cons c; x = c.cdr)
            names.Add(((Symbol)((Cons)((Cons)c.car).cdr).car).Name);
        var expected = Cleat.Scheme.PrimitiveRegistry.All.Keys.Concat(Primitives.Table.Select(e => "cleat.kernel/" + e.Name).Distinct())
          .OrderBy(n => n, StringComparer.Ordinal)
          .Concat(s_specials.Select(s => s.Name).OrderBy(n => n, StringComparer.Ordinal)).ToList();
        if (!names.SequenceEqual(expected))
            throw new InvalidOperationException("primitive names do not round-trip through the reader");
    }
}
