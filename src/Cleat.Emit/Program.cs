using System;
using System.IO;

namespace Cleat.Emit;

/// <summary>
/// 命令行：cleat-emit &lt;in.lcil&gt; -o &lt;out.dll&gt; [--pdb] [-r &lt;目录&gt;]…；cleat-emit --references &lt;dll&gt;
/// -r 增加查找被引用程序集（如 Cleat.Core.dll）的目录；默认含发射器所在目录。
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        // cleat-emit --references <dll>：列出程序集引用
        if (args.Length == 2 && args[0] == "--references")
        {
            using var pe = new System.Reflection.PortableExecutable.PEReader(File.OpenRead(args[1]));
            var md = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
            foreach (var h in md.AssemblyReferences)
            {
                var r = md.GetAssemblyReference(h);
                Console.WriteLine(md.GetString(r.Name) + " " + r.Version);
            }
            return 0;
        }
        // cleat-emit --seed <种子目录> -o <输出目录>（cleat-091）：解压种子里的 *.gz，把其中每个 .lcil 发射成同名 .dll
        if (args.Length >= 4 && args[0] == "--seed" && args[2] == "-o")
        {
            for (int i = 4; i + 1 < args.Length; i += 2)
                if (args[i] == "-r") LcilEmitter.ProbeDirectories.Add(Path.GetFullPath(args[i + 1]));
                else return Usage();
            try { EmitSeed(args[1], args[3]); return 0; }
            catch (LcilException e) { Console.Error.WriteLine("cleat-emit: " + e.Message); return 1; }
        }
        string input = null, output = null;
        bool pdb = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-o" when i + 1 < args.Length: output = args[++i]; break;
                case "-r" when i + 1 < args.Length: LcilEmitter.ProbeDirectories.Add(Path.GetFullPath(args[++i])); break;
                case "--pdb": pdb = true; break;
                default:
                    if (input == null && !args[i].StartsWith("-", StringComparison.Ordinal)) input = args[i];
                    else return Usage();
                    break;
            }
        }
        if (input == null) return Usage();
        output ??= Path.ChangeExtension(input, ".dll");
        try
        {
            LcilEmitter.Save(File.ReadAllText(input), input, output, pdb);
            return 0;
        }
        catch (LcilException e)
        {
            Console.Error.WriteLine("cleat-emit: " + e.Message);
            return 1;
        }
    }

    /// <summary>种子（designs/19 D7）：gzip 压缩的 Lcil 文本与旁边的接口文件；解压到 outDir，逐个发射 .lcil（按名字排序，确定）。</summary>
    public static void EmitSeed(string seedDir, string outDir)
    {
        Directory.CreateDirectory(outDir);
        foreach (var gz in Directory.GetFiles(seedDir, "*.gz"))
        {
            using var input = new System.IO.Compression.GZipStream(File.OpenRead(gz), System.IO.Compression.CompressionMode.Decompress);
            using var output = File.Create(Path.Combine(outDir, Path.GetFileNameWithoutExtension(gz)));
            input.CopyTo(output);
        }
        var lcils = Directory.GetFiles(outDir, "*.lcil");
        Array.Sort(lcils, StringComparer.Ordinal);
        LcilEmitter.ProbeDirectories.Add(Path.GetFullPath(outDir));
        foreach (var lcil in lcils)
        {
            string dll = Path.ChangeExtension(lcil, ".dll");
            LcilEmitter.Save(File.ReadAllText(lcil), lcil, dll, true);
            // 后面的单元按类型名（不带程序集名）引用它里面的类（编译器包里内联的守卫引用镜像的函数类）：装进本进程
            System.Reflection.Assembly.LoadFrom(dll);
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("usage: cleat-emit <in.lcil> [-o <out.dll>] [--pdb] [-r <dir>]... | cleat-emit --seed <seed dir> -o <out dir> [-r <dir>]...");
        return 2;
    }
}
