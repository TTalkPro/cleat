using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using BigInteger = System.Numerics.BigInteger;
using Clj = clojure.lang;

namespace Cleat.Scheme;

/// <summary>
/// 二进制 fasl（IronScheme designs/04 §3；cleat designs/05 §3、15 §1）：编译产物的常量表、serialize-port 与接口文件的格式。
/// 发射器（Cleat.Emit.FaslEncoder）把中立编码写成同一格式；这里负责读回对象，并能把对象直接写出。
/// **联合编码**：IronScheme 的标签（Scheme 值：符号按名字驻留、记录类型按 uid 取回已登记的同一 RTD）加 cleat 的 Clojure 标签
/// （clojure.lang 的符号、关键字、Var、持久集合、元数据、正则、类型、记录），每个标签一个独立字节，一个流里可以混用；
/// def / ref 表示共享与环。Cleat.Core.Runtime.Fasl 已退役，Clojure 数据也由这里读写（Cleat.Scheme 引用 Cleat.Core）。
/// </summary>
public static class Fasl
{
    public const byte Version = 2;   // 2：'() 是 EmptyList（标签 Empty），Nil 标签表示 #nil（null）（cleat-058）
    private static readonly byte[] s_magic = Encoding.ASCII.GetBytes("CLFASL");

    // 标签（与 Cleat.Emit.FaslEncoder 一致）：0x0_ 单例；0x1_ 数；0x2_ 字符、字符串、符号；0x3_ 序列；0x4_ Scheme 记录等；0x5_ Clojure 其他；0x6_ 共享
    public const byte Nil = 0x00, True = 0x01, False = 0x02, Unspec = 0x03, EofTag = 0x04, Empty = 0x05,
      Fix = 0x10, Big = 0x11, Rat = 0x12, Flo = 0x13, Cpx = 0x14,
      Long = 0x18, Double = 0x19, BigIntTag = 0x1A, BigDec = 0x1B, RatioTag = 0x1C,
      Chr = 0x20, Str = 0x21, MStr = 0x22, Sym = 0x23,
      Char = 0x28, CljStr = 0x29, CljSym = 0x2A, Kw = 0x2B, VarTag = 0x2C,
      Pair = 0x30, List = 0x31, Vec = 0x32, Bv = 0x33,
      CljList = 0x38, CljVec = 0x39, Map = 0x3A, Set = 0x3B, SortedMap = 0x3C, SortedSet = 0x3D, Queue = 0x3E,
      Rtd = 0x40, Rec = 0x41, Ann = 0x42, EqHt = 0x43, Cnd = 0x44, Rcd = 0x45,
      Meta = 0x50, RegexTag = 0x51, Inst = 0x52, Uuid = 0x53, TypeTag = 0x54, Record = 0x55,
      Def = 0x60, Ref = 0x61;

    #region 读

    /// <summary>常量表：发射器把 Deflate 压缩的 fasl 作为初始化数据嵌入程序集，类型初始化器取出后调用本方法。</summary>
    public static object[] ReadPackedTable(byte[] packed)
    {
        using var z = new System.IO.Compression.DeflateStream(new MemoryStream(packed), System.IO.Compression.CompressionMode.Decompress);
        using var r = new BinaryReader(z);
        return ToTable(Read(r));
    }

    /// <summary>未压缩的常量表（测试与工具用）。</summary>
    public static object[] ReadTable(byte[] data) => ToTable(Read(data));

    /// <summary>常量表在 fasl 里是一个向量（发射器 EncodeTable 写 Scheme 向量；也接受 Clojure 向量）。</summary>
    private static object[] ToTable(object x)
    {
        if (x is object[] v) return v;
        if (x is Clj.IPersistentVector pv)
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

    /// <summary>Clojure 的类型名 → Type：Cleat.Core 的 TypeResolver（ClojureCLR 的类型名语法）。</summary>
    /// 找不到时，类型可能是别的命名空间的 AOT 程序集里的 deftype，而那个命名空间还没加载（cleat-063）：先载入它的程序集再找。
    private static Type ResolveClojureType(string name) =>
      Cleat.Core.Runtime.TypeResolver.Resolve(name, Array.Empty<string>())
      ?? Cleat.Core.Runtime.Loader.LoadAssemblyDefining(name)
      ?? throw new InvalidDataException("fasl: type not found: " + name);

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

        // 复合对象在读子项之前登记，环才能指回自己
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
                // ———— 单例 ————
                case Nil: return Register<object>(def, null);
                case Empty: return Register<object>(def, EmptyList.Instance);
                case True: return Register(def, RT.True);
                case False: return Register(def, RT.False);
                case Unspec: return Register<object>(def, Unspecified.Instance);
                case EofTag: return Register(def, Builtins.EofObject());
                // ———— Scheme 数 ————
                case Fix: return Register(def, RT.Box(_r.ReadInt32()));
                case Big:
                case Rat: return Register(def, Builtins.ParseNumber(String()) ?? throw new InvalidDataException("bad number in fasl"));
                case Flo: return Register<object>(def, BitConverter.Int64BitsToDouble(_r.ReadInt64()));
                case Cpx:
                    {
                        var re = Object();
                        var im = Object();
                        return Register(def, Builtins.MakeRectangular(re, im));
                    }
                // ———— Clojure 数 ————
                case Long: return Register<object>(def, _r.ReadInt64());
                case Double: return Register<object>(def, BitConverter.Int64BitsToDouble(_r.ReadInt64()));
                case BigIntTag: return Register<object>(def, Clj.BigInt.fromBigInteger(Clj.BigInteger.Parse(String())));
                case BigDec: return Register<object>(def, Clj.BigDecimal.Parse(String()));
                case RatioTag: return Register(def, Clj.LispReader.MatchNumber(String()) ?? throw new InvalidDataException("bad ratio in fasl"));
                // ———— 字符、字符串、符号 ————
                case Chr:
                    {
                        int cp = _r.ReadInt32();
                        return Register<object>(def, cp < 0x10000 ? (object)(char)cp : new SchemeChar(cp));
                    }
                case Str: return Register<object>(def, SchemeString.Normalize(String()));   // 含 BMP 外字符的为宽串（iron-055）
                case MStr: return Register<object>(def, SchemeString.MutableFrom(String()));
                case Sym: return Register<object>(def, Symbol.Intern(String()));
                case Char: return Register<object>(def, (char)_r.ReadUInt16());
                case CljStr: return Register<object>(def, String());
                case CljSym: { string ns = String(), name = String(); return Register<object>(def, Clj.Symbol.intern(NsOrNull(ns), name)); }
                case Kw: { string ns = String(), name = String(); return Register<object>(def, Clj.Keyword.intern(NsOrNull(ns), name)); }
                case VarTag: { string ns = String(), name = String(); return Register<object>(def, Clj.Var.intern(Clj.Namespace.findOrCreatePending(Clj.Symbol.intern(ns)), Clj.Symbol.intern(name))); }
                // ———— Scheme 序列 ————
                case Pair:
                    {
                        var c = Register(def, new Cons(null, null));
                        c.car = Object();
                        c.cdr = Object();
                        return c;
                    }
                case List:
                    {
                        int n = Count();
                        if (n == 0) return Register(def, Object());
                        var head = Register(def, new Cons(null, null));
                        var c = head;
                        for (int i = 0; i < n; i++)
                        {
                            if (i > 0) { var next = new Cons(null, null); c.cdr = next; c = next; }
                            c.car = Object();
                        }
                        c.cdr = Object();
                        return head;
                    }
                case Vec:
                    {
                        var v = Register(def, new object[Count()]);
                        for (int i = 0; i < v.Length; i++) v[i] = Object();
                        return v;
                    }
                case Bv:
                    {
                        int n = Count();
                        return Register<object>(def, _r.ReadBytes(n));
                    }
                // ———— Clojure 集合（不可变：先读元素再建；表内共享经 def/ref 仍可用，环不支持）————
                case CljList:
                    {
                        var items = Items();
                        return Register<object>(def, items.Length == 0 ? Clj.PersistentList.EMPTY : Clj.PersistentList.create(items));
                    }
                case CljVec: return Register<object>(def, Clj.LazilyPersistentVector.createOwning(Items()));
                case Set: return Register<object>(def, Clj.PersistentHashSet.create(Items()));
                case SortedSet: return Register<object>(def, Clj.PersistentTreeSet.create(Clj.RT.seq(Items())));
                case Queue:
                    {
                        Clj.IPersistentCollection q = Clj.PersistentQueue.EMPTY;
                        foreach (var x in Items()) q = q.cons(x);
                        return Register<object>(def, q);
                    }
                case Map:
                    {
                        var kvs = KeyValues();
                        return Register<object>(def, kvs.Length <= 16 ? Clj.PersistentArrayMap.createWithCheck(kvs) : Clj.PersistentHashMap.createWithCheck(kvs));
                    }
                case SortedMap: return Register<object>(def, Clj.PersistentTreeMap.create(Clj.RT.seq(KeyValues())));
                // ———— Scheme 记录、注解、哈希表 ————
                case Rtd:
                    {
                        string name = String();
                        object uid = Object();
                        object parent = Object();
                        bool @sealed = _r.ReadByte() != 0, opaque = _r.ReadByte() != 0;
                        var fields = new object[Count()];
                        for (int i = 0; i < fields.Length; i++)
                        {
                            bool mutable = _r.ReadByte() != 0;
                            fields[i] = Cons.FromArray(Symbol.Intern(mutable ? "mutable" : "immutable"), Symbol.Intern(String()));
                        }
                        // 非生成性：按名字与 uid 取回已登记的同一 RTD；生成性：新建（同一 fasl 内经 def/ref 共享）
                        return Register(def, Records.MakeRecordTypeDescriptor(Symbol.Intern(name), parent, uid, RT.Bool(@sealed), RT.Bool(opaque), fields));
                    }
                case Rec:
                    {
                        var rtd = Object() as RecordTypeDescriptor ?? throw new InvalidDataException("record without type descriptor in fasl");
                        var fields = new object[Count()];
                        var rec = Register(def, rtd.Instantiate(fields));
                        for (int i = 0; i < fields.Length; i++) fields[i] = Object();
                        return rec;
                    }
                case Ann:
                    {
                        var expr = Object();
                        var source = Object();
                        var stripped = Object();
                        return Register(def, Builtins.MakeAnnotation(expr, source, stripped));
                    }
                case EqHt:
                    {
                        // eq 哈希表（make-eq-hashtable 即 System.Collections.Hashtable）；'() 键按 Hashtables 的约定包装
                        int n = Count();
                        var ht = Register(def, new System.Collections.Hashtable(Math.Max(n, 1)));
                        for (int i = 0; i < n; i++)
                        {
                            var k = Object();
                            ht[Hashtables.ToNull(k)] = Object();
                        }
                        return ht;
                    }
                case Rcd:
                    {
                        // 记录构造描述符（没有 protocol 的）：nanopass 把展开时创建的 RCD 作为常量写进生成的代码
                        var rtd = Object();
                        var parent = Object();
                        return Register(def, Records.MakeRecordConstructorDescriptor(rtd, parent, RT.False));
                    }
                case Cnd:
                    {
                        // 复合条件：由简单条件组成，构造时就要有组成部分，所以读完再登记（组成部分不会指回它）
                        var parts = Items();
                        return Register(def, new CompoundCondition(parts));
                    }
                // ———— Clojure 其他 ————
                case Meta:
                    {
                        var m = Object() as Clj.IPersistentMap;
                        var x = Object() as Clj.IObj ?? throw new InvalidDataException("fasl: meta on a non-IObj value");
                        return Register<object>(def, x.withMeta(m));
                    }
                case RegexTag: return Register<object>(def, new Regex(String()));
                case Inst: return Register<object>(def, DateTime.Parse(String(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind));
                case Uuid: return Register<object>(def, Guid.Parse(String()));
                case TypeTag: return Register<object>(def, ResolveClojureType(String()));
                case Record:
                    {
                        // defrecord 的静态 create(IPersistentMap)
                        var t = ResolveClojureType(String());
                        var items = Items();
                        var create = t.GetMethod("create", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Clj.IPersistentMap) }, null)
                                     ?? throw new InvalidDataException("fasl: record type has no create(IPersistentMap): " + t.FullName);
                        if (items.Length % 2 != 0) throw new InvalidDataException("fasl: record needs key / value pairs");
                        return Register(def, create.Invoke(null, new object[] { Clj.PersistentArrayMap.createWithCheck(items) }));
                    }
                // ———— 共享与环 ————
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

    #endregion

    #region serialize-port / deserialize-port（取代旧版 BinaryFormatter，iron-008 转来）

    [Primitive("serialize-port")]
    public static object SerializePort(object obj, object port)
    {
        var stream = Errors.RequiresType<Stream>(port, "serialize-port");
        using (var w = new BinaryWriter(stream, Encoding.UTF8, true)) Write(w, obj);
        return Unspecified.Instance;
    }

    [Primitive("deserialize-port")]
    public static object DeserializePort(object port)
    {
        var stream = Errors.RequiresType<Stream>(port, "deserialize-port");
        using var r = new BinaryReader(stream, Encoding.UTF8, true);
        return Read(r);
    }

    #endregion

    #region 写

    /// <summary>
    /// 对象 → fasl。clojure 为假（缺省，serialize-port）时 CLR 字符串按 Scheme 字符串写出（含 BMP 外字符的读回为宽串 SchemeString，iron-055）；
    /// 为真（Clojure 数据：接口文件等）时写成 Clojure 字符串，读回仍是 System.String。其余类型两边都不歧义。
    /// </summary>
    public static byte[] Write(object obj, bool clojure = false)
    {
        var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, true)) Write(w, obj, clojure);
        return ms.ToArray();
    }

    public static void Write(BinaryWriter w, object obj, bool clojure = false)
    {
        w.Write(s_magic);
        w.Write(Version);
        new Writer(w, clojure).Run(obj);
    }

    /// <summary>可共享的对象：eq? / identical? 身份有意义，出现多次时用 def/ref。</summary>
    private static bool Shareable(object o) =>
      o is Cons || o is object[] || o is byte[] || o is Record || o is RecordTypeDescriptor || o is RecordConstructorDescriptor
      || o is SchemeString || o is StringBuilder || o is Annotation || IsEqHashtable(o)
      || o is Clj.IPersistentCollection || (o is Clj.IObj io && io.meta() != null && !(o is Clj.Var));

    /// <summary>只支持 eq 哈希表（类型恰为 Hashtable）；带自定义哈希函数的表无法序列化。</summary>
    private static bool IsEqHashtable(object o) => o != null && o.GetType() == typeof(System.Collections.Hashtable);

    private sealed class Writer
    {
        private readonly BinaryWriter _w;
        private readonly bool _clojure;
        private readonly Dictionary<object, int> _counts = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<object, int> _ids = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);

        public Writer(BinaryWriter w, bool clojure) { _w = w; _clojure = clojure; }

        public void Run(object root)
        {
            Count(root);
            Object(root);
        }

        // 第一遍：统计可共享对象出现的次数（已见过的不再深入，所以环也能结束）
        private void Count(object o)
        {
            while (true)
            {
                if (!Shareable(o)) return;
                _counts.TryGetValue(o, out int n);
                _counts[o] = n + 1;
                if (n > 0) return;
                if (o is Clj.IObj io && io.meta() != null && !(o is Clj.Var)) Count(io.meta());
                switch (o)
                {
                    case Cons c: Count(c.car); o = c.cdr; continue;   // 沿表尾循环，长表不递归
                    case object[] v: foreach (var e in v) Count(e); return;
                    case CompoundCondition cc: foreach (var e in cc._conds) Count(e); return;
                    case RecordConstructorDescriptor rcd: Count(rcd.type); Count(rcd.parent); return;
                    case Record r: Count(r.Rtd); foreach (var e in r.Fields) Count(e); return;
                    case RecordTypeDescriptor t: Count(t.uid); Count(t.Parent); return;
                    case Annotation a: Count(a.expression); Count(a.source); Count(a.stripped); return;
                    case System.Collections.Hashtable h:
                        foreach (System.Collections.DictionaryEntry e in h) { Count(Hashtables.FromNull(e.Key)); Count(e.Value); }
                        return;
                    case Clj.IPersistentMap m:
                        for (var s = m.seq(); s != null; s = s.next()) { var e = (Clj.IMapEntry)s.first(); Count(e.key()); Count(e.val()); }
                        return;
                    case Clj.IPersistentCollection pc:
                        for (var s = pc.seq(); s != null; s = s.next()) Count(s.first());
                        return;
                    default: return;
                }
            }
        }

        private bool Shared(object o) => _counts.TryGetValue(o, out int n) && n > 1;

        private void String(string s)
        {
            _w.Write7BitEncodedInt(s.Length);
            foreach (char c in s) _w.Write((ushort)c);
        }

        private void Named(string ns, string name) { String(ns ?? ""); String(name); }

        private void Items(byte tag, Clj.ISeq s, int count)
        {
            _w.Write(tag);
            _w.Write7BitEncodedInt(count);
            for (; s != null; s = s.next()) Object(s.first());
        }

        private void Pairs(byte tag, Clj.IPersistentMap m)
        {
            _w.Write(tag);
            _w.Write7BitEncodedInt(m.count());
            for (var s = m.seq(); s != null; s = s.next())
            {
                var e = (Clj.IMapEntry)s.first();
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
            // Clojure 元数据：(meta m x)，x 去掉元数据后再写
            if (o is Clj.IObj io && io.meta() != null && !(o is Clj.Var))
            {
                _w.Write(Meta);
                Object(io.meta());
                o = io.withMeta(null);
            }
            switch (o)
            {
                // ———— 单例与 Scheme 值 ————
                case null: _w.Write(Nil); return;
                case EmptyList _: _w.Write(Empty); return;
                case bool b: _w.Write(b ? True : False); return;
                case Unspecified _: _w.Write(Unspec); return;
                case int i: _w.Write(Fix); _w.Write(i); return;
                case BigInteger b: _w.Write(Big); String(BigIntegers.ToDecimalString(b)); return;   // 十进制
                case Fraction f: _w.Write(Rat); String(f.ToString()); return;   // "n/d"
                case double d: _w.Write(Flo); _w.Write(BitConverter.DoubleToInt64Bits(d)); return;
                case ComplexFraction cf: _w.Write(Cpx); Object(Builtins.ToExact(cf.Real)); Object(Builtins.ToExact(cf.Imag)); return;
                case Complex64 cd: _w.Write(Cpx); Object(cd.Real); Object(cd.Imag); return;
                case char c: _w.Write(Chr); _w.Write((int)c); return;
                case SchemeChar sc: _w.Write(Chr); _w.Write(sc.Value); return;
                case string s: _w.Write(_clojure ? CljStr : Str); String(s); return;
                case SchemeString ss: _w.Write(ss.IsMutable ? MStr : Str); String(ss.ToString()); return;
                case StringBuilder sb: _w.Write(MStr); String(sb.ToString()); return;
                case Symbol sym: _w.Write(Sym); String(sym.Name); return;
                case Cons c:
                    {
                        // 表尾中未被共享的序对连成一段，用 List 写出
                        var items = new List<object> { c.car };
                        object tail = c.cdr;
                        while (tail is Cons next && !Shared(next)) { items.Add(next.car); tail = next.cdr; }
                        if (items.Count == 1) { _w.Write(Pair); Object(c.car); Object(c.cdr); return; }
                        _w.Write(List);
                        _w.Write7BitEncodedInt(items.Count);
                        foreach (var e in items) Object(e);
                        Object(tail);
                        return;
                    }
                case object[] v: _w.Write(Vec); _w.Write7BitEncodedInt(v.Length); foreach (var e in v) Object(e); return;
                case byte[] bv: _w.Write(Bv); _w.Write7BitEncodedInt(bv.Length); _w.Write(bv); return;
                case RecordTypeDescriptor t:
                    _w.Write(Rtd);
                    String(t.Name);
                    Object(t._generative && !(t.uid is string) ? RT.False : t.uid);
                    Object((object)t.Parent ?? RT.False);
                    _w.Write((byte)(t._sealed ? 1 : 0));
                    _w.Write((byte)(t._opaque ? 1 : 0));
                    _w.Write7BitEncodedInt(t.Fields.Length);
                    foreach (var f in t.Fields) { _w.Write((byte)(f._mutable ? 1 : 0)); String(f.Name); }
                    return;
                case RecordConstructorDescriptor rcd when rcd.protocol == null:
                    _w.Write(Rcd);
                    Object(rcd.type);
                    Object((object)rcd.parent ?? RT.False);
                    return;
                case CompoundCondition cc:
                    _w.Write(Cnd);
                    _w.Write7BitEncodedInt(cc._conds.Length);
                    foreach (var e in cc._conds) Object(e);
                    return;
                case Record r when r.Rtd != null:
                    _w.Write(Rec);
                    Object(r.Rtd);
                    _w.Write7BitEncodedInt(r.Fields.Length);
                    foreach (var e in r.Fields) Object(e);
                    return;
                case Annotation a: _w.Write(Ann); Object(a.expression); Object(a.source); Object(a.stripped); return;
                case System.Collections.Hashtable h when IsEqHashtable(h):
                    {
                        _w.Write(EqHt);
                        _w.Write7BitEncodedInt(h.Count);
                        foreach (var (k, v) in SortedEntries(h)) { Object(k); Object(v); }
                        return;
                    }
                // ———— Clojure 值 ————
                case long l: _w.Write(Long); _w.Write(l); return;
                case Clj.BigInt bi: _w.Write(BigIntTag); String(bi.ToString()); return;
                case Clj.BigInteger bI: _w.Write(BigIntTag); String(bI.ToString()); return;
                case Clj.BigDecimal bd: _w.Write(BigDec); String(bd.ToString()); return;
                case Clj.Ratio r: _w.Write(RatioTag); String(r.ToString()); return;
                case Clj.Keyword k: _w.Write(Kw); Named(k.Namespace, k.Name); return;
                case Clj.Symbol sym: _w.Write(CljSym); Named(sym.Namespace, sym.Name); return;
                case Clj.Var v: _w.Write(VarTag); Named(v.Namespace.Name.Name, v.Symbol.Name); return;
                case Regex re: _w.Write(RegexTag); String(re.ToString()); return;
                case DateTime dt: _w.Write(Inst); String(dt.ToString("o", System.Globalization.CultureInfo.InvariantCulture)); return;
                case Guid g: _w.Write(Uuid); String(g.ToString()); return;
                case Type t: _w.Write(TypeTag); String(t.FullName); return;
                case Clj.IRecord rec:
                    {
                        _w.Write(Record); String(o.GetType().FullName);
                        var m = (Clj.IPersistentMap)rec;
                        _w.Write7BitEncodedInt(m.count() * 2);
                        for (var s = m.seq(); s != null; s = s.next()) { var e = (Clj.IMapEntry)s.first(); Object(e.key()); Object(e.val()); }
                        return;
                    }
                case Clj.PersistentTreeMap tm: Pairs(SortedMap, tm); return;
                case Clj.PersistentTreeSet ts: Items(SortedSet, ts.seq(), ts.count()); return;
                case Clj.PersistentQueue q: Items(Queue, q.seq(), q.count()); return;
                case Clj.IPersistentMap pm: Pairs(Map, pm); return;
                case Clj.IPersistentSet ps: Items(Set, ps.seq(), ps.count()); return;
                case Clj.IPersistentVector pv: Items(CljVec, pv.seq(), pv.count()); return;
                case Clj.IPersistentList _:
                case Clj.ISeq _: { var s = Clj.RT.seq(o); Items(CljList, s, Clj.RT.count(s)); return; }
            }
            if (ReferenceEquals(o, Builtins.EofObject())) { _w.Write(EofTag); return; }
            Errors.AssertionViolation("fasl-write", "cannot serialize object", o);   // 没有钩子时是 SchemeRuntimeError
        }
    }

    /// <summary>条目按键排序（符号、字符串、数按其文本），使相同内容的表写出相同字节；其余键保持枚举顺序。</summary>
    private static List<(object, object)> SortedEntries(System.Collections.Hashtable h)
    {
        var list = new List<(object Key, object Value)>();
        foreach (System.Collections.DictionaryEntry e in h) list.Add((Hashtables.FromNull(e.Key), e.Value));
        static string SortKey(object k) => k switch { Symbol s => "s" + s.Name, string s => "t" + s, int i => "i" + i.ToString("D11"), _ => null };
        if (list.TrueForAll(e => SortKey(e.Key) != null)) list.Sort((a, b) => string.CompareOrdinal(SortKey(a.Key), SortKey(b.Key)));
        return list.ConvertAll(e => (e.Key, e.Value));
    }

    #endregion
}
