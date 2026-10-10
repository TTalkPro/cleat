using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Cleat.Emit;

/// <summary>
/// 常量的中立编码（designs/05-cil-backend.md §3）→ 二进制 fasl。格式与 Cleat.Core.Runtime.Fasl 一致，
/// 这里不引用 Core，标签值重复定义。从 IronScheme 的 FaslEncoder 移植，编码按 Clojure 数据重定义。
/// </summary>
public static class FaslEncoder
{
    public const byte Version = 1;
    public static readonly byte[] Magic = Encoding.ASCII.GetBytes("CLFASL");

    public const byte Nil = 0x00, True = 0x01, False = 0x02,
      Long = 0x10, Double = 0x11, BigInt = 0x12, BigDec = 0x13, Ratio = 0x14,
      Char = 0x20, Str = 0x21,
      Sym = 0x30, Kw = 0x31, Var = 0x32,
      List = 0x40, Vec = 0x41, Map = 0x42, Set = 0x43, SortedMap = 0x44, SortedSet = 0x45, Queue = 0x46,
      Meta = 0x50,
      Regex = 0x60, Inst = 0x61, Uuid = 0x62, TypeTag = 0x63, Record = 0x64,
      Def = 0x70, Ref = 0x71;

    /// <summary>常量表（各项编码）→ fasl：整个表写成一个向量，def / ref 编号在全表内共享。</summary>
    public static byte[] EncodeTable(IList<SNode> constants, string file)
    {
        var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, true))
        {
            w.Write(Magic);
            w.Write(Version);
            w.Write(Vec);
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

        private void Arity(SList l, int n)
        {
            if (l.Count - 1 != n) throw Err(l, $"{l.Head} takes {n} operand(s)");
        }

        private string StrArg(SList l, int i) => l[i] is SStr s ? s.Value : throw Err(l[i], "expected a string");
        private long IntArg(SList l, int i) => l[i] is SInt n ? n.Value : throw Err(l[i], "expected an integer");

        /// <summary>(sym "name") / (sym "ns" "name")：命名空间为空时写空串。</summary>
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

        public void CheckRefs()
        {
            foreach (var (id, pos) in _refs)
                if (!_defined.Contains(id)) throw Err(pos, $"reference to undefined object {id}");
        }

        public void Encode(SNode n)
        {
            if (!(n is SList l) || l.Head == null) throw Err(n, "expected an encoding like (long 1)");
            switch (l.Head)
            {
                case "nil": Arity(l, 0); _w.Write(Nil); return;
                case "true": Arity(l, 0); _w.Write(True); return;
                case "false": Arity(l, 0); _w.Write(False); return;
                case "long": Arity(l, 1); _w.Write(Long); _w.Write(IntArg(l, 1)); return;
                case "double":
                    {
                        Arity(l, 1);
                        if (!ulong.TryParse(StrArg(l, 1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong bits) || StrArg(l, 1).Length != 16)
                            throw Err(l[1], "double must be 16 hexadecimal digits (IEEE bits)");
                        _w.Write(Double); _w.Write(unchecked((long)bits));
                        return;
                    }
                case "bigint": Arity(l, 1); _w.Write(BigInt); String(StrArg(l, 1)); return;
                case "bigdec": Arity(l, 1); _w.Write(BigDec); String(StrArg(l, 1)); return;
                case "ratio": Arity(l, 1); _w.Write(Ratio); String(StrArg(l, 1)); return;
                case "char":
                    {
                        Arity(l, 1);
                        long c = IntArg(l, 1);
                        if (c < 0 || c > 0xFFFF) throw Err(l[1], "char must be a UTF-16 code unit");
                        _w.Write(Char); _w.Write((ushort)c);
                        return;
                    }
                case "str": Arity(l, 1); _w.Write(Str); String(StrArg(l, 1)); return;
                case "sym": _w.Write(Sym); Named(l); return;
                case "kw": _w.Write(Kw); Named(l); return;
                case "var": Arity(l, 2); _w.Write(Var); String(StrArg(l, 1)); String(StrArg(l, 2)); return;
                case "list": _w.Write(List); Items(l, 1); return;
                case "vec": _w.Write(Vec); Items(l, 1); return;
                case "set": _w.Write(Set); Items(l, 1); return;
                case "sorted-set": _w.Write(SortedSet); Items(l, 1); return;
                case "queue": _w.Write(Queue); Items(l, 1); return;
                case "map": _w.Write(Map); Pairs(l); return;
                case "sorted-map": _w.Write(SortedMap); Pairs(l); return;
                case "meta": Arity(l, 2); _w.Write(Meta); Encode(l[1]); Encode(l[2]); return;
                case "regex": Arity(l, 1); _w.Write(Regex); String(StrArg(l, 1)); return;
                case "inst": Arity(l, 1); _w.Write(Inst); String(StrArg(l, 1)); return;
                case "uuid": Arity(l, 1); _w.Write(Uuid); String(StrArg(l, 1)); return;
                case "type": Arity(l, 1); _w.Write(TypeTag); String(StrArg(l, 1)); return;
                case "record":
                    if (l.Count < 2) throw Err(l, "record needs a type name");
                    _w.Write(Record); String(StrArg(l, 1)); Items(l, 2);
                    return;
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
