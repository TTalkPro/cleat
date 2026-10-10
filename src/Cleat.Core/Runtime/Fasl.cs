using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using clojure.lang;

namespace Cleat.Core.Runtime;

/// <summary>
/// 二进制 fasl（designs/05-cil-backend.md §3）：编译产物的常量表、接口文件与 Lcore 种子的格式。
/// 发射器（Cleat.Emit.FaslEncoder）把中立编码写成同一格式；这里负责读回对象，并能把对象直接写出。
/// 符号与关键字按名字重建；Var 按 ns/name intern；def / ref 表示共享与环。
/// </summary>
public static class Fasl
{
    public const byte Version = 1;
    private static readonly byte[] s_magic = Encoding.ASCII.GetBytes("CLFASL");

    // 标签（与 Cleat.Emit.FaslEncoder 一致）
    public const byte Nil = 0x00, True = 0x01, False = 0x02,
      Long = 0x10, Double = 0x11, BigIntTag = 0x12, BigDec = 0x13, RatioTag = 0x14,
      Char = 0x20, Str = 0x21,
      Sym = 0x30, Kw = 0x31, VarTag = 0x32,
      List = 0x40, Vec = 0x41, Map = 0x42, Set = 0x43, SortedMap = 0x44, SortedSet = 0x45, Queue = 0x46,
      Meta = 0x50,
      RegexTag = 0x60, Inst = 0x61, Uuid = 0x62, TypeTag = 0x63, Record = 0x64,
      Def = 0x70, Ref = 0x71;

    /// <summary>常量表：发射器把 Deflate 压缩的 fasl 作为初始化数据嵌入程序集，类型初始化器取出后调用本方法。</summary>
    public static object[] ReadPackedTable(byte[] packed)
    {
        using var z = new DeflateStream(new MemoryStream(packed), CompressionMode.Decompress);
        using var r = new BinaryReader(z);
        return ToTable(Read(r));
    }

    /// <summary>未压缩的常量表（测试与工具用）。</summary>
    public static object[] ReadTable(byte[] data) => ToTable(Read(data));

    /// <summary>常量表在 fasl 里是一个向量（发射器 EncodeTable 这样写），读回后转成数组供 ldconst 下标访问。</summary>
    private static object[] ToTable(object x)
    {
        var v = x as IPersistentVector ?? throw new InvalidDataException("fasl: constant table must be a vector");
        var table = new object[v.count()];
        for (int i = 0; i < table.Length; i++) table[i] = v.nth(i);
        return table;
    }

    public static object Read(byte[] data)
    {
        using var r = new BinaryReader(new MemoryStream(data));
        return Read(r);
    }

    public static object Read(BinaryReader r)
    {
        var magic = r.ReadBytes(s_magic.Length);
        if (!magic.AsSpan().SequenceEqual(s_magic)) throw new InvalidDataException("not a cleat fasl stream");
        byte v = r.ReadByte();
        if (v != Version) throw new InvalidDataException("unsupported fasl version " + v);
        return new Reader(r).Object();
    }

    /// <summary>类型名 → Type；cleat-010 的 TypeResolver 可用时经它解析，否则 RT.classForName。</summary>
    private static Type ResolveType(string name)
    {
        var resolver = typeof(Fasl).Assembly.GetType("Cleat.Core.Runtime.TypeResolver");
        var m = resolver?.GetMethod("Resolve", new[] { typeof(string), typeof(IEnumerable<string>) });
        var t = m != null ? (Type)m.Invoke(null, new object[] { name, Array.Empty<string>() }) : RT.classForName(name);
        return t ?? throw new InvalidDataException("fasl: type not found: " + name);
    }

    private sealed class Reader
    {
        private readonly BinaryReader _r;
        private readonly Dictionary<int, object> _defs = new Dictionary<int, object>();
        private int _pendingDef = -1;

        public Reader(BinaryReader r) { _r = r; }

        private int Count() => _r.Read7BitEncodedInt();

        private string String()
        {
            int n = Count();
            var cs = new char[n];
            for (int i = 0; i < n; i++) cs[i] = (char)_r.ReadUInt16();
            return new string(cs);
        }

        private T Register<T>(int def, T obj)
        {
            if (def >= 0) _defs[def] = obj;
            return obj;
        }

        private object[] Items()
        {
            var items = new object[Count()];
            for (int i = 0; i < items.Length; i++) items[i] = Object();
            return items;
        }

        public object Object()
        {
            int def = _pendingDef;
            _pendingDef = -1;
            byte tag = _r.ReadByte();
            switch (tag)
            {
                case Nil: return Register<object>(def, null);
                case True: return Register<object>(def, true);
                case False: return Register<object>(def, false);
                case Long: return Register<object>(def, _r.ReadInt64());
                case Double: return Register<object>(def, BitConverter.Int64BitsToDouble(_r.ReadInt64()));
                case BigIntTag: return Register<object>(def, BigInt.fromBigInteger(BigInteger.Parse(String())));
                case BigDec: return Register<object>(def, BigDecimal.Parse(String()));
                case RatioTag: return Register(def, LispReader.MatchNumber(String()) ?? throw new InvalidDataException("bad ratio in fasl"));
                case Char: return Register<object>(def, (char)_r.ReadUInt16());
                case Str: return Register<object>(def, String());
                case Sym: { string ns = String(), name = String(); return Register<object>(def, Symbol.intern(ns.Length == 0 ? null : ns, name)); }
                case Kw: { string ns = String(), name = String(); return Register<object>(def, Keyword.intern(ns.Length == 0 ? null : ns, name)); }
                case VarTag: { string ns = String(), name = String(); return Register<object>(def, Var.intern(Symbol.intern(ns), Symbol.intern(name))); }
                case List:
                    {
                        // 列表先读元素再建（PersistentList 不可变）；环经 def/ref 只支持向量 / map 等不是常见情况，表内共享仍可用
                        var items = Items();
                        return Register<object>(def, items.Length == 0 ? PersistentList.EMPTY : PersistentList.create(items));
                    }
                case Vec: return Register<object>(def, LazilyPersistentVector.createOwning(Items()));
                case Set: return Register<object>(def, PersistentHashSet.create(Items()));
                case SortedSet: return Register<object>(def, PersistentTreeSet.create(RT.seq(Items())));
                case Queue:
                    {
                        IPersistentCollection q = PersistentQueue.EMPTY;
                        foreach (var x in Items()) q = q.cons(x);
                        return Register<object>(def, q);
                    }
                case Map:
                    {
                        var kvs = new object[Count() * 2];
                        for (int i = 0; i < kvs.Length; i++) kvs[i] = Object();
                        return Register<object>(def, kvs.Length <= 16 ? PersistentArrayMap.createWithCheck(kvs) : PersistentHashMap.createWithCheck(kvs));
                    }
                case SortedMap:
                    {
                        var kvs = new object[Count() * 2];
                        for (int i = 0; i < kvs.Length; i++) kvs[i] = Object();
                        return Register<object>(def, PersistentTreeMap.create(RT.seq(kvs)));
                    }
                case Meta:
                    {
                        var m = Object() as IPersistentMap;
                        var x = Object() as IObj ?? throw new InvalidDataException("fasl: meta on a non-IObj value");
                        return Register<object>(def, x.withMeta(m));
                    }
                case RegexTag: return Register<object>(def, new Regex(String()));
                case Inst: return Register<object>(def, DateTime.Parse(String(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind));
                case Uuid: return Register<object>(def, Guid.Parse(String()));
                case TypeTag: return Register<object>(def, ResolveType(String()));
                case Record:
                    {
                        // defrecord 的静态 create(IPersistentMap)
                        var t = ResolveType(String());
                        var items = Items();
                        var create = t.GetMethod("create", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(IPersistentMap) }, null)
                                     ?? throw new InvalidDataException("fasl: record type has no create(IPersistentMap): " + t.FullName);
                        if (items.Length % 2 != 0) throw new InvalidDataException("fasl: record needs key / value pairs");
                        return Register(def, create.Invoke(null, new object[] { PersistentArrayMap.createWithCheck(items) }));
                    }
                case Def:
                    _pendingDef = Count();
                    return Object();
                case Ref:
                    {
                        int id = Count();
                        return _defs.TryGetValue(id, out var o) ? o : throw new InvalidDataException("fasl: reference to undefined object " + id);
                    }
                default: throw new InvalidDataException($"fasl: unknown tag 0x{tag:x2}");
            }
        }
    }

    // ———————————————— 写 ————————————————

    public static byte[] Write(object x)
    {
        var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, true))
        {
            w.Write(s_magic);
            w.Write(Version);
            new Writer(w).Object(x);
        }
        return ms.ToArray();
    }

    /// <summary>对象直接写成 fasl（种子、接口文件）。复合对象按引用去重为 def / ref。</summary>
    private sealed class Writer
    {
        private readonly BinaryWriter _w;
        private readonly Dictionary<object, int> _seen = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
        private int _next;

        public Writer(BinaryWriter w) { _w = w; }

        private void String(string s)
        {
            _w.Write7BitEncodedInt(s.Length);
            foreach (char c in s) _w.Write((ushort)c);
        }

        private void Named(string ns, string name) { String(ns ?? ""); String(name); }

        private void Items(byte tag, ICollection items)
        {
            _w.Write(tag);
            _w.Write7BitEncodedInt(items.Count);
            foreach (var x in items) Object(x);
        }

        private void Pairs(byte tag, IPersistentMap m)
        {
            _w.Write(tag);
            _w.Write7BitEncodedInt(m.count());
            for (ISeq s = m.seq(); s != null; s = s.next())
            {
                var e = (IMapEntry)s.first();
                Object(e.key());
                Object(e.val());
            }
        }

        private static bool Compound(object x) => x is IPersistentCollection || x is IObj;

        public void Object(object x)
        {
            if (x != null && Compound(x))
            {
                if (_seen.TryGetValue(x, out int id)) { _w.Write(Ref); _w.Write7BitEncodedInt(id); return; }
                id = _next++;
                _seen[x] = id;
                _w.Write(Def); _w.Write7BitEncodedInt(id);
            }
            if (x is IObj o && o.meta() != null && !(x is Var) && !(x is Symbol s0 && false))
            {
                var m = o.meta();
                // Symbol / 集合的元数据：写 (meta m x)，x 去掉元数据后再写
                _w.Write(Meta);
                Object(m);
                x = o.withMeta(null);
                if (x is Symbol || x is IPersistentCollection) { WriteBare(x); return; }
            }
            WriteBare(x);
        }

        private void WriteBare(object x)
        {
            switch (x)
            {
                case null: _w.Write(Nil); return;
                case bool b: _w.Write(b ? True : False); return;
                case long l: _w.Write(Long); _w.Write(l); return;
                case int i: _w.Write(Long); _w.Write((long)i); return;
                case double d: _w.Write(Double); _w.Write(BitConverter.DoubleToInt64Bits(d)); return;
                case BigInt bi: _w.Write(BigIntTag); String(bi.ToString()); return;
                case BigInteger bI: _w.Write(BigIntTag); String(bI.ToString()); return;
                case BigDecimal bd: _w.Write(BigDec); String(bd.ToString()); return;
                case Ratio r: _w.Write(RatioTag); String(r.ToString()); return;
                case char c: _w.Write(Char); _w.Write((ushort)c); return;
                case string s: _w.Write(Str); String(s); return;
                case Keyword k: _w.Write(Kw); Named(k.Namespace, k.Name); return;
                case Symbol sym: _w.Write(Sym); Named(sym.Namespace, sym.Name); return;
                case Var v: _w.Write(VarTag); Named(v.Namespace.Name.Name, v.Symbol.Name); return;
                case Regex re: _w.Write(RegexTag); String(re.ToString()); return;
                case DateTime dt: _w.Write(Inst); String(dt.ToString("o", System.Globalization.CultureInfo.InvariantCulture)); return;
                case Guid g: _w.Write(Uuid); String(g.ToString()); return;
                case Type t: _w.Write(TypeTag); String(t.FullName); return;
                case IRecord rec:
                    {
                        _w.Write(Record); String(x.GetType().FullName);
                        var m = (IPersistentMap)rec;
                        _w.Write7BitEncodedInt(m.count() * 2);
                        for (ISeq s = m.seq(); s != null; s = s.next()) { var e = (IMapEntry)s.first(); Object(e.key()); Object(e.val()); }
                        return;
                    }
                case PersistentTreeMap tm: Pairs(SortedMap, tm); return;
                case PersistentTreeSet ts: Items(SortedSet, ToList(ts.seq())); return;
                case PersistentQueue q: Items(Queue, ToList(q.seq())); return;
                case IPersistentMap pm: Pairs(Map, pm); return;
                case IPersistentSet ps: Items(Set, ToList(ps.seq())); return;
                case IPersistentVector pv: Items(Vec, ToList(pv.seq())); return;
                case IPersistentList _:
                case ISeq _: Items(List, ToList(RT.seq(x))); return;
                default: throw new NotSupportedException("fasl: cannot encode " + x.GetType().FullName);
            }
        }

        private static List<object> ToList(ISeq s)
        {
            var l = new List<object>();
            for (; s != null; s = s.next()) l.Add(s.first());
            return l;
        }
    }
}
