using System;
using System.IO;
using System.Text;

namespace Cleat.Scheme;

/// <summary>
/// (boot io ports) 用的宿主原语（iron-056，designs/11-portability.md §1 D3）：boot 不再直接用 clr-* 操作
/// Stream / TextReader / TextWriter，端口的每个宿主操作在这里包成一个 $port-* 原语。
/// 端口对象仍是 .NET 的 Stream（二进制）、TextReader / TextWriter（文本）与 CustomTextReaderWriter（文本双向）；
/// 取 reader / writer 时自动拆开 CustomTextReaderWriter。参数不做类型检查（$ 原语），错误是 .NET 异常，
/// 由 Scheme 侧的 with-clr-exception-handler 转成 &amp;i/o-port 条件。
/// </summary>
public static class PortPrimitives
{
    private static TextReader Reader(object port) => port is CustomTextReaderWriter c ? c.input : (TextReader)port;
    private static TextWriter Writer(object port) => port is CustomTextReaderWriter c ? c.output : (TextWriter)port;
    private static object Unspecified => Cleat.Scheme.Unspecified.Instance;

    #region 谓词与编解码器

    [Primitive("$codec?", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object IsCodec(object o) => RT.Bool(o is Encoding);

    [Primitive("$transcoder?", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object IsTranscoder(object o) => RT.Bool(o is Transcoder);

    [Primitive("$latin-1-codec", Flags = PrimitiveFlags.Unsafe)]
    public static object Latin1Codec() => Encoding.GetEncoding("iso-8859-1");

    [Primitive("$utf-8-codec", Flags = PrimitiveFlags.Unsafe)]
    public static object Utf8Codec() => new UTF8Encoding(false);

    [Primitive("$utf-16-codec", Flags = PrimitiveFlags.Unsafe)]
    public static object Utf16Codec() => new UnicodeEncoding(true, false);

    [Primitive("$make-transcoder", Flags = PrimitiveFlags.Unsafe)]
    public static object MakeTranscoder(object codec, object eolStyle, object handlingMode) =>
      new Transcoder((Encoding)codec, eolStyle, handlingMode);

    [Primitive("$native-transcoder", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object NativeTranscoder() => Transcoder.native;

    [Primitive("$transcoder-codec", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object TranscoderCodec(object tc) => ((Transcoder)tc).codec;

    [Primitive("$transcoder-eol-style", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object TranscoderEolStyle(object tc) => ((Transcoder)tc).eolstyle;

    [Primitive("$transcoder-error-handling-mode", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object TranscoderHandlingMode(object tc) => ((Transcoder)tc).handlingmode;

    /// <summary>端口的转码器，没有时 #f（文本双向端口取输出端的，再取输入端的）。</summary>
    [Primitive("$port-transcoder", Flags = PrimitiveFlags.Unsafe)]
    public static object PortTranscoder(object port) => port switch
    {
        TranscodedReader r => r.Transcoder,
        NonBufferedTranscodedReader r => r.Transcoder,
        TranscodedWriter w => w.Transcoder,
        CustomTextReaderWriter c => PortTranscoder(c.output) is Transcoder t ? t : PortTranscoder(c.input),
        _ => RT.False,
    };

    /// <summary>原生行尾：Windows 为 crlf，其他平台为 lf。</summary>
    [Primitive("$native-eol-style", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object NativeEolStyle() => Symbol.Intern(OperatingSystem.IsWindows() ? "crlf" : "lf");

    [Primitive("$textual-port?", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object IsTextualPort(object o) => RT.Bool(o is TextReader || o is TextWriter || o is CustomTextReaderWriter);

    [Primitive("$binary-port?", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object IsBinaryPort(object o) => RT.Bool(o is Stream);

    [Primitive("$binary-input-port?", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object IsBinaryInputPort(object o) => RT.Bool(o is Stream s && s.CanRead);

    [Primitive("$binary-output-port?", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object IsBinaryOutputPort(object o) => RT.Bool(o is Stream s && s.CanWrite);

    [Primitive("$input-port?", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object IsInputPort(object o) => RT.Bool((o is Stream s && s.CanRead) || o is TextReader || o is CustomTextReaderWriter);

    [Primitive("$output-port?", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object IsOutputPort(object o) => RT.Bool((o is Stream s && s.CanWrite) || o is TextWriter || o is CustomTextReaderWriter);

    /// <summary>文本双向端口（make-custom-textual-input/output-port 与双向文件的转码端口）。</summary>
    [Primitive("$custom-textual-port?", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object IsCustomTextualPort(object o) => RT.Bool(o is CustomTextReaderWriter);

    [Primitive("$custom-port-input", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object CustomPortInput(object o) => ((CustomTextReaderWriter)o).input;

    [Primitive("$custom-port-output", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object CustomPortOutput(object o) => ((CustomTextReaderWriter)o).output;

    [Primitive("$buffered-port?", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object IsBufferedPort(object o) => RT.Bool(o is BufferedStream);

    #endregion

    #region 打开与关闭

    [Primitive("$console-input-port", Flags = PrimitiveFlags.Unsafe)]
    public static object ConsoleInput() => Console.In;

    [Primitive("$console-output-port", Flags = PrimitiveFlags.Unsafe)]
    public static object ConsoleOutput() => Console.Out;

    [Primitive("$console-error-port", Flags = PrimitiveFlags.Unsafe)]
    public static object ConsoleError() => Console.Error;

    [Primitive("$standard-input-port", Flags = PrimitiveFlags.Unsafe)]
    public static object StandardInput() => Console.OpenStandardInput();

    [Primitive("$standard-output-port", Flags = PrimitiveFlags.Unsafe)]
    public static object StandardOutput() => Console.OpenStandardOutput();

    [Primitive("$standard-error-port", Flags = PrimitiveFlags.Unsafe)]
    public static object StandardError() => Console.OpenStandardError();

    /// <summary>文本输入文件（原生转码）。</summary>
    [Primitive("$open-input-file", Flags = PrimitiveFlags.Unsafe)]
    public static object OpenInputFile(object filename) => File.OpenText(filename.ToString());

    /// <summary>文本输出文件（原生转码）；append? 为真时追加。</summary>
    [Primitive("$open-output-file", Flags = PrimitiveFlags.Unsafe)]
    public static object OpenOutputFile(object filename, object append) =>
      RT.IsTrue(append) ? File.AppendText(filename.ToString()) : File.CreateText(filename.ToString());

    /// <summary>二进制输入文件；block? 为真时加缓冲。</summary>
    [Primitive("$open-file-input-port", Flags = PrimitiveFlags.Unsafe)]
    public static object OpenFileInputPort(object filename, object block)
    {
        Stream s = File.OpenRead(filename.ToString());
        return RT.IsTrue(block) ? new BufferedStream(s) : s;
    }

    [Primitive("$open-bytevector-input-port", Flags = PrimitiveFlags.Unsafe)]
    public static object OpenBytevectorInputPort(object bv) => new MemoryStream((byte[])bv, false);

    [Primitive("$open-string-input-port", Flags = PrimitiveFlags.Unsafe)]
    public static object OpenStringInputPort(object s) => new StringReader(s.ToString());

    [Primitive("$open-bytevector-output-port", Flags = PrimitiveFlags.Unsafe)]
    public static object OpenBytevectorOutputPort() => new WriteOnlyMemoryStream();

    /// <summary>取出字节向量输出端口已写的内容并清空（open-bytevector-output-port 的提取过程）。</summary>
    [Primitive("$bytevector-output-port-extract", Flags = PrimitiveFlags.Unsafe)]
    public static object BytevectorOutputPortExtract(object port)
    {
        var s = (MemoryStream)port;
        var r = s.ToArray();
        s.SetLength(0);
        return r;
    }

    [Primitive("$open-output-string", Flags = PrimitiveFlags.Unsafe)]
    public static object OpenOutputString() => new StringWriter();

    /// <summary>取出字符串输出端口已写的内容并清空。</summary>
    [Primitive("$get-output-string", Flags = PrimitiveFlags.Unsafe)]
    public static object GetOutputString(object port) => SchemeString.Normalize(((StringWriter)port).GetBuffer());

    [Primitive("$make-custom-binary-input-port", Flags = PrimitiveFlags.Unsafe)]
    public static object MakeCustomBinaryInputPort(object id, object read, object getPos, object setPos, object close) =>
      new CustomBinaryInputStream(id.ToString(), (Procedure)read, getPos, setPos, close);

    [Primitive("$make-custom-binary-output-port", Flags = PrimitiveFlags.Unsafe)]
    public static object MakeCustomBinaryOutputPort(object id, object write, object getPos, object setPos, object close) =>
      new CustomBinaryOutputStream(id.ToString(), (Procedure)write, getPos, setPos, close);

    [Primitive("$make-custom-binary-input/output-port", Flags = PrimitiveFlags.Unsafe)]
    public static object MakeCustomBinaryInputOutputPort(object id, object read, object write, object getPos, object setPos, object close) =>
      new CustomBinaryInputOutputStream(id.ToString(), (Procedure)read, (Procedure)write, getPos, setPos, close);

    [Primitive("$make-custom-textual-input-port", Flags = PrimitiveFlags.Unsafe)]
    public static object MakeCustomTextualInputPort(object id, object read, object getPos, object setPos, object close) =>
      new CustomTextReader(id.ToString(), (Procedure)read, getPos, setPos, close);

    [Primitive("$make-custom-textual-output-port", Flags = PrimitiveFlags.Unsafe)]
    public static object MakeCustomTextualOutputPort(object id, object write, object getPos, object setPos, object close) =>
      new CustomTextWriter(id.ToString(), (Procedure)write, getPos, setPos, close);

    [Primitive("$make-custom-textual-input/output-port", Flags = PrimitiveFlags.Unsafe)]
    public static object MakeCustomTextualInputOutputPort(object id, object read, object write, object getPos, object setPos, object close) =>
      new CustomTextReaderWriter(id,
        new CustomTextReader(id.ToString(), (Procedure)read, getPos, setPos, close),
        new CustomTextWriter(id.ToString(), (Procedure)write, getPos, setPos, close));

    /// <summary>二进制端口加转码器：可读可写 → 文本双向端口；只读 → 转码读取器；只写 → 转码写入器。</summary>
    [Primitive("$transcoded-port", Flags = PrimitiveFlags.Unsafe)]
    public static object TranscodedPort(object port, object transcoder)
    {
        var s = (Stream)port;
        var tc = (Transcoder)transcoder;
        if (s.CanRead)
            return s.CanWrite
              ? new CustomTextReaderWriter("textual/input-output-port", new NonBufferedTranscodedReader(s, tc), new TranscodedWriter(s, tc))
              : new TranscodedReader(s, tc);
        return new TranscodedWriter(s, tc);
    }

    /// <summary>关闭端口的输入端（Stream、TextReader，或文本双向端口的输入端）。</summary>
    [Primitive("$port-close-input", Flags = PrimitiveFlags.Unsafe)]
    public static object CloseInput(object port)
    {
        switch (port)
        {
            case Stream s: s.Close(); break;
            case TextReader r: r.Close(); break;
            case CustomTextReaderWriter c: c.input.Close(); break;
            default: return Errors.AssertionViolation("close-input-port", "not an input-port", port);
        }
        return Unspecified;
    }

    [Primitive("$port-close-output", Flags = PrimitiveFlags.Unsafe)]
    public static object CloseOutput(object port)
    {
        switch (port)
        {
            case Stream s: s.Close(); break;
            case TextWriter w: w.Close(); break;
            case CustomTextReaderWriter c: c.output.Close(); break;
            default: return Errors.AssertionViolation("close-output-port", "not an output-port", port);
        }
        return Unspecified;
    }

    [Primitive("$port-flush", Flags = PrimitiveFlags.Unsafe)]
    public static object Flush(object port)
    {
        switch (port)
        {
            case Stream s: s.Flush(); break;
            case TextWriter w: w.Flush(); break;
            case CustomTextReaderWriter c: c.output.Flush(); break;
            default: return Errors.AssertionViolation("flush-output-port", "not an output port", port);
        }
        return Unspecified;
    }

    #endregion

    #region 位置

    [Primitive("$port-has-position?", Flags = PrimitiveFlags.Unsafe)]
    public static object HasPosition(object port) => port switch
    {
        CustomTextWriter w => RT.Bool(w.HasPosition),
        CustomTextReader r => RT.Bool(r.HasPosition),
        CustomStream s => RT.Bool(s.HasPosition),
        Stream => RT.True,
        CustomTextReaderWriter c => HasPosition(c.input),
        TextWriter => RT.False,
        TextReader => RT.False,
        _ => Errors.AssertionViolation("port-has-port-position?", "not a port", port),
    };

    [Primitive("$port-position", Flags = PrimitiveFlags.Unsafe)]
    public static object Position(object port) => port switch
    {
        CustomTextReader r => r.HasPosition ? RT.Box(r.Position) : Errors.AssertionViolation("port-position", "not supplied to custom port", port),
        CustomTextWriter w => w.HasPosition ? RT.Box(w.Position) : Errors.AssertionViolation("port-position", "not supplied to custom port", port),
        CustomStream s => s.HasPosition ? Builtins.ToIntegerIfPossible(s.Position) : Errors.AssertionViolation("port-position", "not supplied to custom port", port),
        Stream s => Builtins.ToIntegerIfPossible(s.Position),
        CustomTextReaderWriter c => Position(c.input),
        _ => Errors.AssertionViolation("port-position", "not supported", port),
    };

    [Primitive("$port-has-set-position?", Flags = PrimitiveFlags.Unsafe)]
    public static object HasSetPosition(object port) => port switch
    {
        CustomTextWriter w => RT.Bool(w.HasSetPosition),
        CustomTextReader r => RT.Bool(r.HasSetPosition),
        Stream s => RT.Bool(s.CanSeek),
        CustomTextReaderWriter c => HasSetPosition(c.input),
        TextWriter => RT.False,
        TextReader => RT.False,
        _ => Errors.AssertionViolation("port-has-set-port-position!?", "not a port", port),
    };

    [Primitive("$port-set-position!", Flags = PrimitiveFlags.Unsafe)]
    public static object SetPosition(object port, object pos)
    {
        switch (port)
        {
            case CustomTextWriter w:
                if (!w.HasSetPosition) return Errors.AssertionViolation("set-port-position!", "not supplied to custom port", port);
                w.Position = (int)pos; break;
            case CustomTextReader r:
                if (!r.HasSetPosition) return Errors.AssertionViolation("set-port-position!", "not supplied to custom port", port);
                r.Position = (int)pos; break;
            case Stream s: s.Position = (int)pos; break;
            case CustomTextReaderWriter c: return SetPosition(c.input, pos);
            default: return Errors.AssertionViolation("set-port-position!", "not supported", port);
        }
        return Unspecified;
    }

    #endregion

    #region 读写

    /// <summary>读一个字节；文件尾为 -1。</summary>
    [Primitive("$port-read-byte", Flags = PrimitiveFlags.Unsafe)]
    public static object ReadByte(object port) => RT.Box(((Stream)port).ReadByte());

    /// <summary>读至多 count 个字节到 bv[start..]，返回读到的个数。</summary>
    [Primitive("$port-read-bytes", Flags = PrimitiveFlags.Unsafe)]
    public static object ReadBytes(object port, object bv, object start, object count) =>
      RT.Box(((Stream)port).Read((byte[])bv, (int)start, (int)count));

    /// <summary>bv 的前 k 个字节组成的新字节向量（读不满时截短缓冲区）。</summary>
    [Primitive("$bytevector-truncate", Flags = PrimitiveFlags.Unsafe)]
    public static object BytevectorTruncate(object bv, object k)
    {
        var r = new byte[(int)k];
        Array.Copy((byte[])bv, r, r.Length);
        return r;
    }

    /// <summary>写一个字节（0 ~ 255，超出报 &amp;assertion）。</summary>
    [Primitive("$port-write-byte", Flags = PrimitiveFlags.Unsafe)]
    public static object WriteByte(object port, object b)
    {
        if (b is not int k) return Errors.AssertionViolation("put-u8", "not a fixnum", b);
        if (k < 0 || k > 255) return Errors.AssertionViolation("put-u8", "too big or small for byte", b);
        ((Stream)port).WriteByte((byte)k);
        return Unspecified;
    }

    [Primitive("$port-write-bytes", Flags = PrimitiveFlags.Unsafe)]
    public static object WriteBytes(object port, object bv, object start, object count)
    {
        ((Stream)port).Write((byte[])bv, (int)start, (int)count);
        return Unspecified;
    }

    /// <summary>按码点读一个字符（代理对合成）；文件尾为 eof 对象。文本双向端口读输入端。</summary>
    [Primitive("$port-read-char", Flags = PrimitiveFlags.Unsafe)]
    public static object ReadChar(object port) => TextPorts.ReadChar(Reader(port));

    [Primitive("$port-peek-char", Flags = PrimitiveFlags.Unsafe)]
    public static object PeekChar(object port) => TextPorts.PeekChar(Reader(port));

    /// <summary>读至多 n 个字符（按码点）；没有字符时为 eof 对象。</summary>
    [Primitive("$port-read-string", Flags = PrimitiveFlags.Unsafe)]
    public static object ReadString(object port, object n) => TextPorts.ReadString(Reader(port), (int)n);

    /// <summary>读到末尾；没有字符时为 eof 对象。</summary>
    [Primitive("$port-read-all", Flags = PrimitiveFlags.Unsafe)]
    public static object ReadAll(object port) => TextPorts.ReadAll(Reader(port));

    [Primitive("$port-read-line", Flags = PrimitiveFlags.Unsafe)]
    public static object ReadLine(object port) => TextPorts.ReadLine(Reader(port));

    /// <summary>写一个字符串（SchemeString 或 .NET 字符串）。文本双向端口写输出端。</summary>
    [Primitive("$port-write-string", Flags = PrimitiveFlags.Unsafe)]
    public static object WriteString(object port, object s)
    {
        Writer(port).Write(s.ToString());
        return Unspecified;
    }

    [Primitive("$port-write-char", Flags = PrimitiveFlags.Unsafe)]
    public static object WriteChar(object port, object ch)
    {
        Writer(port).Write(ch.ToString());
        return Unspecified;
    }

    #endregion
}
