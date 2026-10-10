using System.IO;
using System.Text;
using Cleat.Scheme;
using NUnit.Framework;

namespace Cleat.Scheme.Tests;

public class IOTests
{
    private static Transcoder Utf8(string eol = "lf", string mode = "replace") =>
      new Transcoder(new UTF8Encoding(false), Symbol.Intern(eol), Symbol.Intern(mode));

    [Test]
    public void StringBytevectorRoundTrip()
    {
        var tc = Utf8();
        var bv = (byte[])IO.StringToByteVector("héllo 世界", tc);
        Assert.That(Encoding.UTF8.GetString(bv), Is.EqualTo("héllo 世界"));
        Assert.That(IO.ByteVectorToString(bv, tc), Is.EqualTo("héllo 世界"));
    }

    [Test]
    public void EolStyleOnEncode()
    {
        var bv = (byte[])IO.StringToByteVector("a\nb", Utf8("crlf"));
        Assert.That(Encoding.UTF8.GetString(bv), Is.EqualTo("a\r\nb"));
    }

    [Test]
    public void Latin1Codec()
    {
        var tc = new Transcoder(Encoding.Latin1, Symbol.Intern("lf"), Symbol.Intern("replace"));
        Assert.That((byte[])IO.StringToByteVector("é", tc), Is.EqualTo(new byte[] { 0xE9 }));
    }

    [Test]
    public void RaiseHandlingModeReportsDecodingError()
    {
        var e = Assert.Throws<SchemeRuntimeError>(() => IO.ByteVectorToString(new byte[] { 0xFF, 0x41, 0xC3 }, Utf8(mode: "raise")));
        Assert.That(e.Kind, Is.EqualTo("i/o-decoding-error"));
    }

    [Test]
    public void FileOutputThenInputOutputPort()
    {
        var path = Path.Combine(Path.GetTempPath(), "iron008-" + Path.GetRandomFileName());
        try
        {
            using (var s = (Stream)IO.OpenFileOutputPort(path))
                s.Write(new byte[] { 1, 2, 3 });
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(new byte[] { 1, 2, 3 }));

            // 默认文件选项要求文件不存在（R6RS）
            var e = Assert.Throws<SchemeRuntimeError>(() => IO.OpenFileInputOutputPort(path));
            Assert.That(e.Kind, Is.EqualTo("file-already-exists-violation"));

            File.Delete(path);
            using (var rw = (Stream)IO.OpenFileInputOutputPort(path))
            {
                Assert.That(rw.CanRead && rw.CanWrite, Is.True);
                rw.WriteByte(7);
                rw.Position = 0;
                Assert.That(rw.ReadByte(), Is.EqualTo(7));
            }
        }
        finally { File.Delete(path); }
    }

    [Test]
    public void StringWriterGetBufferClears()
    {
        var w = new Cleat.Scheme.StringWriter();
        w.Write("abc");
        Assert.That(w.GetBuffer(), Is.EqualTo("abc"));
        Assert.That(w.GetBuffer(), Is.EqualTo(""));
    }

    [Test]
    public void EofObject()
    {
        Assert.That(Builtins.IsEof(Builtins.EofObject()), Is.SameAs(RT.True));
        Assert.That(Builtins.IsEof(null), Is.SameAs(RT.False));
        Assert.That(Builtins.EofObject().ToString(), Is.EqualTo("#<eof>"));
    }
}
