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

    // 关闭后的读写报错（R6RS 8.2.6：关闭的端口不再传送数据，iron-074；原来照样调 read / write 过程）；重复关闭不再调 close 过程
    private bool _closed;

    protected void CheckOpen()
    {
        if (_closed) throw new ObjectDisposedException(ToString(), "port is closed");
    }

    protected bool MarkClosed()
    {
        if (_closed) return false;
        _closed = true;
        return true;
    }
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
        CheckOpen();
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
        if (MarkClosed() && _close != null)
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

    // 关闭后的读写报错（R6RS 8.2.6：关闭的端口不再传送数据，iron-074；原来照样调 read / write 过程）；重复关闭不再调 close 过程
    private bool _closed;

    private void CheckOpen()
    {
        if (_closed) throw new ObjectDisposedException(ToString(), "port is closed");
    }

    private bool MarkClosed()
    {
        if (_closed) return false;
        _closed = true;
        return true;
    }

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
        CheckOpen();
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
        if (MarkClosed() && _close != null)
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
        CheckOpen();
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
        if (MarkClosed() && _close != null)
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

    // 关闭后的读写报错（R6RS 8.2.6：关闭的端口不再传送数据，iron-074；原来照样调 read / write 过程）；重复关闭不再调 close 过程
    private bool _closed;

    private void CheckOpen()
    {
        if (_closed) throw new ObjectDisposedException(ToString(), "port is closed");
    }

    private bool MarkClosed()
    {
        if (_closed) return false;
        _closed = true;
        return true;
    }

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
        CheckOpen();
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
        if (MarkClosed() && _close != null)
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

/// <summary>转码读取器（缓冲式，iron-074）：自己的字节缓冲与字符缓冲，Decoder 把字节直接解码进字符缓冲（结构照 StreamReader：
/// 两层缓冲、只解码一次、字符缓冲按 GetMaxCharCount 定大小；字节序标记的识别照它的 IsPreamble / DetectEncoding）。
/// eol-style 不是 none 时，输入的 CR、CRLF、NEL、CRNEL、LS 都转成换行符（R6RS 8.2.4）：每取一块就地把行尾编码改写成 LF
/// （块里没有 CR / NEL / LS 时一次向量化查找就过去），跨块由两态的状态机接上——块尾是单独的 CR 时进入 AfterCr，下一块开头的
/// LF / NEL 属于同一个行尾。之后 Read / Peek 只取数组元素，ReadLine 只找 LF、按段截取（同 StreamReader.ReadLine 的做法）。
/// 继承 TextReader 而不是 StreamReader：StreamReader 的缓冲是私有的，子类重载 ReadLine 也用不上，只能经公开的 Read 多拷一遍。</summary>
[CLSCompliant(false)]
public class TranscodedReader : TextReader
{
    private const int ByteBufferSize = 4096;
    private static readonly System.Buffers.SearchValues<char> s_eolStarts = System.Buffers.SearchValues.Create("\r\u0085\u2028");

    private enum EolState { Normal, AfterCr }

    private readonly Stream _port;
    private readonly Transcoder _tc;
    private readonly bool _eol;
    private readonly byte[] _bytes = new byte[ByteBufferSize];
    private int _byteLen;            // 第一次解码前攒下、留待识别字节序标记的字节数
    private Decoder _decoder;        // 第一次取块时按字节序标记定下
    private char[] _buf;
    private int _pos, _len;
    private bool _eof, _disposed;
    private EolState _state;

    public Transcoder Transcoder
    {
        get { return _tc; }
    }

    public Stream BaseStream
    {
        get { return _port; }
    }

    public TranscodedReader(Stream s, Transcoder tc)
    {
        _tc = tc;
        _port = s;
        _eol = IO.ConvertsEol(tc);
    }

    // 关闭时清空缓冲：之后任何读取都走到 Fill，在那里报已关闭（热路径上不另加检查）
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed) _port.Close();
        _disposed = true;
        _pos = _len = 0;
        base.Dispose(disposing);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(null, "Cannot read from a closed TextReader.");
    }

    // —— 字节序标记（照 StreamReader）——
    // 1. 转码器的编码自带前导标记（如带 BOM 的 UTF-16）且字节以它开头：跳过，编码不变（IsPreamble）。
    // 2. 否则按标记换编码并跳过（DetectEncoding）：FE FF → UTF-16BE；FF FE → UTF-16LE，后面再跟 00 00 时是 UTF-32LE；
    //    EF BB BF → UTF-8；00 00 FE FF → UTF-32BE。换成 UTF-8 而转码器本来就是 UTF-8 时保留它（连同出错处理方式），
    //    其余换编码时也沿用转码器的解码出错处理（StreamReader 换成默认的 replace）。
    // 至少攒够 4 个字节（或到流尾）再判断，免得把半个标记当正文。返回要跳过的字节数
    private int StartDecoder()
    {
        Encoding enc = _tc.codec;
        int skip = 0;
        ReadOnlySpan<byte> b = _bytes.AsSpan(0, _byteLen);
        ReadOnlySpan<byte> pre = enc.Preamble;
        if (pre.Length > 0 && b.StartsWith(pre))
        {
            skip = pre.Length;
        }
        else if (b.Length >= 2)
        {
            Encoding found = null;
            if (b[0] == 0xFE && b[1] == 0xFF) { found = new UnicodeEncoding(true, false); skip = 2; }
            else if (b[0] == 0xFF && b[1] == 0xFE)
            {
                if (b.Length >= 4 && b[2] == 0 && b[3] == 0) { found = new UTF32Encoding(false, false); skip = 4; }
                else { found = new UnicodeEncoding(false, false); skip = 2; }
            }
            else if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) { found = enc is UTF8Encoding ? enc : new UTF8Encoding(false); skip = 3; }
            else if (b.Length >= 4 && b[0] == 0 && b[1] == 0 && b[2] == 0xFE && b[3] == 0xFF) { found = new UTF32Encoding(true, false); skip = 4; }
            if (found != null && found != enc)
            {
                found = (Encoding)found.Clone();
                found.DecoderFallback = _tc.codec.DecoderFallback;
                enc = found;
            }
        }
        _decoder = enc.GetDecoder();
        _buf = new char[enc.GetMaxCharCount(ByteBufferSize)];
        return skip;
    }

    // 解码下一块进 _buf；文件尾为 0
    private int Decode()
    {
        for (;;)
        {
            if (_eof) return 0;
            int n, skip = 0;
            if (_decoder == null)
            {
                while (_byteLen < 4)
                {
                    int k = _port.Read(_bytes, _byteLen, _bytes.Length - _byteLen);
                    if (k == 0) break;
                    _byteLen += k;
                }
                skip = StartDecoder();
                n = _byteLen;
            }
            else
            {
                n = _port.Read(_bytes, 0, _bytes.Length);
            }
            if (n == 0)
            {
                _eof = true;
                return _decoder.GetChars(_bytes, 0, 0, _buf, 0, true);   // 末尾不完整的字节序列
            }
            int c = _decoder.GetChars(_bytes, skip, n - skip, _buf, 0, false);
            if (c > 0) return c;
        }
    }

    // 缓冲读完时取下一块并规范行尾；文件尾为 false
    private bool Fill()
    {
        ThrowIfDisposed();
        for (;;)
        {
            _pos = 0;
            _len = Decode();
            if (_len == 0) { _state = EolState.Normal; return false; }
            if (!_eol) return true;
            if (_state == EolState.AfterCr)
            {
                _state = EolState.Normal;
                if (_buf[0] == '\n' || _buf[0] == '\u0085') _pos = 1;
            }
            int i = _buf.AsSpan(_pos, _len - _pos).IndexOfAny(s_eolStarts);
            if (i >= 0) Normalize(_pos + i);
            if (_pos < _len) return true;   // 整块只有一个被吞掉的 LF / NEL 时再取一块
        }
    }

    // 从 i（第一个要改写的行尾字符）起就地改写 [i, _len)：CR / CRLF / CRNEL / NEL / LS → LF；块尾单独的 CR 进入 AfterCr
    private void Normalize(int i)
    {
        int w = i;
        while (i < _len)
        {
            char c = _buf[i++];
            switch (c)
            {
                case '\r':
                    if (i == _len) _state = EolState.AfterCr;
                    else if (_buf[i] == '\n' || _buf[i] == '\u0085') i++;
                    _buf[w++] = '\n';
                    break;
                case '\u0085':
                case '\u2028':
                    _buf[w++] = '\n';
                    break;
                default:
                    _buf[w++] = c;
                    break;
            }
        }
        _len = w;
    }

    public override int Read()
    {
        if (_pos == _len && !Fill()) return -1;
        return _buf[_pos++];
    }

    public override int Peek()
    {
        if (_pos == _len && !Fill()) return -1;
        return _buf[_pos];
    }

    public override int Read(char[] buffer, int index, int count) => Read(buffer.AsSpan(index, count));

    public override int Read(Span<char> buffer)
    {
        if (buffer.Length == 0) return 0;
        if (_pos == _len && !Fill()) return 0;
        int n = Math.Min(buffer.Length, _len - _pos);
        _buf.AsSpan(_pos, n).CopyTo(buffer);
        _pos += n;
        return n;
    }

    public override int ReadBlock(char[] buffer, int index, int count) => ReadBlock(buffer.AsSpan(index, count));

    public override int ReadBlock(Span<char> buffer)
    {
        int n = 0;
        while (n < buffer.Length)
        {
            int k = Read(buffer.Slice(n));
            if (k == 0) break;
            n += k;
        }
        return n;
    }

    public override string ReadToEnd()
    {
        ThrowIfDisposed();
        var sb = new StringBuilder(_len - _pos);
        sb.Append(_buf, _pos, _len - _pos);
        while (Fill()) sb.Append(_buf, _pos, _len - _pos);
        _pos = _len;
        return sb.ToString();
    }

    /// <summary>读到换行符为止（不含）；行尾已在缓冲里规范成 LF。文件尾且一个字符也没读到时为 null。
    /// 行在一块之内时直接截取，跨块时才拼接（同 StreamReader.ReadLine）。</summary>
    public override string ReadLine()
    {
        if (_pos == _len && !Fill()) return null;
        StringBuilder sb = null;
        for (;;)
        {
            var span = _buf.AsSpan(_pos, _len - _pos);
            int i = span.IndexOf('\n');
            if (i >= 0)
            {
                string line = sb == null ? new string(span.Slice(0, i)) : sb.Append(span.Slice(0, i)).ToString();
                _pos += i + 1;
                return line;
            }
            (sb ??= new StringBuilder(span.Length + 80)).Append(span);
            _pos = _len;
            if (!Fill()) return sb.ToString();
        }
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
    private readonly bool _eol;

    public Transcoder Transcoder
    {
        get { return _tc; }
    }

    public NonBufferedTranscodedReader(Stream s, Transcoder tc)
    {
        _tc = tc;
        _port = s;
        _eol = IO.ConvertsEol(tc);
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
        int c = ReadRaw();
        if (!_eol) return c;
        if (c == '\u0085' || c == '\u2028') return '\n';
        if (c != '\r') return c;
        // CR 后面跟 LF / NEL 时一起算一个行尾，否则退回位置
        long p = _port.Position;
        int d = ReadRaw();
        if (d != '\n' && d != '\u0085') _port.Position = p;
        return '\n';
    }

    private int ReadRaw()
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
                return ReadRaw();
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

    // ReadToEnd 用 TextReader 的默认实现（逐个 Read），行尾已在 Read 里转换

    /// <summary>读到换行符为止（不含）；只认换行符（R6RS get-line）。</summary>
    public override string ReadLine() => IO.ReadLineLf(this);

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
        CheckOpen();
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
        CheckOpen();
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
        if (MarkClosed() && _close != null)
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
    private static readonly object s_bmBlock = Symbol.Intern("block");

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

    /// <summary>转码器的 eol-style 不是 none 时，输入要转换行尾（R6RS 8.2.4）。</summary>
    internal static bool ConvertsEol(Transcoder tc) => tc.eolstyle != s_eolNone;

    /// <summary>c 是否开始一个行尾编码（CR、LF、NEL、LS；CRLF / CRNEL 以 CR 开始）。</summary>
    internal static bool IsEolStart(int c) => c == '\r' || c == '\u0085' || c == '\u2028';

    /// <summary>输入方向的行尾转换：CR、CRLF、NEL、CRNEL、LS 都成换行符。</summary>
    internal static string ConvertInputEol(string s)
    {
        int i = s.AsSpan().IndexOfAny('\r', '\u0085', '\u2028');
        if (i < 0) return s;
        var sb = new StringBuilder(s.Length);
        sb.Append(s, 0, i);
        for (; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\r')
            {
                if (i + 1 < s.Length && (s[i + 1] == '\n' || s[i + 1] == '\u0085')) i++;
                sb.Append('\n');
            }
            else if (c == '\u0085' || c == '\u2028') sb.Append('\n');
            else sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>逐字符读到换行符为止（不含换行符）；文件尾且一个字符也没读到时为 null。</summary>
    internal static string ReadLineLf(TextReader r)
    {
        var sb = new StringBuilder();
        for (;;)
        {
            int c = r.Read();
            if (c < 0) return sb.Length == 0 ? null : sb.ToString();
            if (c == '\n') return sb.ToString();
            sb.Append((char)c);
        }
    }

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
        return OpenFileOutputPort(filename, fileoptions, null);   // 缓冲模式省略时为 block（R6RS 8.2.7）
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
        if (!IsBufferMode(buffermode)) return AssertionViolation("open-file-output-port", "not a buffer mode", buffermode);
        if (maybetranscoder != FALSE && maybetranscoder is not Transcoder)
            return AssertionViolation("open-file-output-port", "not a transcoder", maybetranscoder);
        FileOptions fo = ToFileOptions(fileoptions);
        FileMode fm = GetMode(fo, fn);
        bool blockbuffer = buffermode == Symbol.Intern("block");
        bool nobuffer = buffermode == Symbol.Intern("none");

        Transcoder tc = maybetranscoder as Transcoder;
        try
        {
            Stream s = OpenFileStream(fn, fm, FileAccess.Write, nobuffer);

            if (blockbuffer)
            {
                s = new BufferedStream(s);
            }

            return WithBufferMode(tc == null ? s : TranscodedOutputPort(s, tc), buffermode);
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


    // none：FileStream 不带缓冲，每次写直接落到文件；line / block / 省略：FileStream 自带的缓冲（block 外面另加 BufferedStream）
    private static Stream OpenFileStream(string fn, FileMode fm, FileAccess access, bool nobuffer) =>
      nobuffer ? new FileStream(fn, fm, access, FileShare.None, 0) : File.Open(fn, fm, access);

    // null 为省略
    private static bool IsBufferMode(object m) =>
      m == null || m == Symbol.Intern("none") || m == Symbol.Intern("line") || m == Symbol.Intern("block");

    /// <summary>记下打开时的缓冲模式（省略时为 block），output-port-buffer-mode 返回它（iron-074）。</summary>
    private static object WithBufferMode(object port, object buffermode)
    {
        PortPrimitives.SetBufferMode(port, buffermode ?? s_bmBlock);
        return port;
    }

    [Primitive("open-file-input/output-port")]
    public static object OpenFileInputOutputPort(object filename)
    {
        return OpenFileInputOutputPort(filename, null);
    }

    [Primitive("open-file-input/output-port")]
    public static object OpenFileInputOutputPort(object filename, object fileoptions)
    {
        return OpenFileInputOutputPort(filename, fileoptions, null);
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
        if (!IsBufferMode(buffermode)) return AssertionViolation("open-file-input/output-port", "not a buffer mode", buffermode);
        if (maybetranscoder != FALSE && maybetranscoder is not Transcoder)
            return AssertionViolation("open-file-input/output-port", "not a transcoder", maybetranscoder);
        FileOptions fo = ToFileOptions(fileoptions);
        FileMode fm = GetMode(fo, fn);
        Transcoder tc = maybetranscoder as Transcoder;
        bool blockbuffer = buffermode == Symbol.Intern("block");
        try
        {
            Stream s = OpenFileStream(fn, fm, FileAccess.ReadWrite, buffermode == Symbol.Intern("none"));

            if (blockbuffer)
            {
                s = new BufferedStream(s);
            }

            return WithBufferMode(tc == null ? s : TranscodedInputOutputPort(s, tc), buffermode);
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

