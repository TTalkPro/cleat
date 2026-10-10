using System;
using System.IO;
using Cleat.Core.Runtime;

namespace Cleat.PrimGen;

/// <summary>primgen [输出路径]：把 Primitives.Table 写成 primitives.edn（默认 src/boot/primitives.edn，相对仓库根目录）。</summary>
public static class Program
{
    public static int Main(string[] args)
    {
        string output = args.Length > 0 ? args[0] : Path.Combine(FindRoot(), "src", "boot", "primitives.edn");
        string text;
        try { text = Primitives.ToEdn(); }
        catch (Exception e)
        {
            Console.Error.WriteLine("primgen: " + e.Message);
            return 1;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)));
        if (!File.Exists(output) || File.ReadAllText(output) != text) File.WriteAllText(output, text);
        Console.WriteLine($"primgen: {Primitives.Table.Count} primitives -> {output}");
        return 0;
    }

    private static string FindRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "Cleat.slnx"))) return d.FullName;
        return Directory.GetCurrentDirectory();
    }
}
