using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using clojure.lang;

namespace Cleat.Compiler.Emit;

/// <summary>
/// 持久化单元的常量表（原 Clojure 编译器的 encode-constant 与发射器的 fasl 编码合一）：Clojure 值 → 二进制 fasl，格式与 Cleat.Core.Runtime.Fasl 一致。
/// 先建中立编码（标签 + 项），哈希 map / set 的项按编码文本排序（迭代顺序依赖哈希，Type、Var 的哈希按对象标识、每个进程不同；
/// 加载时按值重建，顺序不影响语义，排序使两次编译的 dll 相同），再写字节。只有 Clojure 的值；记录、集合按值重建，不共享。
/// </summary>
internal static class ConstantEncoder
{
    private const byte Version = 2;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("CLFASL");

    private const byte Nil = 0x00, True = 0x01, False = 0x02, Fix = 0x10, Long = 0x18, Double = 0x19, BigIntTag = 0x1A, BigDec = 0x1B, RatioTag = 0x1C,
        Char = 0x28, Str = 0x29, Sym = 0x2A, Kw = 0x2B, VarTag = 0x2C, Table = 0x32, List = 0x38, Vec = 0x39, Map = 0x3A, Set = 0x3B,
        SortedMap = 0x3C, SortedSet = 0x3D, Meta = 0x50, Regex = 0x51, TypeTag = 0x54;

    /// <summary>中立编码：标签名与项（string / long / int / Enc）。</summary>
    private sealed class Enc
    {
        public Enc(string head, params object[] items)
        {
            Head = head;
            Items = items;
        }

        public string Head { get; }
        public object[] Items { get; }

        private string _text;

        /// <summary>编码文本（codegen.clj encoded-text），排序用。</summary>
        public string Text
        {
            get
            {
                if (_text != null) return _text;
                var sb = new StringBuilder();
                Write(sb, this);
                return _text = sb.ToString();
            }
        }

        private static void Write(StringBuilder sb, object x)
        {
            switch (x)
            {
                case Enc e:
                    sb.Append('(').Append(e.Head);
                    foreach (var i in e.Items)
                    {
                        sb.Append(' ');
                        Write(sb, i);
                    }
                    sb.Append(')');
                    break;
                case string s: sb.Append('"').Append(s.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"'); break;
                default: sb.Append(Convert.ToString(x, System.Globalization.CultureInfo.InvariantCulture)); break;
            }
        }
    }

    private static List<object> RawItems(object x)
    {
        var items = new List<object>();
        if (x is IPersistentMap)
            for (ISeq s = RT.seq(x); s != null; s = s.next())
            {
                var e = (IMapEntry)s.first();
                items.Add(e.key());
                items.Add(e.val());
            }
        else if (x is Seqable or ISeq || x == null)
            for (ISeq s = RT.seq(x); s != null; s = s.next()) items.Add(s.first());
        else if (x is IEnumerable en)
            foreach (var o in en) items.Add(o);
        return items;
    }

    private static string DoubleHex(double x)
    {
        var bs = BitConverter.GetBytes(x);
        if (BitConverter.IsLittleEndian) Array.Reverse(bs);
        return BitConverter.ToString(bs).Replace("-", "");
    }

    private static object[] Named(string ns, string name) => ns == null ? new object[] { name } : new object[] { ns, name };

    private static Enc Encode(object x)
    {
        Enc WithMeta(Enc e) => x is IMeta m && m.meta() is { } meta ? new Enc("meta", Encode(meta), e) : e;
        object[] Encs(IEnumerable<object> xs) => xs.Select(i => (object)Encode(i)).ToArray();
        switch (x)
        {
            case null: return new Enc("nil");
            case Var v: return new Enc("var", v.Namespace.Name.Name, v.Symbol.Name);
            case long l: return new Enc("long", l);
            case int n: return new Enc("fix", n);
            case double d: return new Enc("double", DoubleHex(d));
            case bool b: return new Enc(b ? "true" : "false");
            case char c: return new Enc("char", (int)c);
            case string s: return new Enc("str", s);
            case Keyword k: return new Enc("kw", Named(k.Namespace, k.Name));
            case Symbol sym: return WithMeta(new Enc("csym", Named(sym.Namespace, sym.Name)));
            case IPersistentVector: return WithMeta(new Enc("cvec", Encs(RawItems(x))));
            case PersistentTreeMap: return WithMeta(new Enc("sorted-map", Encs(RawItems(x))));
            case IPersistentMap:
                {
                    var encs = RawItems(x).Select(Encode).ToList();
                    var pairs = new List<(Enc K, Enc V)>();
                    for (int i = 0; i < encs.Count; i += 2) pairs.Add((encs[i], encs[i + 1]));
                    return WithMeta(new Enc("map", pairs.OrderBy(p => p.K.Text, StringComparer.Ordinal).SelectMany(p => new object[] { p.K, p.V }).ToArray()));
                }
            case PersistentTreeSet: return WithMeta(new Enc("sorted-set", Encs(RawItems(x))));
            case IPersistentSet: return WithMeta(new Enc("set", RawItems(x).Select(Encode).OrderBy(e => e.Text, StringComparer.Ordinal).Cast<object>().ToArray()));
            case IPersistentList or ISeq: return WithMeta(new Enc("clist", Encs(RawItems(x))));
            case BigInt or System.Numerics.BigInteger: return new Enc("bigint", x.ToString());
            case BigDecimal: return new Enc("bigdec", x.ToString());
            case Ratio: return new Enc("ratio", x.ToString());
            case System.Text.RegularExpressions.Regex r: return new Enc("regex", r.ToString());
            case Type t: return new Enc("type", TypeName(t));
            default: throw new InvalidOperationException($"encode-constant: cannot encode object {RT.printString(x)}");
        }
    }

    /// <summary>
    /// 类型常量的名字（cleat-167）：封闭泛型按 ClojureCLR 的语法、实参递归、**不带程序集名**写（<c>SortedSet`1[my.ns.Entry]</c>）。
    /// Type.FullName 会把实参的程序集写进去——编包时包里 deftype 的程序集是临时的 eval 单元（eval$s104），从包加载时找不到；
    /// 非泛型类型本来就只按全名找。Fasl 读回时 TypeResolver 解析这种写法（实参在已加载的程序集里找）。
    /// </summary>
    internal static string TypeName(Type t)
    {
        if (t.IsArray) return TypeName(t.GetElementType()) + (t.GetArrayRank() == 1 ? "[]" : "[" + new string(',', t.GetArrayRank() - 1) + "]");
        if (!t.IsGenericType || t.IsGenericTypeDefinition) return t.FullName;
        return t.GetGenericTypeDefinition().FullName + "[" + string.Join(",", t.GetGenericArguments().Select(TypeName)) + "]";
    }

    /// <summary>常量表 → fasl 字节（未压缩）。</summary>
    public static byte[] EncodeTable(IList<object> constants)
    {
        var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, true))
        {
            w.Write(Magic);
            w.Write(Version);
            w.Write(Table);
            w.Write7BitEncodedInt(constants.Count);
            foreach (var c in constants) Write(w, Encode(c));
        }
        return ms.ToArray();
    }

    private static void String(BinaryWriter w, string s)
    {
        w.Write7BitEncodedInt(s.Length);
        foreach (char c in s) w.Write((ushort)c);
    }

    private static void Named(BinaryWriter w, Enc e)
    {
        if (e.Items.Length == 1) { String(w, ""); String(w, (string)e.Items[0]); }
        else { String(w, (string)e.Items[0]); String(w, (string)e.Items[1]); }
    }

    private static void Items(BinaryWriter w, Enc e)
    {
        w.Write7BitEncodedInt(e.Items.Length);
        foreach (var i in e.Items) Write(w, (Enc)i);
    }

    private static void Pairs(BinaryWriter w, Enc e)
    {
        w.Write7BitEncodedInt(e.Items.Length / 2);
        foreach (var i in e.Items) Write(w, (Enc)i);
    }

    private static void Write(BinaryWriter w, Enc e)
    {
        switch (e.Head)
        {
            case "nil": w.Write(Nil); return;
            case "true": w.Write(True); return;
            case "false": w.Write(False); return;
            case "fix": w.Write(Fix); w.Write((int)e.Items[0]); return;
            case "long": w.Write(Long); w.Write((long)e.Items[0]); return;
            case "double": w.Write(Double); w.Write(unchecked((long)ulong.Parse((string)e.Items[0], System.Globalization.NumberStyles.AllowHexSpecifier))); return;
            case "bigint": w.Write(BigIntTag); String(w, (string)e.Items[0]); return;
            case "bigdec": w.Write(BigDec); String(w, (string)e.Items[0]); return;
            case "ratio": w.Write(RatioTag); String(w, (string)e.Items[0]); return;
            case "char": w.Write(Char); w.Write((ushort)(int)e.Items[0]); return;
            case "str": w.Write(Str); String(w, (string)e.Items[0]); return;
            case "csym": w.Write(Sym); Named(w, e); return;
            case "kw": w.Write(Kw); Named(w, e); return;
            case "var": w.Write(VarTag); String(w, (string)e.Items[0]); String(w, (string)e.Items[1]); return;
            case "clist": w.Write(List); Items(w, e); return;
            case "cvec": w.Write(Vec); Items(w, e); return;
            case "set": w.Write(Set); Items(w, e); return;
            case "sorted-set": w.Write(SortedSet); Items(w, e); return;
            case "map": w.Write(Map); Pairs(w, e); return;
            case "sorted-map": w.Write(SortedMap); Pairs(w, e); return;
            case "meta": w.Write(Meta); Write(w, (Enc)e.Items[0]); Write(w, (Enc)e.Items[1]); return;
            case "regex": w.Write(Regex); String(w, (string)e.Items[0]); return;
            case "type": w.Write(TypeTag); String(w, (string)e.Items[0]); return;
            default: throw new InvalidOperationException("encode-constant: unknown encoding " + e.Head);
        }
    }
}
