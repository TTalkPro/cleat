using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Reflection;
using clojure.lang;

namespace Cleat.Core.Runtime;

/// <summary>
/// 接口文件 `名字.ifc`（designs/05-cil-backend.md §6、04 §6）：编译产物旁的 EDN，记录镜像 id（MVID）与其中的已知函数
/// （Var → 类名与各 arity），供编译器做跨单元直接链接。加载 dll 时若 MVID 相符就登记；boot 镜像的接口文件另有 BootInterface.Check。
/// </summary>
public sealed class InterfaceFile
{
    public sealed class Entry
    {
        public Symbol Var;          // ns/name
        public string ClassName;    // 函数类（含程序集名："T, asm"）
        public int[] Arities;       // 固定参数个数；变长用 -(required + 1) 表示
        public bool HasStatic;      // 有 invokeStatic
        // 可内联的固定子句（cleat-042）：参数个数 → L0 子句的 fasl 字节（Cleat.Core.Runtime.Fasl，[:cleat/l0 子句]）；没有时为 null。
        // 不写进 .ifc（启动时要解析），写在旁边的二进制文件 .inl，第一次要用时才读（FindInline）
        public Dictionary<int, byte[]> Inline;
        internal InterfaceFile Owner;
    }

    /// <summary>.ifc 旁的内联体文件（cleat-042）</summary>
    public static string InlinePathFor(string ifcPath) => Path.ChangeExtension(ifcPath, ".inl");

    private string _inlinePath;        // 读入时：旁边的 .inl（还没读；包程序集里的是 "程序集:cleat.inl"）
    private Func<Stream> _inlineSource;
    private bool _inlineLoaded;

    public string AssemblyName;
    public Guid ImageId;
    public List<Entry> Entries = new List<Entry>();

    private static readonly Keyword KAsm = Keyword.intern("assembly"), KId = Keyword.intern("image-id"), KEntries = Keyword.intern("entries"),
      KVar = Keyword.intern("var"), KClass = Keyword.intern("class"), KArities = Keyword.intern("arities"), KStatic = Keyword.intern("static");

    public string ToEdn()
    {
        var entries = Entries.OrderBy(e => e.Var.ToString(), StringComparer.Ordinal).Select(e =>
          (object)RT.map(KVar, e.Var, KClass, e.ClassName, KArities, LazilyPersistentVector.createOwning(e.Arities.Select(a => (object)(long)a).ToArray()), KStatic, e.HasStatic)).ToArray();
        var m = RT.map(KAsm, AssemblyName, KId, ImageId.ToString(), KEntries, LazilyPersistentVector.createOwning(entries));
        return ";; cleat 接口文件（designs/05 §6）；由编译驱动生成\n" + RT.printString(m) + "\n";
    }

    public static InterfaceFile Parse(string text)
    {
        var m = (IPersistentMap)EdnReader.readString(text, RT.map(Keyword.intern("eof"), null));
        var f = new InterfaceFile { AssemblyName = (string)m.valAt(KAsm), ImageId = Guid.Parse((string)m.valAt(KId)) };
        for (ISeq s = RT.seq(m.valAt(KEntries)); s != null; s = s.next())
        {
            var e = (IPersistentMap)s.first();
            f.Entries.Add(new Entry
            {
                Var = (Symbol)e.valAt(KVar),
                ClassName = (string)e.valAt(KClass),
                Arities = ((IPersistentVector)e.valAt(KArities)).seq() is ISeq a ? RT.toArray(a).Select(x => (int)(long)x).ToArray() : Array.Empty<int>(),
                HasStatic = RT.booleanCast(e.valAt(KStatic)),
            });
        }
        return f;
    }

    public void Save(string path)
    {
        File.WriteAllText(path, ToEdn());
        string inl = InlinePathFor(path);
        if (File.Exists(inl)) File.Delete(inl);
        var withInline = Entries.Where(e => e.Inline is { Count: > 0 }).OrderBy(e => e.Var.ToString(), StringComparer.Ordinal).ToList();
        if (withInline.Count == 0) return;
        // 格式：镜像 id、条目数，每条 Var 名、子句数、(参数个数 长度 字节)…
        using var w = new BinaryWriter(File.Create(inl));
        w.Write(ImageId.ToString());
        w.Write(withInline.Count);
        foreach (var e in withInline)
        {
            w.Write(e.Var.ToString());
            w.Write(e.Inline.Count);
            foreach (var kv in e.Inline.OrderBy(kv => kv.Key)) { w.Write(kv.Key); w.Write(kv.Value.Length); w.Write(kv.Value); }
        }
    }

    public static InterfaceFile Load(string path)
    {
        var f = Parse(File.ReadAllText(path));
        string inl = InlinePathFor(path);
        if (File.Exists(inl)) { f._inlinePath = inl; f._inlineSource = () => File.OpenRead(inl); }
        return f;
    }

    /// <summary>已知函数的可内联子句（参数个数 n）的 fasl 字节；没有时 null。第一次用到时读 .inl（镜像 id 不符的旧文件忽略）</summary>
    public static byte[] FindInline(Entry e, int n)
    {
        var f = e.Owner;
        if (f != null && !f._inlineLoaded)
            lock (f)
                if (!f._inlineLoaded)
                {
                    try { f.LoadInline(); }
                    catch (Exception ex) { RT.errPrintWriter().WriteLine($"warning: ignoring inline file {f._inlinePath}: {ex.Message}"); }
                    f._inlineLoaded = true;
                }
        return e.Inline != null && e.Inline.TryGetValue(n, out var b) ? b : null;
    }

    private void LoadInline()
    {
        if (_inlineSource == null) return;
        using var r = new BinaryReader(_inlineSource());
        if (!Guid.TryParse(r.ReadString(), out var id) || id != ImageId) return;
        var byVar = Entries.ToDictionary(e => e.Var.ToString(), StringComparer.Ordinal);
        int count = r.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            string v = r.ReadString();
            int k = r.ReadInt32();
            var d = new Dictionary<int, byte[]>();
            for (int j = 0; j < k; j++) { int n = r.ReadInt32(); int len = r.ReadInt32(); d[n] = r.ReadBytes(len); }
            if (byVar.TryGetValue(v, out var e)) e.Inline = d;
        }
    }

    /// <summary>dll 旁的接口文件路径：`x.y.dll` → `x.y.ifc`（包程序集的接口文件是嵌入资源，见 TryRegisterEmbedded）。</summary>
    public static string PathFor(string dllPath) => Path.ChangeExtension(dllPath, ".ifc");

    // ———————————————— 已知函数登记 ————————————————

    private static readonly ConcurrentDictionary<Symbol, Entry> s_known = new ConcurrentDictionary<Symbol, Entry>();

    /// <summary>已加载产物中的已知函数（编译器跨单元直接链接用）。</summary>
    public static Entry FindKnown(Symbol var) => s_known.TryGetValue(var, out var e) ? e : null;

    public static int KnownCount => s_known.Count;

    /// <summary>登记接口文件；程序集的 MVID 与文件记录的不符时不登记并返回 false（镜像重建后的旧文件）。</summary>
    public static bool Register(InterfaceFile f, Assembly asm)
    {
        if (asm.ManifestModule.ModuleVersionId != f.ImageId) return false;
        foreach (var e in f.Entries) { e.Owner = f; s_known[e.Var] = e; }
        return true;
    }

    /// <summary>加载 dll 后调用：有同名 .ifc 且 MVID 相符则登记。</summary>
    public static bool TryRegisterBeside(string dllPath, Assembly asm)
    {
        string p = PathFor(dllPath);
        if (!File.Exists(p)) return false;
        try { return Register(Load(p), asm); }
        catch (Exception e) { RT.errPrintWriter().WriteLine($"warning: ignoring interface file {p}: {e.Message}"); return false; }
    }

    /// <summary>包程序集里的接口文件（cleat-067）：嵌入资源 <see cref="ResourceName"/> 与 <see cref="InlineResourceName"/>。</summary>
    public const string ResourceName = "cleat.ifc", InlineResourceName = "cleat.inl";

    /// <summary>
    /// 程序集里嵌入了接口文件时登记（包程序集，cleat-067）。接口与程序集一起发射，不核对镜像 id（嵌入的接口文件的 id 为空）。
    /// </summary>
    public static bool TryRegisterEmbedded(Assembly asm)
    {
        try
        {
            using var stream = asm.GetManifestResourceStream(ResourceName);
            if (stream is null) return false;
            var f = Parse(new StreamReader(stream).ReadToEnd());
            if (asm.GetManifestResourceInfo(InlineResourceName) is not null)
            {
                f._inlinePath = asm.GetName().Name + ":" + InlineResourceName;
                f._inlineSource = () => asm.GetManifestResourceStream(InlineResourceName);
            }
            foreach (var e in f.Entries) { e.Owner = f; s_known[e.Var] = e; }
            return true;
        }
        catch (Exception e)
        {
            RT.errPrintWriter().WriteLine($"warning: ignoring the interface file in {asm.GetName().Name}: {e.Message}");
            return false;
        }
    }

    public static void ClearKnown() => s_known.Clear();
}

/// <summary>编译产物（用了 boot 镜像接口的包程序集）在入口开头调用：镜像 id 不符则该产物是对旧镜像编译的，报错要求重编。</summary>
public static class BootInterface
{
    public static Guid? CurrentImageId { get; set; }

    public sealed class StaleModuleException : Exception
    {
        public StaleModuleException(string message) : base(message) { }
    }

    public static void Check(string imageId)
    {
        if (CurrentImageId == null) return;
        if (!Guid.TryParse(imageId, out var g) || g != CurrentImageId.Value)
            throw new StaleModuleException($"module was compiled against boot image {imageId}, but the running image is {CurrentImageId}; recompile it");
    }
}
