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

    private static int Usage()
    {
        Console.Error.WriteLine("usage: cleat-emit <in.lcil> [-o <out.dll>] [--pdb] [-r <dir>]...");
        return 2;
    }
}
