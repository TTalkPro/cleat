using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Cleat.Scheme;
using Cleat.Stage0;

// stage0 命令行（iron-016）：
//   stage0 --core <文件.core> …            依次解释执行 Lcore 文件（不加载种子）
//   stage0 <种子> [<脚本.sps> 参数 …]       执行种子，再以 (load/program 脚本) 运行顶层程序
//   stage0 --boot <boot.dll> [<脚本.sps> …]  加载新编译器生成的 boot 镜像代替种子
// 选项（写在种子与脚本之前）：--time 把各阶段耗时写到标准错误；-I <目录> 增加库搜索路径；
// --deterministic 开启构建模式的确定性 gensym（iron-014）。
int exitCode = 0;
var thread = new Thread(() => exitCode = Main(args), 512 * 1024 * 1024);
thread.Start();
thread.Join();
return exitCode;

static int Main(string[] args)
{
    bool time = false;
    var rest = new System.Collections.Generic.List<string>();
    for (int i = 0; i < args.Length; i++)
    {
        if (rest.Count < 2 && args[i] == "--time") time = true;
        else if (rest.Count < 2 && args[i] == "--deterministic") RuntimeOptions.DeterministicGensym = true;
        else if (rest.Count < 2 && args[i] == "-I" && i + 1 < args.Length) Builtins.AddIncludePath(args[++i]);
        else rest.Add(args[i]);
    }
    args = rest.ToArray();
    if (args.Length == 0)
    {
        Console.Error.WriteLine("usage: stage0 [--time] [--deterministic] [-I dir] (--core <file.core> ... | <seed> [<script.sps> args ...])");
        return 2;
    }

    var sw = Stopwatch.StartNew();
    // 展开器在 boot 初始化时读取命令行，所以要在执行种子 / 加载 boot 之前设置：脚本及其参数
    int scriptIndex = args[0] == "--boot" ? 2 : 1;
    if (args[0] != "--core" && args.Length > scriptIndex) Builtins.CommandLineArgs = args[scriptIndex..];
    Interpreter.Initialize();
    try
    {
        if (args[0] == "--core")
        {
            for (int i = 1; i < args.Length; i++) Interpreter.RunFile(args[i]);
            return 0;
        }

        if (args[0] == "--boot" && args.Length >= 2)
        {
            // 用编译好的 boot 镜像代替文本种子（新编译器的验证；eval 仍由 stage0 解释）
            ModuleLoader.LoadModule(args[1]);
            if (time) Console.Error.WriteLine($"stage0: boot image {args[1]} loaded in {sw.ElapsedMilliseconds} ms");
            args = args[1..];
        }
        else
        {
            int forms = Interpreter.RunFile(args[0]);
            if (time) Console.Error.WriteLine($"stage0: seed {forms} forms in {sw.ElapsedMilliseconds} ms");
        }
        if (args.Length == 1) return 0;

        sw.Restart();
        try
        {
            Interpreter.EvalTopLevel(Cons.FromArray(Symbol.Intern("load/program"), args[1].Replace('\\', '/')));
        }
        catch (ProgramFailedException)
        {
            return 1;
        }
        finally
        {
            if (time) Console.Error.WriteLine($"stage0: program in {sw.ElapsedMilliseconds} ms");
        }
        return 0;
    }
    catch (Exception e)
    {
        Interpreter.Report(e);
        return 1;
    }
    finally
    {
        Console.Out.Flush();
    }
}
