using System;
using System.IO;
using System.Text.RegularExpressions;

namespace Cleat.Scheme;

/// <summary>reader 相关原语（从旧 Ports.cs、Numbers.cs 转来，iron-008 → iron-010）。</summary>
public partial class Builtins
{
    private static string PortFileName(TextReader r)
    {
        Stream baseStream = r switch { StreamReader sr => sr.BaseStream, TranscodedReader tr => tr.BaseStream, _ => null };
        if (baseStream is FileStream fs)
        {
            string name = fs.Name;
            string cwd = Environment.CurrentDirectory + Path.DirectorySeparatorChar;
            return name.StartsWith(cwd, StringComparison.Ordinal) ? name.Substring(cwd.Length) : name;
        }
        return "";
    }

    /// <summary>从端口读一个数据。read（在 Scheme 中定义）= read-annotated 后去掉注解。</summary>
    public static object ReadFromPort(object port, bool annotate)
    {
        var r = RequiresNotNull<TextReader>(port);
        string fn = PortFileName(r);
        try
        {
            return new SchemeReader(r, fn, annotate).Read();
        }
        catch (ReaderException ex)
        {
            return LexicalError($"{ex.Message} at ({ex.Line},{ex.Column})", fn.Length > 0 ? fn : (object)"(port)");
        }
        catch (ObjectDisposedException)
        {
            return IOPortViolation("read", "port has already been closed", port);
        }
        catch (IOException ex)
        {
            return IOPortViolation("read", ex.Message, port);
        }
    }

    [Primitive("read-annotated")]
    public static object ReadAnnotated(object port) => ReadFromPort(port, annotate: true);

    [Primitive("make-annotation")]
    public static object MakeAnnotation(object expr, object source, object stripped) =>
      expr is Cleat.Scheme.Cons c && stripped is Cleat.Scheme.Cons s
        ? new ConsAnnotation(c, source, s)
        : new Annotation(expr, source, stripped);

    [Primitive("string->number", Flags = PrimitiveFlags.Pure)]
    public static object StringToNumber(object obj) => ParseNumber(RequiresNotNull<string>(obj)) ?? FALSE;

    /// <summary>radix 只接受 2、8、10、16（R6RS；其他值报 &amp;assertion，iron-072 对拍 Chez mats）；文本中的前缀优先于 radix。</summary>
    [Primitive("string->number", Flags = PrimitiveFlags.Pure)]
    public static object StringToNumber(object obj, object radix)
    {
        string s = RequiresNotNull<string>(obj);
        int r = RequiresNotNull<int>(radix);
        if (r != 2 && r != 8 && r != 10 && r != 16) return AssertionViolation("string->number", "not a valid radix", radix);
        return ParseNumber(s, r) ?? FALSE;
    }

    private static readonly Regex s_locationPattern = new Regex(@"\((?<sl>\d+),(?<sc>\d+)\) - \((?<el>\d+),(?<ec>\d+)\)", RegexOptions.Compiled);

    /// <summary>
    /// "(行,列) - (行,列)" → ((起始行 . 起始列) (结束行 . 结束列))。
    /// nanopass 的覆盖适配库 lib/nanopass/implementation-helpers.cleat-scheme.sls 按名字调用 Cleat.Scheme.Builtins.SourceLocation。
    /// </summary>
    public static object SourceLocation(string location)
    {
        var m = s_locationPattern.Match(location ?? "");
        if (!m.Success) return FALSE;
        int I(string g) => int.Parse(m.Groups[g].Value);
        return Cleat.Scheme.Cons.FromArray(
          new Cleat.Scheme.Cons(I("sl"), I("sc")),
          new Cleat.Scheme.Cons(I("el"), I("ec")));
    }
}
