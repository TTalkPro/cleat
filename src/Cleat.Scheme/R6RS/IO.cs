#region License
/* Copyright (c) 2007-2016 Llewellyn Pritchard
 * All rights reserved.
 * This source code is subject to terms and conditions of the BSD License.
 * See docs/license.txt. */
#endregion

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace Cleat.Scheme;


#region  Support classes

public class Transcoder
{
    public Encoding codec = Encoding.Default;
    // 原生行尾：Windows 为 crlf，其他平台为 lf（与 native-eol-style 一致，iron-054）
    public object eolstyle = Symbol.Intern(OperatingSystem.IsWindows() ? "crlf" : "lf");
    public object handlingmode = Symbol.Intern("replace");

    public static readonly Transcoder native;

    static Transcoder()
    {
        native = new Transcoder();
    }

    internal Transcoder()
    {

    }

    public Transcoder(Encoding codec, object eolstyle, object handlingmode)
    {
        this.codec = codec;
        this.eolstyle = eolstyle;
        this.handlingmode = handlingmode;

        if (handlingmode == Symbol.Intern("raise"))
        {
            this.codec = (Encoding)this.codec.Clone();
            this.codec.EncoderFallback = new EncCB(null);
            this.codec.DecoderFallback = new DecCB(null);
        }
        else if (handlingmode == Symbol.Intern("replace"))
        {
            this.codec = (Encoding)this.codec.Clone();
            this.codec.EncoderFallback = EncoderFallback.ReplacementFallback;
            this.codec.DecoderFallback = DecoderFallback.ReplacementFallback;
        }
    }
}

[CLSCompliant(false)]
public class EncCB : EncoderFallback
{
    private Procedure _error;
    public EncCB(Procedure error)
    {
        _error = error;
    }

    public override EncoderFallbackBuffer CreateFallbackBuffer()
    {
        if (_error == null)
        {
            Builtins.IOEncodingError();
        }
        else
        {
            _error.Call();
        }
        return null;
    }

    public override int MaxCharCount
    {
        get { return 1; }
    }
}

[CLSCompliant(false)]
public class DecCB : DecoderFallback
{
    private Procedure _error;
    public DecCB(Procedure error)
    {
        _error = error;
    }

    public override DecoderFallbackBuffer CreateFallbackBuffer()
    {
        if (_error == null)
        {
            Builtins.IODecodingError();
        }
        else
        {
            _error.Call();
        }
        return null;
    }

    public override int MaxCharCount
    {
        get { return 1; }
    }
}



public abstract class CustomStream : Stream
{
    public abstract bool HasPosition { get; }
}

[CLSCompliant(false)]
public class CustomBinaryInputStream : CustomStream
{
    private string _id;
    private Procedure _read, _getPos, _setPos, _close;

    public CustomBinaryInputStream(string id, Procedure read, object get_pos, object set_pos, object close)
    {
        _id = id;
        _read = read;
        // rest optional
        _getPos = get_pos as Procedure;
        _setPos = set_pos as Procedure;
        _close = close as Procedure;
    }

    public override bool CanRead
    {
        get { return true; }
    }

    public override bool CanSeek
    {
        get { return _setPos != null; }
    }

    public override bool CanWrite
    {
        get { return false; }
    }

    public override void Flush()
    {

    }

    public override bool HasPosition
    {
        get { return _getPos != null; }
    }

    public override long Length
    {
        get { throw new Exception("The method or operation is not implemented."); }
    }

    public override long Position
    {
        get
        {
            if (_getPos != null)
            {
                return Convert.ToInt64(_getPos.Call());
            }
            return (int)Builtins.AssertionViolation("port-position", "not supported");
        }
        set
        {
            if (_setPos != null)
            {
                _setPos.Call((int)value);
            }
            else
            {
                Builtins.AssertionViolation("set-port-position!", "not supported");
            }
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return (int)_read.Call(buffer, offset, count);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        if (_setPos != null)
        {
            _setPos.Call((int)offset);
            return offset;
        }
        return (long)Builtins.AssertionViolation("set-port-position!", "not supported");
    }

    public override void SetLength(long value)
    {
        throw new Exception("The method or operation is not implemented.");
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        Builtins.AssertionViolation("write", "cannot write to input port");
    }

    public override string ToString()
    {
        return string.Format("#<custom-binary-input-port id: {0}>", _id);
    }

    public override void Close()
    {
        if (_close != null)
        {
            _close.Call();
        }
    }
}

[CLSCompliant(false)]
public class CustomTextReader : TextReader
{
    private string _id;
    private Procedure _read, _getPos, _setPos, _close;

    public CustomTextReader(string id, Procedure read, object get_pos, object set_pos, object close)
    {
        _id = id;
        _read = read;
        // rest optional
        _getPos = get_pos as Procedure;
        _setPos = set_pos as Procedure;
        _close = close as Procedure;
    }

    public override int Read(char[] buffer, int index, int count)
    {
        StringBuilder sb = new StringBuilder(new string(buffer));

        int res = (int)_read.Call(sb, index, count);

        for (int i = 0; i < res; i++)
        {
            buffer[i + index] = sb[i + index];
        }

        return res;
    }

    public override int Peek()
    {
        int p = Position;
        int i = Read();
        Position = p;
        return i;
    }

    public override int Read()
    {
        char[] c = new char[1];
        int i = Read(c, 0, 1);
        if (i == 0)
        {
            return -1;
        }
        return c[0];
    }

    public bool HasPosition
    {
        get { return _getPos != null; }
    }

    public bool HasSetPosition
    {
        get { return _setPos != null; }
    }

    public int Position
    {
        get
        {
            if (_getPos != null)
            {
                return Convert.ToInt32(_getPos.Call());
            }

            return (int)Builtins.AssertionViolation("port-position", "not supported");
        }
        set
        {
            if (_setPos != null)
            {
                _setPos.Call(value);
            }
            else
            {
                Builtins.AssertionViolation("set-port-position!", "not supported");
            }
        }
    }

    public override string ToString()
    {
        return string.Format("#<custom-textual-input-port id: {0}>", _id);
    }

    public override void Close()
    {
        if (_close != null)
        {
            _close.Call();
        }
    }

}

[CLSCompliant(false)]
public class CustomBinaryOutputStream : CustomStream
{
    private string _id;
    private Procedure _write, _getPos, _setPos, _close;

    public CustomBinaryOutputStream(string id, Procedure write, object get_pos, object set_pos, object close)
    {
        _id = id;
        _write = write;
        // rest optional
        _getPos = get_pos as Procedure;
        _setPos = set_pos as Procedure;
        _close = close as Procedure;
    }

    public override bool CanRead
    {
        get { return false; }
    }

    public override bool CanSeek
    {
        get { return _setPos != null; }
    }

    public override bool CanWrite
    {
        get { return true; }
    }

    public override bool HasPosition
    {
        get { return _getPos != null; }
    }

    public override void Flush()
    {

    }

    public override long Length
    {
        get { throw new Exception("The method or operation is not implemented."); }
    }

    public override long Position
    {
        get
        {
            if (_getPos != null)
            {
                return Convert.ToInt64(_getPos.Call());
            }
            return (int)Builtins.AssertionViolation("port-position", "not supported");
        }
        set
        {
            if (_setPos != null)
            {
                _setPos.Call((int)value);
            }
            else
            {
                Builtins.AssertionViolation("set-port-position!", "not supported");
            }
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        Builtins.AssertionViolation("read", "cannot read from output port");
        return 0;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        if (_setPos != null)
        {
            _setPos.Call((int)offset);
            return offset;
        }
        return (long)Builtins.AssertionViolation("set-port-position!", "not supported");
    }

    public override void SetLength(long value)
    {
        throw new Exception("The method or operation is not implemented.");
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        if (HasPosition)
        {
            long pos = Position;
            int written = Convert.ToInt32(_write.Call(buffer, offset, count));
            if (written != count && written != (Position - pos))
            {
                Builtins.AssertionViolation("write!", "position does not match number of bytes written");
            }
        }
        else
        {
            int written = Convert.ToInt32(_write.Call(buffer, offset, count));
            if (written != count)
            {
                Builtins.AssertionViolation("write!", "could not write all the bytes");
            }
        }

    }

    public override string ToString()
    {
        return string.Format("#<custom-binary-output-port id: {0}>", _id);
    }

    public override void Close()
    {
        if (_close != null)
        {
            _close.Call();
        }
    }
}

[CLSCompliant(false)]
public class CustomTextWriter : TextWriter
{
    private string _id;
    private Procedure _write, _getPos, _setPos, _close;

    public CustomTextWriter(string id, Procedure write, object get_pos, object set_pos, object close)
    {
        _id = id;
        _write = write;
        // rest optional
        _getPos = get_pos as Procedure;
        _setPos = set_pos as Procedure;
        _close = close as Procedure;
    }

    public override void Write(char[] buffer, int index, int count)
    {
        if (HasPosition)
        {
            long pos = Position;
            int written = WriteInternal(buffer, index, count);
            if (written != count && written != (Position - pos))
            {
                Builtins.AssertionViolation("write!", "position does not match number of characters written");
            }
        }
        else
        {
            int written = WriteInternal(buffer, index, count);
            if (written != count)
            {
                Builtins.AssertionViolation("write!", "could not write all the characters");
            }
        }
    }

    private int WriteInternal(char[] buffer, int index, int count)
    {
        StringBuilder sb = new StringBuilder(new string(buffer));

        int written = Convert.ToInt32(_write.Call(sb, index, count));

        for (int i = 0; i < count; i++)
        {
            buffer[i] = sb[i];
        }

        return written;
    }

    public override void Write(char value)
    {
        Write(new char[] { value }, 0, 1);
    }

    public bool HasPosition
    {
        get { return _getPos != null; }
    }

    public bool HasSetPosition
    {
        get { return _setPos != null; }
    }

    public int Position
    {
        get
        {
            if (_getPos != null)
            {
                return Convert.ToInt32(_getPos.Call());
            }
            return (int)Builtins.AssertionViolation("port-position", "not supported");
        }
        set
        {
            if (_setPos != null)
            {
                _setPos.Call(value);
            }
            else
            {
                Builtins.AssertionViolation("set-port-position!", "not supported");
            }
        }
    }

    public override string ToString()
    {
        return string.Format("#<custom-textual-output-port id: {0}>", _id);
    }

    public override void Close()
    {
        if (_close != null)
        {
            _close.Call();
        }
    }


    public override Encoding Encoding
    {
        get { return Encoding.Default; }
    }
}

[CLSCompliant(false)]
public class TranscodedWriter : StreamWriter
{
    private Transcoder _tc;
    private Stream _port;

    public Transcoder Transcoder
    {
        get { return _tc; }
    }

    public TranscodedWriter(Stream s, Transcoder tc) : base(s, tc.codec)
    {
        NewLine = IO.GetNewline(tc.eolstyle, "\n");
        _tc = tc;
        _port = s;
        AutoFlush = true;
    }

    public override void Close()
    {
        _port.Close();
    }

    public override void Flush()
    {
        _port.Flush();
    }

    public override void Write(string value)
    {
        byte[] bytes = IO.StringToByteVector(value, _tc) as byte[];
        _port.Write(bytes, 0, bytes.Length);
    }

    public Stream BinaryPort
    {
        get { return _port; }
    }
}

[CLSCompliant(false)]
public class TranscodedReader : StreamReader
{
    private Stream _port;
    private Transcoder _tc;

    public Transcoder Transcoder
    {
        get { return _tc; }
    }


    public TranscodedReader(Stream s, Transcoder tc) : base(s, tc.codec, true)
    {
        _tc = tc;
        _port = s;
    }

    public override void Close()
    {
        _port.Close();
    }

    public override string ReadToEnd()
    {
        string value = base.ReadToEnd();
        if (value != null)
        {
            value = IO.s_eoltx.Replace(value, delegate (Match m)
            {
                return IO.GetNewline(_tc.eolstyle, m.Value);
            });
        }
        return value;
    }

    public override string ReadLine()
    {
        string value = base.ReadLine();
        if (value != null)
        {
            value = IO.s_eoltx.Replace(value, delegate (Match m)
            {
                return IO.GetNewline(_tc.eolstyle, m.Value);
            });
        }
        return value;
    }

    public Stream BinaryPort
    {
        get { return _port; }
    }
}

// TODO: fix/improve this monster
[CLSCompliant(false)]
public class NonBufferedTranscodedReader : TextReader
{
    private static readonly object s_hmReplace = Symbol.Intern("replace");
    private static readonly object s_hmIgnore = Symbol.Intern("ignore");

    private Stream _port;
    private Transcoder _tc;

    public Transcoder Transcoder
    {
        get { return _tc; }
    }

    public NonBufferedTranscodedReader(Stream s, Transcoder tc)
    {
        _tc = tc;
        _port = s;
    }

    public override void Close()
    {
        _port.Close();
    }

    public override int Peek()
    {
        long p = _port.Position;
        int r = Read();
        _port.Position = p;
        return r;
    }

    public override int Read()
    {
        if (_port.Position == 0)
        {
            // CHECKME: should the codec not be assigned, or did it conflict with the spec?
            Encoding enc = ParsePreamble(_port);
        }

        long p = _port.Position;
        int max = _tc.codec.GetMaxByteCount(1);
        byte[] buffer = new byte[max];
        int i = _port.Read(buffer, 0, buffer.Length);
        if (i == 0)
        {
            return -1;
        }
        char[] chars = _tc.codec.GetChars(buffer, 0, i);
        if (chars[0] == '\ufffd')
        {
            if (_tc.handlingmode == s_hmReplace)
            {
                _port.Position = p + 1;
                return chars[0];
            }
            if (_tc.handlingmode == s_hmIgnore)
            {
                _port.Position = p + 1;
                return Read();
            }
        }
        int len = _tc.codec.GetByteCount(chars, 0, 1);
        _port.Position = p + len;
        return chars[0];
    }

    private Encoding ParsePreamble(Stream port)
    {
        byte[] b = new byte[4];
        int len = port.Read(b, 0, 4);

        if (b[0] == 0xff && b[1] == 0xfe && (len < 4 || !(b[2] == 0 && b[3] == 0)))
        {
            port.Position = 2;
            return new UnicodeEncoding(false, false);
        }
        else if (b[0] == 0xfe && b[1] == 0xff && (len < 4 || !(b[2] == 0 && b[3] == 0)))
        {
            port.Position = 2;
            return new UnicodeEncoding(true, false);
        }
        else if (len >= 4 && b[0] == 0 && b[1] == 0 && b[2] == 0xfe && b[3] == 0xff)
        {
            port.Position = 4;
            return new UTF32Encoding(false, false);
        }
        else if (len >= 4 && b[0] == 0xff && b[1] == 0xef && b[2] == 0 && b[3] == 0)
        {
            port.Position = 4;
            return new UTF32Encoding(true, false);
        }
        else
        {
            port.Position = 0;
            return Encoding.UTF8;
        }
    }

    public override string ReadToEnd()
    {
        string value = base.ReadToEnd();
        if (value != null)
        {
            value = IO.s_eoltx.Replace(value, delegate (Match m)
            {
                return IO.GetNewline(_tc.eolstyle, m.Value);
            });
        }
        return value;
    }


    public override string ReadLine()
    {
        string value = base.ReadLine();
        if (value != null)
        {
            value = IO.s_eoltx.Replace(value, delegate (Match m)
            {
                return IO.GetNewline(_tc.eolstyle, m.Value);
            });
        }
        return value;
    }

    public Stream BinaryPort
    {
        get { return _port; }
    }
}

[CLSCompliant(false)]
public class CustomBinaryInputOutputStream : CustomStream
{
    private string _id;
    private Procedure _read, _write, _getPos, _setPos, _close;

    public CustomBinaryInputOutputStream(string id, Procedure read, Procedure write, object get_pos, object set_pos, object close)
    {
        _id = id;
        _read = read;
        _write = write;
        // rest optional
        _getPos = get_pos as Procedure;
        _setPos = set_pos as Procedure;
        _close = close as Procedure;
    }

    public override bool CanRead
    {
        get { return true; }
    }

    public override bool CanSeek
    {
        get { return _setPos != null; }
    }

    public override bool CanWrite
    {
        get { return true; }
    }

    public override void Flush()
    {

    }

    public override bool HasPosition
    {
        get { return _getPos != null; }
    }

    public override long Length
    {
        get { throw new Exception("The method or operation is not implemented."); }
    }

    public override long Position
    {
        get
        {
            if (_getPos != null)
            {
                return Convert.ToInt64(_getPos.Call());
            }
            Builtins.AssertionViolation("get-position", "not supported");
            return 0;
        }
        set
        {
            if (_setPos != null)
            {
                _setPos.Call((int)value);
            }
            else
            {
                Builtins.AssertionViolation("set-port-position!", "not supported");
            }
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return (int)_read.Call(buffer, offset, count);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        if (_setPos != null)
        {
            _setPos.Call((int)offset);
            return offset;
        }
        return (long)Builtins.AssertionViolation("set-port-position!", "not supported");
    }

    public override void SetLength(long value)
    {
        throw new Exception("The method or operation is not implemented.");
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        if (HasPosition)
        {
            long pos = Position;
            int written = Convert.ToInt32(_write.Call(buffer, offset, count));
            if (written != count && written != (Position - pos))
            {
                Builtins.AssertionViolation("write!", "position does not match number of bytes written");
            }
        }
        else
        {
            int written = Convert.ToInt32(_write.Call(buffer, offset, count));
            if (written != count)
            {
                Builtins.AssertionViolation("write!", "could not write all the bytes");
            }
        }
    }

    public override string ToString()
    {
        return string.Format("#<custom-binary-input/output-port id: {0}>", _id);
    }

    public override void Close()
    {
        if (_close != null)
        {
            _close.Call();
        }
    }
}


[CLSCompliant(false)]
public class CustomTextReaderWriter
{
    public object id;
    public readonly TextWriter output;
    public readonly TextReader input;

    public CustomTextReaderWriter(object id, TextReader input, TextWriter output)
    {
        this.id = id;
        this.input = input;
        this.output = output;
    }

    public override string ToString()
    {
        return string.Format("#<custom-textual-input/output-port id: {0}>", id);
    }
}

public class WriteOnlyMemoryStream : MemoryStream
{
    public WriteOnlyMemoryStream()
    {

    }
    public override bool CanRead => false;
}

#endregion

public class IO : Builtins
{
    private static readonly object s_bmLine = Symbol.Intern("line");

    private static readonly object s_eolLf = Symbol.Intern("lf"); //10
    private static readonly object s_eolCr = Symbol.Intern("cr"); //13
    internal static object s_eolCrlf = Symbol.Intern("crlf");
    private static readonly object s_eolNel = Symbol.Intern("nel");
    private static readonly object s_eolCrnel = Symbol.Intern("crnel"); //194 133
    private static readonly object s_eolLs = Symbol.Intern("ls"); // 226 128 168
    internal static object s_eolNone = Symbol.Intern("none");

    private const string Nel = "\u0085";
    private const string Ls = "\u2028";

    internal static Regex s_eoltx = new Regex(string.Join("|",
      Array.ConvertAll<string, string>(
      new string[] { "\r\n", Nel, "\r" + Nel, Ls, "\r", "\n", }, Regex.Escape)), RegexOptions.Compiled);
    internal static Regex s_lftx = new Regex("\\n", RegexOptions.Compiled);

    internal static string GetNewline(object symbolId, string current)
    {
        if (symbolId == s_eolNone)
        {
            return current;
        }
        else if (symbolId == s_eolLf)
        {
            return "\n";
        }
        else if (symbolId == s_eolCr)
        {
            return "\r";
        }
        else if (symbolId == s_eolCrlf)
        {
            return "\r\n";
        }
        else if (symbolId == s_eolNel)
        {
            return Nel;
        }
        else if (symbolId == s_eolCrnel)
        {
            return "\r" + Nel;
        }
        else if (symbolId == s_eolLs)
        {
            return Ls;
        }
        else
        {
            return (string)AssertionViolation("transcode-port", "not a valid eol symbol", symbolId);
        }
    }

    private static byte[] TrimFront(byte[] v, int k)
    {
        byte[] r = new byte[v.Length - k];
        Buffer.BlockCopy(v, k, r, 0, r.Length);
        return r;
    }

    //(bytevector->string bytevector transcoder)
    [Primitive("bytevector->string")]
    public static object ByteVectorToString(object bv, object tc)
    {
        byte[] b = RequiresNotNull<byte[]>(bv);
        Transcoder t = RequiresNotNull<Transcoder>(tc);

        if (b.Length > 0)
        {
            if ((b[0] == 0xFF && b[1] == 0xFE) || (b[0] == 0xFE && b[1] == 0xFF))
            {
                b = TrimFront(b, 2);
            }
        }

        string value = t.codec.GetString(b);

        //if (t.eolstyle == eol_none)
        //{
        //  value = value.Replace("\r", "");
        //}
        //else
        {
            value = s_eoltx.Replace(value, delegate (Match m)
            {
                return GetNewline(t.eolstyle, m.Value);
            });
        }

        return value;
    }

    //(string->bytevector string transcoder)
    [Primitive("string->bytevector")]
    public static object StringToByteVector(object s, object tc)
    {
        Transcoder t = RequiresNotNull<Transcoder>(tc);
        string value = RequiresNotNull<string>(s);

        value = s_lftx.Replace(value, delegate (Match m)
        {
            return GetNewline(t.eolstyle, m.Value);
        });

        return t.codec.GetBytes(value);
    }

    private static object TranscodedOutputPort(object binaryport, object transcoder)
    {
        Stream s = RequiresNotNull<Stream>(binaryport);
        Transcoder tc = RequiresNotNull<Transcoder>(transcoder);
        if (s.CanWrite)
        {
            TextWriter w = new TranscodedWriter(s, tc);
            return w;
        }
        return FALSE;
    }

    private static object TranscodedInputOutputPort(object binaryport, object transcoder)
    {
        Stream s = RequiresNotNull<Stream>(binaryport);
        Transcoder tc = RequiresNotNull<Transcoder>(transcoder);
        return new CustomTextReaderWriter(Symbol.Intern("textual/input-output-port"),
          new NonBufferedTranscodedReader(s, tc), new TranscodedWriter(s, tc));
    }

    [Primitive("open-file-output-port")]
    public static object OpenFileOutputPort(object filename)
    {
        return OpenFileOutputPort(filename, null);
    }

    [Primitive("open-file-output-port")]
    public static object OpenFileOutputPort(object filename, object fileoptions)
    {
        return OpenFileOutputPort(filename, fileoptions, s_bmLine);
    }

    [Primitive("open-file-output-port")]
    public static object OpenFileOutputPort(object filename, object fileoptions, object buffermode)
    {
        return OpenFileOutputPort(filename, fileoptions, buffermode, FALSE);
    }

    [Flags]
    private enum FileOptions
    {
        NoFail = 1,
        NoCreate = 2,
        NoTruncate = 4,
        Append = 8,
    }


    private static FileOptions ToFileOptions(object fo)
    {
        if (fo == null)
        {
            return 0;
        }
        if (fo == FALSE)
        {
            return FileOptions.NoFail;
        }
        // file-options 是 boot/enums.sls 的 enum 记录，位掩码在字段 value 中（旧系统按 CLR 字段反射读取）
        if (fo is Record r && r.Rtd != null)
        {
            foreach (var fd in r.Rtd.GetAllFields())
                if (fd.Name == "value") return (FileOptions)Convert.ToInt32(r.Fields[fd.Index], System.Globalization.CultureInfo.InvariantCulture);
        }
        AssertionViolation("file-options", "not a file-options enum set", fo);
        return 0;
    }

    private static FileMode GetMode(FileOptions fo, string filename)
    {
        if ((fo & FileOptions.Append) != 0)
        {
            return FileMode.Append;
        }
        else if ((fo & FileOptions.NoCreate) != 0)
        {
            if ((fo & FileOptions.NoTruncate) != 0)
            {
                if (!File.Exists(filename))
                {
                    return (FileMode)FileNotFoundViolation("open-file-output-port", "file does not exist", filename);
                }
                return FileMode.Open;
            }
            else
            {
                return FileMode.Truncate;
            }
        }
        else
        {
            if ((fo & FileOptions.NoTruncate) != 0)
            {
                if (File.Exists(filename) && (fo & FileOptions.NoFail) == 0)
                {
                    return (FileMode)FileAlreadyExistsViolation("open-file-output-port", filename);
                }
                if ((fo & FileOptions.NoFail) != 0)
                {
                    return FileMode.OpenOrCreate;
                }
                else
                {
                    return FileMode.Append; // this is probably a bug, and never reached...
                }
            }
            else
            {
                if (File.Exists(filename) && (fo & FileOptions.NoFail) != 0)
                {
                    return FileMode.Truncate;
                }
                else
                {

                    return FileMode.CreateNew;
                }
            }
        }
    }

    [Primitive("open-file-output-port")]
    public static object OpenFileOutputPort(object filename, object fileoptions, object buffermode, object maybetranscoder)
    {
        string fn = RequiresNotNull<string>(filename);
        FileOptions fo = ToFileOptions(fileoptions);
        FileMode fm = GetMode(fo, fn);
        bool blockbuffer = buffermode == Symbol.Intern("block");
        bool nobuffer = buffermode == Symbol.Intern("none");

        Transcoder tc = maybetranscoder as Transcoder;
        try
        {
            Stream s = File.Open(fn, fm, FileAccess.Write);

            if (blockbuffer)
            {
                s = new BufferedStream(s);
            }

            if (tc == null)
            {
                return s;
            }
            else
            {
                return TranscodedOutputPort(s, tc);
            }
        }
        catch (ContinuationException)
        {
            throw;
        }
        catch (FileNotFoundException ex)
        {
            return FileNotFoundViolation("open-file-output-port", ex.Message, filename);
        }
        catch (Exception ex)
        {
            if ((fo & FileOptions.NoFail) != 0)
            {
                return FALSE;
            }
            if (ex.Message.StartsWith("The process cannot access the file"))
            {
                return FileInUseViolation("open-file-output-port", filename);
            }
            if (ex.Message.EndsWith("already exists."))
            {
                return FileAlreadyExistsViolation("open-file-output-port", filename);
            }
            else
            {
                return AssertionViolation("open-file-output-port", ex.Message, filename);
            }
        }
    }


    [Primitive("open-file-input/output-port")]
    public static object OpenFileInputOutputPort(object filename)
    {
        return OpenFileInputOutputPort(filename, null);
    }

    [Primitive("open-file-input/output-port")]
    public static object OpenFileInputOutputPort(object filename, object fileoptions)
    {
        return OpenFileInputOutputPort(filename, fileoptions, s_bmLine);
    }

    [Primitive("open-file-input/output-port")]
    public static object OpenFileInputOutputPort(object filename, object fileoptions, object buffermode)
    {
        return OpenFileInputOutputPort(filename, fileoptions, buffermode, FALSE);
    }

    [Primitive("open-file-input/output-port")]
    public static object OpenFileInputOutputPort(object filename, object fileoptions, object buffermode, object maybetranscoder)
    {
        string fn = RequiresNotNull<string>(filename);
        FileOptions fo = ToFileOptions(fileoptions);
        FileMode fm = GetMode(fo, fn);
        Transcoder tc = maybetranscoder as Transcoder;
        bool blockbuffer = buffermode == Symbol.Intern("block");
        try
        {
            Stream s = File.Open(fn, fm, FileAccess.ReadWrite);

            if (blockbuffer)
            {
                s = new BufferedStream(s);
            }

            if (tc == null)
            {
                return s;
            }
            else
            {
                return TranscodedInputOutputPort(s, tc);
            }
        }
        catch (ContinuationException)
        {
            throw;
        }
        catch (FileNotFoundException ex)
        {
            return FileNotFoundViolation("open-file-input/output-port", ex.Message, filename);
        }
        catch (Exception ex)
        {
            // 旧实现漏掉了以下两种映射（只写端口版本有）；R6RS 要求报对应的 &i/o-file-* 条件
            if (ex.Message.StartsWith("The process cannot access the file"))
            {
                return FileInUseViolation("open-file-input/output-port", filename);
            }
            if (ex.Message.EndsWith("already exists."))
            {
                return FileAlreadyExistsViolation("open-file-input/output-port", filename);
            }
            return AssertionViolation("open-file-input/output-port", ex.Message, filename);
        }
    }
}

