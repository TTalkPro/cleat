using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Cleat.Emit;

/// <summary>
/// 常量的中立编码（designs/05-cil-backend.md §3）→ 二进制 fasl，格式与 Cleat.Core.Runtime.Fasl 一致：
/// nil true false fix long double bigint bigdec ratio char str sym kw var list vec map set sorted-map sorted-set queue meta regex inst uuid type record def ref，
/// 每个标签一个独立的字节（标签值沿用 IronScheme 的联合编码；Scheme 专有的标签随元层在 cleat-093 删除，值不复用）。
/// 这里不引用运行时，标签值重复定义。
/// </summary>
public static class FaslEncoder
{
    public const byte Version = 2;   // 与 Cleat.Core.Runtime.Fasl.Version 一致
    public static readonly byte[] Magic = Encoding.ASCII.GetBytes("CLFASL");

    // 标签（与 Cleat.Core.Runtime.Fasl 一致）：0x0_ 单例；0x1_ 数；0x2_ 字符、字符串、符号；0x3_ 序列；0x5_ 其他；0x6_ 共享
    public const byte Nil = 0x00, True = 0x01, False = 0x02,
      Fix = 0x10, Long = 0x18, Double = 0x19, BigInt = 0x1A, BigDec = 0x1B, Ratio = 0x1C,
      Char = 0x28, CljStr = 0x29, CljSym = 0x2A, Kw = 0x2B, Var = 0x2C,
      Table = 0x32,   // 常量表的外层（IronScheme 的 vec 标签，读方只认它作表）
      CljList = 0x38, CljVec = 0x39, Map = 0x3A, Set = 0x3B, SortedMap = 0x3C, SortedSet = 0x3D, Queue = 0x3E,
      Meta = 0x50, Regex = 0x51, Inst = 0x52, Uuid = 0x53, TypeTag = 0x54, Record = 0x55,
      Def = 0x60, Ref = 0x61;

    /// <summary>常量表（各项编码）→ fasl：整个表写成一个表，def / ref 编号在全表内共享。</summary>
    public static byte[] EncodeTable(IList<SNode> constants, string file) => EncodeTable(constants, file, LcilRuntime.Clojure);

    public static byte[] EncodeTable(IList<SNode> constants, string file, string runtime)
    {
        if (Array.IndexOf(LcilRuntime.All, runtime) < 0) throw new LcilException($"{file}: unknown runtime {runtime}");
        var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, true))
        {
            w.Write(Magic);
            w.Write(Version);
            w.Write(Table);
            w.Write7BitEncodedInt(constants.Count);
            var e = new Encoder(w, file);
            foreach (var c in constants) e.Encode(c);
            e.CheckRefs();
        }
        return ms.ToArray();
    }

    private sealed class Encoder
    {
        private readonly BinaryWriter _w;
        private readonly string _file;
        private readonly HashSet<long> _defined = new HashSet<long>();
        private readonly List<(long, SNode)> _refs = new List<(long, SNode)>();

        public Encoder(BinaryWriter w, string file) { _w = w; _file = file; }

        private LcilException Err(SNode n, string msg) => new LcilException($"{n.Where(_file)}: constant: {msg}");

        private void String(string s)
        {
            _w.Write7BitEncodedInt(s.Length);
            foreach (char c in s) _w.Write((ushort)c);
        }

        private static string StrOf(SNode n) => (n as SStr)?.Value;

        private void Arity(SList l, int n)
        {
            if (l.Count - 1 != n) throw Err(l, $"{l.Head} takes {n} operand(s)");
        }

        private string StrArg(SList l, int i) => StrOf(l[i]) ?? throw Err(l[i], "expected a string");
        private long IntArg(SList l, int i) => l[i] is SInt n ? n.Value : throw Err(l[i], "expected an integer");

        /// <summary>(sym "name") / (sym "ns" "name")（Clojure 的符号、关键字）：命名空间为空时写空串。</summary>
        private void Named(SList l)
        {
            if (l.Count == 2) { String(""); String(StrArg(l, 1)); }
            else if (l.Count == 3) { String(StrArg(l, 1)); String(StrArg(l, 2)); }
            else throw Err(l, $"{l.Head} takes a name or a namespace and a name");
        }

        private void Items(SList l, int from)
        {
            _w.Write7BitEncodedInt(l.Count - from);
            for (int i = from; i < l.Count; i++) Encode(l[i]);
        }

        private void Pairs(SList l)
        {
            if ((l.Count - 1) % 2 != 0) throw Err(l, $"{l.Head} needs key / value pairs");
            _w.Write7BitEncodedInt((l.Count - 1) / 2);
            for (int i = 1; i < l.Count; i++) Encode(l[i]);
        }

        private ulong Bits(SList l, string what)
        {
            Arity(l, 1);
            string hex = StrArg(l, 1);
            if (hex.Length != 16 || !ulong.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong bits))
                throw Err(l[1], $"{what} must be 16 hexadecimal digits (IEEE bits)");
            return bits;
        }

        public void CheckRefs()
        {
            // ref 必须指向已定义（或正在定义、即环）的编号
            foreach (var (id, pos) in _refs)
                if (!_defined.Contains(id)) throw Err(pos, $"reference to undefined object {id}");
        }

        public void Encode(SNode n)
        {
            if (!(n is SList l) || l.Head == null) throw Err(n, "expected an encoding like (fix 1)");
            switch (l.Head)
            {
                // ———— 单例 ————
                case "nil": Arity(l, 0); _w.Write(Nil); return;
                case "true": Arity(l, 0); _w.Write(True); return;
                case "false": Arity(l, 0); _w.Write(False); return;
                // ———— 数 ————
                case "fix":   // Int32（读入器给元数据的行号等）
                    {
                        Arity(l, 1);
                        long v = IntArg(l, 1);
                        if (v < int.MinValue || v > int.MaxValue) throw Err(l, "fix out of range");
                        _w.Write(Fix); _w.Write((int)v);
                        return;
                    }
                case "long": Arity(l, 1); _w.Write(Long); _w.Write(IntArg(l, 1)); return;
                case "double": _w.Write(Double); _w.Write(unchecked((long)Bits(l, "double"))); return;
                case "bigint": Arity(l, 1); _w.Write(BigInt); String(StrArg(l, 1)); return;
                case "bigdec": Arity(l, 1); _w.Write(BigDec); String(StrArg(l, 1)); return;
                case "ratio": Arity(l, 1); _w.Write(Ratio); String(StrArg(l, 1)); return;
                // ———— 字符、字符串、符号 ————
                case "char":
                    {
                        Arity(l, 1);
                        long c = IntArg(l, 1);
                        if (c < 0 || c > 0xFFFF) throw Err(l[1], "char must be a UTF-16 code unit");
                        _w.Write(Char); _w.Write((ushort)c);
                        return;
                    }
                case "str": case "cstr": Arity(l, 1); _w.Write(CljStr); String(StrArg(l, 1)); return;
                case "sym": case "csym": _w.Write(CljSym); Named(l); return;
                case "kw": _w.Write(Kw); Named(l); return;
                case "var": Arity(l, 2); _w.Write(Var); String(StrArg(l, 1)); String(StrArg(l, 2)); return;
                // ———— 序列 ————
                case "list": case "clist": _w.Write(CljList); Items(l, 1); return;
                case "vec": case "cvec": _w.Write(CljVec); Items(l, 1); return;
                case "set": _w.Write(Set); Items(l, 1); return;
                case "sorted-set": _w.Write(SortedSet); Items(l, 1); return;
                case "queue": _w.Write(Queue); Items(l, 1); return;
                case "map": _w.Write(Map); Pairs(l); return;
                case "sorted-map": _w.Write(SortedMap); Pairs(l); return;
                // ———— 其他 ————
                case "meta": Arity(l, 2); _w.Write(Meta); Encode(l[1]); Encode(l[2]); return;
                case "regex": Arity(l, 1); _w.Write(Regex); String(StrArg(l, 1)); return;
                case "inst": Arity(l, 1); _w.Write(Inst); String(StrArg(l, 1)); return;
                case "uuid": Arity(l, 1); _w.Write(Uuid); String(StrArg(l, 1)); return;
                case "type": Arity(l, 1); _w.Write(TypeTag); String(StrArg(l, 1)); return;
                case "record":
                    if (l.Count < 2) throw Err(l, "record needs a type name");
                    _w.Write(Record); String(StrArg(l, 1)); Items(l, 2);
                    return;
                // ———— 共享与环 ————
                case "def":
                    {
                        Arity(l, 2);
                        long id = IntArg(l, 1);
                        if (id < 0 || !_defined.Add(id)) throw Err(l, $"duplicate or negative definition {id}");
                        _w.Write(Def); _w.Write7BitEncodedInt((int)id); Encode(l[2]);
                        return;
                    }
                case "ref":
                    Arity(l, 1);
                    _refs.Add((IntArg(l, 1), l));
                    _w.Write(Ref); _w.Write7BitEncodedInt((int)IntArg(l, 1));
                    return;
                default: throw Err(l, "unknown encoding: " + l.Head);
            }
        }
    }
}
