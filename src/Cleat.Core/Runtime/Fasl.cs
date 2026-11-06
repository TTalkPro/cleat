using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using clojure.lang;

namespace Cleat.Core.Runtime;

/// <summary>
/// Clojure 数据的 fasl（cleat-092；designs/05 §3）：编译器写进程序集的常量表（Cleat.Compiler 的 ConstantEncoder 编码、Deflate 压缩，
/// 类型初始化器经 <see cref="ReadPackedTable"/> 读回）与接口文件旁的内联体 .inl（<see cref="Write"/> / <see cref="Read"/>）。
/// 标签与 ConstantEncoder 一致（标签值沿用 IronScheme 的联合编码，Scheme 专有的标签随元层在 cleat-093 删除）；fix 读成 Int32，
/// 表的外层是 Table 标签（object[]）。
/// def / ref 表示共享与环（不可变集合先读元素再建，环不支持）。
/// </summary>
public static class Fasl
{
    public const byte Version = 2;
    private static readonly byte[] s_magic = Encoding.ASCII.GetBytes("CLFASL");

    // 标签（与 Cleat.Compiler 的 ConstantEncoder 一致）：0x0_ 单例；0x1_ 数；0x2_ 字符、字符串、符号；0x3_ 序列；0x5_ 其他；0x6_ 共享
    private const byte Nil = 0x00, True = 0x01, False = 0x02,
      Fix = 0x10,
      Long = 0x18, Double = 0x19, BigIntTag = 0x1A, BigDec = 0x1B, RatioTag = 0x1C,
      Char = 0x28, CljStr = 0x29, CljSym = 0x2A, Kw = 0x2B, VarTag = 0x2C,
      Table = 0x32,
      CljList = 0x38, CljVec = 0x39, Map = 0x3A, Set = 0x3B, SortedMap = 0x3C, SortedSet = 0x3D, Queue = 0x3E,
      Meta = 0x50, RegexTag = 0x51, Inst = 0x52, Uuid = 0x53, TypeTag = 0x54, Record = 0x55,
      Def = 0x60, Ref = 0x61;

    /// <summary>程序集里 Deflate 压缩的常量表 → object[]（发射器 EmitConstants 生成的类型初始化器调用）。</summary>
    public static object[] ReadPackedTable(byte[] packed)
    {
        using var z = new System.IO.Compression.DeflateStream(new MemoryStream(packed), System.IO.Compression.CompressionMode.Decompress);
        using var r = new BinaryReader(z);
        return ToTable(Read(r));
    }

    /// <summary>未压缩的常量表（测试与工具用）。</summary>
    public static object[] ReadTable(byte[] data) => ToTable(Read(data));

    private static object[] ToTable(object x)
    {
        if (x is object[] v) return v;
        if (x is IPersistentVector pv)
        {
            var table = new object[pv.count()];
            for (int i = 0; i < table.Length; i++) table[i] = pv.nth(i);
            return table;
        }
        throw new InvalidDataException("fasl: constant table must be a vector");
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

    /// <summary>
    /// 类型名 → Type：TypeResolver（ClojureCLR 的类型名语法）；找不到时类型可能是别的命名空间的 AOT 程序集里的 deftype，
    /// 而那个命名空间还没加载（cleat-063）：先载入它的程序集再找。
    /// </summary>
    private static Type ResolveType(string name) =>
      TypeResolver.Resolve(name, Array.Empty<string>())
      ?? Loader.LoadAssemblyDefining(name)
      ?? throw new InvalidDataException("fasl: type not found: " + name);

    private sealed class Reader
    {
        private readonly BinaryReader _r;
        private readonly Dictionary<int, object> _defs = new();
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

        private object[] KeyValues()
        {
            var kvs = new object[Count() * 2];
            for (int i = 0; i < kvs.Length; i++) kvs[i] = Object();
            return kvs;
        }

        private static string NsOrNull(string ns) => ns.Length == 0 ? null : ns;

        public object Object()
        {
            int def = _pendingDef;
            _pendingDef = -1;
            byte tag = _r.ReadByte();
            switch (tag)
            {
                case Nil: return Register<object>(def, null);
                case True: return Register<object>(def, Truth.True);
                case False: return Register<object>(def, Truth.False);
                case Fix: return Register<object>(def, _r.ReadInt32());
                case Double: return Register<object>(def, BitConverter.Int64BitsToDouble(_r.ReadInt64()));
                case Long: return Register<object>(def, _r.ReadInt64());
                case BigIntTag: return Register<object>(def, BigInt.fromBigInteger(BigInteger.Parse(String())));
                case BigDec: return Register<object>(def, BigDecimal.Parse(String()));
                case RatioTag: return Register(def, LispReader.MatchNumber(String()) ?? throw new InvalidDataException("bad ratio in fasl"));
                case Char: return Register<object>(def, (char)_r.ReadUInt16());
                case CljStr: return Register<object>(def, String());
                case CljSym: { string ns = String(), name = String(); return Register<object>(def, Symbol.intern(NsOrNull(ns), name)); }
                case Kw: { string ns = String(), name = String(); return Register<object>(def, Keyword.intern(NsOrNull(ns), name)); }
                case VarTag: { string ns = String(), name = String(); return Register<object>(def, Var.intern(Namespace.findOrCreatePending(Symbol.intern(ns)), Symbol.intern(name))); }
                case Table:
                    {
                        // 常量表的外层；元素读成 object[]
                        var v = Register(def, new object[Count()]);
                        for (int i = 0; i < v.Length; i++) v[i] = Object();
                        return v;
                    }
                case CljList:
                    {
                        var items = Items();
                        return Register<object>(def, items.Length == 0 ? PersistentList.EMPTY : PersistentList.create(items));
                    }
                case CljVec: return Register<object>(def, LazilyPersistentVector.createOwning(Items()));
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
                        var kvs = KeyValues();
                        return Register<object>(def, kvs.Length <= 16 ? PersistentArrayMap.createWithCheck(kvs) : PersistentHashMap.createWithCheck(kvs));
                    }
                case SortedMap: return Register<object>(def, PersistentTreeMap.create(RT.seq(KeyValues())));
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
                        int n = Count();
                        return _defs.TryGetValue(n, out var o) ? o : throw new InvalidDataException("fasl: reference to undefined object " + n);
                    }
                default: throw new InvalidDataException($"fasl: unknown tag 0x{tag:x2}");
            }
        }
    }

    /// <summary>Clojure 值 → fasl（接口文件旁的 .inl 等）。</summary>
    public static byte[] Write(object obj)
    {
        var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, true))
        {
            w.Write(s_magic);
            w.Write(Version);
            new Writer(w).Run(obj);
        }
        return ms.ToArray();
    }

    /// <summary>可共享的对象：identical? 身份有意义，出现多次时用 def/ref。</summary>
    private static bool Shareable(object o) =>
      o is object[] || o is IPersistentCollection || (o is IObj io && io.meta() != null && !(o is Var));

    private sealed class Writer
    {
        private readonly BinaryWriter _w;
        private readonly Dictionary<object, int> _counts = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<object, int> _ids = new(ReferenceEqualityComparer.Instance);

        public Writer(BinaryWriter w) { _w = w; }

        public void Run(object root)
        {
            Count(root);
            Object(root);
        }

        // 第一遍：统计可共享对象出现的次数（已见过的不再深入）
        private void Count(object o)
        {
            if (!Shareable(o)) return;
            _counts.TryGetValue(o, out int n);
            _counts[o] = n + 1;
            if (n > 0) return;
            if (o is IObj io && io.meta() != null && !(o is Var)) Count(io.meta());
            switch (o)
            {
                case object[] v: foreach (var e in v) Count(e); return;
                case IPersistentMap m:
                    for (var s = m.seq(); s != null; s = s.next()) { var e = (IMapEntry)s.first(); Count(e.key()); Count(e.val()); }
                    return;
                case IPersistentCollection pc:
                    for (var s = pc.seq(); s != null; s = s.next()) Count(s.first());
                    return;
                default: return;
            }
        }

        private bool Shared(object o) => _counts.TryGetValue(o, out int n) && n > 1;

        private void String(string s)
        {
            _w.Write7BitEncodedInt(s.Length);
            foreach (char c in s) _w.Write((ushort)c);
        }

        private void Named(string ns, string name) { String(ns ?? ""); String(name); }

        private void Items(byte tag, ISeq s, int count)
        {
            _w.Write(tag);
            _w.Write7BitEncodedInt(count);
            for (; s != null; s = s.next()) Object(s.first());
        }

        private void Pairs(byte tag, IPersistentMap m)
        {
            _w.Write(tag);
            _w.Write7BitEncodedInt(m.count());
            for (var s = m.seq(); s != null; s = s.next())
            {
                var e = (IMapEntry)s.first();
                Object(e.key());
                Object(e.val());
            }
        }

        private void Object(object o)
        {
            if (Shareable(o))
            {
                if (_ids.TryGetValue(o, out int id)) { _w.Write(Ref); _w.Write7BitEncodedInt(id); return; }
                if (Shared(o))
                {
                    id = _ids.Count;
                    _ids[o] = id;
                    _w.Write(Def);
                    _w.Write7BitEncodedInt(id);
                }
            }
            // 元数据：(meta m x)，x 去掉元数据后再写
            if (o is IObj io && io.meta() != null && !(o is Var))
            {
                _w.Write(Meta);
                Object(io.meta());
                o = io.withMeta(null);
            }
            switch (o)
            {
                case null: _w.Write(Nil); return;
                case bool b: _w.Write(b ? True : False); return;
                case int i: _w.Write(Fix); _w.Write(i); return;
                case long l: _w.Write(Long); _w.Write(l); return;
                case double d: _w.Write(Double); _w.Write(BitConverter.DoubleToInt64Bits(d)); return;
                case BigInt bi: _w.Write(BigIntTag); String(bi.ToString()); return;
                case BigInteger bI: _w.Write(BigIntTag); String(bI.ToString()); return;
                case BigDecimal bd: _w.Write(BigDec); String(bd.ToString()); return;
                case Ratio r: _w.Write(RatioTag); String(r.ToString()); return;
                case char c: _w.Write(Char); _w.Write((ushort)c); return;
                case string s: _w.Write(CljStr); String(s); return;
                case Keyword k: _w.Write(Kw); Named(k.Namespace, k.Name); return;
                case Symbol sym: _w.Write(CljSym); Named(sym.Namespace, sym.Name); return;
                case Var v: _w.Write(VarTag); Named(v.Namespace.Name.Name, v.Symbol.Name); return;
                case Regex re: _w.Write(RegexTag); String(re.ToString()); return;
                case DateTime dt: _w.Write(Inst); String(dt.ToString("o", System.Globalization.CultureInfo.InvariantCulture)); return;
                case Guid g: _w.Write(Uuid); String(g.ToString()); return;
                case Type t: _w.Write(TypeTag); String(t.FullName); return;
                case object[] arr:
                    _w.Write(Table);
                    _w.Write7BitEncodedInt(arr.Length);
                    foreach (var e in arr) Object(e);
                    return;
                case IRecord rec:
                    {
                        var m = (IPersistentMap)rec;
                        _w.Write(Record);
                        String(rec.GetType().FullName);
                        _w.Write7BitEncodedInt(m.count() * 2);
                        for (var s = m.seq(); s != null; s = s.next()) { var e = (IMapEntry)s.first(); Object(e.key()); Object(e.val()); }
                        return;
                    }
                case PersistentTreeMap tm: Pairs(SortedMap, tm); return;
                case PersistentTreeSet ts: Items(SortedSet, ts.seq(), ts.count()); return;
                case PersistentQueue q: Items(Queue, q.seq(), q.count()); return;
                case IPersistentMap pm: Pairs(Map, pm); return;
                case IPersistentSet ps: Items(Set, ps.seq(), ps.count()); return;
                case IPersistentVector pv: Items(CljVec, pv.seq(), pv.count()); return;
                case IPersistentList _:
                case ISeq _: { var s = RT.seq(o); Items(CljList, s, RT.count(s)); return; }
                default: throw new InvalidDataException("fasl: cannot write " + o.GetType().FullName);
            }
        }
    }
}
