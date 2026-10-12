using System;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Cleat.Scheme;

/// <summary>
/// 跨单元直接调用 boot 导出的已知过程（iron-034e，designs/03-compiler.md §4.1 D5）。
/// nano 构建脚本在 boot 镜像旁写出 cleat.scheme.boot.ifc：镜像的编译实例 id（MVID，由内容哈希得出）与每个导出的已知过程的类名、子句形状。
/// 编译器编译其它单元时，接口文件的 id 与正在运行的 boot 镜像一致才用它；用了它的单元在入口一开始调用 <see cref="Check"/>，
/// boot 镜像换了（重新构建）时报错，库的 .fasl / dll 由加载方退回源码，程序报"需要重新编译"。
/// </summary>
public static class BootInterface
{
    public const string BootAssemblyName = "cleat.scheme.boot";

    private static Assembly s_boot;

    /// <summary>正在运行的 boot 镜像（ModuleLoader 加载第一个同名程序集时登记）。</summary>
    public static Assembly Boot => s_boot;

    // ModuleLoader 运行过的编译产物（boot、.fasl、库 dll），按程序集名。按字节加载的程序集各在独立的加载上下文中，
    // 别的产物按名字引用其中的类（直接调用）时默认上下文找不到，由 AssemblyResolve 从这里给出
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Assembly> s_loaded = new();
    private static int s_resolveHooked;

    internal static void Register(Assembly asm)
    {
        if (asm.IsDynamic) return;
        string name = asm.GetName().Name;
        if (name == null) return;
        // 同名的产物再次加载（REPL 里重复 load 同一个 .fasl）时以最新的为准：它的库才是现在装入的那个；boot 只认第一个
        if (name == BootAssemblyName) { if (s_boot == null) { s_boot = asm; s_loaded[name] = asm; } }
        else s_loaded[name] = asm;
        if (System.Threading.Interlocked.Exchange(ref s_resolveHooked, 1) == 0)
            AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
                s_loaded.TryGetValue(new AssemblyName(e.Name).Name ?? "", out var a) ? a : null;
    }

    // —— 用户库的接口文件（.fasl / 库 dll 旁的 名字.ifc，compile-library-dll 写出）——
    private static readonly object s_ifcGate = new();
    private static object s_ifcFiles = EmptyList.Instance;   // Scheme 表，按加载顺序（追加在表尾，编译器记下读过几个）

    /// <summary>加载 .fasl / 库 dll 之后：旁边的接口文件记录的程序集名与加载的相同时登记它。</summary>
    internal static void RegisterInterfaceFile(string path, Assembly asm)
    {
        string ifc = Path.ChangeExtension(path, ".ifc");
        if (!File.Exists(ifc)) return;
        string head;
        using (var r = new StreamReader(ifc)) head = r.ReadLine() ?? "";
        if (!head.StartsWith("(cleat-scheme-interface 1 \"" + asm.GetName().Name + "\"", StringComparison.Ordinal)) return;
        lock (s_ifcGate)
        {
            // 不可变的表：新表 = 旧表 + 新元素（文件数很少）
            var items = new System.Collections.Generic.List<object>();
            for (object p = s_ifcFiles; p is Cons c; p = c.cdr) items.Add(c.car);
            items.Add(ifc);
            object list = EmptyList.Instance;
            for (int i = items.Count - 1; i >= 0; i--) list = new Cons(items[i], list);
            s_ifcFiles = list;
        }
    }

    /// <summary>($library-interface-files) → 已登记的接口文件路径表（按加载顺序）。</summary>
    [Primitive("$library-interface-files", Flags = PrimitiveFlags.Unsafe)]
    public static object LibraryInterfaceFiles() { lock (s_ifcGate) return s_ifcFiles; }

    private static string Id(Assembly asm) => asm.ManifestModule.ModuleVersionId.ToString("N");

    /// <summary>($boot-image-id) → 正在运行的 boot 镜像的 id 字符串；没有 boot 时为 #f。</summary>
    [Primitive("$boot-image-id", Flags = PrimitiveFlags.Unsafe)]
    public static object ImageId() => s_boot != null ? Id(s_boot) : RT.False;

    /// <summary>($boot-interface-file) → boot 镜像旁的接口文件路径；镜像不是从文件加载的（自包含发布）或文件不存在时为 #f。</summary>
    [Primitive("$boot-interface-file", Flags = PrimitiveFlags.Unsafe)]
    public static object InterfaceFile()
    {
        string loc = s_boot?.Location;
        if (string.IsNullOrEmpty(loc)) return RT.False;
        string path = Path.ChangeExtension(loc, ".ifc");
        return File.Exists(path) ? path : RT.False;
    }

    /// <summary>($assembly-file-id 文件名) → dll 文件的 id 字符串（与 <see cref="ImageId"/> 同一形式），构建脚本写接口文件时用。</summary>
    [Primitive("$assembly-file-id", Flags = PrimitiveFlags.Unsafe)]
    public static object AssemblyFileId(object filename)
    {
        string path = Errors.RequiresType<string>(filename is SchemeString or System.Text.StringBuilder ? filename.ToString() : filename, "$assembly-file-id");
        using var pe = new PEReader(File.OpenRead(path));
        var md = pe.GetMetadataReader();
        return md.GetGuid(md.GetModuleDefinition().Mvid).ToString("N");
    }

    /// <summary>编译产物入口的第一条调用：编译时所用 boot 镜像的 id 与正在运行的不同则报错。</summary>
    public static void Check(string id)
    {
        if (s_boot != null && Id(s_boot) != id)
            Errors.AssertionViolation("load", "compiled code requires a different boot image; recompile it", id, Id(s_boot));
    }
}
