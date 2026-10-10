using System.Text;

namespace Cleat.Scheme;

/// <summary>字符串输出端口的底层写入器：GetBuffer 取出已写内容并清空（open-string-output-port 使用）。</summary>
public sealed class StringWriter : System.IO.StringWriter
{
    public string GetBuffer()
    {
        StringBuilder sb = GetStringBuilder();
        string r = sb.ToString();
        sb.Length = 0;
        return r;
    }
}

public partial class Builtins
{
    private sealed class Eof
    {
        public override string ToString() => "#<eof>";
    }

    protected internal static readonly object EOF = new Eof();

    [Primitive("eof-object")]
    public static object EofObject() => EOF;

    [Primitive("eof-object?")]
    public static object IsEof(object obj) => GetBool(obj == EOF);
}
