using System;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace Cleat.Scheme.Tests;

/// <summary>
/// Scheme 元层的自举不动点（cleat-046；IronScheme iron-030，designs/05-bootstrap.md §3）：
///   stage0（文本种子 + 解释器）运行 ironscheme-buildscript-nano.sps → stage1；
///   stage0 --boot 加载 stage1 镜像（eval 走镜像内的编译器）运行同一脚本 → stage2；加载 stage2 → stage3。
/// 判据：stage2 与 stage3 的 Lcil 文本一致、dll 逐字节一致，stage1 的 dll 也与之相同；ILVerify 通过。
/// 最后把 stage3 装为工作区的镜像 src/boot/cleat.scheme.boot.dll。每级约 40 ~ 60 秒。
/// </summary>
[Order(1)]
[Category("Bootstrap")]
[NonParallelizable]
public class BootstrapTests
{
    private static readonly string s_root = Path.Combine(Path.GetTempPath(), "cleat-scheme-bootstrap");
    private static string StageDir(int n) => Path.Combine(s_root, "stage" + n);
    private static string Dll(int n) => Path.Combine(StageDir(n), "cleat.scheme.boot.dll");
    private static string Lcil(int n) => Path.Combine(StageDir(n), "cleat.scheme.boot.lcil");

    private const string Script = "boot/ironscheme-buildscript-nano.sps";

    [Test, Order(1)]
    public void Stage1_FromStage0()
    {
        Directory.CreateDirectory(StageDir(1));
        var r = SchemePaths.Run("dotnet", new[] { SchemePaths.Stage0 }.Concat(SchemePaths.IncludeArgs).Concat(new[] { "boot/boot-seed.core", Script, StageDir(1) }).ToArray(), SchemePaths.SchemeDir);
        TestContext.Out.WriteLine($"stage1 (Phase A on stage0): {r.Elapsed.TotalSeconds:F1} s\n{r.Output}");
        Assert.That(File.Exists(Dll(1)), Is.True);
    }

    [Test, Order(2)]
    public void Stage2_FromStage1() => BuildWith(1, 2);

    [Test, Order(3)]
    public void Stage3_FromStage2() => BuildWith(2, 3);

    private static void BuildWith(int from, int to)
    {
        if (!File.Exists(Dll(from))) Assert.Ignore($"stage{from} not built");
        Directory.CreateDirectory(StageDir(to));
        // 选项在 --boot 之前（stage0 的命令行约定）
        var r = SchemePaths.Run("dotnet", new[] { SchemePaths.Stage0 }.Concat(SchemePaths.IncludeArgs).Concat(new[] { "--boot", Dll(from), Script, StageDir(to) }).ToArray(), SchemePaths.SchemeDir);
        TestContext.Out.WriteLine($"stage{to} (on stage{from}): {r.Elapsed.TotalSeconds:F1} s\n{r.Output}");
        Assert.That(File.Exists(Dll(to)), Is.True);
    }

    [Test, Order(4)]
    public void Fixpoint()
    {
        if (!File.Exists(Dll(3))) Assert.Ignore("stage3 not built");
        Assert.That(File.ReadAllText(Lcil(3)), Is.EqualTo(File.ReadAllText(Lcil(2))), "stage2 / stage3 Lcil");
        Assert.That(File.ReadAllBytes(Dll(3)), Is.EqualTo(File.ReadAllBytes(Dll(2))), "stage2 / stage3 dll");
        // stage0 编译出的 stage1 也与之相同（符号哈希只取决于名字、gensym 规范改名，产物与宿主无关）
        Assert.That(File.ReadAllBytes(Dll(1)), Is.EqualTo(File.ReadAllBytes(Dll(2))), "stage1 / stage2 dll");
        TestContext.Out.WriteLine("sha256 " + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Dll(3)))) + "  cleat.scheme.boot.dll");
    }

    [Test, Order(5)]
    public void Verify()
    {
        if (!File.Exists(Dll(3))) Assert.Ignore("stage3 not built");
        var r = ILVerify(Dll(3));
        Assert.That(r.Output, Does.Contain($"All Classes and Methods in {Dll(3)} Verified."), r.ToString());
    }

    [Test, Order(6)]
    public void InstallStage3()
    {
        if (!File.Exists(Dll(3))) Assert.Ignore("stage3 not built");
        Directory.CreateDirectory(Path.GetDirectoryName(SchemePaths.Boot));
        Replace(Dll(3), SchemePaths.Boot);
        Replace(Lcil(3), Path.ChangeExtension(SchemePaths.Boot, ".lcil"));
        // 接口文件记录镜像 id：与镜像一起换，否则编译器认为它过期而不用
        var ifc = Path.ChangeExtension(Dll(3), ".ifc");
        if (File.Exists(ifc)) Replace(ifc, Path.ChangeExtension(SchemePaths.Boot, ".ifc"));
    }

    /// <summary>
    /// 先复制到同目录的临时文件再原子改名：File.Copy 覆盖会就地截断重写，并行运行的宿主进程（Cleat.Host.Tests）
    /// 若正映射着旧镜像就会收到 SIGBUS（退出码 135）；改名后旧 inode 仍对已打开它的进程有效。
    /// </summary>
    private static void Replace(string source, string destination)
    {
        string tmp = destination + "." + System.Guid.NewGuid().ToString("N") + ".tmp";
        File.Copy(source, tmp);
        File.Move(tmp, destination, true);
    }

    /// <summary>
    /// ILVerify（dotnet tool install -g dotnet-ilverify）；找不到时忽略。全局工具目录为用户目录下的 .dotnet/tools，
    /// Windows 上文件名带 .exe；可用环境变量 ILVERIFY 指定（与 tools/build.cs 一致）。
    /// </summary>
    internal static SchemePaths.ProcessResult ILVerify(params string[] assemblies)
    {
        var tool = Environment.GetEnvironmentVariable("ILVERIFY")
          ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools",
                          OperatingSystem.IsWindows() ? "ilverify.exe" : "ilverify");
        if (!File.Exists(tool)) Assert.Ignore("ilverify not installed (dotnet tool install -g dotnet-ilverify)");
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        var args = assemblies.Concat(new[]
        {
            "-r", Path.Combine(runtimeDir, "*.dll"),
            "-r", typeof(Cleat.Core.CleatModuleAttribute).Assembly.Location,
            "-r", typeof(Cleat.Scheme.Procedure).Assembly.Location,
            "-r", typeof(Cleat.Emit.LcilEmitter).Assembly.Location,
        }).ToArray();
        // 编译产物可能直接调用 boot 镜像中的过程（iron-034e）：校验它们时引用镜像；校验镜像本身时不重复
        if (!assemblies.Any(a => Path.GetFileName(a) == "cleat.scheme.boot.dll") && File.Exists(SchemePaths.Boot))
            args = args.Concat(new[] { "-r", SchemePaths.Boot }).ToArray();
        return SchemePaths.Run(tool, args, SchemePaths.SchemeDir, checkExitCode: false);
    }
}
