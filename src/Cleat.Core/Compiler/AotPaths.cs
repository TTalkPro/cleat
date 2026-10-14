using System;
using System.IO;
using Cleat.Core.Runtime;
using clojure.lang;

namespace Cleat.Core.Compiler;

/// <summary>AOT 的输出路径、加载键、镜像核对与直接链接目标（cleat-082 从元层的 Driver 搬来）。</summary>
public static class AotPaths
{
    private static readonly object s_false = false;

    /// <summary>
    /// (compile-output-path 源路径 源名字) → clojure.core/compile 的包程序集路径（designs/17 §2.3）：a/b.clj → a.b.dll。目录：*compile-path*
    /// 是字符串时用它，否则用找到源码的那个加载路径目录（源路径去掉源名字）。
    /// </summary>
    public static string CompileOutputPath(object sourcePath, object sourceName)
    {
        string path = sourcePath?.ToString() ?? "", name = (sourceName?.ToString() ?? Path.GetFileName(path)).Replace('\\', '/');
        string file = LoadKeyNamespace(LoadKey(sourcePath, sourceName)) + ".dll";
        if (clojure.lang.RT.CompilePathVar.deref() is string dir && dir.Length > 0) return Path.GetFullPath(Path.Combine(dir, file));
        string full = Path.GetFullPath(path).Replace('\\', '/');
        string root = full.EndsWith("/" + name, StringComparison.Ordinal) ? full.Substring(0, full.Length - name.Length) : Path.GetDirectoryName(full);
        return Path.GetFullPath(Path.Combine(root, file));
    }

    /// <summary>(load-key 源路径 源名字) → 加载器查包索引用的键：去掉扩展名的加载路径（<c>nrepl/server.clj</c> → <c>nrepl/server</c>）。</summary>
    public static string LoadKey(object sourcePath, object sourceName)
    {
        string name = (sourceName as string ?? Path.GetFileName(sourcePath?.ToString() ?? "")).Replace('\\', '/').TrimStart('/');
        foreach (string ext in new[] { ".cljr", ".cljc", ".clj" })
            if (name.EndsWith(ext, StringComparison.Ordinal)) return name.Substring(0, name.Length - ext.Length);
        return name;
    }

    /// <summary>加载键 → 包程序集名（<c>a/b</c> → <c>a.b</c>）。</summary>
    public static string LoadKeyNamespace(object key) => (key?.ToString() ?? "").Replace('/', '.');

    public static string PackageFullPath(object path) => Path.GetFullPath(path?.ToString() ?? "");

    /// <summary>(under-directory? 全路径 根)：文件在目录根（递归）下，或就是文件根（tools.build 的 :filter-nses 给出单个文件）。</summary>
    public static bool UnderDirectory(object full, object dir)
    {
        string d = Path.GetFullPath(dir?.ToString() ?? "");
        if (File.Exists(d)) return string.Equals(full?.ToString(), d, StringComparison.Ordinal);
        if (!d.EndsWith(Path.DirectorySeparatorChar)) d += Path.DirectorySeparatorChar;
        return (full?.ToString() ?? "").StartsWith(d, StringComparison.Ordinal);
    }

    /// <summary>编译时正在运行的 Clojure boot 镜像（cleat.boot.dll）的 id；没有时 #f。产物入口据此生成 BootInterface.Check。</summary>
    public static object ClojureImageId() => BootInterface.CurrentImageId?.ToString() ?? s_false;

    /// <summary>
    /// (direct-link-target var argc)：Var 在接口文件里的条目有静态 <c>invokeStatic</c>（:static）且 arity 恰为 argc 时
    /// 返回类名（"类, 程序集"），否则 #f。展开器对 Var 调用据此生成 <c>$clj-static-call</c>（跨单元直接链接，cleat-033，
    /// designs/04 §6）；其余调用照常走 IFn.invoke。
    /// </summary>
    public static object DirectLinkTarget(object var, object argc)
    {
        if (var is not Var v) return s_false;
        int n;
        try { n = Convert.ToInt32(argc); }
        catch (Exception) { return s_false; }   // 不是参数个数的直接放弃直连
        // IFn.invoke 只有 1 ~ 20 个固定参数的签名：更多参数的调用不做直连
        var e = InterfaceFile.FindKnown(clojure.lang.Symbol.intern(v.Namespace.Name.Name, v.Symbol.Name));
        return e is { HasStatic: true } && n is >= 1 and <= 20 && Array.IndexOf(e.Arities, n) >= 0 ? e.ClassName : s_false;
    }

}
